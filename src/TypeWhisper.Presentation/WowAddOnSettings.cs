using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>
/// The settings the player chose on the add-on's page in World of Warcraft. Add-ons cannot reach TypeWhisper, so
/// TypeWhisper reads the file the game saves them in. The game writes it when the player types /reload or logs out,
/// so a change reaches TypeWhisper only then.
/// </summary>
/// <param name="Send">Whether Enter sends each message.</param>
/// <param name="ShowBar">Whether the add-on's cast bar shows the dictation, in place of TypeWhisper's indicator.</param>
/// <param name="TranslateTo">The language messages are translated into, in English, or null to keep the dictated language.</param>
public sealed partial record WowAddOnSettings(bool Send = true, bool ShowBar = true, string? TranslateTo = null)
{
    private const string FileName = WowAddOn.FolderName + ".lua";
    private const int MaxTranslationTarget = 64;

    /// <summary>The add-on's defaults, used before the game first saves them.</summary>
    public static WowAddOnSettings Default { get; } = new();

    /// <summary>
    /// The chosen translation as an App workflow for the game, or null without one. It ranks last among the game's App
    /// rules, so a workflow of the player's own for the game still wins, while it beats a Global fallback. The shared
    /// workflow LLM translates.
    /// </summary>
    public Workflow? TranslationWorkflow() => TranslateTo is null ? null : new Workflow
    {
        Id = "wow-addon-translation", Name = Loc.T("World of Warcraft translation"), Template = WorkflowTemplate.Translation,
        SortOrder = int.MaxValue, Trigger = WorkflowTrigger.App([.. GameCatalog.WorldOfWarcraft.ProcessNames]),
        Behavior = new() { TranslationTarget = TranslateTo, ProviderOverride = WorkflowLlmDefaults.Inherit }
    };

    /// <summary>
    /// Reads the newest saved settings of the game version in <paramref name="versionFolder"/>, such as "_retail_".
    /// Several game accounts each have their own file; the one saved last wins. Missing or unreadable files give the defaults.
    /// </summary>
    public static WowAddOnSettings Read(string? versionFolder)
    {
        if (string.IsNullOrWhiteSpace(versionFolder)) return Default;
        try
        {
            var accounts = Path.Combine(versionFolder, "WTF", "Account");
            if (!Directory.Exists(accounts)) return Default;
            var newest = Directory.EnumerateDirectories(accounts)
                .Select(account => new FileInfo(Path.Combine(account, "SavedVariables", FileName)))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest is null ? Default : Parse(File.ReadAllText(newest.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Default;
        }
    }

    /// <summary>
    /// Reads the add-on's top-level values from a saved variables file. The game writes one <c>["key"] = value,</c> per line;
    /// unknown keys and values are ignored.
    /// </summary>
    public static WowAddOnSettings Parse(string savedVariables)
    {
        var settings = Default;
        foreach (Match match in ValueLine().Matches(savedVariables))
        {
            var value = match.Groups["value"].Value;
            var text = match.Groups["text"].Success ? match.Groups["text"].Value : null;
            settings = match.Groups["key"].Value switch
            {
                "send" when value is "true" or "false" => settings with { Send = value == "true" },
                "showBar" when value is "true" or "false" => settings with { ShowBar = value == "true" },
                "translateTo" when text is { Length: > 0 and <= MaxTranslationTarget } && text.All(c => char.IsLetter(c) || c is ' ' or '-')
                    => settings with { TranslateTo = text },
                _ => settings
            };
        }
        return settings;
    }

    [GeneratedRegex("""^\s*\["(?<key>send|showBar|translateTo)"\]\s*=\s*(?<value>true|false|"(?<text>[^"\\\r\n]*)")\s*,?\s*$""", RegexOptions.Multiline)]
    private static partial Regex ValueLine();
}
