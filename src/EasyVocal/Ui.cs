using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace EasyVocal;

/// <summary>The smoke and e2e tests' hands: clicks that go through the same code a mouse click does.</summary>
internal static class Ui
{
    private static readonly MethodInfo ButtonOnClick =
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("Button.OnClick");

    /// <summary>The same as a mouse click: <c>OnClick</c> (a toggle button or check box flips first, then Click runs).</summary>
    public static void Click(Button button) => ButtonOnClick.Invoke(button, null);

    /// <summary>A menu item picked (from a right-click or the card's "⋯").</summary>
    public static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));

    /// <summary>The first control below <paramref name="root"/> (logical tree) that matches.</summary>
    public static T? Find<T>(ILogical root, Func<T, bool> match) where T : class
    {
        foreach (var child in root.GetLogicalDescendants())
            if (child is T t && match(t)) return t;
        return null;
    }
}
