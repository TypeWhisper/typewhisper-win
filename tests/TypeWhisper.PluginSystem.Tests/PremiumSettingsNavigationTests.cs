using System.Text.Json;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views.Sections;
using TypeWhisper.Core.Models;

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

    [Fact]
    public void Diagnostics_IncludePrivacySafeRecoveryState()
    {
        var settings = AppSettings.Default with
        {
            DictationRecoveryRetentionDays = 60,
            DictationRecoveryAutomaticFallbackEnabled = true,
            WorkflowRequestRecoveryEnabled = false
        };

        var json = SettingsWindowViewModel.AddRecoveryDiagnostics("{}", settings, 3);
        using var document = JsonDocument.Parse(json);
        var recovery = document.RootElement.GetProperty("dictation_recovery");

        Assert.Equal(60, recovery.GetProperty("retention_days").GetInt32());
        Assert.Equal(3, recovery.GetProperty("recording_count").GetInt32());
        Assert.True(recovery.GetProperty("automatic_stt_fallback_enabled").GetBoolean());
        Assert.False(recovery.GetProperty("workflow_request_recovery_enabled").GetBoolean());
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoveryNavigation_FollowsFileTranscriptionInCaptureGroup()
    {
        var routes = SettingsNavigationCatalog.Build(key => key)
            .Single(group => group.Group == SettingsGroup.Capture)
            .Items.Select(item => item.Route).ToList();

        var fileTranscriptionIndex = routes.IndexOf(SettingsRoute.FileTranscription);
        Assert.True(fileTranscriptionIndex >= 0);
        Assert.Equal(fileTranscriptionIndex + 1, routes.IndexOf(SettingsRoute.Recovery));
        Assert.True(typeof(RecoverySection).IsAssignableTo(typeof(System.Windows.Controls.UserControl)));
    }
}
