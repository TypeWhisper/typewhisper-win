using System.Windows;
using System.Windows.Interop;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Manages hotkeys for dictation: Toggle mode (RegisterHotKey) and Push-to-Talk (low-level hook).
/// Hybrid mode auto-detects: short press = toggle, long hold = push-to-talk.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyIdToggle = 9000;
    private const double PushToTalkThresholdMs = 600;

    private readonly ISettingsService _settings;
    private readonly KeyboardHook _keyboardHook;

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private bool _toggleRegistered;
    private bool _disposed;

    private RecordingMode _activeMode;
    private DateTime _keyDownTime;
    private bool _isActive; // currently recording

    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public HotkeyMode? CurrentMode { get; private set; }
    public bool IsEnabled { get; set; } = true;

    public HotkeyService(ISettingsService settings)
    {
        _settings = settings;
        _keyboardHook = new KeyboardHook();
        _keyboardHook.KeyDown += OnHookKeyDown;
        _keyboardHook.KeyUp += OnHookKeyUp;
    }

    public void Initialize(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        ApplySettings();
        _settings.SettingsChanged += _ => Application.Current?.Dispatcher.Invoke(ApplySettings);
    }

    public void ApplySettings()
    {
        var s = _settings.Current;
        _activeMode = RecordingMode.Hybrid;

        // Unregister everything first
        UnregisterAll();

        // Always use hybrid mode: short press = toggle, long hold = push-to-talk
        var hotkey = !string.IsNullOrWhiteSpace(s.PushToTalkHotkey) ? s.PushToTalkHotkey : s.ToggleHotkey;
        StartPushToTalkHook(hotkey);
    }

    private void RegisterToggleHotkey(string hotkeyString)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!HotkeyParser.Parse(hotkeyString, out var modifiers, out var vk)) return;
        if (vk == 0) return; // Toggle needs a non-modifier key

        _toggleRegistered = NativeMethods.RegisterHotKey(
            _hwnd, HotkeyIdToggle,
            modifiers | NativeMethods.MOD_NOREPEAT, vk);
    }

    private void StartPushToTalkHook(string hotkeyString)
    {
        _keyboardHook.SetHotkey(hotkeyString);
        _keyboardHook.Start();
    }

    private void UnregisterAll()
    {
        if (_toggleRegistered && _hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyIdToggle);
            _toggleRegistered = false;
        }

        _keyboardHook.Stop();
        _isActive = false;
        CurrentMode = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && !_disposed && IsEnabled)
        {
            var id = wParam.ToInt32();
            if (id == HotkeyIdToggle)
            {
                handled = true;
                HandleToggle();
            }
        }
        return IntPtr.Zero;
    }

    private void HandleToggle()
    {
        if (_isActive)
        {
            _isActive = false;
            CurrentMode = null;
            DictationStopRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _isActive = true;
            CurrentMode = HotkeyMode.Toggle;
            DictationStartRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnHookKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;

        if (_activeMode == RecordingMode.Hybrid && _isActive)
        {
            // Already recording in toggle mode → stop
            _isActive = false;
            CurrentMode = null;
            DictationStopRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _keyDownTime = DateTime.UtcNow;
        _isActive = true;
        CurrentMode = HotkeyMode.PushToTalk; // Assume PTT until release
        DictationStartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHookKeyUp(object? sender, EventArgs e)
    {
        if (!_isActive) return;

        if (_activeMode == RecordingMode.Hybrid)
        {
            var holdMs = (DateTime.UtcNow - _keyDownTime).TotalMilliseconds;
            if (holdMs < PushToTalkThresholdMs)
            {
                // Short press → switch to toggle mode, keep recording
                CurrentMode = HotkeyMode.Toggle;
                return;
            }
        }

        // PTT or Hybrid long-hold → stop
        _isActive = false;
        CurrentMode = null;
        DictationStopRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ForceStop()
    {
        if (_isActive)
        {
            _isActive = false;
            CurrentMode = null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            UnregisterAll();
            _hwndSource?.RemoveHook(WndProc);
            _keyboardHook.Dispose();
            _disposed = true;
        }
    }
}

public enum HotkeyMode
{
    Toggle,
    PushToTalk
}
