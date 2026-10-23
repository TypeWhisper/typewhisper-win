using Microsoft.Win32;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.Platform.Tests;

// Uses a disposable key under HKCU; production reads HKLM, which these tests never touch.
public sealed class RegistryManagedPolicySourceTests : IDisposable
{
    private readonly string _root = @"Software\TypeWhisper.Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _parent;
    private readonly string _path;

    public RegistryManagedPolicySourceTests()
    {
        _parent = _root + @"\Policies";
        _path = _parent + @"\TypeWhisper";
        using var parent = Registry.CurrentUser.CreateSubKey(_parent, writable: true);
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); }
        catch (IOException) { /* best-effort cleanup of the disposable test key */ }
    }

    private RegistryManagedPolicySource Create() => new(RegistryHive.CurrentUser, RegistryView.Registry64, _path);

    private RegistryKey PolicyKey() => Registry.CurrentUser.CreateSubKey(_path, writable: true);

    [Fact]
    public void MissingKey_ReadsAbsent()
    {
        using var source = Create();

        var reading = source.Read();

        Assert.True(reading.Readable);
        Assert.Null(reading.Error);
        Assert.False(reading.Enforced.Exists);
        Assert.False(reading.Recommended.Exists);
    }

    [Fact]
    public void ReadsValuesListsAndRecommendedSeparately()
    {
        using (var key = PolicyKey())
        {
            key.SetValue("AllowCloudTranscription", 0, RegistryValueKind.DWord);
            key.SetValue("TranscriptionProviderId", "com.typewhisper.sherpa-onnx", RegistryValueKind.String);
            key.SetValue("Blob", new byte[] { 1, 2 }, RegistryValueKind.Binary);
            key.SetValue("", "default", RegistryValueKind.String);
            key.SetValue("AllowedPluginsConfigured", 1, RegistryValueKind.DWord);
            using var list = key.CreateSubKey("AllowedPlugins", writable: true);
            list.SetValue("2", "second", RegistryValueKind.String);
            list.SetValue("1", "first", RegistryValueKind.String);
            list.SetValue("10", "tenth", RegistryValueKind.String);
            using var recommended = key.CreateSubKey("Recommended", writable: true);
            recommended.SetValue("UpdateChannel", 1, RegistryValueKind.DWord);
            recommended.SetValue("LaunchAtLogin", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
        }
        using var source = Create();

        var reading = source.Read();

        Assert.True(reading.Readable);
        Assert.True(reading.Enforced.Exists);
        var cloud = reading.Enforced.Values["allowcloudtranscription"];
        Assert.Equal(ManagedPolicyRawKind.Number, cloud.Kind);
        Assert.Equal(0, cloud.Number);
        var provider = reading.Enforced.Values["TranscriptionProviderId"];
        Assert.Equal(ManagedPolicyRawKind.Text, provider.Kind);
        Assert.Equal("com.typewhisper.sherpa-onnx", provider.Text);
        var blob = reading.Enforced.Values["Blob"];
        Assert.Equal(ManagedPolicyRawKind.Unsupported, blob.Kind);
        Assert.Equal("Binary", blob.TypeName);
        Assert.False(reading.Enforced.Values.ContainsKey(""));
        Assert.Equal(1, reading.Enforced.Values["AllowedPluginsConfigured"].Number);
        var plugins = reading.Enforced.Lists["AllowedPlugins"];
        Assert.False(plugins.Malformed);
        Assert.Equal(["first", "second", "tenth"], plugins.Items);
        Assert.False(reading.Enforced.Lists.ContainsKey("Recommended"));
        Assert.True(reading.Recommended.Exists);
        Assert.Equal(1, reading.Recommended.Values["UpdateChannel"].Number);
        Assert.Equal(uint.MaxValue, reading.Recommended.Values["LaunchAtLogin"].Number);
        Assert.Empty(reading.Recommended.Lists);
    }

    [Fact]
    public void ListEntries_MustBeNumberedStringsWithoutNestedKeys()
    {
        using (var key = PolicyKey())
        {
            using var nonNumeric = key.CreateSubKey("AllowedLlmProviders", writable: true);
            nonNumeric.SetValue("1", "ok", RegistryValueKind.String);
            nonNumeric.SetValue("x", "bad", RegistryValueKind.String);
            using var wrongType = key.CreateSubKey("AllowedPlugins", writable: true);
            wrongType.SetValue("1", 5, RegistryValueKind.DWord);
            using var nested = key.CreateSubKey("AllowedUpdateChannels", writable: true);
            nested.SetValue("1", "stable", RegistryValueKind.String);
            using var inner = nested.CreateSubKey("Nested", writable: true);
            using var zero = key.CreateSubKey("AllowedTranscriptionProviders", writable: true);
            zero.SetValue("0", "zero", RegistryValueKind.String);
            zero.SetValue("1", "one", RegistryValueKind.String);
        }
        using var source = Create();

        var lists = source.Read().Enforced.Lists;

        Assert.True(lists["AllowedLlmProviders"].Malformed);
        Assert.Equal(["ok"], lists["AllowedLlmProviders"].Items);
        Assert.True(lists["AllowedPlugins"].Malformed);
        Assert.Empty(lists["AllowedPlugins"].Items);
        Assert.True(lists["AllowedUpdateChannels"].Malformed);
        Assert.Equal(["stable"], lists["AllowedUpdateChannels"].Items);
        Assert.True(lists["AllowedTranscriptionProviders"].Malformed);
        Assert.Equal(["one"], lists["AllowedTranscriptionProviders"].Items);
    }

    [Fact]
    public async Task Watching_ReportsCreationChangesAndDeletion()
    {
        // Not disposed: a notification already past the source's lock can still release it after the test ends,
        // and releasing a disposed semaphore on that thread-pool thread would crash the test host.
        var signal = new SemaphoreSlim(0);
        using var source = Create();
        source.Changed += () => signal.Release();
        Assert.True(source.StartWatching(), source.WatchError);
        Assert.True(source.IsWatching);
        Assert.True(source.StartWatching());

        using (PolicyKey()) { }
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)), "creation of the policy key was not reported");

        await DrainAsync(signal);
        using (var key = PolicyKey()) key.SetValue("LaunchAtLogin", 1, RegistryValueKind.DWord);
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)), "a value change was not reported");
        Assert.True(source.IsWatching);

        await DrainAsync(signal);
        Registry.CurrentUser.DeleteSubKeyTree(_path);
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)), "deletion of the policy key was not reported");
        Assert.True(source.IsWatching);
        Assert.False(source.Read().Enforced.Exists);
    }

    [Fact]
    public async Task Dispose_StopsWatching()
    {
        var source = Create();
        var signals = 0;
        source.Changed += () => Interlocked.Increment(ref signals);
        Assert.True(source.StartWatching(), source.WatchError);

        source.Dispose();
        Assert.False(source.IsWatching);
        using (PolicyKey()) { }
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref signals));
        Assert.False(source.StartWatching());
    }

    [Fact]
    public void StartWatching_WithoutParentKey_ReportsUnavailableButStillReads()
    {
        using var source = new RegistryManagedPolicySource(RegistryHive.CurrentUser, RegistryView.Registry64, _root + @"\Missing\TypeWhisper");

        Assert.False(source.StartWatching());
        Assert.False(source.IsWatching);
        Assert.Equal("parent key missing", source.WatchError);
        Assert.True(source.Read().Readable);
    }

    // Late notifications from the previous step must not satisfy the next assertion.
    private static async Task DrainAsync(SemaphoreSlim signal)
    {
        await Task.Delay(100);
        while (signal.CurrentCount > 0) await signal.WaitAsync();
    }
}
