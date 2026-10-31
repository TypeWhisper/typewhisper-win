using System.Text;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class PowerShellCompletionProfilesTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "TypeWhisper-completion-test-" + Guid.NewGuid());
    private readonly string _documents;
    private readonly string _stable;
    private readonly string _beta;
    private bool _coreInstalled = true;
    private bool _windowsAllowsScripts;
    private readonly bool _coreAllowsScripts = true;

    public PowerShellCompletionProfilesTests()
    {
        _documents = Path.Join(_root, "Documents");
        _stable = Path.Join(_root, "TypeWhisper", "Cli");
        _beta = Path.Join(_root, "TypeWhisper-Beta", "Cli");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private PowerShellCompletionProfiles Service() => new(_documents,
        edition => edition == PowerShellEdition.Windows || _coreInstalled,
        edition => edition == PowerShellEdition.Windows ? _windowsAllowsScripts : _coreAllowsScripts);

    private string WindowsProfile => Path.Join(_documents, "WindowsPowerShell", "profile.ps1");
    private string CoreProfile => Path.Join(_documents, "PowerShell", "profile.ps1");

    [Fact]
    public void NewProfilesAreOnlyCreatedWhereTheExecutionPolicyLoadsThem()
    {
        var setup = Service().Register(Path.Join(_stable, "typewhisper.exe"));

        Assert.Equal([CoreProfile], setup.Added);
        Assert.Equal([PowerShellEdition.Windows], setup.BlockedByPolicy);
        Assert.Null(setup.Error);
        Assert.False(File.Exists(WindowsProfile));
        var text = File.ReadAllText(CoreProfile);
        Assert.Contains($"& '{Path.Join(_stable, "typewhisper.exe")}' completions powershell | Out-String | Invoke-Expression", text);
        Assert.StartsWith("# >>> TypeWhisper CLI completion: ", text);
    }

    [Fact]
    public void MissingPowerShell7GetsNoProfile()
    {
        _coreInstalled = false;
        _windowsAllowsScripts = true;

        var setup = Service().Register(Path.Join(_stable, "typewhisper.exe"));

        Assert.Equal([WindowsProfile], setup.Added);
        Assert.Empty(setup.BlockedByPolicy);
        Assert.False(Directory.Exists(Path.Join(_documents, "PowerShell")));
    }

    [Fact]
    public void ExistingProfileIsExtendedEvenWhenItsPolicyRefusesNewOnes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WindowsProfile)!);
        File.WriteAllText(WindowsProfile, "Set-Alias ll Get-ChildItem");

        var setup = Service().Register(Path.Join(_stable, "typewhisper.exe"));

        Assert.Contains(WindowsProfile, setup.Added);
        Assert.Empty(setup.BlockedByPolicy);
        Assert.StartsWith("Set-Alias ll Get-ChildItem\r\n# >>> TypeWhisper CLI completion: ", File.ReadAllText(WindowsProfile));
    }

    [Theory]
    [InlineData(new byte[] { 0x24, 0x78, 0x20, 0x3D, 0x20, 0x27, 0xE4, 0x27, 0x0D, 0x0A })] // ANSI "ä"
    [InlineData(new byte[] { 0x24, 0x78, 0x20, 0x3D, 0x20, 0x27, 0xC3, 0xA4, 0x27, 0x0A })] // UTF-8 "ä" without BOM
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x24, 0x78, 0x0D, 0x0A })] // UTF-8 with BOM
    [InlineData(new byte[] { 0xFF, 0xFE, 0x24, 0x00, 0x78, 0x00, 0x0D, 0x00, 0x0A, 0x00 })] // UTF-16 LE
    public void RegisterAndUnregisterKeepTheRestOfTheProfileByteForByte(byte[] original)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CoreProfile)!);
        File.WriteAllBytes(CoreProfile, original);
        var service = Service();

        service.Register(Path.Join(_stable, "typewhisper.exe"));
        var registered = File.ReadAllBytes(CoreProfile);
        Assert.Equal(original, registered[..original.Length]);
        Assert.Null(service.Unregister(_stable));

        Assert.Equal(original, File.ReadAllBytes(CoreProfile));
    }

    [Fact]
    public void RegisteringAgainReplacesTheBlockAndEachInstallationOwnsItsOwn()
    {
        var service = Service();
        service.Register(Path.Join(_stable, "typewhisper.exe"));
        service.Register(Path.Join(_beta, "typewhisper.exe"));
        service.Register(Path.Join(_stable, "typewhisper.exe"));

        var text = File.ReadAllText(CoreProfile);
        Assert.Equal(2, text.Split("# >>> TypeWhisper CLI completion: ").Length - 1);
        Assert.Equal(2, text.Split(Path.Join(_stable, "typewhisper.exe")).Length - 1); // Test-Path and the call

        service.Unregister(_stable);
        text = File.ReadAllText(CoreProfile);
        Assert.DoesNotContain(_stable + " >>>", text);
        Assert.Contains(_beta + " >>>", text);

        service.Unregister(_beta);
        Assert.False(File.Exists(CoreProfile), "A profile that only held the completion is removed with it.");
    }

    [Fact]
    public void ExecutablePathsAreQuotedForPowerShell()
    {
        var directory = Path.Join(_root, "O'Brien", "Cli");
        Service().Register(Path.Join(directory, "typewhisper.exe"));

        Assert.Contains($"'{Path.Join(_root, "O''Brien", "Cli", "typewhisper.exe")}'", File.ReadAllText(CoreProfile));
        Assert.Null(Service().Unregister(directory));
        Assert.False(File.Exists(CoreProfile));
    }

    [Fact]
    public void NonAsciiPathsStayAsciiInAnsiProfiles()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WindowsProfile)!);
        byte[] original = [0x24, 0x78, 0x20, 0x3D, 0x20, 0x27, 0xE4, 0x27, 0x0D, 0x0A]; // ANSI "ä"
        File.WriteAllBytes(WindowsProfile, original);
        var directory = Path.Join(_root, "Jürgen 太郎", "Cli");
        var executable = Path.Join(directory, "typewhisper.exe");
        var service = Service();

        service.Register(executable);
        var bytes = File.ReadAllBytes(WindowsProfile);
        Assert.Equal(original, bytes[..original.Length]);
        Assert.All(bytes[original.Length..], b => Assert.True(b < 128));
        var block = Encoding.ASCII.GetString(bytes[original.Length..]);
        Assert.Contains($"[char[]]@({string.Join(',', executable.Select(c => (int)c))})", block);
        Assert.Contains("J\\u00FCrgen \\u592A\\u90CE", block);

        service.Register(executable);
        Assert.Equal(bytes, File.ReadAllBytes(WindowsProfile));
        Assert.Null(service.Unregister(directory));
        Assert.Equal(original, File.ReadAllBytes(WindowsProfile));
    }

    [Fact]
    public void RemovalNeverReachesIntoAnotherInstallationsBlock()
    {
        var service = Service();
        service.Register(Path.Join(_stable, "typewhisper.exe"));
        File.AppendAllText(CoreProfile, "Set-Alias ll Get-ChildItem\r\n");
        service.Register(Path.Join(_beta, "typewhisper.exe"));
        // The user deletes the stable block's end marker.
        var edited = File.ReadAllText(CoreProfile);
        var firstEnd = edited.IndexOf("# <<< TypeWhisper CLI completion <<<", StringComparison.Ordinal);
        edited = edited.Remove(firstEnd, "# <<< TypeWhisper CLI completion <<<".Length);
        File.WriteAllText(CoreProfile, edited);

        Assert.Null(service.Unregister(_stable));
        Assert.Equal(edited, File.ReadAllText(CoreProfile));

        // Registering again appends a complete block, which a later removal finds past the edited one.
        service.Register(Path.Join(_stable, "typewhisper.exe"));
        Assert.Null(service.Unregister(_stable));
        Assert.Equal(edited, File.ReadAllText(CoreProfile));
        Assert.Contains("Set-Alias ll Get-ChildItem", edited);
        Assert.Contains(_beta + " >>>", edited);
    }

    [Fact]
    public void EditedBlockWithoutEndMarkerIsLeftAlone()
    {
        var service = Service();
        service.Register(Path.Join(_stable, "typewhisper.exe"));
        var edited = File.ReadAllText(CoreProfile).Replace("# <<< TypeWhisper CLI completion <<<", "");
        File.WriteAllText(CoreProfile, edited);

        Assert.Null(service.Unregister(_stable));
        Assert.Equal(edited, File.ReadAllText(CoreProfile));
    }

    [Fact]
    public void CliInstallationAddsCompletionOnInstallAndRemovesItOnRemove()
    {
        var bundle = Path.Join(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Join(bundle, "typewhisper.exe"), "cli");
        var userPath = "";
        var installation = new CliInstallation(bundle, _stable, () => userPath, value => userPath = value, null, Service());

        var setup = installation.Install();

        Assert.NotNull(setup);
        Assert.Equal([CoreProfile], setup.Added);
        Assert.Contains(Path.Join(_stable, "typewhisper.exe"), File.ReadAllText(CoreProfile, Encoding.UTF8));
        installation.Remove();
        Assert.False(File.Exists(CoreProfile));
    }
}
