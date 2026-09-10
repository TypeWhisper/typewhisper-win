using System.IO;
using System.Net;
using System.Text;

namespace TypeWhisper.Windows.Services;

internal interface IHttpApiResponseTransport
{
    Task SubmitAsync(HttpApiResponse response, CancellationToken cancellationToken);
    void Close();
}

internal sealed class HttpListenerResponseTransport(HttpListenerResponse response)
    : IHttpApiResponseTransport
{
    public async Task SubmitAsync(HttpApiResponse apiResponse, CancellationToken cancellationToken)
    {
        response.StatusCode = apiResponse.StatusCode;
        response.ContentType = apiResponse.ContentType;
        foreach (var (name, value) in apiResponse.Headers)
            response.Headers[name] = value;

        var bytes = Encoding.UTF8.GetBytes(apiResponse.Body);
        response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0)
            await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    public void Close() => response.Close();
}

internal sealed class HttpApiResponseSender(IHttpApiResponseTransport transport)
{
    private int _submissionAttempted;

    public bool SubmissionAttempted => Volatile.Read(ref _submissionAttempted) != 0;

    public async Task<bool> TrySendAsync(
        HttpApiResponse response,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _submissionAttempted, 1) != 0)
            return false;

        try
        {
            await transport.SubmitAsync(response, cancellationToken);
            return true;
        }
        catch (Exception ex) when (IsExpectedResponseTermination(ex))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HttpApi] Response ended before submission completed: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        try
        {
            transport.Close();
        }
        catch (Exception ex) when (IsExpectedResponseTermination(ex))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HttpApi] Response close was interrupted: {ex.Message}");
        }
    }

    internal static bool IsExpectedResponseTermination(Exception exception) =>
        exception is OperationCanceledException
            or IOException
            or HttpListenerException
            or ObjectDisposedException
            or InvalidOperationException;
}
