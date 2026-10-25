using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace EasyVocal;

/// <summary>The smoke and e2e tests' hands: clicks that go through the same code a mouse click does.</summary>
internal static class Ui
{
    private static readonly MethodInfo ButtonOnClick =
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("Button.OnClick");

    /// <summary>On screen and clickable: in a window, not hidden by itself or a parent, enabled.</summary>
    public static bool CanClick(Control c) => c.IsEffectivelyEnabled && c.IsEffectivelyVisible && c.GetVisualRoot() != null;

    /// <summary>
    /// The same as a mouse click: <c>OnClick</c> (a toggle button or check box flips first, then Click runs). A person
    /// can't click what is disabled or not on screen: that throws.
    /// </summary>
    public static void Click(Button button)
    {
        if (!CanClick(button))
            throw new InvalidOperationException("The button '" + (button.Content as string ?? button.Name) + "' can't be clicked (disabled or not on screen).");
        ButtonOnClick.Invoke(button, null);
    }

    /// <summary>A menu item picked (from a right-click or the card's "⋯"); it must be enabled.</summary>
    public static void Click(MenuItem item)
    {
        if (!item.IsEffectivelyEnabled) throw new InvalidOperationException("The menu item '" + item.Header + "' is disabled.");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    }

    /// <summary>The first control below <paramref name="root"/> (logical tree) that matches.</summary>
    public static T? Find<T>(ILogical root, Func<T, bool> match) where T : class
    {
        foreach (var child in root.GetLogicalDescendants())
            if (child is T t && match(t)) return t;
        return null;
    }
}

/// <summary>
/// A mouse the tests move: presses, moves and releases raised on a control the way the platform raises them (with
/// pointer capture), at points in that control's own coordinates (off it too).
/// </summary>
internal sealed class TestMouse
{
    private readonly Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
    private ulong time = 1;

    public Avalonia.Input.IInputElement? Captured => pointer.Captured;

    private (Avalonia.Visual Root, Avalonia.Point At) Where(Control target, Avalonia.Point local)
    {
        var root = (Avalonia.Visual)(target.GetVisualRoot() ?? throw new InvalidOperationException("not on screen"));
        return (root, Avalonia.VisualExtensions.TranslatePoint(target, local, root) ?? local);
    }

    private static Avalonia.Input.PointerPointProperties Props(bool left, Avalonia.Input.PointerUpdateKind kind) =>
        new(left ? Avalonia.Input.RawInputModifiers.LeftMouseButton : Avalonia.Input.RawInputModifiers.None, kind);

    public void Press(Control target, Avalonia.Point local)
    {
        var (root, at) = Where(target, local);
        target.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(target, pointer, root, at, time++,
            Props(true, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), Avalonia.Input.KeyModifiers.None));
    }

    /// <summary>A move over <paramref name="target"/> (the captured control gets it while there is one, as on screen).</summary>
    public void Move(Control target, Avalonia.Point local, bool leftDown)
    {
        var to = pointer.Captured as Control ?? target;
        var (root, at) = Where(target, local);
        to.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, to, pointer, root, at, time++,
            Props(leftDown, Avalonia.Input.PointerUpdateKind.Other), Avalonia.Input.KeyModifiers.None));
    }

    public void Release(Control target, Avalonia.Point local)
    {
        var to = pointer.Captured as Control ?? target;
        var (root, at) = Where(target, local);
        to.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(to, pointer, root, at, time++,
            Props(false, Avalonia.Input.PointerUpdateKind.LeftButtonReleased), Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));
    }
}
