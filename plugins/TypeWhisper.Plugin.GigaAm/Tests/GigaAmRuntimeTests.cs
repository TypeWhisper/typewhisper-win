using System.Security.Cryptography;
using TypeWhisper.Plugin.GigaAm;

namespace TypeWhisper.Plugin.GigaAm.Tests;

public sealed class GigaAmRuntimeTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void PreprocessorShipsWithThePlugin()
    {
        // onnx-asr 0.12.0, onnx_asr/preprocessors/data/gigaam_v3.onnx, as named in THIRD-PARTY-NOTICES.md.
        Assert.Equal("919C3BC8A434B6E733E5FE5DCD247974D0A7B1F2BC5B6995B6AD55F7225E014D",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(GigaAmPlugin.PreprocessorPath))));
    }

    [Fact]
    public void VocabularyJoinsWordPiecesAndPunctuation()
    {
        var vocabulary = GigaAmVocabulary.Parse("<unk> 0\n. 1\n, 2\n▁ 3\n▁При 4\nвет 5\n▁мир 6\n▁— 7\n▁« 8\n» 9\n<blk> 10\n");

        Assert.Equal(10, vocabulary.BlankId);
        Assert.Equal("Привет, мир.", vocabulary.Text([4, 5, 2, 6, 1]));
        Assert.Equal("Привет — мир", vocabulary.Text([4, 5, 7, 6]));
        Assert.Equal("Привет «мир».", vocabulary.Text([4, 5, 8, 6, 9, 1]));
        Assert.Equal("мир.", vocabulary.Text([3, 6, 3, 1]));
        Assert.Equal("", vocabulary.Text([]));
    }

    [Fact]
    public void VocabularyAcceptsWindowsLineEndings()
    {
        var vocabulary = GigaAmVocabulary.Parse("▁да 0\r\n<blk> 1\r\n");

        Assert.Equal(1, vocabulary.BlankId);
        Assert.Equal("да", vocabulary.Text([0]));
    }

    [Theory]
    [InlineData("a 0\nb 1")]
    [InlineData("broken")]
    [InlineData("a 0\n<blk> 0")]
    [InlineData("a x\n<blk> 1")]
    public void VocabularyRejectsInvalidFiles(string text) =>
        Assert.Throws<InvalidDataException>(() => GigaAmVocabulary.Parse(text));

    [Fact]
    public void ShortAudioIsPaddedToOneSecond()
    {
        Assert.Equal(SampleRate, GigaAmRuntime.PadToMinimumDuration([0.5f]).Length);
        var longer = new float[20000];
        Assert.Same(longer, GigaAmRuntime.PadToMinimumDuration(longer));
    }

    [Fact]
    public void ArgmaxPicksTheFirstMaximum() => Assert.Equal(1, GigaAmRuntime.Argmax([0.1f, 0.9f, 0.3f, 0.9f]));

    [Fact]
    public void AudioUpToTwentySecondsStaysOneChunk()
    {
        var samples = new float[20 * SampleRate];

        Assert.Equal([(0, samples.Length)], GigaAmChunking.ChunkRanges(samples, GigaAmChunking.MaximumChunkSeconds * SampleRate));
    }

    [Fact]
    public void LongAudioIsCutAtTheQuietestStretchNearAnEvenSplit()
    {
        // 50 s of tone in three chunks; the even splits fall near 16.7 s and 33.3 s.
        var samples = Tone(50 * SampleRate);
        Silence(samples, 16.4, 16.8);
        Silence(samples, 33.0, 33.5);

        var chunks = GigaAmChunking.ChunkRanges(samples, GigaAmChunking.MaximumChunkSeconds * SampleRate);

        Assert.Equal(3, chunks.Count);
        Assert.InRange(chunks[1].Offset / (double)SampleRate, 16.4, 16.8);
        Assert.InRange(chunks[2].Offset / (double)SampleRate, 33.0, 33.5);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Count, 1, GigaAmChunking.MaximumChunkSeconds * SampleRate));
        for (var index = 1; index < chunks.Count; index++)
            Assert.Equal(chunks[index - 1].Offset + chunks[index - 1].Count, chunks[index].Offset);
        Assert.Equal(samples.Length, chunks[^1].Offset + chunks[^1].Count);
    }

    [Fact]
    public void ChunkAfterAMidSentenceCutStartsLowercase()
    {
        Assert.Equal("Спустя десять лет", GigaAmChunking.Continuing("", "Спустя десять лет"));
        Assert.Equal("эта истина верна.", GigaAmChunking.Continuing("Спустя десять лет", "Эта истина верна."));
        Assert.Equal("Новое предложение.", GigaAmChunking.Continuing("Эта истина верна.", "Новое предложение."));
        Assert.Equal("Правда?", GigaAmChunking.Continuing("Это так?", "Правда?"));
        Assert.Equal("США подписали", GigaAmChunking.Continuing("Спустя десять лет", "США подписали"));
    }

    [Fact]
    public void ChunkTranscriptsAreJoinedWithOneSpace()
    {
        var transcript = GigaAmChunking.Append("", "Первая часть.");
        transcript = GigaAmChunking.Append(transcript, " ");
        transcript = GigaAmChunking.Append(transcript, " Вторая часть. ");

        Assert.Equal("Первая часть. Вторая часть.", transcript);
    }

    private static float[] Tone(int length)
    {
        var samples = new float[length];
        for (var index = 0; index < length; index++) samples[index] = 0.5f * MathF.Sin(index * 0.05f);
        return samples;
    }

    private static void Silence(float[] samples, double fromSeconds, double toSeconds) =>
        Array.Clear(samples, (int)(fromSeconds * SampleRate), (int)((toSeconds - fromSeconds) * SampleRate));
}
