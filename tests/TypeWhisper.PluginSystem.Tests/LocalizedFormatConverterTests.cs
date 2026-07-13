using System.Globalization;
using System.Windows.Data;
using TypeWhisper.Windows.Converters;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class LocalizedFormatConverterTests
{
    [Fact]
    public void Convert_EnglishUsageTemplate_FormatsCount()
    {
        var converter = new LocalizedFormatConverter();

        var result = converter.Convert(["{0}x used", 2], typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("2x used", result);
    }

    [Fact]
    public void Convert_JapaneseUsageTemplate_FormatsCount()
    {
        var converter = new LocalizedFormatConverter();

        var result = converter.Convert(["{0}回使用", 2], typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("2回使用", result);
    }

    [Fact]
    public void Convert_InvalidTemplate_ReturnsTemplate()
    {
        var converter = new LocalizedFormatConverter();

        var result = converter.Convert(["{0}x {1}", 2], typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("{0}x {1}", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Convert_MissingTemplate_ReturnsEmptyString(string? template)
    {
        var converter = new LocalizedFormatConverter();

        var result = converter.Convert([template!, 2], typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("", result);
    }

    [Fact]
    public void IntegerEqualsConverter_SynchronizesSelectedValue()
    {
        var converter = new IntegerEqualsConverter();

        Assert.True((bool)converter.Convert(2, typeof(bool), "2", CultureInfo.InvariantCulture));
        Assert.False((bool)converter.Convert(1, typeof(bool), "2", CultureInfo.InvariantCulture));
        Assert.Equal(2, converter.ConvertBack(true, typeof(int), "2", CultureInfo.InvariantCulture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(false, typeof(int), "2", CultureInfo.InvariantCulture));
    }
}
