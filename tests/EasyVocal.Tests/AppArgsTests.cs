using System.IO;

namespace EasyVocal.Tests;

public class AppArgsTests
{
    [Fact]
    public void SmokeTestWithoutData_IsRefused()
    {
        var args = AppArgs.Parse(new[] { "--smoketest" });
        Assert.True(args.SmokeTest);
        Assert.Null(args.DataFolder);
        Assert.False(args.Allowed); // it would add and remove items in the person's real settings
    }

    [Fact]
    public void SmokeTestWithData_IsAllowed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EV_args");
        var args = AppArgs.Parse(new[] { "--data", folder, "--smoketest" });
        Assert.True(args.Allowed);
        Assert.Equal(Path.GetFullPath(folder), args.DataFolder);
    }

    [Fact]
    public void NormalStart_IsAllowed()
    {
        Assert.True(AppArgs.Parse(new string[0]).Allowed);
        var tray = AppArgs.Parse(new[] { "--tray" });
        Assert.True(tray.Allowed);
        Assert.True(tray.StartHidden);
        Assert.False(tray.SmokeTest);
    }

    [Fact]
    public void E2EWithoutData_IsRefused()
    {
        var args = AppArgs.Parse(new[] { "--e2e" });
        Assert.True(args.E2E);
        Assert.True(args.TestMode);
        Assert.False(args.Allowed);
        Assert.False(AppArgs.Parse(new[] { "--e2e-relaunch" }).Allowed);
    }

    [Fact]
    public void E2EWithData_IsAllowed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EV_args_e2e");
        var args = AppArgs.Parse(new[] { "--data", folder, "--e2e" });
        Assert.True(args.Allowed);
        Assert.False(args.E2ERelaunch);
        Assert.False(args.SmokeTest);
        var relaunch = AppArgs.Parse(new[] { "--data", folder, "--e2e-relaunch" });
        Assert.True(relaunch.E2E && relaunch.E2ERelaunch && relaunch.Allowed);
    }

    [Fact]
    public void DataWithoutAFolder_IsIgnored()
    {
        var args = AppArgs.Parse(new[] { "--smoketest", "--data" });
        Assert.Null(args.DataFolder);
        Assert.False(args.Allowed);
    }

    [Fact]
    public void UpdateFlags_AreReadAndAreNotTests()
    {
        var none = AppArgs.Parse(new[] { "--tray" });
        Assert.False(none.UpdateCheck || none.UpdateNow);
        var check = AppArgs.Parse(new[] { "--update-check" });
        Assert.True(check.UpdateCheck && !check.UpdateNow && !check.TestMode && check.Allowed);
        var now = AppArgs.Parse(new[] { "--data", "x", "--update-now" });
        Assert.True(now.UpdateNow && !now.UpdateCheck && !now.TestMode && now.Allowed);
    }

    [Fact]
    public void E2E_AI_test_models_are_the_tiny_fixtures_or_a_real_pack()
    {
        var none = AppArgs.Parse(new[] { "--data", "x", "--e2e" });
        Assert.Null(none.AiFixtures);
        Assert.Null(none.AiPackZip);
        var real = AppArgs.Parse(new[] { "--data", "x", "--e2e", "--e2e-ai-pack", "EasyVocal-AI-win-x64.zip" });
        Assert.Equal(Path.GetFullPath("EasyVocal-AI-win-x64.zip"), real.AiPackZip);
        Assert.Null(real.AiFixtures);
        var tiny = AppArgs.Parse(new[] { "--data", "x", "--e2e", "--e2e-ai", "Fixtures" });
        Assert.Equal(Path.GetFullPath("Fixtures"), tiny.AiFixtures);
        Assert.Null(AppArgs.Parse(new[] { "--e2e-ai-pack" }).AiPackZip); // no file given
    }
}
