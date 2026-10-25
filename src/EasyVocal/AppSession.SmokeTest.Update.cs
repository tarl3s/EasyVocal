using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The smoke test of the updater, with a fake GitHub and a fake helper launcher (nothing goes to the network and
/// nothing is replaced): the update dialog (<c>smoke_update.png</c>, at its smallest, while downloading, in a source
/// build), Cancel, Skip this version, Update now up to the checked program, Settings' Check now, and the tray notice.
/// </summary>
internal sealed partial class AppSession
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

    private static string SmokeReleaseJson(string tag, string asset) =>
        "{ \"tag_name\": \"" + tag + "\", \"html_url\": \"https://github.com/tarl3s/EasyVocal/releases/tag/" + tag + "\"," +
        " \"body\": \"## Highlights\\r\\n- **Updates itself**: EasyVocal tells you when there's a new version\\r\\n" +
        "- A brand-new robot voice 🤖\\r\\n- Sounds load faster\\r\\n\\r\\n## Fixes\\r\\n- The level meter no longer freezes after the PC sleeps\\r\\n" +
        "- Long sound names fit on their cards\\r\\n\\r\\nThanks to everyone who reported a problem!\"," +
        " \"assets\": [{ \"name\": \"" + asset + "\", \"size\": 1000, \"browser_download_url\": \"https://example.com/" + asset + "\" }] }";

    private async Task SmokeUpdates(MainWindow main, AppController c, string dataFolder, Func<string, string> Png)
    {
        // A pretend install and release zip inside the scratch folder: the real EasyVocal is never involved.
        var updater = c.Platform.Updater;
        var assetName = updater.AssetNames(RuntimeInformation.OSArchitecture).FirstOrDefault();
        if (assetName == null || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("Smoke test: this platform has no zip release yet, the updater part is skipped");
            return;
        }
        // Windows' unpack checks the program starts with "MZ"; a pretend platform's with "PROG".
        string magic = updater.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "MZ" : "PROG";
        var folder = Path.Combine(dataFolder, "smoke update");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, updater.ExeName);
        File.WriteAllText(exe, magic + " pretend old EasyVocal");
        if (!updater.CanSelfUpdate(exe))
        {
            // macOS replaces only a marked EasyVocal.app (its own tests cover that): a plain folder is never an install.
            AppLog.Info("Smoke test: this platform updates only a real install, not the pretend folder: the updater part is skipped");
            return;
        }
        var zip = Path.Combine(folder, "release.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var entry = new StreamWriter(archive.CreateEntry(updater.ExeName).Open()))
            entry.Write(magic + " pretend new EasyVocal");

        var current = UpdateRules.RunningVersion(typeof(AppSession).Assembly);
        var source = new SmokeUpdateSource(SmokeReleaseJson("v9.9.0", assetName), zip);
        var launcher = new SmokeLauncher();
        var service = new UpdateService(updater, source, launcher, current, exe, Path.Combine(folder, "work"), AppLog.Info);
        var release = UpdateRules.ParseRelease(source.Json)!;
        var asset = release.Assets[0];
        string? skippedBefore = c.Settings.SkippedUpdateVersion;
        c.Settings.SkippedUpdateVersion = null;

        await Dialog(main, new UpdateWindow(c, service, release, asset, canInstall: true), w =>
        {
            Check(w.Headline.Text == L.F("EasyVocal {0} is available (you have {1})", "9.9.0", current), "the update dialog names both versions");
            Check(w.NotesPanel.Children.Count >= 8, "the update dialog shows the release notes");
            Check(OnScreen(w, w.UpdateButton) && OnScreen(w, w.LaterButton) && OnScreen(w, w.SkipButton) && w.UpdateButton.IsEnabled,
                "the update dialog shows Update now, Later and Skip this version");
            Snapshot(w, Png("update"));
        });

        await DialogAsync(main, new UpdateWindow(c, service, release, asset, canInstall: true), async w =>
        {
            await ShrinkAsync(w);
            Check(OnScreen(w, w.UpdateButton) && OnScreen(w, w.LaterButton) && OnScreen(w, w.SkipButton), "the update dialog at its smallest keeps its buttons on screen");
            Snapshot(w, Png("small_update"));
        });

        await Dialog(main, new UpdateWindow(c, service, release, asset, canInstall: false), w =>
        {
            Check(!w.UpdateButton.IsEnabled && w.StatusText.Text == L.T("This EasyVocal runs from its source code, so it doesn't update itself."),
                "a source build only tells about the update");
            Snapshot(w, Png("update_source_build"));
        });

        // Update now, then Cancel while it downloads: back to the start, nothing left in the work folder.
        source.Gate = new TaskCompletionSource();
        string? stopped = null;
        await DialogAsync(main, new UpdateWindow(c, service, release, asset, canInstall: true), async w =>
        {
            Press(w.UpdateButton);
            await Task.Delay(300);
            await Settle(w);
            Check(w.ProgressTrack.IsVisible && w.LaterButton.Content as string == L.T("Cancel") && !w.UpdateButton.IsEnabled,
                "while downloading the dialog shows the progress and Cancel");
            Snapshot(w, Png("update_downloading"));
            Press(w.LaterButton);
            await Task.Delay(300);
            stopped = w.StatusText.Text;
            Check(w.IsVisible && w.UpdateButton.IsEnabled && w.LaterButton.Content as string == L.T("Later"), "Cancel stops the download and keeps the dialog");
        });
        Check(stopped == L.T("The download was stopped."), "a stopped download says so");
        var work = Path.Combine(folder, "work");
        Check(!Directory.Exists(work) || Directory.GetFileSystemEntries(work).Length == 0, "a stopped download leaves nothing behind");
        source.Gate = null;

        // Update now to the end: the checked program is ready and the dialog closes by itself.
        string? prepared = null;
        await DialogAsync(main, new UpdateWindow(c, service, release, asset, canInstall: true), async w =>
        {
            Press(w.UpdateButton);
            for (int i = 0; i < 50 && w.IsVisible; i++) await Task.Delay(100);
            Check(!w.IsVisible, "the update dialog closes once the download is checked");
            prepared = w.PreparedExe;
        });
        Check(prepared != null && File.ReadAllText(prepared) == magic + " pretend new EasyVocal", "Update now unpacks the new EasyVocal");
        if (prepared != null)
        {
            var script = service.StartInstall(prepared, Environment.ProcessId, Path.Combine(folder, "update.log"), updater.RelaunchArguments(dataFolder));
            Check(launcher.Started.Count == 1 && launcher.Started[0] == script && File.ReadAllText(script).Contains(exe),
                "the helper is written for this program and started (a fake launcher: it never runs)");
            Check(File.ReadAllText(exe) == magic + " pretend old EasyVocal", "nothing is replaced before EasyVocal quits");
        }

        // Skip this version: saved, and not offered again when EasyVocal opens.
        await Dialog(main, new UpdateWindow(c, service, release, asset, canInstall: true), w => Press(w.SkipButton));
        Check(c.Settings.SkippedUpdateVersion == "9.9.0", "Skip this version is saved");
        Check((await service.CheckAsync(c.Settings.SkippedUpdateVersion, manual: false)).Status == UpdateStatus.UpToDate, "a skipped version isn't offered on open");

        // The check on open, while EasyVocal is in the tray: a notice, no dialog.
        string? noticeText = null;
        var hidden = new UpdateCoordinator(c, service, canCheck: true, canInstall: true, forceCheck: false, visibleWindow: () => null,
            notice: (text, _) => noticeText = text, quit: () => Check(false, "the smoke test's updater never quits"),
            logFile: Path.Combine(folder, "update.log"), relaunchArguments: "");
        c.Settings.SkippedUpdateVersion = null;
        await hidden.CheckOnOpenAsync();
        Check(noticeText == L.F("EasyVocal {0} is available — click to update", "9.9.0"), "started hidden, a new version shows a tray notice");
        if (noticeText != null)
        {
            // The notice itself (a window of ours in the screen's corner).
            TrayNotice.Show(noticeText, () => { }, TimeSpan.FromSeconds(5));
            await Task.Delay(400);
            if (TrayNotice.Shown is { } notice) Snapshot(notice, Png("tray_notice"));
            TrayNotice.CloseAll();
        }
        bool checkBefore = c.Settings.CheckForUpdates;
        c.Settings.CheckForUpdates = false;
        int checks = source.Checks;
        await hidden.CheckOnOpenAsync();
        Check(source.Checks == checks, "'Check for updates when EasyVocal opens' off: no check");
        c.Settings.CheckForUpdates = checkBefore;

        // Settings: the switch and Check now ("You have the latest version" against a fake latest = this version).
        source.Json = SmokeReleaseJson("v" + current, assetName);
        c.Updates = hidden;
        await DialogAsync(main, new SettingsWindow(c), async w =>
        {
            Check(w.UpdateBox.IsChecked == c.Settings.CheckForUpdates && w.CheckNowButton.IsVisible, "settings shows the update switch and Check now");
            Ui.Click(w.UpdateBox);
            Check(!c.Settings.CheckForUpdates, "'Check for updates when EasyVocal opens' is saved");
            Ui.Click(w.UpdateBox);
            Press(w.CheckNowButton);
            for (int i = 0; i < 30 && w.UpdateStatus.Text != L.T("You have the latest version"); i++) await Task.Delay(100);
            Check(w.UpdateStatus.Text == L.T("You have the latest version"), "Check now says when this is the latest version");
            w.UpdateStatus.BringIntoView();
            await Settle(w);
            Snapshot(w, Png("settings_updates"));
        });
        c.Updates = null;
        c.Settings.SkippedUpdateVersion = skippedBefore;
        c.Save();
        try { Directory.Delete(folder, recursive: true); } catch (Exception) { /* left in the scratch folder */ }
    }
}
