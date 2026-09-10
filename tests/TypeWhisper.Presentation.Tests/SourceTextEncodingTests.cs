using System.Text;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class SourceTextEncodingTests
{
    [Fact]
    public void ApplicationTextIsUtf8WithoutKnownMojibake()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "TypeWhisper.WinUI"))) root = root.Parent;
        Assert.NotNull(root);
        var decoder = new UTF8Encoding(false, true);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".xaml", ".json", ".resx", ".resw" };
        // UTF-8 decoded as Windows-1252 produces these characteristic sequences.
        string[] corrupt = ["\u00c2\u00b7", "\u00c2\u00a0", "\u00e2\u20ac", "\u00e2\u2020", "\u00e2\u0152", "\u00f0\u0178", "\ufffd",
            "\u00c3\u00a4", "\u00c3\u00b6", "\u00c3\u00bc", "\u00c3\u0178", "\u00c3\u009f"];
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file)) || Path.GetRelativePath(root.FullName, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj")) continue;
            try
            {
                var text = decoder.GetString(File.ReadAllBytes(file));
                if (corrupt.Any(text.Contains)) failures.Add(Path.GetRelativePath(root.FullName, file) + ": corrupted Unicode sequence");
            }
            catch (DecoderFallbackException) { failures.Add(Path.GetRelativePath(root.FullName, file) + ": invalid UTF-8"); }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
