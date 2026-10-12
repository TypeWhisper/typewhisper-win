using System.Globalization;
using System.Security;
using System.Security.AccessControl;
using Microsoft.Win32;
using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Reads <c>HKLM\SOFTWARE\Policies\TypeWhisper</c> through the 64-bit registry view and watches its parent key
/// so the policy key appearing, changing or disappearing is noticed. Never writes the registry.
/// </summary>
internal sealed class RegistryManagedPolicySource : IManagedPolicySource, IDisposable
{
    internal const string DefaultPath = @"SOFTWARE\Policies\TypeWhisper";
    private const string RecommendedSubkey = "Recommended";

    private readonly RegistryHive _hive;
    private readonly RegistryView _view;
    private readonly string _path;
    private readonly string _parentPath;
    private readonly object _lock = new();
    private RegistryKey? _watchedKey;
    private ManualResetEvent? _signal;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    /// <param name="hive">The hive; production reads the machine hive, tests use a disposable user key.</param>
    /// <param name="view">The registry view; policies live in the native 64-bit view.</param>
    /// <param name="path">The policy key below the hive. Its parent key is watched for changes.</param>
    public RegistryManagedPolicySource(RegistryHive hive = RegistryHive.LocalMachine, RegistryView view = RegistryView.Registry64, string path = DefaultPath)
    {
        _hive = hive;
        _view = view;
        _path = path;
        var separator = path.LastIndexOf('\\');
        _parentPath = separator > 0 ? path[..separator] : path;
    }

    public event Action? Changed;

    /// <summary>Whether change notifications are active; without them only the fallback interval refreshes.</summary>
    public bool IsWatching { get; private set; }

    /// <summary>Why watching is unavailable, as a technical description without paths.</summary>
    public string? WatchError { get; private set; }

    public ManagedPolicyReading Read()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(_hive, _view);
            using var root = baseKey.OpenSubKey(_path, writable: false);
            if (root is null) return ManagedPolicyReading.Absent;
            using var recommended = root.OpenSubKey(RecommendedSubkey, writable: false);
            return new(true, null, ReadSection(root, RecommendedSubkey),
                recommended is null ? ManagedPolicyRawSection.Missing : ReadSection(recommended, null));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return ManagedPolicyReading.Unreadable(ex.GetType().Name);
        }
    }

    /// <summary>Starts watching the parent key and its subtree. Returns false when nothing can be watched.</summary>
    public bool StartWatching()
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (IsWatching) return true;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(_hive, _view);
                _watchedKey = baseKey.OpenSubKey(_parentPath, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.Notify);
                if (_watchedKey is null)
                {
                    WatchError = "parent key missing";
                    return false;
                }
                _signal = new ManualResetEvent(false);
                if (!Register()) return false;
                IsWatching = true;
                WatchError = null;
                return true;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                WatchError = ex.GetType().Name;
                StopWatchingUnsafe();
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            StopWatchingUnsafe();
        }
    }

    private static ManagedPolicyRawSection ReadSection(RegistryKey key, string? skippedSubkey)
    {
        var values = new List<KeyValuePair<string, ManagedPolicyRawValue>>();
        foreach (var name in key.GetValueNames())
        {
            // The unnamed default value is never a policy.
            if (name.Length == 0) continue;
            values.Add(new(name, ReadValue(key, name)));
        }
        var lists = new List<KeyValuePair<string, ManagedPolicyRawList>>();
        foreach (var name in key.GetSubKeyNames())
        {
            if (skippedSubkey is not null && string.Equals(name, skippedSubkey, StringComparison.OrdinalIgnoreCase)) continue;
            using var subkey = key.OpenSubKey(name, writable: false);
            // Deleted between enumeration and open.
            if (subkey is null) continue;
            lists.Add(new(name, ReadList(subkey)));
        }
        return ManagedPolicyRawSection.Of(values, lists);
    }

    private static ManagedPolicyRawValue ReadValue(RegistryKey key, string name)
    {
        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return kind switch
        {
            RegistryValueKind.DWord when value is int number => ManagedPolicyRawValue.FromNumber(unchecked((uint)number)),
            RegistryValueKind.String when value is string text => ManagedPolicyRawValue.FromText(text),
            _ => ManagedPolicyRawValue.Unsupported(kind.ToString())
        };
    }

    // ADMX list elements write REG_SZ values named 1, 2, ...; anything else makes the list malformed.
    private static ManagedPolicyRawList ReadList(RegistryKey key)
    {
        var malformed = key.SubKeyCount > 0;
        var entries = new List<(int Index, string Text)>();
        foreach (var name in key.GetValueNames())
        {
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index <= 0)
            {
                malformed = true;
                continue;
            }
            if (key.GetValueKind(name) != RegistryValueKind.String ||
                key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string text)
            {
                malformed = true;
                continue;
            }
            entries.Add((index, text));
        }
        return new(entries.OrderBy(entry => entry.Index).Select(entry => entry.Text).ToArray(), malformed);
    }

    // Called under the lock with an open key and signal.
    private bool Register()
    {
        _signal!.Reset();
        var result = NativeMethods.RegNotifyChangeKeyValue(_watchedKey!.Handle, true,
            NativeMethods.REG_NOTIFY_CHANGE_NAME | NativeMethods.REG_NOTIFY_CHANGE_LAST_SET | NativeMethods.REG_NOTIFY_THREAD_AGNOSTIC,
            _signal.SafeWaitHandle, true);
        if (result != 0)
        {
            WatchError = $"RegNotifyChangeKeyValue {result}";
            StopWatchingUnsafe();
            return false;
        }
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, OnSignaled, null, Timeout.Infinite, executeOnlyOnce: true);
        return true;
    }

    private void OnSignaled(object? state, bool timedOut)
    {
        lock (_lock)
        {
            if (_disposed || !IsWatching) return;
            _wait?.Unregister(null);
            _wait = null;
            // Renew before reporting so a change made during the handler is not missed.
            if (!Register()) IsWatching = false;
        }
        Changed?.Invoke();
    }

    private void StopWatchingUnsafe()
    {
        IsWatching = false;
        _wait?.Unregister(null);
        _wait = null;
        _signal?.Dispose();
        _signal = null;
        _watchedKey?.Dispose();
        _watchedKey = null;
    }
}
