using System.Text.Json;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-diagnostics-" + Guid.NewGuid());
    private DateTimeOffset _now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private string LogPath => Path.Combine(_directory, "diagnostics.jsonl");
    private DiagnosticLogFile Log(DiagnosticLogPreferences? preferences = null) => new(LogPath, preferences ?? new(), () => _now);
    private DiagnosticLogLine Line(string name) => new(_now, name);
    private string[] Lines() => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];

    [Fact]
    public void WritesOneCompactJsonLinePerEvent()
    {
        var dictation = Guid.NewGuid();
        Log().Write(new(_now, "delivery.completed", dictation, 1234, new Dictionary<string, string> { ["target"] = "True" }));

        var json = Assert.Single(Lines());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("delivery.completed", root.GetProperty("event").GetString());
        Assert.Equal(dictation, root.GetProperty("dictationId").GetGuid());
        Assert.Equal(1234, root.GetProperty("elapsedMs").GetInt64());
        Assert.Equal("True", root.GetProperty("data").GetProperty("target").GetString());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public void ReadsBackLinesWithErrorsWhenPruning()
    {
        var log = Log();
        log.Write(new(_now, "dictation.failed", Error: "System.InvalidOperationException", HResult: "0x80131509"));
        _now = _now.AddHours(2);
        log.Configure(new());

        using var document = JsonDocument.Parse(Assert.Single(Lines()));
        Assert.Equal("0x80131509", document.RootElement.GetProperty("hresult").GetString());
    }

    [Fact]
    public void AdmitsOnlyContentFreeValues()
    {
        var line = DiagnosticLogFile.Admit(new(_now, "Please paste my secret sentence", Guid.Empty, -5,
            new Dictionary<string, string> { ["engine"] = "parakeet-tdt-0.6b", ["title"] = "Inbox - Outlook", ["bad key"] = "x" },
            "System.IO.IOException: C:\\Users\\someone\\notes.txt", "E_FAIL",
            ["TypeWhisper.WinUI.App.<LaunchAsync>b__0_1", "at C:\\Users\\someone\\file.cs line 3"]));

        Assert.Equal("diagnostics.invalid-event", line.Event);
        Assert.Null(line.DictationId);
        Assert.Null(line.ElapsedMs);
        Assert.Equal(new Dictionary<string, string> { ["engine"] = "parakeet-tdt-0.6b" }, line.Data);
        Assert.Null(line.Error);
        Assert.Null(line.HResult);
        Assert.Equal(["TypeWhisper.WinUI.App.<LaunchAsync>b__0_1"], line.Stack);
    }

    [Fact]
    public void KeepsExceptionTypesAndHResults()
    {
        var line = DiagnosticLogFile.Admit(new(_now, "field.capture.exception",
            Error: "System.Runtime.InteropServices.COMException", HResult: "0x80004005"));

        Assert.Equal("System.Runtime.InteropServices.COMException", line.Error);
        Assert.Equal("0x80004005", line.HResult);
    }

    [Fact]
    public void WritesNothingWhileOffAndDeletesTheLogWhenTurnedOff()
    {
        var log = Log();
        log.Write(Line("app.start"));
        Assert.Single(Lines());

        log.Configure(new(Enabled: false));
        Assert.False(File.Exists(LogPath));
        log.Write(Line("app.start"));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void DeletesLinesOlderThanTheRetentionPeriod()
    {
        var log = Log(new(RetentionDays: 3));
        log.Write(Line("old"));
        _now = _now.AddDays(2);
        log.Write(Line("recent"));
        _now = _now.AddDays(2);
        log.Write(Line("new"));

        Assert.Equal(["recent", "new"], Lines().Select(json => JsonDocument.Parse(json).RootElement.GetProperty("event").GetString()));
    }

    [Fact]
    public void ShorterRetentionPrunesImmediately()
    {
        var log = Log(new(RetentionDays: 30));
        log.Write(Line("old"));
        _now = _now.AddDays(5);
        log.Write(Line("new"));

        log.Configure(new(RetentionDays: 3));

        Assert.Single(Lines());
    }

    [Fact]
    public void TrimsToTheNewestLinesWhenTheFileGrowsTooLarge()
    {
        var log = Log();
        var padding = Enumerable.Range(0, 15).ToDictionary(key => "pad" + key, _ => new string('x', 64));
        for (var index = 0; index < 3_000; index++)
            log.Write(new(_now, "dictation.step", Data: new Dictionary<string, string>(padding) { ["index"] = index.ToString() }));

        Assert.True(new FileInfo(LogPath).Length <= DiagnosticLogFile.MaximumBytes);
        Assert.Contains("\"index\":\"2999\"", Lines()[^1]);
    }

    [Fact]
    public void ExportWritesHeaderAndDropsLinesThatWereNotWrittenByTheLog()
    {
        var log = Log();
        log.Write(Line("dictation.start"));
        File.AppendAllText(LogPath, "{\"time\":\"2026-09-27T12:00:00+00:00\",\"event\":\"dictation.start\",\"data\":{\"text\":\"hello there\"}}\n");
        File.AppendAllText(LogPath, "not json\n");
        log.Write(Line("delivery.completed"));
        var destination = Path.Combine(_directory, "export.jsonl");

        var count = log.Export(destination, new(_now, "diagnostics.export", Data: new Dictionary<string, string> { ["version"] = "1.2.0+abc" }));

        Assert.Equal(2, count);
        var exported = File.ReadAllLines(destination);
        Assert.Equal(3, exported.Length);
        Assert.Contains("\"diagnostics.export\"", exported[0]);
        Assert.DoesNotContain(exported, line => line.Contains("hello"));
    }

    [Fact]
    public void ClearDeletesEveryLine()
    {
        var log = Log();
        log.Write(Line("app.start"));
        log.Clear();
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void PreferencesRoundTripAndRejectUnsupportedValues()
    {
        var path = Path.Combine(_directory, "diagnostics.json");
        var store = new DiagnosticLogPreferencesStore(path);
        Assert.Equal(new DiagnosticLogPreferences(), store.Current);
        Assert.Null(store.Error);

        Assert.NotNull(store.Save(new(RetentionDays: 5)));
        Assert.Null(store.Save(new(Enabled: false, RetentionDays: 14)));
        Assert.Equal(new DiagnosticLogPreferences(false, 14), new DiagnosticLogPreferencesStore(path).Current);

        File.WriteAllText(path, "{\"Enabled\":true,\"RetentionDays\":999}");
        var invalid = new DiagnosticLogPreferencesStore(path);
        Assert.Equal(new DiagnosticLogPreferences(), invalid.Current);
        Assert.NotNull(invalid.Error);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (DirectoryNotFoundException) { }
    }
}
