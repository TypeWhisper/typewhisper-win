using System.Diagnostics;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Extracts lasting personal facts from transcriptions using an LLM provider.
/// Facts are stored via IMemoryStoragePlugin and can be injected into prompts.
/// </summary>
public sealed class MemoryService
{
    private const int MinTextLength = 30;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly PluginManager _pluginManager;
    private DateTime _lastExtraction = DateTime.MinValue;

    private const string ExtractionPrompt = """
        Extract any lasting personal facts from the following transcribed speech.
        Facts include: names, job titles, preferences, locations, relationships,
        projects, tools used, responsibilities, or recurring topics.

        Return ONLY the facts as a bullet list (one per line, starting with "- ").
        If there are no lasting facts, return exactly "NONE".
        Do not include temporary information like meeting times or deadlines.
        """;

    /// <summary>
    /// Gets or sets the is enabled value.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Initializes a new instance of the MemoryService class.
    /// </summary>
    public MemoryService(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
    }

    /// <summary>
    /// Attempts to extract and store memories from the given transcription text.
    /// Respects cooldown and minimum text length. No-ops if no LLM or memory plugin available.
    /// </summary>
    public async Task ExtractAndStoreAsync(string text, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinTextLength) return;
        if (DateTime.UtcNow - _lastExtraction < Cooldown) return;

        var memoryPlugin = _pluginManager.GetPlugins<IMemoryStoragePlugin>().FirstOrDefault();
        if (memoryPlugin is null) return;

        var llm = _pluginManager.LlmProviders.FirstOrDefault(p => p.IsAvailable);
        if (llm is null) return;

        var model = llm.SupportedModels.FirstOrDefault()?.Id;
        if (model is null) return;

        try
        {
            _lastExtraction = DateTime.UtcNow;

            var result = await llm.ProcessAsync(ExtractionPrompt, text, model, ct);
            if (string.IsNullOrWhiteSpace(result) || result.Trim() == "NONE") return;

            var facts = result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("- "))
                .Select(line => line[2..].Trim())
                .Where(fact => fact.Length > 5);

            foreach (var fact in facts)
            {
                await memoryPlugin.StoreAsync(fact, ct);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MemoryService extraction failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves relevant memories for the given context, for injection into prompts.
    /// </summary>
    public async Task<string?> GetContextAsync(string query, CancellationToken ct = default)
    {
        if (!IsEnabled) return null;

        var memoryPlugin = _pluginManager.GetPlugins<IMemoryStoragePlugin>().FirstOrDefault();
        if (memoryPlugin is null) return null;

        try
        {
            var memories = await memoryPlugin.SearchAsync(query, 10, ct);
            if (memories.Count == 0) return null;
            return string.Join("\n", memories.Select(m => $"- {m}"));
        }
        catch
        {
            return null;
        }
    }
}
