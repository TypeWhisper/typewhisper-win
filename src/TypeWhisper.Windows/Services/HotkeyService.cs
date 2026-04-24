using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Manages independent hotkeys for dictation and standalone actions:
/// - Hybrid: short press = toggle, long hold = push-to-talk
/// - Toggle-only: press to start, press again to stop
/// - Hold-only: hold to record, release to stop
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const double PushToTalkThresholdMs = 600;

    private readonly ISettingsService _settings;
    private readonly IWorkflowService _workflows;
    private readonly KeyboardHook _hybridHook;
    private readonly KeyboardHook _toggleOnlyHook;
    private readonly KeyboardHook _holdOnlyHook;
    private readonly KeyboardHook _cancelHook;
    private readonly KeyboardHook _recentTranscriptionsHook;
    private readonly KeyboardHook _copyLastTranscriptionHook;
    private readonly List<(KeyboardHook Hook, string WorkflowId)> _workflowHooks = [];

    private bool _disposed;
    private DateTime _keyDownTime;
    private bool _isActive;

    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler<string>? WorkflowDictationRequested;
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

    public HotkeyService(ISettingsService settings, IWorkflowService workflows)
    {
        _settings = settings;
        _workflows = workflows;

        _hybridHook = new KeyboardHook();
        _hybridHook.KeyDown += OnHybridKeyDown;
        _hybridHook.KeyUp += OnHybridKeyUp;

        _toggleOnlyHook = new KeyboardHook();
        _toggleOnlyHook.KeyDown += OnToggleOnlyKeyDown;

        _holdOnlyHook = new KeyboardHook();
        _holdOnlyHook.KeyDown += OnHoldOnlyKeyDown;
        _holdOnlyHook.KeyUp += OnHoldOnlyKeyUp;

        _cancelHook = new KeyboardHook();
        _cancelHook.SetHotkey("Escape");
        _cancelHook.KeyDown += OnCancelKeyDown;

        _recentTranscriptionsHook = new KeyboardHook();
        _recentTranscriptionsHook.KeyDown += OnRecentTranscriptionsKeyDown;

        _copyLastTranscriptionHook = new KeyboardHook();
        _copyLastTranscriptionHook.KeyDown += OnCopyLastTranscriptionKeyDown;
    }

    public void Initialize(Window window)
    {
        ApplySettings();
        _settings.SettingsChanged += _ => Application.Current?.Dispatcher.Invoke(ApplySettings);
        _workflows.WorkflowsChanged += () => Application.Current?.Dispatcher.Invoke(ApplyWorkflowHotkeys);
    }

    public void ApplySettings()
    {
        var s = _settings.Current;

        StopAllHooks();
        StopWorkflowHooks();

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

        if (!string.IsNullOrWhiteSpace(s.RecentTranscriptionsHotkey))
        {
            _recentTranscriptionsHook.SetHotkey(s.RecentTranscriptionsHotkey);
            _recentTranscriptionsHook.Start();
        }

        if (!string.IsNullOrWhiteSpace(s.CopyLastTranscriptionHotkey))
        {
            _copyLastTranscriptionHook.SetHotkey(s.CopyLastTranscriptionHotkey);
            _copyLastTranscriptionHook.Start();
        }

        _cancelHook.Start();

        ApplyWorkflowHotkeys();
        ApplyEnabledState();
    }

    private void ApplyWorkflowHotkeys()
    {
        StopWorkflowHooks();

        foreach (var workflow in _workflows.Workflows)
        {
            if (!workflow.IsEnabled || workflow.Trigger.Kind != WorkflowTriggerKind.Hotkey)
                continue;

            foreach (var hotkey in workflow.Trigger.Hotkeys.Where(static hotkey => !string.IsNullOrWhiteSpace(hotkey)))
            {
                var hook = new KeyboardHook();
                var workflowId = workflow.Id;
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
                        WorkflowDictationRequested?.Invoke(this, workflowId);
                    }
                };
                hook.SetHotkey(hotkey);
                hook.Start();
                hook.IsEnabled = _isEnabled;
                _workflowHooks.Add((hook, workflowId));
                Debug.WriteLine($"Registered workflow hotkey: {hotkey} for {workflow.Name}");
            }
        }
    }

    private void ApplyEnabledState()
    {
        _hybridHook.IsEnabled = _isEnabled;
        _toggleOnlyHook.IsEnabled = _isEnabled;
        _holdOnlyHook.IsEnabled = _isEnabled;
        _cancelHook.IsEnabled = _isEnabled && IsCancelShortcutEnabled;
        _recentTranscriptionsHook.IsEnabled = _isEnabled;
        _copyLastTranscriptionHook.IsEnabled = _isEnabled;

        foreach (var (hook, _) in _workflowHooks)
            hook.IsEnabled = _isEnabled;
    }

    private void StopWorkflowHooks()
    {
        foreach (var (hook, _) in _workflowHooks)
        {
            hook.Stop();
            hook.Dispose();
        }
        _workflowHooks.Clear();
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

    private void OnCancelKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled || !IsCancelShortcutEnabled) return;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecentTranscriptionsKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;
        RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCopyLastTranscriptionKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;
        CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
    }

    // --- Common ---

    private void StopAllHooks()
    {
        _hybridHook.Stop();
        _toggleOnlyHook.Stop();
        _holdOnlyHook.Stop();
        _cancelHook.Stop();
        _recentTranscriptionsHook.Stop();
        _copyLastTranscriptionHook.Stop();
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
            StopWorkflowHooks();
            _hybridHook.Dispose();
            _toggleOnlyHook.Dispose();
            _holdOnlyHook.Dispose();
            _cancelHook.Dispose();
            _recentTranscriptionsHook.Dispose();
            _copyLastTranscriptionHook.Dispose();
            _disposed = true;
        }
    }
}

public enum HotkeyMode
{
    Toggle,
    PushToTalk
}
