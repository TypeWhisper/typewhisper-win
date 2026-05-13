using System.IO;

namespace TypeWhisper.Plugin.SupertonicTts;

internal static class SupertonicPaths
{
    public const string ModelDirectoryName = "supertonic-3";
    public const string LicenseFileName = "LICENSE.openrail-m.txt";
    public const string SourceFileName = "SOURCE.txt";

    public static string VoiceStylePath(string assetRoot, string voiceId) =>
        Path.Combine(assetRoot, "voice_styles", $"{voiceId}.json");
}
