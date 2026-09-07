using TypeWhisper.Core.Interfaces;
using TypeWhisper.Presentation;
using Xunit;

public sealed class TranscriptionTaskPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-task-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "task.json");

    [Fact]
    public void MissingProfileSelectsTranscribeWithoutWriting()
    {
        var store = new TranscriptionTaskPreferencesStore(PreferencesPath);
        Assert.Equal(TranscriptionTask.Transcribe, store.Current);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(TranscriptionTask.Transcribe)]
    [InlineData(TranscriptionTask.Translate)]
    public void TaskSurvivesProfileReload(TranscriptionTask task)
    {
        var store = new TranscriptionTaskPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(task));
        Assert.Equal(task, new TranscriptionTaskPreferencesStore(PreferencesPath).Current);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("broken")]
    [InlineData("{\"Task\":\"Summarize\"}")]
    [InlineData("{\"Task\":1}")]
    [InlineData("{\"Task\":\"1\"}")]
    public void InvalidFileIsReportedWithoutOverwritingAndCanBeRepaired(string json)
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(PreferencesPath, json);
        var store = new TranscriptionTaskPreferencesStore(PreferencesPath);
        Assert.Equal(TranscriptionTask.Transcribe, store.Current);
        Assert.NotNull(store.Error);
        Assert.Equal(json, File.ReadAllText(PreferencesPath));
        Assert.Null(store.Save(TranscriptionTask.Translate));
        Assert.Equal(TranscriptionTask.Translate, new TranscriptionTaskPreferencesStore(PreferencesPath).Current);
    }

    [Fact]
    public void FailedSaveKeepsSavedTaskAndCleansTemporaryFiles()
    {
        var store = new TranscriptionTaskPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(TranscriptionTask.Translate));
        File.Delete(PreferencesPath); Directory.CreateDirectory(PreferencesPath);
        Assert.NotNull(store.Save(TranscriptionTask.Transcribe));
        Assert.Equal(TranscriptionTask.Translate, store.Current);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void InvalidTaskCannotReplaceSelection()
    {
        var store = new TranscriptionTaskPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(TranscriptionTask.Translate));
        Assert.NotNull(store.Save((TranscriptionTask)99));
        Assert.Equal(TranscriptionTask.Translate, store.Current);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
