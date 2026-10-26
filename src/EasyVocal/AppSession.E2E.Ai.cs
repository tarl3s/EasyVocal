using System.IO;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Model;
using EasyVocal.E2E;

namespace EasyVocal;

/// <summary>
/// The e2e run's AI voices on the tiny test models or a real AI pack (only when one is given: the headless tests pass
/// the tiny ones, CI's packaged runs pass the release's pack with --e2e-ai-pack; a packaged run with neither skips this
/// step): the AI pack downloaded from a local server, checked and installed, the PC tested, an AI voice
/// picked (the previous voice plays while it loads, then the call gets the converted voice, never the mic as it is),
/// the quality changed, voices added from a .pth file and from a link, renamed, deleted, and AI mode removed.
/// </summary>
internal sealed partial class AppSession
{
    private async Task E2EAiVoices(AppController c, MainWindow main, double normalShare)
    {
        await Step("13 AI voices: download + install + PC test, an AI voice on the call (the previous voice while it loads), quality, import by file and by link, edit, delete, remove AI mode", async () =>
        {
            if (e2ePack == null || c.Ai is not { } ai)
            {
                Note("SKIPPED: no AI test models given (--e2e-ai <tests/EasyVocal.Ai.Tests/Fixtures> or --e2e-ai-pack <EasyVocal-AI-*.zip>)");
                return;
            }
            int slow = e2ePack.Real ? 10 : 1; // the real models: a bigger download, a longer PC test, slower loads
            Note(e2ePack.Real ? $"the real AI pack {e2ePack.AssetName} ({e2ePack.PackBytes / 1048576} MB)" : "the tiny AI test pack");
            await Baseline(c, main);
            Expect(main.AiSection.IsVisible && !ai.PackInstalled && main.DownloadAiButton is { } dl && Ui.CanClick(dl), "without the AI pack the Voices tab offers to download it");
            ClickVoice(main, "robot"); // a classic voice plays while the AI voice loads later

            // ---- the pack: found on the (local) release, downloaded, checked, installed; the PC tested once ----
            Click(main.DownloadAiButton!);
            await WaitUntil(() => ai.PackInstalled && !main.AiTesting && main.VoiceCard(e2ePack.VoiceA) != null, 90000 * slow, "the AI pack to download, install and the PC test to finish");
            Expect(c.Settings.Ai.BenchmarkedUtc != null && c.Settings.Ai.SuggestedQuality != null, "the PC test saved its suggestion (" + c.Settings.Ai.SuggestedQuality + ")");
            Expect(!Directory.Exists(Path.Combine(e2eData, "AI.download")), "the download's temporary files are gone");

            // ---- an AI voice: the previous one plays while it loads, then the call gets the converted voice ----
            ClickVoice(main, e2ePack.VoiceA);
            Expect(c.ActiveVoice.Id == e2ePack.VoiceA, "the AI card is picked");
            if (c.AiState == AiVoiceState.Loading)
                Expect(c.PlayingVoiceId == "robot" && main.SpinnerShown(e2ePack.VoiceA), "while it loads, its card spins and Robot still plays");
            await WaitUntil(() => c.AiState == AiVoiceState.Ready, 60000 * slow, "the AI voice to load and play");
            Expect(c.PlayingVoiceId == e2ePack.VoiceA && main.AiReadout.IsVisible, "it plays, and the top bar shows its delay (" + main.AiReadoutText.Text + ")");
            var (got, sent) = await Record(Call, seconds: 0.5, settleMs: 700);
            double corr = Math.Abs(E2ESignal.Correlation(got, sent)), share = E2ESignal.HarmonicShare(got);
            Note(Inv($"AI voice on the call: RMS {E2ESignal.Rms(got):0.000}, correlation with the mic {corr:0.00}, on the vowel's harmonics {share:P0} (Normal {normalShare:P0}); delay {c.VoiceLatencyMs} ms"));
            Expect(E2ESignal.Rms(got) > 0.001, "the call gets the AI voice (not silence)");
            Expect(corr < 0.5 && share < 0.8 * normalShare, "the call doesn't get the mic as it is");

            // ---- the quality: the voice reloads while it keeps playing ----
            await Modal(w => w is SettingsWindow, () => Click(main.SettingsButton), async w =>
            {
                var s = (SettingsWindow)w;
                Expect(s.AiPanel.IsVisible && s.AiControls.IsVisible, "Settings has the AI section");
                s.AiQualityBox.SelectedItem = s.AiQualityBox.Items.OfType<Avalonia.Controls.ComboBoxItem>().First(i => (string?)i.Tag == "light");
                Expect(c.Settings.Ai.Quality == "light", "Light is saved");
                Expect(c.PlayingVoiceId == e2ePack.VoiceA, "the AI voice keeps playing while the new level loads");
                await WaitUntil(() => c.AiState == AiVoiceState.Ready, 60000 * slow, "the AI voice at Light");
            }, "the settings window");

            // ---- import: a .pth file, then a link (no extension: told by its bytes) ----
            string? fromFile = null, fromLink = null;
            await Modal(w => w is AddAiVoiceWindow, () => Click(main.AddAiVoiceCard!), async w =>
            {
                var add = (AddAiVoiceWindow)w;
                add.UseFiles(new[] { e2ePack.VoicePth });
                add.NameBox.Text = "E2E file voice";
                await add.AddAsync();
                fromFile = add.AddedId;
            }, "Add AI voice (file)");
            Expect(fromFile != null && main.VoiceCard(fromFile!) != null, "a voice from a .pth file gets a card");
            await Modal(w => w is AddAiVoiceWindow, () => Click(main.AddAiVoiceCard!), async w =>
            {
                var add = (AddAiVoiceWindow)w;
                add.UseLink(e2ePack.VoiceLink);
                add.NameBox.Text = "E2E link voice";
                await add.AddAsync();
                fromLink = add.AddedId;
            }, "Add AI voice (link)");
            Expect(fromLink != null && main.VoiceCard(fromLink!) != null, "a voice from a link gets a card");
            ClickVoice(main, fromFile!);
            await WaitUntil(() => c.AiState == AiVoiceState.Ready && c.PlayingVoiceId == fromFile, 60000 * slow, "the imported voice to play");

            // ---- edit and delete ----
            var linked = c.Settings.Voices.First(v => v.Id == fromLink);
            await Modal(w => w is AiVoiceWindow, () => FireAndForget.Watch(new AiVoiceWindow(c, linked).ShowAsync(main), "Editing"), w =>
            {
                var edit = (AiVoiceWindow)w;
                edit.NameBox!.Text = "E2E renamed";
                edit.Save();
                return Task.CompletedTask;
            }, "the AI voice editor");
            Expect(linked.Name == "E2E renamed" && main.VoiceCard(fromLink!) != null, "an AI voice can be renamed");
            string folder = Path.Combine(e2eData, "AI", "voices", linked.AiVoiceId!);
            Expect(Directory.Exists(folder), "the imported voice's files are in the pack's voices");
            c.DeleteVoice(fromLink!);
            Expect(main.VoiceCard(fromLink!) == null && !Directory.Exists(folder), "deleting an imported voice removes its card and its files");

            // ---- remove AI mode: Normal, the pack gone, the voice added from a file kept ----
            await c.RemoveAiPackAsync(alsoImported: false);
            await Settle(main);
            Expect(!ai.PackInstalled && c.ActiveVoice.Id == AppController.NormalVoiceId, "Remove AI mode: back to Normal and the pack is gone");
            Expect(main.DownloadAiButton is { } again && Ui.CanClick(again), "the download card is back");
            Expect(c.Settings.Voices.Any(v => v.Id == fromFile) && Directory.Exists(Path.Combine(e2eData, "AI", "voices", fromFile!)), "the voices the person added are kept");
            await ExpectCopy(Call, "Normal again: the call gets the clean voice");
            c.SetAiQuality("auto");
        });
    }
}
