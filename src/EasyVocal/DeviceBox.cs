using System.Windows.Controls;
using EasyVocal.Core.Engine;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>The device pickers of Settings and Setup: one list logic, so both always show the same choice.</summary>
internal static class DeviceBox
{
    /// <summary>
    /// The first item is the automatic choice (no device id). A saved device that is unplugged stays listed as
    /// "Device not connected" and selected, so the box shows what EasyVocal is really trying to use.
    /// </summary>
    public static void Fill(ComboBox box, string autoLabel, IEnumerable<DeviceInfo> devices, string? savedId)
    {
        box.Items.Clear();
        var auto = new ComboBoxItem { Content = autoLabel };
        box.Items.Add(auto);
        ComboBoxItem selected = auto;
        bool found = savedId == null;
        foreach (var device in devices)
        {
            var item = new ComboBoxItem { Content = device.Name, Tag = device };
            box.Items.Add(item);
            if (device.Id == savedId)
            {
                selected = item;
                found = true;
            }
        }
        if (!found)
        {
            var gone = new ComboBoxItem { Content = L.T("Device not connected"), Tag = new DeviceInfo(savedId!, L.T("Device not connected"), false) };
            box.Items.Add(gone);
            selected = gone;
        }
        box.SelectedItem = selected;
    }

    /// <summary>The selected device's id; null for the automatic choice.</summary>
    public static string? IdOf(ComboBox box) => ((box.SelectedItem as ComboBoxItem)?.Tag as DeviceInfo)?.Id;
}
