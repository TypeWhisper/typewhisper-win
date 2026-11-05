using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using TypeWhisper.Plugin.ParakeetCtc;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class CtcModelAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctc-assets-test-" + Guid.NewGuid());
    private string Model => Path.Combine(_root, "model");
    private static readonly byte[] Tokenizer = Encoding.UTF8.GetBytes("""
        {"model":{"type":"BPE","vocab":{"a":0},"merges":[]}}
        """);
    private static byte[] Archive(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var data = new MemoryStream(bytes);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
            }
        using var compressed = new MemoryStream();
        using (var compressor = BZip2Stream.Create(compressed, CompressionMode.Compress, false, leaveOpen: true))
        {
            compressor.Write(output.ToArray());
            // With leaveOpen, disposal does not finalize SharpCompress's encoder.
            compressor.Finish();
        }
        return compressed.ToArray();
    }
    private static byte[] ValidArchive() => Archive(("export/model.int8.onnx", new byte[] { 1, 2, 3 }),
        ("export/tokens.txt", Encoding.UTF8.GetBytes("a 0\n<blk> 1024\n")));
    private static CtcAssetSource Source(string path, byte[] bytes, long maximum = 1024 * 1024) =>
        new("https://fixture.invalid/" + path, Convert.ToHexString(SHA256.HashData(bytes)), maximum);
    private static CtcModelAssets Create(HttpClient client, byte[] archive) => new(client, Source("model", archive), Source("tokenizer", Tokenizer));

    [Fact]
    public async Task VerifiedBZip2TarAssetsPublishTogetherAndRestartMakesNoRequest()
    {
        var archive = ValidArchive();
        Assert.Equal("BZh", Encoding.ASCII.GetString(archive, 0, 3));
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            return Task.FromResult(Response(request.RequestUri!.AbsolutePath == "/model" ? archive : Tokenizer));
        }));
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(Model, "model.int8.onnx")));
        Assert.Equal(1024, new CtcTokenizer(Path.Combine(Model, "tokens.txt")).BlankId);
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Empty(Directory.GetDirectories(_root, ".ctc-staging-*"));
    }

    [Fact]
    public async Task HashFailureLeavesPriorDirectoryUntouchedAndCanRetry()
    {
        Directory.CreateDirectory(Model);
        var prior = Path.Combine(Model, "keep-existing.txt");
        File.WriteAllText(prior, "previous assets");
        var archive = ValidArchive();
        var corrupt = true;
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(Response(
            request.RequestUri!.AbsolutePath == "/tokenizer" ? Tokenizer : corrupt ? new byte[] { 9 } : archive))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(client, archive).EnsureAsync(Model, CancellationToken.None));
        Assert.Equal("previous assets", File.ReadAllText(prior));
        Assert.Empty(Directory.GetDirectories(_root, ".ctc-staging-*"));
        corrupt = false;
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(Model, "verified-assets.json")));
        var backup = Assert.Single(Directory.GetDirectories(_root, "model.previous-*"));
        Assert.Equal("previous assets", File.ReadAllText(Path.Combine(backup, "keep-existing.txt")));
    }

    [Fact]
    public async Task CancellationDuringHttpDrainsWithoutPublishingPartialAssets()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Response([]);
        }));
        var run = Create(client, ValidArchive()).EnsureAsync(Model, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(Directory.Exists(Model));
        Assert.Empty(Directory.GetDirectories(_root, ".ctc-staging-*"));
    }

    [Theory]
    [InlineData("../model.int8.onnx")]
    [InlineData("/model.int8.onnx")]
    public async Task TraversalArchiveIsRejectedBeforePublishing(string entry)
    {
        var archive = Archive((entry, new byte[] { 1 }), ("tokens.txt", Encoding.UTF8.GetBytes("a 0\n<blk> 1024\n")));
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(archive))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(client, archive).EnsureAsync(Model, CancellationToken.None));
        Assert.False(Directory.Exists(Model));
        Assert.False(File.Exists(Path.Combine(_root, "model.int8.onnx")));
    }

    [Fact]
    public async Task ResponseSizeLimitRejectsOtherwiseMatchingHash()
    {
        var archive = ValidArchive();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(archive))));
        var installer = new CtcModelAssets(client, Source("model", archive, 1), Source("tokenizer", Tokenizer));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.EnsureAsync(Model, CancellationToken.None));
        Assert.False(Directory.Exists(Model));
    }

    [Fact]
    public async Task ChangedInstalledBytesRequireVerifiedRepair()
    {
        var archive = ValidArchive();
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            return Task.FromResult(Response(request.RequestUri!.AbsolutePath == "/model" ? archive : Tokenizer));
        }));
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(Model, "model.int8.onnx"), [9]);
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(4, requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(Model, "model.int8.onnx")));
    }

    [Fact]
    public async Task UnchangedVerifiedAssetsAreNotHashedAgainUntilTheirStampChanges()
    {
        var archive = ValidArchive();
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            return Task.FromResult(Response(request.RequestUri!.AbsolutePath == "/model" ? archive : Tokenizer));
        }));
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        // Files written moments ago get no stamp: a rewrite within the file-time resolution could keep it.
        Assert.Empty(Stamps());
        AgeInstalledFiles();
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(["model.int8.onnx", "tokenizer.json", "tokens.txt"], Stamps().Keys.Order(StringComparer.Ordinal));
        var receipt = Path.Combine(Model, "verified-assets.json");
        var receiptWritten = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(receipt, receiptWritten);
        var model = Path.Combine(Model, "model.int8.onnx");
        var modelWritten = File.GetLastWriteTimeUtc(model);
        // Different bytes behind the recorded size and last write time: a matching stamp means the file is not read again.
        File.WriteAllBytes(model, [3, 2, 1]);
        File.SetLastWriteTimeUtc(model, modelWritten);
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(new byte[] { 3, 2, 1 }, File.ReadAllBytes(model));
        Assert.Equal(receiptWritten, File.GetLastWriteTimeUtc(receipt)); // An unchanged receipt is not rewritten.
        // A changed stamp is verified by hashing again, and the mismatch is repaired.
        File.SetLastWriteTimeUtc(model, modelWritten.AddSeconds(2));
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(4, requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(model));
    }

    [Fact]
    public async Task ReceiptWithoutStampsIsVerifiedOnceAndUpgraded()
    {
        var archive = ValidArchive();
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            return Task.FromResult(Response(request.RequestUri!.AbsolutePath == "/model" ? archive : Tokenizer));
        }));
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        var receipt = Path.Combine(Model, "verified-assets.json");
        // The receipt format before stamps existed: the pinned source hashes and one hash per file.
        var legacy = JsonNode.Parse(File.ReadAllText(receipt))!.AsObject();
        legacy.Remove("Stamps");
        File.WriteAllText(receipt, legacy.ToJsonString());
        AgeInstalledFiles();
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(3, Stamps().Count);
        var upgraded = JsonNode.Parse(File.ReadAllText(receipt))!;
        foreach (var field in new[] { "ArchiveHash", "TokenizerHash", "Files" })
            Assert.Equal(legacy[field]!.ToJsonString(), upgraded[field]!.ToJsonString());
        Assert.Empty(Directory.GetFiles(_root, ".ctc-receipt-*"));
    }

    // Moves the installed files' last write times back, as they are on every launch after the one that installed them.
    private void AgeInstalledFiles()
    {
        foreach (var path in Directory.GetFiles(Model))
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-10));
    }

    private Dictionary<string, JsonNode?> Stamps() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Model, "verified-assets.json")))!["Stamps"]?.AsObject()
            .ToDictionary(entry => entry.Key, entry => entry.Value) ?? [];

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
