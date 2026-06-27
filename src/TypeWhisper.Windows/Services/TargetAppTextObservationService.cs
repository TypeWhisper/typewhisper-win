using System.Runtime.InteropServices;
using System.Windows.Automation;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Captures plain text values from focused target-app fields via UI Automation.
/// </summary>
public sealed class TargetAppTextObservationService : ITargetAppTextObserver
{
    /// <summary>
    /// Captures the currently focused text element in the target window.
    /// </summary>
    public TargetAppTextObservation? Capture(IntPtr targetHwnd, int maxTextLength) =>
        CaptureCore(targetHwnd, maxTextLength, preferredText: null);

    /// <summary>
    /// Captures a text element in the target window, preferring one that contains the supplied text.
    /// </summary>
    public TargetAppTextObservation? Capture(IntPtr targetHwnd, int maxTextLength, string preferredText) =>
        CaptureCore(targetHwnd, maxTextLength, string.IsNullOrEmpty(preferredText) ? null : preferredText);

    private static TargetAppTextObservation? CaptureCore(IntPtr targetHwnd, int maxTextLength, string? preferredText)
    {
        if (targetHwnd == IntPtr.Zero)
            return null;

        var focused = CaptureFocusedElement(targetHwnd, maxTextLength);
        if (preferredText is null ||
            focused?.Value.Contains(preferredText, StringComparison.Ordinal) == true)
        {
            return focused;
        }

        return CaptureUniqueDescendantContaining(targetHwnd, maxTextLength, preferredText);
    }

    /// <summary>
    /// Recaptures the same text element if it is still observable.
    /// </summary>
    public TargetAppTextObservation? Recapture(TargetAppTextObservation baseline)
    {
        try
        {
            if (baseline.Element is { } baselineElement)
            {
                var baselineElementKey = GetElementKey(baselineElement);
                if (!string.Equals(baselineElementKey, baseline.ElementKey, StringComparison.Ordinal))
                    return null;

                return CaptureElement(baselineElement, baseline.WindowHandle, baseline.MaxValueLength);
            }

            var element = AutomationElement.FocusedElement;
            var currentKey = GetElementKey(element);
            if (!string.Equals(currentKey, baseline.ElementKey, StringComparison.Ordinal))
                return null;

            return CaptureElement(element, baseline.WindowHandle, baseline.MaxValueLength);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns whether the focused element still matches the baseline.
    /// </summary>
    public TargetAppTextElementMatch GetFocusedElementMatch(TargetAppTextObservation baseline)
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != baseline.WindowHandle)
                return TargetAppTextElementMatch.Different;

            var element = AutomationElement.FocusedElement;
            var currentKey = GetElementKey(element);
            if (string.IsNullOrWhiteSpace(currentKey))
                return TargetAppTextElementMatch.Unavailable;

            return string.Equals(currentKey, baseline.ElementKey, StringComparison.Ordinal)
                ? TargetAppTextElementMatch.Same
                : TargetAppTextElementMatch.Different;
        }
        catch (ElementNotAvailableException)
        {
            return TargetAppTextElementMatch.Unavailable;
        }
        catch (InvalidOperationException)
        {
            return TargetAppTextElementMatch.Unavailable;
        }
        catch (COMException)
        {
            return TargetAppTextElementMatch.Unavailable;
        }
    }

    private static TargetAppTextObservation? CaptureElement(
        AutomationElement element,
        IntPtr windowHandle,
        int maxTextLength)
    {
        if (element is null)
            return null;

        maxTextLength = Math.Clamp(
            maxTextLength,
            TargetAppCorrectionLearningService.MinObservedTextLength,
            TargetAppCorrectionLearningService.MaxObservedTextLength);

        var elementKey = GetElementKey(element);
        if (string.IsNullOrWhiteSpace(elementKey))
            return null;

        var value = ReadPlainText(element, maxTextLength);
        return value is null
            ? null
            : new TargetAppTextObservation(elementKey, value, windowHandle, maxTextLength, element);
    }

    private static TargetAppTextObservation? CaptureFocusedElement(IntPtr targetHwnd, int maxTextLength)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            return IsElementInWindow(element, targetHwnd)
                ? CaptureElement(element, targetHwnd, maxTextLength)
                : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static TargetAppTextObservation? CaptureUniqueDescendantContaining(
        IntPtr targetHwnd,
        int maxTextLength,
        string preferredText)
    {
        try
        {
            var root = AutomationElement.FromHandle(targetHwnd);
            if (root is null)
                return null;

            var textCondition = new OrCondition(
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true),
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));
            var elements = root.FindAll(TreeScope.Element | TreeScope.Descendants, textCondition);
            TargetAppTextObservation? match = null;

            foreach (AutomationElement element in elements)
            {
                var observation = CaptureElement(element, targetHwnd, maxTextLength);
                if (observation is null ||
                    !observation.Value.Contains(preferredText, StringComparison.Ordinal))
                {
                    continue;
                }

                if (match is not null &&
                    !string.Equals(match.ElementKey, observation.ElementKey, StringComparison.Ordinal))
                {
                    return null;
                }

                match = observation;
            }

            return match;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? ReadPlainText(AutomationElement element, int maxTextLength)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern) &&
            textPattern is TextPattern text)
        {
            var textValue = text.DocumentRange.GetText(maxTextLength + 1).TrimEnd('\r', '\n');
            return textValue.Length > maxTextLength ? null : textValue;
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) &&
            valuePattern is ValuePattern value)
        {
            var valueText = value.Current.Value ?? string.Empty;
            return valueText.Length > maxTextLength ? null : valueText;
        }

        return null;
    }

    private static string? GetElementKey(AutomationElement element)
    {
        try
        {
            var runtimeId = element.GetRuntimeId();
            if (runtimeId is { Length: > 0 })
                return string.Join(".", runtimeId);
        }
        catch (ElementNotAvailableException)
        {
            // Fall back to current properties below.
        }
        catch (InvalidOperationException)
        {
            // Fall back to current properties below.
        }
        catch (COMException)
        {
            // Fall back to current properties below.
        }

        try
        {
            var current = element.Current;
            return string.Join(
                "|",
                current.ProcessId,
                current.ControlType.Id,
                current.AutomationId,
                current.Name,
                current.NativeWindowHandle);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool IsElementInWindow(AutomationElement element, IntPtr windowHandle)
    {
        if (element is null)
            return false;

        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var current = element;
            while (current is not null && current != AutomationElement.RootElement)
            {
                if ((IntPtr)current.Current.NativeWindowHandle == windowHandle)
                    return true;

                current = walker.GetParent(current);
            }
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }

        return false;
    }
}
