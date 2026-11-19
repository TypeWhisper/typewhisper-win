using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

// Catalog and dictionary contracts ported from the macOS AssemblyAIModelCatalog.swift
// at 803a3a99 (AssemblyAI 1.4.0). No legacy profile is imported.
/// <summary>
/// A transcription model. <c>AsyncModels</c> is <c>speech_models</c> for the async API: the model, then the one it falls back
/// to for languages it does not support. <c>Sync</c> describes the Sync and Dictation APIs, or is null when the model has only
/// the async API. <c>LiveLanguages</c> are the languages a live session can be steered to, or null when the model has no live
/// sessions.
/// </summary>
internal sealed record AssemblyAiModel(string Id, string Name, IReadOnlyList<string> Languages, DictionaryTermsBudget Budget,
    IReadOnlyList<string> AsyncModels, AssemblyAiSyncLimits? Sync = null, IReadOnlyList<string>? LiveLanguages = null)
{
    /// <summary>Dictionary terms go to AssemblyAI as <c>keyterms_prompt</c>; otherwise as <c>word_boost</c>.</summary>
    internal bool UsesKeyterms => Id == AssemblyAiModels.DefaultId;
}

/// <summary>
/// AssemblyAI's Sync API (<c>POST https://sync.assemblyai.com/v1/transcribe</c>) returns the transcript in the same
/// request, without the async API's upload, job and polling round trips. The Dictation API takes the same request.
/// </summary>
/// <param name="Model">The <c>X-AAI-Model</c> header of the Sync API.</param>
/// <param name="Languages">Codes <c>language_codes</c> accepts; any other code is rejected with a 400.</param>
/// <param name="MaxDuration">Longer audio is rejected with a 413.</param>
/// <param name="MaxBytes">Larger files are rejected with a 413.</param>
/// <param name="MaxTerms">At most this many <c>keyterms_prompt</c> entries.</param>
/// <param name="MaxTermCharacters">At most this many characters across all entries.</param>
internal sealed record AssemblyAiSyncLimits(string Model, IReadOnlyList<string> Languages, TimeSpan MaxDuration, long MaxBytes, int MaxTerms,
    int MaxTermCharacters);

/// <summary>
/// How dictations stream and how recordings are transcribed, with AssemblyAI's list prices for AssemblyAI-STT-3.7 and
/// its realtime models, and for Universal-3.6 Pro on the Sync and Dictation APIs.
/// </summary>
internal static class AssemblyAiModes
{
    /// <summary>Dictations stream to the realtime API ($0.45/h or $0.30/h of connection time, by the live model); recordings use the Sync API.</summary>
    internal const string Live = "live";
    /// <summary>Recordings go to the Sync API ($0.30/h); no live preview from AssemblyAI.</summary>
    internal const string Sync = "sync";
    /// <summary>
    /// Dictations go to the Dictation API ($0.62/h), which returns the text cleaned up; recordings and files go to the
    /// Sync API. No live preview from AssemblyAI.
    /// </summary>
    internal const string Dictation = "dictation";
    /// <summary>Recordings go to the async API ($0.21/h); no live preview from AssemblyAI.</summary>
    internal const string Async = "async";
    internal static readonly IReadOnlyList<string> All = [Live, Sync, Dictation, Async];
    internal static string Normalize(string? mode) => All.Contains(mode) ? mode! : Live;
}

/// <summary>
/// The realtime models a live session can stream to. Both take the same 32 languages and up to 100 dictionary terms;
/// the final text is as good with either.
/// </summary>
internal static class AssemblyAiLiveModels
{
    /// <summary>Built for voice agents: the preview grows word by word and rarely changes. $0.45/h.</summary>
    internal const string RealtimeAgent = "assemblyai-stt-3-7-realtime-agent";
    /// <summary>Built for captions and notetakers: cheaper, but the preview is revised more often while you speak. $0.30/h.</summary>
    internal const string Realtime = "assemblyai-stt-3-7-realtime";
    internal static readonly IReadOnlyList<string> All = [RealtimeAgent, Realtime];
    internal static string Normalize(string? model) => All.Contains(model) ? model! : RealtimeAgent;
}

internal static class AssemblyAiModels
{
    internal const string DefaultId = "assemblyai-stt-3-7";
    private const string Universal2Id = "universal-2";
    /// <summary>
    /// Earlier defaults. AssemblyAI deprecated both on November 17, 2026 in favour of AssemblyAI-STT-3.7; saved
    /// selections and workflow models move to it.
    /// </summary>
    internal static readonly IReadOnlyList<string> LegacyDefaultIds = ["universal-3-5-pro", "universal-3-pro"];

    /// <summary>
    /// AssemblyAI-STT-3.7, its realtime models and Universal-3.6 Pro on the Sync and Dictation APIs all take these
    /// 32 languages.
    /// </summary>
    private static readonly IReadOnlyList<string> Stt37Languages = Array.AsReadOnly(new[]
    {
        "en", "es", "de", "fr", "it", "pt", "tr", "nl", "sv", "no", "da", "fi", "hi", "vi", "ar", "he", "ja", "ur", "zh", "ko",
        "ca", "gl", "ru", "ro", "et", "fa", "yue", "af", "mr", "zu", "xh", "nn"
    });

    internal static readonly IReadOnlyList<AssemblyAiModel> All = Array.AsReadOnly(new[]
    {
        new AssemblyAiModel(DefaultId, "AssemblyAI-STT-3.7", Stt37Languages, new(MaxTerms: 1000, MaxWordsPerTerm: 6),
            Array.AsReadOnly(new[] { DefaultId, Universal2Id }),
            // The Sync API runs Universal-3.6 Pro; it routes the retired universal-3-5-pro to it.
            new("universal-3-6-pro", Stt37Languages, TimeSpan.FromMinutes(20), MaxBytes: 256L * 1024 * 1024, MaxTerms: 100,
                MaxTermCharacters: 8000),
            Stt37Languages),
        new AssemblyAiModel(Universal2Id, "Universal-2", Array.AsReadOnly(new[]
        {
            "bg", "ca", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hi", "hr", "hu", "id", "it", "ja", "ko", "lt", "lv",
            "ms", "nl", "no", "pl", "pt", "ro", "ru", "sk", "sl", "sq", "sr", "sv", "th", "tr", "uk", "vi", "zh"
        }), new(MaxTerms: 100, MaxCharsPerTerm: 50), Array.AsReadOnly(new[] { Universal2Id }))
    });

    /// <summary>The model for a saved ID; earlier defaults map to AssemblyAI-STT-3.7, unknown IDs to the default.</summary>
    internal static AssemblyAiModel Resolve(string? id) => Find(id) ?? All[0];

    /// <summary>The model for an explicit selection; earlier defaults map to AssemblyAI-STT-3.7, unknown IDs are rejected.</summary>
    internal static AssemblyAiModel Require(string id) =>
        Find(id) ?? throw new ArgumentException("Unknown AssemblyAI model.", nameof(id));

    private static AssemblyAiModel? Find(string? id) =>
        id is not null && LegacyDefaultIds.Contains(id) ? All[0] : All.FirstOrDefault(m => m.Id == id);

    internal static string? Language(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null : language.Trim().ToLowerInvariant();

    internal static IReadOnlyList<string> Terms(string? prompt, AssemblyAiModel model) =>
        PluginDictionaryTerms.Clip(PluginDictionaryTerms.ParsePrompt(prompt, [',', ';', '\n', '\r']), model.Budget);
}
