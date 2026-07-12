namespace TypeWhisper.Windows.Services;

/// <summary>
/// Adds TypeWhisper purchase attribution to Polar checkout links.
/// </summary>
public static class PolarCheckoutUrlBuilder
{
    /// <summary>
    /// Builds a Polar checkout URL attributed to the Windows app.
    /// </summary>
    public static string BuildAppCheckoutUrl(string baseUrl, string content)
    {
        var builder = new UriBuilder(baseUrl);
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(builder.Query))
            parameters.Add(builder.Query.TrimStart('?'));

        parameters.Add("utm_source=typewhisper_windows");
        parameters.Add("utm_medium=app");
        parameters.Add($"utm_content={Uri.EscapeDataString($"windows_{content}")}");
        builder.Query = string.Join("&", parameters);
        return builder.Uri.AbsoluteUri;
    }
}
