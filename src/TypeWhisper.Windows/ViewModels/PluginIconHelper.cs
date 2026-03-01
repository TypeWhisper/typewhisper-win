namespace TypeWhisper.Windows.ViewModels;

internal static class PluginIconHelper
{
    public static string GetIcon(string pluginId) => pluginId switch
    {
        "com.typewhisper.groq" => "\U0001F4A8",           // Groq - lightning fast
        "com.typewhisper.openai" => "\U0001F916",          // OpenAI - robot
        "com.typewhisper.openai-compatible" => "\U0001F310", // OpenAI Compatible - globe
        "com.typewhisper.sherpa-onnx" => "\U0001F3AF",     // SherpaOnnx - local/target
        "com.typewhisper.webhook" => "\U0001F517",         // Webhook - link
        _ => "\U0001F9E9"                                   // Default - puzzle piece
    };

    public static string GetGradientStart(string pluginId) => pluginId switch
    {
        "com.typewhisper.groq" => "#F55036",
        "com.typewhisper.openai" => "#10A37F",
        "com.typewhisper.openai-compatible" => "#6366F1",
        "com.typewhisper.sherpa-onnx" => "#F59E0B",
        "com.typewhisper.webhook" => "#8B5CF6",
        _ => "#0078D4"
    };

    public static string GetGradientEnd(string pluginId) => pluginId switch
    {
        "com.typewhisper.groq" => "#C0392B",
        "com.typewhisper.openai" => "#0D8A6A",
        "com.typewhisper.openai-compatible" => "#4F46E5",
        "com.typewhisper.sherpa-onnx" => "#D97706",
        "com.typewhisper.webhook" => "#7C3AED",
        _ => "#005A9E"
    };
}
