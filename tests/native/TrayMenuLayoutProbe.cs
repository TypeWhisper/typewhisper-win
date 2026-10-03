#if DEBUG
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.WinUI.Platform;
using global::Windows.Foundation;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

internal static class TrayMenuLayoutProbe
{
    internal static async Task RunAsync()
    {
        var samples = new List<object>();
        var failures = new List<string>();
        Directory.CreateDirectory(WinUIProfile.Root);
        // An interrupted run must not leave an earlier successful result behind.
        File.Delete(WinUIProfile.DataPath("tray-layout-probe.json"));
        try
        {
            static void Noop() { }
            using var tray = new TrayIconService(Noop, Noop, Noop, Noop, Noop, Noop, Noop, Noop,
                Noop, Noop, Noop, Noop, Noop, Noop, Noop);
            var menu = (TrayMenuWindow)typeof(TrayIconService).GetField("_menuWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            var presenter = (MenuFlyoutPresenter)menu.Content;
            tray.UpdateHotkeyPause(false, true, null);
            tray.UpdateDictation("Groq · Whisper Large V3 Turbo ready ·", false);

            object Snapshot(string phase, bool expectFit = true)
            {
                var scroll = FindChild<ScrollViewer>(presenter);
                var bottom = presenter.Items.OfType<MenuFlyoutItem>().Last();
                var bottomPoint = bottom.TransformToVisual(presenter).TransformPoint(new Point(0, bottom.ActualHeight));
                if (expectFit && (!presenter.IsLoaded || !menu.AppWindow.IsVisible || scroll is null || bottom.ActualHeight <= 0))
                    failures.Add($"{phase}: the menu has not finished loading and displaying its items.");
                if (expectFit && (bottomPoint.Y > presenter.ActualHeight || scroll?.ScrollableHeight > 1))
                    failures.Add($"{phase}: the last menu item is clipped or requires scrolling despite ample display space.");
                return new
                {
                    phase,
                    size = new { menu.AppWindow.Size.Width, menu.AppWindow.Size.Height },
                    desired = new { presenter.DesiredSize.Width, presenter.DesiredSize.Height },
                    actual = new { presenter.ActualWidth, presenter.ActualHeight },
                    scale = presenter.XamlRoot?.RasterizationScale,
                    dpi = NativeMethods.GetDpiForWindow(tray.WindowHandle),
                    lastItemBottom = bottomPoint.Y,
                    viewportHeight = scroll?.ViewportHeight,
                    extentHeight = scroll?.ExtentHeight,
                    scrollableHeight = scroll?.ScrollableHeight,
                    rows = presenter.Items.OfType<MenuFlyoutItemBase>().Select(i => new
                    {
                        name = (i as MenuFlyoutItem)?.Text ?? "separator", visible = i.Visibility.ToString(),
                        desiredHeight = i.DesiredSize.Height, actualHeight = i.ActualHeight,
                        marginTop = i.Margin.Top, marginBottom = i.Margin.Bottom
                    }).ToArray()
                };
            }

            for (var iteration = 0; iteration < 12; iteration++)
            {
                if (iteration == 2)
                    menu.AppWindow.Resize(new SizeInt32(270, 308));
                if (iteration == 4)
                    menu.AppWindow.Resize(new SizeInt32(1, 1));
                tray.UpdateDictation(iteration % 2 == 0 ? "Groq · Whisper Large V3 Turbo ready ·" : "Paste sent. Saved to History.", iteration % 3 == 0);
                tray.UpdateProcessing(iteration % 4 == 0);
                menu.Present();
                await Task.Delay(150);
                samples.Add(Snapshot($"open-{iteration}"));
                if (iteration == 6)
                {
                    tray.UpdateDictation("Recording", true);
                    tray.UpdateProcessing(true);
                    await Task.Delay(150);
                    samples.Add(Snapshot("changed-while-open"));
                }
                menu.AppWindow.Hide();
                await Task.Delay(60);
                if (iteration == 8)
                {
                    presenter.Measure(new Size(420, double.PositiveInfinity));
                    samples.Add(Snapshot("infinite-measure-while-hidden", expectFit: false));
                }
            }

            tray.UpdateDictation("Groq · Whisper Large V3 Turbo ready ·", false);
            tray.UpdateProcessing(false);
            menu.Present();
            await Task.Delay(150);
            menu.AppWindow.Resize(new SizeInt32(269, 309));
            await Task.Delay(150);
            samples.Add(Snapshot("forced-small-visible", expectFit: false));
            menu.AppWindow.Hide();
            await Task.Delay(150);
            menu.Present();
            await Task.Delay(150);
            samples.Add(Snapshot("reopen-from-small-unchanged"));
            menu.AppWindow.Hide();
            menu.AppWindow.Resize(new SizeInt32(1, 1));
            await Task.Delay(150);
            tray.UpdateDictation("Paste sent. Saved to History.", false);
            await Task.Delay(150);
            menu.Present();
            await Task.Delay(150);
            samples.Add(Snapshot("reopen-from-tiny-hidden"));

            // Exercise the real change handler without changing the desktop's scaling.
            var scaleField = typeof(TrayMenuWindow).GetField("_scale", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var queuedField = typeof(TrayMenuWindow).GetField("_layoutQueued", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var scaleChanged = typeof(TrayMenuWindow).GetMethod("OnXamlRootChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var root = presenter.XamlRoot;
            scaleField.SetValue(menu, root.RasterizationScale + 0.00000001);
            scaleChanged.Invoke(menu, [root, null]);
            if ((bool)queuedField.GetValue(menu)!) failures.Add("Scale rounding noise queued a layout update.");
            scaleField.SetValue(menu, root.RasterizationScale + 0.25);
            scaleChanged.Invoke(menu, [root, null]);
            if (!(bool)queuedField.GetValue(menu)!) failures.Add("A material scale change did not queue a layout update.");
            await Task.Delay(150);
            samples.Add(Snapshot("scale-change-notification"));

            var area = DisplayArea.GetFromWindowId(menu.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var previousPosition = menu.AppWindow.Position;
            var previousRight = menu.AppWindow.Position.X + menu.AppWindow.Size.Width;
            var previousBottom = menu.AppWindow.Position.Y + menu.AppWindow.Size.Height;
            tray.UpdateDictation("Groq · Whisper Large V3 Turbo ready ·", false);
            tray.UpdateProcessing(true);
            await Task.Delay(150);
            samples.Add(Snapshot("text-and-height-change-while-open"));
            // At the left/top work-area edge, a larger menu must grow inward instead.
            var movedRight = previousPosition.X > area.X && menu.AppWindow.Position.X > area.X
                && previousRight != menu.AppWindow.Position.X + menu.AppWindow.Size.Width;
            var movedBottom = previousPosition.Y > area.Y && menu.AppWindow.Position.Y > area.Y
                && previousBottom != menu.AppWindow.Position.Y + menu.AppWindow.Size.Height;
            if (movedRight || movedBottom)
                failures.Add("Changing the menu content moved its anchor.");
            tray.UpdateProcessing(false);
            menu.AppWindow.Hide();
            await Task.Delay(150);
            if (menu.AppWindow.IsVisible) failures.Add("A queued layout reopened the dismissed menu.");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        File.WriteAllText(WinUIProfile.DataPath("tray-layout-probe.json"), JsonSerializer.Serialize(
            new { passed = failures.Count == 0, failures, samples }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
#endif
