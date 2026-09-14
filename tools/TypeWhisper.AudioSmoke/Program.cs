using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args is ["devices"])
{
    using var enumerator = new MMDeviceEnumerator();
    var result = new List<object>();
    foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
    {
        string? defaultId = null;
        try { using var endpoint = enumerator.GetDefaultAudioEndpoint(flow, Role.Console); defaultId = endpoint.ID; }
        catch (System.Runtime.InteropServices.COMException) { }
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            using (endpoint) result.Add(new { flow = flow.ToString(), name = endpoint.FriendlyName,
                id = endpoint.ID, isDefault = endpoint.ID == defaultId });
    }
    Console.WriteLine(JsonSerializer.Serialize(result));
}
else if (args is ["synthesize", var output, var text])
{
    using var synth = new SpeechSynthesizer();
    var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
        ?? throw new InvalidOperationException("An enabled English Windows speech voice is required.");
    synth.SelectVoice(voice.VoiceInfo.Name);
    synth.SetOutputToWaveFile(output, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
    synth.Speak(text);
    synth.SetOutputToNull();
}
else if (args is ["play", var deviceId, var input])
{
    using var enumerator = new MMDeviceEnumerator();
    using var endpoint = enumerator.GetDevice(deviceId);
    if (endpoint.DataFlow != DataFlow.Render) throw new ArgumentException("Choose a render endpoint.");
    using var wave = new AudioFileReader(input);
    if (wave.TotalTime > TimeSpan.FromSeconds(20)) throw new ArgumentException("Smoke-test audio must be at most 20 seconds.");
    using var player = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 100);
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    player.PlaybackStopped += (_, e) => { if (e.Exception is not null) stopped.TrySetException(e.Exception); else stopped.TrySetResult(); };
    player.Init(wave);
    player.Play();
    try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
    finally { player.Stop(); }
}
else throw new ArgumentException("Usage: devices | synthesize <wav> <English text> | play <render endpoint ID> <wav>");
