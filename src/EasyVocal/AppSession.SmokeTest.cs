using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;
using EasyVocal.Platform.Windows;

namespace EasyVocal;

/// <summary>
/// <c>--smoketest</c>: starts like a normal launch, saves a picture of each tab (<c>smoke_voices.png</c>,
/// <c>smoke_sounds.png</c> in the data folder), opens every editor and dialog, drives them (add a filter, add a
/// sound from a file, a broken file, a hotkey…), saves a picture of each, then quits the normal way and logs
/// "Smoke test passed". Proves the app starts and closes cleanly without a person (use it with <c>--data</c> and a
/// scratch folder; it adds and removes items there). Exit code 1 when a check failed. The headless UI tests run the
/// same steps (<c>tests/EasyVocal.UiTests</c>).
/// </summary>
internal sealed partial class AppSession
{
    private int smokeFailures;

    /// <summary>How many smoke checks failed so far (the headless host reports it).</summary>
    internal int SmokeFailures => smokeFailures;

    private void RunSmokeTest(string dataFolder)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(options.Hosted ? 1 : 3) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (window == null || controller == null) return;
            var main = window;
            var c = controller;
            try
            {
                await SmokeSteps(main, c, dataFolder);
            }
            catch (Exception ex)
            {
                AppLog.Error("Smoke test step failed", ex);
                smokeFailures++;
            }
            AppLog.Info($"Smoke test: language {L.Language}, audio {c.State}, {main.VoiceCount} voices and "
                        + $"{main.SoundCount} sounds on screen");
            if (smokeFailures > 0)
            {
                AppLog.Info($"Smoke test FAILED: {smokeFailures} check(s)");
                Environment.ExitCode = 1;
            }
            else AppLog.Info("Smoke test passed");
            await QuitAsync(ask: false);
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
        await Settle(main);
        Snapshot(main, Png("voices"));
        main.Tabs.SelectedIndex = 1;
        await Settle(main);
        Snapshot(main, Png("sounds"));
        main.SetShowHidden(true);
        await Settle(main);
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
        await Dialog(main, new FilterEditorWindow(c, monster), w =>
        {
            Check(c.HearingMe && w.HearToggle.IsChecked == true, "the filter editor's own Hear me is on and shown");
            Check(c.HearMe == hearBefore, "the filter editor doesn't change the saved Hear me");
            Ui.Click(w.HearToggle);
            Check(!c.HearingMe, "the filter editor's Hear me can be turned off");
            Ui.Click(w.HearToggle);
            Check(w.SaveButton.Content as string == L.T("Save as new filter"), "a built-in voice offers 'Save as new filter'");
            Check(w.Sliders.Count == 9, "the filter editor has its nine sliders");
            Snapshot(w, Png("filter_editor"));
            // As if the name box had the keyboard and never reported losing it: no text box will lose focus on close.
            w.HearToggle.Focus();
            c.SetTyping(true);
        });
        Check(c.HearMe == hearBefore && c.HearingMe == hearBefore, "Hear me is put back after the filter editor");
        await Task.Delay(150);
        Check(!c.IsTyping, "the hotkeys come back after a dialog closes with its text box focused");

        await Dialog(main, new FilterEditorWindow(c, null), w =>
        {
            w.NameBox.Text = "Smoke filter";
            Ui.Click(w.SaveButton);
        });
        var mine = c.Settings.Voices.FirstOrDefault(v => v.Name == "Smoke filter");
        Check(mine is { BuiltIn: false }, "a new filter is added");
        if (mine != null)
        {
            await Dialog(main, new FilterEditorWindow(c, mine), w => Snapshot(w, Png("filter_editor_own")));
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
        await DialogAsync(main, new SoundEditorWindow(c, null, source), async w =>
        {
            await w.IdleAsync(); // the file is decoded in the background
            Check(w.NameBox.Text == "smoke source", "the sound's name starts as the file name");
            Check(w.SaveButton.IsEnabled && w.PreviewButton.IsEnabled, "a loaded sound can be saved and previewed");
            Snapshot(w, Png("sound_editor_new"));
            w.NameBox.Text = "Smoke sound";
            Ui.Click(w.SaveButton);
            await w.IdleAsync();
        }, wait: 100);
        var added = c.Settings.Sounds.FirstOrDefault(s => s.Name == "Smoke sound");
        Check(c.Settings.Sounds.Count == sounds + 1 && added != null && File.Exists(Path.Combine(c.SoundsFolder, added.FileName)),
            "the sound is added and its file copied into the Sounds folder");
        if (added != null)
        {
            var again = c.DuplicateSound(added.Id);
            Check(again.FileName == added.FileName && !again.BuiltIn, "a copy of a sound shares the file");
            await DialogAsync(main, new SoundEditorWindow(c, added, null), async w =>
            {
                await w.IdleAsync();
                Snapshot(w, Png("sound_editor_edit"));
            }, wait: 100);
            c.DeleteSound(again.Id);
            Check(File.Exists(Path.Combine(c.SoundsFolder, added.FileName)), "deleting the copy keeps the shared file");
            c.DeleteSound(added.Id);
            Check(c.Settings.Sounds.Count == sounds && !File.Exists(Path.Combine(c.SoundsFolder, added.FileName)),
                "deleting the last sound using a file removes the file");
        }

        // ---- a file that isn't a sound: friendly error, nothing added ----
        var fake = Path.Combine(dataFolder, "smoke fake.mp3");
        File.WriteAllText(fake, "this is not an mp3");
        await DialogAsync(main, new SoundEditorWindow(c, null, fake), async w =>
        {
            await w.IdleAsync();
            Check(w.StatusText.IsVisible && w.StatusText.Text == L.T("We couldn't read that sound file. It may be damaged."),
                "a fake .mp3 shows a friendly error");
            Check(!w.SaveButton.IsEnabled, "nothing can be saved from a fake .mp3");
            Snapshot(w, Png("sound_editor_error"));
        }, wait: 100);
        Check(c.Settings.Sounds.Count == sounds, "a fake .mp3 adds nothing");

        // ---- a sound whose file won't open is marked on its card ----
        var missing = new SoundItem { Id = "smoke-missing", Name = "Missing file", FileName = "missing.wav" };
        c.Settings.Sounds.Add(missing);
        var brokenCopy = c.DuplicateSound(missing.Id);
        await Task.Delay(800);
        Check(c.IsSoundBroken(brokenCopy.Id), "a sound with a missing file is marked broken");
        main.Tabs.SelectedIndex = 1;
        await Settle(main);
        Snapshot(main, Png("sounds_broken"));
        c.DeleteSound(brokenCopy.Id);
        c.DeleteSound(missing.Id);
        main.Tabs.SelectedIndex = 0;

        // ---- hotkey dialog: a plain key warns, a used key can't be saved ----
        var other = c.Settings.Voices.First(v => v.Id == "robot");
        other.Hotkey = "Ctrl+9";
        var dialog = HotkeyCaptureDialog.ForTest(c, "voice:monster", "Monster", null);
        dialog.Show(main);
        dialog.Simulate(HotkeyMods.None, 0x37); // 7
        await Settle(dialog);
        Snapshot(dialog, Png("hotkey_typing"));
        Check(dialog.SaveButton.IsEnabled && dialog.TypingNote.IsVisible, "a plain key warns about typing but can be saved");
        dialog.Simulate(HotkeyMods.Ctrl, 0x39); // Ctrl+9, used by Robot
        await Settle(dialog);
        Snapshot(dialog, Png("hotkey_used"));
        Check(!dialog.SaveButton.IsEnabled && dialog.ProblemText.Text == L.F("Already used by {0}", L.T("Robot")),
            "a used key says who has it and can't be saved");
        dialog.Close();
        other.Hotkey = null;
        c.Save();

        // ---- the ask dialog ----
        await Dialog(main, null, () => AskDialog.AskAsync(main, L.F("Delete “{0}”? This can't be undone.", "Smoke"), L.T("Delete"), L.T("Cancel"), L.T("Don't ask again")),
            w => Snapshot(w, Png("ask")));

        await SmokePopups(main, c, Png);
        await SmokeUpdates(main, c, dataFolder, Png);
        await SmokeSetupAndSettings(main, c, Png);
        await SmokeAudioEditor(main, c, dataFolder, Png);
        await SmokeSmallWindows(main, c, dataFolder, Png);
    }

    /// <summary>What opens over a window: a card's menu, a list's drop-down, a tooltip (pictures of their popups).</summary>
    private async Task SmokePopups(MainWindow main, AppController c, Func<string, string> Png)
    {
        main.Tabs.SelectedIndex = 0;
        await Settle(main);
        if (main.VoiceCard("robot") is { ContextMenu: { } menu } card)
        {
            menu.Open(card);
            await Settle(main);
            Check(menu.IsOpen && SnapshotPopup(menu, Png("menu")), "a card's menu opens");
            menu.Close();
        }
        await DialogAsync(main, new SettingsWindow(c), async w =>
        {
            w.LanguageBox.IsDropDownOpen = true;
            await Settle(w);
            var popup = w.LanguageBox.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
            Check(popup?.Child != null && SnapshotPopup(popup.Child, Png("dropdown")), "a list's drop-down opens");
            w.LanguageBox.IsDropDownOpen = false;
            var tipped = w.HotkeyButton(HotkeyMap.MuteMic);
            var tip = new TextBlock { Text = L.T("Set hotkey…") }; // as a control, so the popup it lands in can be found
            ToolTip.SetTip(tipped, tip);
            ToolTip.SetIsOpen(tipped, true);
            await Settle(w);
            Check(SnapshotPopup(tip, Png("tooltip")), "a tooltip opens");
            ToolTip.SetIsOpen(tipped, false);
        });
    }

    /// <summary>
    /// What a popup (menu, drop-down, tooltip) is drawn in, saved as a PNG: its own little window, or (headless, where
    /// popups are drawn over their window) its overlay host. False when it isn't shown.
    /// </summary>
    private static bool SnapshotPopup(Visual inPopup, string path)
    {
        var root = inPopup.GetSelfAndVisualAncestors().OfType<IPopupHost>().OfType<Visual>().FirstOrDefault();
        if (root == null) return false;
        var size = root.Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return false;
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
            bitmap.Render(root);
            bitmap.Save(path);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving the snapshot " + path + " failed", ex);
            return false;
        }
    }

    // ---------- first-run setup and settings ----------

    /// <summary>
    /// Where "Start with Windows" goes in a test run: on Windows a throwaway registry key (never the real Run key),
    /// elsewhere only memory.
    /// </summary>
    private static IStartupService SmokeStartup() =>
        OperatingSystem.IsWindows() ? new StartupRegistration(@"Software\EasyVocalTest\Smoke", "EasyVocalSmoke") : new E2E.MemoryStartup();

    private async Task SmokeSetupAndSettings(MainWindow main, AppController c, Func<string, string> Png)
    {
        bool realBefore = OperatingSystem.IsWindows() && StartupRegistration.Default.IsEnabled();
        c.Settings.SetupDone = false;
        c.Settings.StartWithWindows = false;
        try
        {
            // Setup, virtual microphone missing (the probe is faked; Install is never pressed: nothing is downloaded).
            await DialogAsync(main, new SetupWindow(c, () => false), async w =>
            {
                Check(w.CableMissingPanel.IsVisible && w.InstallButton.IsEnabled && !w.CableReadyPanel.IsVisible,
                    "setup offers Install when the virtual microphone is missing");
                Check(w.StartBox.IsChecked == true, "setup starts with 'Start with Windows' ticked on the first run");
                Check(w.GuideTabs.Items.Count == 3, "setup has the Discord, WhatsApp and Other guides");
                Snapshot(w, Png("setup_missing"));
                w.GuideTabs.SelectedIndex = 1;
                await Settle(w);
                Snapshot(w, Png("setup_whatsapp"));
                w.GuideTabs.SelectedIndex = 2;
                await Settle(w);
                Snapshot(w, Png("setup_other"));
                w.ShowRestartMessage();
                await Settle(w);
                Check(w.CableStatus.Text == L.T("Restart your PC to finish installing, then open EasyVocal again."), "setup says to restart when the device never shows up");
                Snapshot(w, Png("setup_restart"));
                w.ShowOfficialPageFallback(open: false);
                await Settle(w);
                Snapshot(w, Png("setup_fallback"));
                w.ShowNotVerifiedFallback(open: false);
                await Settle(w);
                Snapshot(w, Png("setup_not_verified"));
            });
            Check(!c.Settings.SetupDone, "closing setup without Done doesn't finish it");

            // Setup, virtual microphone ready: Done finishes it and writes the (throwaway) startup entry.
            await Dialog(main, new SetupWindow(c, () => true), w =>
            {
                Check(w.CableReadyPanel.IsVisible && !w.CableMissingPanel.IsVisible, "setup shows the tick when the virtual microphone is there");
                Snapshot(w, Png("setup_ready"));
                Ui.Click(w.DoneButton);
            });
            Check(c.Settings.SetupDone && c.Settings.StartWithWindows && c.Startup.IsEnabled(), "Done finishes the setup and turns on 'Start with Windows'");
            if (OperatingSystem.IsWindows() && c.Startup is StartupRegistration smoke)
                Check(smoke.Command.EndsWith(" --tray", StringComparison.Ordinal), "the startup entry starts EasyVocal with --tray");

            // Settings.
            c.Settings.GeneralHotkeys[HotkeyMap.MuteMic] = "Ctrl+F9";
            string? micBefore = c.Settings.MicDeviceId;
            await DialogAsync(main, new SettingsWindow(c), async w =>
            {
                Check(w.StartBox.IsChecked == true, "settings shows 'Start with Windows' on");
                Check(w.HotkeyButton(HotkeyMap.MuteMic).Content as string == "Ctrl+F9" && w.HotkeyButton(HotkeyMap.StopSounds).Content as string == L.T("Not set"),
                    "settings lists the general hotkeys");
                Check(w.LanguageNote.IsVisible, "settings says the language changes on restart");
                Snapshot(w, Png("settings"));

                // A CABLE device as microphone is refused.
                var cable = new ComboBoxItem
                {
                    Content = "CABLE Output (VB-Audio Virtual Cable)",
                    Tag = new DeviceInfo("fake-cable", "CABLE Output (VB-Audio Virtual Cable)", false),
                };
                w.MicBox.Items.Add(cable);
                var before = w.MicBox.SelectedItem;
                w.MicBox.SelectedItem = cable;
                await Settle(w);
                Check(w.MicProblem.IsVisible
                      && w.MicProblem.Text == L.T("That is EasyVocal's own output — using it as the microphone would loop your voice.")
                      && ReferenceEquals(w.MicBox.SelectedItem, before) && c.Settings.MicDeviceId == micBefore,
                    "a CABLE device is refused as the microphone");
                Snapshot(w, Png("settings_refused"));
                w.MicBox.Items.Remove(cable);

                // Switches apply at once.
                w.LanguageBox.SelectedIndex = 2;
                Check(c.Settings.Language == "pt-BR", "picking a language is saved");
                w.LanguageBox.SelectedIndex = 0;
                Check(c.Settings.Language == "auto", "the language can go back to Automatic");
                Ui.Click(w.AskQuitBox);
                Check(!c.Settings.ConfirmQuit, "'Ask before quitting' is saved");
                Ui.Click(w.AskQuitBox);
                Ui.Click(w.StartBox);
                Check(!c.Settings.StartWithWindows && !c.Startup.IsEnabled(), "turning off 'Start with Windows' removes the startup entry");
            });
            c.Settings.GeneralHotkeys.Remove(HotkeyMap.MuteMic);
            c.Save();
        }
        finally
        {
            if (OperatingSystem.IsWindows() && c.Startup is StartupRegistration) RemoveSmokeKey();
        }
        if (OperatingSystem.IsWindows())
            Check(StartupRegistration.Default.IsEnabled() == realBefore, "the real 'Start with Windows' entry was not touched");
    }

    /// <summary>Only the smoke test's own key; the shared parent goes only when nobody else's key is left in it.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveSmokeKey()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\EasyVocalTest\Smoke", throwOnMissingSubKey: false);
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\EasyVocalTest", throwOnMissingSubKey: false); }
            catch (InvalidOperationException) { /* another key is still in it */ }
        }
        catch (Exception ex) { AppLog.Error("Cleaning the smoke test registry key failed", ex); }
    }

    // ---------- driving windows ----------

    /// <summary>
    /// Shows <paramref name="window"/> over <paramref name="owner"/>, runs <paramref name="whileOpen"/> once it has
    /// drawn, then closes it if it is still open; completes when the window is gone.
    /// </summary>
    private Task Dialog<T>(Window owner, T window, Action<T> whileOpen, int wait = 600) where T : AppWindow =>
        DialogAsync(owner, window, w =>
        {
            whileOpen(w);
            return Task.CompletedTask;
        }, wait);

    private async Task DialogAsync<T>(Window owner, T window, Func<T, Task> whileOpen, int wait = 600) where T : AppWindow
    {
        var stepDone = new TaskCompletionSource();
        window.Opened += async (_, _) =>
        {
            try
            {
                await Task.Delay(wait);
                await Settle(window);
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
                stepDone.TrySetResult();
            }
        };
        await window.ShowAsync(owner);
        // The window may close by itself (Save, a finished download) before the step has finished its checks.
        await stepDone.Task;
    }

    /// <summary>A dialog something else opens (<paramref name="open"/>): found among the open windows, driven, closed.</summary>
    private async Task Dialog(Window owner, Func<AppWindow, bool>? match, Func<Task> open, Action<AppWindow> whileOpen)
    {
        var before = AppWindow.OpenWindows;
        var running = open();
        AppWindow? found = null;
        for (int i = 0; i < 100 && found == null; i++)
        {
            await Task.Delay(30);
            found = AppWindow.OpenWindows.FirstOrDefault(w => !before.Contains(w) && !ReferenceEquals(w, owner) && (match?.Invoke(w) ?? true));
        }
        if (found == null)
        {
            Check(false, "the dialog opened");
            return;
        }
        try
        {
            await Task.Delay(300);
            await Settle(found);
            whileOpen(found);
        }
        catch (Exception ex)
        {
            AppLog.Error("Smoke test dialog step failed", ex);
            smokeFailures++;
        }
        finally
        {
            if (found.IsVisible) found.Close();
        }
        await running;
    }

    /// <summary>The window's client area as it is drawn, saved as a PNG (at 96 dpi: one pixel per unit).</summary>
    internal static void Snapshot(Window w, string path)
    {
        try
        {
            var size = w.ClientSize;
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width)), Math.Max(1, (int)Math.Ceiling(size.Height)));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
            bitmap.Render(w);
            bitmap.Save(path);
        }
        catch (Exception ex) { AppLog.Error("Saving the snapshot " + path + " failed", ex); }
    }

    /// <summary>Lets the window lay itself out (and draw) after a change.</summary>
    private static async Task Settle(Window w)
    {
        w.UpdateLayout();
        await Task.Delay(120);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    /// <summary>All of the element is inside the window's client area (and shown).</summary>
    private static bool OnScreen(Window w, Control e)
    {
        if (!e.IsEffectivelyVisible) return false;
        var at = e.TranslatePoint(new Point(0, 0), w);
        if (at == null) return false;
        var box = new Rect(at.Value, e.Bounds.Size);
        return box.Left >= -0.5 && box.Top >= -0.5 && box.Right <= w.ClientSize.Width + 0.5 && box.Bottom <= w.ClientSize.Height + 0.5;
    }

    /// <summary>Scrolling <paramref name="scroll"/> brings all of <paramref name="e"/> into its view.</summary>
    private static async Task<bool> Reachable(Window w, ScrollViewer scroll, Control e)
    {
        e.BringIntoView();
        await Settle(w);
        var at = e.TranslatePoint(new Point(0, 0), scroll);
        bool inside = at is { } p && p.Y >= -0.5 && p.Y + e.Bounds.Height <= scroll.Viewport.Height + 0.5
                      && p.X >= -0.5 && p.X + e.Bounds.Width <= scroll.Bounds.Width + 0.5;
        scroll.Offset = default;
        return inside && e.IsEffectivelyVisible;
    }

    private static T? FindChild<T>(Visual parent) where T : Visual => parent.GetVisualDescendants().OfType<T>().FirstOrDefault();
}
