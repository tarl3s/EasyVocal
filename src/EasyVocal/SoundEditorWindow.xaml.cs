using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Core.Text;
using Microsoft.Win32;

namespace EasyVocal;

/// <summary>
/// Adds or edits a soundboard sound: the file (chosen or dropped), a name, an emoji, the volume, a preview on the
/// headphones, "✂ Edit audio…" and a hotkey. A file that can't be opened shows a friendly reason and adds nothing.
/// Audio edits wait for Save like everything else: Save writes the edited copy (the original file is kept).
/// </summary>
public partial class SoundEditorWindow : Window
{
    private readonly AppController controller;
    private readonly SoundItem? editing;
    private readonly string itemId;
    private readonly EmojiGrid emojiGrid;
    private float[]? samples;          // the sound as the person hears it now (edited or not), for the preview
    private float[]? fileSamples;      // the decoded file, what the mixer gets until an edited copy is saved
    private string? newFilePath;       // a file the person picked (to copy into the Sounds folder on Save)
    private string? pendingHotkey;
    private bool nameTouched, settingName, loadingFile, previewing, saving;
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
        DarkTitleBar.Use(this);
        WindowPlacer.Use(this, "soundEditor", controller);

        SetName(editing != null ? AppController.DisplayName(editing) : "");
        nameTouched = editing != null;
        emojiGrid = new EmojiGrid(editing?.Emoji ?? "🔊");
        EmojiHost.Content = emojiGrid;
        VolumeSlider.Value = editing?.Volume ?? 0.8;
        pendingHotkey = editing?.Hotkey;
        FileText.Text = editing != null ? editing.FileName : L.T("No file chosen yet");
        if (editing is { BuiltIn: true })
        {
            ChooseButton.Visibility = Visibility.Collapsed; // a built-in keeps its own file
            DropHint.Visibility = Visibility.Collapsed;
        }
        UpdatePreviewButton();
        UpdateHotkeyButton();
        UpdateSave();

        controller.PreviewEnded += OnPreviewEnded;
        Closing += (_, e) => e.Cancel |= saving; // the edited copy is being written: Save closes the window when done
        Closed += (_, _) =>
        {
            controller.PreviewEnded -= OnPreviewEnded;
            controller.StopPreview();
        };
        Loaded += (_, _) =>
        {
            if (droppedPath != null) _ = LoadFileAsync(droppedPath);
            else if (editing != null) _ = LoadFileAsync(controller.SoundPath(editing.FileName), existing: true);
        };
    }

    // ---------- the file ----------

    private void ChooseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Choose file…"),
            Filter = L.T("Sound and video files") + "|" + SoundLoader.DialogPattern,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) _ = LoadFileAsync(dialog.FileName);
    }

    protected override void OnPreviewDragOver(DragEventArgs e)
    {
        base.OnPreviewDragOver(e);
        if (!CanTakeFile) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    protected override void OnPreviewDrop(DragEventArgs e)
    {
        base.OnPreviewDrop(e);
        if (!CanTakeFile || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;
        _ = LoadFileAsync(files[0]);
    }

    private bool CanTakeFile => editing is not { BuiltIn: true };

    /// <summary>Decodes the file off the UI thread. Failure leaves the previous choice as it was and says why.</summary>
    private async Task LoadFileAsync(string? path, bool existing = false)
    {
        int ticket = ++loadTicket;
        loadingFile = true;
        ShowStatus(L.T("Loading…"), error: false);
        UpdateSave();
        try
        {
            if (path == null) throw new SoundLoadException("We couldn't find that sound file.");
            var data = await Task.Run(() => SoundLoader.Load(path));
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
        StatusText.Foreground = (Brush)FindResource(error ? "Danger" : "TextDim");
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusText.Visibility = Visibility.Collapsed;

    // ---------- name, volume ----------

    private void SetName(string text)
    {
        settingName = true;
        NameBox.Text = text;
        settingName = false;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!settingName) nameTouched = true;
        UpdateSave();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        VolumeText.Text = Math.Round(e.NewValue * 100) + "%";

    // ---------- preview ----------

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
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

    private void EditAudioButton_Click(object sender, RoutedEventArgs e)
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
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? L.T("New sound") : NameBox.Text.Trim();
        var editor = new AudioEditorWindow(controller, name, emojiGrid.Selected, VolumeSlider.Value, samples,
            isOriginal, isOriginal ? null : OriginalPath(), editing?.Id) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        samples = result.Samples;
        audioChange = !result.IsOriginal ? AudioChange.Edited : neverEdited ? AudioChange.None : AudioChange.Restored;
        UpdateFileText();
        UpdateSave();
    }

    /// <summary>The file the sound came from: a file just picked, else the sound's original (or its only) file.</summary>
    private string? OriginalPath() =>
        newFilePath ?? (string.IsNullOrEmpty(editing?.OriginalFileName) ? controller.SoundPath(editing?.FileName) : controller.SoundPath(editing!.OriginalFileName));

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

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? L.T("New sound") : NameBox.Text.Trim();
        if (HotkeyCaptureDialog.Pick(this, controller, "sound:" + itemId, name, pendingHotkey, out var result))
        {
            pendingHotkey = result;
            UpdateHotkeyButton();
        }
    }

    private void UpdateHotkeyButton() =>
        HotkeyButton.Content = string.IsNullOrEmpty(pendingHotkey) ? "⌨  " + L.T("Set hotkey…") : "⌨  " + pendingHotkey;

    // ---------- save ----------

    private void UpdateSave() =>
        SaveButton.IsEnabled = !saving && !loadingFile && !string.IsNullOrWhiteSpace(NameBox.Text) && (editing != null || samples != null);

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        saving = true;
        UpdateSave();
        try
        {
            string? fileName = null;
            if (newFilePath != null)
            {
                try { fileName = ItemOps.ImportSoundFile(newFilePath, controller.SoundsFolder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error("Copying a sound file failed", ex);
                    ShowStatus(L.T("We couldn't copy that sound into EasyVocal."), error: true);
                    return;
                }
            }

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

            // Windows could still refuse the key (an app grabbed it since we checked): don't keep it.
            if (pendingHotkey != null && controller.RefusedActions.Contains("sound:" + itemId))
            {
                var item = editing ?? controller.Settings.Sounds.First(s => s.Id == itemId);
                item.Hotkey = editing != null ? previousHotkey : null;
                controller.Save();
                controller.RebuildHotkeys();
                AskDialog.Tell(this, L.F("Another app is already using {0}. Pick a different one.", pendingHotkey));
            }
            saving = false; // lets the window close
            DialogResult = true;
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
