using TypeWhisper.Plugin.Games;

public class ChatDeliveryOperationTests
{
    private static readonly ChatDeliveryOptions AddOn = new() { OpenKeys = [0x11, 0x10, 0x7A] };

    private sealed class Fake : IChatDeliveryPlatform
    {
        public readonly List<string> Calls = [];
        public bool InsertSucceeds = true;
        public Action<string>? OnCall;
        public bool Ready = true;
        public bool TargetIsReady => Ready;
        public bool Press(IReadOnlyList<ushort> chord) { Record("press:" + string.Join("+", chord.Select(key => key.ToString("X2")))); return true; }
        public Task<bool> InsertAsync(string text) { Record("paste:" + text); return Task.FromResult(InsertSucceeds); }
        public Task SettleAsync() { Record("settle"); return Task.CompletedTask; }
        public Task PauseAsync(int milliseconds) { Record("pause:" + milliseconds); return Task.CompletedTask; }
        private void Record(string call) { Calls.Add(call); OnCall?.Invoke(call); }
    }

    [Fact]
    public void ChatFor_WorldOfWarcraft_UsesTheAddOnChordAndByteLimit()
    {
        var game = GameCatalog.FindByProcess("WowClassic");

        var chat = GameChat.ChatFor(game!, WowAddOnSettings.Default, addOnInstalled: true);

        Assert.Equal(GameCatalog.WorldOfWarcraft, game);
        Assert.Equal([0x11, 0x10, 0x7A], chat.OpenKeys);
        Assert.Equal(255, chat.MaxMessageBytes);
        Assert.Equal(1000, chat.MessageIntervalMilliseconds);
        Assert.True(chat.Submit);
        Assert.Null(GameCatalog.FindByProcess("notepad"));
    }

    [Fact]
    public void ChatFor_FollowsTheAddOn()
    {
        var game = GameCatalog.WorldOfWarcraft;

        // Without the add-on only Enter opens the chat; sending is the add-on's setting.
        Assert.Equal([0x0D], GameChat.ChatFor(game, WowAddOnSettings.Default, addOnInstalled: false).OpenKeys);
        Assert.False(GameChat.ChatFor(game, new WowAddOnSettings(Send: false), true).Submit);
    }

    [Fact]
    public async Task AddOn_OpensWithTheChordInsertsAndSends()
    {
        var platform = new Fake();

        var result = await ChatDeliveryOperation.RunAsync(platform, "gg wp", AddOn);

        Assert.Equal(new ChatDeliveryResult(1, 1, null), result);
        Assert.Equal(["press:11+10+7A", "settle", "paste:gg wp", "settle", "press:0D"], platform.Calls);
    }

    [Fact]
    public async Task Enter_OpensWithEnter()
    {
        var platform = new Fake();

        var result = await ChatDeliveryOperation.RunAsync(platform, "gg wp", new() { OpenKeys = [0x0D] });

        Assert.True(result.Completed);
        Assert.Equal(["press:0D", "settle", "paste:gg wp", "settle", "press:0D"], platform.Calls);
    }

    [Fact]
    public async Task WithoutOpenKeyOrSubmit_OnlyInsertsTheWholeText()
    {
        var platform = new Fake();
        var text = new string('a', 20) + " " + new string('b', 20);

        var result = await ChatDeliveryOperation.RunAsync(platform, text,
            new() { OpenKeys = [], Submit = false, MaxMessageBytes = 50 });

        Assert.True(result.Completed);
        Assert.Equal(["paste:" + text], platform.Calls);
    }

    [Fact]
    public async Task WithoutSubmit_TextLongerThanOneMessage_IsNotInserted()
    {
        // The chat field holds one message; cutting the rest would lose it while reporting success.
        var platform = new Fake();
        var text = new string('a', 40) + " " + new string('b', 40);

        var result = await ChatDeliveryOperation.RunAsync(platform, text, AddOn with { Submit = false, MaxMessageBytes = 50 });

        Assert.Equal(new ChatDeliveryResult(0, 2, ChatDeliveryStage.TooLong), result);
        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task LongText_IsSentAsSeveralMessagesWithAPause()
    {
        var platform = new Fake();

        var result = await ChatDeliveryOperation.RunAsync(platform, "First part. Second part.",
            AddOn with { MaxMessageBytes = 12, MessageIntervalMilliseconds = 1000 });

        Assert.Equal(new ChatDeliveryResult(2, 2, null), result);
        Assert.Equal(["press:11+10+7A", "settle", "paste:First part.", "settle", "press:0D",
            "settle", "pause:1000", "press:11+10+7A", "settle", "paste:Second part.", "settle", "press:0D"], platform.Calls);
    }

    [Fact]
    public async Task FocusLostAfterOpening_DoesNotInsertOrSend()
    {
        var platform = new Fake();
        platform.OnCall = call => { if (call.StartsWith("press:")) platform.Ready = false; };

        var result = await ChatDeliveryOperation.RunAsync(platform, "hello", AddOn);

        Assert.Equal(new ChatDeliveryResult(0, 1, ChatDeliveryStage.Insert), result);
        Assert.Equal(["press:11+10+7A", "settle"], platform.Calls);
    }

    [Fact]
    public async Task FocusLostAfterInsert_DoesNotSendEnterToAnotherWindow()
    {
        var platform = new Fake();
        platform.OnCall = call => { if (call.StartsWith("paste:")) platform.Ready = false; };

        var result = await ChatDeliveryOperation.RunAsync(platform, "hello", AddOn);

        Assert.Equal(new ChatDeliveryResult(0, 1, ChatDeliveryStage.Submit), result);
        Assert.DoesNotContain("press:0D", platform.Calls);
    }

    [Fact]
    public async Task NotReadyAtStart_PressesNothing()
    {
        var platform = new Fake { Ready = false };

        var result = await ChatDeliveryOperation.RunAsync(platform, "hello", AddOn);

        Assert.Equal(ChatDeliveryStage.Open, result.FailedAt);
        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task FailedInsert_StopsBeforeEnter()
    {
        var platform = new Fake { InsertSucceeds = false };

        var result = await ChatDeliveryOperation.RunAsync(platform, "hello", AddOn);

        Assert.Equal(ChatDeliveryStage.Insert, result.FailedAt);
        Assert.Equal(["press:11+10+7A", "settle", "paste:hello"], platform.Calls);
    }

    [Fact]
    public async Task CancellationStopsBetweenMessages()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Fake();
        platform.OnCall = call => { if (call == "paste:First part.") cancellation.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChatDeliveryOperation.RunAsync(platform, "First part. Second part.", AddOn with { MaxMessageBytes = 12 }, cancellation.Token));

        Assert.Equal(["press:11+10+7A", "settle", "paste:First part.", "settle", "press:0D"], platform.Calls);
    }
}
