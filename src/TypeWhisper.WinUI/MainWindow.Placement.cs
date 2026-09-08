using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private bool _placementReady;
    private PlacementSubclass? _placementSubclass;
    private delegate IntPtr PlacementSubclass(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, IntPtr data);

    private void ConfigureLauncherMinimumSize()
    {
        _placementSubclass = (hwnd, message, wParam, lParam, id, data) =>
        {
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            if (message == 0x0024) // WM_GETMINMAXINFO
            {
                var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
                Marshal.WriteInt32(lParam, 24, (int)Math.Round(640 * scale));
                Marshal.WriteInt32(lParam, 28, (int)Math.Round(480 * scale));
            }
            return result;
        };
        if (!SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), _placementSubclass, 0x545750, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, PlacementSubclass callback, nuint id, IntPtr data);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private Microsoft.UI.Xaml.DispatcherTimer? _placementSaveTimer;
    private static string LauncherPlacementPath => WinUIProfile.DataPath("quick-launch-window.json");
    private sealed record LauncherPlacement(int X, int Y, int Width, int Height);

    private bool RestoreLauncherPlacement()
    {
        try
        {
            if (!File.Exists(LauncherPlacementPath)) return false;
            var saved = JsonSerializer.Deserialize<LauncherPlacement>(File.ReadAllText(LauncherPlacementPath));
            if (saved is null || saved.Width <= 0 || saved.Height <= 0) return false;
            var area = DisplayArea.GetFromPoint(new PointInt32(saved.X, saved.Y), DisplayAreaFallback.Primary);
            if (area is null) return false;
            ApplyLauncherBounds(saved, area.WorkArea);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return false; }
    }

    private void KeepLauncherOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null) return;
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        ApplyLauncherBounds(new(position.X, position.Y, size.Width, size.Height), area.WorkArea);
    }

    private void ApplyLauncherBounds(LauncherPlacement bounds, RectInt32 work)
    {
        var width = Math.Clamp(bounds.Width, Math.Min(640, work.Width), work.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(480, work.Height), work.Height);
        var x = Math.Clamp(bounds.X, work.X, work.X + work.Width - width);
        var y = Math.Clamp(bounds.Y, work.Y, work.Y + work.Height - height);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void SaveLauncherPlacement()
    {
        if (!_placementReady || AppWindow.Presenter is not OverlappedPresenter presenter ||
            presenter.State != OverlappedPresenterState.Restored) return;
        _placementSaveTimer ??= CreatePlacementSaveTimer();
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private Microsoft.UI.Xaml.DispatcherTimer CreatePlacementSaveTimer()
    {
        var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_closing || _profileRestoreClosing || AppWindow.Presenter is not OverlappedPresenter current ||
                current.State != OverlappedPresenterState.Restored) return;
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            if (size.Width <= 0 || size.Height <= 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LauncherPlacementPath)!);
                var temporary = LauncherPlacementPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new LauncherPlacement(position.X, position.Y, size.Width, size.Height)));
                File.Move(temporary, LauncherPlacementPath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine($"Could not save Quick Launch placement: {error.Message}"); }
        };
        return timer;
    }
}
