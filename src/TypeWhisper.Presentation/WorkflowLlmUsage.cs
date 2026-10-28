namespace TypeWhisper.Presentation;

/// <summary>Records which LLM a dictation workflow used and how long it took, for the History details.</summary>
public sealed class WorkflowLlmUsage
{
    private readonly Func<string, string, (string Provider, string Model)> _names;
    private readonly TimeProvider _time;

    /// <summary>Creates a recorder that resolves provider and model IDs to display names with <paramref name="names"/>.</summary>
    public WorkflowLlmUsage(Func<string, string, (string Provider, string Model)> names, TimeProvider? time = null)
    {
        _names = names;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The display name of the provider that was called, or null when no LLM ran.</summary>
    public string? Provider { get; private set; }
    /// <summary>The display name of the model that was called, or null when no LLM ran.</summary>
    public string? Model { get; private set; }
    /// <summary>Seconds the LLM took until it answered, including queueing and retries; null when it failed or did not run.</summary>
    public double? Seconds { get; private set; }

    /// <summary>Wraps an LLM call so its provider, model and duration are recorded.</summary>
    public Func<string, string, string, string, CancellationToken, Task<string>> Wrap(
        Func<string, string, string, string, CancellationToken, Task<string>> process) =>
        async (provider, prompt, text, model, ct) =>
        {
            var names = _names(provider, model);
            Provider = string.IsNullOrWhiteSpace(names.Provider) ? provider : names.Provider;
            Model = string.IsNullOrWhiteSpace(names.Model) ? model : names.Model;
            var started = _time.GetTimestamp();
            var result = await process(provider, prompt, text, model, ct);
            Seconds = (Seconds ?? 0) + _time.GetElapsedTime(started).TotalSeconds;
            return result;
        };
}
