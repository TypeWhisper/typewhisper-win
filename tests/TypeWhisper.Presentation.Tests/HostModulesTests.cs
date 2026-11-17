using System.Reflection;
using TypeWhisper.ModuleKit;
using TypeWhisper.Modules;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HostModulesTests
{
    private static readonly Assembly StubAssembly = typeof(ModuleEntry).Assembly;

    [Fact]
    public void StubUsesTheModulesAssemblyNameMarksItselfAndContributesNothing()
    {
        Assert.Equal("TypeWhisper.Modules", StubAssembly.GetName().Name);
        Assert.Equal("stub", HostModules.BuildKindOf(StubAssembly));
        Assert.Empty(ModuleEntry.Create());

        var modules = HostModules.Load(new TestHost(), StubAssembly, ModuleEntry.RequiredModuleKitVersion, ModuleEntry.Create, (stage, error) => Assert.Fail(stage));

        Assert.True(modules.IsStub);
        Assert.Empty(modules.Modules);
        Assert.Empty(modules.Contributions.ManagedPolicyKeys);
        Assert.Empty(modules.Catalog.HotkeyActions);
    }

    [Fact]
    public void ContributionsOfEveryModuleAreCombinedAndTheHostIsPassedOn()
    {
        var host = new TestHost();
        var first = new TestModule("first", (seen, contributions) =>
        {
            Assert.Same(host, seen);
            contributions.ManagedPolicyKeys.Add(Key("AllowFirst"));
        });
        var second = new TestModule("second", (_, contributions) => contributions.ManagedPolicyKeys.Add(Key("AllowSecond")));

        var modules = HostModules.Load(host, StubAssembly, ModuleKitVersion.Current, () => [first, second], (stage, error) => Assert.Fail(stage));

        Assert.Equal([first, second], modules.Modules);
        Assert.Equal(["AllowFirst", "AllowSecond"], modules.Contributions.ManagedPolicyKeys.Select(key => key.Name));
    }

    [Fact]
    public void AModuleThatThrowsIsReportedAndLeftOutWithoutItsPartialContributions()
    {
        var failures = new List<(string Stage, Exception Error)>();
        var broken = new TestModule("broken", (_, contributions) =>
        {
            contributions.ManagedPolicyKeys.Add(Key("AllowBroken"));
            throw new InvalidOperationException("module failure");
        });
        var working = new TestModule("working", (_, contributions) => contributions.ManagedPolicyKeys.Add(Key("AllowWorking")));

        var modules = HostModules.Load(new TestHost(), StubAssembly, ModuleKitVersion.Current, () => [broken, working], (stage, error) => failures.Add((stage, error)));

        Assert.Equal([working], modules.Modules);
        Assert.Equal(["AllowWorking"], modules.Contributions.ManagedPolicyKeys.Select(key => key.Name));
        var failure = Assert.Single(failures);
        Assert.Equal("modules.contribute.failed", failure.Stage);
        Assert.IsType<InvalidOperationException>(failure.Error);
    }

    [Fact]
    public void AnEntryPointThatThrowsIsReportedAndLoadsNoModules()
    {
        var failures = new List<string>();

        var modules = HostModules.Load(new TestHost(), StubAssembly, ModuleKitVersion.Current,
            () => throw new TypeLoadException("missing type"), (stage, _) => failures.Add(stage));

        Assert.Empty(modules.Modules);
        Assert.Empty(modules.Contributions.ManagedPolicyKeys);
        Assert.Equal(["modules.create.failed"], failures);
        Assert.True(modules.IsStub);
    }

    [Fact]
    public void AnAssemblyWithoutTheMarkerIsNotTreatedAsTheStub()
    {
        var modules = HostModules.Load(new TestHost(), typeof(HostModulesTests).Assembly, ModuleKitVersion.Current, () => [], (stage, _) => Assert.Fail(stage));

        Assert.Null(modules.BuildKind);
        Assert.False(modules.IsStub);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 9)]
    [InlineData(1, 2)]
    public void ModulesRequiringAnUnsupportedVersionAreNotCreated(int major, int minor)
    {
        var required = new ModuleKitVersion(major, minor);
        var failures = new List<(string Stage, Exception Error)>();
        var created = false;

        var modules = HostModules.Load(new TestHost(), StubAssembly, required,
            () => { created = true; return [new TestModule("any", (_, _) => { })]; }, (stage, error) => failures.Add((stage, error)));

        Assert.False(created);
        Assert.Empty(modules.Modules);
        Assert.Empty(modules.Contributions.ManagedPolicyKeys);
        var failure = Assert.Single(failures);
        Assert.Equal($"modules.version.unsupported required={required} supported={ModuleKitVersion.Current}", failure.Stage);
        var error = Assert.IsType<NotSupportedException>(failure.Error);
        Assert.Contains(required.ToString(), error.Message);
        Assert.Contains(ModuleKitVersion.Current.ToString(), error.Message);
    }

    [Fact]
    public void ModulesWrittenAgainstAnEarlierMinorVersionAreLoaded()
    {
        var module = new TestModule("earlier", (_, _) => { });

        var modules = HostModules.Load(new TestHost(), StubAssembly, new ModuleKitVersion(ModuleKitVersion.Current.Major, 0), () => [module],
            (stage, _) => Assert.Fail(stage));

        Assert.Equal([module], modules.Modules);
        Assert.Equal(ModuleKitVersion.Current, ModuleEntry.RequiredModuleKitVersion);
    }

    [Fact]
    public void WithDiagnosticsEachModuleSeesTheSharedHostWithItsOwnDiagnostics()
    {
        var host = new TestHost { SpeechInput = new NoSpeechInput() };
        var written = new List<string>();
        var failures = new List<(string Stage, Exception Error)>();
        var seen = new Dictionary<string, IModuleHost>();
        var first = new TestModule("First", (moduleHost, _) => seen["first"] = moduleHost);
        var second = new TestModule("second-module", (moduleHost, _) => seen["second"] = moduleHost);
        var unnamed = new TestModule("not valid!", (moduleHost, _) => seen["unnamed"] = moduleHost);

        var modules = HostModules.Load(host, StubAssembly, ModuleKitVersion.Current, () => [first, second, unnamed], (stage, error) => Assert.Fail(stage),
            diagnosticsFor: id => ModuleDiagnostics.For(id, written.Add, (stage, error) => failures.Add((stage, error))));

        Assert.Equal([first, second, unnamed], modules.Modules);
        Assert.Null(seen["unnamed"].Diagnostics);
        seen["first"].Diagnostics!.Write("loaded");
        seen["second"].Diagnostics!.Write("index.ready");
        var error = new InvalidOperationException("secret text");
        seen["second"].Diagnostics!.WriteFailure("index.failed", error);
        Assert.Equal(["module.first.loaded", "module.second-module.index.ready"], written);
        Assert.Equal(("module.second-module.index.failed", (Exception)error), Assert.Single(failures));

        // Everything else is the shared host's.
        var scoped = seen["first"];
        Assert.Equal(host.HostVersion, scoped.HostVersion);
        Assert.Same(host.WhenStarted, scoped.WhenStarted);
        Assert.Same(host.SpeechInput, scoped.SpeechInput);
        scoped.ShowPanel("panel");
        scoped.ShowSettingsSection("section");
        Assert.Equal(["show-panel panel", "show-settings-section section"], host.Calls);
    }

    [Fact]
    public void ModuleDiagnosticsNameEventsAfterTheModuleAndKeepInvalidStagesOut()
    {
        var written = new List<string>();
        var diagnostics = ModuleDiagnostics.For("Example", written.Add, (_, _) => Assert.Fail("no failure"))!;

        foreach (var stage in new[] { "started", "sync.step-2", "Has Spaces", "upper.Case", "a..b", "-a", "a.", "line\nbreak", "ümlaut", "", null!,
            new string('a', ModuleDiagnostics.MaximumStageLength), new string('a', ModuleDiagnostics.MaximumStageLength + 1) })
            diagnostics.Write(stage);

        Assert.Equal(["module.example.started", "module.example.sync.step-2", "module.example.invalid-stage", "module.example.invalid-stage",
            "module.example.invalid-stage", "module.example.invalid-stage", "module.example.invalid-stage", "module.example.invalid-stage",
            "module.example.invalid-stage", "module.example.invalid-stage", "module.example.invalid-stage",
            "module.example." + new string('a', ModuleDiagnostics.MaximumStageLength), "module.example.invalid-stage"], written);
        // The longest event a module can cause still fits the log's 80 characters per event name.
        Assert.True(ModuleDiagnostics.EventPrefix.Length + ModuleDiagnostics.MaximumMarkerLength + 1 + ModuleDiagnostics.MaximumStageLength <= 80);
        Assert.Throws<ArgumentNullException>(() => diagnostics.WriteFailure("failed", null!));
    }

    [Theory]
    [InlineData("example", "example")]
    [InlineData("Example-Two", "example-two")]
    [InlineData("module2", "module2")]
    [InlineData("with.dot", null)]
    [InlineData("with_underscore", null)]
    [InlineData("-leading", null)]
    [InlineData("double--hyphen", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("abcdefghijklmnopqrstuvwxy", null)]
    public void ModuleIdentifiersBecomeOnePartOfAnEventName(string? id, string? marker) => Assert.Equal(marker, ModuleDiagnostics.Marker(id));

    [Fact]
    public void ANewHostMemberHasAHarmlessDefault()
    {
        IModuleHost host = new MinimalHost();

        Assert.Null(host.Diagnostics);
        Assert.Null(host.SpeechInput);
        Assert.True(host.WhenStarted.IsCompletedSuccessfully);
        host.ShowSettingsSection("anything");
    }

    private static ModuleManagedPolicyKey Key(string name) => new(name, ModuleManagedPolicyKind.Boolean);

    private sealed class TestHost : IModuleHost
    {
        private readonly TaskCompletionSource _started = new();
        public List<string> Calls { get; } = [];
        public string HostVersion => "0.0.1-test";
        public string InterfaceLanguage => "en";
        public IHistoryReading? HistoryReading => null;
        public IManagedPolicyReading? ManagedPolicyReading => null;
        public IPremiumAccessReading? PremiumAccessReading => null;
        public IUserDataLifecycle? UserDataLifecycle => null;
        public IAudioDiarization? AudioDiarization => null;
        public IHistorySearching? HistorySearching => null;
        public ILanguageModelAnswering? LanguageModelAnswering => null;
        public IModuleDiagnostics? Diagnostics => null;
        public ISpeechInput? SpeechInput { get; init; }
        public Task WhenStarted => _started.Task;
        public void ShowSettingsSection(string sectionId) => Calls.Add("show-settings-section " + sectionId);
        public void ShowPanel(string id) => Calls.Add("show-panel " + id);
        public void HidePanel(string id) => Calls.Add("hide-panel " + id);
        public void TogglePanel(string id) => Calls.Add("toggle-panel " + id);
        public bool IsPanelVisible(string id) => false;
    }

    private sealed class NoSpeechInput : ISpeechInput
    {
        public Task<ModuleSpeechInputResult> ListenAsync(ModuleSpeechInputOptions? options = null, IProgress<ModuleSpeechInputState>? progress = null,
            CancellationToken cancellationToken = default) => throw new ModuleSpeechInputException(ModuleSpeechInputErrorKind.NotSetUp);
        public void Stop() { }
    }

    // Implements only the members without a default, as a host written before the newer members would.
    private sealed class MinimalHost : IModuleHost
    {
        public string HostVersion => "0.0.1-test";
        public string InterfaceLanguage => "en";
        public IHistoryReading? HistoryReading => null;
        public IManagedPolicyReading? ManagedPolicyReading => null;
        public IPremiumAccessReading? PremiumAccessReading => null;
        public IUserDataLifecycle? UserDataLifecycle => null;
        public IAudioDiarization? AudioDiarization => null;
        public void ShowPanel(string id) { }
        public void HidePanel(string id) { }
        public void TogglePanel(string id) { }
        public bool IsPanelVisible(string id) => false;
    }

    private sealed class TestModule(string id, Action<IModuleHost, ModuleContributions> contribute) : IHostModule
    {
        public string Id => id;
        public void Contribute(IModuleHost host, ModuleContributions contributions) => contribute(host, contributions);
    }
}
