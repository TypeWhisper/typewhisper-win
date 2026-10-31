using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public class CompletionTests
{
    private const string ModelsJson = """
        {"status":"ready","models":[
          {"id":"large-v3","engine":"whisper"},
          {"id":"turbo","engine":"whisper"},
          {"id":"whisper-large-v3","engine":"groq"},
          {"id":"turbo","engine":"whisper"},
          {"id":"bad\nid","engine":"groq"},
          {"id":"$(touch pwned)","engine":"groq"},
          {"id":"has space","engine":"groq"},
          {"id":"","engine":""}
        ]}
        """;

    // Words after "typewhisper"; the last word is the one being completed.
    private static readonly string[] Globals = ["--port", "--api-token", "--dev", "--profile", "--json", "--help", "--version"];
    private static readonly (string Line, string[] Expected)[] StaticCases =
    [
        ("", ["status", "models", "transcribe", "history", "last", "dictation", "export", "import", "audio", "completions"]),
        ("mo", ["models"]),
        ("--json mo", ["models"]),
        ("--port 9 st", ["status"]),
        ("models ", ["list", "load", "unload", "delete"]),
        ("models load --", ["--engine", "--model", .. Globals]),
        ("models unload --e", ["--engine"]),
        ("models list --", Globals),
        ("status --", Globals),
        ("history last --", Globals),
        ("history search notes --", ["--query", "--limit", "--offset", .. Globals]),
        ("dictation start --w", ["--workflow"]),
        ("transcribe rec.wav --l", ["--language", "--language-hint"]),
        ("transcribe rec.wav --task ", ["transcribe", "translate"]),
        ("audio s", ["show", "set"]),
        ("completions ", ["powershell", "bash", "zsh", "fish"]),
        ("completions bash --", ["--help"]),
        ("transcribe re", ["rec.wav"]),
        ("audio set re", ["rec.wav"]),
        ("export re", ["rec.wav"]),
        ("--profile pro", ["profdir"])
    ];

    // The fake typewhisper command echoes its arguments, so these cases check what the scripts forward.
    private static readonly (string Line, string[] Expected)[] DynamicCases =
    [
        ("--dev --port 9 models load --engine ", ["ARGS:__complete engines --dev --port 9", "whisper", "groq"]),
        ("models load --engine whisper --model ", ["ARGS:__complete models --engine whisper", "large-v3", "turbo"]),
        ("--api-token secret transcribe --model ", ["ARGS:__complete models --api-token secret", "large-v3", "turbo"]),
        // Bash splits C:/x at the colon; the script has to rejoin it.
        ("--profile C:/x models load --engine ", ["ARGS:__complete engines --profile C:/x", "whisper", "groq"])
    ];

    [Theory]
    [InlineData("powershell", "Register-ArgumentCompleter -Native")]
    [InlineData("bash", "complete -F _typewhisper typewhisper typewhisper.exe")]
    [InlineData("zsh", "#compdef typewhisper typewhisper.exe")]
    [InlineData("fish", "complete -c typewhisper -f")]
    public async Task CompletionsPrintScriptsWithoutRunningApp(string shell, string marker)
    {
        var result = await RunCliAsync(["completions", shell], environment: new() { ["TYPEWHISPER_PROFILE"] = Path.Join(Path.GetTempPath(), "missing-profile") });
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        Assert.Contains(marker, result.Output);
        Assert.DoesNotContain("\r", result.Output);
        Assert.DoesNotMatch("##[A-Z_]+##", result.Output);
        Assert.All(result.Output, character => Assert.True(character < 128, "Scripts stay ASCII for every console encoding."));
    }

    [Theory]
    [InlineData("completions")]
    [InlineData("completions cmd")]
    [InlineData("completions bash zsh")]
    [InlineData("completions bash --json")]
    public async Task CompletionsRejectInvalidArguments(string arguments)
    {
        var result = await RunCliAsync(arguments.Split(' '));
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.StartsWith("Error: Usage: typewhisper completions", result.Error);
    }

    [Theory]
    [InlineData("engines", "whisper\ngroq\n")]
    [InlineData("models --engine whisper", "large-v3\nturbo\n")]
    [InlineData("models", "large-v3\nturbo\nwhisper-large-v3\n")]
    public async Task HelperPrintsIdsFromRunningApp(string arguments, string expected)
    {
        using var server = Listen(out var port);
        var run = RunCliAsync(["__complete", .. arguments.Split(' '), "--port", port.ToString(), "--api-token", "test-token"]);
        var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("GET", context.Request.HttpMethod);
        Assert.Equal("/v1/models", context.Request.RawUrl);
        Assert.Equal("Bearer test-token", context.Request.Headers["Authorization"]);
        await Reply(context, 200, ModelsJson);
        var result = await run;
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        Assert.Equal(expected, result.Output);
    }

    [Theory]
    [InlineData("engines --port 1")]
    [InlineData("models --engine whisper --port 1")]
    [InlineData("engines --port")]
    [InlineData("models --profile")]
    [InlineData("engines --dev --profile somewhere")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task HelperStaysSilentWhenAppOrInputIsUnavailable(string arguments)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await RunCliAsync(["__complete", .. arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Output);
        Assert.Empty(result.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HelperGivesUpQuicklyOnUnresponsiveApp()
    {
        using var server = Listen(out var port);
        var stopwatch = Stopwatch.StartNew();
        var result = await RunCliAsync(["__complete", "engines", "--port", port.ToString()]);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Output);
        Assert.Empty(result.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void IdsSkipDuplicatesBlanksAndControlCharacters()
    {
        Assert.Equal(["whisper", "groq"], CliCompletions.Ids(ModelsJson, engines: true));
        Assert.Equal(["large-v3", "turbo"], CliCompletions.Ids(ModelsJson, engines: false, engine: " WHISPER "));
        Assert.Equal(["whisper-large-v3"], CliCompletions.Ids(ModelsJson, engines: false, engine: "groq"));
        Assert.Empty(CliCompletions.Ids("{\"status\":\"no_model\"}", engines: true));
    }

    public static TheoryData<string> Shells => ["bash", "zsh", "fish", "pwsh", "powershell"];

    [Theory]
    [MemberData(nameof(Shells))]
    public async Task GeneratedScriptCompletesInShell(string shell)
    {
        var executable = FindShell(shell);
        if (executable is null) return; // The shell is not installed on this machine.

        var root = Directory.CreateTempSubdirectory("typewhisper-completion-");
        try
        {
            var work = Directory.CreateDirectory(Path.Join(root.FullName, "work")).FullName;
            File.WriteAllText(Path.Join(work, "rec.wav"), "");
            Directory.CreateDirectory(Path.Join(work, "profdir"));
            var bin = CreateFakeCommand(root.FullName, shell);
            var scriptPath = Path.Join(root.FullName, "completion." + shell);
            var scriptShell = shell is "pwsh" or "powershell" ? "powershell" : shell;
            File.WriteAllText(scriptPath, (await RunCliAsync(["completions", scriptShell])).Output);

            var cases = StaticCases.Concat(DynamicCases).ToArray();
            var driverPath = Path.Join(root.FullName, "driver" + (shell is "pwsh" or "powershell" ? ".ps1" : "." + shell));
            File.WriteAllText(driverPath, Driver(shell, Unix(scriptPath, shell), Unix(work, shell), cases.Select(item => item.Line).ToArray()));

            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in shell switch
            {
                "pwsh" or "powershell" => new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", driverPath },
                "zsh" => ["-f", driverPath],
                "bash" => ["--norc", "--noprofile", Unix(driverPath, shell)],
                _ => [driverPath]
            }) start.ArgumentList.Add(argument);
            start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            var output = await stdout;
            Assert.True(process.ExitCode == 0, $"{shell} failed: {await stderr}\n{output}");

            var results = ParseResults(output);
            var mismatches = cases.Select((item, index) => (item.Line, item.Expected, Actual: results.GetValueOrDefault(index) ?? []))
                .Where(item => !item.Expected.Order(StringComparer.Ordinal).SequenceEqual(item.Actual.Order(StringComparer.Ordinal)))
                .Select(item => $"'typewhisper {item.Line}' completed [{string.Join(", ", item.Actual)}], expected [{string.Join(", ", item.Expected)}]")
                .ToList();
            Assert.True(mismatches.Count == 0, $"{shell}:\n{string.Join("\n", mismatches)}\n\nOutput:\n{output}\n{await stderr}");
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("\"some", "some dir")]
    [InlineData("'some", "some dir")]
    [InlineData("\"some dir/re", "some dir/rec.wav")]
    [InlineData("some\\ dir/re", "some dir/rec.wav")]
    public async Task BashLooksUpQuotedAndEscapedPathsLiterally(string word, string expected)
    {
        if (FindShell("bash") is not { } bash) return;
        var root = Directory.CreateTempSubdirectory("typewhisper-completion-");
        try
        {
            Directory.CreateDirectory(Path.Join(root.FullName, "some dir"));
            File.WriteAllText(Path.Join(root.FullName, "some dir", "rec.wav"), "");
            var script = Path.Join(root.FullName, "completion.bash");
            File.WriteAllText(script, (await RunCliAsync(["completions", "bash"])).Output);
            var start = new ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--norc", "--noprofile", "-c", """
                source "$1"; cd "$2"
                COMP_LINE="typewhisper transcribe $3"; COMP_POINT=${#COMP_LINE}
                COMP_WORDS=(typewhisper transcribe "$3"); COMP_CWORD=2
                _typewhisper; printf '%s\n' "${COMPREPLY[@]}"
                """.ReplaceLineEndings("\n"), "bash", Unix(script, "bash"), Unix(root.FullName, "bash"), word })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(expected, output.ReplaceLineEndings("\n").TrimEnd('\n'));
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("some\\ dir")]
    [InlineData("\"some dir\"")]
    [InlineData("'some dir'")]
    public async Task BashForwardsProfileValuesWithoutShellQuoting(string profile)
    {
        if (FindShell("bash") is not { } bash) return;
        var root = Directory.CreateTempSubdirectory("typewhisper-completion-");
        try
        {
            var bin = CreateFakeCommand(root.FullName, "bash");
            var script = Path.Join(root.FullName, "completion.bash");
            File.WriteAllText(script, (await RunCliAsync(["completions", "bash"])).Output);
            var start = new ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--norc", "--noprofile", "-c", """
                source "$1"
                COMP_LINE="typewhisper --profile $2 models load --engine "; COMP_POINT=${#COMP_LINE}
                COMP_WORDS=(typewhisper --profile "$2" models load --engine ""); COMP_CWORD=6
                _typewhisper; printf '%s\n' "${COMPREPLY[@]}"
                """.ReplaceLineEndings("\n"), "bash", Unix(script, "bash"), profile })
                start.ArgumentList.Add(argument);
            start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("ARGS:__complete engines --profile some dir", output.ReplaceLineEndings("\n").Split('\n'));
        }
        finally { root.Delete(recursive: true); }
    }

    private static string Driver(string shell, string script, string work, string[] lines)
    {
        static string Single(string value) => "'" + value.Replace("'", "'\\''") + "'";
        var driver = new StringBuilder();
        switch (shell)
        {
            case "bash":
                driver.Append($$"""
                    source {{Single(script)}}
                    cd {{Single(work)}}
                    t() { COMP_LINE="$1"; COMP_POINT=${#COMP_LINE}; shift; COMP_WORDS=(typewhisper "$@"); COMP_CWORD=$((${#COMP_WORDS[@]} - 1)); COMPREPLY=(); _typewhisper; if [ ${#COMPREPLY[@]} -gt 0 ]; then printf '%s\n' "${COMPREPLY[@]}"; fi; }

                    """);
                break;
            case "zsh":
                driver.Append($$"""
                    compdef() { :; }
                    source {{Single(script)}}
                    compadd() { local c; [[ $1 == -- ]] && shift; for c in "$@"; do [[ $c == "$PREFIX"* ]] && print -r -- $c; done; return 0; }
                    _files() { local f; if [[ $1 == -/ ]]; then for f in ${PREFIX}*(N/); do print -r -- $f; done; else for f in ${PREFIX}*(N); do print -r -- $f; done; fi; }
                    cd {{Single(work)}}
                    t() { words=(typewhisper "$@"); CURRENT=$#words; PREFIX="${words[-1]}"; _typewhisper; }

                    """);
                break;
            case "fish":
                driver.Append($"source {Single(script)}\ncd {Single(work)}\n");
                break;
            default:
                driver.Append($$"""
                    $ErrorActionPreference = 'Stop'
                    Get-Content -Raw -LiteralPath '{{script}}' | Out-String | Invoke-Expression
                    Set-Location -LiteralPath '{{work}}'
                    function T([string]$line) { (TabExpansion2 -inputScript $line -cursorColumn $line.Length).CompletionMatches | ForEach-Object { $_.CompletionText } }
                    # Diagnostic: does this PowerShell pass a word starting with '-' to a native completer?
                    $global:probe = @()
                    Register-ArgumentCompleter -Native -CommandName 'twprobe' -ScriptBlock { param($word) $global:probe += $word; [System.Management.Automation.CompletionResult]::new('x') }
                    $null = TabExpansion2 -inputScript 'twprobe a --e' -cursorColumn 13
                    "PROBE PowerShell $($PSVersionTable.PSVersion) native completer called for '--e': $($global:probe.Count -gt 0)"

                    """);
                break;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = "typewhisper " + lines[i];
            var words = lines[i].Split(' ');
            driver.Append($"echo '@@{i}'\n");
            driver.Append(shell switch
            {
                // Like bash with its default COMP_WORDBREAKS, the bash driver splits words at colons.
                "bash" => $"t {Single(line)} " + string.Join(' ', words
                    .SelectMany(word => word.Length == 0 ? [""] : Regex.Split(word, "(:)").Where(part => part.Length > 0))
                    .Select(Single)) + "\n",
                "zsh" => "t " + string.Join(' ', words.Select(Single)) + "\n",
                "fish" => $"complete -C {Single(line)} | string replace -r '\\t.*' ''\n",
                _ => $"T '{line}'\n"
            });
        }
        return driver.ToString().ReplaceLineEndings("\n");
    }

    private static Dictionary<int, List<string>> ParseResults(string output)
    {
        var results = new Dictionary<int, List<string>>();
        List<string>? current = null;
        foreach (var raw in output.ReplaceLineEndings("\n").Split('\n'))
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal)) { results[int.Parse(raw[2..])] = current = []; continue; }
            // Shells differ in how they decorate paths: ./rec.wav, .\rec.wav, profdir/.
            var value = raw.Trim();
            if (value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith(".\\", StringComparison.Ordinal)) value = value[2..];
            value = value.TrimEnd('/', '\\');
            if (value.Length > 0) current?.Add(value);
        }
        return results;
    }

    private static string CreateFakeCommand(string root, string shell)
    {
        var bin = Directory.CreateDirectory(Path.Join(root, "bin")).FullName;
        if (OperatingSystem.IsWindows() && shell is "pwsh" or "powershell")
        {
            File.WriteAllText(Path.Join(bin, "typewhisper.cmd"), "@echo ARGS:%*\r\n@call :ids %2\r\n@exit /b 0\r\n:ids\r\n@if \"%1\"==\"engines\" (echo whisper& echo groq) else (echo large-v3& echo turbo)\r\n");
            return bin;
        }
        var path = Path.Join(bin, "typewhisper");
        File.WriteAllText(path, "#!/bin/sh\necho \"ARGS:$*\"\nif [ \"$2\" = engines ]; then echo whisper; echo groq; else echo large-v3; echo turbo; fi\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bin;
    }

    // Git Bash reads C:/ paths; other shells take native paths.
    private static string Unix(string path, string shell) =>
        OperatingSystem.IsWindows() && shell == "bash" ? path.Replace('\\', '/') : path;

    private static string? FindShell(string shell)
    {
        if (OperatingSystem.IsWindows())
        {
            // Only Git Bash: the System32 bash.exe launches WSL with a different file system.
            if (shell == "bash")
            {
                var gitBash = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
                return File.Exists(gitBash) ? gitBash : null;
            }
            if (shell is "zsh" or "fish") return null;
        }
        else if (shell == "powershell") return null;

        var names = OperatingSystem.IsWindows() ? new[] { shell + ".exe" } : [shell];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Join(directory, name)))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<(int Exit, string Output, string Error)> RunCliAsync(string[] args, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Join(AppContext.BaseDirectory, "TypeWhisper.Cli.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Remove("TYPEWHISPER_API_TOKEN");
        start.Environment.Remove("TYPEWHISPER_PROFILE");
        foreach (var (key, value) in environment ?? []) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return (process.ExitCode, await stdout, await stderr);
    }

    private static HttpListener Listen(out int port)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static async Task Reply(HttpListenerContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
