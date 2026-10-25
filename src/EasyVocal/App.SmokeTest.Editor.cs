using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Core.Sounds;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The smoke test of the sound editor ("✂ Edit audio…": every edit, undo, split, export, save, back to original)
/// and of small screens (every window at its smallest size, its Save / Done / Close button still on screen).
/// </summary>
public partial class App
{

    /// <summary>Three seconds that look like speech on the wave: vowel-ish bursts of different loudness with pauses.</summary>
    private static float[] SpeechLike()
    {
        var x = new float[3 * Sr];
        var rng = new Random(7);
        double phase = 0;
        for (int burst = 0; burst < 7; burst++)
        {
            int start = (int)(0.25 * Sr + burst * 0.38 * Sr);
            int len = (int)((0.18 + rng.NextDouble() * 0.15) * Sr);
            double amp = 0.25 + rng.NextDouble() * 0.5;
            double f0 = 120 + rng.NextDouble() * 60;
            for (int i = 0; i < len && start + i < x.Length; i++)
            {
                double env = Math.Sin(Math.PI * i / len);
                phase += 2 * Math.PI * f0 / Sr;
                double v = Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.3 * Math.Sin(3 * phase) + 0.15 * Math.Sin(5 * phase);
                x[start + i] = (float)(amp * env * env * v / 1.6);
            }
        }
        return x;
    }

    /// <summary>A click on an enabled button (a disabled one is a failed check: a person couldn't press it).</summary>
    private void Press(Button b)
    {
        Check(b.IsEnabled, "the button '" + (b.Content as string ?? b.Name) + "' can be pressed");
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Like <see cref="Dialog{T}"/>, with steps that wait for the window's background work.</summary>
    private void DialogAsync<T>(T window, Func<T, Task> whileOpen, int wait = 600) where T : Window
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(wait);
                window.UpdateLayout();
                await whileOpen(window);
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

    private async Task SmokeAudioEditor(MainWindow main, AppController c, string dataFolder, Func<string, string> Png)
    {
        // A sound of the person's own, from a file in the Sounds folder.
        var source = Path.Combine(dataFolder, "smoke speech.wav");
        var speech = SpeechLike();
        SoundWriter.WriteWav(source, speech);
        var fileName = ItemOps.ImportSoundFile(source, c.SoundsFolder);
        var item = new SoundItem { Id = ItemOps.NewId(), Name = "Smoke speech", Emoji = "🗣️", FileName = fileName, Volume = 0.8 };
        c.AddSound(item, SoundLoader.Load(Path.Combine(c.SoundsFolder, fileName)));
        int len = c.SoundSamples(item.Id)!.Length;

        main.Tabs.SelectedIndex = 1;
        main.UpdateLayout();
        var menu = main.SoundCard(item.Id)?.ContextMenu;
        Check(menu != null && menu.Items.OfType<MenuItem>().Any(m => (m.Header as string)?.Contains(L.T("Edit audio…")) == true),
            "a sound card's menu has 'Edit audio…'");
        main.Tabs.SelectedIndex = 0;

        Dialog(new SoundEditorWindow(c, item, null) { Owner = main }, w =>
        {
            Check(w.EditAudioButton.IsEnabled && w.EditAudioButton.IsVisible, "the sound editor offers 'Edit audio…'");
        }, wait: 1000);

        SmokeEditedCopyFails(main, c, dataFolder);

        int soundsBefore = c.Settings.Sounds.Count;
        AudioEditorWindow? editor = null;
        DialogAsync(editor = new AudioEditorWindow(c, item.Name, item.Emoji, item.Volume, c.SoundSamples(item.Id)!, true, null, item.Id) { Owner = main }, async w =>
        {
            SnapshotWindow(w, Png("audio_editor"));
            Check(!w.SaveButton.IsEnabled && !w.UndoButton.IsEnabled && !w.OriginalButton.IsEnabled, "nothing to save or undo before a change");

            w.SelectForTest(Sr / 2, Sr / 2);
            w.UpdateLayout();
            Check(w.CutButton.IsEnabled && w.TrimButton.IsEnabled && w.PlayPartButton.IsEnabled, "a picked part can be cut, kept or played");
            SnapshotWindow(w, Png("audio_editor_selection"));
            Press(w.CutButton);
            await w.IdleAsync();
            Check(w.Current.Length == len - Sr / 2 && w.SaveButton.IsEnabled, "Cut removes the picked part");
            Press(w.UndoButton);
            Check(w.Current.Length == len && !w.SaveButton.IsEnabled, "Undo brings it back");
            Press(w.RedoButton);
            Check(w.Current.Length == len - Sr / 2, "Redo cuts it again");
            Press(w.UndoButton);

            w.SelectForTest(Sr, Sr);
            Press(w.TrimButton);
            await w.IdleAsync();
            Check(w.Current.Length == Sr, "'Keep only this' keeps the picked part");
            Press(w.UndoButton);

            // Every other edit, on the whole sound.
            w.PlaceCursorForTest(0);
            Press(w.SpeedButton); // 1.5×, keeping the pitch
            await w.IdleAsync();
            Check(Math.Abs(w.Current.Length - len / 1.5) < 4, "Speed 1.5× makes it 1.5 times shorter");
            Press(w.UndoButton);
            foreach (var (button, name) in new[]
            {
                (w.HigherButton, "Higher"), (w.LowerButton, "Lower"), (w.LouderButton, "Louder"), (w.QuieterButton, "Quieter"),
                (w.NormalizeButton, "As loud as possible"), (w.FadeInButton, "Fade in"), (w.FadeOutButton, "Fade out"),
                (w.ReverseButton, "Reverse"), (w.EchoLightButton, "Light echo"), (w.EchoStrongButton, "Strong echo"),
                (w.VoiceButton, "Apply voice"), (w.SilenceButton, "Remove silence"),
            })
            {
                int steps = w.History.UndoCount;
                Press(button);
                await w.IdleAsync();
                bool nothingToDo = name == "Remove silence" && w.BusyText.Text == L.T("There is no silence to remove at the start or the end.");
                Check(nothingToDo || (w.History.UndoCount == steps + 1 && w.Current.Length > 0 && w.BusyText.Text.Length == 0), name + " makes one undo step");
            }
            Check(w.Current.Length < len + 3 * Sr, "the edits keep a sensible length");
            w.UpdateLayout();
            SnapshotWindow(w, Png("audio_editor_edited"));

            // Split: two new sounds, the original kept.
            w.PlaceCursorForTest(w.Current.Length / 2);
            w.UpdateLayout();
            Check(w.SplitButton.IsEnabled, "Split is offered with the cursor inside the sound");
            Press(w.SplitButton);
            await w.IdleAsync();
            Check(w.SplitDone && c.Settings.Sounds.Count == soundsBefore + 2
                  && c.Settings.Sounds.Any(s => s.Name == "Smoke speech 1") && c.Settings.Sounds.Any(s => s.Name == "Smoke speech 2"),
                "Split adds 'Smoke speech 1' and 'Smoke speech 2'");

            // Save a copy as WAV (and MP3 when Windows can).
            var wav = Path.Combine(dataFolder, "smoke copy.wav");
            Check(await w.Export(wav) && SoundLoader.Load(wav).Length == w.Current.Length, "Save a copy as WAV");
            if (SoundWriter.Mp3Supported)
            {
                var mp3 = Path.Combine(dataFolder, "smoke copy.mp3");
                Check(await w.Export(mp3) && SoundLoader.Load(mp3).Length > 0, "Save a copy as MP3");
            }

            // Back to original, then one edit to save.
            Press(w.OriginalButton);
            await w.IdleAsync();
            Check(w.History.Current.IsOriginal && !w.SaveButton.IsEnabled, "Back to original gives the sound as it was");
            Press(w.ReverseButton);
            await w.IdleAsync();
            Press(w.SaveButton);
        });
        foreach (var half in c.Settings.Sounds.Where(s => s.Name is "Smoke speech 1" or "Smoke speech 2").ToList()) c.DeleteSound(half.Id);
        Check(c.Settings.Sounds.Count == soundsBefore, "the split sounds can be deleted");

        var result = editor?.Result;
        Check(result is { IsOriginal: false }, "Save gives the edited sound");
        if (result != null) await AudioEditorWindow.SaveResultAsync(main, c, item.Id, result);
        var edited = item.FileName;
        Check(item.OriginalFileName == fileName && edited != fileName && File.Exists(Path.Combine(c.SoundsFolder, edited))
              && File.Exists(Path.Combine(c.SoundsFolder, fileName)), "Save writes an edited copy and keeps the original file");
        Check(c.SoundSamples(item.Id) is { } now && now.Length == len && Math.Abs(now[^Sr] - speech[Sr - 1]) < 0.01,
            "the soundboard plays the edited (reversed) sound");

        // Opened again: it starts from the edited copy, and "Back to original" reads the original file.
        DialogAsync(editor = new AudioEditorWindow(c, item.Name, item.Emoji, item.Volume, c.SoundSamples(item.Id)!, false,
            c.SoundPath(item.OriginalFileName), item.Id) { Owner = main }, async w =>
        {
            Check(w.OriginalButton.IsEnabled, "an edited sound offers 'Back to original'");
            Press(w.OriginalButton);
            await w.IdleAsync();
            Check(w.History.Current.IsOriginal && w.SaveButton.IsEnabled, "Back to original loads the original file");
            Press(w.SaveButton);
        });
        if (editor.Result is { } back) await AudioEditorWindow.SaveResultAsync(main, c, item.Id, back);
        Check(item.FileName == fileName && item.OriginalFileName == null && !File.Exists(Path.Combine(c.SoundsFolder, edited)),
            "Back to original plays the original file again and drops the edited copy");

        // Small: every tool still reachable.
        DialogAsync(new AudioEditorWindow(c, item.Name, item.Emoji, item.Volume, c.SoundSamples(item.Id)!, true, null, item.Id) { Owner = main }, async w =>
        {
            await ShrinkAsync(w);
            SnapshotWindow(w, Png("audio_editor_small"));
            Check(OnScreen(w, w.SaveButton) && OnScreen(w, w.CancelButton) && OnScreen(w, w.PlayButton), "the audio editor at its smallest keeps Play, Save and Cancel on screen");
            Check(Reachable(w, w.ToolsScroll, w.SilenceButton), "the audio editor at its smallest can scroll to every tool");
            Check(w.Wave.ActualHeight >= 100, "the audio editor at its smallest keeps a usable wave");
        });

        c.DeleteSound(item.Id);
        Check(!File.Exists(Path.Combine(c.SoundsFolder, fileName)), "deleting the sound removes its file");
    }

    /// <summary>
    /// A new sound saved with edited audio whose copy can't be written (a folder is in the way): the person is told,
    /// and the sound is added playing its own file, not the edited audio.
    /// </summary>
    private void SmokeEditedCopyFails(MainWindow main, AppController c, string dataFolder)
    {
        var source = Path.Combine(dataFolder, "smoke unsaved.wav");
        var original = SpeechLike();
        SoundWriter.WriteWav(source, original);
        var blocker = Path.Combine(c.SoundsFolder, "smoke unsaved (edited).wav");
        Directory.CreateDirectory(blocker);
        int told = 0;
        DialogAsync(new SoundEditorWindow(c, null, source) { Owner = main }, async w =>
        {
            await w.IdleAsync();
            // Closes the "couldn't save" message as soon as it shows.
            var closer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            closer.Tick += (_, _) =>
            {
                foreach (var open in Application.Current.Windows.OfType<Window>().Where(x => x.Owner == w).ToList())
                {
                    told++;
                    open.Close();
                }
            };
            closer.Start();
            w.NameBox.Text = "Smoke unsaved";
            w.UseEditedAudioForTest(AudioEdit.Gain(original, 8)); // far over full scale: clamped only when saved
            Press(w.SaveButton);
            await w.IdleAsync();
            closer.Stop();
        }, wait: 100);
        var added = c.Settings.Sounds.FirstOrDefault(s => s.Name == "Smoke unsaved");
        var plays = added != null ? c.SoundSamples(added.Id) : null;
        Check(told == 1 && added is { OriginalFileName: null } && added.FileName == "smoke unsaved.wav"
              && plays != null && plays.Length == original.Length && plays.Max(v => Math.Abs(v)) <= 1f,
            "an edited copy that can't be written: the person is told and the sound plays its own file");
        if (added != null) c.DeleteSound(added.Id);
        Directory.Delete(blocker);
        File.Delete(source);
    }

    // ---------- small screens ----------

    private void SmokeSmallWindows(MainWindow main, AppController c, string dataFolder, Func<string, string> Png)
    {
        var monster = c.Settings.Voices.First(v => v.Id == "monster");

        DialogAsync(new FilterEditorWindow(c, monster) { Owner = main }, async w =>
        {
            // A 1366×768 laptop at 125 %: about 580 px of screen height.
            w.SizeToContent = SizeToContent.Manual;
            w.Height = 560;
            await Settle(w);
            var scroll = FindChild<ScrollViewer>(w);
            var last = (FrameworkElement)w.SliderPanel.Children[^1];
            Check(OnScreen(w, w.SaveButton), "the filter editor's Save button is on screen at a laptop's height");
            Check(scroll != null && scroll.ScrollableHeight > 0 && Reachable(w, scroll, last), "the filter editor scrolls to its last slider at a laptop's height");
            SnapshotWindow(w, Png("small_filter_editor_laptop"));
            await ShrinkAsync(w);
            Check(OnScreen(w, w.SaveButton) && OnScreen(w, w.CancelButton), "the filter editor at its smallest keeps Save and Cancel on screen");
            SnapshotWindow(w, Png("small_filter_editor"));
        });

        var source = Path.Combine(dataFolder, "smoke small.wav");
        SoundWriter.WriteWav(source, SpeechLike());
        DialogAsync(new SoundEditorWindow(c, null, source) { Owner = main }, async w =>
        {
            await ShrinkAsync(w);
            Check(OnScreen(w, w.SaveButton) && Reachable(w, w.FormScroll, w.HotkeyButton), "the sound editor at its smallest keeps Save on screen and scrolls to every field");
            SnapshotWindow(w, Png("small_sound_editor"));
        }, wait: 1000);

        DialogAsync(new SettingsWindow(c) { Owner = main }, async w =>
        {
            await ShrinkAsync(w);
            Check(OnScreen(w, w.CloseButton), "settings at their smallest keep Close on screen");
            SnapshotWindow(w, Png("small_settings"));
        });

        bool setupDone = c.Settings.SetupDone;
        DialogAsync(new SetupWindow(c, () => true) { Owner = main }, async w =>
        {
            await ShrinkAsync(w);
            Check(OnScreen(w, w.DoneButton), "setup at its smallest keeps Done on screen");
            SnapshotWindow(w, Png("small_setup"));
        });
        c.Settings.SetupDone = setupDone;

        // The main window at its smallest.
        double width = main.Width, height = main.Height;
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        main.UpdateLayout();
        Snapshot(main, Png("small_main"));
        Check(main.Tabs.ActualHeight > 150 && main.SettingsButton.IsVisible && OnScreen(main, main.SettingsButton), "the main window at its smallest keeps the tabs and Settings");
        int tab = main.Tabs.SelectedIndex;
        main.Tabs.SelectedIndex = 1;
        main.UpdateLayout();
        Snapshot(main, Png("small_main_sounds"));
        double toggleRight = main.SoundsUseVoiceBox.TranslatePoint(new Point(main.SoundsUseVoiceBox.ActualWidth, 0), main).X;
        double hiddenLeft = main.ShowHiddenSounds.TranslatePoint(new Point(0, 0), main).X;
        Check(OnScreen(main, main.StopAllButton) && OnScreen(main, main.ShowHiddenSounds) && toggleRight <= hiddenLeft + 0.5,
            "the Sounds tab's footer fits at the smallest size (Stop all, Sounds use the voice, Show hidden items side by side)");
        main.Tabs.SelectedIndex = tab;
        main.Width = width;
        main.Height = height;

        // Being on screen: a window put far off is brought back inside the working area.
        var area = WindowPlacer.WorkAreas()[0];
        Dialog(new SettingsWindow(c) { Owner = main }, w =>
        {
            w.Left = area.Left + area.Width + 500;
            w.Top = area.Top - 400;
            WindowPlacer.KeepOnScreen(w);
            Check(w.Left + w.ActualWidth <= area.Left + area.Width + 1 && w.Top >= area.Top - 1, "a window off the screen is brought back on it");
            Check(w.ActualHeight <= area.Height + 1 && w.ActualWidth <= area.Width + 1, "no window is bigger than the screen");
        });

        // The smoke test's own sizes must not stick for the next run.
        c.Settings.WindowPlacements.Clear();
        c.Save();
        try { File.Delete(source); } catch (Exception) { /* left in the scratch folder */ }
    }

    /// <summary>The window at its minimum size, laid out.</summary>
    private static async Task ShrinkAsync(Window w)
    {
        w.SizeToContent = SizeToContent.Manual;
        w.Width = w.MinWidth;
        w.Height = w.MinHeight;
        await Settle(w);
    }

    private static async Task Settle(Window w)
    {
        w.UpdateLayout();
        await Task.Delay(150);
        w.UpdateLayout();
    }

    /// <summary>All of the element is inside the window's client area (and shown).</summary>
    private static bool OnScreen(Window w, FrameworkElement e)
    {
        if (!e.IsVisible) return false;
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        var box = e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        return box.Left >= -0.5 && box.Top >= -0.5 && box.Right <= root.ActualWidth + 0.5 && box.Bottom <= root.ActualHeight + 0.5;
    }

    /// <summary>Scrolling <paramref name="scroll"/> brings all of <paramref name="e"/> into its view.</summary>
    private static bool Reachable(Window w, ScrollViewer scroll, FrameworkElement e)
    {
        e.BringIntoView();
        w.UpdateLayout();
        var box = e.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        bool inside = box.Top >= -0.5 && box.Bottom <= scroll.ViewportHeight + 0.5 && box.Left >= -0.5 && box.Right <= scroll.ActualWidth + 0.5;
        scroll.ScrollToTop();
        return inside && e.IsVisible;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
