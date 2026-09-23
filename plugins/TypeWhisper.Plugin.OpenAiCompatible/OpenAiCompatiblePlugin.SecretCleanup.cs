namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed partial class OpenAiCompatiblePlugin
{
    private const string PendingSecretCleanupSetting = "pendingSecretDeletions";

    // Record the secret name before removing its profile/revision. Never store key values here.
    private void QueueSecretCleanup(string secret)
    {
        if (_host is null) return;
        var pending = _host.GetSetting<List<string>>(PendingSecretCleanupSetting) ?? [];
        if (!pending.Contains(secret))
        {
            pending.Add(secret);
            _host.SetSetting(PendingSecretCleanupSetting, pending);
        }
    }

    private async Task RetrySecretCleanupAsync()
    {
        if (_host is null) return;
        var pending = _host.GetSetting<List<string>>(PendingSecretCleanupSetting) ?? [];
        if (pending.Count == 0) return;
        var remaining = new List<string>();
        var active = _profiles.Select(p => SecretKey(p.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var secret in pending)
        {
            // A failed profile write can leave its cleanup marker behind. The live key wins.
            if (active.Contains(secret)) continue;
            try { await _host.DeleteSecretAsync(secret); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                remaining.Add(secret);
                _host.Log(TypeWhisper.PluginSDK.Models.PluginLogLevel.Warning, "Encrypted key cleanup will be retried at the next activation.");
            }
        }
        try { _host.SetSetting(PendingSecretCleanupSetting, remaining); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _host.Log(TypeWhisper.PluginSDK.Models.PluginLogLevel.Warning, "Encrypted key cleanup markers could not be updated; retries remain safe."); }
    }
}
