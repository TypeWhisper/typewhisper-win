using System.IO;
using System.Xml.Linq;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ShellTranscriptionServiceTests
{
    [Fact]
    public void ParseFilePaths_ReadsAndDeduplicatesPathsFollowingTheShellSwitch()
    {
        var first = Path.Join(Path.GetTempPath(), "TypeWhisper shell test", "first clip.webm");
        var second = Path.Join(Path.GetTempPath(), "TypeWhisper shell test", "second.mp3");

        var paths = ShellTranscriptionService.ParseFilePaths([
            "--minimized",
            ShellTranscriptionService.CommandLineSwitch,
            first,
            second,
            first,
            "--another-option"
        ]);

        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], paths);
    }

    [Fact]
    public async Task RequestInbox_PreservesConcurrentRequestsUntilTheyAreDrained()
    {
        var requestDirectory = CreateTemporaryDirectory();
        try
        {
            var results = await Task.WhenAll(
                Task.Run(() => ShellTranscriptionService.Enqueue([@"C:\media\first.webm"], requestDirectory)),
                Task.Run(() => ShellTranscriptionService.Enqueue(
                    [@"C:\media\second.mp3", @"C:\media\first.webm"],
                    requestDirectory)));

            Assert.All(results, result => Assert.True(result));
            Assert.True(ShellTranscriptionService.HasPendingRequests(requestDirectory));

            var paths = ShellTranscriptionService.Drain(requestDirectory);
            string[] expectedPaths = [@"C:\media\first.webm", @"C:\media\second.mp3"];

            Assert.Equal(
                expectedPaths.Order(StringComparer.OrdinalIgnoreCase),
                paths.Order(StringComparer.OrdinalIgnoreCase));
            Assert.False(ShellTranscriptionService.HasPendingRequests(requestDirectory));
        }
        finally
        {
            Directory.Delete(requestDirectory, recursive: true);
        }
    }

    [Fact]
    public void RequestInbox_DiscardsMalformedRequestsWithoutBlockingValidOnes()
    {
        var requestDirectory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Join(requestDirectory, "000-invalid.json"), "not json");
            Assert.True(ShellTranscriptionService.Enqueue([@"C:\media\clip.wav"], requestDirectory));

            Assert.Equal([@"C:\media\clip.wav"], ShellTranscriptionService.Drain(requestDirectory));
            Assert.Empty(Directory.EnumerateFiles(requestDirectory, "*.json"));
        }
        finally
        {
            Directory.Delete(requestDirectory, recursive: true);
        }
    }

    [Fact]
    public void ExplorerCommand_QuotesTheExecutableAndSelectedFile()
    {
        Assert.Equal(
            "\"C:\\Program Files\\TypeWhisper\\TypeWhisper.exe\" --transcribe-file \"%1\"",
            ShellTranscriptionService.BuildCommand(@"C:\Program Files\TypeWhisper\TypeWhisper.exe"));
    }

    [Fact]
    public void StoreManifest_RegistersTheTranscriptionVerbForEverySupportedMediaType()
    {
        var manifestPath = TestFile.ProjectFile(
            "src",
            "TypeWhisper.Windows.StorePackage",
            "Package.appxmanifest.template");
        var document = XDocument.Load(manifestPath);
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        XNamespace uap2 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/2";
        XNamespace uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";

        var association = Assert.Single(document.Descendants(uap3 + "FileTypeAssociation"));
        Assert.Equal("--transcribe-file \"%1\"", (string?)association.Attribute("Parameters"));
        Assert.Equal("Player", (string?)association.Attribute("MultiSelectModel"));
        Assert.Equal(
            AudioFileService.SupportedFileExtensions.Order(StringComparer.OrdinalIgnoreCase),
            association.Descendants(uap + "FileType")
                .Select(element => element.Value)
                .Order(StringComparer.OrdinalIgnoreCase));

        var verb = Assert.Single(association.Descendants(uap2 + "SupportedVerbs").Elements(uap3 + "Verb"));
        Assert.Equal("transcribe", (string?)verb.Attribute("Id"));
        Assert.Equal("--transcribe-file \"%1\"", (string?)verb.Attribute("Parameters"));
        Assert.Equal("Player", (string?)verb.Attribute("MultiSelectModel"));
        Assert.Equal("Transcribe with TypeWhisper", verb.Value);
    }

    [Fact]
    public void StoreManifest_RegistersShareTargetForEverySupportedMediaType()
    {
        var manifestPath = TestFile.ProjectFile(
            "src",
            "TypeWhisper.Windows.StorePackage",
            "Package.appxmanifest.template");
        var document = XDocument.Load(manifestPath);
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

        var extension = Assert.Single(
            document.Descendants(uap + "Extension"),
            element => (string?)element.Attribute("Category") == "windows.shareTarget");
        var shareTarget = Assert.Single(extension.Elements(uap + "ShareTarget"));

        Assert.Equal("Transcribe audio and video with TypeWhisper", (string?)shareTarget.Attribute("Description"));
        Assert.Equal(
            AudioFileService.SupportedFileExtensions.Order(StringComparer.OrdinalIgnoreCase),
            shareTarget.Descendants(uap + "FileType")
                .Select(element => element.Value)
                .Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["StorageItems"], shareTarget.Elements(uap + "DataFormat").Select(element => element.Value));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Join(Path.GetTempPath(), $"TypeWhisper-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
