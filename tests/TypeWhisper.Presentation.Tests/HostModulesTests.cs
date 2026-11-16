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
    [InlineData(1, 1)]
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

    private static ModuleManagedPolicyKey Key(string name) => new(name, ModuleManagedPolicyKind.Boolean);

    private sealed class TestHost : IModuleHost
    {
        public string HostVersion => "0.0.1-test";
        public string InterfaceLanguage => "en";
        public IHistoryReading? HistoryReading => null;
        public IManagedPolicyReading? ManagedPolicyReading => null;
        public IPremiumAccessReading? PremiumAccessReading => null;
        public IUserDataLifecycle? UserDataLifecycle => null;
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
