using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TypeWhisper.Core.Tests;

// Interface text is translated by Loc.T("English text"); XAML cannot call it. Every text literal
// in a .xaml file is therefore only a design-time placeholder that the code-behind re-assigns after
// InitializeComponent, e.g. `RecorderTitle.Text = Loc.T("Recorder");` or
// `AutomationProperties.SetName(PauseButton, Loc.T("Pause or resume this recording"));`
// (SettingsWindow collects these in LocalizeXamlText()). A literal without that twin ships in
// English for de, ja and zh-Hans without any signal, which is what this test guards against.
//
// When it fails: give the element an x:Name and add the twin assignment to the code-behind's
// constructor or LocalizeXamlText-style method. LocTests.Catalog_TranslatesEveryTextUsedInTheSource
// then asks for the translations of the new Loc.T text. Literals without letters (×, ⠿, digits)
// need no twin.
public sealed partial class XamlLocalizationTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Properties that show text, assigned as `<Name>.<Property> = Loc.T(...)`.
    private static readonly string[] TextProperties =
    [
        "Text", "Content", "Header", "PlaceholderText", "Description", "Title", "OnContent", "OffContent", "Label", "Message",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText",
    ];

    // Attached properties that show or announce text, assigned as `<Owner>.Set<Property>(<Name>, Loc.T(...))`.
    private static readonly string[] AttachedTextProperties = ["AutomationProperties.Name", "AutomationProperties.HelpText", "ToolTipService.ToolTip"];

    [Fact]
    public void EveryXamlTextLiteralHasATranslatedTwinInTheCodeBehind()
    {
        var pairs = new List<string>();
        var missing = new List<string>();

        foreach (var xaml in Repository.SourceFiles(Path.Combine("src", "TypeWhisper.WinUI"), "*.xaml"))
        {
            var document = XDocument.Load(xaml);
            if (document.Root is null || document.Root.Name == Presentation + "Application" || document.Root.Name == Presentation + "ResourceDictionary")
                continue; // Styles and resources have no code-behind that localizes them.

            var file = Path.GetFileName(xaml);
            var codeBehind = CodeBehind(xaml);
            foreach (var element in document.Root.DescendantsAndSelf())
            {
                // The root (Window, UserControl) is `this` in the code-behind and carries no x:Name.
                var name = element == document.Root ? null : element.Attribute(Xaml + "Name")?.Value ?? element.Attribute("Name")?.Value;
                foreach (var attribute in element.Attributes())
                {
                    var property = attribute.Name.LocalName;
                    var text = Literal(attribute.Value);
                    if (text is null || !TextProperties.Contains(property) && !AttachedTextProperties.Contains(property))
                        continue;

                    var location = $"{file}: {name ?? (element == document.Root ? "this" : "<" + element.Name.LocalName + ">")}.{property} = \"{text}\"";
                    pairs.Add(location);
                    if (name is null && element != document.Root)
                        missing.Add(location + " – the element needs an x:Name so the code-behind can assign Loc.T text");
                    else if (!Twins(codeBehind, name, property).Any(Translated))
                        missing.Add(location + " – add " + TwinExample(name, property));
                }
            }
        }

        Assert.Contains("RecorderView.xaml: RecorderTitle.Text = \"Recorder\"", pairs);
        Assert.True(missing.Count == 0,
            $"{missing.Count} XAML text literal(s) have no Loc.T twin in their code-behind, so they would show in English in every language:\n" + string.Join("\n", missing));
    }

    // Returns the user text of an attribute value, or null for bindings, resources and symbol-only text.
    private static string? Literal(string value)
    {
        if (value.StartsWith("{}", StringComparison.Ordinal))
            value = value[2..];
        else if (value.StartsWith('{'))
            return null;
        return value.Any(char.IsLetter) ? value : null;
    }

    // The right-hand sides of every assignment of the property in the code-behind; `name` null means the root element.
    private static IEnumerable<string> Twins(string codeBehind, string? name, string property)
    {
        var dot = property.IndexOf('.');
        var pattern = dot < 0
            ? (name is null ? @"(?m)^\s*(?:this\.)?" : @"(?<![\w.])(?:this\.)?" + Regex.Escape(name) + @"\.") + property + @"\s*=(?!=)([^;]*);"
            : @"(?<!\w)(?:[\w.]+\.)?" + property[..dot] + @"\.Set" + property[(dot + 1)..] + @"\(\s*" + (name is null ? "this" : Regex.Escape(name)) + @"\s*,([^;]*);";
        return Regex.Matches(codeBehind, pattern).Select(match => match.Groups[1].Value);
    }

    // A twin passes Loc.T text or text computed elsewhere (a helper that returns Loc.T text, a profile name, an error);
    // a hardcoded string is as wrong in C# as in XAML.
    private static bool Translated(string expression) =>
        expression.Contains("Loc.T(", StringComparison.Ordinal) || !HardcodedText().IsMatch(expression.Replace("\"\"", ""));

    private static string TwinExample(string? name, string property)
    {
        var dot = property.IndexOf('.');
        return dot < 0
            ? $"{(name is null ? "" : name + ".")}{property} = Loc.T(\"…\");"
            : $"{property[..dot]}.Set{property[(dot + 1)..]}({name ?? "this"}, Loc.T(\"…\"));";
    }

    // The .xaml.cs file and the other partial class files next to it, e.g. SettingsWindow.Integrations.cs.
    private static string CodeBehind(string xaml)
    {
        var prefix = Path.GetFileNameWithoutExtension(xaml) + ".";
        var files = Directory.EnumerateFiles(Path.GetDirectoryName(xaml)!, "*.cs")
            .Where(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal))
            .Select(File.ReadAllText);
        return string.Join("\n", files);
    }

    [GeneratedRegex(""" "(?:[^"\\]|\\.)+" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex HardcodedText();
}
