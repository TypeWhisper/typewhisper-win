namespace TypeWhisper.PluginSystem.Tests;

public class SpokenFormattingLayoutTests
{
    [Fact]
    public void DictationSettings_ExposeProfileAndGuidedTestAutomationIds()
    {
        var audioSection = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AudioSection.xaml");
        var dialog = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "SpokenFormattingVerificationWindow.xaml");

        Assert.Contains("SpokenFormattingLanguage", audioSection);
        Assert.Contains("SpokenFormattingStrategy", audioSection);
        Assert.Contains("SpokenFormattingVerificationStatus", audioSection);
        Assert.Contains("SpokenFormattingTest", audioSection);
        Assert.Contains("SpokenFormattingTestDialog", dialog);
        Assert.Contains("SpokenFormattingKeepAutomatic", dialog);
        Assert.Contains("SpokenFormattingUseFallback", dialog);
        Assert.Contains("SpokenFormattingNativeWorks", dialog);
        Assert.Contains("SelectedLanguageDisplayName, Mode=OneWay", dialog);
    }

    [Fact]
    public void DictionaryAndWorkflowSurfaces_ExplainEscapesAndAiProviderRequirement()
    {
        var dictionary = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "DictionarySection.xaml");
        var workflows = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "WorkflowsSection.xaml");

        Assert.Contains("DictionaryNewReplacementEscapesHint", dictionary);
        Assert.Contains("DictionaryEditReplacementEscapesHint", dictionary);
        Assert.Contains("WorkflowsProviderWarning", workflows);
        Assert.Contains("WorkflowsOpenIntegrations", workflows);
        Assert.Contains("WorkflowsProviderWarningSummary", workflows);
        Assert.Contains("WorkflowsOpenIntegrationsSummary", workflows);
    }
}
