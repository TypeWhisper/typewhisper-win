using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TypeWhisper.WinUI;

// Host-owned branding keeps existing plugin packages compatible with the settings navigation.
public sealed class PluginBrandIcon : UserControl
{
    public static readonly DependencyProperty PluginIdProperty = DependencyProperty.Register(
        nameof(PluginId), typeof(string), typeof(PluginBrandIcon),
        new PropertyMetadata(string.Empty, (sender, _) => ((PluginBrandIcon)sender).Refresh()));

    public string PluginId
    {
        get => (string)GetValue(PluginIdProperty);
        set => SetValue(PluginIdProperty, value);
    }

    internal PluginBrandIcon(string pluginId) : this() => PluginId = pluginId;

    public PluginBrandIcon()
    {
        IsHitTestVisible = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        ActualThemeChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        var brand = PluginId switch
        {
            "com.typewhisper.deepgram" => "deepgram",
            "com.typewhisper.elevenlabs" => "elevenlabs",
            "com.typewhisper.groq" => "groq",
            LocalTranscriptionPlugin.PluginId => "nvidia",
            "com.typewhisper.openai" => "openai",
            _ => null
        };
        if (brand is null) { ShowFallback(); return; }
        var light = ActualTheme == ElementTheme.Light;
        var file = brand switch
        {
            "openai" => light ? "openai-light" : "openai-dark",
            "elevenlabs" when light => "elevenlabs-light",
            _ => brand
        };
        var logo = new Image { Stretch = Stretch.Uniform };
        logo.ImageFailed += (_, _) => { if (ReferenceEquals(Content, logo)) ShowFallback(); };
        Content = logo;
        logo.Source = new SvgImageSource(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "PluginLogos", file + ".svg")));
    }

    private void ShowFallback() => Content = new TypeWhisperGlyph { Kind = "plugin" };
}
