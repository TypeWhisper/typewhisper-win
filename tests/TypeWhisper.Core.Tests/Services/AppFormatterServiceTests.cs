using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class AppFormatterServiceTests
{
    [Fact]
    public void Format_Outlook_ReturnsPlainText()
    {
        var result = AppFormatterService.Format("Hello from dictation.", "OUTLOOK");

        Assert.Equal("Hello from dictation.", result);
    }

    [Fact]
    public void Format_OutlookBulletText_DoesNotEmitHtmlTags()
    {
        var result = AppFormatterService.Format("- one\n- two", "OUTLOOK");

        Assert.Equal("- one\n- two", result);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
    }

    [Fact]
    public void Format_Thunderbird_ReturnsPlainText()
    {
        var result = AppFormatterService.Format("Email body text.", "Thunderbird");

        Assert.Equal("Email body text.", result);
    }

    [Fact]
    public void Format_MarkdownApp_StillConvertsSpokenBullets()
    {
        var result = AppFormatterService.Format("bullet first\nplain line", "Obsidian");

        Assert.Equal("- first\nplain line", result);
    }
}
