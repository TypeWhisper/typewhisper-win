using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace TypeWhisper.WinUI.Platform;

internal enum AudioPacketKind { WindowsSilent, ZeroSamples, Signal }

internal readonly record struct AudioPacketCounts(long WindowsSilent, long ZeroSamples, long Signal, AudioPacketKind LastKind)
{
    internal long Total => WindowsSilent + ZeroSamples + Signal;

    internal AudioPacketCounts Add(AudioPacketKind kind) => new(
        WindowsSilent + (kind == AudioPacketKind.WindowsSilent ? 1 : 0),
        ZeroSamples + (kind == AudioPacketKind.ZeroSamples ? 1 : 0),
        Signal + (kind == AudioPacketKind.Signal ? 1 : 0), kind);

    internal AudioPacketCounts Add(AudioPacketCounts other) => other.Total == 0 ? this : new(
        WindowsSilent + other.WindowsSilent, ZeroSamples + other.ZeroSamples,
        Signal + other.Signal, other.LastKind);
}

internal static class AudioPacketDiagnostics
{
    // A SILENT packet need not have a readable data pointer. Preserve its origin before
    // filling our buffer with zeros, and inspect unflagged data before downmixing/resampling.
    internal static AudioPacketKind CopyPacket(IntPtr source, byte[] destination, int offset,
        int byteCount, bool windowsSilent, WaveFormat format)
    {
        if (windowsSilent)
        {
            Array.Clear(destination, offset, byteCount);
            return AudioPacketKind.WindowsSilent;
        }

        Marshal.Copy(source, destination, offset, byteCount);
        return Classify(destination.AsSpan(offset, byteCount), format);
    }

    internal static AudioPacketKind Classify(ReadOnlySpan<byte> data, WaveFormat format)
    {
        format = format.AsStandardWaveFormat();
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            // Both signs of floating-point zero are silence; tiny nonzero values remain signal.
            var bytesPerSample = format.BitsPerSample / 8;
            for (var offset = 0; offset < data.Length; offset += bytesPerSample)
            {
                var isZero = bytesPerSample switch
                {
                    4 => (BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) & 0x7fffffff) == 0,
                    8 => (BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]) & 0x7fffffffffffffff) == 0,
                    _ => throw new NotSupportedException("Unsupported floating-point microphone format.")
                };
                if (!isZero) return AudioPacketKind.Signal;
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            // Eight-bit PCM is unsigned: its numeric zero is encoded as 128.
            var zero = format.BitsPerSample == 8 ? (byte)128 : (byte)0;
            foreach (var value in data)
                if (value != zero) return AudioPacketKind.Signal;
        }
        else throw new NotSupportedException("Unsupported microphone packet format.");

        return AudioPacketKind.ZeroSamples;
    }
}

internal enum MicrophoneTestState { WaitingForPackets, NoPackets, WindowsSilent, ZeroSamples, Signal, Failed }

internal sealed record MicrophoneTestSnapshot(string DeviceName, bool Running, bool HasWindowsFlags,
    MicrophoneTestState State, AudioPacketCounts Packets, TimeSpan Duration, TimeSpan PacketAge,
    float Peak, string? Error);

internal sealed class MicrophoneTestDiagnostics(string deviceName, bool hasWindowsFlags, TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan PacketTimeout = TimeSpan.FromSeconds(1);
    private readonly object _lock = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly long _started = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private long? _lastPacket;
    private long? _stopped;
    private AudioPacketCounts _packets;
    private float _peak;
    private string? _error;
    private bool _hasWindowsFlags = hasWindowsFlags;

    internal void SetWindowsFlags(bool available)
    {
        lock (_lock) _hasWindowsFlags = available;
    }

    internal void Observe(AudioPacketCounts packets, float peak)
    {
        if (packets.Total == 0) return;
        lock (_lock)
        {
            if (_stopped.HasValue) return;
            _lastPacket = _clock.GetTimestamp();
            _packets = _packets.Add(packets);
            _peak = float.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
        }
    }

    internal void Stop(string? error = null)
    {
        lock (_lock)
        {
            _stopped ??= _clock.GetTimestamp();
            _error ??= error;
        }
    }

    internal MicrophoneTestSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = _stopped ?? _clock.GetTimestamp();
            var age = _clock.GetElapsedTime(_lastPacket ?? _started, now);
            var state = _error is not null ? MicrophoneTestState.Failed
                : age >= PacketTimeout ? MicrophoneTestState.NoPackets
                : !_lastPacket.HasValue ? MicrophoneTestState.WaitingForPackets
                : _packets.LastKind switch
                {
                    AudioPacketKind.WindowsSilent => MicrophoneTestState.WindowsSilent,
                    AudioPacketKind.ZeroSamples => MicrophoneTestState.ZeroSamples,
                    _ => MicrophoneTestState.Signal
                };
            return new(deviceName, !_stopped.HasValue, _hasWindowsFlags, state, _packets,
                _clock.GetElapsedTime(_started, now), age, age >= PacketTimeout ? 0 : _peak, _error);
        }
    }
}
