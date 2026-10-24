using System.Collections.Generic;

namespace EasyVocal.Core.Sounds;

/// <summary>
/// One counter per sound id, so a file decoded in the background is only used when nothing newer happened to that
/// sound meanwhile (it was edited, replaced, deleted or loaded again). Take a ticket before the work starts, take a
/// new one whenever the sound's audio changes, and keep the result only while <see cref="IsCurrent"/>. One thread.
/// </summary>
public sealed class LoadTickets
{
    private readonly Dictionary<string, int> latest = new();

    /// <summary>A new ticket for <paramref name="id"/>; every older one stops being current.</summary>
    public int Next(string id)
    {
        latest.TryGetValue(id, out var t);
        latest[id] = ++t;
        return t;
    }

    /// <summary>True when <paramref name="ticket"/> is the newest one taken for <paramref name="id"/>.</summary>
    public bool IsCurrent(string id, int ticket) => latest.TryGetValue(id, out var t) && t == ticket;
}
