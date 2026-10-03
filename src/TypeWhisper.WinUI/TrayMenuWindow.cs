using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using TypeWhisper.WinUI.Platform;
using global::Windows.Graphics;
using global::Windows.System;

namespace TypeWhisper.WinUI;

// Own the first layout pass instead of relying on NotifyIcon's experimental
// second-window flyout, which measures before Loaded and hides its first open.
internal sealed class TrayMenuWindow : Window
{
    private readonly MenuFlyoutPresenter _presenter;
    private bool _opening;
    private bool _layoutQueued;
    private bool _closed;
    private PointInt32 _anchor;
    private double _scale;

    internal TrayMenuWindow(MenuFlyout menu)
    {
        NativeWindowAppearance.ApplyAppTitleBar(this);
        Title = Loc.T("TypeWhisper tray menu");
        _presenter = new MenuFlyoutPresenter
        {
            Style = menu.MenuFlyoutPresenterStyle,
            RequestedTheme = ElementTheme.Dark,
        };
        var itemTemplate = (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="MenuFlyoutItem">
                <Grid x:Name="Root" Background="Transparent" Padding="{TemplateBinding Padding}">
                    <VisualStateManager.VisualStateGroups>
                        <VisualStateGroup x:Name="CommonStates">
                            <VisualState x:Name="Normal" />
                            <VisualState x:Name="PointerOver">
                                <VisualState.Setters><Setter Target="Root.Background" Value="#414141" /></VisualState.Setters>
                            </VisualState>
                            <VisualState x:Name="Pressed">
                                <VisualState.Setters><Setter Target="Root.Background" Value="#363636" /></VisualState.Setters>
                            </VisualState>
                            <VisualState x:Name="Disabled">
                                <VisualState.Setters><Setter Target="Label.Opacity" Value="0.45" /></VisualState.Setters>
                            </VisualState>
                        </VisualStateGroup>
                    </VisualStateManager.VisualStateGroups>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="24" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                    <ContentPresenter Content="{TemplateBinding Icon}" VerticalAlignment="Center" HorizontalAlignment="Left" />
                    <TextBlock x:Name="Label" Grid.Column="1" Text="{TemplateBinding Text}" Foreground="{TemplateBinding Foreground}"
                               FontFamily="{TemplateBinding FontFamily}" FontSize="{TemplateBinding FontSize}" VerticalAlignment="Center" />
                </Grid>
            </ControlTemplate>
            """);
        foreach (var item in menu.Items.ToArray())
        {
            menu.Items.Remove(item);
            _presenter.Items.Add(item);
            item.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => QueueLayout());
            if (item is not MenuFlyoutSeparator)
            {
                item.MinHeight = 0;
                item.Height = item is MenuFlyoutItem { Command: null } ? 24 : 28;
                item.Padding = new Thickness(11, 0, 11, 0);
            }
            if (item is MenuFlyoutItem action)
            {
                action.Template = itemTemplate;
                action.UseSystemFocusVisuals = true;
                action.RegisterPropertyChangedCallback(MenuFlyoutItem.TextProperty, (_, _) => QueueLayout());
                action.Click += (_, _) => Hide();
            }
        }
        Content = _presenter;
        ExtendsContentIntoTitleBar = true;
        AppWindow.IsShownInSwitchers = false;
        var windowPresenter = (OverlappedPresenter)AppWindow.Presenter;
        windowPresenter.SetBorderAndTitleBar(false, false);
        windowPresenter.IsResizable = false;
        windowPresenter.IsMaximizable = false;
        windowPresenter.IsMinimizable = false;
        windowPresenter.IsAlwaysOnTop = true;
        NativeWindowAppearance.RemoveSystemBorder(this);
        AppWindow.Resize(new SizeInt32(1, 1));
        _presenter.Loaded += (_, _) =>
        {
            _presenter.XamlRoot.Changed -= OnXamlRootChanged;
            _presenter.XamlRoot.Changed += OnXamlRootChanged;
            if (_opening) DispatcherQueue.TryEnqueue(LayoutAndShow);
        };
        Closed += (_, _) => { _closed = true; _opening = false; };
        _presenter.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            Hide();
            args.Handled = true;
        };
        Activated += (_, args) =>
        {
            NativeWindowAppearance.RemoveSystemBorder(this);
            if (!_opening && args.WindowActivationState == WindowActivationState.Deactivated)
                Hide();
        };
    }

    internal void DisableActions()
    {
        foreach (var item in _presenter.Items.OfType<MenuFlyoutItemBase>()) item.IsEnabled = false;
    }

    internal void Present()
    {
        NativeMethods.GetCursorPos(out _anchor);
        _opening = true;
        // Initial activation loads XAML off-screen, without a visible empty surface.
        if (!_presenter.IsLoaded)
        {
            AppWindow.Move(new PointInt32(-32000, -32000));
            Activate();
        }
        else LayoutAndShow();
    }

    private void LayoutAndShow()
    {
        if (_closed || !_opening) return;
        AppWindow.Move(_anchor);
        ResizeToContent();
        AppWindow.Show();
        Activate();
        NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _opening = false;
        var firstAction = _presenter.Items.OfType<MenuFlyoutItem>().FirstOrDefault(item => item.IsEnabled);
        firstAction?.Focus(FocusState.Programmatic);
    }

    private void Hide()
    {
        _opening = false;
        AppWindow.Hide();
    }

    private void QueueLayout()
    {
        if (_closed || _opening || !AppWindow.IsVisible || _layoutQueued) return;
        _layoutQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _layoutQueued = false;
            // A status update must not reopen a dismissed menu or move it to the current pointer.
            if (!_closed && !_opening && AppWindow.IsVisible) ResizeToContent();
        });
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // Ignore projection rounding noise; a single native DPI step is 1/96.
        if (Math.Abs(sender.RasterizationScale - _scale) > 0.0001) QueueLayout();
    }

    private void ResizeToContent()
    {
        var area = DisplayArea.GetFromPoint(_anchor, DisplayAreaFallback.Primary).WorkArea;
        // The native DPI is current after moving monitors; XamlRoot can catch up later.
        _scale = NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var availableWidth = Math.Min(420, area.Width / _scale);
        _presenter.MaxHeight = Math.Max(1, (area.Height - 16) / _scale);
        _presenter.Measure(new global::Windows.Foundation.Size(availableWidth, _presenter.MaxHeight));

        // Measure the complete item list, not the ScrollViewer's last constrained viewport.
        // This also works on the first open and after a hidden menu's content has changed.
        var horizontalInsets = _presenter.Padding.Left + _presenter.Padding.Right
            + _presenter.BorderThickness.Left + _presenter.BorderThickness.Right;
        var contentWidth = Math.Max(0, _presenter.MinWidth - horizontalInsets);
        var contentHeight = _presenter.Padding.Top + _presenter.Padding.Bottom
            + _presenter.BorderThickness.Top + _presenter.BorderThickness.Bottom;
        foreach (var item in _presenter.Items.OfType<MenuFlyoutItemBase>())
        {
            if (item.Visibility != Visibility.Visible) continue;
            item.Measure(new global::Windows.Foundation.Size(Math.Max(0, availableWidth - horizontalInsets), double.PositiveInfinity));
            contentWidth = Math.Max(contentWidth, item.DesiredSize.Width);
            contentHeight += item.DesiredSize.Height;
        }
        var width = Math.Min(area.Width, (int)Math.Ceiling(Math.Min(availableWidth, contentWidth + horizontalInsets) * _scale) + 2);
        var height = Math.Min(area.Height, (int)Math.Ceiling(Math.Min(_presenter.MaxHeight, contentHeight) * _scale) + 2);
        AppWindow.MoveAndResize(new RectInt32(
            Math.Clamp(_anchor.X - width, area.X, area.X + area.Width - width),
            Math.Clamp(_anchor.Y - height, area.Y, area.Y + area.Height - height),
            width, height));
    }
}
