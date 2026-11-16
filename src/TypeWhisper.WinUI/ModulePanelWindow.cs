using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.ModuleKit;
using TypeWhisper.WinUI.Platform;
using global::Windows.Graphics;
using global::Windows.System;

namespace TypeWhisper.WinUI;

// A module panel: a small window that floats above other apps, has no taskbar or Alt+Tab entry, keeps its title bar
// so it can be moved, closes with Escape and is sized to its content. Closing it only hides it.
internal sealed class ModulePanelWindow : Window, IModulePanelSurface
{
    private const double MaximumWidth = 640, MaximumHeight = 720;
    private readonly Border _frame;
    private readonly ContentControl _host;
    private readonly Action _hiddenByUser;
    private bool _loaded;
    private bool _showRequested;
    private bool _allowClose;

    // Creates the module's content and the window for it, or returns null when either fails.
    internal static IModulePanelSurface? Open(ModulePanel panel, Action hiddenByUser)
    {
        var content = ModuleCalls.Get("panel.content", panel.Id, panel.CreateContent, AppDiagnostics.WriteFailure);
        if (content is null) return null;
        try { return new ModulePanelWindow(panel.Title, content, hiddenByUser); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.WriteFailure(ModuleCalls.FailureStage("panel.window", panel.Id), ex);
            return null;
        }
    }

    private ModulePanelWindow(string title, UIElement content, Action hiddenByUser)
    {
        _hiddenByUser = hiddenByUser;
        Title = title;
        NativeWindowAppearance.ApplyAppTitleBar(this);
        _frame = new Border { Child = content, Padding = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var scroll = new ScrollViewer { Content = _frame, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)Application.Current.Resources["InkBrush"] };
        // Focusable, so Escape reaches the panel even when its content has nothing to focus.
        _host = new ContentControl
        {
            Content = scroll, IsTabStop = true, UseSystemFocusVisuals = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetName(_host, title);
        _host.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            HideByUser();
        };
        _host.Loaded += (_, _) =>
        {
            _loaded = true;
            Fit();
            Place();
            if (_showRequested) Present();
        };
        Content = _host;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = presenter.IsMinimizable = false;
        }
        // The close button hides the panel like Escape; the window ends only at shutdown.
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            HideByUser();
        };
        Closed += (_, _) => _allowClose = true;
    }

    public bool IsVisible => !_allowClose && _showRequested && AppWindow.IsVisible;

    public void Show()
    {
        if (_allowClose) return;
        _showRequested = true;
        if (_loaded) { Present(); return; }
        // The first activation loads the content off-screen; it is measured and placed before it appears.
        AppWindow.Move(new PointInt32(-32000, -32000));
        Activate();
    }

    public void Hide()
    {
        _showRequested = false;
        if (!_allowClose) AppWindow.Hide();
    }

    void IModulePanelSurface.Close()
    {
        if (_allowClose) return;
        _allowClose = true;
        Close();
    }

    private void HideByUser()
    {
        if (!IsVisible) return;
        Hide();
        _hiddenByUser();
    }

    private void Present()
    {
        if (!AppWindow.IsVisible) AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _host.Focus(FocusState.Programmatic);
    }

    // Sized to the content, up to a maximum; taller content scrolls.
    private void Fit()
    {
        var scale = Math.Max(96u, NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96d;
        _frame.Measure(new global::Windows.Foundation.Size(MaximumWidth, double.PositiveInfinity));
        var size = _frame.DesiredSize;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Clamp((int)Math.Ceiling(Math.Max(size.Width, 160) * scale), 1, work.Width);
        var height = Math.Clamp((int)Math.Ceiling(Math.Clamp(size.Height, 48, MaximumHeight) * scale), 1, work.Height);
        AppWindow.ResizeClient(new SizeInt32(width, height));
    }

    // Centered horizontally on the display under the pointer, in its upper third.
    private void Place()
    {
        var area = NativeMethods.GetCursorPos(out var cursor) ? DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Primary) : DisplayArea.Primary;
        var work = area.WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(work.X + Math.Max(0, (work.Width - size.Width) / 2), work.Y + Math.Max(0, (work.Height - size.Height) / 3)));
    }
}
