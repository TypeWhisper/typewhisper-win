using TypeWhisper.Core.Data;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class PromptActionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TypeWhisperDatabase _db;
    private readonly PromptActionService _sut;

    public PromptActionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tw_test_{Guid.NewGuid():N}.db");
        _db = new TypeWhisperDatabase(_dbPath);
        _db.Initialize();
        _sut = new PromptActionService(_db);
    }

    [Fact]
    public void AddAction_PersistsAndLoads()
    {
        _sut.AddAction(new PromptAction
        {
            Id = "1",
            Name = "Test Prompt",
            SystemPrompt = "Do something",
            Icon = "\U0001F680"
        });

        var freshService = new PromptActionService(_db);
        var action = Assert.Single(freshService.Actions);
        Assert.Equal("Test Prompt", action.Name);
        Assert.Equal("Do something", action.SystemPrompt);
        Assert.Equal("\U0001F680", action.Icon);
    }

    [Fact]
    public void UpdateAction_PersistsChanges()
    {
        _sut.AddAction(new PromptAction
        {
            Id = "1",
            Name = "Original",
            SystemPrompt = "Original prompt"
        });

        _sut.UpdateAction(new PromptAction
        {
            Id = "1",
            Name = "Updated",
            SystemPrompt = "Updated prompt"
        });

        var freshService = new PromptActionService(_db);
        var action = Assert.Single(freshService.Actions);
        Assert.Equal("Updated", action.Name);
        Assert.Equal("Updated prompt", action.SystemPrompt);
    }

    [Fact]
    public void DeleteAction_RemovesFromDb()
    {
        _sut.AddAction(new PromptAction { Id = "1", Name = "A", SystemPrompt = "a" });
        _sut.AddAction(new PromptAction { Id = "2", Name = "B", SystemPrompt = "b" });

        _sut.DeleteAction("1");

        var freshService = new PromptActionService(_db);
        Assert.Single(freshService.Actions);
        Assert.Equal("B", freshService.Actions[0].Name);
    }

    [Fact]
    public void SeedPresets_CreatesDefaults()
    {
        _sut.SeedPresets();

        Assert.Equal(5, _sut.Actions.Count);
        Assert.All(_sut.Actions, a => Assert.True(a.IsPreset));
        Assert.Contains(_sut.Actions, a => a.Name == "Translate to English");
        Assert.Contains(_sut.Actions, a => a.Name == "Reply");
    }

    [Fact]
    public void SeedPresets_IsIdempotent()
    {
        _sut.SeedPresets();
        var count1 = _sut.Actions.Count;

        _sut.SeedPresets();
        var count2 = _sut.Actions.Count;

        Assert.Equal(count1, count2);
    }

    [Fact]
    public void EnabledActions_FiltersAndSorts()
    {
        _sut.AddAction(new PromptAction { Id = "1", Name = "C", SystemPrompt = "c", SortOrder = 2, IsEnabled = true });
        _sut.AddAction(new PromptAction { Id = "2", Name = "A", SystemPrompt = "a", SortOrder = 0, IsEnabled = true });
        _sut.AddAction(new PromptAction { Id = "3", Name = "B", SystemPrompt = "b", SortOrder = 1, IsEnabled = false });

        var enabled = _sut.EnabledActions;
        Assert.Equal(2, enabled.Count);
        Assert.Equal("A", enabled[0].Name);
        Assert.Equal("C", enabled[1].Name);
    }

    [Fact]
    public void Reorder_UpdatesSortOrder()
    {
        _sut.AddAction(new PromptAction { Id = "1", Name = "First", SystemPrompt = "a", SortOrder = 0 });
        _sut.AddAction(new PromptAction { Id = "2", Name = "Second", SystemPrompt = "b", SortOrder = 1 });
        _sut.AddAction(new PromptAction { Id = "3", Name = "Third", SystemPrompt = "c", SortOrder = 2 });

        _sut.Reorder(["3", "1", "2"]);

        var freshService = new PromptActionService(_db);
        var ordered = freshService.EnabledActions;
        Assert.Equal("Third", ordered[0].Name);
        Assert.Equal("First", ordered[1].Name);
        Assert.Equal("Second", ordered[2].Name);
    }

    [Fact]
    public void ActionsChanged_FiresOnAdd()
    {
        var fired = false;
        _sut.ActionsChanged += () => fired = true;

        _sut.AddAction(new PromptAction { Id = "1", Name = "Test", SystemPrompt = "test" });

        Assert.True(fired);
    }

    [Fact]
    public void ProviderOverride_PersistsCorrectly()
    {
        _sut.AddAction(new PromptAction
        {
            Id = "1",
            Name = "With Provider",
            SystemPrompt = "test",
            ProviderOverride = "plugin:com.test:model-1",
            ModelOverride = "model-1"
        });

        var freshService = new PromptActionService(_db);
        var action = Assert.Single(freshService.Actions);
        Assert.Equal("plugin:com.test:model-1", action.ProviderOverride);
        Assert.Equal("model-1", action.ModelOverride);
    }

    [Fact]
    public void SchemaV5_CreatesPromptActionsTable()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(prompt_actions)";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("id", columns);
        Assert.Contains("name", columns);
        Assert.Contains("system_prompt", columns);
        Assert.Contains("icon", columns);
        Assert.Contains("is_preset", columns);
        Assert.Contains("sort_order", columns);
        Assert.Contains("provider_override", columns);
    }

    [Fact]
    public void SchemaV5_AddsPromptActionIdToProfiles()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(profiles)";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("prompt_action_id", columns);
    }

    [Fact]
    public void SchemaV6_AddsNewColumnsToPromptActions()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(prompt_actions)";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("target_action_plugin_id", columns);
        Assert.Contains("hotkey_key", columns);
    }

    [Fact]
    public void SchemaV6_AddsModelUsedToHistory()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(transcription_history)";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("model_used", columns);
    }

    [Fact]
    public void SchemaV6_AddsHotkeyDataToProfiles()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(profiles)";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("hotkey_data", columns);
    }

    [Fact]
    public void TargetActionPluginId_PersistsCorrectly()
    {
        _sut.AddAction(new PromptAction
        {
            Id = "1",
            Name = "With Target",
            SystemPrompt = "test",
            TargetActionPluginId = "com.test.linear",
            HotkeyKey = "Ctrl+Shift+L"
        });

        var freshService = new PromptActionService(_db);
        var action = Assert.Single(freshService.Actions);
        Assert.Equal("com.test.linear", action.TargetActionPluginId);
        Assert.Equal("Ctrl+Shift+L", action.HotkeyKey);
    }

    [Fact]
    public void TargetActionPluginId_NullByDefault()
    {
        _sut.AddAction(new PromptAction
        {
            Id = "1",
            Name = "Normal",
            SystemPrompt = "test"
        });

        var freshService = new PromptActionService(_db);
        var action = Assert.Single(freshService.Actions);
        Assert.Null(action.TargetActionPluginId);
        Assert.Null(action.HotkeyKey);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
