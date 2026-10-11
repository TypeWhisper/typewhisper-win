using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class WorkflowOutputTests
{
    [Fact]
    public void NumberNormalizationMode_DefaultsToInherit()
    {
        var output = new WorkflowOutput();

        Assert.Equal(WorkflowNumberNormalizationMode.Inherit, output.NumberNormalizationMode);
        Assert.Null(output.NumberNormalizationMode.OverrideValue());
    }

    [Theory]
    [InlineData("enabled", WorkflowNumberNormalizationMode.Enabled, true)]
    [InlineData("disabled", WorkflowNumberNormalizationMode.Disabled, false)]
    [InlineData("unknown", WorkflowNumberNormalizationMode.Inherit, null)]
    public void NumberNormalizationMode_ParsesRawValue(
        string rawValue,
        WorkflowNumberNormalizationMode expectedMode,
        bool? expectedOverride)
    {
        var output = new WorkflowOutput { NumberNormalizationModeRaw = rawValue };

        Assert.Equal(expectedMode, output.NumberNormalizationMode);
        Assert.Equal(expectedOverride, output.NumberNormalizationMode.OverrideValue());
    }

    [Fact]
    public void NumberNormalizationModeRaw_UsesMacCompatibleJsonName()
    {
        var output = new WorkflowOutput { NumberNormalizationModeRaw = "disabled" };

        var json = JsonSerializer.Serialize(output);
        var roundTripped = JsonSerializer.Deserialize<WorkflowOutput>(json);

        Assert.Contains("\"numberNormalizationModeRaw\":\"disabled\"", json);
        Assert.Equal(WorkflowNumberNormalizationMode.Disabled, roundTripped?.NumberNormalizationMode);
    }

    [Theory]
    [InlineData(null, false, WorkflowAutoEnterMode.Never)]
    [InlineData(null, true, WorkflowAutoEnterMode.Always)]
    [InlineData("never", true, WorkflowAutoEnterMode.Never)]
    [InlineData("spokenCommand", false, WorkflowAutoEnterMode.SpokenCommand)]
    [InlineData("always", false, WorkflowAutoEnterMode.Always)]
    [InlineData("duringDictation", false, WorkflowAutoEnterMode.DuringDictation)]
    [InlineData("future", true, WorkflowAutoEnterMode.Always)]
    [InlineData("DuringDictation", false, WorkflowAutoEnterMode.Never)]
    public void AutoEnterMode_ParsesMacRawValueWithLegacyFallback(string? rawValue, bool autoEnter, WorkflowAutoEnterMode expected)
    {
        var output = new WorkflowOutput { AutoEnterModeRaw = rawValue, AutoEnter = autoEnter };

        Assert.Equal(expected, output.AutoEnterMode);
        Assert.Equal(rawValue is null or "never" or "spokenCommand" or "always" or "duringDictation", WorkflowAutoEnterModes.IsKnown(rawValue));
    }

    [Theory]
    [InlineData(WorkflowAutoEnterMode.Never, null, false)]
    [InlineData(WorkflowAutoEnterMode.SpokenCommand, "spokenCommand", false)]
    [InlineData(WorkflowAutoEnterMode.Always, "always", true)]
    [InlineData(WorkflowAutoEnterMode.DuringDictation, "duringDictation", false)]
    public void WithAutoEnterMode_WritesTheLegacyFlagLikeMacOS(WorkflowAutoEnterMode mode, string? rawValue, bool autoEnter)
    {
        var output = new WorkflowOutput { AutoEnter = true, AutoEnterModeRaw = "always", TargetActionPluginId = "kept" }.WithAutoEnterMode(mode);

        Assert.Equal(rawValue, output.AutoEnterModeRaw);
        Assert.Equal(autoEnter, output.AutoEnter);
        Assert.Equal(mode, output.AutoEnterMode);
        Assert.Equal("kept", output.TargetActionPluginId);
    }

    [Fact]
    public void AutoEnterModeRaw_ReadsMacWorkflowJson()
    {
        var output = JsonSerializer.Deserialize<WorkflowOutput>("""{"autoEnter":false,"autoEnterModeRaw":"duringDictation"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var json = JsonSerializer.Serialize(output);

        Assert.Equal(WorkflowAutoEnterMode.DuringDictation, output?.AutoEnterMode);
        Assert.Contains("\"autoEnterModeRaw\":\"duringDictation\"", json);
        Assert.DoesNotContain("\"AutoEnterMode\"", json);
    }

    [Theory]
    [InlineData("Send it press enter", "Send it")]
    [InlineData("Send it. Press Return!", "Send it.")]
    [InlineData("  Ship the fix press ENTER.  ", "Ship the fix")]
    [InlineData("First line\nsecond line press enter", "First line\nsecond line")]
    public void SpokenCommand_RemovesTheTrailingCommandAndSubmits(string text, string expected)
    {
        var resolution = WorkflowAutoEnterResolver.Resolve(text, WorkflowAutoEnterMode.SpokenCommand);

        Assert.Equal(expected, resolution.Text);
        Assert.True(resolution.ShouldPressEnter);
    }

    [Theory]
    [InlineData("press enter")]
    [InlineData("Press enter to continue")]
    [InlineData("I pressed enter")]
    [InlineData("Just text")]
    public void SpokenCommand_KeepsTextWithoutATrailingCommand(string text)
    {
        var resolution = WorkflowAutoEnterResolver.Resolve(text, WorkflowAutoEnterMode.SpokenCommand);

        Assert.Equal(text, resolution.Text);
        Assert.False(resolution.ShouldPressEnter);
    }

    [Theory]
    [InlineData(WorkflowAutoEnterMode.Never, true, "text", false)]
    [InlineData(WorkflowAutoEnterMode.Always, false, "text", true)]
    [InlineData(WorkflowAutoEnterMode.DuringDictation, false, "text", false)]
    [InlineData(WorkflowAutoEnterMode.DuringDictation, true, "text", true)]
    [InlineData(WorkflowAutoEnterMode.DuringDictation, true, "  ", false)]
    [InlineData(WorkflowAutoEnterMode.SpokenCommand, true, "text", false)]
    public void Resolve_SubmitsOnlyForTheModeAndRequest(WorkflowAutoEnterMode mode, bool requested, string text, bool expected)
    {
        var resolution = WorkflowAutoEnterResolver.Resolve(text, mode, requested);

        Assert.Equal(text, resolution.Text);
        Assert.Equal(expected, resolution.ShouldPressEnter);
    }

    [Fact]
    public void ShouldPressEnterAfterInsertion_SkipsFailedAlwaysAndEmptyExplicitSubmissions()
    {
        var always = WorkflowAutoEnterResolver.Resolve("text", WorkflowAutoEnterMode.Always);
        var requested = WorkflowAutoEnterResolver.Resolve("text", WorkflowAutoEnterMode.DuringDictation, true);

        Assert.True(WorkflowAutoEnterResolver.ShouldPressEnterAfterInsertion(WorkflowAutoEnterMode.Always, always, "text", false));
        Assert.False(WorkflowAutoEnterResolver.ShouldPressEnterAfterInsertion(WorkflowAutoEnterMode.Always, always, "text", true));
        Assert.False(WorkflowAutoEnterResolver.ShouldPressEnterAfterInsertion(WorkflowAutoEnterMode.Always, always, "", false));
        Assert.True(WorkflowAutoEnterResolver.ShouldPressEnterAfterInsertion(WorkflowAutoEnterMode.DuringDictation, requested, "processed", true));
        Assert.False(WorkflowAutoEnterResolver.ShouldPressEnterAfterInsertion(WorkflowAutoEnterMode.DuringDictation, requested, " ", false));
    }
}
