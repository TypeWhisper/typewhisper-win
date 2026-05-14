using System.IO;

namespace TypeWhisper.Windows.ViewModels;

internal static class PluginIconHelper
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly IReadOnlyDictionary<string, string> LogoFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["com.typewhisper.openai"] = "openai.png",
        ["com.typewhisper.groq"] = "groq.png",
        ["com.typewhisper.xai"] = "xai.png",
        ["com.typewhisper.gemini"] = "gemini.png",
        ["com.typewhisper.claude"] = "claude.png",
        ["com.typewhisper.cohere"] = "cohere.png"
    };

    public static string? GetLogoPath(string pluginId, string? baseDirectory = null)
    {
        if (!LogoFileNames.TryGetValue(pluginId, out var fileName))
            return null;

        var resolvedBaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var candidate = Path.Combine(
            resolvedBaseDirectory,
            "Resources",
            "PluginLogos",
            fileName);

        return IsReadablePng(candidate) ? candidate : null;
    }

    public static string GetIcon(string pluginId) => pluginId switch
    {
        "com.typewhisper.groq" => "\U0001F4A8",           // Groq - lightning fast
        "com.typewhisper.xai" => "\u2728",                 // xAI / Grok - sparkles
        "com.typewhisper.openai" => "\U0001F916",          // OpenAI - robot
        "com.typewhisper.openai-compatible" => "\U0001F310", // OpenAI Compatible - globe
        "com.typewhisper.sherpa-onnx" => "\U0001F3AF",     // SherpaOnnx - local/target
        "com.typewhisper.elevenlabs" => "\U0001F399",       // ElevenLabs - studio microphone
        "com.typewhisper.supertonic-tts" => "\U0001F50A",   // Supertonic TTS - speaker
        "com.typewhisper.webhook" => "\U0001F517",         // Webhook - link
        _ => "\U0001F9E9"                                   // Default - puzzle piece
    };

    public static string GetGradientStart(string pluginId) => pluginId switch
    {
        "com.typewhisper.groq" => "#F55036",
        "com.typewhisper.xai" => "#111827",
        "com.typewhisper.openai" => "#10A37F",
        "com.typewhisper.openai-compatible" => "#6366F1",
        "com.typewhisper.sherpa-onnx" => "#F59E0B",
        "com.typewhisper.elevenlabs" => "#111827",
        "com.typewhisper.supertonic-tts" => "#00A6A6",
        "com.typewhisper.webhook" => "#8B5CF6",
        _ => "#0078D4"
    };

    public static string GetGradientEnd(string pluginId) => pluginId switch
    {
        "com.typewhisper.groq" => "#C0392B",
        "com.typewhisper.xai" => "#1D4ED8",
        "com.typewhisper.openai" => "#0D8A6A",
        "com.typewhisper.openai-compatible" => "#4F46E5",
        "com.typewhisper.sherpa-onnx" => "#D97706",
        "com.typewhisper.elevenlabs" => "#F97316",
        "com.typewhisper.supertonic-tts" => "#2563EB",
        "com.typewhisper.webhook" => "#7C3AED",
        _ => "#005A9E"
    };

    private static bool IsReadablePng(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != signature.Length)
                return false;

            for (var i = 0; i < signature.Length; i++)
            {
                if (signature[i] != PngSignature[i])
                    return false;
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
