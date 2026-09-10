using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class WinUIHttpApi
{
    internal event Action? DataChanged;

    private Task<LocalApiResponse?> HandleDataAsync(LocalApiRequest request, CancellationToken ct)
    {
        var response = new LocalApiDataHandler(WinUIProfile.DataPath("dictionary.json"),
            WinUIProfile.DataPath("workflows.json")).Handle(request, ct);
        if (response is { StatusCode: >= 200 and < 300 } && request.Method is "PUT" or "DELETE") DataChanged?.Invoke();
        return Task.FromResult(response);
    }
}
