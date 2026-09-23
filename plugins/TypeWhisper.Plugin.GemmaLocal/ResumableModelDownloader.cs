using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TypeWhisper.Plugin.GemmaLocal;

internal static class ResumableModelDownloader
{
    internal static async Task DownloadAsync(HttpClient client, GemmaModelDefinition model, string destination,
        IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var partial = destination + ".download";
        var discard = false;
        try
        {
            await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 81920, useAsync: true))
            {
                if (output.Length > model.SizeBytes) output.SetLength(0);
                var complete = output.Length == model.SizeBytes && await HashMatchesAsync(output, model.Sha256, ct);
                if (!complete)
                {
                    if (output.Length == model.SizeBytes) output.SetLength(0);
                    var offset = output.Length;
                    progress?.Report(Math.Min(0.99, offset / (double)model.SizeBytes));
                    using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
                    request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("The model server returned an unsupported content encoding.");
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range is null || range.Unit != "bytes" || range.From != offset
                            || range.To != model.SizeBytes - 1 || range.Length != model.SizeBytes)
                            throw new IOException("The model server returned an invalid download range. Retry the download.");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        // A server may ignore Range. Validate the full response before replacing the partial file.
                        offset = 0;
                    }
                    else throw new IOException("The model server returned an unexpected response.");
                    if (response.Content.Headers.ContentLength is { } length && length != model.SizeBytes - offset)
                        throw new IOException("The model server returned an unexpected download length.");
                    ct.ThrowIfCancellationRequested();
                    if (offset == 0) output.SetLength(0);
                    output.Position = offset;
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[81920];
                    var reportTimer = System.Diagnostics.Stopwatch.StartNew();
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        if (count > model.SizeBytes - output.Position)
                        {
                            discard = true;
                            throw new IOException("The model download exceeds its expected size.");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        if (reportTimer.ElapsedMilliseconds >= 250)
                        { progress?.Report(Math.Min(0.99, output.Position / (double)model.SizeBytes)); reportTimer.Restart(); }
                    }
                    if (output.Length != model.SizeBytes)
                        throw new IOException("The model download was interrupted. Download again to continue from the saved data.");
                    if (!await HashMatchesAsync(output, model.Sha256, ct))
                    {
                        discard = true;
                        throw new IOException("The model failed its integrity check. Retry to download a fresh copy.");
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            if (discard) File.Delete(partial);
        }
    }

    private static async Task<bool> HashMatchesAsync(FileStream file, string expected, CancellationToken ct)
    {
        await file.FlushAsync(ct);
        file.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
