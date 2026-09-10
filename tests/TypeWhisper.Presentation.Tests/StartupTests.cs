using System.Text.Json;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class StartupTests
{
    private static string Executable => Path.Combine(Path.GetTempPath(), "published output", "TypeWhisper.WinUI.exe");

    [Theory]
    [InlineData(false, "", true)]
    [InlineData(false, "--minimized", false)]
    [InlineData(false, "--MINIMIZED", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "--settings", true)]
    [InlineData(false, "--minimized --files", true)]
    [InlineData(false, "\"C:/some --minimized folder/app.exe\"", true)]
    public void LaunchPolicyDoesNotRaiseExistingWindowForSilentStartup(bool startup, string commandLine, bool visible)
    {
        var decision = StartupLaunchPolicy.EvaluateCommandLine(commandLine, startup);
        Assert.Equal(visible, decision.ShowWindow);
        Assert.Equal(visible, decision.NotifyExisting);
    }

    [Fact]
    public void MissingRegistrationStaysOffUntilExplicitChoiceAndOnlyChangesItsOwnIdentity()
    {
        var backend = new Backend(); backend.Values["TypeWhisper"] = "installed product command";
        var registration = new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, Executable);
        Assert.False(registration.Read().IsEnabled); Assert.Equal(0, backend.Writes);
        var enabled = registration.SetEnabled(true);
        Assert.True(enabled.IsEnabled); Assert.Null(enabled.Error);
        Assert.Equal("\"" + Executable + "\" --minimized", backend.Values[StartupPublication.DevelopmentIdentity]);
        Assert.False(registration.SetEnabled(false).IsEnabled);
        Assert.Equal("installed product command", backend.Values["TypeWhisper"]);
        Assert.False(backend.Values.ContainsKey(StartupPublication.DevelopmentIdentity));
    }

    [Fact]
    public void TestProfileBlockPreventsEveryBackendReadAndWrite()
    {
        var backend = new Backend { FailEveryAccess = true };
        var registration = new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, null, "Test profile");
        Assert.False(registration.Read().CanChange);
        Assert.False(registration.SetEnabled(true).CanChange);
        Assert.False(registration.SetEnabled(false).CanChange);
        Assert.Equal(0, backend.Accesses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrIgnoredWriteReturnsActualRegistrationInsteadOfRequestedChoice(bool throwOnWrite)
    {
        var backend = new Backend { IgnoreWrites = !throwOnWrite, ThrowOnWrite = throwOnWrite };
        var registration = new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, Executable);
        var result = registration.SetEnabled(true);
        Assert.False(result.IsEnabled); Assert.NotNull(result.Error);
        Assert.Empty(backend.Values);
    }

    [Fact]
    public void FailedDisableReportsThatRegistrationIsStillEnabled()
    {
        var backend = new Backend();
        var registration = new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, Executable);
        Assert.True(registration.SetEnabled(true).IsEnabled);
        backend.ThrowOnWrite = true;
        var result = registration.SetEnabled(false);
        Assert.True(result.IsEnabled); Assert.NotNull(result.Error);
    }

    [Fact]
    public void ForeignCommandIsNeitherOverwrittenNorDeleted()
    {
        var backend = new Backend(); backend.Values[StartupPublication.DevelopmentIdentity] = "another command";
        var registration = new StartupRegistration(backend, StartupPublication.DevelopmentIdentity, Executable);
        Assert.False(registration.SetEnabled(true).CanChange);
        Assert.False(registration.SetEnabled(false).CanChange);
        Assert.Equal("another command", backend.Values[StartupPublication.DevelopmentIdentity]);
        Assert.Equal(0, backend.Writes);
    }

    [Fact]
    public void ReceiptAcceptsConfiguredExternalOutputWithoutDriveAssumptions()
    {
        var root = Path.GetTempPath();
        var source = Path.Combine(root, "checkout"); var output = Path.Combine(root, "custom output");
        var executable = Path.Combine(output, "TypeWhisper.WinUI.exe");
        Assert.Equal(executable, StartupPublication.Validate(Receipt(source, output), executable));
    }

    [Theory]
    [InlineData("inside")]
    [InlineData("same")]
    [InlineData("copied")]
    [InlineData("production")]
    [InlineData("version")]
    public void ReceiptRejectsCheckoutBuildCopiedOutputAndUnknownIdentity(string defect)
    {
        var source = Path.Combine(Path.GetTempPath(), "checkout");
        var output = defect == "inside" ? Path.Combine(source, "bin") : defect == "same" ? source : Path.Combine(Path.GetTempPath(), "published");
        var executable = Path.Combine(output, "TypeWhisper.WinUI.exe");
        if (defect == "copied") executable = Path.Combine(Path.GetTempPath(), "copy", "TypeWhisper.WinUI.exe");
        var json = Receipt(source, output, defect == "production" ? "TypeWhisper" : StartupPublication.DevelopmentIdentity, defect == "version" ? 2 : 1);
        Assert.Throws<InvalidDataException>(() => StartupPublication.Validate(json, executable));
    }

    private static string Receipt(string source, string output, string identity = StartupPublication.DevelopmentIdentity, int version = 1) =>
        JsonSerializer.Serialize(new { Version = version, Identity = identity, SourceRoot = source, OutputDirectory = output });

    private sealed class Backend : IStartupRegistrationBackend
    {
        internal Dictionary<string, string> Values { get; } = [];
        internal int Writes { get; private set; }
        internal int Accesses { get; private set; }
        internal bool FailEveryAccess { get; init; }
        internal bool ThrowOnWrite { get; set; }
        internal bool IgnoreWrites { get; init; }
        private void Access() { Accesses++; if (FailEveryAccess) throw new InvalidOperationException("Backend must not be accessed."); }
        public string? Read(string identity) { Access(); return Values.GetValueOrDefault(identity); }
        public void Write(string identity, string command)
        { Access(); Writes++; if (ThrowOnWrite) throw new IOException("Injected failure"); if (!IgnoreWrites) Values[identity] = command; }
        public void Delete(string identity)
        { Access(); Writes++; if (ThrowOnWrite) throw new IOException("Injected failure"); if (!IgnoreWrites) Values.Remove(identity); }
    }
}
