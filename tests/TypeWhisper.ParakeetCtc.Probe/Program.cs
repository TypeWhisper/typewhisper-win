using System.Text.Json;
using NAudio.Wave;
using TypeWhisper.Plugin.ParakeetCtc;
using TypeWhisper.PluginSDK;
using Moq;
using TypeWhisper.PluginHost;

if (args.Length is not (2 or 3)) throw new ArgumentException("Usage: <CTC model directory> <fixture WAV> [published plugin directory]");
var tokens = File.ReadAllLines(Path.Combine(args[0], "tokens.txt")).Select(line => line[..line.LastIndexOf(' ')]).ToArray();
using var reader = new WaveFileReader(args[1]);
if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1 || reader.WaveFormat.BitsPerSample != 16)
    throw new ArgumentException("Expected mono 16-kHz PCM16 WAV.");
var bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
var samples = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768f).ToArray();
using var model = new NemoCtcModel(Path.Combine(args[0], "model.int8.onnx"));
Console.WriteLine(JsonSerializer.Serialize(model.Metadata));
var emission = model.Evaluate(samples, CancellationToken.None);
var pieces = new List<string>(); var last = -1;
for (var t = 0; t < emission.Frames; t++)
{
    var offset = t * emission.VocabularySize; var best = 0;
    for (var v = 1; v < emission.VocabularySize; v++) if (emission.LogProbabilities[offset + v] > emission.LogProbabilities[offset + best]) best = v;
    if (best != last && best < tokens.Length && tokens[best] is not ("<blk>" or "<eps>")) pieces.Add(tokens[best]);
    last = best;
}
Console.WriteLine(string.Concat(pieces).Replace('▁', ' ').Trim());
Console.WriteLine($"Frames={emission.Frames}; vocabulary={emission.VocabularySize}; finite={emission.LogProbabilities.All(float.IsFinite)}");
if (string.Concat(pieces).Replace('▁', ' ').Trim() == "I love you.")
{
    var host = new Mock<IPluginHostServices>();
    var diagnostics = new List<string>();
    host.Setup(h => h.Log(It.IsAny<TypeWhisper.PluginSDK.Models.PluginLogLevel>(), It.IsAny<string>()))
        .Callback<TypeWhisper.PluginSDK.Models.PluginLogLevel, string>((_, message) => diagnostics.Add(message));
    host.Setup(h => h.GetSetting<string>("ModelDirectory")).Returns(args[0]);
    using var plugin = new ParakeetCtcPlugin();
    await plugin.ActivateAsync(host.Object);
    var audioLength = samples.Length / 16000d;
    var correction = await plugin.RescoreAsync(new(Guid.NewGuid(), "I live you.", samples, 16000,
        [new("I live you.", 0, audioLength)], [new("love", .7f)]), CancellationToken.None);
    if (correction.Replacements.Count != 1 || correction.Replacements[0].Term != "love")
        throw new InvalidOperationException("Acoustic positive control failed.");
    Console.WriteLine("Acoustic positive control: " + JsonSerializer.Serialize(correction.Replacements));
    var negative = await plugin.RescoreAsync(new(Guid.NewGuid(), "I love you.", samples, 16000,
        [new("I love you.", 0, audioLength)], [new("live", .7f)]), CancellationToken.None);
    if (negative.Replacements.Count != 0) throw new InvalidOperationException("Acoustic negative control failed.");
    Console.WriteLine("Acoustic negative control: original retained.");
    if (!diagnostics.Any(m => m.Contains("accepted=True")) || !diagnostics.Any(m => m.Contains("accepted=False")))
        throw new InvalidOperationException("Missing acoustic decision diagnostics.");
    var invalid = await plugin.RescoreAsync(new(Guid.NewGuid(), "I live you.", samples, 16000,
        [new("I live you.", -1, audioLength)], [new("love", .7f)]), CancellationToken.None);
    if (invalid.Replacements.Count != 0 || !diagnostics.Any(m => m.Contains("reason=invalid-timing")))
        throw new InvalidOperationException("Missing invalid-timing diagnostic.");
    if (diagnostics.Any(m => m.Contains("I live you.") || m.Contains("I love you.")))
        throw new InvalidOperationException("Transcript leaked into diagnostics.");
    Console.WriteLine("Diagnostics: acceptance, rejection and invalid timing verified without transcript text.");
    if (args.Length == 3)
    {
        await using var lease = await VocabularyPluginLease.LoadAsync(args[2], host.Object, new Version(0, 0, 1));
        if (!lease.Plugin.IsReady) throw new InvalidOperationException("Published package is not ready.");
        var packaged = await lease.Plugin.RescoreAsync(new(Guid.NewGuid(), "I live you.", samples, 16000,
            [new("I live you.", 0, audioLength)], [new("love", .7f)]), CancellationToken.None);
        if (packaged.Replacements.Count != 1 || packaged.Replacements[0].Term != "love")
            throw new InvalidOperationException("Published package acoustic control failed.");
        Console.WriteLine("Published package: loaded, activated and acoustic positive control passed.");
    }
}
