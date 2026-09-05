using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// Keep the WinUI switch's native pointer, drag, keyboard and automation behavior.
internal static class PrototypeToggleSwitch
{
    internal static ToggleSwitch Create(bool isOn)
    {
        var control = new ToggleSwitch { IsOn = isOn };
        Configure(control);
        return control;
    }

    internal static void Configure(ToggleSwitch control)
    {
        control.OnContent = control.OffContent = string.Empty;
        control.MinWidth = 0;
        control.Width = control.MaxWidth = 52;
        control.MinHeight = 36;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            foreach (var state in new[] { "", "PointerOver", "Pressed" })
            {
                control.Resources[$"ToggleSwitchFillOn{state}"] = (Brush)Application.Current.Resources["AccentBrush"];
                control.Resources[$"ToggleSwitchKnobFillOn{state}"] = (Brush)Application.Current.Resources["InkBrush"];
            }
        }
    }
}
