namespace TypeWhisper.Presentation;

/// <summary>The storage-file and completion operations supplied by Windows sharing.</summary>
public interface ISharedFileOperation
{
    /// <summary>Notifies Windows that reception has started.</summary>
    void ReportStarted();
    /// <summary>Gets local file paths; unsupported items must not be silently omitted.</summary>
    Task<IReadOnlyList<string>> ReadPathsAsync();
    /// <summary>Notifies Windows that the shared data has been retrieved.</summary>
    void ReportDataRetrieved();
    /// <summary>Acknowledges a successfully admitted request.</summary>
    void ReportCompleted();
    /// <summary>Reports a rejected or failed share operation.</summary>
    void ReportError(string message);
}

/// <summary>Queues shared files for explicit processing using the ordinary bounded activation path.</summary>
public static class SharedFileActivation
{
    /// <summary>Rejects a share when the host cannot open its profile, without acknowledging receipt.</summary>
    public static void Reject(ISharedFileOperation operation, string message)
    {
        try { operation.ReportError(message); }
        catch (Exception reportError) when (reportError is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Share error reporting failed: {0}", reportError); }
    }

    /// <summary>Completes the Windows share operation only after valid files have entered the activation inbox.</summary>
    public static async Task ReceiveAsync(ISharedFileOperation operation, ActivationInbox inbox,
        Func<bool>? canReceive = null, Task<bool>? startupReady = null)
    {
        try
        {
            operation.ReportStarted();
            if ((startupReady is not null && !await startupReady) || canReceive?.Invoke() == false)
            {
                Reject(operation, "TypeWhisper cannot receive files while its profile is unavailable or the app is shutting down. Please try again after reopening it.");
                return;
            }
            var paths = await operation.ReadPathsAsync();
            operation.ReportDataRetrieved();
            var request = ApplicationActivationRequest.Parse(new[] { "--transcribe-file" }.Concat(paths));
            if (request.Error is not null) { operation.ReportError(request.Error); return; }
            // Profile restoration or shutdown can begin while Windows is retrieving files.
            if (canReceive?.Invoke() == false || !inbox.TryAdd(request))
            {
                operation.ReportError("TypeWhisper is busy or shutting down. Please share the files again.");
                return;
            }
            operation.ReportCompleted();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError("Shared file activation failed: {0}", ex);
            Reject(operation, "TypeWhisper could not receive the shared files. Please try again.");
        }
    }
}
