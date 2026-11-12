using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// A workflow sends its dictation only to processes the user started in this session (#764).
[SupportedOSPlatform("windows")]
public sealed class TargetAppProcessTrustTests
{
    [Fact]
    public void OnlyProcessesOfThisSessionAndUserAtTheSameIntegrityOrAboveReceiveDictation()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var own = new ProcessIdentity(1, user, 8192);

        Assert.True(TargetAppProcessTrust.Admits(own, own));
        Assert.True(TargetAppProcessTrust.Admits(own, own with { Integrity = 12288 }));
        Assert.False(TargetAppProcessTrust.Admits(own, own with { Integrity = 4096 }));
        Assert.False(TargetAppProcessTrust.Admits(own, own with { Session = 2 }));
        Assert.False(TargetAppProcessTrust.Admits(own, own with { User = new("S-1-5-21-1-2-3-1002") }));
        Assert.False(TargetAppProcessTrust.Admits(own, null));
    }

    [Fact]
    public void ReadsTheIdentityOfThisProcessAndRefusesSystemProcesses()
    {
        var own = TargetAppProcessTrust.Read((uint)Environment.ProcessId);

        Assert.NotNull(own);
        Assert.Equal(WindowsIdentity.GetCurrent().User, own.Value.User);
        Assert.Equal((uint)Process.GetCurrentProcess().SessionId, own.Value.Session);
        Assert.True(own.Value.Integrity > 0);
        Assert.True(TargetAppProcessTrust.IsTrusted((uint)Environment.ProcessId));
        // The System process runs in session 0 under another account.
        Assert.False(TargetAppProcessTrust.IsTrusted(4));
        Assert.False(TargetAppProcessTrust.IsTrusted(uint.MaxValue));
    }

    [Fact]
    public void AClosedWindowNoLongerStandsForItsProcess()
    {
        Assert.False(TargetAppWindow.IsOwnedBy(IntPtr.Zero, (uint)Environment.ProcessId));
        Assert.False(TargetAppWindow.IsOwnedBy(new IntPtr(0x7FFFFFF0), (uint)Environment.ProcessId));
    }
}
