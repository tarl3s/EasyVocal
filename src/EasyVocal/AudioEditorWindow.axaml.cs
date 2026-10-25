using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// "✂ Edit audio…": a wave of the sound to pick a part of, playback on the headphones only, Undo / Redo, and the
/// edits of <see cref="AudioEdit"/> (cut, keep only, split, speed, pitch, volume, fades, reverse, echo, a voice,
/// remove silence). Nothing is saved here: <see cref="Result"/> is what the caller saves (a new WAV in the Sounds
/// folder; the original file is never changed). Split is the exception: it adds two new sounds at once.
/// </summary>
public partial class AudioEditorWindow : AppWindow
{
    private const int Sr = AudioFormat.SampleRate;

    private readonly AppController controller;
    /// <summary>Stops a decode still running when the window closes.</summary>
    private readonly CancellationTokenSource closing = new();
    private readonly string soundName;
    private readonly string emoji;
    private readonly double volume;
    private readonly string? originalPath;   // the original file, when what we opened is an edited copy
    private readonly string? soundId;        // the saved sound being edited (null: a new one not saved yet)
    private readonly AudioHistory history;
    private readonly DispatcherTimer playTimer;
    private float[]? originalSamples;        // decoded once for "Back to original"
    private int playOffset = -1;             // where the preview started in the sound; -1 = not playing
    private bool busy;

    /// <param name="samples">The sound as it plays now.</param>
    /// <param name="isOriginal">Those samples are the original file's (the sound was never edited).</param>
    /// <param name="originalPath">The original file, for "Back to original", when <paramref name="isOriginal"/> is false.</param>
    /// <param name="soundId">The saved sound (split puts the two new sounds after it), or null.</param>
    public AudioEditorWindow(AppController controller, string soundName, string emoji, double volume, float[] samples,
        bool isOriginal, string? originalPath, string? soundId)
    {
        this.controller = controller;
        this.soundName = string.IsNullOrWhiteSpace(soundName) ? L.T("New sound") : soundName.Trim();
        this.emoji = emoji;
        this.volume = volume;
        this.originalPath = isOriginal ? null : originalPath;
        this.soundId = soundId;
        history = new AudioHistory(new AudioState(samples, isOriginal));
        if (isOriginal) originalSamples = samples;
        InitializeComponent();
        Place("audioEditor", controller);
        Title = L.T("Edit audio") + " — " + this.soundName;

        PlayButton.Content = "▶  " + L.T("Play");
        PlayPartButton.Content = "▶  " + L.T("Play part");
        UndoButton.Content = "↶  " + L.T("Undo");
        RedoButton.Content = "↷  " + L.T("Redo");
        FillChoices();

        Wave.Samples = samples;
        Wave.SelectionChanged += UpdateAll;
        Wave.ViewChanged += UpdateScroll;
        playTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        playTimer.Tick += (_, _) => UpdatePlayhead();
        controller.PreviewEnded += OnPreviewEnded;
        Root.SizeChanged += (_, _) => FitTools();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            closing.Cancel();
            controller.PreviewEnded -= OnPreviewEnded;
            playTimer.Stop();
            if (playOffset >= 0) controller.StopPreview();
        };
        Opened += (_, _) =>
        {
            FitTools();
            Wave.Focus();
        };
        UpdateAll();
    }

    /// <summary>What to save, set by Save: the edited sound, or the original again ("Back to original"). Null = nothing to save.</summary>
    public AudioState? Result { get; private set; }

    /// <summary>The sound as it is in the editor now.</summary>
    internal float[] Current => history.Current.Samples;

    internal AudioHistory History => history;

    // ---------- the pickers ----------

    private void FillChoices()
    {
        foreach (var s in AudioEdit.Speeds)
            SpeedBox.Items.Add(new ComboBoxItem { Content = s.ToString("0.##", L.Culture) + "×", Tag = s });
        SpeedBox.SelectedIndex = Array.IndexOf(AudioEdit.Speeds, 1.5);
        SpeedModeBox.Items.Add(new ComboBoxItem { Content = L.T("Keep the pitch"), Tag = true });
        SpeedModeBox.Items.Add(new ComboBoxItem { Content = L.T("Change the pitch too"), Tag = false });
        SpeedModeBox.SelectedIndex = 0;
        foreach (var n in AudioEdit.PitchSteps)
        {
            string text = n == 1 ? L.T("1 step") : n == 12 ? L.T("12 steps (an octave)") : L.F("{0} steps", n);
            PitchBox.Items.Add(new ComboBoxItem { Content = text, Tag = n });
        }
        PitchBox.SelectedIndex = Array.IndexOf(AudioEdit.PitchSteps, 3);
        foreach (var v in controller.Settings.Voices)
        {
            if (v.Hidden || v.Kind != VoiceKind.Classic || v.Sliders.IsNeutral) continue;
            var item = new ComboBoxItem { Content = v.Emoji + "  " + AppController.DisplayName(v), Tag = v.Sliders.Clone() };
            VoiceBox.Items.Add(item);
        }
        if (VoiceBox.Items.Count > 0) VoiceBox.SelectedIndex = 0;
    }

    private static T Picked<T>(ComboBox box, T fallback) => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : fallback;

    // ---------- state shown ----------

    private void UpdateAll()
    {
        var x = Current;
        bool any = x.Length > 0;
        bool sel = Wave.HasSelection;
        string info = L.F("Length {0}", WaveformView.Time(x.Length));
        if (sel)
            info += "   ·   " + L.F("Picked {0} – {1} ({2} s)", WaveformView.Time(Wave.SelectionStart),
                WaveformView.Time(Wave.SelectionStart + Wave.SelectionLength),
                (Wave.SelectionLength / (double)Sr).ToString("0.0", L.Culture));
        else if (any) info += "   ·   " + L.F("Cursor at {0}", WaveformView.Time(Wave.CursorPosition));
        InfoText.Text = info;

        bool free = !busy;
        TopBar.IsEnabled = free;
        Tools.IsEnabled = free;
        UndoButton.IsEnabled = history.CanUndo;
        RedoButton.IsEnabled = history.CanRedo;
        PlayButton.IsEnabled = any;
        PlayPartButton.IsEnabled = sel;
        StopButton.IsEnabled = playOffset >= 0;
        CutButton.IsEnabled = sel && Wave.SelectionLength < x.Length;
        TrimButton.IsEnabled = sel && Wave.SelectionLength < x.Length;
        SplitButton.IsEnabled = !sel && Wave.CursorPosition > 0 && Wave.CursorPosition < x.Length;
        VoiceButton.IsEnabled = VoiceBox.Items.Count > 0;
        OriginalButton.IsEnabled = free && !history.Current.IsOriginal && (originalSamples != null || originalPath != null);
        ExportButton.IsEnabled = free && any;
        SaveButton.IsEnabled = free && history.Changed && any;
        UpdateScroll();
    }

    /// <summary>The scroll bar and the zoom buttons follow the view (also on their own, while the wave is dragged or zoomed).</summary>
    private void UpdateScroll()
    {
        double max = Math.Max(0, Current.Length - Wave.VisibleSamples);
        WaveScroll.IsVisible = max > 0;
        WaveScroll.Minimum = 0;
        WaveScroll.Maximum = max;
        WaveScroll.ViewportSize = Wave.VisibleSamples;
        WaveScroll.LargeChange = Wave.VisibleSamples * 0.9;
        WaveScroll.SmallChange = Wave.VisibleSamples * 0.1;
        WaveScroll.Value = Wave.ViewStart;
        ZoomInButton.IsEnabled = Current.Length > 0 && Wave.CanZoomIn;
        ZoomOutButton.IsEnabled = Wave.CanZoomOut;
        FitButton.IsEnabled = Wave.CanZoomOut;
    }

    private void WaveScroll_Scroll(object? sender, ScrollEventArgs e) => Wave.ScrollTo(WaveScroll.Value);

    /// <summary>The tools never squeeze the wave below ~120 px: past that they scroll.</summary>
    private void FitTools()
    {
        // On a short screen the how-to line gives its room to the wave and the tools (the tooltips still explain).
        HintText.IsVisible = Root.Bounds.Height >= 470;
        double others = 0;
        foreach (var child in Root.Children)
            if (child != ToolsScroll && child != Wave && child.IsVisible) others += child.DesiredSize.Height;
        ToolsScroll.MaxHeight = Math.Max(70, Root.Bounds.Height - others - 120);
    }

    private void Status(string text) => BusyText.Text = text;

    // ---------- playback (headphones only) ----------

    private void Play(int start, int count)
    {
        var part = AudioEdit.Slice(Current, start, count);
        if (part.Length == 0) return;
        if (!controller.PreviewSound(AudioEdit.Clamped(part), volume))
        {
            Status(L.T("No headphones or speakers were found to play the preview on."));
            return;
        }
        playOffset = start;
        playTimer.Start();
        UpdateAll();
    }

    private void StopPlaying()
    {
        if (playOffset < 0) return;
        controller.StopPreview();
        OnPreviewEnded();
    }

    private void OnPreviewEnded()
    {
        playOffset = -1;
        playTimer.Stop();
        Wave.Playhead = -1;
        UpdateAll();
    }

    private void UpdatePlayhead()
    {
        int played = controller.PreviewPosition;
        Wave.Playhead = playOffset >= 0 && played >= 0 ? playOffset + played : -1;
    }

    private void PlayButton_Click(object? sender, RoutedEventArgs e)
    {
        // From the cursor when it is placed inside the sound, else from the start.
        int from = !Wave.HasSelection && Wave.CursorPosition < Current.Length ? Wave.CursorPosition : 0;
        Play(from, Current.Length - from);
    }

    private void PlayPartButton_Click(object? sender, RoutedEventArgs e) => Play(Wave.SelectionStart, Wave.SelectionLength);

    private void StopButton_Click(object? sender, RoutedEventArgs e) => StopPlaying();

    // ---------- undo, zoom ----------

    private void UndoButton_Click(object? sender, RoutedEventArgs e)
    {
        if (busy || !history.Undo()) return;
        Show(history.Current);
    }

    private void RedoButton_Click(object? sender, RoutedEventArgs e)
    {
        if (busy || !history.Redo()) return;
        Show(history.Current);
    }

    private void ZoomInButton_Click(object? sender, RoutedEventArgs e) => ZoomAroundSelection(2);

    private void ZoomOutButton_Click(object? sender, RoutedEventArgs e) => ZoomAroundSelection(0.5);

    private void FitButton_Click(object? sender, RoutedEventArgs e)
    {
        Wave.Fit();
        UpdateAll();
    }

    /// <summary>Zooms keeping the cursor (or the picked part) in view.</summary>
    private void ZoomAroundSelection(double factor)
    {
        double focus = Wave.HasSelection ? Wave.SelectionStart + Wave.SelectionLength / 2.0 : Wave.CursorPosition;
        double x = (focus - Wave.ViewStart) / Wave.SamplesPerPixel;
        Wave.Zoom(factor, x >= 0 && x <= Wave.Bounds.Width ? x : null);
        UpdateAll();
    }

    // ---------- keyboard ----------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (busy || e.Handled) return;
        // Ctrl on Windows and Linux, Command on a Mac.
        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var focused = FocusManager?.GetFocusedElement();
        if (focused is ComboBox or TextBox) return; // their own keys
        switch (e.Key)
        {
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                RedoButton_Click(this, e);
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                UndoButton_Click(this, e);
                e.Handled = true;
                break;
            case Key.A when ctrl:
                Wave.Select(0, Current.Length);
                e.Handled = true;
                break;
            case Key.Space when focused is not Button:
                if (playOffset >= 0) StopPlaying();
                else if (Wave.HasSelection) PlayPartButton_Click(this, e);
                else PlayButton_Click(this, e);
                e.Handled = true;
                break;
            case Key.Delete when CutButton.IsEnabled:
                CutButton_Click(this, e);
                e.Handled = true;
                break;
        }
    }

    // ---------- edits ----------

    /// <summary>
    /// Runs one edit off the UI thread and shows the result as a new undo step (none when the sound came back the
    /// same, or empty). <paramref name="edit"/> returns the new sound and the part to select afterwards (count 0 =
    /// place the cursor there).
    /// </summary>
    private async Task Edit(Func<float[], (float[] Samples, int SelStart, int SelCount)> edit)
    {
        if (busy) return;
        StopPlaying();
        var input = Current;
        busy = true;
        Status(L.T("Working…"));
        UpdateAll();
        Cursor = new Cursor(StandardCursorType.Wait);
        try
        {
            var (result, selStart, selCount, same) = await Task.Run(() =>
            {
                var (y, s, c) = edit(input);
                return (y, s, c, y.AsSpan().SequenceEqual(input));
            });
            if (result.Length == 0)
            {
                Status(L.T("That would leave the sound empty."));
                return;
            }
            if (same)
            {
                Status(L.T("That didn't change the sound."));
                return; // no undo step for nothing
            }
            history.Push(new AudioState(result, false));
            Show(history.Current, selStart, selCount);
            Status("");
        }
        catch (Exception ex)
        {
            AppLog.Error("A sound edit failed", ex);
            Status(L.T("Something went wrong with that change. Nothing was changed."));
        }
        finally
        {
            busy = false;
            Cursor = null;
            UpdateAll();
        }
    }

    /// <summary>Shows a version of the sound, keeping the selection where it still fits.</summary>
    private void Show(AudioState state, int? selStart = null, int? selCount = null)
    {
        int s = selStart ?? Wave.SelectionStart;
        int c = selCount ?? Wave.SelectionLength;
        Wave.Samples = state.Samples;
        if (c > 0) Wave.Select(s, c);
        else Wave.PlaceCursor(selStart ?? Wave.CursorPosition);
        UpdateAll();
    }

    /// <summary>The picked part, or the whole sound when nothing is picked.</summary>
    private (int Start, int Count) Target() =>
        Wave.HasSelection ? (Wave.SelectionStart, Wave.SelectionLength) : (0, Current.Length);

    /// <summary>An edit of the picked part (or everything) that may change its length; the result stays picked when a part was.</summary>
    private Task EditRange(Func<float[], float[]> op)
    {
        var (start, count) = Target();
        bool picked = Wave.HasSelection;
        return Edit(x =>
        {
            var part = op(AudioEdit.Slice(x, start, count));
            var y = AudioEdit.Splice(x, start, count, part);
            return picked ? (y, start, part.Length) : (y, 0, 0);
        });
    }

    private async void CutButton_Click(object? sender, RoutedEventArgs e)
    {
        int start = Wave.SelectionStart, count = Wave.SelectionLength;
        if (count <= 0) return;
        await Edit(x => (AudioEdit.Cut(x, start, count), start, 0));
    }

    private async void TrimButton_Click(object? sender, RoutedEventArgs e)
    {
        int start = Wave.SelectionStart, count = Wave.SelectionLength;
        if (count <= 0) return;
        await Edit(x => (AudioEdit.Trim(x, start, count), 0, 0));
    }

    private async void SplitButton_Click(object? sender, RoutedEventArgs e)
    {
        int at = Wave.CursorPosition;
        var x = Current;
        if (busy) return;
        if (at <= 0 || at >= x.Length)
        {
            Status(L.T("Click inside the sound to place the cursor first."));
            return;
        }
        StopPlaying();
        var (a, b) = AudioEdit.Split(x, at);
        busy = true;
        Status(L.T("Working…"));
        UpdateAll();
        try
        {
            var (first, second) = await controller.SplitSoundAsync(soundId, soundName, emoji, volume, a, b);
            Status(L.F("Made two new sounds: “{0}” and “{1}”.", first.Name, second.Name));
            SplitDone = true;
        }
        catch (ObjectDisposedException) { return; } // EasyVocal quit meanwhile
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Splitting a sound failed", ex);
            Status(L.T("We couldn't save the new sounds."));
        }
        finally
        {
            busy = false;
            UpdateAll();
        }
    }

    /// <summary>A split added two sounds (for the tests).</summary>
    internal bool SplitDone { get; private set; }

    private async void SpeedButton_Click(object? sender, RoutedEventArgs e)
    {
        double speed = Picked(SpeedBox, 1.5);
        bool keep = Picked(SpeedModeBox, true);
        await EditRange(p => AudioEdit.ChangeSpeed(p, speed, keep));
    }

    private async void HigherButton_Click(object? sender, RoutedEventArgs e) => await Pitch(+1);

    private async void LowerButton_Click(object? sender, RoutedEventArgs e) => await Pitch(-1);

    private Task Pitch(int sign)
    {
        int steps = Picked(PitchBox, 3);
        return EditRange(p => AudioEdit.ShiftPitch(p, sign * steps));
    }

    private async void LouderButton_Click(object? sender, RoutedEventArgs e) => await EditRange(p => AudioEdit.Gain(p, AudioEdit.LouderFactor));

    private async void QuieterButton_Click(object? sender, RoutedEventArgs e) => await EditRange(p => AudioEdit.Gain(p, 1 / AudioEdit.LouderFactor));

    private async void NormalizeButton_Click(object? sender, RoutedEventArgs e) => await EditRange(p => AudioEdit.Normalize(p));

    private async void FadeInButton_Click(object? sender, RoutedEventArgs e) => await Fade(fadeIn: true);

    private async void FadeOutButton_Click(object? sender, RoutedEventArgs e) => await Fade(fadeIn: false);

    // With nothing picked a fade covers the first or last half second, not the whole sound.
    private Task Fade(bool fadeIn) => EditPart((x, s, c) => AudioEdit.OnRange(x, s, c, fadeIn ? AudioEdit.FadeIn : AudioEdit.FadeOut),
        Wave.HasSelection ? null : AudioEdit.DefaultFadeRange(Current.Length, fadeIn));

    private async void ReverseButton_Click(object? sender, RoutedEventArgs e) => await EditRange(AudioEdit.Reverse);

    private async void EchoLightButton_Click(object? sender, RoutedEventArgs e) => await Echo(AudioEdit.EchoStrength.Light);

    private async void EchoStrongButton_Click(object? sender, RoutedEventArgs e) => await Echo(AudioEdit.EchoStrength.Strong);

    private Task Echo(AudioEdit.EchoStrength strength) => EditPart((x, s, c) => AudioEdit.EchoRange(x, s, c, strength));

    private async void VoiceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (VoiceBox.SelectedItem is not ComboBoxItem { Tag: VoiceSliders sliders }) return;
        await EditPart((x, s, c) => AudioEdit.ApplyVoiceRange(x, s, c, sliders));
    }

    /// <summary>
    /// An edit of the whole sound given a range (<paramref name="range"/>, else the picked part or everything), such
    /// as one whose tail rings on over what follows; a picked part stays picked.
    /// </summary>
    private Task EditPart(Func<float[], int, int, float[]> op, (int Start, int Count)? range = null)
    {
        var (start, count) = range ?? Target();
        bool picked = Wave.HasSelection;
        return Edit(x =>
        {
            var y = op(x, start, count);
            return picked ? (y, start, count) : (y, 0, 0);
        });
    }

    private async void SilenceButton_Click(object? sender, RoutedEventArgs e)
    {
        var (start, count) = AudioEdit.SoundBounds(Current);
        if (count == 0 || (start == 0 && count == Current.Length))
        {
            Status(L.T("There is no silence to remove at the start or the end."));
            return;
        }
        await Edit(x => (AudioEdit.RemoveSilence(x), 0, 0));
    }

    // ---------- back to original, export, save ----------

    private async void OriginalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (busy || history.Current.IsOriginal) return;
        StopPlaying();
        if (originalSamples == null && originalPath != null)
        {
            busy = true;
            Status(L.T("Loading…"));
            UpdateAll();
            try
            {
                var path = originalPath;
                originalSamples = await Task.Run(() => SoundLoader.Load(path, controller.Platform.Decoders, closing.Token));
                Status("");
            }
            catch (OperationCanceledException) { return; } // the window closed
            catch (Exception ex)
            {
                AppLog.Info("The original sound file couldn't be read: " + ex.Message);
                Status(L.T("We couldn't open the original file."));
            }
            finally
            {
                busy = false;
            }
        }
        if (originalSamples != null)
        {
            // The very first version when that was the original (so "nothing changed" again), else a new step.
            if (history.Initial.IsOriginal) history.Push(history.Initial);
            else history.Push(new AudioState(originalSamples, true));
            Show(history.Current, 0, 0);
        }
        UpdateAll();
    }

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        StopPlaying();
        bool mp3 = Mp3 != null;
        var types = new List<FilePickerFileType> { new(L.T("WAV sound")) { Patterns = new[] { "*.wav" } } };
        if (mp3) types.Add(new FilePickerFileType(L.T("MP3 sound")) { Patterns = new[] { "*.mp3" } });
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("Save a copy as…"),
                SuggestedFileName = ItemOps.SafeStem(soundName),
                DefaultExtension = "wav",
                FileTypeChoices = types,
                ShowOverwritePrompt = true,
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            if (!Path.HasExtension(path)) path += ".wav";
            await Export(path);
        }
        catch (Exception ex)
        {
            AppLog.Error("The save dialog failed", ex);
            Status(L.T("We couldn't save that file."));
        }
    }

    /// <summary>This computer's MP3 encoder when it has a working one, else null (only WAV is offered).</summary>
    private IMp3Encoder? Mp3 => controller.Platform.Mp3Encoder is { Supported: true } m ? m : null;

    /// <summary>Writes the sound as it is now to <paramref name="path"/> (.mp3 when the name says so and the computer can, else WAV).</summary>
    internal async Task<bool> Export(string path)
    {
        var samples = AudioEdit.Clamped(Current);
        bool asMp3 = string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase) && Mp3 != null;
        busy = true;
        Status(L.T("Working…"));
        UpdateAll();
        try
        {
            await Task.Run(() =>
            {
                if (asMp3) Mp3!.Write(path, samples);
                else SoundWriter.WriteWav(path, samples);
            });
            Status(L.F("Saved a copy: {0}", Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving a copy of a sound failed", ex);
            Status(L.T("We couldn't save that file."));
            return false;
        }
        finally
        {
            busy = false;
            UpdateAll();
        }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (busy || !history.Changed) return;
        StopPlaying();
        Result = history.Current;
        Accept();
    }

    // ---------- for the smoke test ----------

    /// <summary>Picks a part of the sound like a drag would.</summary>
    internal void SelectForTest(int start, int count) => Wave.Select(start, count);

    internal void PlaceCursorForTest(int at) => Wave.PlaceCursor(at);

    /// <summary>Waits until no edit is running.</summary>
    internal async Task IdleAsync()
    {
        for (int i = 0; i < 600 && busy; i++) await Task.Delay(20);
    }

    /// <summary>
    /// Opens the editor for a saved sound (the sound card's "✂ Edit audio…") and saves what the person keeps:
    /// an edited copy, or the original again.
    /// </summary>
    public static async Task EditSavedSoundAsync(Window owner, AppController controller, SoundItem item)
    {
        var samples = controller.SoundSamples(item.Id);
        if (samples == null)
        {
            await AskDialog.TellAsync(owner, controller.IsSoundBroken(item.Id)
                ? L.T("This sound file couldn't be opened")
                : L.T("This sound is still loading. Try again in a moment."));
            return;
        }
        bool edited = !string.IsNullOrEmpty(item.OriginalFileName);
        var editor = new AudioEditorWindow(controller, AppController.DisplayName(item), item.Emoji, item.Volume, samples,
            isOriginal: !edited, originalPath: edited ? controller.SoundPath(item.OriginalFileName) : null, soundId: item.Id);
        await editor.ShowAsync(owner);
        if (!editor.Accepted || editor.Result is not { } result || controller.IsDisposed) return;
        await SaveResultAsync(owner, controller, item.Id, result);
    }

    /// <summary>Saves an editor's result on a saved sound; false (and the person is told) when the file can't be written.</summary>
    public static async Task<bool> SaveResultAsync(Window owner, AppController controller, string id, AudioState result)
    {
        try
        {
            if (result.IsOriginal) controller.RestoreOriginalAudio(id, result.Samples);
            else await controller.ApplyEditedAudioAsync(id, result.Samples);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving an edited sound failed", ex);
            await AskDialog.TellAsync(owner, L.T("We couldn't save the edited sound."));
            return false;
        }
    }
}
