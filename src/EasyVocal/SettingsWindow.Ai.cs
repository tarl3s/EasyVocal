using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyVocal.Ai;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>Settings' "AI voices": quality, device, what Hear me plays, the PC test, "Remove AI mode…".</summary>
public partial class SettingsWindow
{
    private bool aiTesting;

    /// <summary>For the tests.</summary>
    internal bool AiTestRunning => aiTesting;

    private void LoadAi()
    {
        var ai = controller.Ai;
        AiPanel.IsVisible = ai != null;
        if (ai == null) return;
        bool installed = ai.PackInstalled;
        AiMissingText.IsVisible = !installed;
        AiControls.IsVisible = installed;
        if (!installed) return;
        var s = controller.Settings.Ai;
        string suggested = AiLevels.Parse(s.SuggestedQuality) is AiQuality q ? AiText.QualityName(q) : AiText.QualityName(AiQuality.Balanced);
        Fill(AiQualityBox, s.Quality, ("auto", L.F("Automatic ({0})", suggested)), ("light", L.T("Light — quickest, lightest on the PC")),
            ("balanced", L.T("Balanced")), ("max", L.T("Max — most realistic, heaviest")));
        Fill(AiDeviceBox, s.Device, ("auto", L.T("Automatic")), ("cpu", L.T("Processor (CPU)")), ("gpu", L.T("Graphics card")));
        HearAiBox.IsChecked = controller.HearConvertedVoice;
        TestPcText.Text = TestedText();
        long bytes = ai.PackBytes;
        AiSizeText.Text = bytes > 0 ? L.F("Uses {0} MB", Math.Max(1, bytes / 1048576)) : "";
    }

    private string TestedText()
    {
        var s = controller.Settings.Ai;
        if (s.BenchmarkedUtc == null) return L.T("Not tested yet.");
        var q = AiLevels.Parse(s.SuggestedQuality) ?? AiQuality.Balanced;
        return L.F("Suggested for this PC: {0} ({1})", AiText.QualityName(q), s.SuggestedDevice == "gpu" ? L.T("graphics card") : L.T("processor"));
    }

    private static void Fill(ComboBox box, string current, params (string Value, string Label)[] items)
    {
        box.Items.Clear();
        foreach (var (value, label) in items)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (string.Equals(value, current, StringComparison.OrdinalIgnoreCase)) box.SelectedItem = item;
        }
        box.SelectedItem ??= box.Items[0];
    }

    private void AiQualityBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || AiQualityBox.SelectedItem is not ComboBoxItem { Tag: string q }) return;
        controller.SetAiQuality(q);
    }

    private void AiDeviceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || AiDeviceBox.SelectedItem is not ComboBoxItem { Tag: string d }) return;
        controller.SetAiDevice(d);
    }

    private void HearAiBox_Click(object? sender, RoutedEventArgs e) => controller.HearConvertedVoice = HearAiBox.IsChecked == true;

    private async void TestPcButton_Click(object? sender, RoutedEventArgs e) => await TestPcAsync();

    internal async Task TestPcAsync()
    {
        if (controller.Ai is not { } ai || aiTesting) return;
        aiTesting = true;
        TestPcButton.IsEnabled = false;
        TestPcText.Text = L.T("Testing your PC…");
        try
        {
            var result = await ai.BenchmarkAsync(null, CancellationToken.None);
            if (controller.IsDisposed) return;
            controller.SaveBenchmark(result);
            loading = true;
            try { LoadAi(); }
            finally { loading = false; }
            TestPcText.Text = TestedText();
        }
        catch (Exception ex)
        {
            AppLog.Error("The AI benchmark failed", ex);
            if (!controller.IsDisposed) TestPcText.Text = L.T("Your PC couldn't be tested; Balanced is used.");
        }
        finally
        {
            aiTesting = false;
            TestPcButton.IsEnabled = true;
        }
    }

    private async void RemoveAiButton_Click(object? sender, RoutedEventArgs e)
    {
        var answer = await AskDialog.AskAsync(this,
            L.T("Remove AI mode? The AI voices that came with it are removed and the space is freed. You can download it again any time."),
            L.T("Remove"), L.T("Cancel"), L.T("Also delete the AI voices I added"));
        if (!answer.Yes || controller.IsDisposed) return;
        await RemoveAiAsync(answer.Checked);
    }

    internal async Task RemoveAiAsync(bool alsoImported)
    {
        RemoveAiButton.IsEnabled = false;
        try { await controller.RemoveAiPackAsync(alsoImported); }
        catch (Exception ex)
        {
            AppLog.Error("Removing the AI pack failed", ex);
            if (!controller.IsDisposed) await AskDialog.TellAsync(this, L.T("AI mode couldn't be removed completely. Close EasyVocal and try again."));
        }
        finally
        {
            RemoveAiButton.IsEnabled = true;
        }
        if (controller.IsDisposed) return;
        loading = true;
        try { LoadAi(); }
        finally { loading = false; }
    }

    private void CreditsButton_Click(object? sender, RoutedEventArgs e) => FireAndForget.Watch(new CreditsWindow(controller).ShowAsync(this), "Credits");
}
