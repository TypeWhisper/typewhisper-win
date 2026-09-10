using NAudio.Wave;

namespace TypeWhisper.WinUI;

internal static class MediaFoundationFileDecoder
{
    private const int MaximumSamples = 16000 * 60 * 60;

    internal static Task<float[]> LoadAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("The selected media file no longer exists.", path);
        try
        {
            using var reader = new MediaFoundationReader(path);
            if (reader.TotalTime > TimeSpan.FromHours(1))
                throw new InvalidOperationException("File transcription supports media up to 60 minutes. Split this file into shorter parts.");
            using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
            var samples = new List<float>();
            var buffer = new byte[16384];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var count = resampler.Read(buffer, 0, buffer.Length);
                ct.ThrowIfCancellationRequested();
                if (count == 0) break;
                if (count % 2 != 0) throw new InvalidOperationException("The media decoder returned incomplete audio samples.");
                if (samples.Count > MaximumSamples - count / 2)
                    throw new InvalidOperationException("File transcription supports media up to 60 minutes. Split this file into shorter parts.");
                for (var index = 0; index < count; index += 2)
                    samples.Add(BitConverter.ToInt16(buffer, index) / 32768f);
            }
            if (samples.Count == 0) throw new InvalidOperationException("This file contains no decodable audio.");
            return samples.ToArray();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        { throw new InvalidOperationException("Windows could not decode this media format. Export the audio as WAV and try again.", ex); }
    }, ct);
}
