using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Plugin.Diarization.Nemotron;

namespace TypeWhisper.Plugin.Diarization.Nemotron.Tests;

public sealed class OnnxGeluRewriteTests
{
    // Minimal protobuf writer for ModelProto, GraphProto, NodeProto, AttributeProto and OperatorSetIdProto.
    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; }
        bytes.Add((byte)value);
        return [.. bytes];
    }

    private static byte[] Len(int field, byte[] value) => [.. Varint(((ulong)field << 3) | 2), .. Varint((ulong)value.Length), .. value];
    private static byte[] Str(int field, string value) => Len(field, Encoding.UTF8.GetBytes(value));
    private static byte[] Int(int field, ulong value) => [.. Varint((ulong)field << 3), .. Varint(value)];

    private static byte[] Node(string op, string name, string? domain = "", string? approximate = null) =>
    [
        .. Str(1, name + "_in"), .. Str(2, name + "_out"), .. Str(3, name), .. Str(4, op),
        .. approximate is null ? [] : Len(5, [.. Str(1, "approximate"), .. Str(4, approximate), .. Int(20, 3)]),
        .. domain is null ? [] : Str(7, domain)
    ];

    private static byte[] Opset(string domain, ulong version) => [.. Str(1, domain), .. Int(2, version)];

    private static byte[] Model(byte[][] nodes, params byte[][] opsets) =>
    [
        .. Int(1, 10), .. Str(2, "pytorch"),
        .. Len(7, [.. nodes.SelectMany(node => Len(1, node)), .. Str(2, "graph")]),
        .. opsets.SelectMany(opset => Len(8, opset))
    ];

    [Fact]
    public void ExactGeluNodesMoveToTheMicrosoftDomainAndTheOpsetIsAdded()
    {
        var model = Model([Node("Gelu", "a"), Node("Relu", "b"), Node("Gelu", "c", domain: null, approximate: "none"), Node("Gelu", "d", "ai.onnx")],
            Opset("", 20));

        var rewritten = OnnxGeluRewrite.Rewrite(model, out var replaced);

        Assert.Equal(3, replaced);
        var expected = Model([Node("Gelu", "a", "com.microsoft"), Node("Relu", "b"), Node("Gelu", "c", "com.microsoft"), Node("Gelu", "d", "com.microsoft")],
            Opset("", 20), Opset("com.microsoft", 1));
        Assert.Equal(expected, rewritten);
        // A second pass finds nothing left to change.
        Assert.Equal(rewritten, OnnxGeluRewrite.Rewrite(rewritten, out var again));
        Assert.Equal(0, again);
    }

    [Fact]
    public void ExistingMicrosoftOpsetIsKeptAndOtherDomainsAreLeftAlone()
    {
        var model = Model([Node("Gelu", "a", "com.example")], Opset("", 20), Opset("com.microsoft", 1));

        Assert.Equal(model, OnnxGeluRewrite.Rewrite(model, out var replaced));
        Assert.Equal(0, replaced);
    }

    [Fact]
    public void TanhApproximationAndMalformedModelsAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => OnnxGeluRewrite.Rewrite(Model([Node("Gelu", "a", approximate: "tanh")], Opset("", 20)), out _));
        Assert.Throws<InvalidDataException>(() => OnnxGeluRewrite.Rewrite([.. Int(1, 10)], out _));
        var truncated = Model([Node("Gelu", "a")], Opset("", 20));
        Assert.Throws<InvalidDataException>(() => OnnxGeluRewrite.Rewrite(truncated[..^3], out _));
    }

    [NemotronModelFact]
    public void ThePinnedExportBecomesThePinnedDirectMlGraph()
    {
        var rewritten = OnnxGeluRewrite.Rewrite(File.ReadAllBytes(Path.Combine(NemotronModelFactAttribute.ModelDirectory!, "model.onnx")), out var replaced);

        Assert.Equal(31, replaced);
        Assert.Equal(NemotronModelAssets.DirectMlModelSha256, Convert.ToHexStringLower(SHA256.HashData(rewritten)));
    }
}
