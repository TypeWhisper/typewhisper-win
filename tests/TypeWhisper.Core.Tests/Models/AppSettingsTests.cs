using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class AppSettingsTests
{
    [Fact]
    public void DefaultIndicatorStyle_IsStatusIsland()
    {
        Assert.Equal(IndicatorStyle.StatusIsland, AppSettings.Default.IndicatorStyle);
    }

    [Fact]
    public void DefaultPreviewBubbleAutoHideMilliseconds_IsFifteenHundred()
    {
        Assert.Equal(1500, AppSettings.Default.PreviewBubbleAutoHideMilliseconds);
    }

    [Fact]
    public void DefaultLiveTranscriptionFontSize_IsTwelve()
    {
        Assert.Equal(12d, AppSettings.Default.LiveTranscriptionFontSize);
    }

    [Fact]
    public void DefaultOnlineAsrBatchLiveTranscriptionEnabled_IsFalse()
    {
        Assert.False(AppSettings.Default.OnlineAsrBatchLiveTranscriptionEnabled);
    }

    [Fact]
    public void DefaultTranscriptionNumberNormalizationEnabled_IsTrue()
    {
        Assert.True(AppSettings.Default.TranscriptionNumberNormalizationEnabled);
    }

    [Fact]
    public void DefaultLocalModelAcceleration_IsAuto()
    {
        Assert.Equal(AppSettings.LocalModelAccelerationAuto, AppSettings.Default.LocalModelAcceleration);
    }

    [Fact]
    public void DefaultLocalModelStoragePath_IsNull()
    {
        Assert.Null(AppSettings.Default.LocalModelStoragePath);
    }

    [Fact]
    public void DefaultLastTranslationTargetLanguage_IsNull()
    {
        Assert.Null(AppSettings.Default.LastTranslationTargetLanguage);
    }

    [Fact]
    public void GetMainDictationHotkeys_PrefersConfiguredListOverLegacyStrings()
    {
        var settings = AppSettings.Default with
        {
            PushToTalkHotkey = "Ctrl+Shift",
            ToggleHotkey = "Ctrl+Shift+F9",
            MainDictationHotkeys = ["Ctrl+Alt+D", " Ctrl+Alt+D ", "Ctrl+Shift+D"]
        };

        Assert.Equal(["Ctrl+Alt+D", "Ctrl+Shift+D"], settings.GetMainDictationHotkeys());
    }

    [Fact]
    public void GetShortcutHotkeys_FallsBackToLegacySingleValue()
    {
        var settings = AppSettings.Default with
        {
            MainDictationHotkeys = [],
            PushToTalkHotkey = "",
            ToggleHotkey = "Ctrl+Alt+D",
            WorkflowPaletteHotkeys = [],
            WorkflowPaletteHotkey = "Ctrl+Alt+W"
        };

        Assert.Equal(["Ctrl+Alt+D"], settings.GetMainDictationHotkeys());
        Assert.Equal(["Ctrl+Alt+W"], settings.GetWorkflowPaletteHotkeys());
    }

    [Fact]
    public void GetMainDictationHotkeys_PreservesBothLegacyMainHotkeys()
    {
        var settings = AppSettings.Default with
        {
            MainDictationHotkeys = [],
            PushToTalkHotkey = "Ctrl+Alt+P",
            ToggleHotkey = "Ctrl+Alt+T"
        };

        Assert.Equal(["Ctrl+Alt+P", "Ctrl+Alt+T"], settings.GetMainDictationHotkeys());
    }

    [Fact]
    public void GetMainDictationHotkeys_FallsBackWhenConfiguredListCleansToEmpty()
    {
        var settings = AppSettings.Default with
        {
            MainDictationHotkeys = [" ", ""],
            PushToTalkHotkey = "",
            ToggleHotkey = "Ctrl+Alt+D"
        };

        Assert.Equal(["Ctrl+Alt+D"], settings.GetMainDictationHotkeys());
    }

    [Fact]
    public void NormalizeHotkeyLists_PreservesLegacyMainHotkeyList()
    {
        var settings = AppSettings.Default with
        {
            MainDictationHotkeys = [],
            PushToTalkHotkey = "Ctrl+Alt+P",
            ToggleHotkey = "Ctrl+Alt+T"
        };

        var normalized = settings.NormalizeHotkeyLists();

        Assert.Equal(["Ctrl+Alt+P", "Ctrl+Alt+T"], normalized.MainDictationHotkeys);
        Assert.Equal("Ctrl+Alt+P", normalized.ToggleHotkey);
        Assert.Equal("Ctrl+Alt+P", normalized.PushToTalkHotkey);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1500, 1500)]
    [InlineData(5000, 5000)]
    [InlineData(5001, 5000)]
    public void NormalizePreviewBubbleAutoHideMilliseconds_ClampsToSupportedRange(
        int value,
        int expected)
    {
        Assert.Equal(expected, AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(value));
    }

    [Theory]
    [InlineData(7.0, 10.0)]
    [InlineData(10.0, 10.0)]
    [InlineData(13.5, 13.5)]
    [InlineData(18.0, 18.0)]
    [InlineData(21.0, 18.0)]
    public void NormalizeLiveTranscriptionFontSize_ClampsToSupportedRange(
        double value,
        double expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeLiveTranscriptionFontSize(value));
    }

    [Theory]
    [InlineData(null, AppSettings.LocalModelAccelerationAuto)]
    [InlineData("", AppSettings.LocalModelAccelerationAuto)]
    [InlineData("AUTO", AppSettings.LocalModelAccelerationAuto)]
    [InlineData("cpu", AppSettings.LocalModelAccelerationCpu)]
    [InlineData("NVIDIA CUDA", AppSettings.LocalModelAccelerationNvidiaCuda)]
    [InlineData("cuda", AppSettings.LocalModelAccelerationNvidiaCuda)]
    [InlineData("vulkan", AppSettings.LocalModelAccelerationAmdVulkan)]
    [InlineData("AMD Vulkan", AppSettings.LocalModelAccelerationAmdVulkan)]
    [InlineData("amd_vulkan", AppSettings.LocalModelAccelerationAmdVulkan)]
    [InlineData("rocm", AppSettings.LocalModelAccelerationAmdRocm)]
    [InlineData("HIP", AppSettings.LocalModelAccelerationAmdRocm)]
    [InlineData("AMD ROCm", AppSettings.LocalModelAccelerationAmdRocm)]
    [InlineData("amd_rocm", AppSettings.LocalModelAccelerationAmdRocm)]
    [InlineData("directml", AppSettings.LocalModelAccelerationAuto)]
    public void NormalizeLocalModelAcceleration_ReturnsSupportedStorageValue(
        string? value,
        string expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeLocalModelAcceleration(value));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("D:\\TypeWhisperModels", "D:\\TypeWhisperModels")]
    [InlineData("  D:\\TypeWhisperModels  ", "D:\\TypeWhisperModels")]
    public void NormalizeLocalModelStoragePath_TrimsEmptyToNull(
        string? value,
        string? expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeLocalModelStoragePath(value));
    }
}
