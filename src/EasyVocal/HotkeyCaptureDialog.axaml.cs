using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// "Press the key or combination…": shows the press live, refuses a hotkey another voice, sound or action already
/// uses, warns about keys that would stop typing in other apps, and checks the system will give the key to us.
/// </summary>
public partial class HotkeyCaptureDialog : AppWindow
{
    private readonly AppController controller;
    private readonly string actionId;
    private Hotkey? captured;
    private bool showingMods;

    private HotkeyCaptureDialog(AppController controller, string actionId, string itemName, string? current)
    {
        this.controller = controller;
        this.actionId = actionId;
        InitializeComponent();
        Place("hotkey", null); // kept on screen; it opens over its owner every time
        ItemText.Text = L.F("Hotkey for {0}", itemName);
        RemoveButton.IsVisible = !string.IsNullOrEmpty(current);
        if (Hotkey.TryParse(current, out var key)) captured = key;
        Refresh();
        // Before any button sees the key (a hotkey may be Space or a letter).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
    }

    /// <summary>The chosen hotkey as saved in settings ("Ctrl+1"); null when the person removed it.</summary>
    public string? Result { get; private set; }

    /// <summary>What <see cref="PickAsync"/> gives back: <see cref="Ok"/> when a hotkey was saved or removed (<see cref="Hotkey"/> null = removed).</summary>
    public readonly record struct Pick(bool Ok, string? Hotkey);

    /// <summary>
    /// Opens the dialog and waits. The app's own hotkeys are let go while it is open (they would swallow the press)
    /// and taken back afterwards.
    /// </summary>
    public static async Task<Pick> PickAsync(Window? owner, AppController controller, string actionId, string itemName, string? current)
    {
        controller.SuspendHotkeys();
        try
        {
            var dialog = new HotkeyCaptureDialog(controller, actionId, itemName, current);
            await dialog.ShowAsync(owner);
            return dialog.Accepted ? new Pick(true, dialog.Result) : new Pick(false, null);
        }
        finally
        {
            controller.ResumeHotkeys();
        }
    }

    /// <summary>For the smoke test: the dialog, not shown (drive it with <see cref="Simulate"/>).</summary>
    internal static HotkeyCaptureDialog ForTest(AppController controller, string actionId, string itemName, string? current) =>
        new(controller, actionId, itemName, current);

    /// <summary>For the smoke and e2e tests: the same as pressing the combination.</summary>
    internal void Simulate(HotkeyMods mods, int virtualKey) => Capture(mods, virtualKey);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = KeyMap.Mods(e.KeyModifiers);
        if (e.Key == Key.Escape && mods == HotkeyMods.None)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (KeyMap.IsModifier(e.Key))
        {
            KeyText.Text = Hotkey.ModsPrefix(mods | KeyMap.ModOf(e.Key)) + "…"; // only modifiers so far
            showingMods = true;
            e.Handled = true;
            return;
        }
        if (Capture(mods, KeyMap.VirtualKey(e.Key))) e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (!showingMods) return;
        var left = KeyMap.Mods(e.KeyModifiers) & ~KeyMap.ModOf(e.Key);
        if (left != HotkeyMods.None)
        {
            KeyText.Text = Hotkey.ModsPrefix(left) + "…";
            return;
        }
        showingMods = false;
        Refresh();
    }

    /// <summary>True when the press is a hotkey we can use (the event is then handled).</summary>
    private bool Capture(HotkeyMods mods, int virtualKey)
    {
        var key = Hotkey.FromKeyEvent(mods, virtualKey);
        if (key == null) return false; // Enter, Tab… are left to the buttons
        captured = key;
        showingMods = false;
        Refresh();
        return true;
    }

    private void Refresh()
    {
        ProblemText.IsVisible = false;
        TypingNote.IsVisible = false;
        if (captured is not { } key)
        {
            KeyText.Text = "…";
            SaveButton.IsEnabled = false;
            return;
        }
        KeyText.Text = key.ToString();
        var other = HotkeyMap.WhoUsesAction(controller.Settings, key, actionId);
        if (other != null)
        {
            ShowProblem(L.F("Already used by {0}", controller.ActionDisplayName(other))); // the person's own names stay as typed
            SaveButton.IsEnabled = false;
            return;
        }
        SaveButton.IsEnabled = true;
        if (key.IsPlainTypingKey)
        {
            TypingNoteText.Text = L.F("While EasyVocal is open, this key will stop typing '{0}' in other apps. Ctrl+{0} or the number pad avoid that.", key.KeyName);
            TypingNote.IsVisible = true;
        }
    }

    private void ShowProblem(string text)
    {
        ProblemText.Text = text;
        ProblemText.IsVisible = true;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (captured is not { } key) return;
        if (!controller.IsHotkeyFree(key))
        {
            ShowProblem(L.F("Another app is already using {0}. Pick a different one.", key.ToString()));
            SaveButton.IsEnabled = false; // until another key is pressed
            return;
        }
        Result = key.ToString();
        Accept();
    }

    private void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Accept();
    }
}
