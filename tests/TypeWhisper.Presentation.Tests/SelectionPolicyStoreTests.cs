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
        Assert.Null(store.Current.RetainedProviderId);
        Assert.Null(store.Error);

        Assert.True(store.Save(new("com.typewhisper.groq", "whisper-large-v3")));
        var reloaded = new SelectionPolicyStore(FilePath);
        Assert.Equal("com.typewhisper.groq", reloaded.Current.RetainedProviderId);
        Assert.Equal("whisper-large-v3", reloaded.Current.RetainedModelId);

        Assert.True(reloaded.Save(new()));
        Assert.Null(new SelectionPolicyStore(FilePath).Current.RetainedModelId);
    }

    [Fact]
    public void UnreadableFileBehavesLikeAnEmptyOne()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "not json");

        var store = new SelectionPolicyStore(FilePath);

        Assert.Null(store.Current.RetainedProviderId);
        Assert.NotNull(store.Error);
        Assert.True(store.Save(new("p", "m")));
        Assert.Null(store.Error);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
