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
    private readonly KeyboardHook _cancelHook;
    private readonly List<KeyboardHook> _appHotkeyHooks = [];
    private readonly List<(KeyboardHook Hook, string WorkflowId)> _workflowHooks = [];

    private bool _disposed;
    private DateTime _keyDownTime;
    private bool _isActive;

    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? WorkflowPaletteRequested;
    public event EventHandler<string>? WorkflowDictationRequested;
    public event EventHandler<string>? WorkflowTextProcessingRequested;
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

        _cancelHook = new KeyboardHook();
        _cancelHook.SetHotkey("Escape");
        _cancelHook.KeyDown += OnCancelKeyDown;
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

        foreach (var binding in BuildAppHotkeyBindings(s))
        {
            var hook = new KeyboardHook();
            AttachAppHotkeyHandler(hook, binding.Action);
            hook.SetHotkey(binding.Hotkey);
            hook.Start();
            _appHotkeyHooks.Add(hook);
        }

        _cancelHook.SetHotkey("Escape");
        _cancelHook.Start();

        ApplyWorkflowHotkeys();
        ApplyEnabledState();
    }

    private void ApplyWorkflowHotkeys()
    {
        StopWorkflowHooks();

        foreach (var binding in BuildWorkflowHotkeyBindings(_workflows.Workflows))
        {
            var hook = new KeyboardHook();
            var workflowId = binding.WorkflowId;
            hook.KeyDown += (_, _) => HandleWorkflowHotkeyKeyDown(workflowId, binding.Behavior);
            hook.SetHotkey(binding.Hotkey);
            hook.Start();
            hook.IsEnabled = _isEnabled;
            _workflowHooks.Add((hook, workflowId));
            Debug.WriteLine($"Registered workflow hotkey: {binding.Hotkey} for {workflowId}");
        }
    }

    internal static IReadOnlyList<WorkflowHotkeyBinding> BuildWorkflowHotkeyBindings(
        IReadOnlyList<Workflow> workflows) =>
        workflows
            .Where(workflow => workflow.IsEnabled
                               && workflow.Trigger.IsAutomatic
                               && workflow.Trigger.HasHotkeyBindings)
            .SelectMany(workflow => workflow.Trigger.Hotkeys
                .Where(static hotkey => !string.IsNullOrWhiteSpace(hotkey))
                .Select(hotkey => new WorkflowHotkeyBinding(
                    workflow.Id,
                    hotkey,
                    workflow.Trigger.HotkeyBehavior)))
            .ToList();

    internal static IReadOnlyList<AppHotkeyBinding> BuildAppHotkeyBindings(AppSettings settings) =>
    [
        .. BuildAppHotkeyBindings(AppHotkeyAction.MainDictation, settings.GetMainDictationHotkeys()),
        .. BuildAppHotkeyBindings(AppHotkeyAction.ToggleOnly, settings.GetToggleOnlyHotkeys()),
        .. BuildAppHotkeyBindings(AppHotkeyAction.HoldOnly, settings.GetHoldOnlyHotkeys()),
        .. BuildAppHotkeyBindings(AppHotkeyAction.RecentTranscriptions, settings.GetRecentTranscriptionsHotkeys()),
        .. BuildAppHotkeyBindings(AppHotkeyAction.CopyLastTranscription, settings.GetCopyLastTranscriptionHotkeys()),
        .. BuildAppHotkeyBindings(AppHotkeyAction.WorkflowPalette, settings.GetWorkflowPaletteHotkeys())
    ];

    private static IEnumerable<AppHotkeyBinding> BuildAppHotkeyBindings(
        AppHotkeyAction action,
        IEnumerable<string> hotkeys) =>
        hotkeys
            .Where(static hotkey => !string.IsNullOrWhiteSpace(hotkey))
            .Select(hotkey => new AppHotkeyBinding(action, hotkey));

    private void AttachAppHotkeyHandler(KeyboardHook hook, AppHotkeyAction action)
    {
        switch (action)
        {
            case AppHotkeyAction.MainDictation:
                hook.KeyDown += OnHybridKeyDown;
                hook.KeyUp += OnHybridKeyUp;
                break;
            case AppHotkeyAction.ToggleOnly:
                hook.KeyDown += OnToggleOnlyKeyDown;
                break;
            case AppHotkeyAction.HoldOnly:
                hook.KeyDown += OnHoldOnlyKeyDown;
                hook.KeyUp += OnHoldOnlyKeyUp;
                break;
            case AppHotkeyAction.RecentTranscriptions:
                hook.KeyDown += OnRecentTranscriptionsKeyDown;
                break;
            case AppHotkeyAction.CopyLastTranscription:
                hook.KeyDown += OnCopyLastTranscriptionKeyDown;
                break;
            case AppHotkeyAction.WorkflowPalette:
                hook.KeyDown += OnWorkflowPaletteKeyDown;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private void HandleWorkflowHotkeyKeyDown(string workflowId, WorkflowHotkeyBehavior behavior)
    {
        if (!IsEnabled)
            return;

        if (behavior == WorkflowHotkeyBehavior.ProcessSelectedText)
        {
            WorkflowTextProcessingRequested?.Invoke(this, workflowId);
            return;
        }

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
    }

    private void ApplyEnabledState()
    {
        _cancelHook.IsEnabled = _isEnabled && IsCancelShortcutEnabled;

        foreach (var hook in _appHotkeyHooks)
            hook.IsEnabled = _isEnabled;

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

    private void OnWorkflowPaletteKeyDown(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;
        WorkflowPaletteRequested?.Invoke(this, EventArgs.Empty);
    }

    // --- Common ---

    private void StopAllHooks()
    {
        foreach (var hook in _appHotkeyHooks)
        {
            hook.Stop();
            hook.Dispose();
        }
        _appHotkeyHooks.Clear();
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
            StopWorkflowHooks();
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

internal sealed record WorkflowHotkeyBinding(
    string WorkflowId,
    string Hotkey,
    WorkflowHotkeyBehavior Behavior);

internal enum AppHotkeyAction
{
    MainDictation,
    ToggleOnly,
    HoldOnly,
    RecentTranscriptions,
    CopyLastTranscription,
    WorkflowPalette
}

internal sealed record AppHotkeyBinding(
    AppHotkeyAction Action,
    string Hotkey);
