using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The smoke test's AI voices, on a stand-in (<see cref="StandInAi"/>: no ONNX Runtime, no network): every state is
/// drawn — no pack (the download card), downloading, installed (cards, the PC test), a voice loading (spinner, the
/// previous voice still plays), ready (the delay in the top bar), a failed load (banner), Add AI voice (file, link,
/// an error), Edit, Settings' AI section, the credits, Remove AI mode — also at the smallest window sizes.
/// </summary>
internal sealed partial class AppSession
{
    private static async Task<bool> Until(Func<bool> done, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done())
        {
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
        return true;
    }

    /// <summary>Scrolls the Voices tab down to its AI section.</summary>
    private static async Task ShowAiSection(MainWindow main)
    {
        main.Tabs.SelectedIndex = 0;
        await Settle(main);
        main.AiSection.BringIntoView();
        await Settle(main);
    }

    private async Task SmokeAi(MainWindow main, AppController c, string dataFolder, Func<string, string> Png)
    {
        var ai = smokeAi;
        if (ai == null)
        {
            Check(false, "the smoke test has its stand-in AI voices");
            return;
        }

        // ---- no pack: the download card ----
        await ShowAiSection(main);
        Check(main.AiSection.IsVisible && main.DownloadAiButton is { } dl && Ui.CanClick(dl), "without the AI pack the Voices tab offers to download AI mode");
        Snapshot(main, Png("ai_download"));

        // ---- downloading (held at 42 %), then installed and the PC tested ----
        ai.HoldInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Click(main.DownloadAiButton!);
        Check(await Until(() => main.DownloadAiStatus?.Text == L.F("Downloading… {0} of {1}", L.F("{0} MB", 269), L.F("{0} MB", 640))),
            "the download shows its progress");
        Check(main.DownloadAiCancel is { IsVisible: true }, "a download can be cancelled");
        await Settle(main);
        Snapshot(main, Png("ai_downloading"));
        ai.HoldInstall.SetResult();
        ai.HoldInstall = null;
        Check(await Until(() => c.Ai!.PackInstalled && !main.AiTesting && main.VoiceCard("bright-woman") != null),
            "once installed the bundled AI voices are cards");
        Check(c.Settings.Ai.BenchmarkedUtc != null && c.Settings.Ai.SuggestedQuality == "balanced", "the PC was tested once and its suggestion saved");
        Check(main.AiStatus.IsVisible && main.AiStatus.Text!.Contains(L.T("Balanced"), StringComparison.Ordinal), "the test says what it suggests");
        Check(main.AddAiVoiceCard != null, "there is a ＋ Add AI voice card");
        await ShowAiSection(main);
        Snapshot(main, Png("ai_installed"));

        // ---- loading: a spinner, and the voice before it keeps playing ----
        c.SelectVoice("robot");
        ai.HoldLoads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Click(main.VoiceCard("bright-woman")!);
        await Settle(main);
        Check(c.AiState == AiVoiceState.Loading && main.SpinnerShown("bright-woman"), "a loading AI voice shows a spinner on its card");
        Check(c.PlayingVoiceId == "robot", "while it loads the previous voice keeps playing (never the clean voice)");
        Snapshot(main, Png("ai_loading"));
        ai.HoldLoads.SetResult();
        ai.HoldLoads = null;
        Check(await Until(() => c.AiState == AiVoiceState.Ready), "the AI voice plays once loaded");
        await Settle(main);
        Check(main.AiReadout.IsVisible && main.AiReadoutText.Text == L.F("AI · {0} ms", c.VoiceLatencyMs) && c.VoiceLatencyMs >= 380,
            "the top bar shows how late the AI voice reaches the call");
        Check(!main.SpinnerShown("bright-woman") && c.PlayingVoiceId == "bright-woman", "the spinner goes once it plays");
        Snapshot(main, Png("ai_ready"));

        // ---- a voice that can't load: the card goes back, a banner says why ----
        ai.FailNextLoad = "This is an RVC v1 voice (256-dim content). Only RVC v2 voices are supported for now.";
        Ui.Click(main.VoiceCard("deep-man")!);
        Check(await Until(() => c.AiProblem != null), "a failed AI voice is reported");
        await Settle(main);
        Check(c.ActiveVoice.Id == "bright-woman" && c.PlayingVoiceId == "bright-woman", "after a failed load the voice that plays keeps the card");
        if (main.BannerText.Text?.StartsWith("🧠", StringComparison.Ordinal) == true)
        {
            Check(main.Banner.IsVisible && main.DismissButton.IsVisible && main.BannerText.Text!.Contains(AiText.Friendly(ai.LastFailure!), StringComparison.Ordinal),
                "the banner gives the reason (and can be closed)");
            Snapshot(main, Png("ai_error"));
            Ui.Click(main.DismissButton);
            await Settle(main);
            Check(!main.Banner.IsVisible && c.AiProblem == null, "the banner's ✕ closes it");
        }
        else
        {
            // This computer's audio has its own problem (no virtual cable on a CI runner): that banner comes first.
            AppLog.Info("Smoke: the audio's own banner is shown (" + c.State + "): the AI banner isn't drawn here");
            c.DismissAiProblem();
            Check(c.AiProblem == null, "an AI problem can be dismissed");
        }

        // ---- Add AI voice: a file, an error, a link ----
        var file = Path.Combine(dataFolder, "smoke voice.pth");
        File.WriteAllBytes(file, new byte[16]);
        await DialogAsync(main, new AddAiVoiceWindow(c), async w =>
        {
            Check(w.RightsText.IsVisible && w.RightsText.Text!.Contains(L.T("Only use voices you have the right to use."), StringComparison.Ordinal),
                "Add AI voice says to use only voices you have the right to use");
            Check(!w.AddButton.IsEnabled, "nothing to add before a file or a link is given");
            Snapshot(w, Png("ai_add"));
            w.UseFiles(new[] { file, Path.Combine(dataFolder, "smoke voice.index") });
            ai.FailNextImport = "This voice was trained without pitch. Only RVC voices with pitch are supported for now.";
            await w.AddAsync();
            await Settle(w);
            Check(w.ErrorText.IsVisible && w.IsVisible && w.AddedId == null, "an unusable voice says why and adds nothing");
            Snapshot(w, Png("ai_add_error"));
            w.UseLink("https://huggingface.co/someone/voice/blob/main/voice.pth");
            await Settle(w);
            Snapshot(w, Png("ai_add_link"));
            w.NameBox.Text = "Smoke AI";
            await w.AddAsync();
            Check(w.AddedId != null, "a voice from a link is added");
        }, wait: 100);
        var mine = c.Settings.Voices.FirstOrDefault(v => v.Name == "Smoke AI");
        Check(mine is { Kind: VoiceKind.Ai, BuiltIn: false } && main.VoiceCard(mine.Id) != null, "an added AI voice gets its own card");
        if (mine != null)
        {
            await Dialog(main, new AiVoiceWindow(c, mine), w =>
            {
                Check(w.NameBox != null, "an added AI voice's name can be edited");
                Snapshot(w, Png("ai_edit"));
                w.NameBox!.Text = "Smoke AI 2";
                w.PickQuality("light");
                w.Save();
            });
            Check(mine.Name == "Smoke AI 2" && mine.AiQuality == "light", "Edit saves the name and the voice's own quality");
            var builtIn = c.Settings.Voices.First(v => v.Id == "old-man");
            await Dialog(main, new AiVoiceWindow(c, builtIn), w =>
            {
                Check(w.NameBox == null && w.AboutText.Text!.Contains("CC BY 4.0", StringComparison.Ordinal), "a bundled AI voice shows where it comes from");
                Snapshot(w, Png("ai_edit_builtin"));
            });
        }

        // ---- Settings: the AI section ----
        await DialogAsync(main, new SettingsWindow(c), async w =>
        {
            Check(w.AiPanel.IsVisible && w.AiControls.IsVisible, "Settings has the AI voices section");
            w.AiPanel.BringIntoView();
            await Settle(w);
            Snapshot(w, Png("settings_ai"));
            w.AiQualityBox.SelectedIndex = 3;
            Check(c.Settings.Ai.Quality == "max", "the AI quality is saved");
            w.AiQualityBox.SelectedIndex = 0;
            w.AiDeviceBox.SelectedIndex = 1;
            Check(c.Settings.Ai.Device == "cpu", "the AI device is saved");
            w.AiDeviceBox.SelectedIndex = 0;
            Ui.Click(w.HearAiBox);
            Check(c.HearConvertedVoice, "Hear me can play the AI voice itself");
            Ui.Click(w.HearAiBox);
            await w.TestPcAsync();
            Check(w.TestPcText.Text!.Contains(L.T("Balanced"), StringComparison.Ordinal), "Test my PC says what it suggests");
            await ShrinkAsync(w);
            Check(OnScreen(w, w.CloseButton), "settings with the AI section at their smallest keep Close on screen");
            Snapshot(w, Png("small_settings_ai"));
        });
        await Dialog(main, new CreditsWindow(c), w =>
        {
            Check(w.Body.Text!.Contains("VCTK", StringComparison.Ordinal) && w.Body.Text.Contains("ONNX Runtime", StringComparison.Ordinal), "the credits name the AI voices' sources");
            Snapshot(w, Png("credits"));
        });

        // ---- the smallest main window and Add AI voice ----
        double width = main.Width, height = main.Height;
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        await ShowAiSection(main);
        Snapshot(main, Png("small_main_ai"));
        main.Width = width;
        main.Height = height;
        await DialogAsync(main, new AddAiVoiceWindow(c), async w =>
        {
            await ShrinkAsync(w);
            Check(OnScreen(w, w.AddButton) && OnScreen(w, w.CancelButton), "Add AI voice at its smallest keeps Add and Cancel on screen");
            Snapshot(w, Png("small_ai_add"));
        });

        // ---- Remove AI mode ----
        await c.RemoveAiPackAsync(alsoImported: true);
        await Settle(main);
        Check(!c.Ai!.PackInstalled && main.DownloadAiButton is { } again && Ui.CanClick(again) && main.VoiceCard("bright-woman") == null,
            "after Remove AI mode the download card is back");
        Check(c.ActiveVoice.Id == AppController.NormalVoiceId && c.Settings.Voices.All(v => v.Kind != VoiceKind.Ai || v.BuiltIn),
            "the AI voice in use went back to Normal and the added voices are gone");
        c.SelectVoice(AppController.NormalVoiceId);
        main.Tabs.SelectedIndex = 0;
    }
}
