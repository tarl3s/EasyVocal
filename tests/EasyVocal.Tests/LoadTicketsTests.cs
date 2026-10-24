using EasyVocal.Core.Sounds;

namespace EasyVocal.Tests;

public class LoadTicketsTests
{
    [Fact]
    public void ATicketIsCurrentUntilANewerOneIsTaken()
    {
        var t = new LoadTickets();
        int startup = t.Next("ding");
        Assert.True(t.IsCurrent("ding", startup));

        int edited = t.Next("ding"); // the person replaced the file while the startup decode ran
        Assert.False(t.IsCurrent("ding", startup)); // the startup result must not overwrite the new audio
        Assert.True(t.IsCurrent("ding", edited));
    }

    [Fact]
    public void SoundsHaveTheirOwnTickets()
    {
        var t = new LoadTickets();
        int ding = t.Next("ding");
        int boing = t.Next("boing");
        t.Next("boing");
        Assert.True(t.IsCurrent("ding", ding));
        Assert.False(t.IsCurrent("boing", boing));
    }

    [Fact]
    public void NoTicketTaken_NothingIsCurrent()
    {
        Assert.False(new LoadTickets().IsCurrent("ding", 0));
        Assert.False(new LoadTickets().IsCurrent("ding", 1));
    }
}
