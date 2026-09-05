using System.Text.Json;
using TypeWhisper.PluginHost;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class VocabularyDiagnosticLogTests
{
    [Fact]
    public void JournalEscapesLinesBoundsMessagesAndRotates()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ctc-log-" + Guid.NewGuid());
        try
        {
            var path = Path.Combine(folder, "trace.jsonl");
            var log = new VocabularyDiagnosticLog(path);
            log.Write("one\ntwo");
            Assert.Single(File.ReadAllLines(path));
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
                Assert.Equal("one\ntwo", json.RootElement.GetProperty("message").GetString());
            log.Write(new string('x', 5000));
            using (var json = JsonDocument.Parse(File.ReadAllLines(path)[1]))
                Assert.Equal(2048, json.RootElement.GetProperty("message").GetString()!.Length);
            for (var i = 0; i < 100; i++) log.Write(new string('x', 2048));
            Assert.True(File.Exists(path + ".previous"));
            Assert.True(new FileInfo(path).Length < 132 * 1024);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void UnwritableJournalDoesNotFailDictation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ctc-log-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try { new VocabularyDiagnosticLog(folder).Write("test"); }
        finally { Directory.Delete(folder); }
    }
}
