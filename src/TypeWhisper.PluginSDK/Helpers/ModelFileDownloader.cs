using System.Net.Http;
using System.IO;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>Publishes a model file only after its complete response has been written.</summary>
public static class ModelFileDownloader
{
    /// <summary>Downloads one file, validates its length, and removes temporary data on failure or cancellation.</summary>
    public static async Task DownloadAsync(HttpClient client, string url, string destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pending = destination + ".tmp";
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var expected = response.Content.Headers.ContentLength;
            long received = 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                var lastReport = System.Diagnostics.Stopwatch.StartNew();
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    if (expected > 0 && lastReport.ElapsedMilliseconds >= 250)
                    { progress?.Report(Math.Min(0.99, (double)received / expected.Value)); lastReport.Restart(); }
                }
            }
            if (received == 0 || expected.HasValue && received != expected.Value)
                throw new IOException("The model file download was incomplete.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, destination, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }
}
