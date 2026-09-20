using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class SharedFileActivationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfileFailureRejectsShareWithoutReadingOrAcknowledgingFiles(bool reportingFails)
    {
        var operation = new SharedOperation([@"C:\audio\one.wav"]) { ReportingFails = reportingFails };
        SharedFileActivation.Reject(operation, "Profile recovery failed; files were not received.");
        Assert.Equal("Profile recovery failed; files were not received.", operation.Error);
        Assert.Equal(["error"], operation.Events);
    }

    [Fact]
    public async Task SharedPathsAreQueuedBeforeCompletionAndDispatchedOnce()
    {
        var inbox = new ActivationInbox();
        ApplicationActivationRequest? received = null;
        var operation = new SharedOperation([@"C:\audio\one.wav", @"C:\audio\two.mp3"])
        {
            OnCompleted = () => received = Assert.Single(inbox.Drain())
        };
        await SharedFileActivation.ReceiveAsync(operation, inbox);
        Assert.Equal(["started", "read", "retrieved", "completed"], operation.Events);
        Assert.NotNull(received);
        Assert.Equal("--files", received.Route);
        Assert.Equal(operation.Paths, received.Files);
        Assert.True(received.ShowWindow);
        Assert.Empty(inbox.Drain());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task EmptyOrOversizedSharesAreRejected(int count)
    {
        var inbox = new ActivationInbox();
        var operation = new SharedOperation(Enumerable.Range(0, count).Select(i => $@"C:\audio\{i}.wav").ToArray());
        await SharedFileActivation.ReceiveAsync(operation, inbox);
        Assert.NotNull(operation.Error);
        Assert.DoesNotContain("completed", operation.Events);
        Assert.Empty(inbox.Drain());
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.wav")]
    public async Task UnsupportedStorageItemsRejectTheWholeShare(string unsupported)
    {
        var inbox = new ActivationInbox();
        var operation = new SharedOperation([@"C:\audio\valid.wav", unsupported]);
        await SharedFileActivation.ReceiveAsync(operation, inbox);
        Assert.NotNull(operation.Error);
        Assert.Empty(inbox.Drain());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosedOrFullInboxNeverAcknowledgesReceipt(bool closed)
    {
        var inbox = new ActivationInbox();
        if (closed) inbox.Close();
        else for (int i = 0; i < 8; i++) inbox.Add(ApplicationActivationRequest.Parse([]));
        var operation = new SharedOperation([@"C:\audio\valid.wav"]);
        await SharedFileActivation.ReceiveAsync(operation, inbox);
        Assert.NotNull(operation.Error);
        Assert.DoesNotContain("completed", operation.Events);
    }

    [Fact]
    public async Task DataRetrievalFailureReportsAnErrorWithoutQueuing()
    {
        var inbox = new ActivationInbox();
        var operation = new SharedOperation([]) { ReadError = new IOException("Access denied") };
        await SharedFileActivation.ReceiveAsync(operation, inbox);
        Assert.NotNull(operation.Error);
        Assert.DoesNotContain("completed", operation.Events);
        Assert.Empty(inbox.Drain());
    }

    private sealed class SharedOperation(IReadOnlyList<string> paths) : ISharedFileOperation
    {
        public IReadOnlyList<string> Paths => paths;
        public List<string> Events { get; } = [];
        public string? Error { get; private set; }
        public Exception? ReadError { get; init; }
        public Action? OnCompleted { get; init; }
        public bool ReportingFails { get; init; }
        public void ReportStarted() => Events.Add("started");
        public Task<IReadOnlyList<string>> ReadPathsAsync()
        {
            Events.Add("read");
            return ReadError is null ? Task.FromResult(paths) : Task.FromException<IReadOnlyList<string>>(ReadError);
        }
        public void ReportDataRetrieved() => Events.Add("retrieved");
        public void ReportCompleted() { OnCompleted?.Invoke(); Events.Add("completed"); }
        public void ReportError(string message)
        {
            Error = message; Events.Add("error");
            if (ReportingFails) throw new InvalidOperationException("Share UI already closed");
        }
    }
}
