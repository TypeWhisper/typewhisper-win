using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Plugin.CloudflareAsr;

// Encode directly to the upload stream without duplicating the complete WAV as strings or JSON buffers.
internal sealed class TurboAudioContent : HttpContent
{
    private readonly byte[] _audio;
    private readonly byte[] _suffix;
    private static readonly byte[] Prefix = "{\"audio\":\""u8.ToArray();

    internal TurboAudioContent(byte[] audio, string? language, string? prompt)
    {
        _audio = audio;
        var fields = new Dictionary<string, string> { ["task"] = "transcribe" };
        if (language is not null) fields["language"] = language;
        if (prompt is not null) fields["initial_prompt"] = prompt;
        _suffix = Encoding.UTF8.GetBytes("\"," + JsonSerializer.Serialize(fields)[1..]);
        Headers.ContentType = new("application/json") { CharSet = "utf-8" };
    }

    protected override bool TryComputeLength(out long length)
    {
        length = Prefix.Length + ((_audio.LongLength + 2) / 3 * 4) + _suffix.Length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        const int inputChunkSize = 12_288; // A multiple of three avoids padding between chunks.
        var buffer = ArrayPool<byte>.Shared.Rent(16_384);
        try
        {
            await stream.WriteAsync(Prefix, cancellationToken);
            for (var offset = 0; offset < _audio.Length; offset += inputChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(inputChunkSize, _audio.Length - offset);
                Base64.EncodeToUtf8(_audio.AsSpan(offset, count), buffer, out _, out var written);
                await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken);
            }
            await stream.WriteAsync(_suffix, cancellationToken);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
