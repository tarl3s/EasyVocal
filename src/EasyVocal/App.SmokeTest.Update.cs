using System.IO;
using System.IO.Compression;
using System.Windows;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The smoke test of the updater, with a fake GitHub and a fake helper launcher (nothing goes to the network and
/// nothing is replaced): the update dialog (<c>smoke_update.png</c>, at its smallest, while downloading, in a source
/// build), Cancel, Skip this version, Update now up to the checked exe, Settings' Check now, and the tray balloon.
/// </summary>
public partial class App
{
    private sealed class SmokeUpdateSource(string json, string zip) : IUpdateSource
    {
        public string Json = json;
        public TaskCompletionSource? Gate;
        public int Checks;

        public Task<string> GetLatestReleaseJsonAsync(CancellationToken ct)
        {
            Checks++;
            return Task.FromResult(Json);
        }

        public async Task DownloadAsync(string url, string path, long maxBytes, IProgress<double>? progress, CancellationToken ct)
        {
            progress?.Report(0.42);
            if (Gate != null) await Gate.Task.WaitAsync(ct);
            File.Copy(zip, path, overwrite: true);
            progress?.Report(1);
        }
    }

    private sealed class SmokeLauncher : IHelperLauncher
    {
        public readonly List<string> Started = new();
        public void Start(string scriptPath) => Started.Add(scriptPath);
    }

    private static string SmokeReleaseJson(string tag) =>
        "{ \"tag_name\": \"" + tag + "\", \"html_url\": \"https://github.com/tarl3s/EasyVocal/releases/tag/" + tag + "\"," +
        " \"body\": \"## Highlights\\r\\n- **Updates itself**: EasyVocal tells you when there's a new version\\r\\n" +
        "- A brand-new robot voice 🤖\\r\\n- Sounds load faster\\r\\n\\r\\n## Fixes\\r\\n- The level meter no longer freezes after the PC sleeps\\r\\n" +
        "- Long sound names fit on their cards\\r\\n\\r\\nThanks to everyone who reported a problem!\"," +
        " \"assets\": [{ \"name\": \"EasyVocal-win-x64.zip\", \"size\": 1000, \"browser_download_url\": \"https://example.com/EasyVocal-win-x64.zip\" }] }";

    private async Task SmokeUpdates(MainWindow main, AppController c, string dataFolder, Func<string, string> Png)
    {
        // A pretend install and release zip inside the scratch folder: the real EasyVocal.exe is never involved.
        var folder = Path.Combine(dataFolder, "smoke update");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "EasyVocal.exe");
        File.WriteAllText(exe, "MZ pretend old EasyVocal");
        var zip = Path.Combine(folder, "release.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var entry = new StreamWriter(archive.CreateEntry("EasyVocal.exe").Open()))
            entry.Write("MZ pretend new EasyVocal");

        var current = UpdateRules.RunningVersion(typeof(App).Assembly);
        var source = new SmokeUpdateSource(SmokeReleaseJson("v9.9.0"), zip);
        var launcher = new SmokeLauncher();
        var service = new UpdateService(source, launcher, current, exe, Path.Combine(folder, "work"), AppLog.Info);
        var release = UpdateRules.ParseRelease(source.Json)!;
        var asset = release.Assets[0];
        string? skippedBefore = c.Settings.SkippedUpdateVersion;
        c.Settings.SkippedUpdateVersion = null;

        Dialog(new UpdateWindow(c, service, release, asset, canInstall: true) { Owner = main }, w =>
        {
            Check(w.Headline.Text == L.F("EasyVocal {0} is available (you have {1})", "9.9.0", current), "the update dialog names both versions");
            Check(w.NotesPanel.Children.Count >= 8, "the update dialog shows the release notes");
            Check(OnScreen(w, w.UpdateButton) && OnScreen(w, w.LaterButton) && OnScreen(w, w.SkipButton) && w.UpdateButton.IsEnabled,
                "the update dialog shows Update now, Later and Skip this version");
            SnapshotWindow(w, Png("update"));
        });

        DialogAsync(new UpdateWindow(c, service, release, asset, canInstall: true) { Owner = main }, async w =>
        {
            await ShrinkAsync(w);
            Check(OnScreen(w, w.UpdateButton) && OnScreen(w, w.LaterButton) && OnScreen(w, w.SkipButton), "the update dialog at its smallest keeps its buttons on screen");
            SnapshotWindow(w, Png("small_update"));
        });

        Dialog(new UpdateWindow(c, service, release, asset, canInstall: false) { Owner = main }, w =>
        {
            Check(!w.UpdateButton.IsEnabled && w.StatusText.Text == L.T("This EasyVocal runs from its source code, so it doesn't update itself."),
                "a source build only tells about the update");
            SnapshotWindow(w, Png("update_source_build"));
        });

        // Update now, then Cancel while it downloads: back to the start, nothing left in the work folder.
        source.Gate = new TaskCompletionSource();
        string? stopped = null;
        DialogAsync(new UpdateWindow(c, service, release, asset, canInstall: true) { Owner = main }, async w =>
        {
            Press(w.UpdateButton);
            await Task.Delay(300);
            w.UpdateLayout();
            Check(w.ProgressTrack.IsVisible && w.LaterButton.Content as string == L.T("Cancel") && !w.UpdateButton.IsEnabled,
                "while downloading the dialog shows the progress and Cancel");
            SnapshotWindow(w, Png("update_downloading"));
            Press(w.LaterButton);
            await Task.Delay(300);
            stopped = w.StatusText.Text;
            Check(w.IsVisible && w.UpdateButton.IsEnabled && w.LaterButton.Content as string == L.T("Later"), "Cancel stops the download and keeps the dialog");
        });
        Check(stopped == L.T("The download was stopped."), "a stopped download says so");
        var work = Path.Combine(folder, "work");
        Check(!Directory.Exists(work) || Directory.GetFileSystemEntries(work).Length == 0, "a stopped download leaves nothing behind");
        source.Gate = null;

        // Update now to the end: the checked exe is ready and the dialog closes by itself.
        string? prepared = null;
        DialogAsync(new UpdateWindow(c, service, release, asset, canInstall: true) { Owner = main }, async w =>
        {
            Press(w.UpdateButton);
            for (int i = 0; i < 50 && w.IsVisible; i++) await Task.Delay(100);
            Check(!w.IsVisible, "the update dialog closes once the download is checked");
            prepared = w.PreparedExe;
        });
        Check(prepared != null && File.ReadAllText(prepared) == "MZ pretend new EasyVocal", "Update now unpacks the new EasyVocal.exe");
        if (prepared != null)
        {
            var script = service.StartInstall(prepared, Environment.ProcessId, Path.Combine(folder, "update.log"), HelperScript.RelaunchArguments(dataFolder));
            Check(launcher.Started.Count == 1 && launcher.Started[0] == script && File.ReadAllText(script).Contains(HelperScript.Quote(exe)),
                "the helper is written for this exe and started (a fake launcher: it never runs)");
            Check(File.ReadAllText(exe) == "MZ pretend old EasyVocal", "nothing is replaced before EasyVocal quits");
        }

        // Skip this version: saved, and not offered again when EasyVocal opens.
        Dialog(new UpdateWindow(c, service, release, asset, canInstall: true) { Owner = main }, w => Press(w.SkipButton));
        Check(c.Settings.SkippedUpdateVersion == "9.9.0", "Skip this version is saved");
        Check((await service.CheckAsync(c.Settings.SkippedUpdateVersion, manual: false)).Status == UpdateStatus.UpToDate, "a skipped version isn't offered on open");

        // The check on open, while EasyVocal is in the tray: a balloon, no dialog.
        string? balloonText = null;
        var hidden = new UpdateCoordinator(c, service, canCheck: true, canInstall: true, forceCheck: false, visibleWindow: () => null,
            balloon: (text, _) => balloonText = text, quit: () => Check(false, "the smoke test's updater never quits"),
            logFile: Path.Combine(folder, "update.log"), relaunchArguments: "");
        c.Settings.SkippedUpdateVersion = null;
        await hidden.CheckOnOpenAsync();
        Check(balloonText == L.F("EasyVocal {0} is available — click to update", "9.9.0"), "started hidden, a new version shows a tray balloon");
        bool checkBefore = c.Settings.CheckForUpdates;
        c.Settings.CheckForUpdates = false;
        int checks = source.Checks;
        await hidden.CheckOnOpenAsync();
        Check(source.Checks == checks, "'Check for updates when EasyVocal opens' off: no check");
        c.Settings.CheckForUpdates = checkBefore;

        // Settings: the switch and Check now ("You have the latest version" against a fake latest = this version).
        source.Json = SmokeReleaseJson("v" + current);
        c.Updates = hidden;
        DialogAsync(new SettingsWindow(c) { Owner = main }, async w =>
        {
            Check(w.UpdateBox.IsChecked == c.Settings.CheckForUpdates && w.CheckNowButton.IsVisible, "settings shows the update switch and Check now");
            w.UpdateBox.IsChecked = false;
            w.UpdateBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(!c.Settings.CheckForUpdates, "'Check for updates when EasyVocal opens' is saved");
            w.UpdateBox.IsChecked = true;
            w.UpdateBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Press(w.CheckNowButton);
            for (int i = 0; i < 30 && w.UpdateStatus.Text != L.T("You have the latest version"); i++) await Task.Delay(100);
            Check(w.UpdateStatus.Text == L.T("You have the latest version"), "Check now says when this is the latest version");
            w.UpdateStatus.BringIntoView();
            w.UpdateLayout();
            SnapshotWindow(w, Png("settings_updates"));
        });
        c.Settings.SkippedUpdateVersion = skippedBefore;
        c.Save();
        try { Directory.Delete(folder, recursive: true); } catch (Exception) { /* left in the scratch folder */ }
    }
}
