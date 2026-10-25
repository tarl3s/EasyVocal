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
