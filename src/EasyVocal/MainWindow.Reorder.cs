using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using EasyVocal.Core.Model;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The cards' order: drag a voice or sound card onto another (a bar shows where it goes), or "Move left / right" in its
/// menu. Plain pointer events (the same on Windows, macOS and Linux); a click without moving still picks the voice or
/// plays the sound. The "＋" cards always stay at the end; AI voice cards move among themselves.
/// </summary>
public partial class MainWindow
{
    /// <summary>How far the pointer moves (px) before a press on a card becomes a drag.</summary>
    private const double DragDistance = 6;

    private sealed class CardDrag(string id, bool voice, Card card, Point from)
    {
        public string Id { get; } = id;
        public bool Voice { get; } = voice;
        public Card Card { get; } = card;
        public Point From { get; } = from;
        public bool Moving { get; set; }
        public string? TargetId { get; set; }
        public bool After { get; set; }
    }

    private CardDrag? cardDrag;

    private static Border DropBar(HorizontalAlignment side) => new()
    {
        Width = 4,
        Margin = new Thickness(side == HorizontalAlignment.Left ? 1 : 0, 6, side == HorizontalAlignment.Right ? 1 : 0, 6),
        CornerRadius = new CornerRadius(2),
        Background = Res.Brush("Accent"),
        HorizontalAlignment = side,
        IsVisible = false,
        IsHitTestVisible = false,
    };

    private void AttachReorder(Card card, string id, bool voice)
    {
        var button = card.Button;
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) cardDrag = new CardDrag(id, voice, card, e.GetPosition(this));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        button.AddHandler(PointerMovedEvent, (_, e) => DragMoved(e), RoutingStrategies.Bubble, handledEventsToo: true);
        // Tunnel: before the button's own release, so a dropped card doesn't also click.
        button.AddHandler(PointerReleasedEvent, (_, e) => DragReleased(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private Dictionary<string, Card> CardsOf(bool voice) => voice ? voiceCards : soundCards;

    private void DragMoved(PointerEventArgs e)
    {
        var d = cardDrag;
        if (d == null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndDrag(); // the release went elsewhere
            return;
        }
        var p = e.GetPosition(this);
        if (!d.Moving)
        {
            if (Math.Abs(p.X - d.From.X) < DragDistance && Math.Abs(p.Y - d.From.Y) < DragDistance) return;
            d.Moving = true;
            d.Card.Button.Opacity = 0.5;
        }
        foreach (var (id, c) in CardsOf(d.Voice))
        {
            if (d.Voice && aiCardIds.Contains(id) != aiCardIds.Contains(d.Id)) continue; // AI cards move among AI cards
            var at = e.GetPosition(c.Button);
            var size = c.Button.Bounds.Size;
            if (at.X < 0 || at.Y < 0 || at.X > size.Width || at.Y > size.Height) continue;
            ShowDropTarget(id, at.X > size.Width / 2);
            return;
        }
        ShowDropTarget(null, false); // over no card: no bar, and a drop here moves nothing
    }

    private void ShowDropTarget(string? id, bool after)
    {
        var d = cardDrag;
        if (d == null) return;
        foreach (var (cid, c) in CardsOf(d.Voice))
        {
            bool target = cid == id && cid != d.Id;
            c.DropBefore.IsVisible = target && !after;
            c.DropAfter.IsVisible = target && after;
        }
        d.TargetId = id == d.Id ? null : id;
        d.After = after;
    }

    private void DragReleased(PointerReleasedEventArgs e)
    {
        var d = cardDrag;
        if (d == null) return;
        if (!d.Moving)
        {
            cardDrag = null; // a click: the button does what it always does
            return;
        }
        e.Handled = true; // a drag is not a click
        string? target = d.TargetId;
        bool after = d.After;
        EndDrag();
        e.Pointer.Capture(null);
        if (target != null) Move(d.Id, d.Voice, BeforeIdFor(d.Voice, target, after));
    }

    private void EndDrag()
    {
        var d = cardDrag;
        cardDrag = null;
        if (d == null) return;
        d.Card.Button.Opacity = 1;
        foreach (var c in CardsOf(d.Voice).Values)
        {
            c.DropBefore.IsVisible = false;
            c.DropAfter.IsVisible = false;
        }
    }

    /// <summary>The id the moved item goes before: the target itself, or (after it) whatever follows it in the list.</summary>
    private string? BeforeIdFor(bool voice, string target, bool after)
    {
        var ids = AllIds(voice);
        int i = ids.IndexOf(target);
        if (!after) return target;
        return i >= 0 && i + 1 < ids.Count ? ids[i + 1] : null;
    }

    private List<string> AllIds(bool voice) => voice
        ? controller.Settings.Voices.ConvertAll(v => v.Id)
        : controller.Settings.Sounds.ConvertAll(s => s.Id);

    /// <summary>The ids of the cards on screen, in their order (for a voice: those of its own section, classic or AI).</summary>
    private List<string> ShownIds(bool voice, string? of = null) => voice
        ? ItemOps.VisibleVoices(controller.Settings, showHidden).FindAll(v => of == null || (v.Kind == VoiceKind.Ai) == (controller.Settings.Voices.Find(x => x.Id == of)?.Kind == VoiceKind.Ai)).ConvertAll(v => v.Id)
        : ItemOps.VisibleSounds(controller.Settings, showHidden).ConvertAll(s => s.Id);

    /// <summary>A drop bar shows on some card (for the tests).</summary>
    internal bool DropBarShown => voiceCards.Values.Concat(soundCards.Values).Any(c => c.DropBefore.IsVisible || c.DropAfter.IsVisible);

    private void Move(string id, bool voice, string? beforeId)
    {
        if (voice) controller.MoveVoice(id, beforeId);
        else controller.MoveSound(id, beforeId);
    }

    /// <summary>Move one card to the left (-1) or right (+1) among the cards on screen.</summary>
    internal void MoveCard(string id, bool voice, int direction)
    {
        var shown = ShownIds(voice, id);
        int i = shown.IndexOf(id);
        int j = i + direction;
        if (i < 0 || j < 0 || j >= shown.Count) return;
        Move(id, voice, direction < 0 ? shown[j] : BeforeIdFor(voice, shown[j], after: true));
    }

    /// <summary>"Move left" / "Move right" in a card's menu: the keyboard's (and the touchpad's) way to reorder.</summary>
    private void AddMoveItems(ContextMenu menu, string id, bool voice)
    {
        var shown = ShownIds(voice, id);
        int i = shown.IndexOf(id);
        var left = MenuItem("←  " + L.T("Move left"), () => MoveCard(id, voice, -1));
        left.IsEnabled = i > 0;
        var right = MenuItem("→  " + L.T("Move right"), () => MoveCard(id, voice, +1));
        right.IsEnabled = i >= 0 && i < shown.Count - 1;
        menu.Items.Add(left);
        menu.Items.Add(right);
    }
}
