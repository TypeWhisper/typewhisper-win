using System.Windows;
using System.Windows.Interop;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Manages three independent hotkeys for dictation:
/// - Hybrid: short press = toggle, long hold = push-to-talk
/// - Toggle-only: press to start, press again to stop
/// - Hold-only: hold to record, release to stop
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const double PushToTalkThresholdMs = 600;

    private readonly ISettingsService _settings;
    private readonly KeyboardHook _hybridHook;
    private readonly KeyboardHook _toggleOnlyHook;
    private readonly KeyboardHook _holdOnlyHook;

    private bool _disposed;
    private DateTime _keyDownTime;
    private bool _isActive;

    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public HotkeyMode? CurrentMode { get; private set; }
    public bool IsEnabled { get; set; } = true;

    public HotkeyService(ISettingsService settings)
    {
        _settings = settings;

        _hybridHook = new KeyboardHook();
        _hybridHook.KeyDown += OnHybridKeyDown;
        _hybridHook.KeyUp += OnHybridKeyUp;

        _toggleOnlyHook = new KeyboardHook();
        _toggleOnlyHook.KeyDown += OnToggleOnlyKeyDown;

        _holdOnlyHook = new KeyboardHook();
        _holdOnlyHook.KeyDown += OnHoldOnlyKeyDown;
        _holdOnlyHook.KeyUp += OnHoldOnlyKeyUp;
    }

    public void Initialize(Window window)
    {
        ApplySettings();
        _settings.SettingsChanged += _ => Application.Current?.Dispatcher.Invoke(ApplySettings);
    }

    public void ApplySettings()
    {
        var s = _settings.Current;

        StopAllHooks();

        var hybridKey = !string.IsNullOrWhiteSpace(s.PushToTalkHotkey) ? s.PushToTalkHotkey : s.ToggleHotkey;
        if (!string.IsNullOrWhiteSpace(hybridKey))
        {
            _hybridHook.SetHotkey(hybridKey);
            _hybridHook.Start();
        }

        if (!string.IsNullOrWhiteSpace(s.ToggleOnlyHotkey))
        {
            _toggleOnlyHook.SetHotkey(s.ToggleOnlyHotkey);
            _toggleOnlyHook.Start();
        }

        if (!string.IsNullOrWhiteSpace(s.HoldOnlyHotkey))
        {
            _holdOnlyHook.SetHotkey(s.HoldOnlyHotkey);
            _holdOnlyHook.Start();
        }
    }

    // --- Hybrid: short press = toggle, long hold = PTT ---

    private void OnHybridKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;

        if (_isActive)
        {
            _isActive = false;
            CurrentMode = null;
            DictationStopRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _keyDownTime = DateTime.UtcNow;
        _isActive = true;
        CurrentMode = HotkeyMode.PushToTalk;
        DictationStartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHybridKeyUp(object? sender, EventArgs e)
    {
        if (!_isActive) return;

        var holdMs = (DateTime.UtcNow - _keyDownTime).TotalMilliseconds;
        if (holdMs < PushToTalkThresholdMs)
        {
            CurrentMode = HotkeyMode.Toggle;
            return;
        }

        _isActive = false;
        CurrentMode = null;
        DictationStopRequested?.Invoke(this, EventArgs.Empty);
    }

    // --- Toggle-only: press = start/stop ---

    private void OnToggleOnlyKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;

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

    // --- Hold-only: hold = record, release = stop ---

    private void OnHoldOnlyKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;
        if (_isActive) return;

        _isActive = true;
        CurrentMode = HotkeyMode.PushToTalk;
        DictationStartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHoldOnlyKeyUp(object? sender, EventArgs e)
    {
        if (!_isActive) return;

        _isActive = false;
        CurrentMode = null;
        DictationStopRequested?.Invoke(this, EventArgs.Empty);
    }

    // --- Common ---

    private void StopAllHooks()
    {
        _hybridHook.Stop();
        _toggleOnlyHook.Stop();
        _holdOnlyHook.Stop();
        _isActive = false;
        CurrentMode = null;
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
            StopAllHooks();
            _hybridHook.Dispose();
            _toggleOnlyHook.Dispose();
            _holdOnlyHook.Dispose();
            _disposed = true;
        }
    }
}

public enum HotkeyMode
{
    Toggle,
    PushToTalk
}
