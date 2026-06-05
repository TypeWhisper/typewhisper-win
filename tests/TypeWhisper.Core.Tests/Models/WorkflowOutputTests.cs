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
}
