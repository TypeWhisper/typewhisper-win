using System.Text;

namespace TypeWhisper.Plugin.Diarization.Nemotron;

/// <summary>
/// Writes the DirectML variant of the exported graph: every top-level ONNX <c>Gelu</c> node (opset 20) becomes the
/// <c>com.microsoft</c> Gelu, which computes the same exact erf Gelu and which ONNX Runtime's DirectML provider runs
/// on the GPU. ONNX Gelu has no DirectML kernel, so without the change every Gelu ran on the CPU with copies in both
/// directions. Only the graph changes; the weights stay in the shared external data file.
/// </summary>
/// <remarks>
/// The rewrite edits the protobuf wire format directly and copies every other field byte for byte, so its output
/// equals what the <c>onnx</c> Python package writes for the same change.
/// </remarks>
internal static class OnnxGeluRewrite
{
    internal const string MicrosoftDomain = "com.microsoft";
    // ModelProto.graph, ModelProto.opset_import, GraphProto.node, NodeProto.op_type, NodeProto.attribute, NodeProto.domain,
    // OperatorSetIdProto.domain and .version, AttributeProto.name and .s.
    private const int ModelGraph = 7, ModelOpsetImport = 8, GraphNode = 1, NodeOpType = 4, NodeAttribute = 5, NodeDomain = 7;
    private const int OpsetDomain = 1, OpsetVersion = 2, AttributeName = 1, AttributeString = 4;
    private const int Varint = 0, Fixed64 = 1, LengthDelimited = 2, Fixed32 = 5;

    private readonly record struct Field(int Number, int WireType, int Start, int End, int ValueStart, int ValueEnd);

    /// <summary>Returns the rewritten model and how many Gelu nodes changed.</summary>
    /// <exception cref="InvalidDataException">The model is malformed, or a Gelu uses the tanh approximation.</exception>
    internal static byte[] Rewrite(byte[] model, out int replaced)
    {
        var fields = Fields(model, 0, model.Length);
        if (fields.Count(field => field.Number == ModelGraph && field.WireType == LengthDelimited) != 1)
            throw new InvalidDataException("The model has no graph.");
        var hasMicrosoftOpset = fields.Where(field => field.Number == ModelOpsetImport && field.WireType == LengthDelimited)
            .Any(field => Fields(model, field.ValueStart, field.ValueEnd)
                .Any(entry => entry.Number == OpsetDomain && entry.WireType == LengthDelimited && Text(model, entry) == MicrosoftDomain));
        var lastOpset = fields.FindLastIndex(field => field.Number == ModelOpsetImport);
        var count = 0;
        using var output = new MemoryStream(model.Length);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (field.Number == ModelGraph && field.WireType == LengthDelimited)
                WriteLengthDelimited(output, ModelGraph, RewriteGraph(model, field, ref count));
            else Copy(output, model, field);
            // The new opset entry follows the existing ones, as protobuf writes an appended repeated entry.
            if (!hasMicrosoftOpset && (i == lastOpset || (lastOpset < 0 && field.Number == ModelGraph)))
                WriteLengthDelimited(output, ModelOpsetImport, MicrosoftOpset());
        }

        replaced = count;
        return output.ToArray();
    }

    private static byte[] RewriteGraph(byte[] model, Field graph, ref int count)
    {
        using var output = new MemoryStream(graph.ValueEnd - graph.ValueStart);
        foreach (var field in Fields(model, graph.ValueStart, graph.ValueEnd))
        {
            if (field.Number == GraphNode && field.WireType == LengthDelimited && RewriteNode(model, field) is { } node)
            {
                WriteLengthDelimited(output, GraphNode, node);
                count++;
            }
            else Copy(output, model, field);
        }

        return output.ToArray();
    }

    // Null when the node stays as it is.
    private static byte[]? RewriteNode(byte[] model, Field node)
    {
        var fields = Fields(model, node.ValueStart, node.ValueEnd);
        var opType = fields.Where(field => field.Number == NodeOpType && field.WireType == LengthDelimited).Select(field => Text(model, field)).LastOrDefault();
        var domain = fields.Where(field => field.Number == NodeDomain && field.WireType == LengthDelimited).Select(field => Text(model, field)).LastOrDefault() ?? "";
        if (opType != "Gelu" || domain is not ("" or "ai.onnx")) return null;
        foreach (var attribute in fields.Where(field => field.Number == NodeAttribute && field.WireType == LengthDelimited))
        {
            var entries = Fields(model, attribute.ValueStart, attribute.ValueEnd);
            var name = entries.Where(entry => entry.Number == AttributeName && entry.WireType == LengthDelimited).Select(entry => Text(model, entry)).LastOrDefault();
            var value = entries.Where(entry => entry.Number == AttributeString && entry.WireType == LengthDelimited).Select(entry => Text(model, entry)).LastOrDefault();
            if (name == "approximate" && value is not (null or "none"))
                throw new InvalidDataException("A Gelu node uses the tanh approximation, which has no com.microsoft equivalent here.");
        }

        using var output = new MemoryStream(node.ValueEnd - node.ValueStart + 16);
        var domainWritten = false;
        foreach (var field in fields)
        {
            if (field.Number == NodeAttribute) continue;
            if (field.Number == NodeDomain)
            {
                if (!domainWritten) WriteText(output, NodeDomain, MicrosoftDomain);
                domainWritten = true;
                continue;
            }

            if (field.Number > NodeDomain && !domainWritten) { WriteText(output, NodeDomain, MicrosoftDomain); domainWritten = true; }
            Copy(output, model, field);
        }

        if (!domainWritten) WriteText(output, NodeDomain, MicrosoftDomain);
        return output.ToArray();
    }

    private static byte[] MicrosoftOpset()
    {
        using var output = new MemoryStream();
        WriteText(output, OpsetDomain, MicrosoftDomain);
        WriteVarint(output, ((ulong)OpsetVersion << 3) | Varint);
        WriteVarint(output, 1);
        return output.ToArray();
    }

    private static List<Field> Fields(byte[] data, int start, int end)
    {
        var fields = new List<Field>();
        var position = start;
        while (position < end)
        {
            var fieldStart = position;
            var tag = ReadVarint(data, ref position, end);
            var number = tag >> 3;
            var wireType = (int)(tag & 7);
            if (number is 0 or > int.MaxValue) throw new InvalidDataException("The model has an invalid field number.");
            var valueStart = position;
            switch (wireType)
            {
                case Varint: ReadVarint(data, ref position, end); break;
                case Fixed64: position += 8; break;
                case Fixed32: position += 4; break;
                case LengthDelimited:
                    var length = ReadVarint(data, ref position, end);
                    if (length > (ulong)(end - position)) throw new InvalidDataException("The model is truncated.");
                    valueStart = position;
                    position += (int)length;
                    break;
                default: throw new InvalidDataException("The model uses an unsupported protobuf wire type.");
            }

            if (position > end) throw new InvalidDataException("The model is truncated.");
            fields.Add(new((int)number, wireType, fieldStart, position, valueStart, position));
        }

        return fields;
    }

    private static ulong ReadVarint(byte[] data, ref int position, int end)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= end) throw new InvalidDataException("The model is truncated.");
            var current = data[position++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return value;
        }

        throw new InvalidDataException("The model has an invalid varint.");
    }

    private static string Text(byte[] data, Field field) => Encoding.UTF8.GetString(data, field.ValueStart, field.ValueEnd - field.ValueStart);

    private static void Copy(Stream output, byte[] data, Field field) => output.Write(data, field.Start, field.End - field.Start);

    private static void WriteText(Stream output, int number, string text) => WriteLengthDelimited(output, number, Encoding.UTF8.GetBytes(text));

    private static void WriteLengthDelimited(Stream output, int number, byte[] value)
    {
        WriteVarint(output, ((ulong)number << 3) | LengthDelimited);
        WriteVarint(output, (ulong)value.Length);
        output.Write(value);
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }
}
