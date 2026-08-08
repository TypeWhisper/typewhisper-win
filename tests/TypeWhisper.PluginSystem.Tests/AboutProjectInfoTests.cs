using TypeWhisper.Core;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AboutProjectInfoTests
{
    [Theory]
    [InlineData(
        @"C:\Users\tester\AppData\Local\TypeWhisper\current\TypeWhisper.exe",
        @"C:\fallback\",
        @"C:\Users\tester\AppData\Local\TypeWhisper\current")]
    [InlineData(
        @"D:\Portable\TypeWhisper\TypeWhisper.exe",
        @"C:\fallback\",
        @"D:\Portable\TypeWhisper")]
    [InlineData(null, @"E:\TypeWhisper\app\", @"E:\TypeWhisper\app")]
    public void ResolveInstallationPath_UsesTheRunningExecutableOrBaseDirectory(
        string? processPath,
        string baseDirectory,
        string expected)
    {
        Assert.Equal(
            expected,
            SettingsWindowViewModel.ResolveInstallationPath(processPath, baseDirectory));
    }

    [Fact]
    public void ProjectUrls_PointToThePublicHomepageAndRepository()
    {
        Assert.Equal("https://www.typewhisper.com/", TypeWhisperEnvironment.WebsiteUrl);
        Assert.Equal(
            "https://github.com/TypeWhisper/typewhisper-win",
            TypeWhisperEnvironment.GithubRepoUrl);
    }

    [Fact]
    public void AboutSection_ExposesKeyboardAccessibleProjectLinks()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "InfoSection.xaml");

        Assert.Contains("<Run Text=\"{Binding ProjectWebsiteUrl, Mode=OneWay}\"/>", xaml);
        Assert.Contains("<Run Text=\"{Binding ProjectRepositoryUrl, Mode=OneWay}\"/>", xaml);
        Assert.Contains("<Run Text=\"{Binding InstallationPath, Mode=OneWay}\"/>", xaml);
        Assert.Equal(3, xaml.Split("<Hyperlink").Length - 1);
        Assert.Equal(3, xaml.Split("Focusable=\"True\"").Length - 1);
        Assert.Equal(2, xaml.Split("Command=\"{Binding OpenProjectLinkCommand}\"").Length - 1);
        Assert.Contains("Command=\"{Binding OpenInstallationFolderCommand}\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"AboutWebsiteLink\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"AboutRepositoryLink\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"AboutInstallationLink\"", xaml);
    }
}
