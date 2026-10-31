using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Cli;

/// <summary>
/// Generates shell completion scripts for the CLI and parses the IDs offered by its hidden
/// <c>__complete</c> helper. One command table feeds all shells so their contexts stay aligned.
/// </summary>
public static class CliCompletions
{
    /// <summary>Shells accepted by <c>typewhisper completions &lt;shell&gt;</c>.</summary>
    public static IReadOnlyList<string> Shells { get; } = ["powershell", "bash", "zsh", "fish"];

    private static readonly string[] Commands =
        ["status", "models", "transcribe", "history", "last", "dictation", "export", "import", "audio", "completions"];

    private static readonly string[] GlobalOptions =
        ["--port", "--api-token", "--dev", "--profile", "--json", "--help", "--version"];

    // An argument following one of these options is its value, never a command word.
    private static readonly string[] ValueOptions =
    [
        "--port", "--api-token", "--profile", "--language", "--language-hint", "--task", "--translate-to",
        "--engine", "--model", "--workflow", "--query", "--limit", "--offset"
    ];

    // Commands whose first argument selects a subcommand, so their options depend on it.
    private static readonly string[] SubcommandCommands = ["models", "history", "dictation", "audio", "completions"];

    // Command options, keyed by command or "command subcommand"; mirrors Program.Validate.
    private static readonly (string Context, string[] Options)[] CommandOptions =
    [
        ("transcribe", ["--language", "--language-hint", "--task", "--translate-to", "--engine", "--model", "--await-download", "--no-corrections"]),
        ("models load", ["--engine", "--model"]),
        ("models unload", ["--engine", "--model"]),
        ("models delete", ["--engine", "--model"]),
        ("dictation start", ["--workflow"]),
        ("history", ["--query", "--limit", "--offset"]),
        ("history search", ["--query", "--limit", "--offset"])
    ];

    // Positional arguments, keyed by "command/position" or "command subcommand/position".
    // The first entry is the kind (words, files or dirs) and the rest are the words.
    private static readonly (string Context, string[] Entry)[] Positionals =
    [
        ("/0", ["words", .. Commands]),
        ("models/1", ["words", "list", "load", "unload", "delete"]),
        ("history/1", ["words", "search", "last"]),
        ("dictation/1", ["words", "start", "stop", "status", "result"]),
        ("audio/1", ["words", "show", "set"]),
        ("completions/1", ["words", .. Shells]),
        ("transcribe/1", ["files"]),
        ("export/1", ["files"]),
        ("import/1", ["files"]),
        ("audio set/2", ["files"])
    ];

    private static readonly (string Option, string[] Values)[] OptionValues = [("--task", ["transcribe", "translate"])];

    /// <summary>
    /// Returns the completion script for <paramref name="shell"/>, or null for an unsupported shell.
    /// </summary>
    public static string? Script(string shell) => shell switch
    {
        "powershell" or "pwsh" => PowerShellScript(),
        "bash" => $"{Header("bash", "source <(typewhisper completions bash)")}{ShellCandidates()}{BashGlue}",
        "zsh" => $"#compdef typewhisper typewhisper.exe\n{Header("zsh", "source <(typewhisper completions zsh)")}{ShellCandidates()}{ZshGlue}",
        "fish" => $"{Header("fish", "typewhisper completions fish | source")}{FishScript()}",
        _ => null
    } is { } script ? script.ReplaceLineEndings("\n") : null;

    /// <summary>
    /// Returns the engine IDs, or the model IDs (optionally of one engine), from a <c>GET /v1/models</c> response.
    /// </summary>
    public static IReadOnlyList<string> Ids(string modelsJson, bool engines, string? engine = null)
    {
        using var document = JsonDocument.Parse(modelsJson);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return [];
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray().Where(model => model.ValueKind == JsonValueKind.Object))
        {
            var modelEngine = Text(model, "engine");
            if (!engines && !string.IsNullOrWhiteSpace(engine) && !string.Equals(modelEngine, engine.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            var id = engines ? modelEngine : Text(model, "id");
            // IDs come from plugins and remote servers. Shells read one per line and insert it unquoted,
            // so an ID with line breaks, spaces or shell syntax is never offered.
            if (!string.IsNullOrWhiteSpace(id) && !id.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || UnsafeIdCharacters.Contains(c)) && seen.Add(id))
                ids.Add(id);
        }
        return ids;
    }

    private const string UnsafeIdCharacters = "$`;|&<>(){}[]'\"\\*?!#~";

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Header(string shell, string load) => $"""
        # TypeWhisper CLI completion for {shell}. Generated by: typewhisper completions {shell}
        # Load it with: {load}


        """;

    private static string Quote(string value) => $"'{value}'";

    private static string Words(IEnumerable<string> values) => string.Join(' ', values.Select(Quote));

    // Shared by bash and zsh: positional parameters and case only, no arrays.
    private static string ShellCandidates()
    {
        var options = new StringBuilder();
        foreach (var group in CommandOptions.GroupBy(item => string.Join(' ', item.Options)))
            options.Append($"                {string.Join('|', group.Select(item => Quote(item.Context)))}) printf '%s\\n' {Words(group.First().Options)} ;;\n");
        var positionals = new StringBuilder();
        foreach (var group in Positionals.GroupBy(item => string.Join(' ', item.Entry)))
            positionals.Append($"        {string.Join('|', group.Select(item => Quote(item.Context)))}) printf '%s\\n' {Words(group.First().Entry)} ;;\n");
        var values = new StringBuilder();
        foreach (var (option, optionValues) in OptionValues)
            values.Append($"            {Quote(option)}) printf '%s\\n' words {Words(optionValues)} ;;\n");

        return Fill("""
            # Prints the completion kind (words, files, dirs or none), then the candidate words.
            # Arguments: the command, the word being completed and the words before it.
            __typewhisper_candidates() {
                local exe="$1" cur="$2" cmd="" sub="" count=0 pending="" engine="" profile="" port="" token="" dev="" key="" word=""
                shift 2
                for word in "$@"; do
                    if [ -n "$pending" ]; then
                        case "$pending" in
                            --engine) engine="$word" ;;
                            --profile)
                                case "$word" in
                                    \"*\"|\'*\') profile="${word#?}"; profile="${profile%?}" ;;
                                    *) profile="$word" ;;
                                esac
                                ;;
                            --port) port="$word" ;;
                            --api-token) token="$word" ;;
                        esac
                        pending=""
                        continue
                    fi
                    case "$word" in
                        ##VALUE_OPTIONS##) pending="$word" ;;
                        --dev) dev=1 ;;
                        -*) ;;
                        *)
                            count=$((count + 1))
                            if [ "$count" -eq 1 ]; then cmd="$word"; elif [ "$count" -eq 2 ]; then sub="$word"; fi
                            ;;
                    esac
                done

                if [ -n "$pending" ]; then
                    case "$pending" in
                        --engine|--model)
                            printf '%s\n' words
                            if [ "$pending" = --engine ]; then
                                set -- __complete engines
                            else
                                set -- __complete models
                                if [ -n "$engine" ]; then set -- "$@" --engine "$engine"; fi
                            fi
                            if [ -n "$dev" ]; then set -- "$@" --dev; fi
                            if [ -n "$profile" ]; then set -- "$@" --profile "$profile"; fi
                            if [ -n "$port" ]; then set -- "$@" --port "$port"; fi
                            if [ -n "$token" ]; then set -- "$@" --api-token "$token"; fi
                            "$exe" "$@" 2>/dev/null
                            ;;
                        --profile) printf '%s\n' dirs ;;
            ##VALUES##
                        *) printf '%s\n' none ;;
                    esac
                    return 0
                fi

                case "$cur" in
                    -*)
                        printf '%s\n' words
                        key="$cmd"
                        if [ "$count" -ge 2 ]; then
                            case "$cmd" in
                                ##SUBCOMMAND_COMMANDS##) key="$cmd $sub" ;;
                            esac
                        fi
                        case "$key" in
            ##OPTIONS##
                        esac
                        case "$cmd" in
                            completions) printf '%s\n' --help ;;
                            *) printf '%s\n' ##GLOBAL_OPTIONS## ;;
                        esac
                        return 0
                        ;;
                esac

                case "$count" in
                    0) key="/0" ;;
                    1) key="$cmd/1" ;;
                    2) key="$cmd $sub/2" ;;
                    *) key="" ;;
                esac
                case "$key" in
            ##POSITIONALS##
                    *) printf '%s\n' none ;;
                esac
            }

            """,
            ("VALUE_OPTIONS", string.Join('|', ValueOptions)),
            ("SUBCOMMAND_COMMANDS", string.Join('|', SubcommandCommands)),
            ("GLOBAL_OPTIONS", Words(GlobalOptions)),
            ("VALUES", values.ToString()),
            ("OPTIONS", options.ToString()),
            ("POSITIONALS", positionals.ToString()));
    }

    // Replaces ##NAME## tokens. A token alone on its line is replaced together with its line break,
    // so generated blocks keep their own indentation.
    private static string Fill(string template, params (string Name, string Value)[] values)
    {
        template = template.ReplaceLineEndings("\n");
        foreach (var (name, value) in values)
            template = Regex.Replace(template, $"^##{name}##\n", _ => value, RegexOptions.Multiline).Replace($"##{name}##", value);
        return template;
    }

    private const string BashGlue = """
        _typewhisper() {
            local kind="" line="" word="" rest="" trimmed="" prefix="" i=0 offset=0 glued=0
            local -a words=() candidates=()
            COMPREPLY=()
            # Bash splits words at ':', so C:/Users arrives as C, : and /Users. Rejoin words that no space separates.
            line="${COMP_LINE:0:COMP_POINT}"
            for ((i = 0; i <= COMP_CWORD; i++)); do
                word="${COMP_WORDS[i]}"
                rest="${line:offset}"
                trimmed="${rest#"${rest%%[![:space:]]*}"}"
                glued=0
                if [ "$i" -gt 0 ] && [ "${#trimmed}" -eq "${#rest}" ]; then glued=1; fi
                offset=$((offset + ${#rest} - ${#trimmed} + ${#word}))
                if [ "$glued" -eq 1 ] && { [ "$word" = ":" ] || [[ "${words[${#words[@]}-1]}" == *: ]]; }; then
                    words[${#words[@]}-1]+="$word"
                else
                    words+=("$word")
                fi
            done
            local cur="${words[${#words[@]}-1]}"
            while IFS= read -r line; do
                if [ -z "$kind" ]; then kind="$line"; else candidates+=("$line"); fi
            done < <(__typewhisper_candidates "${words[0]}" "$cur" "${words[@]:1:${#words[@]}-2}")
            case "$kind" in
                words)
                    # Literal prefix matching: compgen -W would expand IDs from the app as shell words.
                    for line in ${candidates[@]+"${candidates[@]}"}; do
                        if [[ "$line" == "$cur"* ]]; then COMPREPLY+=("$line"); fi
                    done
                    ;;
                files|dirs)
                    type compopt >/dev/null 2>&1 && compopt -o filenames
                    while IFS= read -r line; do
                        COMPREPLY+=("$line")
                    done < <(if [ "$kind" = dirs ]; then compgen -d -- "$cur"; else compgen -f -- "$cur"; fi)
                    ;;
            esac
            # Bash only replaces the text after the last ':' of the word being completed.
            if [[ "$cur" == *:* && "$COMP_WORDBREAKS" == *:* ]]; then
                prefix="${cur%"${cur##*:}"}"
                for ((i = 0; i < ${#COMPREPLY[@]}; i++)); do COMPREPLY[i]="${COMPREPLY[i]#"$prefix"}"; done
            fi
        }

        complete -F _typewhisper typewhisper typewhisper.exe

        """;

    private const string ZshGlue = """
        _typewhisper() {
            local -a candidates
            candidates=("${(@f)$(__typewhisper_candidates "${(Q)words[1]}" "${(Q)PREFIX}" "${(@Q)words[2,CURRENT-1]}")}")
            case "$candidates[1]" in
                words) (( $#candidates > 1 )) && compadd -- "${(@)candidates[2,-1]}" ;;
                files) _files ;;
                dirs) _files -/ ;;
            esac
        }

        if [ "$funcstack[1]" = "_typewhisper" ]; then
            _typewhisper "$@"
        else
            compdef _typewhisper typewhisper typewhisper.exe
        fi

        """;

    private static string FishScript()
    {
        var options = new StringBuilder();
        foreach (var group in CommandOptions.GroupBy(item => string.Join(' ', item.Options)))
            options.Append($"            case {string.Join(' ', group.Select(item => Quote(item.Context)))}\n                printf '%s\\n' {Words(group.First().Options)}\n");
        var positionals = new StringBuilder();
        foreach (var group in Positionals.GroupBy(item => string.Join(' ', item.Entry)))
            positionals.Append($"        case {string.Join(' ', group.Select(item => Quote(item.Context)))}\n            printf '%s\\n' {Words(group.First().Entry)}\n");
        var values = new StringBuilder();
        foreach (var (option, optionValues) in OptionValues)
            values.Append($"            case {Quote(option)}\n                printf '%s\\n' words {Words(optionValues)}\n");

        return Fill("""
            # Prints the completion kind (words, files, dirs or none), then the candidate words.
            # Arguments: the command, the word being completed and the words before it.
            function __typewhisper_candidates
                set -l exe $argv[1]
                set -l cur $argv[2]
                set -l cmd ''
                set -l sub ''
                set -l count 0
                set -l pending ''
                set -l engine ''
                set -l profile ''
                set -l port ''
                set -l token ''
                set -l dev ''
                set -e argv[1]
                set -e argv[1]
                for word in $argv
                    if test -n "$pending"
                        switch $pending
                            case --engine
                                set engine $word
                            case --profile
                                set profile $word
                            case --port
                                set port $word
                            case --api-token
                                set token $word
                        end
                        set pending ''
                        continue
                    end
                    switch $word
                        case ##VALUE_OPTIONS##
                            set pending $word
                        case --dev
                            set dev 1
                        case '-*'
                        case '*'
                            set count (math $count + 1)
                            if test $count -eq 1
                                set cmd $word
                            else if test $count -eq 2
                                set sub $word
                            end
                    end
                end

                if test -n "$pending"
                    switch $pending
                        case --engine --model
                            printf '%s\n' words
                            set -l arguments __complete engines
                            if test $pending = --model
                                set arguments __complete models
                                test -n "$engine"; and set -a arguments --engine $engine
                            end
                            test -n "$dev"; and set -a arguments --dev
                            test -n "$profile"; and set -a arguments --profile $profile
                            test -n "$port"; and set -a arguments --port $port
                            test -n "$token"; and set -a arguments --api-token $token
                            $exe $arguments 2>/dev/null
                        case --profile
                            printf '%s\n' dirs
            ##VALUES##
                        case '*'
                            printf '%s\n' none
                    end
                    return 0
                end

                if string match -q -- '-*' $cur
                    printf '%s\n' words
                    set -l key $cmd
                    if test $count -ge 2; and contains -- $cmd ##SUBCOMMAND_COMMANDS##
                        set key "$cmd $sub"
                    end
                    switch $key
            ##OPTIONS##
                    end
                    if test "$cmd" = completions
                        printf '%s\n' --help
                    else
                        printf '%s\n' ##GLOBAL_OPTIONS##
                    end
                    return 0
                end

                set -l key ''
                switch $count
                    case 0
                        set key /0
                    case 1
                        set key "$cmd/1"
                    case 2
                        set key "$cmd $sub/2"
                end
                switch $key
            ##POSITIONALS##
                    case '*'
                        printf '%s\n' none
                end
            end

            function __typewhisper_complete
                set -l tokens (commandline -opc)
                set -l cur (commandline -ct)
                set -l exe $tokens[1]
                set -e tokens[1]
                set -l candidates (__typewhisper_candidates $exe "$cur" $tokens)
                set -l kind $candidates[1]
                set -e candidates[1]
                switch "$kind"
                    case words
                        printf '%s\n' $candidates
                    case files
                        __fish_complete_path "$cur"
                    case dirs
                        __fish_complete_directories "$cur"
                end
            end

            complete -c typewhisper -f -a '(__typewhisper_complete)'
            complete -c typewhisper.exe -f -a '(__typewhisper_complete)'

            """,
            ("VALUE_OPTIONS", string.Join(' ', ValueOptions)),
            ("SUBCOMMAND_COMMANDS", string.Join(' ', SubcommandCommands)),
            ("GLOBAL_OPTIONS", Words(GlobalOptions)),
            ("VALUES", values.ToString()),
            ("OPTIONS", options.ToString()),
            ("POSITIONALS", positionals.ToString()));
    }

    private static string PowerShellTable(IEnumerable<(string Key, string[] Values)> rows) =>
        string.Join("\n", rows.Select(row => $"        {Quote(row.Key)} = @({string.Join(", ", row.Values.Select(Quote))})"));

    private static string PowerShellScript()
    {
        var options = PowerShellTable(CommandOptions.Select(item => (item.Context, item.Options)));
        var positionals = PowerShellTable(Positionals.Select(item => (item.Context, item.Entry)));
        var values = PowerShellTable(OptionValues.Select(item => (item.Option, item.Values)));
        string List(IEnumerable<string> items) => string.Join(", ", items.Select(Quote));

        // Windows PowerShell 5.1 compatible: no ternary, null-coalescing or pipeline-chain operators.
        return Fill("""
            # TypeWhisper CLI completion for PowerShell (Windows PowerShell 5.1 and PowerShell 7).
            # Generated by: typewhisper completions powershell
            # Load it with: typewhisper completions powershell | Out-String | Invoke-Expression

            $global:__TypeWhisperCompleter = {
                param($wordToComplete, $commandAst, $cursorPosition)

                $valueOptions = @(##VALUE_OPTIONS##)
                $subcommandCommands = @(##SUBCOMMAND_COMMANDS##)
                $globalOptions = @(##GLOBAL_OPTIONS##)
                $commandOptions = @{
            ##OPTIONS##
                }
                $positionals = @{
            ##POSITIONALS##
                }
                $optionValues = @{
            ##VALUES##
                }

                $elements = $commandAst.CommandElements
                $exe = $elements[0].Extent.Text
                if ($elements[0] -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $exe = $elements[0].Value }
                $cmd = ''; $sub = ''; $count = 0; $pending = ''
                $engine = ''; $profileDirectory = ''; $port = ''; $token = ''; $dev = $false
                for ($i = 1; $i -lt $elements.Count; $i++) {
                    $element = $elements[$i]
                    # The element under the cursor is the word being completed.
                    if ($element.Extent.EndOffset -ge $cursorPosition) { break }
                    $word = $element.Extent.Text
                    if ($element -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $word = $element.Value }
                    if ($pending) {
                        if ($pending -eq '--engine') { $engine = $word }
                        elseif ($pending -eq '--profile') { $profileDirectory = $word }
                        elseif ($pending -eq '--port') { $port = $word }
                        elseif ($pending -eq '--api-token') { $token = $word }
                        $pending = ''
                    }
                    elseif ($valueOptions -ccontains $word) { $pending = $word }
                    elseif ($word -ceq '--dev') { $dev = $true }
                    elseif (-not $word.StartsWith('-')) {
                        $count++
                        if ($count -eq 1) { $cmd = $word } elseif ($count -eq 2) { $sub = $word }
                    }
                }

                $kind = 'none'
                $candidates = @()
                if ($pending) {
                    if ($pending -eq '--engine' -or $pending -eq '--model') {
                        $kind = 'words'
                        $arguments = @('__complete', 'engines')
                        if ($pending -eq '--model') {
                            $arguments = @('__complete', 'models')
                            if ($engine) { $arguments += @('--engine', $engine) }
                        }
                        if ($dev) { $arguments += '--dev' }
                        # Windows PowerShell 5.1 escapes the closing quote after a trailing backslash.
                        if ($profileDirectory.Length -gt 3) { $profileDirectory = $profileDirectory.TrimEnd('\', '/') }
                        if ($profileDirectory) { $arguments += @('--profile', $profileDirectory) }
                        if ($port) { $arguments += @('--port', $port) }
                        if ($token) { $arguments += @('--api-token', $token) }
                        try { $candidates = @(& $exe @arguments 2>$null) } catch { $candidates = @() }
                    }
                    elseif ($pending -eq '--profile') { $kind = 'dirs' }
                    elseif ($optionValues.ContainsKey($pending)) { $kind = 'words'; $candidates = $optionValues[$pending] }
                }
                elseif ($wordToComplete.StartsWith('-')) {
                    $kind = 'words'
                    $key = $cmd
                    if ($count -ge 2 -and $subcommandCommands -ccontains $cmd) { $key = "$cmd $sub" }
                    if ($commandOptions.ContainsKey($key)) { $candidates += $commandOptions[$key] }
                    if ($cmd -eq 'completions') { $candidates += '--help' } else { $candidates += $globalOptions }
                }
                else {
                    $key = ''
                    if ($count -eq 0) { $key = '/0' } elseif ($count -eq 1) { $key = "$cmd/1" } elseif ($count -eq 2) { $key = "$cmd $sub/2" }
                    if ($positionals.ContainsKey($key)) {
                        $entry = $positionals[$key]
                        $kind = $entry[0]
                        $candidates = @($entry | Select-Object -Skip 1)
                    }
                }

                if ($kind -eq 'files') {
                    return [System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete)
                }
                if ($kind -eq 'dirs') {
                    return [System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete) |
                        Where-Object { $_.ResultType -eq [System.Management.Automation.CompletionResultType]::ProviderContainer }
                }
                $resultType = [System.Management.Automation.CompletionResultType]::ParameterValue
                if ($wordToComplete.StartsWith('-')) { $resultType = [System.Management.Automation.CompletionResultType]::ParameterName }
                $candidates |
                    Where-Object { $_ -and $_.StartsWith($wordToComplete, [System.StringComparison]::Ordinal) } |
                    ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, $resultType, $_) }
            }

            Register-ArgumentCompleter -Native -CommandName 'typewhisper', 'typewhisper.exe' -ScriptBlock $global:__TypeWhisperCompleter

            # Windows PowerShell 5.1 does not pass words that start with '-' to native argument completers.
            # Wrap TabExpansion2 once so those words reach the completer when the original finds nothing.
            if ($PSVersionTable.PSVersion.Major -lt 6 -and -not (Test-Path Function:\__TypeWhisperTabExpansion2)) {
                ${function:global:__TypeWhisperTabExpansion2} = ${function:TabExpansion2}
                function global:TabExpansion2 {
                    [CmdletBinding(DefaultParameterSetName = 'ScriptInputSet')]
                    param(
                        [Parameter(ParameterSetName = 'ScriptInputSet', Mandatory = $true, Position = 0)]
                        [string] $inputScript,
                        [Parameter(ParameterSetName = 'ScriptInputSet', Position = 1)]
                        [int] $cursorColumn = $inputScript.Length,
                        [Parameter(ParameterSetName = 'AstInputSet', Mandatory = $true, Position = 0)]
                        [System.Management.Automation.Language.Ast] $ast,
                        [Parameter(ParameterSetName = 'AstInputSet', Mandatory = $true, Position = 1)]
                        [System.Management.Automation.Language.Token[]] $tokens,
                        [Parameter(ParameterSetName = 'AstInputSet', Mandatory = $true, Position = 2)]
                        [System.Management.Automation.Language.IScriptPosition] $positionOfCursor,
                        [Parameter(ParameterSetName = 'ScriptInputSet', Position = 2)]
                        [Parameter(ParameterSetName = 'AstInputSet', Position = 3)]
                        [Hashtable] $options = $null
                    )

                    $result = __TypeWhisperTabExpansion2 @PSBoundParameters
                    if ($result -and $result.CompletionMatches.Count -gt 0) { return $result }
                    try {
                        if ($PSCmdlet.ParameterSetName -eq 'ScriptInputSet') {
                            $parsed = [System.Management.Automation.CommandCompletion]::MapStringInputToParsedInput($inputScript, $cursorColumn)
                            $ast = $parsed.Item1; $tokens = $parsed.Item2; $positionOfCursor = $parsed.Item3
                        }
                        $offset = $positionOfCursor.Offset
                        $token = $tokens | Where-Object { $_.Extent.StartOffset -lt $offset -and $_.Extent.EndOffset -eq $offset } | Select-Object -Last 1
                        if (-not $token -or -not $token.Text.StartsWith('-')) { return $result }
                        $commandAst = $ast.FindAll({
                            param($node)
                            $node -is [System.Management.Automation.Language.CommandAst] -and
                                $node.Extent.StartOffset -le $offset -and $node.Extent.EndOffset -ge $offset
                        }, $true) | Select-Object -Last 1
                        if (-not $commandAst -or -not $commandAst.GetCommandName()) { return $result }
                        if (@('typewhisper', 'typewhisper.exe') -notcontains [System.IO.Path]::GetFileName($commandAst.GetCommandName())) { return $result }
                        $found = New-Object 'System.Collections.ObjectModel.Collection[System.Management.Automation.CompletionResult]'
                        foreach ($item in @(& $global:__TypeWhisperCompleter $token.Text $commandAst $offset)) { $found.Add($item) }
                        if ($found.Count -eq 0) { return $result }
                        return New-Object System.Management.Automation.CommandCompletion($found, -1, $token.Extent.StartOffset, $token.Text.Length)
                    }
                    catch { return $result }
                }
            }

            """,
            ("VALUE_OPTIONS", List(ValueOptions)),
            ("SUBCOMMAND_COMMANDS", List(SubcommandCommands)),
            ("GLOBAL_OPTIONS", List(GlobalOptions)),
            ("OPTIONS", options + "\n"),
            ("POSITIONALS", positionals + "\n"),
            ("VALUES", values + "\n"));
    }
}
