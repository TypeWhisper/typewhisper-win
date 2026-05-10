using System.IO;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class MainWindowLayoutTests
{
    [Fact]
    public void MainWindow_RemovesOuterMarginForEdgeDock()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"OverlayChrome\"", xaml);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"8\"/>", xaml);
        Assert.Contains("Value=\"{x:Static models:IndicatorStyle.EdgeDock}\"", xaml);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"0\"/>", xaml);
    }

    [Fact]
    public void MainWindow_UsesSharpTextRenderingHints()
    {
        var xaml = File.ReadAllText(ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml"));

        Assert.Contains("UseLayoutRounding=\"True\"", xaml);
        Assert.Contains("SnapsToDevicePixels=\"True\"", xaml);
        Assert.Contains("TextOptions.TextFormattingMode=\"Display\"", xaml);
        Assert.Contains("TextOptions.TextRenderingMode=\"ClearType\"", xaml);
        Assert.Contains("RenderOptions.ClearTypeHint=\"Enabled\"", xaml);
    }

    private static string ProjectFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "TypeWhisper.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Join([directory.FullName, .. parts]);
    }
}
