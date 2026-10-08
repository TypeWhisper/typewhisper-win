using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class ProfileNotificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "profile-notifications-" + Guid.NewGuid());
    public ProfileNotificationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("dictionary")]
    [InlineData("snippet")]
    [InlineData("workflow")]
    [InlineData("history")]
    [InlineData("settings")]
    public async Task SubscribersCanWaitForAnotherProfileWriterWithoutHoldingItsLock(string kind)
    {
        var nested = new DictionaryService(Path.Combine(_root, "nested.json"));
        Task? write = null;
        var completedInHandler = false;
        Action handler = () =>
        {
            write = Task.Run(() => nested.AddEntry(new() { Id = "nested", EntryType = DictionaryEntryType.Term, Original = "nested" }));
            completedInHandler = write.Wait(TimeSpan.FromSeconds(3));
        };
        Publish(kind, handler);
        Assert.NotNull(write);
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completedInHandler, "The notification held the profile mutation lock.");
        Assert.Single(nested.Entries);
    }

    [Theory]
    [InlineData("dictionary")]
    [InlineData("snippet")]
    [InlineData("workflow")]
    [InlineData("history")]
    [InlineData("settings")]
    public void ThrowingSubscriberDoesNotHideACommittedWriteOrPreventOtherSubscribers(string kind)
    {
        var notified = false;
        Action handlers = () => throw new InvalidOperationException("subscriber failed");
        handlers += () => notified = true;
        Publish(kind, handlers);
        Assert.True(notified);
        Assert.NotEmpty(File.ReadAllText(Path.Combine(_root, "primary.json")));
    }

    private void Publish(string kind, Action handlers)
    {
        var path = Path.Combine(_root, "primary.json");
        switch (kind)
        {
            case "dictionary":
                var dictionary = new DictionaryService(path);
                dictionary.EntriesChanged += handlers;
                dictionary.AddEntry(new() { Id = "one", EntryType = DictionaryEntryType.Term, Original = "one" });
                break;
            case "snippet":
                var snippets = new SnippetService(path);
                snippets.SnippetsChanged += handlers;
                snippets.AddSnippet(new() { Id = "one", Trigger = "one", Replacement = "expanded" });
                break;
            case "workflow":
                var workflows = new WorkflowService(path);
                workflows.WorkflowsChanged += handlers;
                workflows.AddWorkflow(new() { Id = "one", Name = "one", Template = WorkflowTemplate.Dictation,
                    Trigger = new() { Kind = WorkflowTriggerKind.Manual } });
                break;
            case "history":
                var history = new HistoryService(path);
                history.RecordsChanged += handlers;
                history.AddRecord(new() { Id = "one", Timestamp = DateTime.UtcNow, RawText = "one", FinalText = "one" });
                break;
            case "settings":
                var settings = new SettingsService(path);
                foreach (Action handler in handlers.GetInvocationList()) settings.SettingsChanged += _ => handler();
                settings.Save(AppSettings.Default);
                break;
        }
    }
}
