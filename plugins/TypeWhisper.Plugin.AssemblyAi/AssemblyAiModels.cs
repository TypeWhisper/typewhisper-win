using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

// Catalog and dictionary contracts ported from the macOS AssemblyAIModelCatalog.swift
// at 15ddecc2cd1af24fdb9906ed04400c5a75cc09b1. No legacy profile is imported.
internal sealed record AssemblyAiModel(string Id, string Name, IReadOnlyList<string> Languages, DictionaryTermsBudget Budget)
{
    internal bool IsPro => Id == AssemblyAiModels.DefaultId;
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
        }), new(MaxTerms: 1000, MaxWordsPerTerm: 6)),
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
