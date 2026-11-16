using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// A window that shows one module panel. Hide keeps it and its content for the next Show; Close ends it.
internal interface IModulePanelSurface
{
    bool IsVisible { get; }
    // Shows the panel, or brings it to the front when it is visible.
    void Show();
    void Hide();
    void Close();
}

// The module panels and their windows. A window, and with it the module's content, is created on the first show
// only. Module callbacks are guarded; unknown identifiers are reported and ignored.
internal sealed class ModulePanels
{
    internal const string UnknownStage = "modules.panel.unknown";
    private readonly Dictionary<string, ModulePanel> _panels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IModulePanelSurface> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    // Creates the window for a panel, or returns null when its content could not be created. The callback is for
    // the window to call after it hid itself, for example on Escape.
    private readonly Func<ModulePanel, Action, IModulePanelSurface?> _open;
    private readonly Action<string> _report;
    private readonly Action<string, Exception> _reportFailure;
    private bool _closed;

    internal ModulePanels(IEnumerable<ModulePanel> panels, Func<ModulePanel, Action, IModulePanelSurface?> open,
        Action<string> report, Action<string, Exception> reportFailure)
    {
        foreach (var panel in panels) _panels.TryAdd(panel.Id, panel);
        _open = open;
        _report = report;
        _reportFailure = reportFailure;
    }

    internal void Show(string id)
    {
        if (_closed || Find(id) is not { } panel) return;
        if (!_surfaces.TryGetValue(panel.Id, out var surface))
        {
            surface = _open(panel, () => DidHide(panel));
            if (surface is null) return;
            _surfaces.Add(panel.Id, surface);
        }
        var wasVisible = surface.IsVisible;
        surface.Show();
        if (!wasVisible && surface.IsVisible && panel.DidShow is { } didShow) ModuleCalls.Run("panel.did-show", panel.Id, didShow, _reportFailure);
    }

    internal void Hide(string id)
    {
        if (_closed || Find(id) is not { } panel || !_surfaces.TryGetValue(panel.Id, out var surface) || !surface.IsVisible) return;
        surface.Hide();
        DidHide(panel);
    }

    internal void Toggle(string id)
    {
        if (IsVisible(id)) Hide(id);
        else Show(id);
    }

    // Queries are not reported, so a module can poll without filling the log.
    internal bool IsVisible(string id) => !_closed && id is not null && _surfaces.TryGetValue(id, out var surface) && surface.IsVisible;

    // At shutdown. Later requests do nothing.
    internal void CloseAll()
    {
        if (_closed) return;
        _closed = true;
        foreach (var surface in _surfaces.Values) surface.Close();
        _surfaces.Clear();
    }

    private void DidHide(ModulePanel panel)
    {
        if (!_closed && panel.DidHide is { } didHide) ModuleCalls.Run("panel.did-hide", panel.Id, didHide, _reportFailure);
    }

    private ModulePanel? Find(string id)
    {
        if (id is not null && _panels.TryGetValue(id, out var panel)) return panel;
        _report(ModuleContributionCatalog.IsValidId(id) ? $"{UnknownStage} id={id}" : UnknownStage);
        return null;
    }
}
