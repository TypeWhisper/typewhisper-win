namespace TypeWhisper.Presentation;

/// <summary>One supported HTTP method and exact URL path.</summary>
/// <param name="Method">Uppercase HTTP method.</param>
/// <param name="Path">Case-sensitive absolute URL path.</param>
public sealed record LocalApiRoute(string Method, string Path);

/// <summary>The shared macOS API contract and additional Windows discovery/documentation routes.</summary>
public static class LocalApiRouteCatalog
{
    /// <summary>The 29 method/path pairs shared with the macOS API.</summary>
    public static IReadOnlyList<LocalApiRoute> MacRoutes { get; } = Array.AsReadOnly<LocalApiRoute>([
        new("POST", "/v1/transcribe"), new("POST", "/v1/transcribe/local-file"),
        new("GET", "/v1/status"), new("GET", "/v1/models"),
        new("POST", "/v1/models/load"), new("POST", "/v1/models/unload"), new("DELETE", "/v1/models"),
        new("GET", "/v1/history"), new("DELETE", "/v1/history"),
        new("GET", "/v1/rules"), new("PUT", "/v1/rules/toggle"),
        new("GET", "/v1/profiles"), new("PUT", "/v1/profiles/toggle"),
        new("POST", "/v1/dictation/start"), new("POST", "/v1/dictation/stop"),
        new("GET", "/v1/dictation/status"), new("GET", "/v1/dictation/transcription"),
        new("POST", "/v1/recorder/start"), new("POST", "/v1/recorder/stop"),
        new("GET", "/v1/recorder/status"), new("GET", "/v1/recorder/session"),
        new("GET", "/v1/dictionary/terms"), new("PUT", "/v1/dictionary/terms"), new("DELETE", "/v1/dictionary/terms"),
        new("GET", "/v1/dictionary/corrections"), new("PUT", "/v1/dictionary/corrections"), new("DELETE", "/v1/dictionary/corrections"),
        new("GET", "/v1/settings/export"), new("POST", "/v1/settings/import")
    ]);

    /// <summary>All concrete routes, including capabilities and both documentation URL spellings.</summary>
    public static IReadOnlyList<LocalApiRoute> Routes { get; } = Array.AsReadOnly<LocalApiRoute>([
        .. MacRoutes, new("GET", "/v1/capabilities"), new("GET", "/docs"), new("GET", "/docs/")
    ]);

    /// <summary>Checks an exact method/path pair; OPTIONS preflight is handled by the transport.</summary>
    public static bool Contains(string method, string path) => Routes.Any(route => route.Method == method && route.Path == path);
}
