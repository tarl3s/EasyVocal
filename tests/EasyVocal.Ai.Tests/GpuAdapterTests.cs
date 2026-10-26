using EasyVocal.Ai.Runtime;
using Xunit.Abstractions;

namespace EasyVocal.Ai.Tests;

/// <summary>DirectML's adapter: the high-performance (discrete) GPU first, never a software adapter.</summary>
public class GpuAdapterTests
{
    private readonly ITestOutputHelper output;

    public GpuAdapterTests(ITestOutputHelper output) => this.output = output;

    private static GpuAdapter A(int index, string name, long mb, bool software = false) => new(index, name, mb * 1048576, software, 1000 + index);

    [Fact]
    public void A_laptop_uses_its_discrete_GPU_before_the_one_showing_the_desktop()
    {
        var list = new[] { A(0, "Intel UHD", 128), A(1, "NVIDIA RTX", 6144), A(2, "Microsoft Basic Render Driver", 0, software: true) };
        Assert.Equal(new[] { 1, 0 }, GpuAdapters.DirectMLOrder(list, highPerformanceLuid: 1001));
        // without Windows' preference (older Windows 10): the one with more memory of its own
        Assert.Equal(new[] { 1, 0 }, GpuAdapters.DirectMLOrder(list, highPerformanceLuid: null));
    }

    [Fact]
    public void Windows_preference_wins_and_software_adapters_are_never_tried()
    {
        var list = new[] { A(0, "AMD Radeon", 4096), A(1, "Other GPU", 8192), A(2, "WARP", 0, software: true) };
        Assert.Equal(new[] { 0, 1 }, GpuAdapters.DirectMLOrder(list, highPerformanceLuid: 1000));
        Assert.Equal(new[] { 0 }, GpuAdapters.DirectMLOrder(new[] { A(0, "AMD Radeon", 4096), A(1, "WARP", 0, software: true) }, 1001));
        Assert.Equal(new[] { 0 }, GpuAdapters.DirectMLOrder(new[] { A(0, "WARP", 0, software: true) }, null)); // DirectML refuses: the CPU
        Assert.Equal(new[] { 0 }, GpuAdapters.DirectMLOrder(Array.Empty<GpuAdapter>(), null));
    }

    [Fact]
    public void This_computer_lists_its_adapters_without_failing()
    {
        foreach (var a in GpuAdapters.List(out var best)) output.WriteLine($"{a.Index}: {a.Name}, {a.DedicatedMemory / 1048576} MB{(a.Software ? ", software" : "")}{(a.Luid == best ? ", high-performance" : "")}");
        var order = GpuAdapters.DirectMLDevices;
        output.WriteLine("DirectML tries: " + string.Join(", ", order));
        Assert.NotEmpty(order);
    }
}
