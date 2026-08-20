namespace TypeWhisper.UiAutomation;

internal static class Program
{
    private static int Main(string[] args)
    {
        DpiAwareness.EnablePerMonitorV2();

        try
        {
            var commandLine = CommandLine.Parse(args);
            return commandLine.Command switch
            {
                "capture" => Capture(commandLine),
                "smoke" => Smoke(commandLine),
                "run" => RunFlow(commandLine),
                "tree" => DumpTree(commandLine),
                "help" or "--help" or "-h" => ShowHelp(),
                _ => throw new ArgumentException($"Unknown command '{commandLine.Command}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UI automation failed: {ex.Message}");
            return 1;
        }
    }

    private static int Capture(CommandLine commandLine)
    {
        var appPath = commandLine.RequirePath("--app", mustExist: true);
        var outputDirectory = commandLine.RequirePath("--output", mustExist: false);
        var language = commandLine.Get("--language") ?? "en";
        var scope = (commandLine.Get("--scope") ?? "all").ToLowerInvariant();
        var registryFile = OptionalExistingPath(commandLine.Get("--plugin-registry"));
        var timeout = commandLine.GetTimeout();
        Directory.CreateDirectory(outputDirectory);

        if (scope is "all" or "app")
        {
            using (var session = TypeWhisperAutomationSession.Launch(
                appPath, language, GetDisplayVersion(commandLine), premium: false, registryFile, timeout))
            {
                ScreenshotCatalog.CaptureApp(session, outputDirectory);
            }

            using var premiumSession = TypeWhisperAutomationSession.Launch(
                appPath, language, GetDisplayVersion(commandLine), premium: true, registryFile, timeout);
            ScreenshotCatalog.CapturePremium(premiumSession, outputDirectory);
        }

        if (scope is "all" or "plugins")
        {
            using var session = TypeWhisperAutomationSession.Launch(
                appPath, language, GetDisplayVersion(commandLine), premium: false, registryFile, timeout);
            var count = ScreenshotCatalog.CapturePlugins(session, outputDirectory);
            Console.WriteLine($"Captured {count} plugin settings window(s).");
        }

        if (scope is not ("all" or "app" or "plugins"))
            throw new ArgumentException("--scope must be app, plugins, or all.");

        Console.WriteLine($"Screenshots written to {outputDirectory}");
        return 0;
    }

    private static int Smoke(CommandLine commandLine)
    {
        using var session = Launch(commandLine, premium: commandLine.HasFlag("--premium"));
        ScreenshotCatalog.Smoke(session);
        Console.WriteLine("All settings routes passed the UI automation smoke test.");
        return 0;
    }

    private static int RunFlow(CommandLine commandLine)
    {
        var flowPath = commandLine.RequirePath("--flow", mustExist: true);
        var outputDirectory = commandLine.RequirePath("--output", mustExist: false);
        Directory.CreateDirectory(outputDirectory);
        using var session = Launch(commandLine, premium: commandLine.HasFlag("--premium"));
        ScreenshotCatalog.RunFlow(session, flowPath, outputDirectory);
        Console.WriteLine($"Automation flow completed: {flowPath}");
        return 0;
    }

    private static int DumpTree(CommandLine commandLine)
    {
        using var session = Launch(commandLine, premium: commandLine.HasFlag("--premium"));
        var route = commandLine.Get("--route");
        if (!string.IsNullOrWhiteSpace(route))
            session.InvokeAndWait(route, $"SettingsSection.{route}");
        session.DumpTree(Console.Out);
        return 0;
    }

    private static TypeWhisperAutomationSession Launch(CommandLine commandLine, bool premium)
    {
        var appPath = commandLine.RequirePath("--app", mustExist: true);
        var registryFile = OptionalExistingPath(commandLine.Get("--plugin-registry"));
        return TypeWhisperAutomationSession.Launch(
            appPath,
            commandLine.Get("--language") ?? "en",
            GetDisplayVersion(commandLine),
            premium,
            registryFile,
            commandLine.GetTimeout());
    }

    private static string GetDisplayVersion(CommandLine commandLine) =>
        commandLine.Get("--display-version") ?? "1.0.0";

    private static string? OptionalExistingPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var path = Path.GetFullPath(value);
        if (!File.Exists(path))
            throw new FileNotFoundException("Plugin registry fixture does not exist.", path);
        return path;
    }

    private static int ShowHelp()
    {
        Console.WriteLine(
            """
            TypeWhisper Windows UI automation

              capture --app <TypeWhisper.exe> --output <dir> [--language en] [--scope app|plugins|all]
              smoke   --app <TypeWhisper.exe> [--language en]
              run     --app <TypeWhisper.exe> --flow <flow.json> --output <dir> [--language en]
              tree    --app <TypeWhisper.exe> [--language en] [--route Integrations]

            Common options: --plugin-registry <plugins.json> --display-version <version> --premium --timeout <seconds>
            """);
        return 0;
    }
}
