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
/// headphones and a hotkey. A file that can't be opened shows a friendly reason and adds nothing.
/// </summary>
public partial class SoundEditorWindow : Window
{
    private readonly AppController controller;
    private readonly SoundItem? editing;
    private readonly string itemId;
    private readonly EmojiGrid emojiGrid;
    private float[]? samples;          // the decoded file, for the preview and to hand to the mixer
    private string? newFilePath;       // a file the person picked (to copy into the Sounds folder on Save)
    private string? pendingHotkey;
    private bool nameTouched, settingName, loadingFile, previewing, saving;
    private int loadTicket;

    /// <param name="editing">The sound to edit in place, or null to add one.</param>
    /// <param name="droppedPath">A file to start from (dropped on the Sounds tab), or null.</param>
    public SoundEditorWindow(AppController controller, SoundItem? editing, string? droppedPath)
    {
        this.controller = controller;
        this.editing = editing;
        itemId = editing?.Id ?? ItemOps.NewId();
        InitializeComponent();
        DarkTitleBar.Use(this);

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
            Filter = L.T("Sound files") + "|*.wav;*.mp3;*.ogg",
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
            samples = data;
            if (!existing)
            {
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
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
                }, samples!);
            }
            else
            {
                string? oldFile = fileName != null ? editing.FileName : null;
                // A built-in keeps its English name (it is translated where shown) unless the person typed another.
                if (!(editing.BuiltIn && name == AppController.DisplayName(editing))) editing.Name = name;
                editing.Emoji = emojiGrid.Selected;
                editing.Volume = VolumeSlider.Value;
                editing.Hotkey = pendingHotkey;
                if (fileName != null) editing.FileName = fileName;
                controller.SoundEdited(editing.Id, fileName != null ? samples : null, editing.BuiltIn ? null : oldFile);
            }

            // Windows could still refuse the key (an app grabbed it since we checked): don't keep it.
            if (pendingHotkey != null && controller.RefusedActions.Contains("sound:" + itemId))
            {
                var item = editing ?? controller.Settings.Sounds.First(s => s.Id == itemId);
                item.Hotkey = editing != null ? previousHotkey : null;
                controller.Save();
                controller.RebuildHotkeys();
                AskDialog.Tell(this, L.F("Another app is already using {0}. Pick a different one.", pendingHotkey));
            }
            DialogResult = true;
        }
        finally
        {
            saving = false;
            UpdateSave();
        }
    }
}
