using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Presentation;

/// <summary>Validates the development launcher's publication receipt without inferring production identity from build configuration.</summary>
public static class StartupPublication
{
    /// <summary>The development registration identity, distinct from the installed Windows product.</summary>
    public const string DevelopmentIdentity = "TypeWhisper.WinUI.Dev";
    /// <summary>The receipt written only after the development launcher successfully publishes the app.</summary>
    public const string ReceiptFileName = "typewhisper-dev-publication.json";
    private sealed record Receipt(
        [property: JsonRequired] int Version,
        [property: JsonRequired] string Identity,
        [property: JsonRequired] string SourceRoot,
        [property: JsonRequired] string OutputDirectory);

    /// <summary>Returns the exact persistent executable target, or throws when the receipt is invalid or the output was copied elsewhere.</summary>
    public static string Validate(string json, string processPath)
    {
        if (json.Length > 16_384) throw new InvalidDataException("The development publication receipt is too large.");
        using var document = JsonDocument.Parse(json);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate publication receipt field.");
        var receipt = document.RootElement.Deserialize<Receipt>(new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Missing publication receipt.");
        if (receipt.Version != 1 || receipt.Identity != DevelopmentIdentity ||
            string.IsNullOrWhiteSpace(receipt.SourceRoot) || !Path.IsPathFullyQualified(receipt.SourceRoot) ||
            string.IsNullOrWhiteSpace(receipt.OutputDirectory) || !Path.IsPathFullyQualified(receipt.OutputDirectory) ||
            !Path.IsPathFullyQualified(processPath))
            throw new InvalidDataException("This is not a recognized development publication.");
        var expected = Path.GetFullPath(Path.Combine(receipt.OutputDirectory, "TypeWhisper.exe"));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(receipt.SourceRoot));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(receipt.OutputDirectory));
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Startup cannot target a checkout or its build artifacts.");
        if (!string.Equals(expected, Path.GetFullPath(processPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Start this development build from its published output before enabling startup.");
        return expected;
    }
}
