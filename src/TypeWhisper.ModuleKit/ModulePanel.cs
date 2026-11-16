using Microsoft.UI.Xaml;

namespace TypeWhisper.ModuleKit;

/// <summary>A floating panel that the module shows on request through <see cref="IModuleHost.ShowPanel"/>.</summary>
/// <remarks>
/// The panel floats above other apps, can be moved, has no taskbar entry, closes with Escape and is sized to its
/// content. Closing it hides it; its content is kept for the next show.
/// </remarks>
/// <param name="Id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
/// <param name="Title">The window title, which also names the panel for screen readers.</param>
/// <param name="CreateContent">
/// Creates the content when the panel is first shown, and never before. If it throws, the panel is not shown and the
/// next show tries again.
/// </param>
/// <param name="DidShow">Runs after the panel was shown, or null.</param>
/// <param name="DidHide">Runs after the panel was hidden, also when the user closed it, or null.</param>
public sealed record ModulePanel(string Id, string Title, Func<UIElement> CreateContent, Action? DidShow = null, Action? DidHide = null);
