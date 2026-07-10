namespace TypeWhisper.PluginSystem.Tests;

public sealed class SettingsWindowWizardLifetimeTests
{
    [Fact]
    public void SettingsWindowViewModel_RequestsWizardWithoutCreatingAnUnownedWindow()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "SettingsWindowViewModel.cs");
        var command = TestFile.ExtractBlock(source, "private void OpenSetupWizard()");

        Assert.Contains("public event EventHandler? SetupWizardRequested;", source);
        Assert.Contains("SetupWizardRequested?.Invoke(this, EventArgs.Empty);", command);
        Assert.DoesNotContain("GetRequiredService<WelcomeWindow>", command);
    }

    [Fact]
    public void SettingsWindow_OwnsReusesAndReleasesItsSetupWizard()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "SettingsWindow.xaml.cs");

        Assert.Contains("private WelcomeWindow? _setupWizard;", source);
        Assert.Contains("_viewModel.SetupWizardRequested += OnSetupWizardRequested;", source);
        Assert.Contains("if (_setupWizard is { IsLoaded: true })", source);
        Assert.Contains("_setupWizard.Activate();", source);
        Assert.Contains("wizard.Closed += OnSetupWizardClosed;", source);
        Assert.Contains("_viewModel.SetupWizardRequested -= OnSetupWizardRequested;", source);

        var ownerIndex = source.IndexOf("wizard.Owner = this;", StringComparison.Ordinal);
        var showIndex = source.IndexOf("wizard.Show();", StringComparison.Ordinal);
        Assert.True(ownerIndex >= 0 && ownerIndex < showIndex, "The wizard owner must be set before it is shown.");
    }
}
