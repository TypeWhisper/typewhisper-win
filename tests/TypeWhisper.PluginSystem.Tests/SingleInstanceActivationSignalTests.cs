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

        Assert.Contains("SingleInstanceActivationSignal.OpenOrCreate()", source);
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
