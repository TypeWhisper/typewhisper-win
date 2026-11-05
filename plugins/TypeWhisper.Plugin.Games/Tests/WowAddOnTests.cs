using System.Text;
using TypeWhisper.Plugin.Games;

public sealed class WowAddOnTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "typewhisper-wow-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BundledAddOn_HasTocAndLuaWithMatchingVersionAndBinding()
    {
        var files = WowAddOn.BundledFiles().Where(file => file.Name != "Icon.tga").ToDictionary(file => file.Name, file => file.Text);

        Assert.Equal(["Icon.tga", "Status.lua", "TypeWhisper.lua", "TypeWhisper.toc"], WowAddOn.BundledFiles().Select(file => file.Name));
        // TypeWhisper's status loads before the add-on reads it.
        Assert.Matches(@"Status\.lua\s+TypeWhisper\.lua", files["TypeWhisper.toc"]);
        Assert.Contains("TypeWhisper.lua", files["TypeWhisper.toc"]);
        Assert.Contains("16001", files["TypeWhisper.toc"]);
        Assert.Matches(@"^\d+\.\d+\.\d+$", WowAddOn.BundledVersion);
        // The add-on binds the same chord TypeWhisper presses.
        Assert.Contains("\"CTRL-SHIFT-F11\"", files["TypeWhisper.lua"]);
        Assert.Contains("\"OPENCHAT\"", files["TypeWhisper.lua"]);
        Assert.Equal("Ctrl+Shift+F11", GameCatalog.WorldOfWarcraft.AddOnChordName);
        Assert.Equal("CTRL-SHIFT-F11", WowKeyName(GameCatalog.WorldOfWarcraft.AddOnChord));
        // The cast bar keeps its position between sessions.
        Assert.Contains("## SavedVariables: TypeWhisperDB", files["TypeWhisper.toc"]);
    }

    [Fact]
    public void BundledAddOn_HasAnIconTheGameCanLoad()
    {
        var files = WowAddOn.BundledFiles();
        var icon = files.Single(file => file.Name == "Icon.tga").Data;
        var toc = files.Single(file => file.Name == "TypeWhisper.toc").Text;

        // Uncompressed 32-bit true color, square, a power of two; the list shows it at 20 px.
        Assert.Equal(2, icon[2]);
        Assert.Equal(32, icon[16]);
        var width = BitConverter.ToUInt16(icon, 12);
        Assert.Equal(width, BitConverter.ToUInt16(icon, 14));
        Assert.True(width >= 32 && (width & (width - 1)) == 0);
        Assert.Equal(18 + width * width * 4, icon.Length);
        Assert.Contains(@"## IconTexture: Interface\AddOns\TypeWhisper\Icon.tga", toc);
    }

    [Fact]
    public void Install_CopiesTheIconByteForByte()
    {
        var retail = Path.Join(_directory, "_retail_");
        Directory.CreateDirectory(retail);
        File.WriteAllBytes(Path.Join(retail, "Wow.exe"), []);

        Assert.Empty(WowAddOn.Install(WowAddOn.VersionFolders(_directory)));

        var installed = File.ReadAllBytes(Path.Join(retail, "Interface", "AddOns", "TypeWhisper", "Icon.tga"));
        Assert.Equal(WowAddOn.BundledFiles().Single(file => file.Name == "Icon.tga").Data, installed);
    }

    [Fact]
    public void StatusContent_IsLuaThatNamesTheLlm()
    {
        Assert.Equal("TypeWhisperStatus = {\n\tllm = \"Groq · Llama 3.3\",\n\tready = true,\n}\n",
            StatusTable(WowAddOn.StatusContent("Groq · Llama 3.3", true)));
        Assert.Equal("TypeWhisperStatus = {\n\tready = false,\n}\n", StatusTable(WowAddOn.StatusContent(null, false)));
        // Quotes, backslashes, line breaks and the game's "|" escapes cannot break out of the string.
        Assert.Contains("llm = \"a\\\"b\\\\c d||e\",", WowAddOn.StatusContent("a\"b\\c\nd|e", false));
    }

    [Fact]
    public void WriteStatus_OnlyIntoAnInstalledAddOnAndOnlyWhenChanged()
    {
        var retail = Path.Join(_directory, "_retail_");
        Assert.False(WowAddOn.WriteStatus(retail, "Groq · Llama", true));

        var addOn = Path.Join(retail, "Interface", "AddOns", "TypeWhisper");
        Directory.CreateDirectory(addOn);
        File.WriteAllText(Path.Join(addOn, "TypeWhisper.toc"), "## Version: 1.0.0");

        Assert.True(WowAddOn.WriteStatus(retail, "Groq · Llama", true));
        Assert.False(WowAddOn.WriteStatus(retail, "Groq · Llama", true));
        Assert.True(WowAddOn.WriteStatus(retail, "Groq · Llama", false));
        Assert.Contains("ready = false", File.ReadAllText(Path.Join(addOn, "Status.lua")));
    }

    private static string StatusTable(string content) => content[content.IndexOf("TypeWhisperStatus", StringComparison.Ordinal)..];

    [Fact]
    public void BundledAddOn_BindsTheIndicatorChordsInOrder()
    {
        var lua = WowAddOn.BundledFiles().Single(file => file.Name == "TypeWhisper.lua").Text;
        var indicator = GameCatalog.WorldOfWarcraft.Indicator!;

        var recording = lua.IndexOf($"key = \"{WowKeyName(indicator.Recording)}\", button = \"TypeWhisperRecordingButton\", signal = \"recording\"", StringComparison.Ordinal);
        var transcribing = lua.IndexOf($"key = \"{WowKeyName(indicator.Transcribing)}\", button = \"TypeWhisperTranscribingButton\", signal = \"transcribing\"", StringComparison.Ordinal);
        var stopped = lua.IndexOf($"key = \"{WowKeyName(indicator.Stopped)}\", button = \"TypeWhisperStoppedButton\", signal = \"stopped\"", StringComparison.Ordinal);

        Assert.True(recording >= 0 && transcribing >= 0 && stopped >= 0, "Each chord TypeWhisper presses is bound to its signal.");
        // TypeWhisper brings its own indicator back when the add-on's bar times out, so both use the same limits.
        Assert.Contains($"RECORDING_TIMEOUT, TRANSCRIBING_TIMEOUT = {GameIndicatorSignals.RecordingLimit.TotalSeconds}, "
            + $"{GameIndicatorSignals.TranscribingLimit.TotalSeconds}", lua);
    }

    // The game's name for a chord: modifiers as CTRL and SHIFT, then the function key.
    private static string WowKeyName(IReadOnlyList<ushort> chord) => string.Join("-", chord.Select(key => key switch
    {
        0x11 => "CTRL",
        0x10 => "SHIFT",
        >= 0x70 and <= 0x7B => "F" + (key - 0x6F),
        _ => throw new ArgumentOutOfRangeException(nameof(chord), key, "Unexpected key.")
    }));

    [Fact]
    public void ProductDatabase_YieldsLengthPrefixedPaths()
    {
        // Protobuf strings: tag, varint length, bytes. The printable tag after a path must not join it.
        var data = new List<byte> { 0x0A, 0x03, (byte)'w', (byte)'o', (byte)'w' };
        AddString(data, 0x12, "E:/World of Warcraft");
        AddString(data, 0x22, "C:/ProgramData/Battle.net/Agent");
        AddString(data, 0x2A, "D:/" + new string('x', 140));
        AddString(data, 0x12, "E:/World of Warcraft");

        var paths = WowAddOn.PathsInProductDatabase(data.ToArray());

        Assert.Equal(["E:/World of Warcraft", "C:/ProgramData/Battle.net/Agent", "D:/" + new string('x', 140)], paths);
    }

    [Fact]
    public void VersionFolders_NeedAGameExecutable()
    {
        var forever = CreateVersion("_forever_", "Wow.exe");
        CreateVersion("_classic_era_", "WowClassic.exe");
        // A beta that was uninstalled leaves its folder without the game.
        Directory.CreateDirectory(Path.Join(_directory, "_classic_beta_", "Interface"));
        Directory.CreateDirectory(Path.Join(_directory, "Data"));

        var versions = WowAddOn.VersionFolders(_directory);

        Assert.Equal(["_classic_era_", "_forever_"], versions.Select(version => version.Folder));
        Assert.Equal(["Classic Era", "Forever"], versions.Select(version => version.Name));
        Assert.All(versions, version => Assert.Null(version.InstalledVersion));
        Assert.Equal(Path.Join(forever, "Interface", "AddOns", "TypeWhisper"), versions[1].AddOnPath);
    }

    [Fact]
    public void Install_WritesTheAddOnIntoEveryVersion()
    {
        CreateVersion("_forever_", "Wow.exe");
        CreateVersion("_retail_", "Wow.exe");

        var failures = WowAddOn.Install(WowAddOn.VersionFolders(_directory));

        Assert.Empty(failures);
        var versions = WowAddOn.VersionFolders(_directory);
        Assert.All(versions, version =>
        {
            Assert.Equal(WowAddOn.BundledVersion, version.InstalledVersion);
            Assert.True(File.Exists(Path.Join(version.AddOnPath, "TypeWhisper.lua")));
        });
    }

    [Fact]
    public void FindInstallRoots_TakesAChosenVersionFolderAsItsInstallation()
    {
        var forever = CreateVersion("_forever_", "Wow.exe");

        var roots = WowAddOn.FindInstallRoots(forever, detect: false);

        Assert.Equal([_directory.TrimEnd('\\')], roots);
        Assert.Empty(WowAddOn.FindInstallRoots(Path.Join(_directory, "missing"), detect: false));
    }

    private string CreateVersion(string folder, string executable)
    {
        var path = Path.Join(_directory, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Join(path, executable), "");
        return path;
    }

    private static void AddString(List<byte> data, byte tag, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        data.Add(tag);
        if (bytes.Length < 0x80) data.Add((byte)bytes.Length);
        else { data.Add((byte)(bytes.Length & 0x7F | 0x80)); data.Add((byte)(bytes.Length >> 7)); }
        data.AddRange(bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
