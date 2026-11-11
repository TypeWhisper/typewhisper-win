using System.Security.Cryptography;
using System.Text.Json;

namespace TypeWhisper.DiarizationBenchmarks;

internal sealed record SetItem(string Id, string Group, string Audio, string Sha256, long Bytes, double Seconds, int Speakers);

internal sealed record BenchmarkSet(string Id, IReadOnlyList<SetItem> Items)
{
    public static BenchmarkSet Load(string manifest)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var root = document.RootElement;
        var items = root.GetProperty("items").EnumerateArray().Select(i => new SetItem(
            i.GetProperty("id").GetString()!,
            i.GetProperty("group").GetString()!,
            i.GetProperty("audio").GetString()!,
            i.GetProperty("sha256").GetString()!,
            i.GetProperty("bytes").GetInt64(),
            i.GetProperty("seconds").GetDouble(),
            i.GetProperty("speakers").GetInt32())).ToList();
        // The manifest is downloaded; its audio paths must stay inside the audio directory.
        if (items.FirstOrDefault(i => Path.IsPathRooted(i.Audio) || i.Audio.Split('/', '\\').Contains("..")) is { } outside)
        {
            throw new InvalidDataException($"{manifest}: audio path {outside.Audio} leaves the audio directory");
        }

        return new BenchmarkSet(root.GetProperty("id").GetString()!, items);
    }

    /// <summary>Files that are missing or differ from the manifest's size and SHA-256.</summary>
    public List<string> Problems(string audioRoot)
    {
        var problems = new List<string>();
        foreach (var item in Items)
        {
            var path = Path.Combine(audioRoot, item.Audio);
            if (!File.Exists(path))
            {
                problems.Add($"missing {item.Audio}");
                continue;
            }

            if (new FileInfo(path).Length != item.Bytes)
            {
                problems.Add($"size of {item.Audio}");
                continue;
            }

            using var stream = File.OpenRead(path);
            if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(item.Sha256, StringComparison.Ordinal))
            {
                problems.Add($"sha256 of {item.Audio}");
            }
        }

        return problems;
    }
}
