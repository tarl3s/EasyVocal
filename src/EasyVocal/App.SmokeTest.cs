using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// <c>--smoketest</c>: starts like a normal launch, saves a picture of each tab (<c>smoke_voices.png</c>,
/// <c>smoke_sounds.png</c> in the data folder), opens every editor and dialog, drives them (add a filter, add a
/// sound from a file, a broken file, a hotkey…), saves a picture of each, then quits the normal way and logs
/// "Smoke test passed". Proves the app starts and closes cleanly without a person (use it with <c>--data</c> and a
/// scratch folder; it adds and removes items there). Exit code 1 when a check failed.
/// </summary>
public partial class App
{
    private int smokeFailures;

    private void RunSmokeTest(string dataFolder)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (window == null || controller == null) return;
            try
            {
                await SmokeSteps(window, controller, dataFolder);
            }
            catch (Exception ex)
            {
                AppLog.Error("Smoke test step failed", ex);
                smokeFailures++;
            }
            AppLog.Info($"Smoke test: language {L.Language}, audio {controller.State}, {window.VoiceCount} voices and "
                        + $"{window.SoundCount} sounds on screen");
            if (smokeFailures > 0)
            {
                AppLog.Info($"Smoke test FAILED: {smokeFailures} check(s)");
                Environment.ExitCode = 1;
            }
            Quit(ask: false);
            if (smokeFailures == 0) AppLog.Info("Smoke test passed");
        };
        timer.Start();
    }

    private void Check(bool ok, string what)
    {
        if (!ok) smokeFailures++;
        AppLog.Info((ok ? "Smoke check ok: " : "Smoke check FAILED: ") + what);
    }

    private async Task SmokeSteps(MainWindow main, AppController c, string dataFolder)
    {
        string Png(string name) => Path.Combine(dataFolder, "smoke_" + name + ".png");
        Snapshot(main, Png("voices"));
        main.Tabs.SelectedIndex = 1;
        main.UpdateLayout();
        Snapshot(main, Png("sounds"));
        main.SetShowHidden(true);
        main.UpdateLayout();
        Snapshot(main, Png("hidden"));
        main.SetShowHidden(false);
        main.Tabs.SelectedIndex = 0;

        // ---- copies and deletes ----
        int voices = c.Settings.Voices.Count;
        var copy = c.DuplicateVoice("monster");
        Check(c.Settings.Voices.Count == voices + 1 && !copy.BuiltIn && copy.Hotkey == null, "duplicate a built-in voice");
        c.DeleteVoice(copy.Id);
        Check(c.Settings.Voices.Count == voices, "delete the copy");

        // ---- filter editor: a built-in opens as a copy, sliders are heard, Hear me comes back ----
        bool hearBefore = c.HearMe;
        var monster = c.Settings.Voices.First(v => v.Id == "monster");
        Dialog(new FilterEditorWindow(c, monster) { Owner = main }, w =>
        {
            Check(c.HearMe, "Hear me is on while the filter editor is open");
            Check(w.SaveButton.Content as string == L.T("Save as new filter"), "a built-in voice offers 'Save as new filter'");
            SnapshotWindow(w, Png("filter_editor"));
        });
        Check(c.HearMe == hearBefore, "Hear me is put back after the filter editor");

        Dialog(new FilterEditorWindow(c, null) { Owner = main }, w =>
        {
            w.NameBox.Text = "Smoke filter";
            w.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        var mine = c.Settings.Voices.FirstOrDefault(v => v.Name == "Smoke filter");
        Check(mine is { BuiltIn: false }, "a new filter is added");
        if (mine != null)
        {
            Dialog(new FilterEditorWindow(c, mine) { Owner = main }, w => SnapshotWindow(w, Png("filter_editor_own")));
            c.DeleteVoice(mine.Id);
            Check(c.Settings.Voices.All(v => v.Id != mine.Id), "the filter is deleted");
        }

        // ---- sound editor: add from a file, copy, delete ----
        var ding = c.SoundPath("ding.wav");
        if (ding == null || !File.Exists(ding))
        {
            Check(false, "the starter sound ding.wav exists");
            return;
        }
        var source = Path.Combine(dataFolder, "smoke source.wav");
        File.Copy(ding, source, overwrite: true);
        int sounds = c.Settings.Sounds.Count;
        Dialog(new SoundEditorWindow(c, null, source) { Owner = main }, w =>
        {
            Check(w.NameBox.Text == "smoke source", "the sound's name starts as the file name");
            Check(w.SaveButton.IsEnabled && w.PreviewButton.IsEnabled, "a loaded sound can be saved and previewed");
            SnapshotWindow(w, Png("sound_editor_new"));
            w.NameBox.Text = "Smoke sound";
            w.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, wait: 1200);
        var added = c.Settings.Sounds.FirstOrDefault(s => s.Name == "Smoke sound");
        Check(c.Settings.Sounds.Count == sounds + 1 && added != null && File.Exists(Path.Combine(c.SoundsFolder, added.FileName)),
            "the sound is added and its file copied into the Sounds folder");
        if (added != null)
        {
            var again = c.DuplicateSound(added.Id);
            Check(again.FileName == added.FileName && !again.BuiltIn, "a copy of a sound shares the file");
            Dialog(new SoundEditorWindow(c, added, null) { Owner = main }, w => SnapshotWindow(w, Png("sound_editor_edit")), wait: 1000);
            c.DeleteSound(again.Id);
            Check(File.Exists(Path.Combine(c.SoundsFolder, added.FileName)), "deleting the copy keeps the shared file");
            c.DeleteSound(added.Id);
            Check(c.Settings.Sounds.Count == sounds && !File.Exists(Path.Combine(c.SoundsFolder, added.FileName)),
                "deleting the last sound using a file removes the file");
        }

        // ---- a file that isn't a sound: friendly error, nothing added ----
        var fake = Path.Combine(dataFolder, "smoke fake.mp3");
        File.WriteAllText(fake, "this is not an mp3");
        Dialog(new SoundEditorWindow(c, null, fake) { Owner = main }, w =>
        {
            Check(w.StatusText.Visibility == Visibility.Visible && w.StatusText.Text == L.T("We couldn't read that sound file. It may be damaged."),
                "a fake .mp3 shows a friendly error");
            Check(!w.SaveButton.IsEnabled, "nothing can be saved from a fake .mp3");
            SnapshotWindow(w, Png("sound_editor_error"));
        }, wait: 1200);
        Check(c.Settings.Sounds.Count == sounds, "a fake .mp3 adds nothing");

        // ---- a sound whose file won't open is marked on its card ----
        var missing = new SoundItem { Id = "smoke-missing", Name = "Missing file", FileName = "missing.wav" };
        c.Settings.Sounds.Add(missing);
        var brokenCopy = c.DuplicateSound(missing.Id);
        await Task.Delay(800);
        Check(c.IsSoundBroken(brokenCopy.Id), "a sound with a missing file is marked broken");
        main.Tabs.SelectedIndex = 1;
        main.UpdateLayout();
        Snapshot(main, Png("sounds_broken"));
        c.DeleteSound(brokenCopy.Id);
        c.DeleteSound(missing.Id);
        main.Tabs.SelectedIndex = 0;

        // ---- hotkey dialog: a plain key warns, a used key can't be saved ----
        var other = c.Settings.Voices.First(v => v.Id == "robot");
        other.Hotkey = "Ctrl+9";
        var dialog = HotkeyCaptureDialog.ForTest(c, "voice:monster", "Monster", null);
        dialog.Owner = main;
        dialog.Show();
        dialog.Simulate(HotkeyMods.None, 0x37); // 7
        dialog.UpdateLayout();
        SnapshotWindow(dialog, Png("hotkey_typing"));
        Check(dialog.SaveButton.IsEnabled && dialog.TypingNote.Visibility == Visibility.Visible, "a plain key warns about typing but can be saved");
        dialog.Simulate(HotkeyMods.Ctrl, 0x39); // Ctrl+9, used by Robot
        dialog.UpdateLayout();
        SnapshotWindow(dialog, Png("hotkey_used"));
        Check(!dialog.SaveButton.IsEnabled && dialog.ProblemText.Text == L.F("Already used by {0}", L.T("Robot")),
            "a used key says who has it and can't be saved");
        dialog.Close();
        other.Hotkey = null;
        c.Save();
    }

    /// <summary>
    /// Shows a dialog window, runs <paramref name="whileOpen"/> once it has drawn, then closes it if it is still open.
    /// <c>ShowDialog</c> runs its own message loop, so the step runs inside it and the call returns when the window is gone.
    /// </summary>
    private void Dialog<T>(T window, Action<T> whileOpen, int wait = 600) where T : Window
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(wait);
                window.UpdateLayout();
                whileOpen(window);
            }
            catch (Exception ex)
            {
                AppLog.Error("Smoke test dialog step failed", ex);
                smokeFailures++;
            }
            finally
            {
                if (window.IsVisible) window.Close();
            }
        };
        window.ShowDialog();
    }

    private static void Snapshot(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + 32), (int)Math.Ceiling(content.ActualHeight + 26),
            96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, bitmap.Width, bitmap.Height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(16, 14, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        Save(bitmap, path);
    }

    /// <summary>A dialog window's client area as it is, margins included, on its background.</summary>
    private static void SnapshotWindow(Window w, string path)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0); // the window's own chrome-free root: the whole client area
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Render(background);
        bitmap.Render(root);
        Save(bitmap, path);
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
