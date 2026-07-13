using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TargetAppTextObservationServiceTests
{
    [Fact]
    public void ResolveObservableWindow_UsesForegroundWindowFromSameProcess()
    {
        var targetHwnd = new IntPtr(200);
        var foregroundHwnd = new IntPtr(300);
        var processIds = new Dictionary<IntPtr, uint>
        {
            [targetHwnd] = 42,
            [foregroundHwnd] = 42
        };

        var result = TargetAppTextObservationService.ResolveObservableWindow(
            targetHwnd,
            foregroundHwnd,
            hwnd => processIds.GetValueOrDefault(hwnd));

        Assert.Equal(foregroundHwnd, result);
    }

    [Fact]
    public void ResolveObservableWindow_KeepsTargetWindowForDifferentForegroundProcess()
    {
        var targetHwnd = new IntPtr(200);
        var foregroundHwnd = new IntPtr(300);
        var processIds = new Dictionary<IntPtr, uint>
        {
            [targetHwnd] = 42,
            [foregroundHwnd] = 99
        };

        var result = TargetAppTextObservationService.ResolveObservableWindow(
            targetHwnd,
            foregroundHwnd,
            hwnd => processIds.GetValueOrDefault(hwnd));

        Assert.Equal(targetHwnd, result);
    }

    [Fact]
    public void CaptureCore_DoesNotScanDescendantsWhenPreferredTextIsMissingFromFocus()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Services",
            "TargetAppTextObservationService.cs");
        var captureBlock = TestFile.ExtractBlock(
            source,
            "private static TargetAppTextObservation? CaptureCore",
            700);

        Assert.DoesNotContain("CaptureUniqueDescendantContaining", captureBlock);
    }

    [Fact]
    public void CaptureDeep_UsesCursorPointBeforeOptionalDescendantScan()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Services",
            "TargetAppTextObservationService.cs");
        var deepCaptureBlock = TestFile.ExtractBlock(
            source,
            "public TargetAppTextObservation? CaptureDeep",
            1800);
        var descendantBlock = TestFile.ExtractBlock(
            source,
            "private static TargetAppTextObservation? CaptureUniqueDescendantContaining",
            2400);

        Assert.Contains("CaptureElementAtCursor", deepCaptureBlock);
        Assert.Contains("if (!allowDescendantScan)", deepCaptureBlock);
        Assert.Contains("CaptureUniqueDescendantContaining", deepCaptureBlock);
        Assert.Contains("FindAll(TreeScope.Descendants", descendantBlock);
        Assert.Contains("TextPattern.Pattern", descendantBlock);
        Assert.Contains("ValuePattern.Pattern", descendantBlock);
    }

    [Fact]
    public void Recapture_AllowsFocusedSameWindowFallbackBeforeCursorWithoutDescendantScan()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Services",
            "TargetAppTextObservationService.cs");
        var recaptureBlock = TestFile.ExtractBlock(
            source,
            "public TargetAppTextObservation? Recapture",
            2400);

        var focusedIndex = recaptureBlock.IndexOf("CaptureFocusedElement", StringComparison.Ordinal);
        var cursorIndex = recaptureBlock.IndexOf("CaptureElementAtCursor", StringComparison.Ordinal);
        Assert.True(focusedIndex >= 0 && focusedIndex < cursorIndex);
        Assert.Contains("ResolveObservableWindow(baseline.WindowHandle)", recaptureBlock);
        Assert.DoesNotContain("CaptureUniqueDescendantContaining", recaptureBlock);
    }
}
