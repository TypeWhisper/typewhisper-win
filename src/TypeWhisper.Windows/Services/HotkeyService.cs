using System.Diagnostics;
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
    private readonly IProfileService _profiles;
    private readonly KeyboardHook _hybridHook;
    private readonly KeyboardHook _toggleOnlyHook;
    private readonly KeyboardHook _holdOnlyHook;
    private readonly KeyboardHook _promptPaletteHook;
    private readonly KeyboardHook _cancelHook;
    private readonly List<(KeyboardHook Hook, string ProfileId)> _profileHooks = [];

    private bool _disposed;
    private DateTime _keyDownTime;
    private bool _isActive;

    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? ProfileDictationRequested;
    public HotkeyMode? CurrentMode { get; private set; }

    private bool _isCancelShortcutEnabled;
    public bool IsCancelShortcutEnabled
    {
        get => _isCancelShortcutEnabled;
        set
        {
            _isCancelShortcutEnabled = value;
            ApplyEnabledState();
        }
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            ApplyEnabledState();
        }
    }

    public HotkeyService(ISettingsService settings, IProfileService profiles)
    {
        _settings = settings;
        _profiles = profiles;

        _hybridHook = new KeyboardHook();
        _hybridHook.KeyDown += OnHybridKeyDown;
        _hybridHook.KeyUp += OnHybridKeyUp;

        _toggleOnlyHook = new KeyboardHook();
        _toggleOnlyHook.KeyDown += OnToggleOnlyKeyDown;

        _holdOnlyHook = new KeyboardHook();
        _holdOnlyHook.KeyDown += OnHoldOnlyKeyDown;
        _holdOnlyHook.KeyUp += OnHoldOnlyKeyUp;

        _promptPaletteHook = new KeyboardHook();
        _promptPaletteHook.KeyDown += OnPromptPaletteKeyDown;

        _cancelHook = new KeyboardHook();
        _cancelHook.SetHotkey("Escape");
        _cancelHook.KeyDown += OnCancelKeyDown;
    }

    public void Initialize(Window window)
    {
        ApplySettings();
        _settings.SettingsChanged += _ => Application.Current?.Dispatcher.Invoke(ApplySettings);
        _profiles.ProfilesChanged += () => Application.Current?.Dispatcher.Invoke(ApplyProfileHotkeys);
    }

    public void ApplySettings()
    {
        var s = _settings.Current;

        StopAllHooks();
        StopProfileHooks();

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

        if (!string.IsNullOrWhiteSpace(s.PromptPaletteHotkey))
        {
            _promptPaletteHook.SetHotkey(s.PromptPaletteHotkey);
            _promptPaletteHook.Start();
        }

        _cancelHook.Start();

        ApplyProfileHotkeys();
        ApplyEnabledState();
    }

    private void ApplyProfileHotkeys()
    {
        StopProfileHooks();

        foreach (var profile in _profiles.Profiles)
        {
            if (!profile.IsEnabled || string.IsNullOrWhiteSpace(profile.HotkeyData)) continue;

            var hook = new KeyboardHook();
            var profileId = profile.Id;
            hook.KeyDown += (_, _) =>
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
                    ProfileDictationRequested?.Invoke(this, profileId);
                }
            };
            hook.SetHotkey(profile.HotkeyData);
            hook.Start();
            hook.IsEnabled = _isEnabled;
            _profileHooks.Add((hook, profileId));
            Debug.WriteLine($"Registered profile hotkey: {profile.HotkeyData} for {profile.Name}");
        }
    }

    private void ApplyEnabledState()
    {
        _hybridHook.IsEnabled = _isEnabled;
        _toggleOnlyHook.IsEnabled = _isEnabled;
        _holdOnlyHook.IsEnabled = _isEnabled;
        _promptPaletteHook.IsEnabled = _isEnabled;
        _cancelHook.IsEnabled = _isEnabled && IsCancelShortcutEnabled;

        foreach (var (hook, _) in _profileHooks)
            hook.IsEnabled = _isEnabled;
    }

    private void StopProfileHooks()
    {
        foreach (var (hook, _) in _profileHooks)
        {
            hook.Stop();
            hook.Dispose();
        }
        _profileHooks.Clear();
    }

    // --- Hybrid: short press = toggle, long hold = PTT ---

    private DateTime _lastActionTime;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);

    private void OnHybridKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;

        // Debounce rapid key presses
        var now = DateTime.UtcNow;
        if (now - _lastActionTime < DebounceInterval) return;
        _lastActionTime = now;

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

        var now = DateTime.UtcNow;
        if (now - _lastActionTime < DebounceInterval) return;
        _lastActionTime = now;

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

    // --- Prompt Palette: single press = toggle ---

    private void OnPromptPaletteKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;
        PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancelKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled || !IsCancelShortcutEnabled) return;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    // --- Common ---

    private void StopAllHooks()
    {
        _hybridHook.Stop();
        _toggleOnlyHook.Stop();
        _holdOnlyHook.Stop();
        _promptPaletteHook.Stop();
        _cancelHook.Stop();
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
            StopProfileHooks();
            _hybridHook.Dispose();
            _toggleOnlyHook.Dispose();
            _holdOnlyHook.Dispose();
            _promptPaletteHook.Dispose();
            _cancelHook.Dispose();
            _disposed = true;
        }
    }
}

public enum HotkeyMode
{
    Toggle,
    PushToTalk
}
