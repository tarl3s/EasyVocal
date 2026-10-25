using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Logging;
using EasyVocal.Core.Model;
using EasyVocal.Core.Platform;
using EasyVocal.Core.Sounds;
using EasyVocal.Core.Storage;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The one object windows talk to: owns the settings, the audio engine, the soundboard mixer and the global
/// hotkeys. Every member belongs to the UI thread; what the engine, the mixer and Windows' device notifications
/// report from their own threads is posted here with <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/>
/// (never <c>Invoke</c>: the engine may be joining one of those threads while the UI waits on it).
/// </summary>
public sealed class AppController : IDisposable
{
    public const string NormalVoiceId = BuiltInVoices.NormalId;

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecoverEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeviceSettle = TimeSpan.FromMilliseconds(500);
    /// <summary>After VB-CABLE shows up, how long a default it takes is put back to what the person had.</summary>
    private static readonly TimeSpan RestoreWindow = TimeSpan.FromSeconds(60);

    private readonly Dispatcher ui;
    private readonly VoiceChain chain = new(AudioFormat.SampleRate);
    private readonly SoundMixer mixer = new();
    private readonly AudioEngine engine;
    private readonly SettingsFile settingsFile;
    private readonly IAudioBackend devices;
    private readonly DispatcherTimer saveTimer, recoverTimer, deviceTimer;
    private IGlobalHotkeys? hotkeys;
    private IReadOnlyList<string> hotkeyProblems = Array.Empty<string>();
    private HashSet<string> refusedActions = new();
    private int captureSuspends;
    private bool typing;
    private VoiceSliders? previewSliders;
    private bool? previewHearMe;
    private readonly HashSet<string> brokenSounds = new();
    private readonly LoadTickets soundLoads = new();
    private SoundPreview? preview;
    private string deviceFingerprint = "";
    private readonly MicPicker micPicker = new();
    private MicPick? micInUse;
    private string? usedMic, usedCall, usedMonitor; // what the current session opened
    private int changePosted;
    private DateTime? restoreUntil; // set when the cable shows up while the defaults from before its install are remembered
    private bool started, disposed, savePending;

    /// <param name="dataFolder">Where settings.json and the Sounds folder live (normally <see cref="SettingsStore.DefaultFolder"/>).</param>
    /// <param name="platform">The system's devices, startup entry and virtual mic (the tests swap in their own devices and a throwaway startup entry).</param>
    public AppController(string dataFolder, PlatformServices platform)
    {
        ui = Dispatcher.CurrentDispatcher;
        Platform = platform;
        devices = platform.Audio;
        Startup = platform.Startup;
        CableInstaller = platform.VirtualMic.InstallAsync;
        DataFolder = dataFolder;
        SoundsFolder = Path.Combine(dataFolder, "Sounds");
        FirstRun = !File.Exists(Path.Combine(dataFolder, "settings.json"));
        settingsFile = new SettingsFile(dataFolder);
        Settings = settingsFile.Load();
        if (settingsFile.CouldNotRead)
            AppLog.Info("settings.json could not be read (another program holds it?): using defaults and not saving this session");
        engine = new AudioEngine(MakeInput, MakeCallOutput, MakeMonitor, chain, mixer);

        saveTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = SaveDelay };
        saveTimer.Tick += (_, _) => SaveNow();
        recoverTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = RecoverEvery };
        recoverTimer.Tick += (_, _) => Recover();
        deviceTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = DeviceSettle };
        deviceTimer.Tick += (_, _) => DevicesSettled();
    }

    public string DataFolder { get; }
    public string SoundsFolder { get; }

    /// <summary>No settings.json existed when the app started.</summary>
    public bool FirstRun { get; }

    /// <summary>The microphones and playback devices every window lists and opens (the e2e test swaps in its own).</summary>
    public IAudioBackend Devices => devices;

    /// <summary>This platform's services (shortcut, virtual mic, updater…).</summary>
    public PlatformServices Platform { get; }

    public AppSettings Settings { get; }

    /// <summary>Something the windows show changed (state, toggles, active voice, a sound started or ended…). UI thread.</summary>
    public event Action? Changed;

    public EngineState State => engine.State;

    /// <summary>Peak of the microphone in the last block, 0..1.</summary>
    public float InputLevel => engine.InputLevel;

    /// <summary>One sentence per hotkey that couldn't be registered (another app owns it), already translated.</summary>
    public IReadOnlyList<string> HotkeyProblems => hotkeyProblems;

    /// <summary>Action ids ("voice:robot", "sound:ding"…) whose hotkey Windows refused in the last <see cref="RebuildHotkeys"/>.</summary>
    public IReadOnlyCollection<string> RefusedActions => refusedActions;

    /// <summary>The voice in use: the active one when it exists and isn't hidden, else Normal.</summary>
    public VoicePreset ActiveVoice =>
        FindVoice(Settings.ActiveVoiceId) is { Hidden: false } v ? v
        : FindVoice(NormalVoiceId) ?? new VoicePreset { Id = NormalVoiceId, Name = "Normal", BuiltIn = true };

    // ---------- start / stop ----------

    /// <summary>Opens the audio, takes over <paramref name="globalHotkeys"/> (registers the keys, disposes it at the end) and loads the sounds.</summary>
    public void Start(IGlobalHotkeys globalHotkeys)
    {
        if (started || disposed)
        {
            globalHotkeys.Dispose();
            return;
        }
        started = true;

        if (FirstRun) Save(); // settings.json exists from now on
        EnsureActiveVoice();
        engine.FilterEnabled = Settings.Enabled;
        engine.MicMuted = Settings.MicMuted;
        engine.HearMe = Settings.HearMe;
        engine.HearSounds = Settings.HearSounds;
        engine.SoundsUseVoice = Settings.SoundsUseVoice;
        ApplyChain();

        mixer.PlayingChanged += OnPlayingChanged;
        engine.StateChanged += OnEngineStateChanged;

        hotkeys = globalHotkeys;
        hotkeys.Pressed += OnHotkey;
        RebuildHotkeys();

        devices.Changed += OnDevicesChanged;
        deviceFingerprint = DeviceFingerprint();
        engine.Start();
        AppLog.Info("Audio started: " + engine.State);
        if (RemembersDefaults && Platform.VirtualMic.Find(devices.Outputs()) == null)
        {
            AppLog.Info("VB-CABLE never showed up: the default devices remembered for its install are forgotten");
            ForgetDefaultsBeforeCable();
        }
        CheckDefaults();
        recoverTimer.Start(); // every 2 s: brings back whatever stopped working (cheap while everything works)

        LoadSoundsInBackground();
    }

    /// <summary>Closes and reopens the audio devices (after a device was picked in Settings, or the device list changed).</summary>
    public void RestartAudio()
    {
        if (!started || disposed) return;
        engine.Stop();
        deviceFingerprint = DeviceFingerprint();
        engine.Start();
        AppLog.Info("Audio restarted: " + engine.State);
        RaiseChanged();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        recoverTimer.Stop();
        deviceTimer.Stop();
        if (started)
        {
            devices.Changed -= OnDevicesChanged;
            if (hotkeys != null)
            {
                hotkeys.Pressed -= OnHotkey;
                hotkeys.Dispose();
            }
            engine.StateChanged -= OnEngineStateChanged;
            mixer.PlayingChanged -= OnPlayingChanged;
        }
        StopPreview();
        engine.Dispose();
        if (restoreUntil != null) ForgetDefaultsBeforeCable(); // the cable came and its restore window was used
        if (savePending) SaveNow();
        saveTimer.Stop();
    }

    // ---------- settings ----------

    /// <summary>Saves settings.json half a second after the last call (several changes in a row write once).</summary>
    public void Save()
    {
        savePending = true;
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void SaveNow()
    {
        saveTimer.Stop();
        savePending = false;
        try
        {
            if (!settingsFile.Save(Settings)) AppLog.Info("Settings not saved: settings.json could not be read when EasyVocal started");
        }
        catch (Exception ex) { AppLog.Error("Saving settings failed", ex); }
    }

    // ---------- switches ----------

    /// <summary>ON/OFF: off sends the clean voice.</summary>
    public bool FilterEnabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled == value) return;
            Settings.Enabled = value;
            engine.FilterEnabled = value;
            Save();
            RaiseChanged();
        }
    }

    public bool MicMuted
    {
        get => Settings.MicMuted;
        set
        {
            if (Settings.MicMuted == value) return;
            Settings.MicMuted = value;
            engine.MicMuted = value;
            Save();
            RaiseChanged();
        }
    }

    /// <summary>Hear your own (changed) voice in the headphones.</summary>
    public bool HearMe
    {
        get => Settings.HearMe;
        set
        {
            if (Settings.HearMe == value) return;
            Settings.HearMe = value;
            engine.HearMe = previewHearMe ?? value;
            Save();
            RaiseChanged();
        }
    }

    /// <summary>Hear the soundboard in the headphones.</summary>
    public bool HearSounds
    {
        get => Settings.HearSounds;
        set
        {
            if (Settings.HearSounds == value) return;
            Settings.HearSounds = value;
            engine.HearSounds = value;
            Save();
            RaiseChanged();
        }
    }

    /// <summary>The soundboard goes through the voice filter too: switching voices changes a sound that is playing.</summary>
    public bool SoundsUseVoice
    {
        get => Settings.SoundsUseVoice;
        set
        {
            if (Settings.SoundsUseVoice == value) return;
            Settings.SoundsUseVoice = value;
            engine.SoundsUseVoice = value;
            Save();
            RaiseChanged();
        }
    }

    // ---------- start with Windows ----------

    /// <summary>Where "Start with Windows" is written: the real Run key, or a throwaway one in the smoke test.</summary>
    public IStartupService Startup { get; set; }

    /// <summary>Turns "Start with Windows" on or off. False (nothing changed) when Windows refused the registry write.</summary>
    public bool SetStartWithWindows(bool on)
    {
        try { Startup.Set(on); }
        catch (Exception ex)
        {
            AppLog.Error("Changing 'Start with Windows' failed", ex);
            return false;
        }
        Settings.StartWithWindows = on;
        Save();
        return true;
    }

    /// <summary>When the person chose to start with Windows, keeps the registered program path right after EasyVocal was moved or updated.</summary>
    public void RefreshStartup()
    {
        if (!Settings.StartWithWindows) return;
        try { Startup.Refresh(); }
        catch (Exception ex) { AppLog.Error("Refreshing 'Start with Windows' failed", ex); }
    }

    // ---------- voices ----------

    /// <summary>Makes a visible voice the active one (and turns the filter on: picking a voice means wanting to hear it).</summary>
    public void SelectVoice(string id)
    {
        var v = FindVoice(id);
        if (v == null || v.Hidden) return;
        Settings.ActiveVoiceId = v.Id;
        ApplyChain();
        if (!Settings.Enabled)
        {
            Settings.Enabled = true;
            engine.FilterEnabled = true;
        }
        Save();
        RaiseChanged();
    }

    /// <summary>Hides or shows a voice. Hiding the active voice goes back to Normal; Normal itself can't be hidden.</summary>
    public void SetVoiceHidden(string id, bool hidden)
    {
        var v = FindVoice(id);
        if (v == null || v.Hidden == hidden || (hidden && !ItemOps.CanHide(v))) return;
        var before = ActiveVoice;
        v.Hidden = hidden;
        EnsureActiveVoice();
        if (!ReferenceEquals(before, ActiveVoice)) ApplyChain();
        Save();
        RebuildHotkeys();
    }

    /// <summary>
    /// While the filter editor is open, the engine plays these sliders (and the filter counts as ON so they are heard);
    /// call it again after changing them. Null goes back to the active voice and the person's ON/OFF choice.
    /// </summary>
    public void PreviewSliders(VoiceSliders? sliders)
    {
        if (disposed) return;
        previewSliders = sliders;
        ApplyChain();
        engine.FilterEnabled = sliders != null || Settings.Enabled;
    }

    /// <summary>
    /// While the filter editor is open it decides on its own whether the headphones play the voice (its 🎧 toggle);
    /// the person's saved Hear me is not touched. Null = back to the saved Hear me.
    /// </summary>
    public void PreviewHearMe(bool? on)
    {
        if (disposed) return;
        previewHearMe = on;
        engine.HearMe = on ?? Settings.HearMe;
    }

    /// <summary>What the headphones do right now: the filter editor's choice while it is open, else Hear me.</summary>
    public bool HearingMe => previewHearMe ?? Settings.HearMe;

    /// <summary>Gives the voice chain what it should play: the preview if one is on, else the active voice.</summary>
    private void ApplyChain() => chain.Apply(previewSliders ?? ActiveVoice.Sliders);

    /// <summary>A copy of a voice (see <see cref="ItemOps.DuplicateVoice"/>).</summary>
    public VoicePreset DuplicateVoice(string id)
    {
        var copy = ItemOps.DuplicateVoice(Settings, id);
        Save();
        RaiseChanged();
        return copy;
    }

    /// <summary>Adds a voice the person made.</summary>
    public void AddVoice(VoicePreset voice)
    {
        Settings.Voices.Add(voice);
        Save();
        RaiseChanged();
    }

    /// <summary>The person edited this voice in place (the object was already changed).</summary>
    public void VoiceEdited(string id)
    {
        if (ActiveVoice.Id == id) ApplyChain();
        Save();
        RaiseChanged();
    }

    /// <summary>Hides a built-in voice or removes the person's own; the active voice falls back to Normal.</summary>
    public void DeleteVoice(string id)
    {
        ItemOps.DeleteVoice(Settings, id);
        EnsureActiveVoice();
        ApplyChain();
        Save();
        RebuildHotkeys();
    }

    private VoicePreset? FindVoice(string? id) => Settings.Voices.FirstOrDefault(v => v.Id == id);

    /// <summary>A missing or hidden active voice (deleted, hidden, edited by hand) becomes Normal.</summary>
    private void EnsureActiveVoice()
    {
        if (FindVoice(Settings.ActiveVoiceId) is { Hidden: false }) return;
        Settings.ActiveVoiceId = NormalVoiceId;
        Save();
    }

    // ---------- sounds ----------

    /// <summary>Plays a visible sound, or stops it when it is playing.</summary>
    public void ToggleSound(string id)
    {
        var s = FindSound(id);
        if (s == null || s.Hidden) return;
        bool wasPlaying = mixer.IsPlaying(id);
        if (!wasPlaying && engine.State != EngineState.Running)
        {
            // Nothing would play it now (no mic audio, or no call to reach); the banner already says why.
            AppLog.Info("Sound not started, the audio is " + engine.State + ": " + id);
            return;
        }
        if (!mixer.Toggle(id, (float)s.Volume) && !wasPlaying)
        {
            AppLog.Info("Sound not loaded (yet): " + id);
            return;
        }
        RaiseChanged();
    }

    public void StopAllSounds()
    {
        mixer.StopAll();
        RaiseChanged();
    }

    /// <summary>True from the moment a sound is started until it ends or is stopped.</summary>
    public bool IsPlaying(string soundId) => mixer.IsPlaying(soundId);

    /// <summary>Hides or shows a sound; hiding a playing sound stops it.</summary>
    public void SetSoundHidden(string id, bool hidden)
    {
        var s = FindSound(id);
        if (s == null || s.Hidden == hidden) return;
        s.Hidden = hidden;
        if (hidden) mixer.Stop(id); // a no-op when it has just ended (Toggle could have started it again)
        Save();
        RebuildHotkeys();
    }

    /// <summary>The sound's file couldn't be opened (missing, damaged, unsupported): its card says so.</summary>
    public bool IsSoundBroken(string soundId) => brokenSounds.Contains(soundId);

    /// <summary>
    /// A copy of a sound (it plays the same file). It shares the original's decoded audio; only when the original
    /// isn't decoded yet is the file loaded in the background.
    /// </summary>
    public SoundItem DuplicateSound(string id)
    {
        var copy = ItemOps.DuplicateSound(Settings, id);
        if (mixer.TryGet(id, out var samples)) SetSamples(copy.Id, samples);
        else if (brokenSounds.Contains(id)) brokenSounds.Add(copy.Id); // the same file can't be opened either
        else LoadSound(copy);
        Save();
        RaiseChanged();
        return copy;
    }

    /// <summary>Adds a sound the person made; <paramref name="samples"/> is its file already decoded.</summary>
    public void AddSound(SoundItem sound, float[] samples)
    {
        Settings.Sounds.Add(sound);
        SetSamples(sound.Id, samples);
        Save();
        RebuildHotkeys();
    }

    /// <summary>
    /// The person edited this sound in place (the object was already changed). <paramref name="newSamples"/> is the
    /// decoded new file when it was replaced; <paramref name="oldFile"/> that file's name, deleted when nothing uses it.
    /// </summary>
    public void SoundEdited(string id, float[]? newSamples, string? oldFile, string? oldOriginal = null)
    {
        if (newSamples != null)
        {
            mixer.Stop(id);
            SetSamples(id, newSamples);
        }
        if (oldFile != null) ItemOps.DeleteFileIfUnused(Settings, oldFile, SoundsFolder);
        if (oldOriginal != null) ItemOps.DeleteFileIfUnused(Settings, oldOriginal, SoundsFolder);
        Save();
        RebuildHotkeys();
    }

    // ---------- the sound editor ----------

    /// <summary>The decoded audio the soundboard holds for a sound (null while it is still loading or broken).</summary>
    public float[]? SoundSamples(string id) => mixer.TryGet(id, out var samples) ? samples : null;

    /// <summary>
    /// Saves edited audio as a new WAV in the Sounds folder and makes the sound play it. The original file is never
    /// changed (it is remembered for "Back to original"). Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the file can't be written; nothing changes then.
    /// The clamping and the writing run off the UI thread; a sound deleted meanwhile is left alone (the new file goes).
    /// </summary>
    public async Task ApplyEditedAudioAsync(string id, float[] samples)
    {
        var item = FindSound(id) ?? throw new ArgumentException("No sound with id '" + id + "'.", nameof(id));
        var folder = SoundsFolder;
        var stem = ItemOps.EditedStem(item);
        var (clean, file) = await Task.Run(() =>
        {
            var c = AudioEdit.Clamped(samples); // what the file holds is what plays
            return (c, ItemOps.WriteSoundFile(folder, stem, c));
        });
        if (disposed || !Settings.Sounds.Contains(item))
        {
            ItemOps.DeleteFileIfUnused(Settings, file, folder);
            return;
        }
        ItemOps.UseEditedFile(Settings, item, file, folder);
        mixer.Stop(id);
        SetSamples(id, clean);
        AppLog.Info($"Sound '{id}' edited: now plays {file}");
        Save();
        RaiseChanged();
    }

    /// <summary>
    /// "Back to original": the sound plays its original file again. <paramref name="originalSamples"/> is that
    /// file already decoded (the editor has it), else it is decoded in the background.
    /// </summary>
    public void RestoreOriginalAudio(string id, float[]? originalSamples)
    {
        var item = FindSound(id);
        if (item == null || !ItemOps.BackToOriginal(Settings, item, SoundsFolder)) return;
        mixer.Stop(id);
        if (originalSamples != null) SetSamples(id, originalSamples);
        else LoadSound(item);
        AppLog.Info($"Sound '{id}' back to its original file {item.FileName}");
        Save();
        RaiseChanged();
    }

    /// <summary>
    /// "Split at cursor": two new sounds of the person's own, "<paramref name="name"/> 1" and "… 2", each with its
    /// half saved as a WAV (off the UI thread), right after <paramref name="afterId"/> (the sound being edited, if it
    /// is saved yet). Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when a file can't
    /// be written; nothing is added then.
    /// </summary>
    public async Task<(SoundItem First, SoundItem Second)> SplitSoundAsync(string? afterId, string name, string emoji,
        double volume, float[] first, float[] second)
    {
        var folder = SoundsFolder;
        var (a, b, fileA, fileB) = await Task.Run(() =>
        {
            var one = AudioEdit.Clamped(first);
            var two = AudioEdit.Clamped(second);
            var oneFile = ItemOps.WriteSoundFile(folder, name + " 1", one);
            try { return (one, two, oneFile, ItemOps.WriteSoundFile(folder, name + " 2", two)); }
            catch
            {
                try { File.Delete(Path.Combine(folder, oneFile)); } catch (Exception) { /* only clutter */ }
                throw;
            }
        });
        var pair = ItemOps.AddSplitSounds(Settings, afterId, name, emoji, volume, fileA, fileB);
        SetSamples(pair.First.Id, a);
        SetSamples(pair.Second.Id, b);
        AppLog.Info($"Sound split into '{pair.First.Name}' and '{pair.Second.Name}'");
        Save();
        RebuildHotkeys();
        RaiseChanged();
        return pair;
    }

    /// <summary>How far the headphone preview has played, in samples; -1 when nothing is previewing.</summary>
    public int PreviewPosition => preview?.PlayedSamples ?? -1;

    /// <summary>Hides a built-in sound or removes the person's own (and its file when nothing else uses it).</summary>
    public void DeleteSound(string id)
    {
        mixer.Stop(id);
        bool builtIn = FindSound(id)?.BuiltIn == true;
        ItemOps.DeleteSound(Settings, id, SoundsFolder);
        if (!builtIn)
        {
            soundLoads.Next(id); // a decode still running for it is dropped
            mixer.Remove(id);
            brokenSounds.Remove(id);
        }
        Save();
        RebuildHotkeys();
    }

    /// <summary>Plays decoded samples on the headphones only. False when there are no headphones to play on.</summary>
    public bool PreviewSound(float[] samples, double volume)
    {
        StopPreview();
        try
        {
            // Its own device, opened without MakeMonitor: a preview must never change what the session notes it uses
            // (a failed one would otherwise make the next device change restart the audio).
            if (MonitorId(devices.Outputs()) is not { } id) return false;
            var output = devices.OpenSpeaker(id);
            SoundPreview? mine = null;
            mine = new SoundPreview(output, samples, (float)volume, () => ui.BeginInvoke(() =>
            {
                if (!ReferenceEquals(preview, mine)) return; // stopped, or replaced by a newer preview
                preview = null;
                PreviewEnded?.Invoke();
            }));
            preview = mine;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Sound preview failed", ex);
            return false;
        }
    }

    /// <summary>The preview ended by itself. UI thread.</summary>
    public event Action? PreviewEnded;

    public void StopPreview()
    {
        var p = preview;
        preview = null;
        p?.Dispose();
    }

    private SoundItem? FindSound(string? id) => Settings.Sounds.FirstOrDefault(x => x.Id == id);

    /// <summary>
    /// The full path of a sound file inside the Sounds folder, or null when the name is empty or points outside it
    /// (a hand-edited settings.json must not make us read arbitrary files).
    /// </summary>
    public string? SoundPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        try
        {
            var root = Path.GetFullPath(SoundsFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, fileName));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Gives the mixer a sound's audio now; a decode of that sound still running is dropped.</summary>
    private void SetSamples(string id, float[] samples)
    {
        soundLoads.Next(id);
        mixer.Set(id, samples);
        brokenSounds.Remove(id);
    }

    /// <summary>
    /// Writes the generated starter sounds that are missing (all of them on the first run), then decodes every
    /// sound off the UI thread. A missing or broken file is logged and its card marked.
    /// </summary>
    private void LoadSoundsInBackground()
    {
        var items = Settings.Sounds.Select(NewLoad).ToList();
        var folder = SoundsFolder;
        bool writeStarters = FirstRun || StarterSounds.Items().Any(s => SoundPath(s.FileName) is { } p && !File.Exists(p));
        Task.Run(() =>
        {
            if (writeStarters)
            {
                try { StarterSoundSynth.WriteAll(folder); }
                catch (Exception ex) { AppLog.Error("Writing the starter sounds failed", ex); }
            }
            int loaded = 0;
            foreach (var load in items)
                if (Decode(load)) loaded++;
            AppLog.Info($"Sounds loaded: {loaded} of {items.Count}");
        });
    }

    /// <summary>Decodes one sound's file in the background; a failure marks its card.</summary>
    private void LoadSound(SoundItem sound)
    {
        var load = NewLoad(sound);
        Task.Run(() => Decode(load));
    }

    /// <summary>One sound's file to decode, with the ticket that says whether the result is still wanted. UI thread.</summary>
    private readonly record struct SoundLoad(string Id, string FileName, string? Path, int Ticket);

    private SoundLoad NewLoad(SoundItem s) => new(s.Id, s.FileName, SoundPath(s.FileName), soundLoads.Next(s.Id));

    /// <summary>
    /// Any thread: decodes the file, then hands the result to the UI thread, which keeps it only when the sound
    /// still exists and wasn't edited, replaced or loaded again meanwhile. False when the file can't be used.
    /// </summary>
    private bool Decode(SoundLoad load)
    {
        float[]? samples = null;
        if (load.Path == null)
        {
            AppLog.Info($"Sound '{load.Id}' skipped: bad file name '{load.FileName}'");
        }
        else
        {
            try { samples = SoundLoader.Load(load.Path); }
            catch (Exception ex) { AppLog.Info($"Sound '{load.Id}' skipped ({load.FileName}): {ex.Message}"); }
        }
        ui.BeginInvoke(() =>
        {
            if (disposed || !soundLoads.IsCurrent(load.Id, load.Ticket) || FindSound(load.Id) == null) return;
            bool cardChanged;
            if (samples != null)
            {
                mixer.Set(load.Id, samples);
                cardChanged = brokenSounds.Remove(load.Id);
            }
            else
            {
                cardChanged = brokenSounds.Add(load.Id);
            }
            if (cardChanged) RaiseChanged(); // the card says whether its file could be opened
        });
        return samples != null;
    }

    // ---------- hotkeys ----------

    /// <summary>Registers every hotkey of <see cref="HotkeyMap.All"/> again; the ones Windows refuses go to <see cref="HotkeyProblems"/>.</summary>
    public void RebuildHotkeys()
    {
        if (hotkeys == null || disposed) return;
        hotkeys.UnregisterAll();
        if (typing && TypingProbe?.Invoke() == false) typing = false; // the box was left without telling us
        if (captureSuspends > 0 || typing) return; // taken back by ResumeHotkeys / SetTyping(false)
        var problems = new List<string>();
        var refused = new HashSet<string>();
        foreach (var (action, key) in HotkeyMap.All(Settings))
        {
            if (hotkeys.Register(action, key)) continue;
            refused.Add(action);
            problems.Add(L.F("{0} ({1}) is already used by another app.", ActionDisplayName(action), key.ToString()));
        }
        hotkeyProblems = problems;
        refusedActions = refused;
        if (problems.Count > 0) AppLog.Info("Hotkeys refused: " + string.Join(" | ", problems));
        RaiseChanged();
    }

    /// <summary>
    /// Lets go of every hotkey (while a window is capturing a key: our own hotkeys would swallow the press).
    /// Call <see cref="ResumeHotkeys"/> afterwards to take them back.
    /// </summary>
    public void SuspendHotkeys()
    {
        captureSuspends++;
        hotkeys?.UnregisterAll();
    }

    /// <summary>Ends one <see cref="SuspendHotkeys"/>; the hotkeys come back when nothing else holds them off.</summary>
    public void ResumeHotkeys()
    {
        if (captureSuspends > 0) captureSuspends--;
        RebuildHotkeys();
    }

    /// <summary>
    /// True while one of EasyVocal's own text boxes has the keyboard: the hotkeys are let go meanwhile, so a plain-key
    /// hotkey ("7", "A") can still be typed into a name. Windows gives them back when the box loses the keyboard
    /// (also when the person switches to another app).
    /// </summary>
    public void SetTyping(bool on)
    {
        if (typing == on || disposed) return;
        typing = on;
        if (on) hotkeys?.UnregisterAll();
        else RebuildHotkeys();
    }

    /// <summary>True when one of our text boxes really has the keyboard now (set by the app; null = trust <see cref="SetTyping"/>).</summary>
    public Func<bool>? TypingProbe { get; set; }

    /// <summary>The hotkeys are let go because a text box has the keyboard.</summary>
    internal bool IsTyping => typing;

    /// <summary>How many hotkeys Windows holds for us right now.</summary>
    internal int RegisteredHotkeyCount => hotkeys?.Count ?? 0;

    /// <summary>The highest hotkey id in use (the e2e test checks it doesn't keep growing).</summary>
    internal int HighestHotkeyId => hotkeys?.HighestId ?? 0;

    /// <summary>The headphones of the running session stopped working (unplugged).</summary>
    internal bool MonitorLost => engine.MonitorLost;

    /// <summary>The sound's file is decoded and ready to play.</summary>
    internal bool IsSoundLoaded(string soundId) => mixer.TryGet(soundId, out _);

    /// <summary>For the e2e test: Windows reporting the press of the hotkey registered for <paramref name="action"/>.</summary>
    internal bool SimulateHotkeyPress(string action) => hotkeys?.SimulatePress(action) ?? false;

    /// <summary>True when Windows would accept this combination now. Only meaningful after <see cref="SuspendHotkeys"/>.</summary>
    public bool IsHotkeyFree(Hotkey key) => hotkeys?.IsFree(key) ?? true;

    /// <summary>What to call an action ("voice:robot"…) on screen: built-in names translated, the person's own as typed.</summary>
    public string ActionDisplayName(string action)
    {
        var (kind, id) = SplitAction(action);
        return kind switch
        {
            "voice" when FindVoice(id) is { } v => DisplayName(v),
            "sound" when FindSound(id) is { } s => DisplayName(s),
            _ => HotkeyMap.GeneralLabel(id) is { } label ? L.T(label) : id,
        };
    }

    /// <summary>"voice:robot" → ("voice", "robot"); an id without a kind → (the whole text, "").</summary>
    private static (string Kind, string Id) SplitAction(string action)
    {
        var colon = action.IndexOf(':');
        return colon < 0 ? (action, "") : (action[..colon], action[(colon + 1)..]);
    }

    // Runs inside the hotkey window's WndProc: an exception must never escape into Windows.
    private void OnHotkey(string action)
    {
        try
        {
            var (kind, id) = SplitAction(action);
            switch (kind)
            {
                case "voice": SelectVoice(id); break;
                case "sound": ToggleSound(id); break;
                case "general":
                    switch (id)
                    {
                        case HotkeyMap.ToggleFilter: FilterEnabled = !FilterEnabled; break;
                        case HotkeyMap.StopSounds: StopAllSounds(); break;
                        case HotkeyMap.MuteMic: MicMuted = !MicMuted; break;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Hotkey " + action + " failed", ex);
        }
    }

    // ---------- names ----------

    /// <summary>Built-in names are translated; the person's own names are shown as typed.</summary>
    public static string DisplayName(VoicePreset v) => v.BuiltIn ? L.T(v.Name) : v.Name;

    public static string DisplayName(SoundItem s) => s.BuiltIn ? L.T(s.Name) : s.Name;

    // ---------- devices ----------

    /// <summary>
    /// The chosen microphone has been missing a while and Windows' default is used for now: its name, else null.
    /// The saved choice is kept and used again as soon as it is back.
    /// </summary>
    public string? MicStandIn => engine.State == EngineState.Running && micInUse is { Fallback: true } pick ? pick.Name : null;

    /// <summary>The virtual cable is missing, but the mic and the headphones work (the voices can be tried with Hear me).</summary>
    public bool MonitorOnly => engine.MonitorOnly;

    /// <summary>What a session would open now (asking the mic picker only times how long a chosen mic has been missing).</summary>
    private AudioRoutes PickRoutes(IReadOnlyList<DeviceInfo> inputs, IReadOnlyList<DeviceInfo> outputs)
    {
        var mic = micPicker.Pick(Settings.MicDeviceId, inputs, DateTime.UtcNow);
        var call = DeviceRules.PickCall(Settings.OutputDeviceId, outputs);
        return new AudioRoutes(mic.Id, call, DeviceRules.PickMonitor(Settings.MonitorDeviceId, outputs, call));
    }

    // The three factories run on the UI thread, inside engine.Start / TryRecover; each notes what it opened.

    private IAudioInput MakeInput()
    {
        micInUse = null;
        usedMic = null;
        // Never pass null: null means Windows' default microphone, which could be the virtual cable (a feedback loop).
        var pick = micPicker.Pick(Settings.MicDeviceId, devices.Inputs(), DateTime.UtcNow);
        var id = pick.Id ?? throw new InvalidOperationException("No microphone is connected.");
        var input = devices.OpenMic(id);
        if (pick.Fallback) AppLog.Info("The chosen microphone is missing: using " + pick.Name + " for now");
        micInUse = pick;
        usedMic = id;
        return input;
    }

    private IAudioOutput MakeCallOutput()
    {
        usedCall = null;
        var id = DeviceRules.PickCall(Settings.OutputDeviceId, devices.Outputs()) ?? throw new NoVirtualCableException();
        var output = devices.OpenSpeaker(id);
        usedCall = id;
        return output;
    }

    /// <summary>The headphones (see <see cref="DeviceRules.PickMonitor"/>). Null = no monitor.</summary>
    private IAudioOutput? MakeMonitor()
    {
        usedMonitor = null;
        if (MonitorId(devices.Outputs()) is not { } id) return null;
        var output = devices.OpenSpeaker(id);
        usedMonitor = id;
        return output;
    }

    /// <summary>The headphones to open now (see <see cref="DeviceRules.PickMonitor"/>), or null for none.</summary>
    private string? MonitorId(IReadOnlyList<DeviceInfo> outputs) =>
        DeviceRules.PickMonitor(Settings.MonitorDeviceId, outputs, DeviceRules.PickCall(Settings.OutputDeviceId, outputs));

    /// <summary>The updater (Settings' "Check now"); null in the tests unless the smoke test sets a fake one.</summary>
    public UpdateCoordinator? Updates { get; set; }

    // ---------- Windows' default devices (VB-CABLE may take them) ----------

    /// <summary>How the VB-CABLE install is run (the e2e test fakes it; the smoke test never downloads anything).</summary>
    public Func<IProgress<double>, CancellationToken, Task<InstallResult>> CableInstaller { get; set; }

    /// <summary>Windows sends the PC's sound into the virtual cable (its default playback device is VB-CABLE): the person hears nothing.</summary>
    public bool SoundGoesToCable { get; private set; }

    /// <summary>The headphones the running session opened (for the e2e test).</summary>
    internal string? MonitorInUse => usedMonitor;

    private bool RemembersDefaults => Settings.PreCableOutputId != null || Settings.PreCableInputId != null;

    /// <summary>
    /// Right before the VB-CABLE installer runs: remembers Windows' default playback device and microphone (not when
    /// they are cable devices already), so they can be put back if the install makes the cable the default.
    /// </summary>
    public void RememberDefaultsBeforeCable()
    {
        Settings.PreCableOutputId = DefaultDeviceRules.Remember(devices.Defaults(playback: true), devices.Outputs());
        Settings.PreCableInputId = DefaultDeviceRules.Remember(devices.Defaults(playback: false), devices.Inputs());
        restoreUntil = null;
        AppLog.Info("Before installing VB-CABLE: default output " + (Settings.PreCableOutputId ?? "none") + ", default input " + (Settings.PreCableInputId ?? "none"));
        Save();
    }

    /// <summary>Nothing to put back any more (the install didn't happen, the restore window is over, Fix was pressed).</summary>
    public void ForgetDefaultsBeforeCable()
    {
        restoreUntil = null;
        if (!RemembersDefaults) return;
        Settings.PreCableOutputId = Settings.PreCableInputId = null;
        Save();
    }

    /// <summary>
    /// Looks at Windows' defaults (at start, after a device change). Within a minute of VB-CABLE showing up, a default
    /// it took goes back to exactly what the person had before the install; nothing else is changed without a click.
    /// </summary>
    private void CheckDefaults()
    {
        if (disposed) return;
        var outputs = devices.Outputs();
        var outDefaults = devices.Defaults(playback: true);
        if (RemembersDefaults)
        {
            var now = DateTime.UtcNow;
            if (restoreUntil == null && Platform.VirtualMic.Find(outputs) != null) restoreUntil = now + RestoreWindow;
            if (restoreUntil is { } until && now > until)
            {
                AppLog.Info("The default devices remembered for the VB-CABLE install are forgotten");
                ForgetDefaultsBeforeCable();
            }
            else if (restoreUntil != null)
            {
                bool changed = false;
                if (DefaultDeviceRules.RestoreTo(outDefaults, outputs, Settings.PreCableOutputId) is { } o)
                    changed |= SetDefault(o, "VB-CABLE took the default output: putting back");
                if (DefaultDeviceRules.RestoreTo(devices.Defaults(playback: false), devices.Inputs(), Settings.PreCableInputId) is { } i)
                    changed |= SetDefault(i, "VB-CABLE took the default input: putting back");
                if (changed) outDefaults = devices.Defaults(playback: true);
            }
        }
        bool taken = DefaultDeviceRules.TakenByCable(outDefaults, outputs);
        if (taken == SoundGoesToCable) return;
        SoundGoesToCable = taken;
        AppLog.Info(taken ? "Windows' default playback device is the virtual cable" : "Windows' default playback device is a real one again");
        RaiseChanged();
    }

    /// <summary>
    /// The banner's Fix: Windows' default playback device (and microphone, when it is the cable too) becomes the one
    /// the person had before the install, else the best real one. False when it couldn't be fixed.
    /// </summary>
    public bool FixDefaults()
    {
        if (disposed) return false;
        bool ok = true;
        var outputs = devices.Outputs();
        var outDefaults = devices.Defaults(playback: true);
        if (DefaultDeviceRules.FixTo(outDefaults, outputs, Settings.PreCableOutputId, playback: true) is { } o)
            ok &= SetDefault(o, "Fix: default output");
        else if (DefaultDeviceRules.TakenByCable(outDefaults, outputs))
            ok = false; // no real playback device to switch to
        if (DefaultDeviceRules.FixTo(devices.Defaults(playback: false), devices.Inputs(), Settings.PreCableInputId, playback: false) is { } i)
            ok &= SetDefault(i, "Fix: default input");
        if (ok) ForgetDefaultsBeforeCable();
        CheckDefaults();
        RaiseChanged();
        return ok;
    }

    private bool SetDefault(string id, string why)
    {
        bool ok = devices.SetDefault(id);
        AppLog.Info(why + " " + id + (ok ? "" : " (failed)"));
        return ok;
    }

    /// <summary>Every active device and which ones are the defaults: a change means the engine may pick other devices.</summary>
    private string DeviceFingerprint()
    {
        var sb = new StringBuilder();
        foreach (var d in devices.Inputs()) sb.Append(d.Id).Append(d.IsDefault ? "*|" : "|");
        sb.Append('#');
        foreach (var d in devices.Outputs()) sb.Append(d.Id).Append(d.IsDefault ? "*|" : "|");
        return sb.ToString();
    }

    // Windows audio thread.
    private void OnDevicesChanged() => ui.BeginInvoke(() =>
    {
        if (disposed) return;
        deviceTimer.Stop();
        deviceTimer.Start(); // several notifications arrive together: act once they settle
    });

    private void DevicesSettled()
    {
        deviceTimer.Stop();
        if (disposed) return;
        CheckDefaults();
        var now = DeviceFingerprint();
        if (now == deviceFingerprint) return;
        deviceFingerprint = now;
        var state = engine.State;
        if (state == EngineState.Running)
        {
            // Only when this session would now open other devices (a mic came back, new default headphones…), or its
            // headphones stopped working. An unrelated device changes nothing.
            var wanted = PickRoutes(devices.Inputs(), devices.Outputs());
            var inUse = new AudioRoutes(usedMic, usedCall, usedMonitor);
            bool restart = AudioRoutes.NeedsRestart(inUse, wanted, engine.MonitorLost);
            AppLog.Info("Devices changed (engine Running): " + (restart ? "restarting the audio" : "nothing to do"));
            if (restart) RestartAudio();
        }
        else
        {
            AppLog.Info("Devices changed (engine " + state + ")");
            if (state != EngineState.Stopped) Recover();
        }
        RaiseChanged();
    }

    /// <summary>
    /// The 2 s recovery (and a device change while the audio isn't running normally): reopens lost devices, failed
    /// headphones and the cable once it is installed. A session waiting for the cable switches to another mic when
    /// one should be used now (the engine keeps its mic running there, so nothing else would).
    /// </summary>
    private void Recover()
    {
        if (disposed || !started) return;
        try
        {
            if (engine.MonitorOnly && AudioRoutes.ShouldSwitchMic(usedMic, micPicker.Pick(Settings.MicDeviceId, devices.Inputs(), DateTime.UtcNow)))
            {
                AppLog.Info("Waiting for the virtual microphone: another microphone should be used now");
                RestartAudio();
                return;
            }
            engine.TryRecover();
            if (RemembersDefaults) CheckDefaults(); // the installer may switch the defaults a while after the cable shows up
        }
        catch (Exception ex) { AppLog.Error("Recovering the audio failed", ex); }
    }

    // Any thread.
    private void OnEngineStateChanged(EngineState state) => ui.BeginInvoke(() =>
    {
        if (disposed) return;
        AppLog.Info("Engine: " + state);
        RaiseChanged();
    });

    // Audio thread: post once, however many sounds start or end before the UI gets to it.
    private void OnPlayingChanged(string id, bool playing)
    {
        if (Interlocked.Exchange(ref changePosted, 1) == 1) return;
        ui.BeginInvoke(() =>
        {
            Volatile.Write(ref changePosted, 0);
            if (!disposed) RaiseChanged();
        });
    }

    private void RaiseChanged()
    {
        if (disposed) return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { AppLog.Error("A window failed to update", ex); }
    }
}
