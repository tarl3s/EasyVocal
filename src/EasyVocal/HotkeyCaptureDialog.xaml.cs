using System.Windows;
using System.Windows.Input;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// "Press the key or combination…": shows the press live, refuses a hotkey another voice, sound or action already
/// uses, warns about keys that would stop typing in other apps, and checks Windows will give the key to us.
/// </summary>
public partial class HotkeyCaptureDialog : Window
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
        DarkTitleBar.Use(this);
        ItemText.Text = L.F("Hotkey for {0}", itemName);
        RemoveButton.Visibility = string.IsNullOrEmpty(current) ? Visibility.Collapsed : Visibility.Visible;
        if (Hotkey.TryParse(current, out var key)) captured = key;
        Refresh();
        Loaded += (_, _) => Focus();
    }

    /// <summary>The chosen hotkey as saved in settings ("Ctrl+1"); null when the person removed it.</summary>
    public string? Result { get; private set; }

    /// <summary>
    /// Opens the dialog. True when the person saved or removed a hotkey (<paramref name="result"/> null = removed).
    /// The app's own hotkeys are let go while it is open (they would swallow the press) and taken back afterwards.
    /// </summary>
    public static bool Pick(Window? owner, AppController controller, string actionId, string itemName, string? current, out string? result)
    {
        controller.SuspendHotkeys();
        try
        {
            var dialog = new HotkeyCaptureDialog(controller, actionId, itemName, current);
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            bool ok = dialog.ShowDialog() == true;
            result = ok ? dialog.Result : null;
            return ok;
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var mods = CurrentMods();
        if (key == Key.Escape && mods == HotkeyMods.None)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            KeyText.Text = Hotkey.ModsPrefix(mods) + "…"; // only modifiers so far
            showingMods = true;
            e.Handled = true;
            return;
        }
        if (Capture(mods, KeyInterop.VirtualKeyFromKey(key))) e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (!showingMods || CurrentMods() != HotkeyMods.None) return;
        showingMods = false;
        Refresh();
    }

    private static HotkeyMods CurrentMods()
    {
        var m = Keyboard.Modifiers;
        var mods = HotkeyMods.None;
        if ((m & ModifierKeys.Control) != 0) mods |= HotkeyMods.Ctrl;
        if ((m & ModifierKeys.Alt) != 0) mods |= HotkeyMods.Alt;
        if ((m & ModifierKeys.Shift) != 0) mods |= HotkeyMods.Shift;
        if ((m & ModifierKeys.Windows) != 0) mods |= HotkeyMods.Win;
        return mods;
    }

    /// <summary>True when the press is a hotkey we can use (the event is then handled).</summary>
    private bool Capture(HotkeyMods mods, int virtualKey)
    {
        var key = Hotkey.FromKeyEvent(mods, virtualKey);
        if (key == null) return false; // Enter, Tab… are left to the buttons
        captured = key;
        Refresh();
        return true;
    }

    private void Refresh()
    {
        ProblemText.Visibility = Visibility.Collapsed;
        TypingNote.Visibility = Visibility.Collapsed;
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
            TypingNote.Visibility = Visibility.Visible;
        }
    }

    private void ShowProblem(string text)
    {
        ProblemText.Text = text;
        ProblemText.Visibility = Visibility.Visible;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (captured is not { } key) return;
        if (!controller.IsHotkeyFree(key))
        {
            ShowProblem(L.F("Another app is already using {0}. Pick a different one.", key.ToString()));
            SaveButton.IsEnabled = false; // until another key is pressed
            return;
        }
        Result = key.ToString();
        DialogResult = true;
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
    }
}
