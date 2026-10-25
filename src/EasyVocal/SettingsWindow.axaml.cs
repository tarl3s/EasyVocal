using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Hotkeys;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// The ⚙ window: which microphone, call output and headphones to use, language, start with Windows, ask before
/// quitting, updates (check on open, Check now), the general hotkeys, and "Open setup again". Every change applies at once (there is no Save button);
/// a device change restarts the audio.
/// </summary>
public partial class SettingsWindow : AppWindow
{
    private readonly AppController controller;
    private ComboBoxItem? micShown;

    /// <summary>One Settings window at a time.</summary>
    protected override string? SingleKind => "settings";
    private bool loading;

    public SettingsWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        Place("settings", controller);
        BuildHotkeyRows();
        Load();
    }

    /// <summary>Shows the window over <paramref name="owner"/> (or on its own) and waits for it.</summary>
    public static Task OpenAsync(AppController controller, Window? owner) => new SettingsWindow(controller).ShowAsync(owner);

    private void Load()
    {
        loading = true;
        try
        {
            var s = controller.Settings;
            var inputs = controller.Devices.Inputs();
            var outputs = controller.Devices.Outputs();

            DeviceBox.Fill(MicBox, L.T("Automatic (Windows default)"), inputs, s.MicDeviceId);
            micShown = MicBox.SelectedItem as ComboBoxItem;
            MicProblem.IsVisible = false;

            OutputHint.Text = L.T(controller.Platform.VirtualMic.Texts.OutputHint);
            DeviceBox.Fill(OutputBox, L.T(controller.Platform.VirtualMic.Texts.AutomaticOutput), outputs, s.OutputDeviceId);
            DeviceBox.Fill(MonitorBox, L.T("Windows default"), DeviceRules.ExcludeVirtualCable(outputs), s.MonitorDeviceId);

            LanguageBox.Items.Clear();
            var languages = new (string Code, string Name)[]
            {
                ("auto", L.T("Automatic")), ("en", "English"), ("pt-BR", "Português (Brasil)"),
            };
            foreach (var (code, name) in languages)
            {
                var item = new ComboBoxItem { Content = name, Tag = code };
                LanguageBox.Items.Add(item);
                if (string.Equals(code, s.Language, StringComparison.OrdinalIgnoreCase)) LanguageBox.SelectedItem = item;
            }
            LanguageBox.SelectedItem ??= LanguageBox.Items[0];

            StartBox.IsChecked = s.StartWithWindows;
            AskQuitBox.IsChecked = s.ConfirmQuit;
            UpdateBox.IsChecked = s.CheckForUpdates;
            CheckNowButton.IsVisible = controller.Updates != null;
            UpdateStatus.Text = VersionText();
            RefreshHotkeyRows();
        }
        finally
        {
            loading = false;
        }
    }

    // ---------- devices ----------

    private void MicBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || MicBox.SelectedItem is not ComboBoxItem item) return;
        if ((item.Tag as DeviceInfo) is { } device && DeviceRules.IsVirtualCable(device.Name))
        {
            // The cable's output is the virtual microphone EasyVocal itself feeds: listening to it would loop the voice.
            MicProblem.Text = L.T("That is EasyVocal's own output — using it as the microphone would loop your voice.");
            MicProblem.IsVisible = true;
            loading = true;
            MicBox.SelectedItem = micShown;
            loading = false;
            return;
        }
        MicProblem.IsVisible = false;
        micShown = item;
        controller.Settings.MicDeviceId = DeviceBox.IdOf(MicBox);
        DevicesChanged();
    }

    private void OutputBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        controller.Settings.OutputDeviceId = DeviceBox.IdOf(OutputBox);
        DevicesChanged();
    }

    private void MonitorBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        controller.Settings.MonitorDeviceId = DeviceBox.IdOf(MonitorBox);
        DevicesChanged();
    }

    private void DevicesChanged()
    {
        controller.Save();
        controller.RestartAudio();
    }

    // ---------- general ----------

    private void LanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loading || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        controller.Settings.Language = code;
        controller.Save();
    }

    private async void StartBox_Click(object? sender, RoutedEventArgs e)
    {
        bool on = StartBox.IsChecked == true;
        if (controller.SetStartWithWindows(on)) return;
        StartBox.IsChecked = !on;
        await AskDialog.TellAsync(this, L.T("Windows would not let EasyVocal start with it.")); // already in Settings: no "try again in Settings"
    }

    private void AskQuitBox_Click(object? sender, RoutedEventArgs e)
    {
        controller.Settings.ConfirmQuit = AskQuitBox.IsChecked == true;
        controller.Save();
    }

    private void UpdateBox_Click(object? sender, RoutedEventArgs e)
    {
        controller.Settings.CheckForUpdates = UpdateBox.IsChecked == true;
        controller.Save();
    }

    private static string VersionText() => L.F("Version {0}", UpdateRules.RunningVersion(typeof(SettingsWindow).Assembly));

    private async void CheckNowButton_Click(object? sender, RoutedEventArgs e)
    {
        if (controller.Updates is not { } updates) return;
        CheckNowButton.IsEnabled = false;
        UpdateStatus.Text = L.T("Checking…");
        try
        {
            var text = await updates.CheckNowAsync(this);
            UpdateStatus.Text = text ?? VersionText();
        }
        catch (Exception ex)
        {
            AppLog.Error("Check now failed", ex);
            UpdateStatus.Text = L.T("Couldn't check for updates. Try again later.");
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    private async void SetupAgainButton_Click(object? sender, RoutedEventArgs e)
    {
        await SetupWindow.OpenAsync(controller, this);
        Load(); // setup may have changed the microphone and "Start with Windows"
    }

    // ---------- general hotkeys ----------

    private readonly Dictionary<string, Button> hotkeyButtons = new();

    /// <summary>For the smoke test.</summary>
    internal Button HotkeyButton(string id) => hotkeyButtons[id];

    private void BuildHotkeyRows()
    {
        foreach (var (id, label) in HotkeyMap.General)
        {
            int row = HotkeyGrid.RowDefinitions.Count;
            HotkeyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var text = new TextBlock { Text = L.T(label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var button = new Button { MinWidth = 130, Margin = new Thickness(0, 3, 0, 3) };
            ToolTip.SetTip(button, L.T("Set hotkey…"));
            var key = id;
            button.Click += (_, _) => FireAndForget.Watch(AssignGeneralAsync(key), "Assigning a hotkey");
            Grid.SetRow(text, row);
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 1);
            HotkeyGrid.Children.Add(text);
            HotkeyGrid.Children.Add(button);
            hotkeyButtons[id] = button;
        }
    }

    private void RefreshHotkeyRows()
    {
        foreach (var (id, button) in hotkeyButtons)
            button.Content = controller.Settings.GeneralHotkeys.TryGetValue(id, out var key) && !string.IsNullOrEmpty(key) ? key : L.T("Not set");
    }

    /// <summary>Asks for a key and registers it; if the system refuses it (another app owns it) the old one comes back and the person is told.</summary>
    private async Task AssignGeneralAsync(string id)
    {
        var map = controller.Settings.GeneralHotkeys;
        map.TryGetValue(id, out var current);
        var action = "general:" + id;
        var pick = await HotkeyCaptureDialog.PickAsync(this, controller, action, controller.ActionDisplayName(action), current);
        if (!pick.Ok || controller.IsDisposed) return; // cancelled, or EasyVocal quit meanwhile
        var chosen = pick.Hotkey;
        SetGeneral(id, chosen);
        controller.RebuildHotkeys(); // unregisters first; refused keys are reported through RefusedActions
        if (chosen != null && controller.RefusedActions.Contains(action))
        {
            SetGeneral(id, current);
            controller.RebuildHotkeys();
            await AskDialog.TellAsync(this, L.F("Another app is already using {0}. Pick a different one.", chosen));
        }
        RefreshHotkeyRows();
    }

    private void SetGeneral(string id, string? key)
    {
        if (string.IsNullOrEmpty(key)) controller.Settings.GeneralHotkeys.Remove(id);
        else controller.Settings.GeneralHotkeys[id] = key;
        controller.Save();
    }
}
