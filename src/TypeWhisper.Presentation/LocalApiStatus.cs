namespace TypeWhisper.Presentation;

/// <summary>The GET /v1/status payload, field for field as the macOS app sends it.</summary>
public static class LocalApiStatus
{
    /// <summary>Builds the status response. Clients such as the browser extension read the supports_* flags
    /// to tell people when their TypeWhisper version is too old.</summary>
    public static LocalApiResponse Response(bool ready, string? engine, string? model, string apiVersion,
        bool supportsStreaming, bool supportsTranslation) => LocalApiResponse.Json(200, new
        {
            status = ready ? "ready" : "no_model", engine, model, api_version = apiVersion, supports_workflow_dictation = true,
            supports_streaming = supportsStreaming, supports_translation = supportsTranslation, supports_browser_context = true
        });
}
