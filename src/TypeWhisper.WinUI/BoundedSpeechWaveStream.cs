namespace TypeWhisper.WinUI;

// SAPI may seek back to update the WAV header. Every allocation-growing path stays bounded.
internal sealed class BoundedSpeechWaveStream(long maximumBytes) : MemoryStream(checked((int)maximumBytes))
{
    private void Check(long position)
    {
        if (position < 0 || position > maximumBytes)
            throw new IOException("Synthesized speech exceeded the audio memory limit.");
    }
    public override long Position { get => base.Position; set { Check(value); base.Position = value; } }
    public override void SetLength(long value) { Check(value); base.SetLength(value); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        Check(target); return base.Seek(offset, origin);
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(checked(Position + count)); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(checked(Position + buffer.Length)); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(checked(Position + 1)); base.WriteByte(value); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
}
