using Microsoft.UI.Dispatching;
using TypeWhisper.Core.Services;
using TypeWhisper.ModuleKit;
using TypeWhisper.Modules;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// The modules assembly this app was built with: the optional modules or the stub (see TypeWhisper.WinUI.csproj).
internal sealed partial class HostModules
{
    // Known without loading the modules, so views can read it at any time.
    internal static bool StubLinked { get; } = BuildKindOf(typeof(ModuleEntry).Assembly) == StubBuildKind;

    // The validated contributions of the running app. Empty until the modules have loaded, and in the stub build.
    internal static ModuleContributionCatalog ActiveCatalog { get; private set; } = ModuleContributionCatalog.Empty;

    internal static HostModules Load(WinUIModuleHost host)
    {
        var modules = Load(host, typeof(ModuleEntry).Assembly, ModuleEntry.RequiredModuleKitVersion, ModuleEntry.Create, AppDiagnostics.WriteFailure,
            stage => AppDiagnostics.Write(stage));
        host.UseManagedPolicyKeys(modules.Contributions.ManagedPolicyKeys);
        host.UsePanels(modules.Catalog.Panels);
        ActiveCatalog = modules.Catalog;
        return modules;
    }
}

// Created on the UI thread with the window's services, before any module. Navigation is connected by the main
// window afterwards; until then it does nothing.
internal sealed class WinUIModuleHost : IModuleHost
{
    private readonly ModuleManagedPolicyReading _managedPolicy;
    private ModulePanels? _panels;

    internal WinUIModuleHost(HistoryService history, LocalDictationSession dictation, HistorySearchIndexer searchIndexer,
        HistorySearchIndex searchIndex, DispatcherQueue dispatcher)
    {
        var plugins = dictation.PluginRuntime;
        void Post(Action action)
        {
            if (!dispatcher.TryEnqueue(() => action())) AppDiagnostics.Write("modules.dispatch-unavailable");
        }
        HistoryReading = new ModuleHistoryReading(history, history.ResolveAudioPath, id => ShowHistoryRecord?.Invoke(id), Post, AppDiagnostics.WriteFailure);
        _managedPolicy = new ModuleManagedPolicyReading(() => WinUIManagedPolicy.Current, WinUIManagedPolicy.RequestRefresh,
            listener => WinUIManagedPolicy.Changed += listener, listener => WinUIManagedPolicy.Changed -= listener, Post, AppDiagnostics.WriteFailure);
        PremiumAccessReading = new ModulePremiumAccessReading(PremiumView.Access, () => ShowPremiumAccess?.Invoke(), Post, AppDiagnostics.WriteFailure);
        UserData = new ModuleUserDataLifecycle(WinUIProfile.DataPath(ModuleUserDataLifecycle.FolderName), AppDiagnostics.WriteFailure);
        AudioDiarization = new ModuleAudioDiarization(new(
            () => plugins.DiarizationProviders.Select(ToModuleProvider).ToArray(),
            listener => plugins.Changed += listener, listener => plugins.Changed -= listener,
            (id, progress, ct) => plugins.PrepareDiarizationModelsAsync(id, progress, ct),
            (id, request, progress, ct) => plugins.DiarizeAsync(id, request, progress, ct),
            MediaFoundationFileDecoder.LoadAsync), Post, AppDiagnostics.WriteFailure);
        HistorySearching = new ModuleHistorySearching(searchIndexer, searchIndex, history);
        LanguageModelAnswering = new ModuleLanguageModelAnswering(new(
            () => dictation.WorkflowDefaults.Enforced,
            // An enforced pair that is incomplete, invalid or unreadable blocks every request, as for workflows.
            () => dictation.WorkflowDefaults is { Enforced: null, Managed: true }
                ? Loc.T("Your organization's policy could not be read. This operation waits until it is readable.") : null,
            () =>
            {
                try { return dictation.WorkflowDefaults.Read(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppDiagnostics.WriteFailure("modules.language-model.default-unreadable", ex);
                    return null;
                }
            },
            () => plugins.LlmProviders.Select(provider => new ModuleLlmProviderState(provider.SelectionId, provider.Name, provider.PluginId,
                provider.IsLocal, provider.Ready, provider.Models.Select(model => model.Id).ToArray())).ToArray(),
            (id, model) => plugins.LlmRefusal(id, model) ?? plugins.SelectionPackageRefusal(id),
            dictation.ProcessLlmAsync,
            // Running nothing inside the provider's lease loads its remembered local model, as a request would.
            (id, model, ct) => plugins.UseLlmAsync(id, (_, _) => Task.FromResult(true), ct, model),
            () => ShowLanguageModelSettings?.Invoke()), AppDiagnostics.WriteFailure);
    }

    private static ModuleDiarizationProvider ToModuleProvider(PortableDiarizationProvider provider) =>
        new(provider.ProviderId, provider.Name, provider.IsLocal, provider.Ready)
        {
            NeedsModels = !provider.ModelsInstalled, ModelSize = provider.Model?.SizeDescription,
            MinimumSpeakerCount = provider.SpeakerCounts?.Minimum, MaximumSpeakerCount = provider.SpeakerCounts?.Maximum
        };

    public string HostVersion => WindowsApplicationUpdates.CurrentVersion;
    public string InterfaceLanguage => Loc.Language;
    public IHistoryReading HistoryReading { get; }
    public IManagedPolicyReading ManagedPolicyReading => _managedPolicy;
    public IPremiumAccessReading PremiumAccessReading { get; }
    public IUserDataLifecycle UserDataLifecycle => UserData;
    public IAudioDiarization AudioDiarization { get; }
    public IHistorySearching HistorySearching { get; }
    public ILanguageModelAnswering LanguageModelAnswering { get; }

    internal ModuleUserDataLifecycle UserData { get; }
    internal Action<string>? ShowHistoryRecord { get; set; }
    internal Action? ShowPremiumAccess { get; set; }
    internal Action? ShowLanguageModelSettings { get; set; }

    // Panels exist once every module has contributed; requests made while modules contribute do nothing.
    public void ShowPanel(string id) => Panels("show")?.Show(id);
    public void HidePanel(string id) => Panels("hide")?.Hide(id);
    public void TogglePanel(string id) => Panels("toggle")?.Toggle(id);
    public bool IsPanelVisible(string id) => _panels?.IsVisible(id) == true;

    // At shutdown, before the app's windows close.
    internal void ClosePanels() => _panels?.CloseAll();

    // Called once every module has contributed: the declared keys become readable and are validated with the host's.
    internal void UseManagedPolicyKeys(IEnumerable<ModuleManagedPolicyKey> keys)
    {
        var resolution = ModulePolicyKeys.Resolve(keys, (name, reason) => AppDiagnostics.Write(
            name is null ? $"{ModulePolicyKeys.RejectedStage} reason={reason}" : $"{ModulePolicyKeys.RejectedStage} key={name} reason={reason}"));
        _managedPolicy.UseDeclaredNames(resolution.DeclaredNames);
        if (resolution.Definitions.Count > 0) WinUIManagedPolicy.UseAdditionalDefinitions(resolution.Definitions);
    }

    internal void UsePanels(IReadOnlyList<ModulePanel> panels) =>
        _panels = new ModulePanels(panels, ModulePanelWindow.Open, stage => AppDiagnostics.Write(stage), AppDiagnostics.WriteFailure);

    private ModulePanels? Panels(string request)
    {
        if (_panels is null) AppDiagnostics.Write("modules.panel.unavailable request=" + request);
        return _panels;
    }
}
