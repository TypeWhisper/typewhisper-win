using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// A prepare-once WASAPI capture that releases the endpoint between recording sessions.
/// The buffer setup and packet-reading structure are adapted from NAudio 2.2.1 WasapiCapture.
/// </summary>
internal sealed class WasapiAudioInputCapture : IAudioInputCapture
{
    private const long ReferenceTimesPerSecond = 10_000_000;
    private const long ReferenceTimesPerMillisecond = 10_000;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lifecycleLock = new();
    private readonly MMDevice _device;
    private readonly int _bufferMilliseconds;
    private readonly ManualResetEventSlim _captureStopped = new(initialState: true);

    private AudioClient? _audioClient;
    private AudioCaptureClient? _captureClient;
    private EventWaitHandle? _frameEvent;
    private WaveFormat? _waveFormat;
    private byte[]? _recordBuffer;
    private int _bytesPerFrame;
    private int _waitMilliseconds;
    private Thread? _captureThread;
    private CaptureState _captureState = CaptureState.Stopped;
    private Exception? _terminalException;
    private bool _disposed;
    private bool _disposeAfterCaptureThread;
    private bool _stopTimedOut;
    private bool _resourcesDisposed;
    private bool _stoppedSignalDisposed;

    public WasapiAudioInputCapture(MMDevice device, int bufferMilliseconds)
    {
        _device = device;
        _bufferMilliseconds = bufferMilliseconds;
        AudioCaptureDiagnostics.Log(
            $"WasapiCapture created device={device.FriendlyName} bufferMs={bufferMilliseconds}");
    }

    public event EventHandler<AudioInputDataAvailableEventArgs>? DataAvailable;
    public event EventHandler<AudioInputRecordingStoppedEventArgs>? RecordingStopped;

    public bool CanRestartAfterStop => true;

    public WaveFormat WaveFormat
    {
        get
        {
            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                return (_waveFormat
                    ?? throw new InvalidOperationException("WASAPI capture has not been prepared."))
                    .AsStandardWaveFormat();
            }
        }
    }

    public void Prepare()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (_audioClient is not null)
                return;

            AudioClient? audioClient = null;
            EventWaitHandle? frameEvent = null;
            try
            {
                audioClient = _device.AudioClient;
                var waveFormat = audioClient.MixFormat;
                var requestedDuration = ReferenceTimesPerMillisecond * _bufferMilliseconds;
                var streamFlags = AudioClientStreamFlags.EventCallback
                    | AudioClientStreamFlags.AutoConvertPcm
                    | AudioClientStreamFlags.SrcDefaultQuality;

                audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    streamFlags,
                    requestedDuration,
                    periodicity: 0,
                    waveFormat,
                    Guid.Empty);

                frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                audioClient.SetEventHandle(frameEvent.SafeWaitHandle.DangerousGetHandle());

                var bufferFrameCount = audioClient.BufferSize;
                var bytesPerFrame = waveFormat.Channels * waveFormat.BitsPerSample / 8;
                var actualDuration = (long)(
                    (double)ReferenceTimesPerSecond * bufferFrameCount / waveFormat.SampleRate);

                _audioClient = audioClient;
                _captureClient = audioClient.AudioCaptureClient;
                _frameEvent = frameEvent;
                _waveFormat = waveFormat;
                _bytesPerFrame = bytesPerFrame;
                _recordBuffer = new byte[bufferFrameCount * bytesPerFrame];
                _waitMilliseconds = Math.Max(
                    1,
                    (int)(3 * actualDuration / ReferenceTimesPerMillisecond));

                audioClient = null;
                frameEvent = null;
                AudioCaptureDiagnostics.Log(
                    $"WasapiCapture prepared startIssued=False bufferMs={_bufferMilliseconds} format={AudioRecordingService.DescribeWaveFormat(WaveFormat)}");
            }
            catch
            {
                frameEvent?.Dispose();
                audioClient?.Dispose();
                throw;
            }
        }
    }

    public void StartRecording()
    {
        Prepare();

        Thread captureThread;
        AudioClient audioClient;
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (_stopTimedOut)
            {
                throw new InvalidOperationException(
                    "WASAPI capture must be recreated after a stop timeout.");
            }
            if (_captureState != CaptureState.Stopped || _captureThread is not null)
                throw new InvalidOperationException("Previous WASAPI recording is still in progress.");

            audioClient = _audioClient!;
            _terminalException = null;
            _captureStopped.Reset();
            _captureState = CaptureState.Starting;

            try
            {
                audioClient.Start();
            }
            catch
            {
                _captureState = CaptureState.Stopped;
                _captureStopped.Set();
                throw;
            }

            _captureState = CaptureState.Capturing;
            var captureClient = _captureClient!;
            var frameEvent = _frameEvent!;
            var recordBuffer = _recordBuffer!;
            var bytesPerFrame = _bytesPerFrame;
            var waitMilliseconds = _waitMilliseconds;
            captureThread = new Thread(() => CaptureThread(
                audioClient,
                captureClient,
                frameEvent,
                recordBuffer,
                bytesPerFrame,
                waitMilliseconds))
            {
                IsBackground = true,
                Name = "TypeWhisper WASAPI capture"
            };
            _captureThread = captureThread;
        }

        try
        {
            captureThread.Start();
            AudioCaptureDiagnostics.Log("WasapiCapture started preparedClient=True");
        }
        catch
        {
            lock (_lifecycleLock)
                _captureState = CaptureState.Stopping;

            var cleanupException = StopAndResetClient(audioClient);
            lock (_lifecycleLock)
            {
                _captureThread = null;
                _captureState = CaptureState.Stopped;
                _terminalException = cleanupException;
                _captureStopped.Set();
            }

            throw;
        }
    }

    public void StopRecording()
    {
        Thread? captureThread;
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (_captureState == CaptureState.Stopped && _captureThread is null)
                return;

            _captureState = CaptureState.Stopping;
            captureThread = _captureThread;
            _frameEvent?.Set();
        }

        if (captureThread is not null && ReferenceEquals(Thread.CurrentThread, captureThread))
            return;

        if (!_captureStopped.Wait(StopTimeout))
        {
            lock (_lifecycleLock)
                _stopTimedOut = true;
            throw new TimeoutException(
                $"WASAPI capture did not stop within {StopTimeout.TotalSeconds:F0} seconds.");
        }

        Exception? terminalException;
        lock (_lifecycleLock)
            terminalException = _terminalException;

        if (terminalException is not null)
        {
            throw new InvalidOperationException(
                "WASAPI capture failed while stopping.",
                terminalException);
        }

        AudioCaptureDiagnostics.Log("WasapiCapture stopped and reset preparedClient=True");
    }

    public void Dispose()
    {
        Thread? captureThread;
        bool stopTimedOut;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            captureThread = _captureThread;
            stopTimedOut = _stopTimedOut;
            if (captureThread is not null)
            {
                _captureState = CaptureState.Stopping;
                _frameEvent?.Set();
                if (stopTimedOut)
                {
                    _disposeAfterCaptureThread = true;
                }
                else if (ReferenceEquals(Thread.CurrentThread, captureThread))
                {
                    _disposeAfterCaptureThread = true;
                    return;
                }
            }
        }

        if (captureThread is not null && stopTimedOut)
        {
            AudioCaptureDiagnostics.Log("WasapiCapture discarding client after stop timeout");
            DisposePreparedResources();
            return;
        }

        if (captureThread is not null && !_captureStopped.Wait(StopTimeout))
        {
            AudioCaptureDiagnostics.Log(
                $"WasapiCapture dispose continuing after stop timeoutMs={StopTimeout.TotalMilliseconds:F0}");
            lock (_lifecycleLock)
                _disposeAfterCaptureThread = true;
            DisposePreparedResources();
            return;
        }

        DisposePreparedResources();
        DisposeStoppedSignal();
    }

    private void CaptureThread(
        AudioClient audioClient,
        AudioCaptureClient captureClient,
        EventWaitHandle frameEvent,
        byte[] recordBuffer,
        int bytesPerFrame,
        int waitMilliseconds)
    {
        Exception? captureException = null;
        try
        {
            while (IsCapturing())
            {
                frameEvent.WaitOne(waitMilliseconds, exitContext: false);
                if (!IsCapturing())
                    break;

                ReadNextPacket(captureClient, recordBuffer, bytesPerFrame);
            }
        }
        catch (Exception ex)
        {
            captureException = ex;
        }
        finally
        {
            var cleanupException = StopAndResetClient(audioClient);
            captureException ??= cleanupException;

            lock (_lifecycleLock)
            {
                _captureState = CaptureState.Stopped;
                _terminalException = captureException;
            }

            try
            {
                RecordingStopped?.Invoke(
                    this,
                    new AudioInputRecordingStoppedEventArgs(captureException));
            }
            catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"WASAPI RecordingStopped subscriber failed: {ex.Message}");
            }
            finally
            {
                bool disposeAfterCaptureThread;
                lock (_lifecycleLock)
                {
                    _captureThread = null;
                    disposeAfterCaptureThread = _disposeAfterCaptureThread;
                }
                _captureStopped.Set();
                if (disposeAfterCaptureThread)
                {
                    DisposePreparedResources();
                    DisposeStoppedSignal();
                }
            }
        }
    }

    private bool IsCapturing()
    {
        lock (_lifecycleLock)
            return _captureState == CaptureState.Capturing;
    }

    private void ReadNextPacket(
        AudioCaptureClient captureClient,
        byte[] recordBuffer,
        int bytesPerFrame)
    {
        var packetSize = captureClient.GetNextPacketSize();
        var recordBufferOffset = 0;

        while (packetSize != 0)
        {
            var buffer = captureClient.GetBuffer(
                out var framesAvailable,
                out var flags);
            try
            {
                var bytesAvailable = framesAvailable * bytesPerFrame;
                var spaceRemaining = Math.Max(0, recordBuffer.Length - recordBufferOffset);
                if (spaceRemaining < bytesAvailable && recordBufferOffset > 0)
                {
                    RaiseDataAvailable(recordBuffer, recordBufferOffset);
                    recordBufferOffset = 0;
                }

                if (bytesAvailable > recordBuffer.Length)
                {
                    throw new InvalidOperationException(
                        "WASAPI returned more audio data than the initialized capture buffer can hold.");
                }

                if ((flags & AudioClientBufferFlags.Silent) == 0)
                    Marshal.Copy(buffer, recordBuffer, recordBufferOffset, bytesAvailable);
                else
                    Array.Clear(recordBuffer, recordBufferOffset, bytesAvailable);

                recordBufferOffset += bytesAvailable;
            }
            finally
            {
                captureClient.ReleaseBuffer(framesAvailable);
            }
            packetSize = captureClient.GetNextPacketSize();
        }

        if (recordBufferOffset > 0)
            RaiseDataAvailable(recordBuffer, recordBufferOffset);
    }

    private void RaiseDataAvailable(byte[] buffer, int bytesRecorded) =>
        DataAvailable?.Invoke(
            this,
            new AudioInputDataAvailableEventArgs(buffer, bytesRecorded));

    private static Exception? StopAndResetClient(AudioClient audioClient)
    {
        Exception? failure = null;
        try
        {
            audioClient.Stop();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            audioClient.Reset();
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        return failure;
    }

    private void DisposePreparedResources()
    {
        AudioClient? audioClient;
        EventWaitHandle? frameEvent;
        lock (_lifecycleLock)
        {
            if (_resourcesDisposed)
                return;

            _resourcesDisposed = true;
            audioClient = _audioClient;
            frameEvent = _frameEvent;
            _audioClient = null;
            _captureClient = null;
            _frameEvent = null;
            _waveFormat = null;
            _recordBuffer = null;
        }

        DisposeSafely(frameEvent, "frame event");
        DisposeSafely(audioClient, "audio client");
        DisposeSafely(_device, "device");
    }

    private void DisposeStoppedSignal()
    {
        lock (_lifecycleLock)
        {
            if (_stoppedSignalDisposed)
                return;
            _stoppedSignalDisposed = true;
        }

        DisposeSafely(_captureStopped, "stop signal");
    }

    private static void DisposeSafely(IDisposable? resource, string resourceName)
    {
        if (resource is null)
            return;

        try
        {
            resource.Dispose();
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            AudioCaptureDiagnostics.Log(
                $"WasapiCapture {resourceName} disposal failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WasapiAudioInputCapture));
    }
}
