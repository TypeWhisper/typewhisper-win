using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class LegacyDailyProfileMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-daily-tests-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "legacy");
    private string Destination => Path.Combine(_root, "daily");

    private string Seed()
    {
        var data = Path.Combine(Source, "Data");
        Directory.CreateDirectory(data);
        new DictionaryService(Path.Combine(data, "dictionary.json")).AddEntry(new DictionaryEntry
        { Id = "word", Original = "Wörterbuch", EntryType = DictionaryEntryType.Term });
        new SnippetService(Path.Combine(data, "snippets.json")).AddSnippet(new Snippet
        { Id = "snippet", Trigger = "hello", Replacement = "Grüße" });
        new WorkflowService(Path.Combine(data, "workflows.json")).AddWorkflow(new Workflow
        { Id = "workflow", Name = "Summary", Template = WorkflowTemplate.Summary, Trigger = WorkflowTrigger.Manual() });
        new HistoryService(Path.Combine(data, "history.json")).AddRecord(new TranscriptionRecord
        { Id = "history", RawText = "raw", FinalText = "final", Timestamp = DateTime.UtcNow, AudioFileName = "legacy.wav" });
        File.WriteAllText(Path.Combine(Source, "settings.json"), "private settings");
        Directory.CreateDirectory(Path.Combine(Source, "Plugins"));
        File.WriteAllText(Path.Combine(Source, "Plugins", "old.dll"), "legacy binary");
        return data;
    }

    [Fact]
    public async Task CopiesCategoriesWithoutTouchingLegacyOrImportingDeviceData()
    {
        Seed();
        var originals = Directory.GetFiles(Source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.Equal("Wörterbuch", Assert.Single(new DictionaryService(Path.Combine(Destination, "dictionary.json")).Entries).Original);
        Assert.Equal("Grüße", Assert.Single(new SnippetService(Path.Combine(Destination, "snippets.json")).Snippets).Replacement);
        Assert.Equal("Summary", Assert.Single(new WorkflowService(Path.Combine(Destination, "workflows.json")).Workflows).Name);
        var record = Assert.Single(new HistoryService(Path.Combine(Destination, "history.json")).Records);
        Assert.Equal("final", record.FinalText);
        Assert.Null(record.AudioFileName);
        Assert.False(File.Exists(Path.Combine(Destination, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(Destination, "Plugins")));
        Assert.True(File.Exists(Path.Combine(Destination, LegacyDailyProfileMigration.ReceiptName)));
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("staged")]
    public async Task InterruptedAttemptPublishesNothingAndCanRetry(string stop)
    {
        Seed();
        await Assert.ThrowsAsync<IOException>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination,
            point => { if (point == stop) throw new IOException("simulated interruption"); }));
        Assert.False(Directory.Exists(Destination));
        Assert.Empty(Directory.GetDirectories(_root, ".typewhisper-import-*"));
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
    }

    [Fact]
    public async Task ExistingProfileIsNeverMergedEvenWhenEmpty()
    {
        Seed();
        Directory.CreateDirectory(Destination);
        Assert.False(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.Empty(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task InvalidSourceDoesNotBecomeAnEmptySuccessfulImport()
    {
        var data = Seed();
        File.WriteAllText(Path.Combine(data, "history.json"), "not json");
        await Assert.ThrowsAnyAsync<Exception>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.False(Directory.Exists(Destination));
        Assert.Equal("not json", File.ReadAllText(Path.Combine(data, "history.json")));
    }

    [Fact]
    public async Task ConcurrentDestinationCreationIsPreserved()
    {
        Seed();
        await Assert.ThrowsAsync<IOException>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination, point =>
        {
            if (point != "staged") return;
            Directory.CreateDirectory(Destination);
            File.WriteAllText(Path.Combine(Destination, "new-data"), "keep");
        }));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Destination, "new-data")));
        Assert.Single(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task AbsentSourceCreatesNothing()
    {
        Assert.False(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task CancellationBeforePublicationAllowsRetry()
    {
        Seed();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination,
            point => { if (point == "staged") cancellation.Cancel(); }, cancellation.Token));
        Assert.False(Directory.Exists(Destination));
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
    }

    [Fact]
    public async Task UnknownFieldsAreRejectedInsteadOfSilentlyDiscarded()
    {
        var data = Seed();
        File.WriteAllText(Path.Combine(data, "dictionary.json"), "[{\"Id\":\"word\",\"Original\":\"word\",\"FutureFeature\":true}]");
        await Assert.ThrowsAnyAsync<Exception>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task ImportCreatesNothingInsideTheSource()
    {
        // Settings-only profile: the backup export used to create <legacy>\Data and preview stages inside it.
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "settings.json"), "{}");
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination, prepareProfile: (_, _, _) => Task.CompletedTask));
        Assert.Equal(new[] { Path.Combine(Source, "settings.json") }, Directory.GetFileSystemEntries(Source, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(Destination, ".legacy-data")));

        Directory.Delete(Destination, true);
        Seed();
        var entries = Directory.GetFileSystemEntries(Source, "*", SearchOption.AllDirectories).Order().ToArray();
        var directories = Directory.GetDirectories(Source, "*", SearchOption.AllDirectories).Prepend(Source).ToArray();
        foreach (var directory in directories) DenyChanges(directory, true);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.CreateDirectory(Path.Combine(Source, "Data", ".probe")));
            Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        }
        finally { foreach (var directory in directories) DenyChanges(directory, false); }
        Assert.Equal(entries, Directory.GetFileSystemEntries(Source, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Single(new HistoryService(Path.Combine(Destination, "history.json")).Records);
    }

    [Fact]
    public async Task StaleStagingDirectoriesAreRemovedBeforeImport()
    {
        Seed();
        var stale = Path.Combine(_root, ".typewhisper-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(stale, "PluginData", "com.example.plugin", "Models"));
        File.WriteAllText(Path.Combine(stale, "PluginData", "com.example.plugin", "secrets.dat"), "secret");
        var unrelated = Path.Combine(_root, ".typewhisper-import-keep");
        Directory.CreateDirectory(unrelated);
        var other = Path.Combine(_root, "other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(unrelated));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task EmptyUserDataFallsBackToThePreviousRoot()
    {
        // #318: an interrupted 1.0.4 migration leaves TypeWhisper-UserData with only empty folders.
        var empty = Path.Combine(_root, "TypeWhisper-UserData");
        foreach (var name in new[] { "Data", "Models", "Logs", "Audio", "Plugins", Path.Combine("PluginData", "com.example.plugin") })
            Directory.CreateDirectory(Path.Combine(empty, name));
        Seed();
        Assert.False(LegacyDailyProfileMigration.HasImportableContent(empty));
        Assert.Equal(Source, LegacyDailyProfileMigration.SelectSource(empty, Source));
        Assert.Null(LegacyDailyProfileMigration.SelectSource(empty, Path.Combine(_root, "absent")));
        Assert.False(await LegacyDailyProfileMigration.ImportAsync(empty, Destination, prepareProfile: (_, _, _) => Task.CompletedTask));

        File.WriteAllText(Path.Combine(empty, "PluginData", "com.example.plugin", "settings.json"), "{}");
        Assert.Equal(empty, LegacyDailyProfileMigration.SelectSource(empty, Source));
        File.Delete(Path.Combine(empty, "PluginData", "com.example.plugin", "settings.json"));
        File.WriteAllText(Path.Combine(empty, "Data", "licenses.dat"), "activation");
        Assert.Equal(empty, LegacyDailyProfileMigration.SelectSource(empty, Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedLegacyRootOrAncestorIsFollowed(bool ancestor)
    {
        var real = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(real);
        Seed();
        Directory.Move(Source, Path.Combine(real, "legacy"));
        var linked = Path.Combine(_root, "linked");
        Link(linked, ancestor ? real : Path.Combine(real, "legacy"));
        var root = ancestor ? Path.Combine(linked, "legacy") : linked;
        Assert.Equal(Path.Combine(real, "legacy"), LegacyDailyProfileMigration.ResolveLinkedPath(root), ignoreCase: true);
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(root, Destination));
        Assert.Single(new HistoryService(Path.Combine(Destination, "history.json")).Records);
        Assert.True(File.Exists(Path.Combine(real, "legacy", "Data", "history.json")));
    }

    [Fact]
    public async Task LinkedDestinationParentIsFollowed()
    {
        Seed();
        var real = Path.Combine(_root, "local-data");
        Directory.CreateDirectory(real);
        Link(Path.Combine(_root, "redirected"), real);
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Path.Combine(_root, "redirected", "daily")));
        Assert.True(File.Exists(Path.Combine(real, "daily", LegacyDailyProfileMigration.ReceiptName)));
    }

    [Fact]
    public async Task LinkInsideSourceIsStillRejected()
    {
        Seed();
        var outside = Path.Combine(_root, "outside");
        Directory.Move(Path.Combine(Source, "Data"), outside);
        Link(Path.Combine(Source, "Data"), outside);
        var error = await Assert.ThrowsAsync<LegacyImportLinkException>(() => LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.Contains("linked file or folder", LegacyDailyProfileMigration.DescribeFailure(error));
        Assert.False(Directory.Exists(Destination));
        Assert.Empty(Directory.GetDirectories(_root, ".typewhisper-import-*"));
    }

    [Fact]
    public async Task SkippedImportPublishesAnEmptyProfileWithoutReadingTheSource()
    {
        Seed();
        var before = Directory.GetFiles(Source, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Assert.True(LegacyDailyProfileMigration.SkipImport(Destination));
        Assert.Equal(new[] { Path.Combine(Destination, LegacyDailyProfileMigration.ReceiptName) }, Directory.GetFileSystemEntries(Destination));
        Assert.False(LegacyDailyProfileMigration.WasImported(Destination));
        Assert.False(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.False(LegacyDailyProfileMigration.SkipImport(Destination));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));

        Directory.Delete(Destination, true);
        Assert.True(await LegacyDailyProfileMigration.ImportAsync(Source, Destination));
        Assert.True(LegacyDailyProfileMigration.WasImported(Destination));
    }

    [Fact]
    public void FailuresAreDescribedByCause()
    {
        Assert.Contains("damaged", LegacyDailyProfileMigration.DescribeFailure(new System.Text.Json.JsonException()));
        Assert.Contains("Close the previous", LegacyDailyProfileMigration.DescribeFailure(new UnauthorizedAccessException()));
        Assert.All(new Exception[] { new InvalidDataException(), new IOException(), new InvalidOperationException() },
            error => Assert.Contains("start with a new profile", LegacyDailyProfileMigration.DescribeFailure(error)));
    }

    // Junctions need no symlink privilege on Windows; other platforms always allow symbolic links.
    internal static void Link(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
    }

    private static void DenyChanges(string directory, bool deny)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(directory);
            var acl = info.GetAccessControl();
            var rule = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories |
                System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles, System.Security.AccessControl.AccessControlType.Deny);
            if (deny) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute | (deny ? UnixFileMode.None : UnixFileMode.UserWrite));
    }

    // Unlink first: recursive deletion reports access denied for junctions.
    internal static void DeleteTree(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var link in Directory.GetDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0).ToArray()) Directory.Delete(link);
        Directory.Delete(root, true);
    }

    public void Dispose() => DeleteTree(_root);
}
