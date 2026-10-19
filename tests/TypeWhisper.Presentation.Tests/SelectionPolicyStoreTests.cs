using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class SelectionPolicyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "selection-policy-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "selection-policy.json");

    [Fact]
    public void RetainedModelRoundTripsAndClears()
    {
        var store = new SelectionPolicyStore(FilePath);
        Assert.Null(store.Current.Retained);
        Assert.Null(store.Error);

        Assert.True(store.Save(store.Current.With("com.typewhisper.groq", "whisper-large-v3").With("other", "m2")));
        var reloaded = new SelectionPolicyStore(FilePath);
        Assert.Equal("whisper-large-v3", reloaded.Current.RetainedModel("com.typewhisper.groq"));
        Assert.Equal("m2", reloaded.Current.RetainedModel("other"));

        Assert.True(reloaded.Save(reloaded.Current.Without("com.typewhisper.groq")));
        Assert.Null(new SelectionPolicyStore(FilePath).Current.RetainedModel("com.typewhisper.groq"));
        Assert.True(reloaded.Save(reloaded.Current.Without("other")));
        Assert.Null(new SelectionPolicyStore(FilePath).Current.Retained);
    }

    [Fact]
    public void FailedWriteKeepsTheLastSavedState()
    {
        // A file where the directory should be makes every write fail.
        File.WriteAllText(_root, "");
        try
        {
            var store = new SelectionPolicyStore(FilePath);
            Assert.False(store.Save(store.Current.With("p", "m")));
            Assert.Null(store.Current.Retained);
            Assert.NotNull(store.Error);
        }
        finally { File.Delete(_root); }
    }

    [Fact]
    public void UnreadableFileBehavesLikeAnEmptyOne()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "not json");

        var store = new SelectionPolicyStore(FilePath);

        Assert.Null(store.Current.Retained);
        Assert.NotNull(store.Error);
        Assert.True(store.Save(store.Current.With("p", "m")));
        Assert.Null(store.Error);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
