using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// Adds or edits a soundboard sound: the file (chosen or dropped), a name, an emoji, the volume, a preview on the
/// headphones, "✂ Edit audio…" and a hotkey. A file that can't be opened shows a friendly reason and adds nothing.
/// Audio edits wait for Save like everything else: Save writes the edited copy (the original file is kept).
/// </summary>
public partial class SoundEditorWindow : AppWindow
{
    private readonly AppController controller;
    /// <summary>Stops a decode still running when the window closes.</summary>
    private readonly CancellationTokenSource closing = new();
    private readonly SoundItem? editing;
    private readonly string itemId;
    private readonly EmojiGrid emojiGrid;
    private float[]? samples;          // the sound as the person hears it now (edited or not), for the preview
    private float[]? fileSamples;      // the decoded file, what the mixer gets until an edited copy is saved
    private string? newFilePath;       // a file the person picked (to copy into the Sounds folder on Save)
    private string? pendingHotkey;
    private string nameSet = "";
    private bool nameTouched, loadingFile, previewing, saving;
    private int loadTicket;

    private enum AudioChange { None, Edited, Restored }
    /// <summary>What the audio editor did, applied on Save: Edited = <see cref="samples"/> is an edited version to save as a copy; Restored = back to the original file.</summary>
    private AudioChange audioChange;

    /// <param name="editing">The sound to edit in place, or null to add one.</param>
    /// <param name="droppedPath">A file to start from (dropped on the Sounds tab), or null.</param>
    public SoundEditorWindow(AppController controller, SoundItem? editing, string? droppedPath)
    {
        this.controller = controller;
        this.editing = editing;
        itemId = editing?.Id ?? ItemOps.NewId();
        InitializeComponent();
        Place("soundEditor", controller);

        SetName(editing != null ? AppController.DisplayName(editing) : "");
        nameTouched = editing != null;
        emojiGrid = new EmojiGrid(editing?.Emoji ?? "🔊");
        EmojiHost.Content = emojiGrid;
        VolumeSlider.Value = editing?.Volume ?? 0.8;
        ShowVolume();
        pendingHotkey = editing?.Hotkey;
        FileText.Text = editing != null ? editing.FileName : L.T("No file chosen yet");
        if (editing is { BuiltIn: true })
        {
            ChooseButton.IsVisible = false; // a built-in keeps its own file
            DropHint.IsVisible = false;
        }
        UpdatePreviewButton();
        UpdateHotkeyButton();
        UpdateSave();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        controller.PreviewEnded += OnPreviewEnded;
        Closing += (_, e) => e.Cancel |= saving && !Quitting; // the edited copy is being written: Save closes the window when done (not when EasyVocal quits)
        Closed += (_, _) =>
        {
            closing.Cancel();
            controller.PreviewEnded -= OnPreviewEnded;
            controller.StopPreview();
        };
        Opened += (_, _) =>
        {
            if (droppedPath != null) _ = LoadFileAsync(droppedPath);
            else if (editing != null) _ = LoadFileAsync(controller.SoundPath(editing.FileName), existing: true);
        };
    }

    // ---------- the file ----------

    private async void ChooseButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Choose file…"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(L.T("Sound and video files")) { Patterns = SoundLoader.Extensions.Select(x => "*" + x).ToArray() },
                },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await LoadFileAsync(path);
        }
        catch (Exception ex) { AppLog.Error("The file picker failed", ex); }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!CanTakeFile || !e.DataTransfer.Contains(DataFormat.File)) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!CanTakeFile) return;
        var path = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
        if (path == null) return;
        e.Handled = true;
        _ = LoadFileAsync(path);
    }

    private bool CanTakeFile => editing is not { BuiltIn: true };

    /// <summary>Decodes the file off the UI thread. Failure leaves the previous choice as it was and says why.</summary>
    private async Task LoadFileAsync(string? path, bool existing = false)
    {
        if (saving) return; // Save is copying the current file: keep it as it is
        int ticket = ++loadTicket;
        loadingFile = true;
        ShowStatus(L.T("Loading…"), error: false);
        UpdateSave();
        try
        {
            if (path == null) throw new SoundLoadException("We couldn't find that sound file.");
            var data = await Task.Run(() => SoundLoader.Load(path, controller.Platform.Decoders, closing.Token));
            if (ticket != loadTicket) return;
            samples = fileSamples = data;
            if (!existing)
            {
                audioChange = AudioChange.None;
                newFilePath = path;
                FileText.Text = Path.GetFileName(path);
                if (!nameTouched) SetName(Path.GetFileNameWithoutExtension(path));
            }
            HideStatus();
        }
        catch (OperationCanceledException) { return; } // the window closed
        catch (SoundLoadException ex)
        {
            if (ticket != loadTicket) return;
            ShowStatus(L.T(ex.Message), error: true);
        }
        catch (Exception ex)
        {
            if (ticket != loadTicket) return;
            AppLog.Error("Reading a sound file failed", ex);
            ShowStatus(L.T("We couldn't read that sound file. It may be damaged."), error: true);
        }
        finally
        {
            if (ticket == loadTicket)
            {
                loadingFile = false;
                UpdatePreviewButton();
                UpdateSave();
            }
        }
    }

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = Res.Brush(error ? "Danger" : "TextDim");
        StatusText.IsVisible = true;
    }

    private void HideStatus() => StatusText.IsVisible = false;

    // ---------- name, volume ----------

    /// <summary>Puts a name in the box that the person didn't type (the file's name, the sound's name).</summary>
    private void SetName(string text)
    {
        nameSet = text;
        NameBox.Text = text;
    }

    // TextChanged may come after the change (Avalonia raises it once the text settled): a name we set is told apart by its text.
    private void NameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if ((NameBox.Text ?? "") != nameSet) nameTouched = true;
        UpdateSave();
    }

    private void VolumeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e) => ShowVolume();

    private void ShowVolume() => VolumeText.Text = Math.Round(VolumeSlider.Value * 100) + "%";

    // ---------- preview ----------

    private void PreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        if (previewing)
        {
            controller.StopPreview();
            previewing = false;
        }
        else if (samples != null)
        {
            if (controller.PreviewSound(samples, VolumeSlider.Value)) { previewing = true; HideStatus(); }
            else ShowStatus(L.T("No headphones or speakers were found to play the preview on."), error: true);
        }
        UpdatePreviewButton();
    }

    private void OnPreviewEnded()
    {
        previewing = false;
        UpdatePreviewButton();
    }

    private void UpdatePreviewButton()
    {
        PreviewButton.Content = previewing ? "■  " + L.T("Stop") : "▶  " + L.T("Preview");
        PreviewButton.IsEnabled = samples != null && !loadingFile;
        EditAudioButton.Content = "✂  " + L.T("Edit audio…");
        EditAudioButton.IsEnabled = samples != null && !loadingFile;
    }

    // ---------- edit audio ----------

    private async void EditAudioButton_Click(object? sender, RoutedEventArgs e)
    {
        if (samples == null) return;
        if (previewing)
        {
            controller.StopPreview();
            previewing = false;
            UpdatePreviewButton();
        }
        bool neverEdited = newFilePath != null || string.IsNullOrEmpty(editing?.OriginalFileName);
        bool isOriginal = audioChange == AudioChange.Restored || (audioChange == AudioChange.None && neverEdited);
        var editor = new AudioEditorWindow(controller, ShownName, emojiGrid.Selected, VolumeSlider.Value, samples,
            isOriginal, isOriginal ? null : OriginalPath(), editing?.Id);
        await editor.ShowAsync(this);
        if (!editor.Accepted || editor.Result is not { } result) return;
        samples = result.Samples;
        audioChange = !result.IsOriginal ? AudioChange.Edited : neverEdited ? AudioChange.None : AudioChange.Restored;
        UpdateFileText();
        UpdateSave();
    }

    /// <summary>The file the sound came from: a file just picked, else the sound's original (or its only) file.</summary>
    private string? OriginalPath() =>
        newFilePath ?? controller.SoundPath(string.IsNullOrEmpty(editing?.OriginalFileName) ? editing?.FileName : editing.OriginalFileName);

    /// <summary>The name as typed, or "New sound" while the box is empty.</summary>
    private string ShownName => string.IsNullOrWhiteSpace(NameBox.Text) ? L.T("New sound") : NameBox.Text.Trim();

    private void UpdateFileText()
    {
        string file = newFilePath != null ? Path.GetFileName(newFilePath) : editing?.FileName ?? L.T("No file chosen yet");
        if (audioChange == AudioChange.Restored && editing?.OriginalFileName is { } original) file = original;
        FileText.Text = audioChange == AudioChange.Edited ? file + "  ·  " + L.T("edited") : file;
    }

    /// <summary>Writes the edited audio as the sound's new file (the person is told when that fails).</summary>
    private async Task SaveEditedAudio(float[] edited)
    {
        if (await AudioEditorWindow.SaveResultAsync(this, controller, itemId, new AudioState(edited, false))) audioChange = AudioChange.None;
    }

    // ---------- hotkey ----------

    private async void HotkeyButton_Click(object? sender, RoutedEventArgs e)
    {
        var pick = await HotkeyCaptureDialog.PickAsync(this, controller, "sound:" + itemId, ShownName, pendingHotkey);
        if (!pick.Ok || controller.IsDisposed) return;
        pendingHotkey = pick.Hotkey;
        UpdateHotkeyButton();
    }

    private void UpdateHotkeyButton() =>
        HotkeyButton.Content = string.IsNullOrEmpty(pendingHotkey) ? "⌨  " + L.T("Set hotkey…") : "⌨  " + pendingHotkey;

    // ---------- save ----------

    private void UpdateSave() =>
        SaveButton.IsEnabled = !saving && !loadingFile && !string.IsNullOrWhiteSpace(NameBox.Text) && (editing != null || samples != null);

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0 || saving) return;
        saving = true;
        UpdateSave();
        try
        {
            string? fileName = null;
            if (newFilePath != null)
            {
                // Off the UI thread: a big file takes a while to copy (and a video's sound is written as a WAV instead).
                var (path, folder, decoded) = (newFilePath, controller.SoundsFolder, fileSamples!);
                try { fileName = await Task.Run(() => ItemOps.ImportSound(path, folder, decoded)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error("Copying a sound file failed", ex);
                    ShowStatus(L.T("We couldn't copy that sound into EasyVocal."), error: true);
                    return;
                }
            }

            if (controller.IsDisposed) return; // EasyVocal quit while the file was copied
            string? previousHotkey = editing?.Hotkey;
            if (editing == null)
            {
                controller.AddSound(new SoundItem
                {
                    Id = itemId,
                    Name = name,
                    Emoji = emojiGrid.Selected,
                    FileName = fileName!,
                    Volume = VolumeSlider.Value,
                    Hotkey = pendingHotkey,
                }, fileSamples!);
            }
            else
            {
                string? oldFile = fileName != null ? editing.FileName : null;
                // A new file starts a new story: the old original (if it had one) goes too when nothing uses it.
                string? oldOriginal = fileName != null ? editing.OriginalFileName : null;
                // A built-in keeps its English name (it is translated where shown) unless the person typed another.
                if (!(editing.BuiltIn && name == AppController.DisplayName(editing))) editing.Name = name;
                editing.Emoji = emojiGrid.Selected;
                editing.Volume = VolumeSlider.Value;
                editing.Hotkey = pendingHotkey;
                if (fileName != null)
                {
                    editing.FileName = fileName;
                    editing.OriginalFileName = null;
                }
                controller.SoundEdited(editing.Id, fileName != null ? fileSamples : null,
                    editing.BuiltIn ? null : oldFile, editing.BuiltIn ? null : oldOriginal);
                if (audioChange == AudioChange.Restored) controller.RestoreOriginalAudio(editing.Id, samples);
            }
            // The edited audio is written as a copy next to the file it came from (that one is kept as the original).
            // The mixer only gets the edited audio once that copy is written: if it fails the person is told and
            // the sound keeps playing its (unedited) file.
            if (audioChange == AudioChange.Edited && samples != null) await SaveEditedAudio(samples);
            if (controller.IsDisposed) return;

            // The system could still refuse the key (an app grabbed it since we checked): don't keep it.
            if (pendingHotkey != null && controller.RefusedActions.Contains("sound:" + itemId))
            {
                var item = editing ?? controller.Settings.Sounds.First(s => s.Id == itemId);
                item.Hotkey = editing != null ? previousHotkey : null;
                controller.Save();
                controller.RebuildHotkeys();
                await AskDialog.TellAsync(this, L.F("Another app is already using {0}. Pick a different one.", pendingHotkey));
            }
            saving = false; // lets the window close
            Accept();
        }
        finally
        {
            saving = false;
            UpdateSave();
        }
    }

    // ---------- for the smoke test ----------

    /// <summary>As if the audio editor had given back <paramref name="edited"/> (saved on Save, like a real edit).</summary>
    internal void UseEditedAudioForTest(float[] edited)
    {
        samples = edited;
        audioChange = AudioChange.Edited;
        UpdateFileText();
        UpdateSave();
    }

    /// <summary>Waits until the file is decoded and no Save is running (at most ~12 s).</summary>
    internal async Task IdleAsync()
    {
        for (int i = 0; i < 600 && (loadingFile || saving); i++) await Task.Delay(20);
    }
}
