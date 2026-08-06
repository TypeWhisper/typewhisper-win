using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Describes a durable dictation recovery recording.
/// </summary>
public sealed record RecoveryRecordingDescriptor(
    string Id,
    string FileName,
    DateTimeOffset CreatedAt,
    double DurationSeconds,
    long FileSizeBytes);

/// <summary>
/// Owns a finalized recording until the dictation pipeline decides whether to keep it.
/// </summary>
public sealed class RecoveryRecordingLease
{
    private readonly DictationRecoveryAudioStore _store;
    private readonly Guid _token;

    internal RecoveryRecordingLease(DictationRecoveryAudioStore store, Guid token)
    {
        _store = store;
        _token = token;
    }

    /// <summary>
    /// Gets the durable recovery file name after the lease has been preserved.
    /// </summary>
    public string? RecoveryFileName { get; private set; }

    /// <summary>
    /// Promotes the pending recording to a durable recovery recording.
    /// </summary>
    public async Task<RecoveryRecordingDescriptor?> PreserveAsync(CancellationToken cancellationToken = default)
    {
        var descriptor = await _store.PreserveLeaseAsync(_token, cancellationToken).ConfigureAwait(false);
        RecoveryFileName = descriptor?.FileName ?? RecoveryFileName;
        return descriptor;
    }

    /// <summary>
    /// Discards the pending recording.
    /// </summary>
    public Task DiscardAsync(CancellationToken cancellationToken = default) =>
        _store.DiscardLeaseAsync(_token, cancellationToken);
}

/// <summary>
/// Serializes recovery WAV file access away from capture and UI threads.
/// </summary>
public sealed class DictationRecoveryAudioStore : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Sample rate used by the normalized dictation capture pipeline.
    /// </summary>
    public const int SampleRate = 16_000;

    private const int WavHeaderSize = 44;
    private const int ImmediatelyRetentionDays = -1;
    private const int NeverRetentionDays = 0;
    private static readonly Regex FinalFileNamePattern = new(
        @"\Adictation-recovery-(?<timestamp>\d{8}-\d{6}-\d{3})-(?<sequence>\d{4})\.wav\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ActiveFileNamePattern = new(
        @"\Adictation-recovery-\d{8}-\d{6}-\d{3}-\d{4}\.active\.wav\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PendingFileNamePattern = new(
        @"\Adictation-recovery-\d{8}-\d{6}-\d{3}-\d{4}\.pending\.wav\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _rootPath;
    private readonly StringComparison _pathComparison;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Channel<Func<Task>> _operations;
    private readonly Task _worker;
    private readonly Task _initialization;
    private readonly Dictionary<Guid, ActiveRecording> _activeRecordings = [];
    private readonly Dictionary<Guid, PendingRecording> _pendingRecordings = [];
    private readonly Dictionary<string, RecoveryRecordingDescriptor> _recordings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _snapshotGate = new();
    private IReadOnlyList<RecoveryRecordingDescriptor> _snapshot = [];
    private volatile int _retentionDays = 30;
    private volatile bool _rootAvailable;
    private int _sequence;
    private bool _disposed;

    /// <summary>
    /// Creates a recovery audio store.
    /// </summary>
    public DictationRecoveryAudioStore(
        string? rootPath = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _rootPath = Path.GetFullPath(rootPath ?? TypeWhisperEnvironment.DictationRecoveryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _operations = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _worker = Task.Run(ProcessOperationsAsync);
        _initialization = EnqueueAsync(
            () =>
            {
                InitializeFromDisk();
                return Task.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Raised whenever the durable recovery list changes.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets recovery recordings ordered newest first.
    /// </summary>
    public IReadOnlyList<RecoveryRecordingDescriptor> Recordings
    {
        get
        {
            lock (_snapshotGate)
                return _snapshot;
        }
    }

    /// <summary>
    /// Gets whether at least one durable recovery recording exists.
    /// </summary>
    public bool HasRecordings => Recordings.Count > 0;

    /// <summary>
    /// Gets the effective retention value in days. -1 means immediately and 0 means never.
    /// </summary>
    public int RetentionDays => _retentionDays;

    /// <summary>
    /// Waits until startup cleanup and pending-file promotion have completed on the store worker.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _initialization.WaitAsync(cancellationToken);

    /// <summary>
    /// Starts a recovery recording without performing file I/O on the caller's thread.
    /// </summary>
    public Guid? BeginRecording()
    {
        ThrowIfDisposed();
        if (_retentionDays == ImmediatelyRetentionDays)
            return null;

        var recordingId = Guid.NewGuid();
        if (!TryQueue(() => BeginRecordingCoreAsync(recordingId)))
            return null;

        return recordingId;
    }

    /// <summary>
    /// Queues normalized 16 kHz mono samples for the active recovery recording.
    /// </summary>
    public void AppendSamples(Guid recordingId, float[] samples)
    {
        if (_disposed || samples.Length == 0 || _retentionDays == ImmediatelyRetentionDays)
            return;

        _ = TryQueue(() => AppendSamplesCoreAsync(recordingId, samples));
    }

    /// <summary>
    /// Finalizes an active recording as a pending lease.
    /// </summary>
    public Task<RecoveryRecordingLease?> FinalizeRecordingAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => FinalizeRecordingCoreAsyncForPublic(recordingId), cancellationToken);

    /// <summary>
    /// Immediately preserves an active recording after an unexpected capture stop.
    /// </summary>
    public Task<RecoveryRecordingDescriptor?> PreserveActiveRecordingAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(async () =>
        {
            var lease = await FinalizeRecordingCoreAsync(recordingId).ConfigureAwait(false);
            return lease is null
                ? null
                : await PreserveLeaseCoreAsync(lease.Token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Discards an active recording for cancellation, no speech, or a clip that is too short.
    /// </summary>
    public Task DiscardActiveRecordingAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => DiscardActiveRecordingCoreAsync(recordingId), cancellationToken);

    /// <summary>
    /// Applies retention and immediately performs cleanup.
    /// </summary>
    public Task SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRetentionDays(retentionDays);
        _retentionDays = normalized;
        return EnqueueAsync(() => ApplyRetentionCoreAsync(normalized), cancellationToken);
    }

    /// <summary>
    /// Refreshes and cleans the durable recovery list.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(RefreshCoreAsync, cancellationToken);

    /// <summary>
    /// Deletes an internally enumerated recovery recording.
    /// </summary>
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        EnqueueAsync(() => DeleteCoreAsync(id), cancellationToken);

    /// <summary>
    /// Deletes every internally enumerated durable recovery recording.
    /// </summary>
    public Task DeleteAllAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(DeleteAllCoreAsync, cancellationToken);

    /// <summary>
    /// Returns a validated path for an internally enumerated recovery recording.
    /// </summary>
    public string? GetRecordingPath(string id)
    {
        RecoveryRecordingDescriptor? descriptor;
        lock (_snapshotGate)
            descriptor = _snapshot.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

        if (descriptor is null)
            return null;

        return TryResolveSafeFile(descriptor.FileName, FinalFileNamePattern, out var path)
            ? path
            : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _operations.Writer.TryComplete();
        await _worker.ConfigureAwait(false);

        foreach (var active in _activeRecordings.Values)
            await DisposeStreamAsync(active.Stream).ConfigureAwait(false);
        _activeRecordings.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal Task<RecoveryRecordingDescriptor?> PreserveLeaseAsync(
        Guid token,
        CancellationToken cancellationToken) =>
        EnqueueAsync(() => PreserveLeaseCoreAsync(token), cancellationToken);

    internal Task DiscardLeaseAsync(Guid token, CancellationToken cancellationToken) =>
        EnqueueAsync(() => DiscardLeaseCoreAsync(token), cancellationToken);

    private void InitializeFromDisk()
    {
        if (!EnsureRootAvailable())
        {
            _recordings.Clear();
            PublishSnapshot(false);
            return;
        }

        RecoverInterruptedFiles();
        EnumerateDurableRecordings();
    }

    private void RecoverInterruptedFiles()
    {
        foreach (var path in EnumerateRootFiles())
        {
            var fileName = Path.GetFileName(path);
            if (ActiveFileNamePattern.IsMatch(fileName))
            {
                TryDeleteSafeFile(fileName, ActiveFileNamePattern);
                continue;
            }

            if (!PendingFileNamePattern.IsMatch(fileName)
                || !TryResolveSafeFile(fileName, PendingFileNamePattern, out var pendingPath))
            {
                continue;
            }

            if (!TryReadDescriptor(pendingPath, pending: true, out _))
            {
                TryDeleteSafeFile(fileName, PendingFileNamePattern);
                continue;
            }

            var finalName = fileName.Replace(".pending.wav", ".wav", StringComparison.Ordinal);
            if (!TryResolveSafeFile(finalName, FinalFileNamePattern, out var finalPath))
                continue;

            try
            {
                if (!File.Exists(finalPath))
                    File.Move(pendingPath, finalPath);
                else
                    TryDeleteSafeFile(fileName, PendingFileNamePattern);
            }
            catch
            {
                // Leave a valid pending file in place so a later startup can retry promotion.
            }
        }
    }

    private void EnumerateDurableRecordings()
    {
        _recordings.Clear();
        if (!_rootAvailable)
        {
            PublishSnapshot(false);
            return;
        }

        foreach (var path in EnumerateRootFiles())
        {
            var fileName = Path.GetFileName(path);
            if (!FinalFileNamePattern.IsMatch(fileName)
                || !TryResolveSafeFile(fileName, FinalFileNamePattern, out var safePath)
                || !TryReadDescriptor(safePath, pending: false, out var descriptor))
            {
                continue;
            }

            _recordings[descriptor.Id] = descriptor;
        }

        PublishSnapshot(false);
    }

    private async Task ProcessOperationsAsync()
    {
        await foreach (var operation in _operations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch
            {
                // Individual operations own their completion result. The worker must stay alive.
            }
        }
    }

    private async Task BeginRecordingCoreAsync(Guid recordingId)
    {
        if (_retentionDays == ImmediatelyRetentionDays || !EnsureRootAvailable())
            return;

        await CleanupRetentionCoreAsync().ConfigureAwait(false);

        FileStream? stream = null;
        string? fileName = null;
        try
        {
            var createdAt = _utcNow();
            var baseName = ReserveBaseName(createdAt);
            fileName = baseName + ".active.wav";
            if (!TryResolveSafeFile(fileName, ActiveFileNamePattern, out var path))
                return;

            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await WriteWavHeaderAsync(stream, 0).ConfigureAwait(false);
            _activeRecordings[recordingId] = new ActiveRecording(
                recordingId,
                createdAt,
                baseName,
                fileName,
                path,
                stream);
            stream = null;
        }
        catch
        {
            await DisposeStreamAsync(stream).ConfigureAwait(false);
            if (fileName is not null)
                TryDeleteSafeFile(fileName, ActiveFileNamePattern);
            // Recovery storage is best effort and must never stop in-memory transcription.
        }
    }

    private async Task AppendSamplesCoreAsync(Guid recordingId, float[] samples)
    {
        if (!_activeRecordings.TryGetValue(recordingId, out var recording)
            || recording.Stream is null)
        {
            return;
        }

        try
        {
            var pcm = new byte[samples.Length * sizeof(short)];
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = Math.Clamp(samples[i], -1f, 1f);
                var pcmValue = sample < 0
                    ? (short)Math.Round(sample * 32768f)
                    : (short)Math.Round(sample * 32767f);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * sizeof(short), sizeof(short)), pcmValue);
            }

            await recording.Stream.WriteAsync(pcm).ConfigureAwait(false);
            recording.SampleCount += samples.Length;
        }
        catch
        {
            await DisableActiveRecordingAsync(recording).ConfigureAwait(false);
        }
    }

    private async Task<LeaseHandle?> FinalizeRecordingCoreAsync(Guid recordingId)
    {
        if (!_activeRecordings.Remove(recordingId, out var recording))
            return null;

        if (recording.Stream is null || recording.SampleCount <= 0)
        {
            await DisposeStreamAsync(recording.Stream).ConfigureAwait(false);
            TryDeleteSafeFile(recording.FileName, ActiveFileNamePattern);
            return null;
        }

        try
        {
            await RewriteWavHeaderAsync(recording.Stream, recording.SampleCount * sizeof(short)).ConfigureAwait(false);
            await recording.Stream.FlushAsync().ConfigureAwait(false);
            recording.Stream.Flush(flushToDisk: true);
            await recording.Stream.DisposeAsync().ConfigureAwait(false);
            recording.Stream = null;

            var pendingFileName = recording.BaseName + ".pending.wav";
            if (!TryResolveSafeFile(pendingFileName, PendingFileNamePattern, out var pendingPath))
                return null;

            File.Move(recording.Path, pendingPath);
            var token = Guid.NewGuid();
            _pendingRecordings[token] = new PendingRecording(
                token,
                recording.CreatedAt,
                recording.BaseName,
                pendingFileName,
                pendingPath,
                recording.SampleCount);
            return new LeaseHandle(token);
        }
        catch
        {
            await DisposeStreamAsync(recording.Stream).ConfigureAwait(false);
            TryDeleteSafeFile(recording.FileName, ActiveFileNamePattern);
            return null;
        }
    }

    private async Task<RecoveryRecordingLease?> FinalizeRecordingCoreAsyncForPublic(Guid recordingId)
    {
        var handle = await FinalizeRecordingCoreAsync(recordingId).ConfigureAwait(false);
        return handle is null ? null : new RecoveryRecordingLease(this, handle.Token);
    }

    private async Task<RecoveryRecordingDescriptor?> PreserveLeaseCoreAsync(Guid token)
    {
        if (!_pendingRecordings.Remove(token, out var pending))
            return null;

        if (_retentionDays == ImmediatelyRetentionDays)
        {
            TryDeleteSafeFile(pending.FileName, PendingFileNamePattern);
            return null;
        }

        var finalFileName = pending.BaseName + ".wav";
        if (!TryResolveSafeFile(finalFileName, FinalFileNamePattern, out var finalPath))
        {
            _pendingRecordings[token] = pending;
            return null;
        }

        try
        {
            File.Move(pending.Path, finalPath);
            var descriptor = new RecoveryRecordingDescriptor(
                finalFileName,
                finalFileName,
                pending.CreatedAt,
                pending.SampleCount / (double)SampleRate,
                new FileInfo(finalPath).Length);
            _recordings[descriptor.Id] = descriptor;
            PublishSnapshot();
            await CleanupRetentionCoreAsync().ConfigureAwait(false);
            return _recordings.GetValueOrDefault(descriptor.Id);
        }
        catch
        {
            // Keep a valid pending file on disk for startup promotion after a transient file error.
            _pendingRecordings[token] = pending;
            return null;
        }
    }

    private Task DiscardLeaseCoreAsync(Guid token)
    {
        if (_pendingRecordings.Remove(token, out var pending))
            TryDeleteSafeFile(pending.FileName, PendingFileNamePattern);
        return Task.CompletedTask;
    }

    private async Task DiscardActiveRecordingCoreAsync(Guid recordingId)
    {
        if (!_activeRecordings.Remove(recordingId, out var recording))
            return;

        await DisposeStreamAsync(recording.Stream).ConfigureAwait(false);
        TryDeleteSafeFile(recording.FileName, ActiveFileNamePattern);
    }

    private async Task ApplyRetentionCoreAsync(int retentionDays)
    {
        if (retentionDays == ImmediatelyRetentionDays)
        {
            foreach (var recording in _activeRecordings.Values)
            {
                await DisposeStreamAsync(recording.Stream).ConfigureAwait(false);
                TryDeleteSafeFile(recording.FileName, ActiveFileNamePattern);
            }
            _activeRecordings.Clear();

            foreach (var pending in _pendingRecordings.Values)
                TryDeleteSafeFile(pending.FileName, PendingFileNamePattern);
            _pendingRecordings.Clear();

            await DeleteAllCoreAsync().ConfigureAwait(false);
            return;
        }

        await CleanupRetentionCoreAsync().ConfigureAwait(false);
    }

    private Task RefreshCoreAsync()
    {
        var wasAvailable = _rootAvailable;
        if (!EnsureRootAvailable())
        {
            _recordings.Clear();
            PublishSnapshot();
            return Task.CompletedTask;
        }

        if (!wasAvailable)
            RecoverInterruptedFiles();
        EnumerateDurableRecordings();
        return CleanupRetentionCoreAsync();
    }

    private Task CleanupRetentionCoreAsync()
    {
        var retentionDays = _retentionDays;
        if (retentionDays is ImmediatelyRetentionDays or NeverRetentionDays)
            return Task.CompletedTask;

        var cutoff = _utcNow().AddDays(-retentionDays);
        var changed = false;
        foreach (var descriptor in _recordings.Values.ToArray())
        {
            if (descriptor.CreatedAt >= cutoff)
                continue;

            if (TryDeleteSafeFile(descriptor.FileName, FinalFileNamePattern))
            {
                _recordings.Remove(descriptor.Id);
                changed = true;
            }
        }

        if (changed)
            PublishSnapshot();
        return Task.CompletedTask;
    }

    private Task<bool> DeleteCoreAsync(string id)
    {
        if (!_recordings.TryGetValue(id, out var descriptor))
            return Task.FromResult(false);

        if (!TryDeleteSafeFile(descriptor.FileName, FinalFileNamePattern))
            return Task.FromResult(false);

        _recordings.Remove(id);
        PublishSnapshot();
        return Task.FromResult(true);
    }

    private Task DeleteAllCoreAsync()
    {
        var changed = false;
        foreach (var descriptor in _recordings.Values.ToArray())
        {
            if (!TryDeleteSafeFile(descriptor.FileName, FinalFileNamePattern))
                continue;

            _recordings.Remove(descriptor.Id);
            changed = true;
        }

        if (changed)
            PublishSnapshot();
        return Task.CompletedTask;
    }

    private async Task DisableActiveRecordingAsync(ActiveRecording recording)
    {
        _activeRecordings.Remove(recording.Id);
        await DisposeStreamAsync(recording.Stream).ConfigureAwait(false);
        recording.Stream = null;
        TryDeleteSafeFile(recording.FileName, ActiveFileNamePattern);
    }

    private string ReserveBaseName(DateTimeOffset createdAt)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var sequence = Interlocked.Increment(ref _sequence) % 10_000;
            var baseName = string.Create(
                CultureInfo.InvariantCulture,
                $"dictation-recovery-{createdAt.UtcDateTime:yyyyMMdd-HHmmss-fff}-{sequence:0000}");
            if (!File.Exists(Path.Combine(_rootPath, baseName + ".active.wav"))
                && !File.Exists(Path.Combine(_rootPath, baseName + ".pending.wav"))
                && !File.Exists(Path.Combine(_rootPath, baseName + ".wav")))
            {
                return baseName;
            }
        }

        throw new IOException("No recovery recording file name is available.");
    }

    private bool TryResolveSafeFile(string fileName, Regex requiredPattern, out string path)
    {
        path = string.Empty;
        if (!_rootAvailable
            || string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || !requiredPattern.IsMatch(fileName))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_rootPath, fileName));
        var parent = Path.GetDirectoryName(candidate)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.Equals(parent, _rootPath, _pathComparison))
            return false;

        if (File.Exists(candidate))
        {
            try
            {
                var attributes = File.GetAttributes(candidate);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    return false;
            }
            catch
            {
                return false;
            }
        }

        path = candidate;
        return true;
    }

    private bool EnsureRootAvailable()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                var attributes = File.GetAttributes(_rootPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || (attributes & FileAttributes.Directory) == 0)
                {
                    _rootAvailable = false;
                    return false;
                }
            }
            else
            {
                Directory.CreateDirectory(_rootPath);
            }

            _rootAvailable = true;
            return true;
        }
        catch
        {
            _rootAvailable = false;
            return false;
        }
    }

    private IReadOnlyList<string> EnumerateRootFiles()
    {
        try
        {
            return Directory.GetFiles(_rootPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            _rootAvailable = false;
            return [];
        }
    }

    private bool TryDeleteSafeFile(string fileName, Regex requiredPattern)
    {
        if (!TryResolveSafeFile(fileName, requiredPattern, out var path))
            return false;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadDescriptor(
        string path,
        bool pending,
        out RecoveryRecordingDescriptor descriptor)
    {
        descriptor = default!;
        try
        {
            var fileName = Path.GetFileName(path);
            var finalFileName = pending
                ? fileName.Replace(".pending.wav", ".wav", StringComparison.Ordinal)
                : fileName;
            var match = FinalFileNamePattern.Match(finalFileName);
            if (!match.Success
                || !DateTimeOffset.TryParseExact(
                    match.Groups["timestamp"].Value,
                    "yyyyMMdd-HHmmss-fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var createdAt))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < WavHeaderSize)
                return false;

            Span<byte> header = stackalloc byte[WavHeaderSize];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8)
                || !header.Slice(8, 4).SequenceEqual("WAVE"u8)
                || !header.Slice(12, 4).SequenceEqual("fmt "u8)
                || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4)) != 16
                || BinaryPrimitives.ReadInt16LittleEndian(header.Slice(20, 2)) != 1
                || BinaryPrimitives.ReadInt16LittleEndian(header.Slice(22, 2)) != 1
                || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(24, 4)) != SampleRate
                || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(28, 4)) != SampleRate * sizeof(short)
                || BinaryPrimitives.ReadInt16LittleEndian(header.Slice(32, 2)) != sizeof(short)
                || BinaryPrimitives.ReadInt16LittleEndian(header.Slice(34, 2)) != 16
                || !header.Slice(36, 4).SequenceEqual("data"u8))
            {
                return false;
            }

            var dataLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(40, 4));
            if (dataLength <= 0
                || dataLength % sizeof(short) != 0
                || stream.Length != WavHeaderSize + dataLength
                || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4)) != 36 + dataLength)
            {
                return false;
            }

            descriptor = new RecoveryRecordingDescriptor(
                finalFileName,
                finalFileName,
                createdAt,
                dataLength / (double)(SampleRate * sizeof(short)),
                stream.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void PublishSnapshot(bool raiseChanged = true)
    {
        var snapshot = _recordings.Values
            .OrderByDescending(recording => recording.CreatedAt)
            .ThenByDescending(recording => recording.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_snapshotGate)
            _snapshot = snapshot;
        if (!raiseChanged || Changed is not { } handlers)
            return;

        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); } catch { }
        }
    }

    private static int NormalizeRetentionDays(int days) =>
        days is -1 or 0 or 1 or 7 or 30 or 60 or 90 or 180 ? days : 30;

    private static async Task WriteWavHeaderAsync(Stream stream, int dataLength)
    {
        var header = BuildWavHeader(dataLength);
        await stream.WriteAsync(header).ConfigureAwait(false);
    }

    private static async Task RewriteWavHeaderAsync(Stream stream, long dataLength)
    {
        if (dataLength > int.MaxValue)
            throw new IOException("Recovery recording exceeds the WAV size limit.");

        stream.Position = 0;
        await WriteWavHeaderAsync(stream, (int)dataLength).ConfigureAwait(false);
        stream.Position = stream.Length;
    }

    private static byte[] BuildWavHeader(int dataLength)
    {
        var header = new byte[WavHeaderSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), SampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), dataLength);
        return header;
    }

    private static async Task DisposeStreamAsync(FileStream? stream)
    {
        if (stream is not null)
            await stream.DisposeAsync().ConfigureAwait(false);
    }

    private bool TryQueue(Func<Task> operation) => _operations.Writer.TryWrite(operation);

    private Task EnqueueAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryQueue(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new ObjectDisposedException(nameof(DictationRecoveryAudioStore)));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private Task<T> EnqueueAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryQueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await operation().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new ObjectDisposedException(nameof(DictationRecoveryAudioStore)));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class ActiveRecording(
        Guid id,
        DateTimeOffset createdAt,
        string baseName,
        string fileName,
        string path,
        FileStream stream)
    {
        public Guid Id { get; } = id;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public string BaseName { get; } = baseName;
        public string FileName { get; } = fileName;
        public string Path { get; } = path;
        public FileStream? Stream { get; set; } = stream;
        public long SampleCount { get; set; }
    }

    private sealed record PendingRecording(
        Guid Token,
        DateTimeOffset CreatedAt,
        string BaseName,
        string FileName,
        string Path,
        long SampleCount);

    private sealed record LeaseHandle(Guid Token);
}
