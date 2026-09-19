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
            "com.typewhisper.assemblyai" => "assemblyai",
            "com.typewhisper.cerebras" => "cerebras",
            "com.typewhisper.cloudflare-asr" => "cloudflare",
            "com.typewhisper.claude" => "claude",
            "com.typewhisper.cohere" => "cohere",
            "com.typewhisper.deepgram" => "deepgram",
            "com.typewhisper.elevenlabs" => "elevenlabs",
            "com.typewhisper.fireworks" => "fireworks",
            "com.typewhisper.gemini" => "gemini",
            "com.typewhisper.gladia" => "gladia",
            "com.typewhisper.groq" => "groq",
            "com.typewhisper.github-copilot" => "github-copilot",
            LocalTranscriptionPlugin.PluginId => "nvidia",
            "com.typewhisper.openai" => "openai",
            "com.typewhisper.openrouter" => "openrouter",
            "com.typewhisper.soniox" => "soniox",
            "com.typewhisper.reson8" => "reson8",
            "com.typewhisper.speechmatics" => "speechmatics",
            _ => null
        };
        if (brand is null) { ShowFallback(); return; }
        var light = ActualTheme == ElementTheme.Light;
        var file = brand switch
        {
            "assemblyai" => light ? "assemblyai-light" : "assemblyai-dark",
            "openai" => light ? "openai-light" : "openai-dark",
            "github-copilot" => light ? "github-copilot-light" : "github-copilot-dark",
            "openrouter" => light ? "openrouter-light" : "openrouter-dark",
            "speechmatics" => light ? "speechmatics-light" : "speechmatics-dark",
            "elevenlabs" when light => "elevenlabs-light",
            _ => brand
        };
        var logo = new Image { Stretch = Stretch.Uniform };
        logo.ImageFailed += (_, _) => { if (ReferenceEquals(Content, logo)) ShowFallback(); };
        Content = logo;
        var raster = brand is "claude" or "gemini" or "soniox" or "reson8";
        var uri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "PluginLogos", file + (raster ? ".png" : ".svg")));
        logo.Source = raster ? new BitmapImage(uri) : new SvgImageSource(uri);
    }

    private void ShowFallback() => Content = new TypeWhisperGlyph
    {
        Kind = PluginId == "com.typewhisper.whisper-cpp" ? "chip" : "plugin"
    };
}
