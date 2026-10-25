using EasyVocal.Core.Engine;
using EasyVocal.Platform.Linux;
using EasyVocal.Platform.Unix;
using Xunit.Abstractions;

namespace EasyVocal.Tests.Platforms;

/// <summary>Linux' sound through pactl: what it prints, the devices EasyVocal makes, all with a pretend pactl.</summary>
public class PactlTests(ITestOutputHelper output)
{
    // `LC_ALL=C pactl list sinks` on PipeWire (trimmed); properties are indented one more tab.
    private const string Sinks = "Sink #47\n\tState: SUSPENDED\n\tName: alsa_output.pci-0000_00_1f.3.analog-stereo\n" +
        "\tDescription: Built-in Audio Analog Stereo\n\tDriver: PipeWire\n\tProperties:\n\t\tnode.name = \"x\"\n\t\tdevice.description = \"Other\"\n" +
        "\nSink #112\n\tState: RUNNING\n\tName: easyvocal_out\n\tDescription: EasyVocal-Output\n\tDriver: PipeWire\n";

    private const string Sources = "Source #48\n\tState: SUSPENDED\n\tName: alsa_output.pci-0000_00_1f.3.analog-stereo.monitor\n" +
        "\tDescription: Monitor of Built-in Audio Analog Stereo\n\tMonitor of Sink: alsa_output.pci-0000_00_1f.3.analog-stereo\n" +
        "\nSource #49\n\tState: SUSPENDED\n\tName: alsa_input.usb-Blue_Yeti-00.analog-stereo\n\tDescription: Yeti Stereo Microphone Analog Stereo\n\tMonitor of Sink: n/a\n" +
        "\nSource #113\n\tState: RUNNING\n\tName: easyvocal_mic\n\tDescription: EasyVocal-Microphone\n\tMonitor of Sink: n/a\n";

    private const string Info = "Server String: /run/user/1000/pulse/native\nServer Name: PulseAudio (on PipeWire 1.0.5)\n" +
        "Default Sink: alsa_output.pci-0000_00_1f.3.analog-stereo\nDefault Source: alsa_input.usb-Blue_Yeti-00.analog-stereo\n";

    private static (FakeRunner Runner, Pactl Pactl) Fake(bool installed = true)
    {
        var runner = new FakeRunner();
        if (installed) runner.Installed["pactl"] = "/usr/bin/pactl";
        runner.Answers["info"] = new CommandResult(0, Info, "");
        runner.Answers["list sinks"] = new CommandResult(0, Sinks, "");
        runner.Answers["list sources"] = new CommandResult(0, Sources, "");
        return (runner, new Pactl(runner));
    }

    [Fact]
    public void Parse_DevicesDefaultsAndModules()
    {
        var sinks = Pactl.ParseList(Sinks);
        Assert.Equal(new[] { "alsa_output.pci-0000_00_1f.3.analog-stereo", "easyvocal_out" }, sinks.Select(d => d.Name));
        Assert.Equal("Built-in Audio Analog Stereo", sinks[0].Description); // not the property "device.description"
        var sources = Pactl.ParseList(Sources);
        Assert.Equal(new[] { true, false, false }, sources.Select(d => d.IsMonitor));
        Assert.Equal("alsa_output.pci-0000_00_1f.3.analog-stereo", Pactl.ParseDefault(Info, playback: true));
        Assert.Equal("alsa_input.usb-Blue_Yeti-00.analog-stereo", Pactl.ParseDefault(Info, playback: false));
        Assert.Null(Pactl.ParseDefault("Server Name: x\n", playback: true));

        var modules = Pactl.ParseModules("22\tmodule-null-sink\tsink_name=easyvocal_out sink_properties=device.description=EasyVocal-Output\n" +
            "23\tmodule-remap-source\tmaster=easyvocal_out.monitor source_name=easyvocal_mic\n7\tmodule-x\t\n junk \n");
        Assert.Equal(new[] { 22, 23, 7 }, modules.Select(m => m.Id));
        Assert.True(PactlVirtualMic.IsOurs(modules[0]));
        Assert.True(PactlVirtualMic.IsOurs(modules[1]));
        Assert.False(PactlVirtualMic.IsOurs(modules[2]));
        Assert.False(PactlVirtualMic.IsOurs(new PulseModule(9, "module-null-sink", "sink_name=easyvocal_out_other")));
        Assert.Equal(536870912, Pactl.ParseModuleId("536870912\n"));
        Assert.Null(Pactl.ParseModuleId("Failure: Module initialization failed"));
    }

    [Fact]
    public void Backend_ListsWhatPactlSays_WithoutMonitors()
    {
        var (_, pactl) = Fake();
        var backend = new PulseBackend(pactl, poll: false);
        Assert.Equal(new[] { "alsa_output.pci-0000_00_1f.3.analog-stereo", "easyvocal_out" }, backend.Outputs().Select(d => d.Id));
        Assert.Equal(new[] { "alsa_input.usb-Blue_Yeti-00.analog-stereo", "easyvocal_mic" }, backend.Inputs().Select(d => d.Id));
        Assert.True(backend.Outputs()[0].IsDefault);
        Assert.Equal(new DefaultIds("alsa_output.pci-0000_00_1f.3.analog-stereo", "alsa_output.pci-0000_00_1f.3.analog-stereo"), backend.Defaults(playback: true));
        Assert.False(backend.SetDefault("easyvocal_out")); // never changes the system's defaults
        // The cable rules on that list: the voice to EasyVocal-Output, the real mic and speakers for the rest.
        Assert.Equal("easyvocal_out", new PactlVirtualMic(pactl).Find(backend.Outputs())?.Id);
        Assert.Equal("alsa_input.usb-Blue_Yeti-00.analog-stereo", DeviceRules.DefaultMic(CableNames.EasyVocalLinux, backend.Inputs())?.Id);

        var (silent, gone) = Fake(installed: false);
        Assert.Empty(new PulseBackend(gone, poll: false).Outputs());
        Assert.Empty(silent.Calls);
    }

    [Fact]
    public async Task VirtualMic_IsMadeRemovedAndCleanedByName()
    {
        var (runner, pactl) = Fake();
        int changes = 0;
        var mic = new PactlVirtualMic(pactl, () => changes++);
        runner.Answers[string.Join(" ", PactlVirtualMic.SinkArguments)] = new CommandResult(0, "536870913\n", "");
        runner.Answers[string.Join(" ", PactlVirtualMic.SourceArguments)] = new CommandResult(0, "536870914\n", "");
        runner.Answers["list short modules"] = new CommandResult(0,
            "5\tmodule-null-sink\tsink_name=easyvocal_out sink_properties=device.description=EasyVocal-Output\n" +
            "6\tmodule-remap-source\tmaster=easyvocal_out.monitor source_name=easyvocal_mic source_properties=device.description=EasyVocal-Microphone\n" +
            "7\tmodule-null-sink\tsink_name=someone_else\n", "");

        Assert.True(mic.CanCreate);
        Assert.False(mic.CanInstall);
        Assert.False(mic.TakesDefaultsOnInstall);
        Assert.Equal("EasyVocal-Microphone", mic.CallAppDeviceName);

        await mic.CleanStaleAsync(default);
        Assert.True(await mic.CreateAsync(default));
        Assert.Equal((536870913, 536870914), (mic.Loaded.Sink!.Value, mic.Loaded.Source!.Value));
        await mic.RemoveAsync(default);
        Assert.Equal((null, null), (mic.Loaded.Sink, mic.Loaded.Source));
        await mic.RemoveAsync(default); // twice: nothing more

        Assert.Equal(new[]
        {
            "list short modules",
            "unload-module 6", "unload-module 5", // a crashed run's leftovers, the remap first; someone else's sink is left alone
            "load-module module-null-sink sink_name=easyvocal_out sink_properties=device.description=EasyVocal-Output",
            "load-module module-remap-source master=easyvocal_out.monitor source_name=easyvocal_mic source_properties=device.description=EasyVocal-Microphone",
            "unload-module 536870914", "unload-module 536870913",
        }, runner.Lines());
        Assert.All(runner.Calls, c => Assert.Equal("/usr/bin/pactl", c.Program));
        Assert.Equal(3, changes); // cleaned, made, removed: the device list is read again each time
    }

    [Fact]
    public async Task VirtualMic_HalfMade_IsUndone_AndNoPactlSaysWhatToInstall()
    {
        var (runner, pactl) = Fake();
        runner.Answers[string.Join(" ", PactlVirtualMic.SinkArguments)] = new CommandResult(0, "41\n", "");
        runner.Answers[string.Join(" ", PactlVirtualMic.SourceArguments)] = new CommandResult(1, "", "Failure: Module initialization failed");
        var mic = new PactlVirtualMic(pactl);
        Assert.False(await mic.CreateAsync(default));
        Assert.Equal("unload-module 41", runner.Lines()[^1]);
        Assert.Equal((null, null), (mic.Loaded.Sink, mic.Loaded.Source));

        var (none, missing) = Fake(installed: false);
        var noPactl = new PactlVirtualMic(missing);
        Assert.False(await noPactl.CreateAsync(default));
        await noPactl.CleanStaleAsync(default);
        Assert.Empty(none.Calls);
        Assert.Contains("pipewire-pulse", noPactl.Texts.Explanation);
        Assert.Contains("pulseaudio-utils", noPactl.Texts.Explanation);
        Assert.DoesNotContain("pipewire-pulse", mic.Texts.Explanation); // with pactl: nothing to install
    }

    /// <summary>
    /// A real PulseAudio-protocol server (CI starts one and sets EV_PULSE_TESTS=1; never on a desktop where an EasyVocal
    /// may be running): the devices are made, the voice played into EasyVocal-Output comes out of EasyVocal-Microphone,
    /// and everything is removed again.
    /// </summary>
    [Fact]
    public async Task Real_VirtualMic_CarriesTheVoice()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("EV_PULSE_TESTS") != "1")
        {
            output.WriteLine("SKIPPED: needs Linux with a PulseAudio/PipeWire server and EV_PULSE_TESTS=1");
            return;
        }
        var backend = new PulseBackend(poll: false);
        var mic = new PactlVirtualMic(devicesChanged: backend.Invalidate);
        await mic.CleanStaleAsync(default);
        Assert.True(await mic.CreateAsync(default), "the virtual microphone was made");
        try
        {
            var call = mic.Find(backend.Outputs());
            Assert.NotNull(call);
            Assert.Contains(backend.Inputs(), d => d.Id == PactlVirtualMic.SourceName && d.Name == PactlVirtualMic.SourceDescription);
            Assert.True(CableNames.EasyVocalLinux.IsCable(backend.Inputs().First(d => d.Id == PactlVirtualMic.SourceName).Name));

            using var speaker = backend.OpenSpeaker(call!.Id);
            using var input = backend.OpenMic(PactlVirtualMic.SourceName);
            double energy = 0;
            int blocks = 0;
            input.Samples += (buffer, count) =>
            {
                double sum = 0;
                for (int i = 0; i < count; i++) sum += buffer[i] * buffer[i];
                lock (output) { energy = Math.Max(energy, sum / Math.Max(1, count)); blocks++; }
            };
            speaker.Start();
            input.Start();
            var tone = new float[480];
            for (int round = 0; round < 150; round++) // 1.5 s of a 440 Hz tone
            {
                for (int i = 0; i < tone.Length; i++) tone[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * (round * 480 + i) / 48000f);
                speaker.Write(tone);
                await Task.Delay(10);
            }
            lock (output)
            {
                output.WriteLine($"{blocks} blocks from the virtual microphone, loudest mean square {energy:0.0000}");
                Assert.True(blocks > 20, "the virtual microphone delivered audio");
                Assert.True(energy > 0.01, "the tone played into EasyVocal-Output came out of EasyVocal-Microphone");
            }
        }
        finally
        {
            await mic.RemoveAsync(default);
        }
        backend.Invalidate();
        Assert.DoesNotContain(backend.Outputs(), d => d.Id == PactlVirtualMic.SinkName);
        Assert.DoesNotContain(backend.Inputs(), d => d.Id == PactlVirtualMic.SourceName);
    }
}
