namespace TypeWhisper.Presentation;

/// <summary>A captured processor identity and lifetime-checked invocation supplied by the host.</summary>
public sealed record DictationTextProcessor(string PluginId, string Version, int Priority,
    Func<string, CancellationToken, Task<string>> ProcessAsync);
