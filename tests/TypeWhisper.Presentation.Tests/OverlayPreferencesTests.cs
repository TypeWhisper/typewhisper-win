using System.Text.Json;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class OverlayPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-overlay-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "overlay.json");

    [Fact]
    public void OldOverlayJsonReceivesRealDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{\"Mode\":1,\"LiveText\":false,\"TechnicalDetails\":true,\"Anchor\":2,\"Left\":3,\"Right\":0}");
        var loaded = OverlayPreferencesStore.Read(FilePath);
        Assert.Equal(12, loaded.LiveTranscriptionFontSize);
        Assert.Equal(1500, loaded.PreviewBubbleAutoHideMilliseconds);
        Assert.Equal(PrototypeOverlayMode.Compact, loaded.Mode);
        Assert.Equal(PrototypeOverlayAnchor.TopRight, loaded.Anchor);
        Assert.False(loaded.LiveText);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(18, 5000)]
    [InlineData(13.5, 1200)]
    public void BoundaryValuesPersistAcrossRestart(double size, int delay)
    {
        var expected = new PrototypeOverlayPreferences(PrototypeOverlayMode.Standard, true, false,
            LiveTranscriptionFontSize: size, PreviewBubbleAutoHideMilliseconds: delay);
        OverlayPreferencesStore.Save(FilePath, expected);
        Assert.Equal(expected, OverlayPreferencesStore.Read(FilePath));
    }

    [Theory]
    [InlineData(9, 1500)]
    [InlineData(19, 1500)]
    [InlineData(12, -1)]
    [InlineData(12, 5001)]
    [InlineData(double.NaN, 1500)]
    public void InvalidChoicesDoNotOverwriteSavedPreferences(double size, int delay)
    {
        var valid = new PrototypeOverlayPreferences(PrototypeOverlayMode.Standard, true, false);
        OverlayPreferencesStore.Save(FilePath, valid);
        Assert.Throws<ArgumentException>(() => OverlayPreferencesStore.Save(FilePath, valid with
            { LiveTranscriptionFontSize = size, PreviewBubbleAutoHideMilliseconds = delay }));
        Assert.Equal(valid, OverlayPreferencesStore.Read(FilePath));
    }

    [Fact]
    public void InvalidPersistedValuesFailVisibly()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{\"Mode\":0,\"LiveText\":true,\"LiveTranscriptionFontSize\":99}");
        Assert.Throws<JsonException>(() => OverlayPreferencesStore.Read(FilePath));
    }

    [Fact]
    public void FailedWritePreservesExistingPreferencesAndCleansTemporaryFile()
    {
        var valid = new PrototypeOverlayPreferences(PrototypeOverlayMode.Standard, true, false);
        OverlayPreferencesStore.Save(FilePath, valid);
        var blocked = Path.Combine(_directory, "blocked");
        Directory.CreateDirectory(blocked);
        var error = Record.Exception(() => OverlayPreferencesStore.Save(blocked, valid with { LiveTranscriptionFontSize = 18 }));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(valid, OverlayPreferencesStore.Read(FilePath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
