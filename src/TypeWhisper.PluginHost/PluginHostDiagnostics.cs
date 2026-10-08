namespace TypeWhisper.PluginHost;

/// <summary>
/// Content-free diagnostic events from the plugin host, routed to the app's local support log.
/// Stages are "event key=value …" with short token values and never carry transcripts, prompts,
/// provider messages or paths; the app's log drops keys it does not list.
/// </summary>
public static class PluginHostDiagnostics
{
    /// <summary>Receives each stage and the exception that caused it; null until the app installs its log.</summary>
    public static Action<string, Exception?>? Sink { get; set; }

    /// <summary>Writes one stage; a failing sink never reaches the request it describes.</summary>
    public static void Write(string stage, Exception? error = null)
    {
        try { Sink?.Invoke(stage, error); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
