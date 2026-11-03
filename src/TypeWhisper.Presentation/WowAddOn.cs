using System.Text;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>One installed World of Warcraft version, such as _retail_ or _forever_.</summary>
/// <param name="Folder">The version folder name, such as "_forever_".</param>
/// <param name="Path">Full path of the version folder.</param>
/// <param name="InstalledVersion">Version of the TypeWhisper add-on found there, or null when it is missing.</param>
public sealed record WowVersionFolder(string Folder, string Path, string? InstalledVersion)
{
    /// <summary>A short display name; product names stay untranslated.</summary>
    public string Name => Folder switch
    {
        "_retail_" => "Retail",
        "_classic_" => "Classic",
        "_classic_era_" => "Classic Era",
        "_anniversary_" => "Anniversary",
        "_forever_" => "Forever",
        "_ptr_" => "PTR",
        "_xptr_" => "PTR 2",
        "_beta_" => "Beta",
        "_classic_ptr_" => "Classic PTR",
        "_classic_beta_" => "Classic Beta",
        "_classic_era_ptr_" => "Classic Era PTR",
        _ => Folder.Trim('_').Replace('_', ' ')
    };

    /// <summary>The add-on folder inside this version.</summary>
    public string AddOnPath => System.IO.Path.Combine(Path, "Interface", "AddOns", WowAddOn.FolderName);
}

/// <summary>One file of the bundled add-on, copied byte for byte.</summary>
/// <param name="Name">File name inside the add-on folder.</param>
/// <param name="Data">File contents.</param>
public sealed record WowAddOnFile(string Name, byte[] Data)
{
    /// <summary>The contents of a text file (TOC or Lua).</summary>
    public string Text => Encoding.UTF8.GetString(Data);
}

/// <summary>Finds World of Warcraft installations and installs the TypeWhisper add-on into them.</summary>
public static partial class WowAddOn
{
    /// <summary>The add-on folder and TOC name.</summary>
    public const string FolderName = "TypeWhisper";
    private const string ResourcePrefix = "WowAddOn/";

    /// <summary>The bundled add-on version from its TOC file.</summary>
    public static string BundledVersion { get; } = ReadVersion(BundledFiles().First(file => file.Name == FolderName + ".toc").Text) ?? "0";

    /// <summary>The add-on files shipped with TypeWhisper.</summary>
    public static IReadOnlyList<WowAddOnFile> BundledFiles()
    {
        var assembly = typeof(WowAddOn).Assembly;
        return assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var data = new MemoryStream();
                stream.CopyTo(data);
                return new WowAddOnFile(name[ResourcePrefix.Length..], data.ToArray());
            }).ToArray();
    }

    /// <summary>
    /// Returns the World of Warcraft folders to look in: a folder the player chose and, with <paramref name="detect"/>,
    /// the install paths Battle.net records in its product database and the default install locations. Only folders
    /// that exist are returned.
    /// </summary>
    public static IReadOnlyList<string> FindInstallRoots(string? chosenFolder = null, bool detect = true, string? productDatabase = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(chosenFolder)) candidates.Add(chosenFolder);
        if (detect)
        {
            productDatabase ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Battle.net", "Agent", "product.db");
            try { candidates.AddRange(PathsInProductDatabase(File.ReadAllBytes(productDatabase))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            foreach (var programs in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                var folder = Environment.GetFolderPath(programs);
                if (folder.Length > 0) candidates.Add(System.IO.Path.Combine(folder, "World of Warcraft"));
            }
        }
        var roots = new List<string>();
        foreach (var candidate in candidates)
        {
            string full;
            // GetFullPath turns the database's forward slashes into the platform separator.
            try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(candidate)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            // A chosen version folder, such as "_retail_", stands for its installation.
            if (VersionFolderPattern().IsMatch(System.IO.Path.GetFileName(full)) && System.IO.Path.GetDirectoryName(full) is { } parent) full = parent;
            if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }
        return roots;
    }

    /// <summary>
    /// Reads the install paths from Battle.net's product database. It is a protobuf file; each path is a
    /// length-prefixed string, so only strings whose length prefix matches are taken.
    /// </summary>
    public static IReadOnlyList<string> PathsInProductDatabase(ReadOnlySpan<byte> data)
    {
        var paths = new List<string>();
        for (var start = 2; start + 2 < data.Length; start++)
        {
            if (!IsAsciiLetter(data[start]) || data[start + 1] != (byte)':' || data[start + 2] is not ((byte)'/' or (byte)'\\')) continue;
            // A one-byte varint length, or a two-byte one for paths of 128 bytes or more.
            int length = data[start - 1];
            if (length >= 0x80 || length < 3)
            {
                if ((data[start - 2] & 0x80) == 0) continue;
                length = (data[start - 2] & 0x7F) | (data[start - 1] << 7);
            }
            if (length < 3 || start + length > data.Length) continue;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(data.Slice(start, length)); }
            catch (DecoderFallbackException) { continue; }
            if (text.Any(char.IsControl) || paths.Contains(text, StringComparer.OrdinalIgnoreCase)) continue;
            paths.Add(text);
        }
        return paths;
    }

    /// <summary>Lists the game versions installed under <paramref name="root"/>; a version needs a game executable.</summary>
    public static IReadOnlyList<WowVersionFolder> VersionFolders(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(path => VersionFolderPattern().IsMatch(System.IO.Path.GetFileName(path))
                    && Directory.EnumerateFiles(path, "Wow*.exe").Any())
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => new WowVersionFolder(System.IO.Path.GetFileName(path), path, InstalledVersion(path)))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Writes the bundled add-on into every version folder; returns the folders that failed with their error.</summary>
    public static IReadOnlyList<(WowVersionFolder Folder, string Error)> Install(IEnumerable<WowVersionFolder> folders)
    {
        var files = BundledFiles();
        var failures = new List<(WowVersionFolder, string)>();
        foreach (var folder in folders)
        {
            try
            {
                Directory.CreateDirectory(folder.AddOnPath);
                foreach (var file in files)
                    AtomicFileWriter.WriteAllBytes(System.IO.Path.Combine(folder.AddOnPath, file.Name), file.Data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add((folder, ex.Message)); }
        }
        return failures;
    }

    /// <summary>The file TypeWhisper writes for the add-on; the game loads it with the add-on on /reload.</summary>
    public const string StatusFileName = "Status.lua";

    /// <summary>
    /// Tells the add-on which LLM translates its chat messages. The game only reads add-on files on /reload, so the
    /// add-on shows what TypeWhisper wrote last. Writes only into an installed add-on, and only when the text changed.
    /// Returns false when nothing was written.
    /// </summary>
    public static bool WriteStatus(string? versionFolder, string? translationLlm, bool translationReady)
    {
        if (!IsInstalledIn(versionFolder)) return false;
        var path = System.IO.Path.Combine(versionFolder!, "Interface", "AddOns", FolderName, StatusFileName);
        var content = StatusContent(translationLlm, translationReady);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return false;
            AtomicFileWriter.WriteAllText(path, content);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The Lua source of the status file.</summary>
    public static string StatusContent(string? translationLlm, bool translationReady)
    {
        var text = new StringBuilder()
            .Append("-- Written by TypeWhisper when you dictate into the game or open its Advanced settings. The game reads it on /reload.\n")
            .Append("-- TypeWhisper replaces this file, so changes here do not last.\n")
            .Append("TypeWhisperStatus = {\n");
        if (!string.IsNullOrWhiteSpace(translationLlm)) text.Append("\tllm = \"").Append(LuaString(translationLlm)).Append("\",\n");
        text.Append("\tready = ").Append(translationReady ? "true" : "false").Append(",\n}\n");
        return text.ToString();
    }

    // A short, single-line Lua string. "|" starts the game's text escapes, so it is doubled to show as itself.
    private static string LuaString(string value)
    {
        var text = new StringBuilder();
        foreach (var c in value.Length > 120 ? value[..120] : value)
            text.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '|' => "||", _ when char.IsControl(c) => " ", _ => c.ToString() });
        return text.ToString();
    }

    /// <summary>Whether the add-on is installed in the game version in <paramref name="versionFolder"/>.</summary>
    public static bool IsInstalledIn(string? versionFolder)
    {
        if (string.IsNullOrWhiteSpace(versionFolder)) return false;
        try { return File.Exists(System.IO.Path.Combine(versionFolder, "Interface", "AddOns", FolderName, FolderName + ".toc")); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    private static string? InstalledVersion(string versionFolder)
    {
        try
        {
            var toc = System.IO.Path.Combine(versionFolder, "Interface", "AddOns", FolderName, FolderName + ".toc");
            return File.Exists(toc) ? ReadVersion(File.ReadAllText(toc)) ?? "0" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "0"; }
    }

    private static string? ReadVersion(string toc) => VersionLinePattern().Match(toc) is { Success: true } match ? match.Groups[1].Value.Trim() : null;

    private static bool IsAsciiLetter(byte value) => value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    [GeneratedRegex(@"^_[a-z0-9_]+_$")]
    private static partial Regex VersionFolderPattern();

    [GeneratedRegex(@"^##\s*Version:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex VersionLinePattern();
}
