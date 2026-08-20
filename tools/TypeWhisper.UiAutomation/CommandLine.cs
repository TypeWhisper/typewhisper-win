namespace TypeWhisper.UiAutomation;

internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options;

    private CommandLine(string command, Dictionary<string, string?> options)
    {
        Command = command;
        _options = options;
    }

    public string Command { get; }

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("A command is required.");

        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{name}'.");

            string? value = null;
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++index];

            if (!options.TryAdd(name, value))
                throw new ArgumentException($"Option '{name}' was specified more than once.");
        }

        return new CommandLine(args[0].ToLowerInvariant(), options);
    }

    public bool HasFlag(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public string RequirePath(string name, bool mustExist)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Option {name} <path> is required.");

        var path = Path.GetFullPath(value);
        if (mustExist && !File.Exists(path))
            throw new FileNotFoundException($"File passed to {name} does not exist.", path);
        return path;
    }

    public TimeSpan GetTimeout()
    {
        var value = Get("--timeout");
        if (string.IsNullOrWhiteSpace(value))
            return TimeSpan.FromSeconds(30);
        if (!int.TryParse(value, out var seconds) || seconds is < 1 or > 300)
            throw new ArgumentException("--timeout must be between 1 and 300 seconds.");
        return TimeSpan.FromSeconds(seconds);
    }
}
