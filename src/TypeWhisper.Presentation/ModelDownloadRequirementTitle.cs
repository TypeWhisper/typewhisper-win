namespace TypeWhisper.Presentation;

/// <summary>Labels the credential fields of a plugin's model download requirements.</summary>
public static class ModelDownloadRequirementTitle
{
    /// <summary>
    /// Returns the plugin's <paramref name="title"/>, marked as optional when the requirement is not required. A title
    /// that already ends with the marker, in English or in the interface language, is shown as the plugin wrote it.
    /// </summary>
    public static string For(string title, bool isRequired) =>
        isRequired || EndsWithOptionalMarker(title) ? title : Loc.T("{0} (optional)", title);

    // Plugins choose their texts from the interface language and fall back to English.
    private static bool EndsWithOptionalMarker(string title)
    {
        var trimmed = title.TrimEnd();
        return trimmed.EndsWith("(optional)", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(Loc.T("(optional)"), StringComparison.OrdinalIgnoreCase);
    }
}
