using System.Text.Json;
using TypeWhisper.Plugin.FileMemory;
using TypeWhisper.PluginSDK;

namespace PortableMigration.Tests;

public sealed class MemorySettingsTests
{
    private static Task Save(FileMemoryPlugin plugin, string text, CancellationToken ct = default) =>
        plugin.SaveProfileSettingsAsync(plugin.ConnectionIdentity,
            new Dictionary<string,string> { [plugin.ConnectionIdentity + ":content"] = text }, null, ct);

    [Fact]
    public async Task DraftIsNotStoredUntilSave_AndCanBeEditedAfterRestart()
    {
        using var f = new PortableFixture();
        using var plugin = new FileMemoryPlugin();
        await plugin.ActivateAsync(f.Host);
        var id = plugin.ConnectionIdentity;
        Assert.Empty(await plugin.GetAllAsync());
        Assert.False(File.Exists(Path.Combine(f.Host.PluginDataDirectory, "memories.json")));
        await Save(plugin, "Washington, D.C.\nÄpfel & Öl");
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(f.Host);
        Assert.Equal(id, plugin.ConnectionIdentity);
        Assert.Contains("\n", plugin.TextSettings.Single(x => x.Id.EndsWith(":content")).Value);
        await Save(plugin, "Edited memory");
        Assert.Equal("Edited memory", Assert.Single(await plugin.GetAllAsync()));
    }

    [Fact]
    public async Task SearchUsesUnsavedQuery_WithoutSavingEditedMemory()
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Save(p, "Saved tea preference");
        var result = await p.ExecuteProfileActionAsync(p.ConnectionIdentity, "search",
            new Dictionary<string,string> { ["query"] = "TEA", [p.ConnectionIdentity + ":content"] = "Unsaved text" }, null, default);
        Assert.Contains("Saved tea preference", result.Message);
        Assert.Equal("Saved tea preference", Assert.Single(await p.GetAllAsync()));
        Assert.Equal("", p.TextSettings.Single(x => x.Id == "query").Value);
    }

    [Fact]
    public async Task RemovingSelectedEntry_PreservesOthers_AndRejectsStaleDelete()
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Save(p, "One"); var first = p.ConnectionIdentity; var stale = p.RemoveProfileActionId!;
        await p.ExecuteSettingsActionAsync("add", default); await Save(p, "Two");
        await Assert.ThrowsAsync<ArgumentException>(() => p.ExecuteSettingsActionAsync(stale, default));
        await p.ExecuteSettingsActionAsync(p.RemoveProfileActionId!, default);
        Assert.Equal("One", Assert.Single(await p.GetAllAsync()));
        Assert.Equal(first, p.ConnectionIdentity);
        await p.DeactivateAsync(); await p.ActivateAsync(f.Host);
        Assert.Equal("One", Assert.Single(await p.GetAllAsync()));
    }

    [Fact]
    public async Task RemovingDraft_DoesNotDeleteSavedMemories()
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Save(p, "Keep me"); await p.ExecuteSettingsActionAsync("add", default);
        await p.ExecuteSettingsActionAsync(p.RemoveProfileActionId!, default);
        Assert.Equal("Keep me", Assert.Single(await p.GetAllAsync()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyContentCannotReplaceSavedEntry(string invalid)
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Save(p, "Keep me");
        await Assert.ThrowsAsync<ArgumentException>(() => Save(p, invalid));
        Assert.Equal("Keep me", Assert.Single(await p.GetAllAsync()));
    }

    [Fact]
    public async Task OversizedAndUnknownFields_DoNotPartiallySave()
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Save(p, "Keep me");
        await Assert.ThrowsAsync<ArgumentException>(() => Save(p, new string('a', 32769)));
        await Assert.ThrowsAsync<ArgumentException>(() => p.SaveProfileSettingsAsync(p.ConnectionIdentity,
            new Dictionary<string,string> { [p.ConnectionIdentity + ":content"] = "Replace", ["unknown"] = "bad" }, null, default));
        Assert.Equal("Keep me", Assert.Single(await p.GetAllAsync()));
    }

    [Fact]
    public async Task StaleProfileAndDuplicateContent_AreRejected()
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Save(p, "One"); var first = p.ConnectionIdentity;
        await p.ExecuteSettingsActionAsync("add", default);
        await Assert.ThrowsAsync<ArgumentException>(() => p.SaveProfileSettingsAsync(first,
            new Dictionary<string,string> { [first + ":content"] = "Wrong" }, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Save(p, "One"));
        Assert.Equal("One", Assert.Single(await p.GetAllAsync()));
    }

    [Fact]
    public async Task FailedWritePreservesInMemoryAndPersistedValues()
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Save(p, "Keep me");
        var file = Path.Combine(f.Host.PluginDataDirectory, "memories.json");
        var backup = file + ".backup"; File.Move(file, backup); Directory.CreateDirectory(file);
        var failure = await Record.ExceptionAsync(() => Save(p, "Lost write"));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("Keep me", Assert.Single(await p.GetAllAsync()));
        Assert.Equal("Keep me", p.TextSettings.Single(x => x.Id.EndsWith(":content")).Value);
        Assert.Empty(Directory.GetFiles(f.Host.PluginDataDirectory, "*.tmp"));
        Directory.Delete(file); File.Move(backup, file);
        await p.DeactivateAsync(); await p.ActivateAsync(f.Host);
        Assert.Equal("Keep me", Assert.Single(await p.GetAllAsync()));
    }

    [Fact]
    public async Task CancellationDoesNotSaveProfile()
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Save(p, "Keep me");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Save(p, "Cancelled", new CancellationToken(true)));
        Assert.Equal("Keep me", Assert.Single(await p.GetAllAsync()));
    }

    [Fact]
    public async Task OldEntriesWithoutIds_KeepContentAndGainPersistentIdentityOnSave()
    {
        using var f = new PortableFixture(); Directory.CreateDirectory(f.Host.PluginDataDirectory);
        var file = Path.Combine(f.Host.PluginDataDirectory, "memories.json");
        await File.WriteAllTextAsync(file, "[{\"Content\":\"Old memory\",\"CreatedAt\":\"2026-09-01T00:00:00Z\"}]");
        using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        var id = p.ConnectionIdentity; Assert.True(Guid.TryParse(id, out _));
        Assert.Equal("Old memory", Assert.Single(await p.GetAllAsync()));
        await Save(p, "Still here"); await p.DeactivateAsync(); await p.ActivateAsync(f.Host);
        Assert.Equal(id, p.ConnectionIdentity);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Content\":null}]")]
    [InlineData("[{\"Content\":\"\"}]")]
    [InlineData("broken")]
    public async Task InvalidFilesAreReadOnlyAndNeverOverwritten(string original)
    {
        using var f = new PortableFixture(); Directory.CreateDirectory(f.Host.PluginDataDirectory);
        var file = Path.Combine(f.Host.PluginDataDirectory, "memories.json"); await File.WriteAllTextAsync(file, original);
        using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        Assert.Null(p.AddProfileActionId); Assert.Empty(p.SettingsActions);
        Assert.Contains(p.TextSettings, x => x.Id == "load_error");
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.StoreAsync("overwrite"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.ClearAllAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ConcurrentStoresDoNotLoseEntriesOrPersistDuplicates()
    {
        using var f = new PortableFixture(); using var p = new FileMemoryPlugin(); await p.ActivateAsync(f.Host);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => p.StoreAsync("Entry " + (i % 20))));
        Assert.Equal(20, await p.CountAsync());
        await p.DeactivateAsync(); await p.ActivateAsync(f.Host); Assert.Equal(20, await p.CountAsync());
    }
}
