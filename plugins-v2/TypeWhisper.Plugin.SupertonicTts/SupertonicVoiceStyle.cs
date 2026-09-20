using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TypeWhisper.Plugin.SupertonicTts;

internal sealed record SupertonicVoiceStyle(DenseTensor<float> Ttl, DenseTensor<float> Dp);

internal static class SupertonicVoiceStyleLoader
{
    /// <summary>
    /// Loads persisted state from storage.
    /// </summary>
    public static SupertonicVoiceStyle Load(string voiceStylePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(voiceStylePath));
        var root = doc.RootElement;

        return new SupertonicVoiceStyle(
            LoadTensor(root.GetProperty("style_ttl")),
            LoadTensor(root.GetProperty("style_dp")));
    }

    private static DenseTensor<float> LoadTensor(JsonElement styleElement)
    {
        var dims = styleElement.GetProperty("dims").EnumerateArray()
            .Select(dim => dim.GetInt32())
            .ToArray();
        var values = new List<float>();
        AppendFloats(styleElement.GetProperty("data"), values);
        return new DenseTensor<float>(values.ToArray(), dims);
    }

    private static void AppendFloats(JsonElement element, List<float> values)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                AppendFloats(child, values);
            return;
        }

        values.Add(element.GetSingle());
    }
}
