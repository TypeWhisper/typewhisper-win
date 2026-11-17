using System.Text.RegularExpressions;
using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// The contributions the host binds, after validation. A contribution with an invalid identifier, a duplicate one
// (per kind, ignoring case) or missing required parts is left out; each is reported once with its kind and reason.
internal sealed partial class ModuleContributionCatalog
{
    internal const string RejectedStage = "modules.contribution.rejected";

    private ModuleContributionCatalog() { }

    internal static ModuleContributionCatalog Empty { get; } = new();

    internal IReadOnlyList<ModuleHotkeyAction> HotkeyActions { get; private init; } = [];
    internal IReadOnlyList<ModuleMenuItem> MenuItems { get; private init; } = [];
    internal IReadOnlyList<ModulePanel> Panels { get; private init; } = [];
    internal IReadOnlyList<ModuleRecordAction> RecordActions { get; private init; } = [];
    internal IReadOnlyList<ModulePremiumCard> PremiumCards { get; private init; } = [];
    internal IReadOnlyList<ModuleSettingsSection> SettingsSections { get; private init; } = [];
    internal IReadOnlyList<ModuleFileTranscriptionOption> FileTranscriptionOptions { get; private init; } = [];
    internal IReadOnlyList<ModuleFileTranscriptionHook> FileTranscriptionHooks { get; private init; } = [];
    internal IReadOnlyList<ModuleHistorySidebarGroup> HistorySidebarGroups { get; private init; } = [];

    internal IEnumerable<ModuleSettingsSection> SectionsFor(ModuleSettingsPage page) => SettingsSections.Where(section => section.Page == page);

    // The settings key a section's card carries, so Settings can bring it into view like one of its own rows.
    internal static string SettingsSectionKey(string sectionId) => "module-section." + sectionId;

    // The settings category of the page a section is appended to.
    internal static string SettingsCategory(ModuleSettingsPage page) => page switch
    {
        ModuleSettingsPage.Advanced => "Advanced",
        _ => "Premium"
    };

    // Where the section with this identifier is shown, or null for an unknown one.
    internal (string Category, string Key)? SettingsSectionTarget(string? sectionId) =>
        sectionId is null ? null
        : SettingsSections.FirstOrDefault(section => string.Equals(section.Id, sectionId, StringComparison.OrdinalIgnoreCase)) is { } found
            ? (SettingsCategory(found.Page), SettingsSectionKey(found.Id)) : null;

    // Letters, digits, dots, hyphens and underscores, as on macOS. Shortcut files are named after these identifiers.
    internal static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    // The stage written for a rejected contribution. Identifiers that failed validation are left out of the log.
    internal static string RejectionStage(string kind, string? id, string reason) =>
        IsValidId(id) ? $"{RejectedStage} kind={kind} id={id} reason={reason}" : $"{RejectedStage} kind={kind} reason={reason}";

    internal static ModuleContributionCatalog Resolve(ModuleContributions contributions, Action<string> report)
    {
        var hotkeys = Accept("hotkey-action", contributions.HotkeyActions, item => item.Id,
            item => !string.IsNullOrWhiteSpace(item.Title) && item.Perform is not null, report, ModuleHotkeys.MaximumCount);
        var hotkeyIds = hotkeys.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var menuItems = Accept("menu-item", contributions.MenuItems, item => item.Id,
            item => !string.IsNullOrWhiteSpace(item.Title) && item.Perform is not null, report)
            .Select(item =>
            {
                if (item.HotkeyActionId is null || hotkeyIds.Contains(item.HotkeyActionId)) return item;
                report(RejectionStage("menu-item", item.Id, "unknown-hotkey-action"));
                return item with { HotkeyActionId = null };
            }).ToArray();
        return new()
        {
            HotkeyActions = hotkeys,
            MenuItems = menuItems,
            Panels = Accept("panel", contributions.Panels, item => item.Id,
                item => !string.IsNullOrWhiteSpace(item.Title) && item.CreateContent is not null, report),
            RecordActions = Accept("record-action", contributions.RecordActions, item => item.Id,
                item => !string.IsNullOrWhiteSpace(item.Title) && item.Perform is not null && item.MaximumRecordCount >= 1, report),
            PremiumCards = Accept("premium-card", contributions.PremiumCards, item => item.Id,
                item => !string.IsNullOrWhiteSpace(item.Title) && item.Summary is not null, report),
            SettingsSections = Accept("settings-section", contributions.SettingsSections, item => item.Id,
                item => !string.IsNullOrWhiteSpace(item.Title) && item.CreateContent is not null && Enum.IsDefined(item.Page), report),
            FileTranscriptionOptions = Accept("file-transcription-option", contributions.FileTranscriptionOptions, item => item.Id,
                item => item.CreateContent is not null, report),
            FileTranscriptionHooks = Accept("file-transcription-hook", contributions.FileTranscriptionCompleted, item => item.Id,
                item => item.Completed is not null, report),
            HistorySidebarGroups = Accept("history-sidebar-group", contributions.HistorySidebarGroups, item => item.Id,
                item => !string.IsNullOrWhiteSpace(item.Title) && item.Entries is not null && item.CreateView is not null, report)
        };
    }

    private static T[] Accept<T>(string kind, IEnumerable<T?> items, Func<T, string?> id, Func<T, bool> complete, Action<string> report,
        int maximum = int.MaxValue) where T : class
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<T>();
        foreach (var item in items)
        {
            // Only a contribution that would be accepted takes its identifier, so a broken one cannot block a later one.
            var reason = item is null ? "missing"
                : !IsValidId(id(item)) ? "invalid-id"
                : !complete(item) ? "incomplete"
                : accepted.Count >= maximum ? "limit"
                : !seen.Add(id(item)!) ? "duplicate"
                : null;
            if (reason is null) accepted.Add(item!);
            else report(RejectionStage(kind, item is null ? null : id(item), reason));
        }
        return accepted.ToArray();
    }

    // \z, because $ would also accept a trailing line break.
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
