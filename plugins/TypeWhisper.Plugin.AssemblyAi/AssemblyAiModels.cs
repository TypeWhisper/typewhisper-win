using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

// Catalog and dictionary contracts ported from the macOS AssemblyAIModelCatalog.swift
// at 15ddecc2cd1af24fdb9906ed04400c5a75cc09b1. No legacy profile is imported.
internal sealed record AssemblyAiModel(string Id, string Name, IReadOnlyList<string> Languages, DictionaryTermsBudget Budget,
    AssemblyAiSyncLimits? Sync = null)
{
    internal bool IsPro => Id == AssemblyAiModels.DefaultId;
}

/// <summary>
/// AssemblyAI's Sync API (<c>POST https://sync.assemblyai.com/v1/transcribe</c>) returns the transcript in the same
/// request, without the async API's upload, job and polling round trips.
/// </summary>
/// <param name="Languages">Codes <c>language_codes</c> accepts; any other code is rejected with a 400.</param>
/// <param name="MaxDuration">Longer audio is rejected with a 413.</param>
/// <param name="MaxTerms">At most this many <c>keyterms_prompt</c> entries.</param>
/// <param name="MaxTermCharacters">At most this many characters across all entries.</param>
internal sealed record AssemblyAiSyncLimits(IReadOnlyList<string> Languages, TimeSpan MaxDuration, int MaxTerms, int MaxTermCharacters);

/// <summary>How dictations stream and how recordings are transcribed, with AssemblyAI's list price for Universal-3.5 Pro.</summary>
internal static class AssemblyAiModes
{
    /// <summary>Dictations stream to the realtime API ($0.45/h of connection time); recordings use the Sync API.</summary>
    internal const string Live = "live";
    /// <summary>Recordings go to the Sync API ($0.30/h); no live preview from AssemblyAI.</summary>
    internal const string Sync = "sync";
    /// <summary>Recordings go to the async API ($0.21/h); no live preview from AssemblyAI.</summary>
    internal const string Async = "async";
    internal static readonly IReadOnlyList<string> All = [Live, Sync, Async];
    internal static string Normalize(string? mode) => All.Contains(mode) ? mode! : Live;
}

internal static class AssemblyAiModels
{
    internal const string DefaultId = "universal-3-5-pro";
    internal static readonly IReadOnlyList<string> StreamingLanguages = Array.AsReadOnly(new[] { "en", "es", "de", "fr", "pt", "it" });
    internal static readonly IReadOnlyList<AssemblyAiModel> All = Array.AsReadOnly(new[]
    {
        new AssemblyAiModel(DefaultId, "Universal-3.5 Pro", Array.AsReadOnly(new[]
        {
            "en", "es", "fr", "de", "it", "pt", "tr", "nl", "sv", "no", "da", "fi", "hi", "vi", "ar", "he", "ja", "zh"
        }), new(MaxTerms: 1000, MaxWordsPerTerm: 6), new(Array.AsReadOnly(new[]
        {
            "en", "es", "de", "fr", "it", "pt", "tr", "nl", "sv", "no", "da", "fi", "hi", "vi", "ar", "he", "ja", "ur", "zh", "ko",
            "ca", "gl", "ru", "ro", "et", "fa", "yue", "af", "mr", "zu", "xh", "nn"
        }), TimeSpan.FromMinutes(20), MaxTerms: 100, MaxTermCharacters: 8000)),
        new AssemblyAiModel("universal-2", "Universal-2", Array.AsReadOnly(new[]
        {
            "bg", "ca", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hi", "hr", "hu", "id", "it", "ja", "ko", "lt", "lv",
            "ms", "nl", "no", "pl", "pt", "ro", "ru", "sk", "sl", "sq", "sr", "sv", "th", "tr", "uk", "vi", "zh"
        }), new(MaxTerms: 100, MaxCharsPerTerm: 50))
    });

    internal static AssemblyAiModel Resolve(string? id) =>
        All.FirstOrDefault(m => m.Id == id) ?? All[0];

    internal static AssemblyAiModel Require(string id) =>
        id == "universal-3-pro" ? All[0] : All.FirstOrDefault(m => m.Id == id)
        ?? throw new ArgumentException("Unknown AssemblyAI model.", nameof(id));

    internal static string? Language(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null : language.Trim().ToLowerInvariant();

    internal static IReadOnlyList<string> Terms(string? prompt, AssemblyAiModel model) =>
        PluginDictionaryTerms.Clip(PluginDictionaryTerms.ParsePrompt(prompt, [',', ';', '\n', '\r']), model.Budget);
}
