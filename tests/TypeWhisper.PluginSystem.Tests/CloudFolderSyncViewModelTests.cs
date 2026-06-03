using System.IO;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CloudFolderSyncViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public CloudFolderSyncViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw_cloud_sync_vm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FreeUserSeesLockedStateAndCannotCreateSyncFiles()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var store = new FakeUserDataSyncStore(dictionaryEntries:
        [
            new UserDataSyncDictionaryEntry(
                UserDataSyncDictionaryEntryType.Term,
                "TypeWhisper",
                null,
                false,
                true,
                Date(1),
                Date(2))
        ]);
        var sut = new CloudFolderSyncViewModel(settings, store, () => false, TimeSpan.Zero);

        sut.SetFolderPath(_tempDir);
        await sut.SyncNowAsync();

        Assert.True(sut.ShowLockedState);
        Assert.False(sut.ShowSyncControls);
        Assert.Contains("Commercial", sut.ErrorMessage);
        Assert.False(Directory.Exists(CloudFolderSyncEngine.PackagePath(_tempDir)));
    }

    [Fact]
    public async Task CommercialUserCanChooseFolderSyncAndPersistState()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var store = new FakeUserDataSyncStore(dictionaryEntries:
        [
            new UserDataSyncDictionaryEntry(
                UserDataSyncDictionaryEntryType.Term,
                "TypeWhisper",
                null,
                false,
                true,
                Date(1),
                Date(2))
        ]);
        var sut = new CloudFolderSyncViewModel(settings, store, () => true, TimeSpan.Zero);

        sut.SetFolderPath(_tempDir);
        await sut.SyncNowAsync();

        Assert.True(sut.ShowSyncControls);
        Assert.False(sut.ShowLockedState);
        Assert.Equal(_tempDir, settings.Current.CloudFolderSyncFolderPath);
        Assert.NotNull(settings.Current.CloudFolderSyncState);
        Assert.NotNull(settings.Current.CloudFolderSyncState.LastSyncAt);
        Assert.Equal(0, sut.PendingChanges);
        Assert.True(Directory.Exists(CloudFolderSyncEngine.PackagePath(_tempDir)));
        Assert.Contains("Synced", sut.StatusMessage);
    }

    [Fact]
    public void ChangingFolderClearsPersistedSyncState()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            CloudFolderSyncFolderPath = Path.Combine(_tempDir, "old"),
            CloudFolderSyncState = new CloudFolderSyncState
            {
                DeviceId = "win-a",
                KnownLocalItemIds = ["dictionary:term:dHlwZXdoaXNwZXI"],
                AppliedOperationIds = ["op-1"],
                LastSyncAt = Date(2)
            }
        });
        var sut = new CloudFolderSyncViewModel(settings, new FakeUserDataSyncStore(), () => true, TimeSpan.Zero);
        var newFolder = Path.Combine(_tempDir, "new");
        Directory.CreateDirectory(newFolder);

        sut.SetFolderPath(newFolder);

        Assert.Equal(newFolder, settings.Current.CloudFolderSyncFolderPath);
        Assert.Null(settings.Current.CloudFolderSyncState);
        Assert.Null(sut.LastSyncDate);
        Assert.Equal(0, sut.PendingChanges);
    }

    [Fact]
    public async Task LocalChangesScheduleSyncForCommercialUser()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var store = new FakeUserDataSyncStore(dictionaryEntries:
        [
            new UserDataSyncDictionaryEntry(
                UserDataSyncDictionaryEntryType.Term,
                "TypeWhisper",
                null,
                false,
                true,
                Date(1),
                Date(2))
        ]);
        var sut = new CloudFolderSyncViewModel(settings, store, () => true, TimeSpan.FromMilliseconds(10));
        sut.SetFolderPath(_tempDir);

        store.TriggerLocalChange();
        await Task.Delay(250);

        Assert.Equal(0, sut.PendingChanges);
        Assert.True(Directory.Exists(CloudFolderSyncEngine.PackagePath(_tempDir)));
    }

    private static DateTime Date(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + (long)(seconds * 1000)).UtcDateTime;

    private sealed class FakeSettingsService(AppSettings initialSettings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initialSettings;
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private sealed class FakeUserDataSyncStore : IUserDataSyncStore
    {
        private readonly List<UserDataSyncDictionaryEntry> _dictionaryEntries;
        private readonly List<UserDataSyncSnippet> _snippets;
        private readonly Dictionary<Guid, Action> _observers = [];

        public FakeUserDataSyncStore(
            IEnumerable<UserDataSyncDictionaryEntry>? dictionaryEntries = null,
            IEnumerable<UserDataSyncSnippet>? snippets = null)
        {
            _dictionaryEntries = dictionaryEntries?.ToList() ?? [];
            _snippets = snippets?.ToList() ?? [];
        }

        public UserDataSyncSnapshot Snapshot() => new(_dictionaryEntries, _snippets);

        public void Apply(IReadOnlyList<UserDataSyncMutation> mutations)
        {
            foreach (var mutation in mutations)
            {
                if (mutation is UserDataSyncMutation.UpsertDictionary upsert)
                    _dictionaryEntries.Add(upsert.Entry);
            }
        }

        public Guid ObserveLocalChanges(Action handler)
        {
            var id = Guid.NewGuid();
            _observers[id] = handler;
            return id;
        }

        public void RemoveLocalChangeObserver(Guid id) => _observers.Remove(id);

        public void TriggerLocalChange()
        {
            foreach (var observer in _observers.Values.ToArray())
                observer();
        }
    }
}
