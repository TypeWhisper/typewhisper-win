using System.IO;
using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

public sealed class AudioFileService
{
    private const int TargetSampleRate = 16000;
    private const int TargetChannels = 1;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".wma",
        ".mp4", ".mkv", ".avi", ".mov", ".webm"
    };

    public static bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(ext);
    }

    public async Task<float[]> LoadAudioAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Datei nicht gefunden.", filePath);

        return await Task.Run(() => LoadAudio(filePath), cancellationToken);
    }

    private static float[] LoadAudio(string filePath)
    {
        using var reader = new MediaFoundationReader(filePath);
        using var resampled = new MediaFoundationResampler(reader,
            new WaveFormat(TargetSampleRate, 16, TargetChannels))
        {
            ResamplerQuality = 60
        };

        var samples = new List<float>();
        var buffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            var sampleCount = bytesRead / 2; // 16-bit = 2 bytes per sample
            for (var i = 0; i < sampleCount; i++)
            {
                var sample = BitConverter.ToInt16(buffer, i * 2) / 32768f;
                samples.Add(sample);
            }
        }

        return samples.ToArray();
    }

    public static TimeSpan GetDuration(string filePath)
    {
        using var reader = new MediaFoundationReader(filePath);
        return reader.TotalTime;
    }
}
