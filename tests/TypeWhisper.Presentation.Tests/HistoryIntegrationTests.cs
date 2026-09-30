using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryIntegrationTests
{
    [Fact]
    public async Task StrictMissingHistoryIsEmptyWithoutCreatingFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "typewhisper-history-test-" + Guid.NewGuid(), "history.json");
        var reader = new HistoryReader(new HistoryService(path) { ThrowOnLoadFailure = true });
        Assert.Empty(await reader.ReadAsync());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task StrictCorruptHistoryFailsWithoutRewritingAndCanRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), "typewhisper-history-test-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "not json");
            var reader = new HistoryReader(new HistoryService(path) { ThrowOnLoadFailure = true });
            await Assert.ThrowsAsync<JsonException>(() => reader.ReadAsync());
            Assert.Equal("not json", await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path, "[]");
            Assert.Empty(await reader.ReadAsync());
        }
        finally { File.Delete(path); }
    }
}
