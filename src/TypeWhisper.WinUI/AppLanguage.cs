using System.Globalization;
using TypeWhisper.Core;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// The interface language is fixed for the lifetime of the process: most text is created once in code, so a new
// choice applies after a restart, as on macOS.
internal static class AppLanguage
{
    internal static InterfaceLanguageStore Store { get; } = new(WinUIProfile.DataPath("interface-language.json"));

    /// <summary>Applies the saved language, or the Windows display language on first start. Call before any UI exists.</summary>
    internal static void Apply()
    {
        Loc.Use(Loc.Resolve(Store.Saved, CultureInfo.CurrentUICulture));
        // Text the Windows controls bring themselves, such as the text box context menu, follows this override.
        try { Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Loc.Language; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceWarning("The language of the Windows controls could not be set: {0}", ex.Message); }
    }
}
