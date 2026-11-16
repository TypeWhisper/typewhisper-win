namespace TypeWhisper.ModuleKit;

/// <summary>A card in the Premium overview, listed with the host's Premium features.</summary>
/// <param name="Id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
/// <param name="Title">The feature's name.</param>
/// <param name="Summary">One sentence about the feature.</param>
/// <param name="Glyph">A Segoe Fluent Icons code point such as <c>""</c>.</param>
/// <param name="Preview">A line shown while Premium access is missing, or null.</param>
/// <param name="Status">
/// The state shown while Premium access is active, such as "On". Null, or a delegate that returns null, shows the
/// feature as active.
/// </param>
/// <param name="Perform">Opens the feature's settings while Premium access is active, or null for no button.</param>
public sealed record ModulePremiumCard(
    string Id,
    string Title,
    string Summary,
    string Glyph,
    string? Preview = null,
    Func<string?>? Status = null,
    Action? Perform = null);
