using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.CloudflareAsr;

public sealed partial class CloudflareAsrPlugin : IPluginSettingsActions
{
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => Connection.Host is null ? [] : [
        new("connect-cloudflare", Connection.L("Connect with Cloudflare", "Mit Cloudflare verbinden"),
            Connection.L("Sign in in your browser and grant TypeWhisper access to Workers AI.",
                "Im Browser anmelden und TypeWhisper Zugriff auf Workers AI gewähren.")) { Section = PluginSettingsSection.Connection },
        .. Connection.UsesOAuth ? new[] { new PluginSettingsAction("disconnect-cloudflare",
            Connection.L("Disconnect Cloudflare", "Cloudflare trennen"),
            Connection.L("Remove the saved sign-in from this device. You can also revoke access in Cloudflare Connected Applications.",
                "Die gespeicherte Anmeldung von diesem Gerät entfernen. Du kannst den Zugriff auch in Cloudflare unter Connected Applications widerrufen.")) { Section = PluginSettingsSection.Connection } } : []
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == "disconnect-cloudflare")
        {
            await Connection.SetKeyAsync("");
            return Connection.L("Cloudflare disconnected on this device.", "Cloudflare auf diesem Gerät getrennt.");
        }
        if (id != "connect-cloudflare") throw new ArgumentException("Unknown action.", nameof(id));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var oauth = new CloudflareOAuth(Connection.Http);
        CloudflareTokens tokens;
        try { tokens = await oauth.SignInAsync(timeout.Token); }
        catch (CloudflareSignInException ex) { return ex.Message; }
        var accounts = await oauth.AccountsAsync(tokens.AccessToken, timeout.Token);
        await Connection.SaveOAuthAsync(tokens, accounts, timeout.Token);
        return IsConfigured ? Connection.L("Connected to Cloudflare.", "Mit Cloudflare verbunden.")
            : Connection.L("Signed in. Choose a Cloudflare account and save settings.", "Angemeldet. Wähle ein Cloudflare-Konto und speichere die Einstellungen.");
    }
}
