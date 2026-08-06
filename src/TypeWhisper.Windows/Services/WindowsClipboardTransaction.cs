using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

internal interface IClipboardLease : IDisposable
{
}

internal enum ClipboardRestoreResult
{
    Restored,
    ClipboardChanged
}

internal readonly record struct ClipboardTextState(string? Text, uint SequenceNumber);

internal sealed class WindowsClipboardTransaction : IDisposable
{
    internal const string ExcludeClipboardContentFromMonitorProcessing =
        "ExcludeClipboardContentFromMonitorProcessing";
    internal const string EnterpriseDataProtectionId = "EnterpriseDataProtectionId";

    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(50);
    private const int MaxClipboardOpenAttempts = 3;
    private const int MaxClipboardRestoreAttempts = 3;
    private static readonly IntPtr MessageOnlyWindowParent = new(-3);

    private readonly Dispatcher? _dispatcher;
    private readonly Action<uint, IntPtr>? _handleReleaseObserver;
    private HwndSource? _ownerWindow;
    private bool _disposed;

    public WindowsClipboardTransaction(
        Dispatcher? dispatcher = null,
        Action<uint, IntPtr>? handleReleaseObserver = null)
    {
        _dispatcher = dispatcher;
        _handleReleaseObserver = handleReleaseObserver;
    }

    public Task<IClipboardLease> BeginTemporaryTextAsync(
        string text,
        CancellationToken cancellationToken) =>
        WithOpenClipboardAsync<IClipboardLease>(
            () => BeginTemporaryTextCore(text),
            cancellationToken);

    public Task SetPersistentTextAsync(string text, CancellationToken cancellationToken) =>
        WithOpenClipboardAsync<object?>(() =>
        {
            ReplaceClipboardWithTextCore(text, excludeFromHistory: false);
            return null;
        }, cancellationToken);

    public Task<bool> CommitTemporaryTextAsync(
        IClipboardLease lease,
        string text,
        CancellationToken cancellationToken)
    {
        var windowsLease = RequireLease(lease);
        return WithOpenClipboardAsync(() =>
        {
            if (windowsLease.IsCompleted)
                return true;

            if (NativeMethods.GetClipboardSequenceNumber() != windowsLease.ExpectedSequenceNumber)
            {
                windowsLease.CompleteWithoutRestore();
                return false;
            }

            if (!NativeMethods.EmptyClipboard())
                throw LastClipboardError("Could not clear the clipboard for the copy fallback.");

            try
            {
                SetUnicodeTextCore(text);
                windowsLease.CompleteWithoutRestore();
                return true;
            }
            catch
            {
                TryRollbackSnapshotCore(windowsLease);
                throw;
            }
        }, cancellationToken);
    }

    public Task<ClipboardRestoreResult> RestoreAsync(
        IClipboardLease lease,
        CancellationToken cancellationToken)
    {
        var windowsLease = RequireLease(lease);
        return WithOpenClipboardAsync(() =>
        {
            if (windowsLease.IsCompleted)
                return ClipboardRestoreResult.Restored;

            if (NativeMethods.GetClipboardSequenceNumber() != windowsLease.ExpectedSequenceNumber)
            {
                windowsLease.CompleteWithoutRestore();
                return ClipboardRestoreResult.ClipboardChanged;
            }

            RestoreSnapshotCore(windowsLease);
            return ClipboardRestoreResult.Restored;
        }, cancellationToken);
    }

    public Task<ClipboardTextState> ReadTextStateAsync(CancellationToken cancellationToken) =>
        WithOpenClipboardAsync(() =>
        {
            string? text = null;
            var textHandle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (textHandle != IntPtr.Zero)
            {
                var textPointer = NativeMethods.GlobalLock(textHandle);
                if (textPointer == IntPtr.Zero)
                    throw LastClipboardError("Could not read Unicode text from the clipboard.");

                try
                {
                    text = Marshal.PtrToStringUni(textPointer);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(textHandle);
                }
            }

            return new ClipboardTextState(text, NativeMethods.GetClipboardSequenceNumber());
        }, cancellationToken);

    public void AcceptSequence(IClipboardLease lease, uint sequenceNumber)
    {
        var windowsLease = RequireLease(lease);
        if (!windowsLease.IsCompleted)
            windowsLease.ExpectedSequenceNumber = sequenceNumber;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var ownerWindow = _ownerWindow;
        _ownerWindow = null;
        if (ownerWindow is null)
            return;

        try
        {
            if (ownerWindow.Dispatcher.CheckAccess())
                ownerWindow.Dispose();
            else if (!ownerWindow.Dispatcher.HasShutdownStarted)
                ownerWindow.Dispatcher.Invoke(ownerWindow.Dispose);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher may already be shutting down.
        }
        catch (OperationCanceledException)
        {
            // The dispatcher shut down between the check and the invoke.
        }
    }

    private WindowsClipboardLease BeginTemporaryTextCore(string text)
    {
        var excludeFormat = NativeMethods.RegisterClipboardFormat(
            ExcludeClipboardContentFromMonitorProcessing);
        if (excludeFormat == 0)
            throw LastClipboardError("Could not register the clipboard history exclusion format.");

        var enterpriseFormat = NativeMethods.RegisterClipboardFormat(EnterpriseDataProtectionId);
        if (enterpriseFormat == 0)
            throw LastClipboardError("Could not register the enterprise clipboard metadata format.");

        var snapshot = CaptureSnapshotCore(enterpriseFormat);
        var clipboardCleared = false;
        try
        {
            if (!NativeMethods.EmptyClipboard())
                throw LastClipboardError("Could not clear the clipboard for temporary text insertion.");

            clipboardCleared = true;
            SetExclusionMarkerCore(excludeFormat);
            SetUnicodeTextCore(text);

            if (NativeMethods.GetClipboardSequenceNumber() == 0)
                throw new COMException("Could not obtain the clipboard sequence number.");

            return new WindowsClipboardLease(snapshot);
        }
        catch
        {
            if (clipboardCleared)
            {
                try
                {
                    RestoreSnapshotCore(snapshot);
                }
                catch
                {
                    // Preserve the original exception. The caller logs the insertion failure.
                }
            }

            snapshot.Dispose();
            throw;
        }
    }

    private WindowsClipboardSnapshot CaptureSnapshotCore(uint enterpriseFormat)
    {
        var entries = new List<ClipboardFormatHandle>();
        string? enterpriseId = null;
        try
        {
            uint currentFormat = 0;
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var nextFormat = NativeMethods.EnumClipboardFormats(currentFormat);
                if (nextFormat == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                        throw ClipboardError("Could not enumerate all clipboard formats.", error);
                    break;
                }

                if (nextFormat == NativeMethods.CF_OWNERDISPLAY)
                {
                    throw new InvalidOperationException(
                        "The clipboard contains owner-display data that cannot be restored independently.");
                }

                if (nextFormat is >= NativeMethods.CF_PRIVATEFIRST and <= NativeMethods.CF_PRIVATELAST
                    or >= NativeMethods.CF_GDIOBJFIRST and <= NativeMethods.CF_GDIOBJLAST)
                {
                    throw new InvalidOperationException(
                        $"The clipboard contains owner-managed format {nextFormat} that cannot be restored independently.");
                }

                if (nextFormat == enterpriseFormat)
                {
                    // CFSTR_ENTERPRISE_ID is WIP metadata exposed through the EDP APIs. It may
                    // intentionally enumerate without a clipboard data handle.
                    enterpriseId = ReadEnterpriseIdCore();
                    currentFormat = nextFormat;
                    continue;
                }

                var sourceHandle = NativeMethods.GetClipboardData(nextFormat);
                if (sourceHandle == IntPtr.Zero)
                {
                    throw LastClipboardError(
                        $"Could not materialize clipboard format {DescribeFormat(nextFormat)}.");
                }

                var duplicateHandle = DuplicateClipboardHandle(nextFormat, sourceHandle);
                if (duplicateHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"Could not duplicate clipboard format {DescribeFormat(nextFormat)}.");
                }

                entries.Add(new ClipboardFormatHandle(
                    nextFormat,
                    duplicateHandle,
                    _handleReleaseObserver));
                currentFormat = nextFormat;
            }

            return new WindowsClipboardSnapshot(entries, enterpriseId);
        }
        catch
        {
            foreach (var entry in entries)
                entry.Dispose();
            throw;
        }
    }

    private static string? ReadEnterpriseIdCore()
    {
        var result = NativeMethods.EdpGetEnterpriseIdForClipboard(out var enterpriseIdPointer);
        if (result < 0)
        {
            throw new COMException(
                "Could not read Windows Information Protection clipboard metadata.",
                result);
        }

        if (enterpriseIdPointer == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(enterpriseIdPointer);
        }
        finally
        {
            NativeMethods.HeapFree(
                NativeMethods.GetProcessHeap(),
                0,
                enterpriseIdPointer);
        }
    }

    private static void SetEnterpriseIdCore(string enterpriseId)
    {
        var result = NativeMethods.EdpSetEnterpriseIdForClipboard(enterpriseId);
        if (result < 0)
        {
            throw new COMException(
                "Could not restore Windows Information Protection clipboard metadata.",
                result);
        }
    }

    private static string DescribeFormat(uint format)
    {
        if (format < 0xC000)
            return format.ToString();

        var name = new StringBuilder(256);
        var length = NativeMethods.GetClipboardFormatName(format, name, name.Capacity);
        return length > 0 ? $"{format} ('{name}')" : format.ToString();
    }

    private static IntPtr DuplicateClipboardHandle(uint format, IntPtr sourceHandle)
    {
        if (format is NativeMethods.CF_ENHMETAFILE or NativeMethods.CF_DSPENHMETAFILE)
            return NativeMethods.CopyEnhMetaFile(sourceHandle, null);

        var duplicationFormat = format switch
        {
            NativeMethods.CF_DSPBITMAP => NativeMethods.CF_BITMAP,
            NativeMethods.CF_DSPMETAFILEPICT => NativeMethods.CF_METAFILEPICT,
            _ => format
        };

        if (duplicationFormat > ushort.MaxValue)
            return IntPtr.Zero;

        return NativeMethods.OleDuplicateData(
            sourceHandle,
            (ushort)duplicationFormat,
            NativeMethods.GMEM_MOVEABLE);
    }

    private static void ReplaceClipboardWithTextCore(string text, bool excludeFromHistory)
    {
        var exclusionFormat = 0u;
        if (excludeFromHistory)
        {
            exclusionFormat = NativeMethods.RegisterClipboardFormat(
                ExcludeClipboardContentFromMonitorProcessing);
            if (exclusionFormat == 0)
                throw LastClipboardError("Could not register the clipboard history exclusion format.");
        }

        if (!NativeMethods.EmptyClipboard())
            throw LastClipboardError("Could not clear the clipboard.");

        SetUnicodeTextCore(text);
        if (excludeFromHistory)
            SetExclusionMarkerCore(exclusionFormat);
    }

    private static void SetUnicodeTextCore(string text)
    {
        var byteCount = checked((nuint)((text.Length + 1) * sizeof(char)));
        var textHandle = NativeMethods.GlobalAlloc(
            NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
            byteCount);
        if (textHandle == IntPtr.Zero)
            throw LastClipboardError("Could not allocate clipboard text memory.");

        try
        {
            var textPointer = NativeMethods.GlobalLock(textHandle);
            if (textPointer == IntPtr.Zero)
                throw LastClipboardError("Could not lock clipboard text memory.");

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, textPointer, text.Length);
            }
            finally
            {
                NativeMethods.GlobalUnlock(textHandle);
            }

            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, textHandle) == IntPtr.Zero)
                throw LastClipboardError("Could not set Unicode clipboard text.");

            textHandle = IntPtr.Zero;
        }
        finally
        {
            if (textHandle != IntPtr.Zero)
                NativeMethods.GlobalFree(textHandle);
        }
    }

    private static void SetExclusionMarkerCore(uint exclusionFormat)
    {
        var markerHandle = NativeMethods.GlobalAlloc(
            NativeMethods.GMEM_MOVEABLE | NativeMethods.GMEM_ZEROINIT,
            4);
        if (markerHandle == IntPtr.Zero)
            throw LastClipboardError("Could not allocate the clipboard history exclusion marker.");

        try
        {
            if (NativeMethods.SetClipboardData(exclusionFormat, markerHandle) == IntPtr.Zero)
                throw LastClipboardError("Could not set the clipboard history exclusion marker.");

            markerHandle = IntPtr.Zero;
        }
        finally
        {
            if (markerHandle != IntPtr.Zero)
                NativeMethods.GlobalFree(markerHandle);
        }
    }

    private static void RestoreSnapshotCore(WindowsClipboardLease lease)
    {
        RestoreSnapshotCore(lease.Snapshot);
        lease.CompleteAfterRestore();
    }

    private static void RestoreSnapshotCore(WindowsClipboardSnapshot snapshot)
    {
        for (var attempt = 0; attempt < MaxClipboardRestoreAttempts; attempt++)
        {
            try
            {
                using var transferSnapshot = snapshot.DuplicateForTransfer();
                if (!NativeMethods.EmptyClipboard())
                {
                    throw LastClipboardError(
                        "Could not clear the clipboard before restoring its contents.");
                }

                transferSnapshot.TransferToClipboard();
                return;
            }
            catch (ExternalException) when (attempt < MaxClipboardRestoreAttempts - 1)
            {
                // A fresh set of transfer handles is used for every retry. Handles already
                // accepted by SetClipboardData remain owned by Windows.
            }
        }
    }

    private static void TryRollbackSnapshotCore(WindowsClipboardLease lease)
    {
        try
        {
            RestoreSnapshotCore(lease);
        }
        catch
        {
            // Preserve the copy-fallback exception.
        }
    }

    private async Task<T> WithOpenClipboardAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var dispatcher = _dispatcher ?? Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("The WPF application dispatcher is unavailable.");

        for (var attempt = 0; attempt < MaxClipboardOpenAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (dispatcher.CheckAccess())
                    return ExecuteWithOpenClipboardCore(action);

                return await dispatcher.InvokeAsync(
                    () => ExecuteWithOpenClipboardCore(action),
                    DispatcherPriority.Send,
                    cancellationToken);
            }
            catch (ClipboardBusyException) when (attempt < MaxClipboardOpenAttempts - 1)
            {
                await Task.Delay(ClipboardRetryDelay, cancellationToken);
            }
        }

        throw new COMException("Could not open the clipboard after multiple attempts.");
    }

    private T ExecuteWithOpenClipboardCore<T>(Func<T> action)
    {
        var ownerHandle = EnsureOwnerWindow().Handle;
        if (!NativeMethods.OpenClipboard(ownerHandle))
            throw new ClipboardBusyException(Marshal.GetLastPInvokeError());

        T result;
        try
        {
            result = action();
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        if (result is WindowsClipboardLease lease && !lease.HasExpectedSequenceNumber)
        {
            var sequenceNumber = NativeMethods.GetClipboardSequenceNumber();
            if (sequenceNumber == 0 || NativeMethods.GetClipboardOwner() != ownerHandle)
            {
                lease.CompleteWithoutRestore();
                throw new InvalidOperationException(
                    "The clipboard changed before the temporary write could be confirmed.");
            }

            lease.ExpectedSequenceNumber = sequenceNumber;
            lease.HasExpectedSequenceNumber = true;
        }

        return result;
    }

    private HwndSource EnsureOwnerWindow()
    {
        if (_ownerWindow is not null)
            return _ownerWindow;

        _ownerWindow = new HwndSource(new HwndSourceParameters("TypeWhisperClipboardOwner")
        {
            ParentWindow = MessageOnlyWindowParent,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0
        });
        return _ownerWindow;
    }

    private static WindowsClipboardLease RequireLease(IClipboardLease lease) =>
        lease as WindowsClipboardLease
        ?? throw new ArgumentException("The clipboard lease belongs to a different platform.", nameof(lease));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static COMException LastClipboardError(string message) =>
        ClipboardError(message, Marshal.GetLastPInvokeError());

    private static COMException ClipboardError(string message, int error) =>
        new($"{message} Win32 error: {error}.", HResultFromWin32(error));

    private static int HResultFromWin32(int error) =>
        error <= 0 ? error : unchecked((int)(0x80070000u | (uint)error));

    private sealed class ClipboardBusyException(int error)
        : ExternalException("The clipboard is currently in use.", HResultFromWin32(error));

    private sealed class WindowsClipboardLease(WindowsClipboardSnapshot snapshot) : IClipboardLease
    {
        public WindowsClipboardSnapshot Snapshot { get; } = snapshot;
        public uint ExpectedSequenceNumber { get; set; }
        public bool HasExpectedSequenceNumber { get; set; }
        public bool IsCompleted { get; private set; }

        public void CompleteAfterRestore()
        {
            IsCompleted = true;
            Snapshot.Dispose();
        }

        public void CompleteWithoutRestore()
        {
            IsCompleted = true;
            Snapshot.Dispose();
        }

        public void Dispose() => CompleteWithoutRestore();
    }

    private sealed class WindowsClipboardSnapshot(
        List<ClipboardFormatHandle> entries,
        string? enterpriseId = null) : IDisposable
    {
        private readonly List<ClipboardFormatHandle> _entries = entries;
        private readonly string? _enterpriseId = enterpriseId;
        private bool _disposed;

        public WindowsClipboardSnapshot DuplicateForTransfer()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var duplicates = new List<ClipboardFormatHandle>(_entries.Count);
            try
            {
                foreach (var entry in _entries)
                    duplicates.Add(entry.Duplicate());

                return new WindowsClipboardSnapshot(duplicates, _enterpriseId);
            }
            catch
            {
                foreach (var duplicate in duplicates)
                    duplicate.Dispose();
                throw;
            }
        }

        public void TransferToClipboard()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var entry in _entries)
                entry.TransferToClipboard();

            if (_enterpriseId is not null)
                SetEnterpriseIdCore(_enterpriseId);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var entry in _entries)
                entry.Dispose();
        }
    }

    private sealed class ClipboardFormatHandle(
        uint format,
        IntPtr handle,
        Action<uint, IntPtr>? releaseObserver) : IDisposable
    {
        private IntPtr _handle = handle;

        public ClipboardFormatHandle Duplicate()
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            var duplicateHandle = DuplicateClipboardHandle(format, _handle);
            if (duplicateHandle == IntPtr.Zero)
            {
                throw new COMException(
                    $"Could not duplicate clipboard format {DescribeFormat(format)} for restoration.");
            }

            return new ClipboardFormatHandle(format, duplicateHandle, releaseObserver);
        }

        public void TransferToClipboard()
        {
            if (_handle == IntPtr.Zero)
                return;

            if (NativeMethods.SetClipboardData(format, _handle) == IntPtr.Zero)
            {
                throw LastClipboardError(
                    $"Could not restore clipboard format {DescribeFormat(format)}.");
            }

            _handle = IntPtr.Zero;
        }

        public void Dispose()
        {
            var handleToRelease = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handleToRelease == IntPtr.Zero)
                return;

            try
            {
                ReleaseClipboardHandle(format, handleToRelease);
            }
            finally
            {
                releaseObserver?.Invoke(format, handleToRelease);
            }
        }

        private static void ReleaseClipboardHandle(uint clipboardFormat, IntPtr clipboardHandle)
        {
            switch (clipboardFormat)
            {
                case NativeMethods.CF_BITMAP:
                case NativeMethods.CF_DSPBITMAP:
                case NativeMethods.CF_PALETTE:
                    NativeMethods.DeleteObject(clipboardHandle);
                    return;

                case NativeMethods.CF_ENHMETAFILE:
                case NativeMethods.CF_DSPENHMETAFILE:
                    NativeMethods.DeleteEnhMetaFile(clipboardHandle);
                    return;

                case NativeMethods.CF_METAFILEPICT:
                case NativeMethods.CF_DSPMETAFILEPICT:
                    ReleaseMetafilePict(clipboardHandle);
                    return;

                default:
                    NativeMethods.GlobalFree(clipboardHandle);
                    return;
            }
        }

        private static void ReleaseMetafilePict(IntPtr clipboardHandle)
        {
            var dataPointer = NativeMethods.GlobalLock(clipboardHandle);
            if (dataPointer != IntPtr.Zero)
            {
                try
                {
                    var metafile = Marshal.PtrToStructure<NativeMethods.METAFILEPICT>(dataPointer);
                    if (metafile.Metafile != IntPtr.Zero)
                        NativeMethods.DeleteMetaFile(metafile.Metafile);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(clipboardHandle);
                }
            }

            NativeMethods.GlobalFree(clipboardHandle);
        }
    }
}
