using System.IO;
using System.Xml.Linq;

namespace TypeWhisper.PluginSystem.Tests;

public class ScrollViewerTouchStyleTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AppDefinesReusableTouchScrollViewerStyleAndGlobalDefault()
    {
        var app = XDocument.Load(ProjectFile("src", "TypeWhisper.Windows", "App.xaml"));
        var scrollViewerStyles = app
            .Descendants(Presentation + "Style")
            .Where(IsScrollViewerStyle)
            .ToList();

        var touchStyle = Assert.Single(scrollViewerStyles, style => (string?)style.Attribute(Xaml + "Key") == "TouchScrollViewerStyle");
        Assert.Contains(touchStyle.Elements(Presentation + "Setter"), setter => IsSetter(setter, "PanningMode", "VerticalFirst"));
        Assert.Contains(touchStyle.Elements(Presentation + "Setter"), setter => IsSetter(setter, "PanningDeceleration", "15"));

        var implicitStyle = Assert.Single(scrollViewerStyles, style => style.Attribute(Xaml + "Key") is null);
        Assert.Equal("{StaticResource TouchScrollViewerStyle}", (string?)implicitStyle.Attribute("BasedOn"));
    }

    [Fact]
    public void LocalDictionaryScrollViewerStylesInheritTouchPanningStyle()
    {
        var dictionarySection = XDocument.Load(ProjectFile("src", "TypeWhisper.Windows", "Views", "Sections", "DictionarySection.xaml"));
        var localScrollViewerStyles = dictionarySection
            .Descendants(Presentation + "ScrollViewer.Style")
            .SelectMany(styleProperty => styleProperty.Elements(Presentation + "Style"))
            .Where(IsScrollViewerStyle)
            .ToList();

        Assert.Equal(2, localScrollViewerStyles.Count);
        Assert.All(localScrollViewerStyles, style =>
            Assert.Equal("{StaticResource TouchScrollViewerStyle}", (string?)style.Attribute("BasedOn")));
    }

    private static bool IsScrollViewerStyle(XElement style)
    {
        return (string?)style.Attribute("TargetType") == "ScrollViewer";
    }

    private static bool IsSetter(XElement setter, string property, string value)
    {
        return (string?)setter.Attribute("Property") == property
            && (string?)setter.Attribute("Value") == value;
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
