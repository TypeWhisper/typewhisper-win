using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LiveTextPlacementTests
{
    [Theory]
    [InlineData(-2500, -900, -1920, 0)]
    [InlineData(-10, 1000, -420, 820)]
    [InlineData(-900, 300, -900, 300)]
    public void FloatingWindow_StaysInsideMonitorWorkArea(int x, int y, int expectedX, int expectedY)
    {
        Assert.Equal(new(expectedX, expectedY), LiveTextPlacement.Clamp(new(x, y), 420, 220, -1920, 0, 1920, 1040));
    }

    [Fact]
    public void RemovedMonitorPosition_IsRecoveredToNearestWorkArea()
    {
        Assert.Equal(new(0, 0), LiveTextPlacement.Clamp(new(-2000, -500), 420, 220, 0, 0, 1920, 1040));
        Assert.Equal(new(0, 0), LiveTextPlacement.Clamp(new(100, 100), 420, 220, 0, 0, 200, 100));
    }

    [Fact]
    public void FloatingPreferenceAndDraggedPosition_PersistSeparately()
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-text-test-" + Guid.NewGuid());
        try
        {
            var preferences = new OverlayPreferences(OverlayMode.Compact, true, false, FloatingLiveText: true, LiveTranscriptionFontSize: 18);
            OverlayPreferencesStore.Save(Path.Combine(directory, "overlay.json"), preferences);
            Assert.Equal(preferences, OverlayPreferencesStore.Read(Path.Combine(directory, "overlay.json")));
            var path = Path.Combine(directory, "position.json");
            Assert.Null(LiveTextPlacement.Read(path));
            LiveTextPlacement.Save(path, new(-1234, 567));
            Assert.Equal(new(-1234, 567), LiveTextPlacement.Read(path));
            File.WriteAllText(path, "broken json");
            Assert.Null(LiveTextPlacement.Read(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
