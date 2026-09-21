using System.IO;

namespace TypeWhisper.Plugin.SupertonicTts;

internal static class SupertonicPaths
{
    /// <summary>
    /// Defines the model directory name constant.
    /// </summary>
    public const string ModelDirectoryName = "supertonic-3";
    /// <summary>
    /// Defines the license file name constant.
    /// </summary>
    public const string LicenseFileName = "LICENSE.openrail-m.txt";
    /// <summary>
    /// Defines the source file name constant.
    /// </summary>
    public const string SourceFileName = "SOURCE.txt";

    /// <summary>
    /// Gets the voice style path.
    /// </summary>
    public static string VoiceStylePath(string assetRoot, string voiceId) =>
        Path.Combine(assetRoot, "voice_styles", $"{voiceId}.json");
}
