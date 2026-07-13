using System.Text.Json;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views.Sections;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PremiumSettingsNavigationTests
{
    [Fact]
    public void SettingsNavigation_RegistersPremiumRouteBeforeLicense()
    {
        var navigation = SettingsNavigationCatalog.Build(key => key);
        var systemRoutes = navigation
            .Single(group => group.Group == SettingsGroup.System)
            .Items
            .Select(item => item.Route)
            .ToList();

        Assert.Contains(SettingsRoute.Premium, systemRoutes);
        Assert.True(
            systemRoutes.IndexOf(SettingsRoute.Premium) <
            systemRoutes.IndexOf(SettingsRoute.License));
        Assert.True(typeof(PremiumSection).IsAssignableTo(typeof(System.Windows.Controls.UserControl)));
    }

    [Fact]
    public void PremiumSection_ShowsLatestCorrectionLearningOutcome()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PremiumSection.xaml");

        Assert.Contains("{Binding TargetAppCorrectionLearningLastOutcome}", xaml);
    }

    [Fact]
    public void Diagnostics_IncludeOnlyPrivacySafeCorrectionLearningStatus()
    {
        var recordedAt = new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero);
        var status = new TargetAppCorrectionLearningOutcome(
            TargetAppCorrectionLearningOutcomeKind.AmbiguousEdit,
            recordedAt);

        var json = SettingsWindowViewModel.AddTargetAppCorrectionLearningDiagnostics("{}", status);
        using var document = JsonDocument.Parse(json);
        var learning = document.RootElement.GetProperty("target_app_correction_learning");

        Assert.Equal("ambiguous_edit", learning.GetProperty("outcome").GetString());
        Assert.Equal(recordedAt.ToString("o"), learning.GetProperty("recorded_at_utc").GetString());
        Assert.DoesNotContain("dictated", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replacement", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnostics_RejectNonObjectRoot()
        => Assert.Throws<JsonException>(() =>
            SettingsWindowViewModel.AddTargetAppCorrectionLearningDiagnostics("[]", null));
}
