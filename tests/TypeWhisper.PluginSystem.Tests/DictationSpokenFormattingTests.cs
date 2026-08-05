using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.SpokenFormatting;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class DictationSpokenFormattingTests
{
    private readonly SpokenFormattingService _service = new(new SpokenFormattingRulesLoader());

    [Fact]
    public void CreateSpokenFormatter_NativeReturnsNoPipelineStep()
    {
        var formatter = DictationViewModel.CreateSpokenFormatter(
            _service,
            Context(SpokenFormattingStrategy.NativeOnly));

        Assert.Null(formatter);
    }

    [Fact]
    public void CreateSpokenFormatter_AutomaticOnlyChangesVisibleCommands()
    {
        var formatter = DictationViewModel.CreateSpokenFormatter(
            _service,
            Context(SpokenFormattingStrategy.Automatic));
        const string unchanged = "  Native output  ";

        Assert.NotNull(formatter);
        Assert.Same(unchanged, formatter(unchanged));
        Assert.Equal("Hallo, Welt", formatter("Hallo Komma Welt"));
    }

    [Fact]
    public void CreateSpokenFormatter_FallbackAlwaysNormalizesSpacing()
    {
        var formatter = DictationViewModel.CreateSpokenFormatter(
            _service,
            Context(SpokenFormattingStrategy.FallbackOnly));

        Assert.NotNull(formatter);
        Assert.Equal("Hallo, Welt", formatter("Hallo  ,  Welt"));
    }

    private static ResolvedSpokenFormattingStrategy Context(SpokenFormattingStrategy strategy) =>
        new(
            "de",
            strategy,
            new DictationSpokenFormattingProfile
            {
                EngineId = "engine",
                ModelId = "model",
                LanguageCode = "de",
                StrategyOverrideRaw = strategy.ToRawValue()
            });
}
