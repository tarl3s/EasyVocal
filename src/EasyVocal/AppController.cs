using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EasyVocal.Audio;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Core.Storage;
using EasyVocal.Core.Text;
using EasyVocal.Hotkeys;

namespace EasyVocal;

/// <summary>
/// The one object windows talk to: owns the settings, the audio engine, the soundboard mixer and the global
/// hotkeys. Every member belongs to the UI thread; what the engine, the mixer and Windows' device notifications
/// report from their own threads is posted here with <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/>
/// (never <c>Invoke</c>: the engine may be joining one of those threads while the UI waits on it).
/// </summary>
public sealed class AppController : IDisposable
{
    public const string NormalVoiceId = "normal";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecoverEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeviceSettle = TimeSpan.FromMilliseconds(500);

    private readonly Dispatcher ui;
    private readonly VoiceChain chain = new(AudioFormat.SampleRate);
    private readonly SoundMixer mixer = new();
    private readonly AudioEngine engine;
    private readonly DispatcherTimer saveTimer, recoverTimer, deviceTimer;
    private GlobalHotkeys? hotkeys;
    private IReadOnlyList<string> hotkeyProblems = Array.Empty<string>();
    private HashSet<string> refusedActions = new();
    private VoiceSliders? previewSliders;
    private readonly HashSet<string> brokenSounds = new();
    private SoundPreview? preview;
    private string deviceFingerprint = "";
    private int changePosted;
    private bool started, disposed, savePending;

    /// <param name="dataFolder">Where settings.json and the Sounds folder live (normally <see cref="SettingsStore.DefaultFolder"/>).</param>
    public AppController(string dataFolder)
    {
        ui = Dispatcher.CurrentDispatcher;
        DataFolder = dataFolder;
        SoundsFolder = Path.Combine(dataFolder, "Sounds");
        FirstRun = !File.Exists(Path.Combine(dataFolder, "settings.json"));
        Settings = SettingsStore.Load(dataFolder);
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

    /// <summary>Opens the audio, registers the hotkeys (on <paramref name="hotkeyOwner"/>'s window handle) and loads the sounds.</summary>
    public void Start(Window hotkeyOwner)
    {
        if (started || disposed) return;
        started = true;

        if (FirstRun) Save(); // settings.json exists from now on
        EnsureActiveVoice();
        engine.FilterEnabled = Settings.Enabled;
        engine.MicMuted = Settings.MicMuted;
        engine.HearMe = Settings.HearMe;
        engine.HearSounds = Settings.HearSounds;
        ApplyChain();

        mixer.PlayingChanged += OnPlayingChanged;
        engine.StateChanged += OnEngineStateChanged;

        hotkeys = new GlobalHotkeys(hotkeyOwner);
        hotkeys.Pressed += OnHotkey;
        RebuildHotkeys();

        DeviceList.Changed += OnDevicesChanged;
        deviceFingerprint = DeviceFingerprint();
        engine.Start();
        AppLog.Info("Audio started: " + engine.State);
        UpdateRecoverTimer();

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
        UpdateRecoverTimer();
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
            DeviceList.Changed -= OnDevicesChanged;
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
        try { SettingsStore.Save(DataFolder, Settings); }
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
            engine.HearMe = value;
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

    /// <summary>Hides or shows a voice. Hiding the active voice goes back to Normal.</summary>
    public void SetVoiceHidden(string id, bool hidden)
    {
        var v = FindVoice(id);
        if (v == null || v.Hidden == hidden) return;
        var before = ActiveVoice;
        v.Hidden = hidden;
        EnsureActiveVoice();
        if (!ReferenceEquals(before, ActiveVoice)) ApplyChain();
        Save();
        RebuildHotkeys();
    }

    /// <summary>
    /// While the filter editor is open, the engine plays these sliders (and the filter counts as ON so they are heard);
    /// null goes back to the active voice and the person's ON/OFF choice.
    /// </summary>
    public void PreviewSliders(VoiceSliders? sliders)
    {
        if (disposed) return;
        previewSliders = sliders;
        ApplyChain();
        engine.FilterEnabled = sliders != null || Settings.Enabled;
    }

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

    /// <summary>A copy of a sound (it plays the same file); its audio is loaded in the background.</summary>
    public SoundItem DuplicateSound(string id)
    {
        var copy = ItemOps.DuplicateSound(Settings, id);
        LoadSound(copy);
        Save();
        RaiseChanged();
        return copy;
    }

    /// <summary>Adds a sound the person made; <paramref name="samples"/> is its file already decoded.</summary>
    public void AddSound(SoundItem sound, float[] samples)
    {
        Settings.Sounds.Add(sound);
        mixer.Set(sound.Id, samples);
        brokenSounds.Remove(sound.Id);
        Save();
        RebuildHotkeys();
    }

    /// <summary>
    /// The person edited this sound in place (the object was already changed). <paramref name="newSamples"/> is the
    /// decoded new file when it was replaced; <paramref name="oldFile"/> that file's name, deleted when nothing uses it.
    /// </summary>
    public void SoundEdited(string id, float[]? newSamples, string? oldFile)
    {
        if (newSamples != null)
        {
            mixer.Stop(id);
            mixer.Set(id, newSamples);
            brokenSounds.Remove(id);
        }
        if (oldFile != null) ItemOps.DeleteFileIfUnused(Settings, oldFile, SoundsFolder);
        Save();
        RebuildHotkeys();
    }

    /// <summary>Hides a built-in sound or removes the person's own (and its file when nothing else uses it).</summary>
    public void DeleteSound(string id)
    {
        mixer.Stop(id);
        bool builtIn = FindSound(id)?.BuiltIn == true;
        ItemOps.DeleteSound(Settings, id, SoundsFolder);
        if (!builtIn)
        {
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
            var output = MakeMonitor();
            if (output == null) return false;
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

    /// <summary>
    /// Writes the generated starter sounds that are missing (all of them on the first run), then decodes every
    /// sound into the mixer, off the UI thread. A missing or broken file is logged and skipped.
    /// </summary>
    private void LoadSoundsInBackground()
    {
        var items = Settings.Sounds.Select(s => (s.Id, s.FileName, Path: SoundPath(s.FileName))).ToList();
        var folder = SoundsFolder;
        bool writeStarters = FirstRun || StarterSounds.Items().Any(s => SoundPath(s.FileName) is { } p && !File.Exists(p));
        Task.Run(() =>
        {
            if (writeStarters)
            {
                try { StarterSoundSynth.WriteAll(folder); }
                catch (Exception ex) { AppLog.Error("Writing the starter sounds failed", ex); }
            }
            var broken = new List<string>();
            foreach (var (id, fileName, path) in items)
            {
                if (!TryLoadInto(id, fileName, path)) broken.Add(id);
            }
            AppLog.Info($"Sounds loaded: {items.Count - broken.Count} of {items.Count}");
            if (broken.Count == 0) return;
            ui.BeginInvoke(() =>
            {
                if (disposed) return;
                foreach (var id in broken) brokenSounds.Add(id);
                RaiseChanged(); // the cards of the files that failed say so
            });
        });
    }

    /// <summary>Decodes one sound's file into the mixer in the background; a failure marks its card.</summary>
    private void LoadSound(SoundItem sound)
    {
        var (id, fileName, path) = (sound.Id, sound.FileName, SoundPath(sound.FileName));
        Task.Run(() =>
        {
            bool ok = TryLoadInto(id, fileName, path);
            ui.BeginInvoke(() =>
            {
                if (disposed) return;
                if (ok) brokenSounds.Remove(id); else brokenSounds.Add(id);
                RaiseChanged();
            });
        });
    }

    /// <summary>Any thread. False (and a log line) when the file can't be used.</summary>
    private bool TryLoadInto(string id, string fileName, string? path)
    {
        if (path == null)
        {
            AppLog.Info($"Sound '{id}' skipped: bad file name '{fileName}'");
            return false;
        }
        try
        {
            mixer.Set(id, SoundLoader.Load(path));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Sound '{id}' skipped ({fileName}): {ex.Message}");
            return false;
        }
    }

    // ---------- hotkeys ----------

    /// <summary>Registers every hotkey of <see cref="HotkeyMap.All"/> again; the ones Windows refuses go to <see cref="HotkeyProblems"/>.</summary>
    public void RebuildHotkeys()
    {
        if (hotkeys == null || disposed) return;
        hotkeys.UnregisterAll();
        var problems = new List<string>();
        var refused = new HashSet<string>();
        foreach (var (action, key) in HotkeyMap.All(Settings))
        {
            if (hotkeys.Register(action, key)) continue;
            refused.Add(action);
            problems.Add(L.F("{0} ({1}) is already used by another app.", ActionName(action), key.ToString()));
        }
        hotkeyProblems = problems;
        refusedActions = refused;
        if (problems.Count > 0) AppLog.Info("Hotkeys refused: " + string.Join(" | ", problems));
        RaiseChanged();
    }

    /// <summary>
    /// Lets go of every hotkey (while a window is capturing a key: our own hotkeys would swallow the press).
    /// Call <see cref="RebuildHotkeys"/> afterwards to take them back.
    /// </summary>
    public void SuspendHotkeys() => hotkeys?.UnregisterAll();

    /// <summary>True when Windows would accept this combination now. Only meaningful after <see cref="SuspendHotkeys"/>.</summary>
    public bool IsHotkeyFree(Hotkey key) => hotkeys?.IsFree(key) ?? true;

    private string ActionName(string action)
    {
        var colon = action.IndexOf(':');
        var kind = colon < 0 ? action : action[..colon];
        var id = colon < 0 ? "" : action[(colon + 1)..];
        return kind switch
        {
            "voice" when FindVoice(id) is { } v => DisplayName(v),
            "sound" when FindSound(id) is { } s => DisplayName(s),
            _ => id switch
            {
                "toggleFilter" => L.T("Filter on/off"),
                "stopSounds" => L.T("Stop all sounds"),
                "muteMic" => L.T("Mute mic"),
                _ => id,
            },
        };
    }

    // Runs inside the hotkey window's WndProc: an exception must never escape into Windows.
    private void OnHotkey(string action)
    {
        try
        {
            var colon = action.IndexOf(':');
            if (colon < 0) return;
            var id = action[(colon + 1)..];
            switch (action[..colon])
            {
                case "voice": SelectVoice(id); break;
                case "sound": ToggleSound(id); break;
                case "general":
                    switch (id)
                    {
                        case "toggleFilter": FilterEnabled = !FilterEnabled; break;
                        case "stopSounds": StopAllSounds(); break;
                        case "muteMic": MicMuted = !MicMuted; break;
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

    private IAudioInput MakeInput()
    {
        // Never pass null: null means Windows' default microphone, which could be the virtual cable (a feedback loop).
        var id = Settings.MicDeviceId ?? DeviceRules.DefaultMic(DeviceList.Inputs())?.Id
            ?? throw new InvalidOperationException("No microphone is connected.");
        return new WasapiMicInput(id);
    }

    private IAudioOutput MakeCallOutput()
    {
        var id = Settings.OutputDeviceId ?? DeviceRules.FindVirtualCable(DeviceList.Outputs())?.Id
            ?? throw new NoVirtualCableException();
        return new WasapiSpeakerOutput(id);
    }

    /// <summary>The headphones: the chosen device, else the Windows default unless it is the cable. Null = no monitor.</summary>
    private IAudioOutput? MakeMonitor()
    {
        var outputs = DeviceList.Outputs();
        var callId = Settings.OutputDeviceId ?? DeviceRules.FindVirtualCable(outputs)?.Id;
        var id = Settings.MonitorDeviceId
            ?? (outputs.FirstOrDefault(d => d.IsDefault && !DeviceRules.IsVirtualCable(d.Name))
                ?? outputs.FirstOrDefault(d => !DeviceRules.IsVirtualCable(d.Name)))?.Id;
        if (id == null || id == callId) return null; // never play the monitor into the call
        return new WasapiSpeakerOutput(id);
    }

    /// <summary>Every active device and which ones are the defaults: a change means the engine may pick other devices.</summary>
    private static string DeviceFingerprint()
    {
        var sb = new StringBuilder();
        foreach (var d in DeviceList.Inputs()) sb.Append(d.Id).Append(d.IsDefault ? "*|" : "|");
        sb.Append('#');
        foreach (var d in DeviceList.Outputs()) sb.Append(d.Id).Append(d.IsDefault ? "*|" : "|");
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
        var now = DeviceFingerprint();
        if (now == deviceFingerprint) return;
        deviceFingerprint = now;
        var state = engine.State;
        AppLog.Info("Devices changed (engine " + state + ")");
        // While running, restart too: a lost monitor (headphones) never comes back by itself, and a new default
        // device should be picked up.
        if (state == EngineState.Running) RestartAudio();
        else if (state != EngineState.Stopped) Recover();
        RaiseChanged();
    }

    private void Recover()
    {
        if (disposed) return;
        try { engine.TryRecover(); }
        catch (Exception ex) { AppLog.Error("Recovering the audio failed", ex); }
        UpdateRecoverTimer();
    }

    private void UpdateRecoverTimer()
    {
        var state = engine.State;
        bool lost = state is EngineState.MicLost or EngineState.OutputLost or EngineState.NoVirtualCable;
        if (lost && !disposed) { if (!recoverTimer.IsEnabled) recoverTimer.Start(); }
        else recoverTimer.Stop();
    }

    // Any thread.
    private void OnEngineStateChanged(EngineState state) => ui.BeginInvoke(() =>
    {
        if (disposed) return;
        AppLog.Info("Engine: " + state);
        UpdateRecoverTimer();
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
