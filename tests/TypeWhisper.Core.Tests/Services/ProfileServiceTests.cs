using TypeWhisper.Core.Data;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class ProfileServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TypeWhisperDatabase _db;
    private readonly ProfileService _sut;

    public ProfileServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tw_test_{Guid.NewGuid():N}.db");
        _db = new TypeWhisperDatabase(_dbPath);
        _db.Initialize();
        _sut = new ProfileService(_db);
    }

    [Fact]
    public void PromptActionId_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Profile",
            PromptActionId = "prompt-123"
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_db);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("prompt-123", loaded.PromptActionId);
    }

    [Fact]
    public void PromptActionId_NullRoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "No Prompt",
            PromptActionId = null
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_db);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Null(loaded.PromptActionId);
    }

    [Fact]
    public void UpdateProfile_ChangesPromptActionId()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test",
            PromptActionId = null
        };

        _sut.AddProfile(profile);
        _sut.UpdateProfile(profile with { PromptActionId = "action-456" });

        var freshService = new ProfileService(_db);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("action-456", loaded.PromptActionId);
    }

    [Fact]
    public void HotkeyData_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "With Hotkey",
            HotkeyData = "{\"key\":\"Ctrl+1\"}"
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_db);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("{\"key\":\"Ctrl+1\"}", loaded.HotkeyData);
    }

    [Fact]
    public void HotkeyData_NullByDefault()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "No Hotkey"
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_db);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Null(loaded.HotkeyData);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
