using Microsoft.UI.Xaml;

namespace TypeWhisper.ModuleKit;

/// <summary>A section appended to a settings page, in a card with a heading like the host's own sections.</summary>
/// <param name="Id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
/// <param name="Page">The page the section is appended to.</param>
/// <param name="Title">The section heading.</param>
/// <param name="CreateContent">
/// Creates the content each time the page is shown. If it throws, the section is left out of the page.
/// </param>
public sealed record ModuleSettingsSection(string Id, ModuleSettingsPage Page, string Title, Func<UIElement> CreateContent);

/// <summary>The settings pages that take module sections.</summary>
public enum ModuleSettingsPage
{
    /// <summary>The Premium page, below its overview.</summary>
    Premium,
    /// <summary>The Advanced page, after the host's sections.</summary>
    Advanced
}
