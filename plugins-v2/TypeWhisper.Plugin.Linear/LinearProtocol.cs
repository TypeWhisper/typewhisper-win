using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Linear;

public sealed partial class LinearPlugin
{

    /// <inheritdoc />
    public string ActionId => "create-linear-issue";
    /// <inheritdoc />
    public string ActionName => Connection.L("Create Linear issue", "Linear-Issue erstellen");
    /// <inheritdoc />
    public string? ActionIcon => "plus.circle";
    private async Task<JsonDocument> GraphQlAsync(string query,object variables,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://api.linear.app/graphql");
        // Personal API keys use the raw Authorization value.
        var key=Connection.RequireKey(); request.Headers.TryAddWithoutValidation("Authorization",key);
        request.Content=ProviderConnection.Json(new { query,variables });
        var document=await Connection.ReadAsync(request,ct);
        if(document.RootElement.TryGetProperty("errors",out var errors) && errors.ValueKind==JsonValueKind.Array && errors.GetArrayLength()>0)
        { document.Dispose(); throw new PluginRequestException("Linear rejected the request. Check your API key and team/project permissions.",PluginRequestFailureKind.Permission); }
        return document;
    }
    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(string input,ActionContext context,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!IsConfigured || string.IsNullOrWhiteSpace(Connection.Get("teamId"))) return new(false,Connection.L("Save an API key and select a team first.", "Speichere zuerst einen API-Schlüssel und wähle ein Team."));
        if(string.IsNullOrWhiteSpace(input)) return new(false,Connection.L("Issue text is empty.", "Der Issue-Text ist leer."));
        var title=input.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if(title.Length>100) title=title[..100];
        if(char.IsHighSurrogate(title[^1])) title=title[..^1];
        var issue=new Dictionary<string,object>{["teamId"]=Connection.Get("teamId"),["title"]=title,["description"]=input};
        if(Connection.Get("projectId") is { Length: >0 } project) issue["projectId"]=project;
        using var document=await GraphQlAsync("mutation CreateIssue($input: IssueCreateInput!) { issueCreate(input: $input) { success issue { url } } }",new { input=issue },ct);
        var payload=ProviderConnection.Required(ProviderConnection.Required(document.RootElement,"data",JsonValueKind.Object),"issueCreate",JsonValueKind.Object);
        if(!payload.TryGetProperty("success",out var success) || success.ValueKind!=JsonValueKind.True) return new(false,"Linear did not create the issue.");
        var url=ProviderConnection.RequiredText(ProviderConnection.Required(payload,"issue",JsonValueKind.Object),"url");
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.Host!="linear.app" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) throw ProviderConnection.InvalidResponse();
        return new(true,Connection.L("Linear issue created.", "Linear-Issue erstellt."),url);
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var result=await GraphQlAsync("query { viewer { id } }",new { },ct);
        _ = ProviderConnection.RequiredText(ProviderConnection.Required(ProviderConnection.Required(result.RootElement,"data",JsonValueKind.Object),"viewer",JsonValueKind.Object),"id");
    }

}
