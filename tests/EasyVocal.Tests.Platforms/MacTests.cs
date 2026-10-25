using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;
using EasyVocal.Platform.MacOS;
using EasyVocal.Platform.Unix;
using Xunit.Abstractions;

namespace EasyVocal.Tests.Platforms;

/// <summary>BlackHole's install with a pretend Homebrew, and macOS' own APIs where the tests run on a Mac.</summary>
public class MacTests(ITestOutputHelper output)
{
    private sealed class NoProgress : IProgress<double>
    {
        public readonly List<double> Values = new();
        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public void BlackHole_IsTheCallDevice()
    {
        var provider = new BlackHoleProvider(new FakeRunner());
        Assert.Equal("BlackHole 2ch", provider.CallAppDeviceName);
        Assert.False(provider.TakesDefaultsOnInstall);
        Assert.False(provider.CanCreate);
        Assert.True(provider.CanInstall);
        Assert.Equal("https://existential.audio/blackhole/", provider.PageUrl);
        var outputs = new[] { new DeviceInfo("mbp", "MacBook Pro Speakers", true), new DeviceInfo("BlackHole2ch_UID", "BlackHole 2ch", false) };
        Assert.Equal("BlackHole2ch_UID", provider.Find(outputs)?.Id);
        Assert.Contains("BlackHole 2ch", provider.Texts.Explanation);
    }

    [Fact]
    public async Task BlackHole_WithoutHomebrew_OpensThePage()
    {
        var runner = new FakeRunner();
        var provider = new BlackHoleProvider(runner);
        var result = await provider.InstallAsync(new NoProgress(), default);
        Assert.Equal(InstallStatus.Failed, result.Status); // the setup then opens the official page and says how
        Assert.Empty(runner.Calls);
        provider.OpenPage();
        Assert.Single(runner.Started);
        Assert.Equal("https://existential.audio/blackhole/", runner.Started[0].Arguments.Single());
    }

    [Fact]
    public async Task BlackHole_WithHomebrew_InstallsInTheBackground_AskingThePasswordInADialog()
    {
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED on Windows: the askpass helper's file permissions are Unix ones");
            return;
        }
        var runner = new FakeRunner();
        runner.Installed["brew"] = "/opt/homebrew/bin/brew";
        string? askpassSeen = null;
        runner.During = call =>
        {
            var path = call.Environment?["SUDO_ASKPASS"];
            if (path != null && File.Exists(path)) askpassSeen = File.ReadAllText(path);
        };
        var progress = new NoProgress();
        var result = await new BlackHoleProvider(runner).InstallAsync(progress, default);
        Assert.Equal(InstallStatus.Installed, result.Status);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("/opt/homebrew/bin/brew", call.Program);
        Assert.Equal(new[] { "install", "blackhole-2ch" }, call.Arguments);
        Assert.Equal("1", call.Environment!["HOMEBREW_NO_AUTO_UPDATE"]);
        Assert.Equal(BlackHoleProvider.AskpassScript, askpassSeen); // the password dialog existed while brew ran…
        Assert.False(File.Exists(call.Environment["SUDO_ASKPASS"])); // …and is gone after
        Assert.Contains(1.0, progress.Values);
    }

    [Fact]
    public void BlackHole_BrewsEndings()
    {
        Assert.Equal(InstallStatus.Installed, BlackHoleProvider.Outcome(new CommandResult(0, "🍺 blackhole-2ch was successfully installed!", ""), false).Status);
        Assert.Equal(InstallStatus.Installed, BlackHoleProvider.Outcome(new CommandResult(1, "", "Warning: Cask 'blackhole-2ch' is already installed."), false).Status);
        Assert.Equal(InstallStatus.DeclinedPermission, BlackHoleProvider.Outcome(new CommandResult(1, "", "sudo: no password was provided"), false).Status);
        Assert.Equal(InstallStatus.DeclinedPermission, BlackHoleProvider.Outcome(new CommandResult(1, "", "execution error: User canceled. (-128)"), false).Status);
        var failed = BlackHoleProvider.Outcome(new CommandResult(1, "", "Error: Download failed\ncurl: (6) Could not resolve host"), false);
        Assert.Equal(InstallStatus.Failed, failed.Status);
        Assert.Contains("Could not resolve host", failed.Detail);
        Assert.Equal(InstallStatus.Cancelled, BlackHoleProvider.Outcome(new CommandResult(-1, "", "cancelled"), true).Status);
        Assert.Contains("osascript", BlackHoleProvider.AskpassScript);
        Assert.Contains("with hidden answer", BlackHoleProvider.AskpassScript);
    }

    [Fact]
    public void FourCharacterCodes()
    {
        Assert.Equal(0x64657623u, CoreAudioDevices.Code("dev#"));
        Assert.Equal(0x676C6F62u, CoreAudioDevices.Code("glob"));
    }

    [Fact]
    public void Real_CoreAudio_ListsDevicesAndThePermission()
    {
        if (!OperatingSystem.IsMacOS())
        {
            output.WriteLine("SKIPPED: needs macOS");
            return;
        }
        var devices = CoreAudioDevices.Instance.Devices();
        output.WriteLine(devices.Count + " CoreAudio device(s): " + string.Join(", ", devices.Select(d => d.Name + (d.Input ? " [in]" : "") + (d.Output ? " [out]" : ""))));
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Id)));
        Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct().Count());
        var defaultOut = CoreAudioDevices.Instance.DefaultId(playback: true);
        if (defaultOut != null) Assert.Contains(devices, d => d.Id == defaultOut && d.Output);
        var status = MicPermission.Current();
        output.WriteLine("Microphone permission: " + status);
        Assert.NotEqual(MicPermission.Status.Unknown, status);

        using var backend = new PortAudioBackend(CoreAudioDevices.Instance, MicPermission.IsRefused);
        Assert.Equal(devices.Count(d => d.Output), backend.Outputs().Count);
        Assert.Null(backend.Problem);
    }
}
