using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.Graphics;
using Windows.System;

namespace TypeWhisper.WinUI;

// Own the first layout pass instead of relying on NotifyIcon's experimental
// second-window flyout, which measures before Loaded and hides its first open.
internal sealed class TrayMenuWindow : Window
{
    private readonly MenuFlyoutPresenter _presenter;
    private bool _opening;

    internal TrayMenuWindow(MenuFlyout menu)
    {
        Title = "TypeWhisper tray menu";
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
                action.Click += (_, _) => AppWindow.Hide();
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
            if (_opening) DispatcherQueue.TryEnqueue(LayoutAndShow);
        };
        _presenter.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            AppWindow.Hide();
            args.Handled = true;
        };
        Activated += (_, args) =>
        {
            NativeWindowAppearance.RemoveSystemBorder(this);
            if (!_opening && args.WindowActivationState == WindowActivationState.Deactivated)
                AppWindow.Hide();
        };
    }

    internal void Present()
    {
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
        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(cursor.X, cursor.Y));
        var scale = _presenter.XamlRoot?.RasterizationScale ?? 1;
        _presenter.MaxHeight = Math.Max(100, (area.Height - 16) / scale);
        _presenter.Measure(new Windows.Foundation.Size(Math.Min(420, area.Width / scale), _presenter.MaxHeight));
        var width = Math.Min(area.Width, (int)Math.Ceiling(_presenter.DesiredSize.Width * scale) + 2);
        var height = Math.Min(area.Height, (int)Math.Ceiling(_presenter.DesiredSize.Height * scale) + 2);
        AppWindow.MoveAndResize(new RectInt32(
            Math.Clamp(cursor.X - width, area.X, area.X + area.Width - width),
            Math.Clamp(cursor.Y - height, area.Y, area.Y + area.Height - height),
            width, height));
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _opening = false;
        var firstAction = _presenter.Items.OfType<MenuFlyoutItem>().FirstOrDefault(item => item.IsEnabled);
        firstAction?.Focus(FocusState.Programmatic);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
