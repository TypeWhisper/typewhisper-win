using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
namespace TypeWhisper.Plugin.AuthenticatedCli;

public sealed partial class AuthenticatedCliPlugin : IPluginTextSettings, IPluginSettingsActions
{
    private string L(string english, string german) =>
        Localization?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? german : english;

    private IReadOnlyList<PluginModelInfo> GetModels(CliProviderDescriptor descriptor)
    {
        if (descriptor.Kind == CliProviderKind.OpenCode)
        {
            string selected;
            lock (_stateLock) selected = _selectedModels.GetValueOrDefault(descriptor.Key, "default");
            return GetOpenCodeFreeModels().OrderByDescending(m => m.Id == selected)
                .Select((m, i) => new PluginModelInfo(m.Id, m.DisplayName) { IsRecommended = i == 0 }).ToArray();
        }
        lock (_stateLock)
        {
            IReadOnlyList<PluginModelInfo> models = descriptor.Kind switch
            {
                CliProviderKind.Codex => _codexModels,
                CliProviderKind.Claude => [new("sonnet", "Sonnet (CLI alias)"), new("opus", "Opus (CLI alias)"), new("haiku", "Haiku (CLI alias)")],
                _ => []
            };
            return [new("default", GetString("Model.Default")), .. models];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            var fields = new List<PluginTextSetting>();
            foreach (var descriptor in CliProviderDescriptor.All)
            {
                var snapshot = GetSnapshot(descriptor);
                var candidates = _discovery.FindCandidates(descriptor.ExecutableName);
                string selected;
                string model;
                lock (_stateLock)
                {
                    selected = _selectedExecutables.GetValueOrDefault(descriptor.Key) ?? "";
                    model = _selectedModels.GetValueOrDefault(descriptor.Key, "default");
                }
                var status = GetString("State." + snapshot.State);
                if (status.StartsWith("State.", StringComparison.Ordinal)) status = snapshot.State.ToString();
                var automatic = L("Automatic", "Automatisch") + " · " + status;
                if (selected.Length == 0 && snapshot.ExecutablePath is { } path) automatic += " · " + path;
                var choices = new List<PluginSettingChoice> { new("", automatic) };
                choices.AddRange(candidates.Select(path => new PluginSettingChoice(path, path)));
                if (selected.Length > 0 && !candidates.Contains(selected, StringComparer.OrdinalIgnoreCase))
                    choices.Add(new(selected, L("Unavailable: ", "Nicht verfügbar: ") + selected));
                fields.Add(new(descriptor.Key, GetString(descriptor.DisplayKey),
                    L("Automatic detects installed native CLIs, including local installer links and npm binaries.",
                      "Automatisch erkennt installierte native CLIs, einschließlich lokaler Installationsverknüpfungen und npm-Programme."), selected)
                    { Choices = choices });
                if (descriptor.Kind == CliProviderKind.Antigravity) continue;
                var models = GetModels(descriptor);
                if (models.Count == 0) continue;
                var modelChoices = models.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToList();
                if (model != "default" && !models.Any(m => m.Id == model))
                    modelChoices.Add(new(model, L("Unavailable: ", "Nicht verfügbar: ") + model));
                if (descriptor.Kind == CliProviderKind.OpenCode && model == "default") model = models[0].Id;
                fields.Add(new("model." + descriptor.Key, GetString(descriptor.DisplayKey) + L(" model", " – Modell"),
                    descriptor.Kind == CliProviderKind.Claude
                        ? L("CLI aliases; availability depends on your Claude login. Workflows can override this selection.", "CLI-Aliase; die Verfügbarkeit hängt vom Claude-Konto ab. Workflows können diese Auswahl überschreiben.")
                        : L("Models returned by the CLI. Refresh to update the list. Workflows can override this selection.", "Von der CLI gelieferte Modelle. Mit Aktualisieren neu laden. Workflows können diese Auswahl überschreiben."), model)
                    { Choices = modelChoices });
            }
            return fields;
        }
    }

    /// <inheritdoc />
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id.StartsWith("model.", StringComparison.Ordinal))
        {
            var descriptor = CliProviderDescriptor.All.SingleOrDefault(d => "model." + d.Key == id) ?? throw new ArgumentException("Unknown provider.");
            if (!GetModels(descriptor).Any(m => m.Id == value)) throw new ArgumentException("Select a current CLI model.");
            _host?.SetSetting("selectedModel." + descriptor.Key, value);
            lock (_stateLock) _selectedModels[descriptor.Key] = value;
            _host?.NotifyCapabilitiesChanged();
            return;
        }
        var provider = CliProviderDescriptor.All.SingleOrDefault(d => d.Key == id) ?? throw new ArgumentException("Unknown provider.", nameof(id));
        value = value.Trim();
        if (value.Length > 0 && !_discovery.FindCandidates(provider.ExecutableName).Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Select a discovered executable.", nameof(value));
        await SelectExecutableAsync(provider, value.Length == 0 ? null : value, ct);
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
        [new("refresh", L("Refresh CLIs and models", "CLIs und Modelle aktualisieren"),
            L("Check installed programs, existing sign-in sessions and available models.", "Installierte Programme, bestehende Anmeldungen und verfügbare Modelle prüfen."))];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        if (id != "refresh") throw new ArgumentException("Unknown action.", nameof(id));
        await RefreshFromSettingsAsync(ct);
        var codex = GetSnapshot(CliProviderDescriptor.All.Single(d => d.Kind == CliProviderKind.Codex));
        string? catalogError = null;
        if (codex.State == CliAvailabilityState.Ready && codex.ExecutablePath is { } executable)
        {
            var directory = CreateTempDirectory();
            try
            {
                var models = await CodexModelCatalogLoader.LoadAsync(executable, directory, ct);
                ct.ThrowIfCancellationRequested();
                _host?.SetSetting("codexModels.v1", models.ToList());
                lock (_stateLock) _codexModels = models;
                _host?.NotifyCapabilitiesChanged();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                catalogError = L("Codex model refresh failed; the previous list is unchanged.", "Codex-Modelle konnten nicht aktualisiert werden; die bisherige Liste bleibt erhalten.");
                _host?.Log(PluginLogLevel.Warning, "event=codex-model-refresh-failed type=" + ex.GetType().Name);
            }
            finally { await DeleteTempDirectoryAsync(directory); }
        }
        var status = string.Join("\n", CliProviderDescriptor.All.Select(d =>
            GetString(d.DisplayKey) + ": " + GetString("State." + GetSnapshot(d).State)));
        return catalogError is null ? status : status + "\n" + catalogError;
    }
}
