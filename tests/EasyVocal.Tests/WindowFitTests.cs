using EasyVocal.Core.Model;

namespace EasyVocal.Tests;

public class WindowFitTests
{
    // A 1366×768 laptop at 125 %: 1093 × 583 device-independent pixels above a 40 px taskbar.
    private static readonly WindowBox Laptop = new(0, 0, 1093, 582);
    private static readonly WindowBox Right = new(1093, 0, 1920, 1040);

    [Fact]
    public void Fit_ShrinksATallWindowToTheScreen()
    {
        var f = WindowFit.Fit(new WindowBox(100, 50, 500, 940), Laptop);
        Assert.Equal(new WindowBox(100, 0, 500, 582), f);
    }

    [Fact]
    public void Fit_MovesAWindowBackOnTheScreen()
    {
        Assert.Equal(new WindowBox(593, 282, 500, 300), WindowFit.Fit(new WindowBox(900, 400, 500, 300), Laptop));
        Assert.Equal(new WindowBox(0, 0, 500, 300), WindowFit.Fit(new WindowBox(-300, -50, 500, 300), Laptop));
    }

    [Fact]
    public void Fit_KeepsTheMinimumUnlessTheScreenIsSmaller()
    {
        Assert.Equal(new WindowBox(0, 0, 400, 300), WindowFit.Fit(new WindowBox(0, 0, 100, 100), Laptop, 400, 300));
        Assert.Equal(new WindowBox(0, 0, 1093, 582), WindowFit.Fit(new WindowBox(0, 0, 100, 100), Laptop, 2000, 900));
    }

    [Fact]
    public void Fit_LeavesAWindowThatFitsAlone()
    {
        var w = new WindowBox(10, 20, 300, 200);
        Assert.Equal(w, WindowFit.Fit(w, Laptop));
    }

    [Fact]
    public void AreaFor_PicksTheScreenUnderTheCentre_ElseTheMostOverlap_ElseThePrimary()
    {
        var areas = new[] { Laptop, Right };
        Assert.Equal(Right, WindowFit.AreaFor(new WindowBox(1500, 100, 400, 300), areas));
        Assert.Equal(Laptop, WindowFit.AreaFor(new WindowBox(800, 100, 400, 300), areas));
        Assert.Equal(Right, WindowFit.AreaFor(new WindowBox(1200, 1000, 400, 300), areas)); // centre below both: overlap
        Assert.Equal(Laptop, WindowFit.AreaFor(new WindowBox(-5000, -5000, 10, 10), areas));
    }

    [Fact]
    public void Fits_OnlyWhenTheRememberedPlaceIsStillOnAScreen()
    {
        var areas = new[] { Laptop, Right };
        Assert.True(WindowFit.Fits(new WindowBox(1200, 100, 800, 600), areas));
        Assert.True(WindowFit.Fits(new WindowBox(-7, 0, 500, 400), areas)); // the invisible border hangs over
        Assert.False(WindowFit.Fits(new WindowBox(1200, 100, 800, 600), new[] { Laptop })); // that monitor is gone
        Assert.False(WindowFit.Fits(new WindowBox(0, 0, 500, 900), new[] { Laptop }));       // taller than the screen
        Assert.False(WindowFit.Fits(new WindowBox(0, 0, 200, 100), areas, minWidth: 400, minHeight: 300));
        Assert.False(WindowFit.Fits(new WindowBox(double.NaN, 0, 500, 400), areas));
        Assert.False(WindowFit.Fits(new WindowBox(0, 0, 500, 400), System.Array.Empty<WindowBox>()));
    }

    [Fact]
    public void Fits_PositionOnly_NeedsTheTitleBarOnAScreen()
    {
        Assert.True(WindowFit.Fits(new WindowBox(300, 200, 0, 0), new[] { Laptop }));
        Assert.False(WindowFit.Fits(new WindowBox(3000, 200, 0, 0), new[] { Laptop }));
        Assert.False(WindowFit.Fits(new WindowBox(300, 200, 0, 50), new[] { Laptop })); // half a size is nonsense
    }
}
