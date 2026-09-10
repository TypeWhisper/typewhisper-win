using System.Text;
using TypeWhisper.WinUI;
using Xunit;

public sealed class MediaFoundationFileDecoderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-media-" + Guid.NewGuid());
    public MediaFoundationFileDecoderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Stereo48KhzToneBecomesFiniteMono16KhzWithoutChangingSource()
    {
        var path = Path.Combine(_root, "stereo.wav");
        WriteTone(path);
        var original = File.ReadAllBytes(path);
        var samples = await MediaFoundationFileDecoder.LoadAsync(path, default);
        // Media Foundation may round the final resampler frame by a few samples.
        Assert.InRange(samples.Length, 3190, 3210);
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample)));
        Assert.InRange(samples.Max(sample => Math.Abs(sample)), 0.1f, 0.6f);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task CanceledTokenPreventsOpeningSource()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaFoundationFileDecoder.LoadAsync(Path.Combine(_root, "missing.wav"), canceled.Token));
    }

    [Fact]
    public async Task MissingSourceReportsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            MediaFoundationFileDecoder.LoadAsync(Path.Combine(_root, "missing.wav"), default));
    }

    [Fact]
    public async Task InvalidMediaReportsDecodeFailureAndPreservesSource()
    {
        var path = Path.Combine(_root, "invalid.wav");
        byte[] original = Encoding.UTF8.GetBytes("This is not an audio file.");
        File.WriteAllBytes(path, original);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MediaFoundationFileDecoder.LoadAsync(path, default));
        Assert.Contains("could not decode", error.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static void WriteTone(string path)
    {
        const int frames = 9600;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)2);
        writer.Write(48000); writer.Write(48000 * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * 4);
        for (var index = 0; index < frames; index++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * index / 48000) * 12000);
            writer.Write(sample); writer.Write(sample);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
