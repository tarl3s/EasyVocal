using EasyVocal.Core.Sounds;

namespace EasyVocal.Tests;

public class AudioHistoryTests
{
    private static AudioState S(int length, bool original = false) => new(new float[length], original);

    [Fact]
    public void UndoRedo_WalkBetweenVersions()
    {
        var first = S(10, original: true);
        var h = new AudioHistory(first);
        Assert.False(h.CanUndo);
        Assert.False(h.Changed);
        var second = S(5);
        var third = S(3);
        h.Push(second);
        h.Push(third);
        Assert.True(h.Changed);
        Assert.True(h.Undo());
        Assert.Same(second, h.Current);
        Assert.True(h.Undo());
        Assert.Same(first, h.Current);
        Assert.False(h.Changed);
        Assert.False(h.Undo());
        Assert.True(h.Redo());
        Assert.True(h.Redo());
        Assert.Same(third, h.Current);
        Assert.False(h.Redo());
    }

    [Fact]
    public void AnEditAfterUndo_DropsRedo()
    {
        var h = new AudioHistory(S(10));
        h.Push(S(9));
        h.Undo();
        Assert.True(h.CanRedo);
        h.Push(S(8));
        Assert.False(h.CanRedo);
        Assert.Equal(1, h.UndoCount);
    }

    [Fact]
    public void KeepsAtMostMaxSteps()
    {
        var h = new AudioHistory(S(1), maxSteps: 20);
        for (int i = 0; i < 30; i++) h.Push(S(1));
        Assert.Equal(20, h.UndoCount);
        int undone = 0;
        while (h.Undo()) undone++;
        Assert.Equal(20, undone);
    }

    [Fact]
    public void ForgetsOldVersionsPastTheMemoryBudget_ButKeepsOneUndo()
    {
        var h = new AudioHistory(S(400), maxSteps: 20, maxSamples: 1000);
        h.Push(S(400));
        h.Push(S(400));
        Assert.True(h.TotalSamples <= 1000 || h.UndoCount == 1);
        Assert.Equal(1, h.UndoCount);
        h.Push(S(5000)); // one huge version: still one step back
        Assert.Equal(1, h.UndoCount);
        Assert.True(h.Undo());
    }
}
