using TypeWhisper.Windows;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SingleInstanceActivationSignalTests
{
    [Fact]
    public void NotifyBeforeListen_IsDeliveredWhenListenerStarts()
    {
        var eventName = $"TypeWhisper-SingleInstance-Activation-{Guid.NewGuid():N}";
        using var primary = SingleInstanceActivationSignal.OpenOrCreate(eventName);
        using var secondary = SingleInstanceActivationSignal.OpenOrCreate(eventName);
        using var delivered = new ManualResetEventSlim();

        Assert.True(secondary.Notify());

        var registration = primary.Listen(delivered.Set);
        try
        {
            Assert.True(delivered.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "", true)]
    [InlineData(true, null, false)]
    [InlineData(false, "typewhisper://supporter/discord", false)]
    public void Program_NotifiesTheRunningInstanceOnlyForInteractiveLaunches(
        bool startMinimized,
        string? callbackArg,
        bool expected)
    {
        Assert.Equal(expected, Program.ShouldNotifyRunningInstance(startMinimized, callbackArg));
    }

    [Fact]
    public void Program_UsesTheActivationSignalForAnExistingInstance()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Program.cs");

        Assert.Contains("SingleInstanceActivationSignal.OpenOrCreate(", source);
        Assert.Contains("UiAutomation.IsEnabled ? $\"-UiAutomation-{UiAutomation.InstanceId}\" : string.Empty", source);
        Assert.Contains("$\"TypeWhisper-SingleInstance-Activation{synchronizationSuffix}\"", source);
        Assert.Contains("if (ShouldNotifyRunningInstance(StartMinimized, callbackArg))", source);
        Assert.Contains("activationSignal.Notify();", source);

        var foregroundPermissionIndex = source.IndexOf(
            "AllowRunningInstanceToSetForegroundWindow();",
            StringComparison.Ordinal);
        var notifyIndex = source.IndexOf("activationSignal.Notify();", StringComparison.Ordinal);
        Assert.True(foregroundPermissionIndex >= 0);
        Assert.True(notifyIndex > foregroundPermissionIndex);
    }

    [Fact]
    public void Program_GrantsForegroundAccessToEverySameSessionCandidate()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Program.cs");
        var methodStart = source.IndexOf(
            "private static void AllowRunningInstanceToSetForegroundWindow()",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);

        var methodEnd = source.IndexOf(
            "internal static bool IsPortableLayout",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodEnd > methodStart);

        var methodSource = source[methodStart..methodEnd];
        Assert.Contains("candidate.SessionId != current.SessionId", methodSource);
        Assert.Contains("NativeMethods.AllowSetForegroundWindow((uint)candidate.Id);", methodSource);
        Assert.DoesNotContain("return;", methodSource);
    }

    [Fact]
    public void App_ActivatesOnboardingOrDashboardWhenAnotherInstanceStarts()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "App.xaml.cs");

        Assert.Contains("StartSingleInstanceActivationListener();", source);
        Assert.Contains("ActivatePrimaryInstance();", source);
        Assert.Contains("ShowSettingsWindow(SettingsRoute.Dashboard);", source);
    }
}
