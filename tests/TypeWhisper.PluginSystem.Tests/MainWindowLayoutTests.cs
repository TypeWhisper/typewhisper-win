namespace TypeWhisper.PluginSystem.Tests;

public sealed class MainWindowLayoutTests
{
    [Fact]
    public void MainWindow_RemovesOuterMarginForEdgeDock()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml");

        Assert.Contains("x:Name=\"OverlayChrome\"", xaml);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"8\"/>", xaml);
        Assert.Contains("Value=\"{x:Static models:IndicatorStyle.EdgeDock}\"", xaml);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"0\"/>", xaml);
    }

    [Fact]
    public void MainWindow_UsesSharpTextRenderingHints()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml");

        Assert.Contains("UseLayoutRounding=\"True\"", xaml);
        Assert.Contains("SnapsToDevicePixels=\"True\"", xaml);
        Assert.Contains("TextOptions.TextFormattingMode=\"Display\"", xaml);
        Assert.Contains("TextOptions.TextRenderingMode=\"ClearType\"", xaml);
        Assert.Contains("RenderOptions.ClearTypeHint=\"Enabled\"", xaml);
    }
}
