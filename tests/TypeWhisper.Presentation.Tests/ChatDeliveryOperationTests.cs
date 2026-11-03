using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

public class ChatDeliveryOperationTests
{
    private static readonly ChatDeliveryOptions AddOn = new() { OpenKey = ChatOpenKey.AddOn, AddOnChord = [0x11, 0x10, 0x7A] };

    private sealed class Fake : IChatDeliveryPlatform
    {
        public List<string> Calls = [];
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
        var preferences = new TypeWhisper.Presentation.GamePreferences { WorldOfWarcraft = new() { Enabled = true } };
        var addOn = TypeWhisper.Presentation.WowAddOnSettings.Default;

        var chat = preferences.ChatFor("WowClassic", addOn, addOnInstalled: true);

        Assert.NotNull(chat);
        Assert.Equal(ChatOpenKey.AddOn, chat.OpenKey);
        Assert.Equal([0x11, 0x10, 0x7A], chat.AddOnChord);
        Assert.Equal(255, chat.MaxMessageBytes);
        Assert.Equal(1000, chat.MessageIntervalMilliseconds);
        Assert.True(chat.Submit);
        Assert.Null(preferences.ChatFor("notepad", addOn, true));
        Assert.Null(new TypeWhisper.Presentation.GamePreferences().ChatFor("Wow", addOn, true));
    }

    [Fact]
    public void ChatFor_FollowsTheAddOn()
    {
        var preferences = new TypeWhisper.Presentation.GamePreferences { WorldOfWarcraft = new() { Enabled = true } };

        // Without the add-on only Enter opens the chat; sending is the add-on's setting.
        Assert.Equal(ChatOpenKey.Enter, preferences.ChatFor("Wow", TypeWhisper.Presentation.WowAddOnSettings.Default, addOnInstalled: false)!.OpenKey);
        Assert.False(preferences.ChatFor("Wow", new TypeWhisper.Presentation.WowAddOnSettings(Send: false), true)!.Submit);
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

        var result = await ChatDeliveryOperation.RunAsync(platform, "gg wp", new() { OpenKey = ChatOpenKey.Enter });

        Assert.True(result.Completed);
        Assert.Equal(["press:0D", "settle", "paste:gg wp", "settle", "press:0D"], platform.Calls);
    }

    [Fact]
    public async Task WithoutOpenKeyOrSubmit_OnlyInsertsTheWholeText()
    {
        var platform = new Fake();
        var text = new string('a', 40) + " " + new string('b', 40);

        var result = await ChatDeliveryOperation.RunAsync(platform, text,
            new() { OpenKey = ChatOpenKey.None, Submit = false, MaxMessageBytes = 50 });

        Assert.True(result.Completed);
        Assert.Equal(["paste:" + text], platform.Calls);
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
