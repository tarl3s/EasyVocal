using EasyVocal.Core.Engine;
using EasyVocal.Core.Platform;
using EasyVocal.Platform.Linux;
using EasyVocal.Platform.Unix;
using Xunit.Abstractions;
using EasyVocal.Tests.Helpers;

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

    private const string Speakers = "alsa_output.pci-0000_00_1f.3.analog-stereo";
    private const string Yeti = "alsa_input.usb-Blue_Yeti-00.analog-stereo";

    private static string Info(string sink = Speakers, string source = Yeti) =>
        "Server String: /run/user/1000/pulse/native\nServer Name: PulseAudio (on PipeWire 1.0.5)\n" +
        "Default Sink: " + sink + "\nDefault Source: " + source + "\n";

    private static (FakeRunner Runner, Pactl Pactl) Fake(bool installed = true)
    {
        var runner = new FakeRunner();
        if (installed) runner.Installed["pactl"] = "/usr/bin/pactl";
        runner.Answers["info"] = new CommandResult(0, Info(), "");
        runner.Answers["list sinks"] = new CommandResult(0, Sinks, "");
        runner.Answers["list sources"] = new CommandResult(0, Sources, "");
        return (runner, new Pactl(runner, json: false));
    }

    [Fact]
    public void Parse_DevicesDefaultsAndModules()
    {
        var sinks = Pactl.ParseList(Sinks);
        Assert.Equal(new[] { Speakers, "easyvocal_out" }, sinks.Select(d => d.Name));
        Assert.Equal("Built-in Audio Analog Stereo", sinks[0].Description); // not the property "device.description"
        var sources = Pactl.ParseList(Sources);
        Assert.Equal(new[] { true, false, false }, sources.Select(d => d.IsMonitor));
        Assert.Equal(Speakers, Pactl.ParseDefault(Info(), playback: true));
        Assert.Equal(Yeti, Pactl.ParseDefault(Info(), playback: false));
        Assert.Null(Pactl.ParseDefault("Server Name: x\n", playback: true));
        Assert.Null(Pactl.ParseDefault(Info("@DEFAULT_SINK@", "@DEFAULT_SOURCE@"), playback: true)); // placeholders aren't devices
        Assert.Null(Pactl.ParseDefault(Info("@DEFAULT_SINK@", "@DEFAULT_SOURCE@"), playback: false));

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
    public void Parse_Json()
    {
        // `pactl -f json list sources` / `info` (PulseAudio 16+, PipeWire), trimmed.
        var sources = Pactl.ParseJsonList("[{\"index\":48,\"name\":\"alsa_output.x.monitor\",\"description\":\"Monitor of Speakers\",\"monitor_of_sink\":\"alsa_output.x\"}," +
            "{\"index\":49,\"name\":\"alsa_input.yeti\",\"description\":\"Yeti\",\"monitor_of_sink\":null},{\"index\":50,\"name\":\"plain\",\"monitor_of_sink\":\"n/a\"}]");
        Assert.NotNull(sources);
        Assert.Equal(new[] { true, false, false }, sources!.Select(d => d.IsMonitor));
        Assert.Equal("plain", sources[2].Description); // no description: the name
        Assert.Equal(new PulseDefaults("alsa_output.x", "alsa_input.yeti"),
            Pactl.ParseJsonInfo("{\"server_name\":\"PulseAudio (on PipeWire 1.0.5)\",\"default_sink_name\":\"alsa_output.x\",\"default_source_name\":\"alsa_input.yeti\"}"));
        Assert.Equal(new PulseDefaults(null, null), Pactl.ParseJsonInfo("{\"default_sink_name\":\"@DEFAULT_SINK@\"}"));
        Assert.Null(Pactl.ParseJsonList("Invalid option -- 'f'"));
        Assert.Null(Pactl.ParseJsonInfo("[]"));
    }

    [Fact]
    public void Json_IsUsedWhenPactlKnowsIt_TextOtherwise()
    {
        var runner = new FakeRunner();
        runner.Installed["pactl"] = "/usr/bin/pactl";
        runner.Answers["-f json info"] = new CommandResult(0, "{\"default_sink_name\":\"s\",\"default_source_name\":\"m\"}", "");
        runner.Answers["-f json list sinks"] = new CommandResult(0, "[{\"name\":\"s\",\"description\":\"Speakers\"}]", "");
        runner.Answers["-f json list sources"] = new CommandResult(0, "[{\"name\":\"m\",\"description\":\"Mic\"}]", "");
        var listing = new Pactl(runner).List();
        Assert.True(listing.Ok);
        Assert.Equal("Speakers", listing.Sinks.Single().Description);
        Assert.DoesNotContain(runner.Lines(), l => l == "list sinks");

        var old = new FakeRunner();
        old.Installed["pactl"] = "/usr/bin/pactl";
        old.Answers["-f json info"] = new CommandResult(1, "", "pactl: invalid option -- 'f'");
        old.Answers["info"] = new CommandResult(0, Info(), "");
        old.Answers["list sinks"] = new CommandResult(0, Sinks, "");
        old.Answers["list sources"] = new CommandResult(0, Sources, "");
        var text = new Pactl(old).List();
        Assert.True(text.Ok);
        Assert.Equal(2, text.Sinks.Count);
        new Pactl(old).List();
        Assert.Equal(2, old.Lines().Count(l => l == "-f json info")); // asked once per Pactl, then text only
    }

    [Fact]
    public void SubscribeLines_ThatMeanTheDevicesChanged()
    {
        Assert.True(Pactl.MattersForDevices("Event 'new' on sink #112"));
        Assert.True(Pactl.MattersForDevices("Event 'remove' on source #49"));
        Assert.True(Pactl.MattersForDevices("Event 'change' on server #4294967295"));
        Assert.False(Pactl.MattersForDevices("Event 'change' on sink #47")); // a volume change
        Assert.False(Pactl.MattersForDevices("Event 'new' on sink-input #300")); // an app started playing
        Assert.False(Pactl.MattersForDevices("Event 'remove' on source-output #301"));
        Assert.False(Pactl.MattersForDevices("garbage"));
    }

    [Fact]
    public void Backend_ListsWhatPactlSays_WithoutMonitors_AndKeepsTheLastGoodLook()
    {
        var (runner, pactl) = Fake();
        var backend = new PulseBackend(pactl, watch: false);
        Assert.Empty(backend.Outputs()); // the UI only reads the snapshot: nothing until a look
        int changes = 0;
        backend.Changed += () => changes++;
        backend.Refresh();
        Assert.Equal(1, changes);
        Assert.Equal(new[] { Speakers, "easyvocal_out" }, backend.Outputs().Select(d => d.Id));
        Assert.Equal(new[] { Yeti, "easyvocal_mic" }, backend.Inputs().Select(d => d.Id));
        Assert.True(backend.Outputs()[0].IsDefault);
        Assert.Equal(new DefaultIds(Speakers, Speakers), backend.Defaults(playback: true));
        Assert.False(backend.SetDefault("easyvocal_out")); // never changes the system's defaults
        Assert.Equal("easyvocal_out", new PactlVirtualMic(pactl).Find(backend.Outputs())?.Id);
        Assert.Equal(Yeti, DeviceRules.DefaultMic(CableNames.EasyVocalLinux, backend.Inputs())?.Id);

        // The server doesn't answer for a moment: the last good snapshot stays (no devices "unplugged").
        runner.Answers["info"] = new CommandResult(1, "", "Connection failure: Connection refused");
        backend.Refresh();
        Assert.Equal(2, backend.Outputs().Count);
        runner.Answers["info"] = new CommandResult(0, Info(), "");
        runner.Answers["list sources"] = new CommandResult(1, "", "Connection failure");
        backend.Refresh();
        Assert.Equal(2, backend.Inputs().Count);
        Assert.Equal(1, changes);

        var (silent, gone) = Fake(installed: false);
        var none = new PulseBackend(gone, watch: false);
        none.Refresh();
        Assert.Empty(none.Outputs());
        Assert.Empty(silent.Calls);
    }

    /// <summary>A stream as the backend watches it.</summary>
    private sealed class FakeStream(string device, bool input) : IPulseStream
    {
        public string Device { get; } = device;
        public bool IsInput { get; } = input;
        public Exception? Failed;
        public void Fail(Exception why) => Failed ??= why;
    }

    [Fact]
    public void Backend_FailsStreamsWhoseDeviceLeft_AndOpensOnlyListedDevices()
    {
        var (runner, pactl) = Fake();
        var backend = new PulseBackend(pactl, watch: false);
        backend.Refresh();
        var call = new FakeStream("easyvocal_out", input: false);
        var mic = new FakeStream(Yeti, input: true);
        backend.Track(call);
        backend.Track(mic);
        backend.Refresh();
        Assert.Null(call.Failed);
        // EasyVocal-Output is removed (pactl unload, a crash of the server's module): its stream must not follow the
        // server to another sink (the voice in the speakers): it fails.
        runner.Answers["list sinks"] = new CommandResult(0, Sinks[..Sinks.IndexOf("\nSink #112", StringComparison.Ordinal)], "");
        backend.Refresh();
        Assert.IsType<IOException>(call.Failed);
        Assert.Null(mic.Failed);

        Assert.Throws<IOException>(() => backend.OpenSpeaker("easyvocal_out")); // not in the list any more
        Assert.Throws<IOException>(() => backend.OpenMic("not.a.source"));
        var own = Assert.Throws<IOException>(() => backend.OpenMic("easyvocal_mic")); // listed, but EasyVocal's own: feedback
        Assert.Contains("loop", own.Message);
        Assert.True(PulseBackend.IsOwnDevice(new DeviceInfo("easyvocal_out.monitor", "Monitor of EasyVocal-Output", false)));
        Assert.False(PulseBackend.IsOwnDevice(new DeviceInfo(Yeti, "Yeti Stereo Microphone Analog Stereo", false)));
    }

    [Fact]
    public async Task Backend_LooksAgainWhenPactlSubscribeSaysSo_AndStartsItAgainWhenItEnds()
    {
        var (runner, pactl) = Fake();
        using var backend = new PulseBackend(pactl, watch: true);
        Assert.True(await backend.FirstLook.WaitAsync(TimeSpan.FromSeconds(5)));
        await Until(() => backend.Outputs().Count == 2 && backend.Subscribed);
        runner.Answers["list sinks"] = new CommandResult(0, Sinks + "\nSink #200\n\tName: usb_headset\n\tDescription: USB Headset\n", "");
        runner.StreamLines.Writer.TryWrite("Event 'change' on sink #47"); // a volume change: nothing to do
        runner.StreamLines.Writer.TryWrite("Event 'new' on sink #200");
        await Until(() => backend.Outputs().Any(d => d.Id == "usb_headset")); // looked again after the event
        Assert.Contains(runner.Lines(), l => l == "subscribe");

        runner.StreamLines.Writer.Complete(); // pactl subscribe ended (the server restarted): it is started again
        await Until(() => runner.StreamsStarted >= 2);
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition never came true");
            await Task.Delay(20);
        }
    }

    private sealed class Stand : IAudioBackend
    {
        public IReadOnlyList<DeviceInfo> Inputs() => new[] { new DeviceInfo("pa:ALSA:mic", "Mic", true) };
        public IReadOnlyList<DeviceInfo> Outputs() => Array.Empty<DeviceInfo>();
        public IAudioInput OpenMic(string id) => throw new IOException();
        public IAudioOutput OpenSpeaker(string id) => throw new IOException();
        public DefaultIds Defaults(bool playback) => new(null, null);
        public bool SetDefault(string id) => false;
        public event Action? Changed { add { } remove { } }
    }

    [Fact]
    public async Task LinuxAudio_SwitchesToPortAudio_WhenNoServerAnswers_AndBackWhenOneDoes()
    {
        var (runner, pactl) = Fake();
        runner.Answers["info"] = new CommandResult(1, "", "Connection failure: Connection refused");
        int made = 0, pulses = 0;
        int changes = 0;
        using var audio = new LinuxAudio(() => { pulses++; return new PulseBackend(pactl, watch: true); },
            () => { made++; return new Stand(); }, pactl, TimeSpan.FromMilliseconds(50));
        audio.Changed += () => Interlocked.Increment(ref changes);
        await Until(() => audio.Inner is Stand);
        Assert.Equal(1, made);
        Assert.Equal("pa:ALSA:mic", audio.Inputs().Single().Id);
        await Task.Delay(200);
        Assert.IsType<Stand>(audio.Inner); // still no server: asked again, PortAudio stays
        Assert.Equal(1, pulses);

        runner.Answers["info"] = new CommandResult(0, Info(), ""); // the server started (or came back)
        await Until(() => audio.Inner is PulseBackend);
        Assert.Equal(2, pulses);
        Assert.Equal(2, audio.Outputs().Count);
        Assert.True(Volatile.Read(ref changes) >= 2, "each switch tells the app to look again");

        // With a server, PulseAudio stays; without pactl or libpulse, PortAudio from the start.
        var (_, good) = Fake();
        using var pulse = new LinuxAudio(() => new PulseBackend(good, watch: false), () => throw new InvalidOperationException("not needed"));
        Assert.IsType<PulseBackend>(pulse.Inner);
        using var direct = new LinuxAudio(null, () => new Stand());
        Assert.IsType<Stand>(direct.Inner);
    }

    /// <summary>
    /// pactl's modules as a server keeps them: load-module adds one under the next number (and prints it), unload-module
    /// removes it, <c>list short modules</c> shows what is loaded. <see cref="OnCall"/> runs first (to cancel, to fail).
    /// </summary>
    private sealed class PretendModules
    {
        public readonly SortedDictionary<int, (string Name, string Arguments)> Loaded = new();
        public Action<FakeRunner.Call>? OnCall;
        /// <summary>The sink's load "runs out of time": the module is made, but its number is never printed.</summary>
        public bool SinkTimesOut;
        public bool SourceFails;
        private int next = 536870913;

        public PretendModules(FakeRunner runner)
        {
            runner.During = call =>
            {
                OnCall?.Invoke(call);
                var a = call.Arguments;
                lock (Loaded)
                {
                    if (a.Length > 1 && a[0] == "load-module")
                    {
                        var key = string.Join(" ", a);
                        if (a[1] == "module-remap-source" && SourceFails)
                            runner.Answers[key] = new CommandResult(1, "", "Failure: Module initialization failed");
                        else
                        {
                            int id = next++;
                            Loaded[id] = (a[1], string.Join(" ", a.Skip(2)));
                            runner.Answers[key] = a[1] == "module-null-sink" && SinkTimesOut
                                ? new CommandResult(-1, "", "timed out: ") : new CommandResult(0, id + "\n", "");
                        }
                    }
                    else if (a.Length == 2 && a[0] == "unload-module") Loaded.Remove(int.Parse(a[1]));
                    runner.Answers["list short modules"] = new CommandResult(0,
                        string.Concat(Loaded.Select(m => m.Key + "\t" + m.Value.Name + "\t" + m.Value.Arguments + "\n")), "");
                }
            };
        }

        public void Add(int id, string name, string arguments)
        {
            lock (Loaded) Loaded[id] = (name, arguments);
        }

        public bool AnyOfOurs()
        {
            lock (Loaded) return Loaded.Any(m => PactlVirtualMic.IsOurs(new PulseModule(m.Key, m.Value.Name, m.Value.Arguments)));
        }
    }

    private const string LoadSink = "load-module module-null-sink sink_name=easyvocal_out sink_properties=device.description=EasyVocal-Output";
    private const string LoadSource = "load-module module-remap-source master=easyvocal_out.monitor source_name=easyvocal_mic source_properties=device.description=EasyVocal-Microphone";

    [Fact]
    public async Task VirtualMic_IsMadeRemovedAndCleanedByName()
    {
        var (runner, pactl) = Fake();
        int changes = 0;
        var mic = new PactlVirtualMic(pactl, () => changes++);
        var server = new PretendModules(runner);
        server.Add(5, "module-null-sink", "sink_name=easyvocal_out sink_properties=device.description=EasyVocal-Output");
        server.Add(6, "module-remap-source", "master=easyvocal_out.monitor source_name=easyvocal_mic source_properties=device.description=EasyVocal-Microphone");
        server.Add(7, "module-null-sink", "sink_name=someone_else");

        Assert.True(mic.CanCreate);
        Assert.False(mic.CanInstall);
        Assert.False(mic.TakesDefaultsOnInstall);
        Assert.Equal("EasyVocal-Microphone", mic.CallAppDeviceName);

        Assert.True(await mic.CreateAsync(default));
        Assert.Equal((536870913, 536870914), (mic.Loaded.Sink!.Value, mic.Loaded.Source!.Value));
        Assert.True(await mic.CreateAsync(default)); // asked again while it is there: nothing changes
        await mic.RemoveAsync(default);
        Assert.Equal((null, null), (mic.Loaded.Sink, mic.Loaded.Source));
        await mic.RemoveAsync(default); // twice: nothing more

        Assert.Equal(new[]
        {
            "list short modules",
            "unload-module 6", "unload-module 5", // a crashed run's leftovers, the remap first; someone else's sink is left alone
            "info", // the defaults before
            LoadSink, LoadSource,
            "info", // and after: unchanged, so nothing is put back
            "list short modules", // the second CreateAsync: both still there
            "unload-module 536870914", "unload-module 536870913", "list short modules",
            "list short modules",
        }, runner.Lines());
        Assert.All(runner.Calls, c => Assert.Equal("/usr/bin/pactl", c.Program));
        Assert.Equal(3, changes); // cleaned, made, removed: the device list is read again each time
        Assert.Equal(new[] { 7 }, server.Loaded.Keys);
    }

    [Fact]
    public async Task VirtualMic_IsMadeAgain_AfterTheServerRestarted()
    {
        var (runner, pactl) = Fake();
        var server = new PretendModules(runner);
        var mic = new PactlVirtualMic(pactl);
        Assert.True(await mic.CreateAsync(default));
        lock (server.Loaded) server.Loaded.Clear(); // systemctl --user restart pipewire: every module is gone
        server.Add(536870913, "module-null-sink", "sink_name=someone_else"); // and the old number is someone else's now
        Assert.True(await mic.CreateAsync(default));
        Assert.Equal((536870915, 536870916), (mic.Loaded.Sink!.Value, mic.Loaded.Source!.Value));
        await mic.RemoveAsync(default);
        Assert.False(server.AnyOfOurs());
        Assert.Contains(536870913, server.Loaded.Keys);
    }

    [Fact]
    public async Task VirtualMic_ThatBecameTheDefault_GivesTheDefaultsBack()
    {
        var (runner, pactl) = Fake();
        _ = new PretendModules(runner);
        runner.InTurn["info"] = new Queue<CommandResult>(new[]
        {
            new CommandResult(0, Info(), ""),                                // before: the person's speakers and mic
            new CommandResult(0, Info("easyvocal_out", "easyvocal_mic"), ""), // after: the system switched to ours
        });
        Assert.True(await new PactlVirtualMic(pactl).CreateAsync(default));
        Assert.Contains("set-default-sink " + Speakers, runner.Lines());
        Assert.Contains("set-default-source " + Yeti, runner.Lines());

        // Already ours before (the person chose it): left alone.
        var (other, mine) = Fake();
        _ = new PretendModules(other);
        other.Answers["info"] = new CommandResult(0, Info("easyvocal_out", Yeti), "");
        await new PactlVirtualMic(mine).CreateAsync(default);
        Assert.DoesNotContain(other.Lines(), l => l.StartsWith("set-default", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VirtualMic_HalfMade_IsUndone_AndNoPactlSaysWhatToInstall()
    {
        var (runner, pactl) = Fake();
        var server = new PretendModules(runner) { SourceFails = true };
        var mic = new PactlVirtualMic(pactl);
        Assert.False(await mic.CreateAsync(default));
        Assert.False(server.AnyOfOurs());
        Assert.Equal(new[] { "unload-module 536870913", "list short modules" }, runner.Lines()[^2..]);
        Assert.Equal((null, null), (mic.Loaded.Sink, mic.Loaded.Source));

        var (none, missing) = Fake(installed: false);
        var noPactl = new PactlVirtualMic(missing);
        Assert.False(await noPactl.CreateAsync(default));
        await noPactl.RemoveAsync(default);
        Assert.Empty(none.Calls);
        Assert.Contains("pipewire-pulse", noPactl.Texts.Explanation);
        Assert.Contains("pulseaudio-utils", noPactl.Texts.Explanation);
        Assert.DoesNotContain("pipewire-pulse", mic.Texts.Explanation); // with pactl: nothing to install
    }

    [Fact]
    public async Task VirtualMic_OutOfTimeHalfWay_IsUndoneWithADeadlineOfItsOwn()
    {
        // The sink's load runs out of time (made, its number never read): found by name and unloaded.
        var (runner, pactl) = Fake();
        var server = new PretendModules(runner) { SinkTimesOut = true };
        Assert.False(await new PactlVirtualMic(pactl).CreateAsync(default));
        Assert.False(server.AnyOfOurs());

        // The creation's own deadline passes right after the sink was made, and the system had switched to it: the
        // undo still runs (its own deadline), the defaults go back, nothing of ours stays.
        var (late, latePactl) = Fake();
        var lateServer = new PretendModules(late);
        late.InTurn["info"] = new Queue<CommandResult>(new[] { new CommandResult(0, Info(), "") });
        late.Answers["info"] = new CommandResult(0, Info("easyvocal_out", Yeti), "");
        using var deadline = new CancellationTokenSource();
        lateServer.OnCall = call => { if (string.Join(" ", call.Arguments) == LoadSource) deadline.Cancel(); };
        var mic = new PactlVirtualMic(latePactl);
        Assert.False(await mic.CreateAsync(deadline.Token));
        Assert.False(lateServer.AnyOfOurs());
        Assert.Contains("set-default-sink " + Speakers, late.Lines());
        Assert.Equal((null, null), (mic.Loaded.Sink, mic.Loaded.Source));
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
        using var backend = new PulseBackend(watch: false);
        backend.Refresh();
        var mic = new PactlVirtualMic(devicesChanged: () => backend.Refresh());
        Assert.True(await mic.CreateAsync(default), "the virtual microphone was made");
        try
        {
            var call = mic.Find(backend.Outputs());
            Assert.NotNull(call);
            Assert.Contains(backend.Inputs(), d => d.Id == PactlVirtualMic.SourceName && d.Name == PactlVirtualMic.SourceDescription);
            Assert.Throws<IOException>(() => backend.OpenMic(PactlVirtualMic.SourceName)); // never EasyVocal's own mic as the mic

            using var speaker = backend.OpenSpeaker(call!.Id);
            using var input = new PulseMic(null, PactlVirtualMic.SourceName); // what a call app hears
            double energy = 0;
            int blocks = 0;
            input.Stopped += why => { lock (output) output.WriteLine("the virtual microphone stopped: " + why?.Message); };
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
                if (blocks <= 20) output.WriteLine(PulseState());
                Assert.True(blocks > 20, "the virtual microphone delivered audio");
                Assert.True(energy > 0.01, "the tone played into EasyVocal-Output came out of EasyVocal-Microphone");
            }
        }
        finally
        {
            await mic.RemoveAsync(default);
        }
        Assert.DoesNotContain(backend.Outputs(), d => d.Id == PactlVirtualMic.SinkName);
        Assert.DoesNotContain(backend.Inputs(), d => d.Id == PactlVirtualMic.SourceName);
    }

    /// <summary>What the server has (for a failure's output): its devices, streams and modules.</summary>
    private static string PulseState()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var what in new[] { "sinks", "sources", "sink-inputs", "source-outputs", "modules" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("pactl", "list short " + what) { RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                sb.Append("pactl list short ").Append(what).Append(":\n").Append(text);
            }
            catch (Exception ex) { sb.Append(what).Append(": ").Append(ex.Message).Append('\n'); }
        }
        return sb.ToString();
    }

    [Fact]
    public void PulseStreams_AskForSmallBuffers()
    {
        const int Block = 480 * sizeof(float); // 10 ms of mono float at 48 kHz
        var play = PulseSimple.Attributes(PulseSimple.Playback, Block);
        Assert.Equal((uint)(3 * Block), play.TLength); // ~30 ms queued at the server
        Assert.Equal((uint)Block, play.MinReq);
        var record = PulseSimple.Attributes(PulseSimple.Record, Block);
        Assert.Equal((uint)Block, record.FragSize);
        Assert.Equal(uint.MaxValue, record.TLength);
    }

    [Fact]
    public void Find_OnlyReturnsPrograms()
    {
        if (OperatingSystem.IsWindows())
        {
            output.WriteLine("SKIPPED on Windows: no execute bits");
            return;
        }
        var folder = TestFolders.New("EV_Test_Find_");
        try
        {
            var plain = Path.Combine(folder, "pactl");
            File.WriteAllText(plain, "not a program");
            File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.False(CommandRunner.IsExecutable(plain));
            Assert.Null(CommandRunner.Instance.Find(plain));
            File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(plain, CommandRunner.Instance.Find(plain));
        }
        finally { TestFolders.Delete(folder); }
    }
}
