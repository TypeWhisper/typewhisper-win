using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace TypeWhisper.UiAutomation;

internal sealed class TypeWhisperAutomationSession : IDisposable
{
    private readonly string _temporaryDirectory;
    private readonly string _eventFile;
    private readonly TimeSpan _timeout;
    private readonly Process _process;
    private readonly Application _application;
    private readonly UIA3Automation _automation;
    private bool _disposed;

    private TypeWhisperAutomationSession(
        string temporaryDirectory,
        TimeSpan timeout,
        Process process,
        Application application,
        UIA3Automation automation,
        Window settingsWindow,
        string eventFile)
    {
        _temporaryDirectory = temporaryDirectory;
        _timeout = timeout;
        _process = process;
        _application = application;
        _automation = automation;
        _eventFile = eventFile;
        SettingsWindow = settingsWindow;
    }

    public Window SettingsWindow { get; }

    public static TypeWhisperAutomationSession Launch(
        string appPath,
        string language,
        string displayVersion,
        bool premium,
        string? registryFile,
        TimeSpan timeout)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var temporaryDirectory = Path.Join(Path.GetTempPath(), $"typewhisper-ui-{instanceId}");
        var dataRoot = Path.Join(temporaryDirectory, "user-data");
        var readyFile = Path.Join(temporaryDirectory, "ready.json");
        Directory.CreateDirectory(dataRoot);

        var startInfo = new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory
        };
        AddArgument(startInfo, "--ui-automation");
        AddArgument(startInfo, "--automation-data-root", dataRoot);
        AddArgument(startInfo, "--automation-ready-file", readyFile);
        AddArgument(startInfo, "--automation-language", language);
        AddArgument(startInfo, "--automation-display-version", displayVersion);
        AddArgument(startInfo, "--automation-instance", instanceId);
        if (premium)
            AddArgument(startInfo, "--automation-premium");
        if (!string.IsNullOrWhiteSpace(registryFile))
            AddArgument(startInfo, "--automation-plugin-registry", registryFile);

        Process? process = null;
        Application? application = null;
        UIA3Automation? automation = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not launch '{appPath}'.");
            application = Application.Attach(process);
            automation = new UIA3Automation();

            WaitUntil(
                () => File.Exists(readyFile) || process.HasExited,
                timeout,
                "the TypeWhisper UI automation ready signal");
            if (process.HasExited)
                throw new InvalidOperationException($"TypeWhisper exited before it became ready with code {process.ExitCode}.");

            Window? settingsWindow = null;
            WaitUntil(
                () =>
                {
                    settingsWindow = application.GetAllTopLevelWindows(automation)
                        .FirstOrDefault(window => string.Equals(
                            window.Properties.AutomationId.ValueOrDefault,
                            "SettingsWindow",
                            StringComparison.Ordinal));
                    return settingsWindow is not null;
                },
                timeout,
                "the SettingsWindow automation element");

            return new TypeWhisperAutomationSession(
                temporaryDirectory,
                timeout,
                process,
                application,
                automation,
                settingsWindow!,
                $"{readyFile}.events");
        }
        catch
        {
            automation?.Dispose();
            application?.Dispose();
            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The attached application can dispose its Process wrapper during failed startup.
            }
            process?.Dispose();
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    public AutomationElement WaitForElement(string automationId, AutomationElement? root = null)
    {
        var searchRoot = root ?? SettingsWindow;
        if (string.Equals(
            searchRoot.Properties.AutomationId.ValueOrDefault,
            automationId,
            StringComparison.Ordinal))
        {
            return searchRoot;
        }

        AutomationElement? element = null;
        WaitUntil(
            () =>
            {
                element = searchRoot.FindFirstDescendant(
                    _automation.ConditionFactory.ByAutomationId(automationId));
                return element is not null;
            },
            _timeout,
            $"automation element '{automationId}'");
        return element!;
    }

    public void Invoke(string automationId)
    {
        var element = WaitForElement(automationId);
        var invokePattern = element.Patterns.Invoke.PatternOrDefault;
        if (invokePattern is null)
            throw new InvalidOperationException($"Automation element '{automationId}' does not support InvokePattern.");
        invokePattern.Invoke();
    }

    public void Click(string automationId)
    {
        var element = WaitForElement(automationId);
        Mouse.MoveTo(new Point(1, 1));
        element.Focus();
        WaitForUiToSettle();
        element.Click();
    }

    public void ScrollAndInvoke(string automationId, string scrollContainerAutomationId)
    {
        var element = WaitForElement(automationId);
        var scrollContainer = WaitForElement(scrollContainerAutomationId);
        var scrollPattern = scrollContainer.Patterns.Scroll.PatternOrDefault
            ?? throw new InvalidOperationException(
                $"Automation element '{scrollContainerAutomationId}' does not support ScrollPattern.");

        for (var attempt = 0; attempt < 30 && element.Properties.IsOffscreen.ValueOrDefault; attempt++)
        {
            scrollPattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
            WaitForUiToSettle();
        }

        if (element.Properties.IsOffscreen.ValueOrDefault)
        {
            throw new InvalidOperationException(
                $"Automation element '{automationId}' remained offscreen after scrolling '{scrollContainerAutomationId}'.");
        }

        WaitForUiToSettle();
        var invokePattern = element.Patterns.Invoke.PatternOrDefault
            ?? throw new InvalidOperationException(
                $"Automation element '{automationId}' does not support InvokePattern.");
        invokePattern.Invoke();
    }

    public void InvokeAndWait(string automationId, string expectedAutomationId)
    {
        Invoke(automationId);
        WaitForElement(expectedAutomationId);
        WaitForUiToSettle();
    }

    public Window WaitForWindow(string automationId)
    {
        Window? window = null;
        try
        {
            WaitUntil(
                () =>
                {
                    window = FindProcessWindow(automationId);
                    return window is not null;
                },
                _timeout,
                $"window '{automationId}'");
        }
        catch (TimeoutException ex)
        {
            var available = _automation.GetDesktop().FindAllDescendants()
                .Where(candidate => candidate.Properties.ProcessId.ValueOrDefault == _process.Id)
                .Select(candidate =>
                    $"id='{candidate.Properties.AutomationId.ValueOrDefault}', name='{candidate.Properties.Name.ValueOrDefault}'")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            throw new TimeoutException(
                $"{ex.Message} Available top-level windows: {string.Join("; ", available)} " +
                $"Automation events: {ReadAutomationEvents()}",
                ex);
        }
        return window!;
    }

    private Window? FindProcessWindow(string automationId)
    {
        var element = _automation.GetDesktop().FindFirstDescendant(
            _automation.ConditionFactory.ByAutomationId(automationId)
                .And(_automation.ConditionFactory.ByProcessId(_process.Id)));
        return element?.AsWindow();
    }

    public IReadOnlyList<string> FindAutomationIds(string prefix)
    {
        return SettingsWindow.FindAllDescendants()
            .Select(element => element.Properties.AutomationId.ValueOrDefault)
            .Where(id => id?.StartsWith(prefix, StringComparison.Ordinal) == true)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public void RequireFullyVisibleScrollContent(string automationId, AutomationElement root)
    {
        var scroll = WaitForElement(automationId, root).Patterns.Scroll.PatternOrDefault
            ?? throw new InvalidOperationException(
                $"Automation element '{automationId}' does not support ScrollPattern.");
        if (scroll.VerticallyScrollable.ValueOrDefault
            || scroll.HorizontallyScrollable.ValueOrDefault)
        {
            var rootName = root.Properties.Name.ValueOrDefault;
            throw new InvalidOperationException(
                $"The plugin settings content for '{rootName}' does not fit on the active desktop. " +
                $"Refusing to create a partial screenshot from '{automationId}'.");
        }
    }

    public void Capture(AutomationElement element, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        Mouse.MoveTo(new Point(1, 1));
        element.Focus();
        WaitForUiToSettle();
        var bounds = element.BoundingRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The automation element has no visible capture bounds.");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.X,
                bounds.Y,
                0,
                0,
                bounds.Size,
                CopyPixelOperation.SourceCopy);
        }
        bitmap.Save(outputPath, ImageFormat.Png);
    }

    public void DumpTree(TextWriter writer)
    {
        DumpElement(writer, SettingsWindow, 0);
    }

    public void WaitForUiToSettle() => Thread.Sleep(300);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (!_application.HasExited)
            {
                SettingsWindow.Close();
                if (!_process.WaitForExit(2000))
                    _application.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            if (!_application.HasExited)
                _application.Kill();
        }
        finally
        {
            _automation.Dispose();
            _application.Dispose();
            _process.Dispose();
            TryDeleteDirectory(_temporaryDirectory);
        }
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string? value = null)
    {
        startInfo.ArgumentList.Add(name);
        if (value is not null)
            startInfo.ArgumentList.Add(value);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
                return;
            Thread.Sleep(100);
        }

        throw new TimeoutException($"Timed out waiting for {description} after {timeout.TotalSeconds:0} seconds.");
    }

    private static void DumpElement(TextWriter writer, AutomationElement element, int depth)
    {
        var indent = new string(' ', depth * 2);
        var bounds = element.BoundingRectangle;
        writer.WriteLine(
            $"{indent}{element.ControlType} id='{element.Properties.AutomationId.ValueOrDefault}' " +
            $"name='{element.Properties.Name.ValueOrDefault}' bounds={bounds.X:0},{bounds.Y:0},{bounds.Width:0},{bounds.Height:0}");
        foreach (var child in element.FindAllChildren())
            DumpElement(writer, child, depth + 1);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove temporary UI automation directory '{path}': {ex.Message}");
        }
    }

    private string ReadAutomationEvents()
    {
        try
        {
            return File.Exists(_eventFile)
                ? string.Join(" | ", File.ReadAllLines(_eventFile))
                : "<none>";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"<unavailable: {ex.Message}>";
        }
    }
}
