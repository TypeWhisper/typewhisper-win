using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;

namespace TypeWhisper.Plugin.GitHubCopilot.Tests;

public sealed class CopilotSdkTransportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedModelSwitchInvalidatesModelAndCleansSessionWithoutSendingText(bool deferred)
    {
        await using var server = new FakeCopilotServer { DeferredSwitch = deferred, MismatchedModel = !deferred };
        await Assert.ThrowsAsync<CopilotModelUnavailableException>(() => server.CreateTransport().ProcessAsync(
            server.Root, FakeTransport.Personal, "s", "private", "model-a", default));
        Assert.DoesNotContain(server.Requests, r => r.Method == "session.send");
        Assert.Contains(server.Requests, r => r.Method == "session.abort");
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
    }

    [Fact]
    public async Task DisabledLiveModelProducesTypedFailureBeforeSessionCreation()
    {
        await using var server = new FakeCopilotServer();
        await Assert.ThrowsAsync<CopilotModelUnavailableException>(() => server.CreateTransport().ProcessAsync(
            server.Root, FakeTransport.Personal, "s", "private", "disabled", default));
        Assert.DoesNotContain(server.Requests, r => r.Method is "session.create" or "session.send");
    }

    [Fact]
    public async Task TurnsReuseVerifiedCatalogUntilExpiryFailureOrSettingsChange()
    {
        await using var server = new FakeCopilotServer();
        var clock = new ManualClock();
        var transport = server.CreateTransport(clock);
        Task<string> Turn() => transport.ProcessAsync(server.Root, FakeTransport.Personal, "s", "u", "model-a", default);
        await Turn();
        await Turn();
        Assert.Equal(1, server.Count("account.getAllUsers"));
        Assert.Equal(1, server.Count("models.list"));
        // The session account binding is still verified on every turn.
        Assert.Equal(2, server.Count("session.gitHubAuth.getStatus"));
        Assert.Equal(2, server.Count("session.send"));

        clock.Now += CopilotTransport.CatalogLifetime;
        await Turn();
        Assert.Equal(2, server.Count("models.list"));

        server.MismatchedSessionAccount = true;
        await Assert.ThrowsAsync<CopilotSignInRequiredException>(Turn);
        server.MismatchedSessionAccount = false;
        await Turn();
        Assert.Equal(3, server.Count("models.list"));

        transport.InvalidateCache();
        await Turn();
        Assert.Equal(4, server.Count("account.getAllUsers"));
        Assert.Equal(4, server.Count("models.list"));
        Assert.Equal(5, server.Count("session.send"));
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ChildEnvironmentPreservesProxySettingsWithoutAmbientTokenOrRuntimeOverrides()
    {
        var source = new Dictionary<string, string>
        {
            ["HTTPS_PROXY"] = "http://proxy.invalid:8080", ["HTTP_PROXY"] = "http://proxy.invalid:8080",
            ["NO_PROXY"] = "localhost,127.0.0.1", ["GH_TOKEN"] = "fixture-token", ["NODE_OPTIONS"] = "fixture-injection"
        };
        var environment = CopilotTransport.CreateClientOptions("fixture", key => source.GetValueOrDefault(key)).Environment!;
        Assert.Equal(3, environment.Count);
        Assert.Equal(source["HTTPS_PROXY"], environment["https_proxy"]);
        Assert.Equal(source["HTTP_PROXY"], environment["HTTP_PROXY"]);
        Assert.Equal(source["NO_PROXY"], environment["NO_PROXY"]);
        Assert.Empty(CopilotTransport.CreateClientOptions("fixture", _ => null).Environment!);
    }

    [Fact]
    public async Task SdkBindsSelectedAccountWithoutCopyingTokensOrChangingGlobalLogin()
    {
        await using var server = new FakeCopilotServer();
        await server.CreateTransport().ProcessAsync(server.Root, FakeTransport.Work, "s", "work text", "model-a", default);
        Assert.Equal("opaque-work", server.Parameters("models.list").GetProperty("selectionId").GetString());
        var credentials = server.Parameters("session.gitHubAuth.setCredentials").GetProperty("credentials");
        Assert.Equal("user", credentials.GetProperty("type").GetString());
        Assert.Equal("work", credentials.GetProperty("login").GetString());
        Assert.Equal("https://github.com", credentials.GetProperty("host").GetString());
        Assert.False(credentials.TryGetProperty("token", out _));
        Assert.DoesNotContain(server.Requests, r => r.Params.ValueKind != JsonValueKind.Undefined && r.Params.GetRawText().Contains("fixture-secret-never-forward"));
        Assert.DoesNotContain(server.Requests, r => r.Method is "account.login" or "account.logout");
        var methods = server.Requests.Select(r => r.Method).ToArray();
        Assert.True(Array.IndexOf(methods, "session.gitHubAuth.setCredentials") < Array.IndexOf(methods, "session.model.switchTo"));
        Assert.True(Array.IndexOf(methods, "session.gitHubAuth.getStatus") < Array.IndexOf(methods, "session.send"));
    }

    [Fact]
    public async Task SdkAccountDiscoveryReturnsOnlyPublicOAuthIdentities()
    {
        await using var server = new FakeCopilotServer();
        var accounts = await server.CreateTransport().GetAccountsAsync(server.Root, default);
        Assert.Equal(new[] { "personal", "work" }, accounts.Select(a => a.Login));
        Assert.DoesNotContain("fixture-secret", System.Text.Json.JsonSerializer.Serialize(accounts));
        Assert.DoesNotContain(server.Requests, r => r.Method == "session.create");
    }

    [Fact]
    public async Task MissingSelectedAccountStopsBeforeModelsOrSessionCreation()
    {
        await using var server = new FakeCopilotServer { MissingPersonal = true };
        await Assert.ThrowsAsync<CopilotSignInRequiredException>(() => server.CreateTransport().ProcessAsync(server.Root, FakeTransport.Personal, "s", "private", "model-a", default));
        Assert.DoesNotContain(server.Requests, r => r.Method is "models.list" or "session.create" or "session.send");
    }

    [Fact]
    public async Task MismatchedSessionAccountAbortsWithoutSendingText()
    {
        await using var server = new FakeCopilotServer { MismatchedSessionAccount = true };
        await Assert.ThrowsAsync<CopilotSignInRequiredException>(() => server.CreateTransport().ProcessAsync(server.Root, FakeTransport.Personal, "s", "private", "model-a", default));
        Assert.DoesNotContain(server.Requests, r => r.Method is "session.send" or "session.model.switchTo");
        Assert.Contains(server.Requests, r => r.Method == "session.abort");
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
    }

    [Fact]
    public async Task RealSdkSendsOnlyTextWithAllAgentFeaturesDisabledAndDeletesSession()
    {
        await using var server = new FakeCopilotServer();
        var transport = server.CreateTransport();
        var result = await transport.ProcessAsync(server.Root, FakeTransport.Personal, "Translate to German.", "Hello 世界\nline two", "model-a", default);
        Assert.Equal("Hallo Welt", result);
        var create = server.Parameters("session.create");
        Assert.False(create.TryGetProperty("model", out var initialModel) && initialModel.ValueKind != JsonValueKind.Null);
        Assert.Equal("model-a", server.Parameters("session.model.switchTo").GetProperty("modelId").GetString());
        Assert.Equal("replace", create.GetProperty("systemMessage").GetProperty("mode").GetString());
        Assert.Equal("Translate to German.", create.GetProperty("systemMessage").GetProperty("content").GetString());
        Assert.Empty(create.GetProperty("availableTools").EnumerateArray());
        Assert.Equal(new[] { "builtin:*", "custom:*", "mcp:*" }, create.GetProperty("excludedTools").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("excluded", create.GetProperty("toolFilterPrecedence").GetString());
        foreach (var flag in new[] { "enableConfigDiscovery", "enableOnDemandInstructionDiscovery", "enableSkills", "enableFileHooks", "enableHostGitOperations", "enableFileChangeTracking", "enableSessionStore", "enableSessionTelemetry" })
            Assert.False(create.GetProperty(flag).GetBoolean(), flag);
        Assert.False(create.GetProperty("infiniteSessions").GetProperty("enabled").GetBoolean());
        Assert.False(create.GetProperty("memory").GetProperty("enabled").GetBoolean());
        Assert.False(create.GetProperty("toolSearch").GetProperty("enabled").GetBoolean());
        var options = server.Parameters("session.options.update");
        Assert.True(options.GetProperty("skipCustomInstructions").GetBoolean());
        Assert.True(options.GetProperty("customAgentsLocalOnly").GetBoolean());
        Assert.False(options.GetProperty("manageScheduleEnabled").GetBoolean());
        var send = server.Parameters("session.send");
        Assert.Equal("Hello 世界\nline two", send.GetProperty("prompt").GetString());
        Assert.False(send.TryGetProperty("attachments", out var attachments) && attachments.ValueKind != JsonValueKind.Null);
        Assert.Contains(server.Requests, r => r.Method == "session.abort");
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
    }

    [Fact]
    public async Task SdkCatalogFiltersDisabledModelsAndDeduplicatesIds()
    {
        await using var server = new FakeCopilotServer();
        var models = await server.CreateTransport().GetModelsAsync(server.Root, FakeTransport.Personal, default);
        Assert.Equal("model-a", Assert.Single(models).Id);
        Assert.DoesNotContain(server.Requests, r => r.Method == "session.create");
    }

    [Theory]
    [InlineData(false, "user")]
    [InlineData(true, "env")]
    [InlineData(true, "token")]
    public async Task SdkRejectsSignedOutOrAmbientTokenAuthentication(bool authenticated, string authType)
    {
        await using var server = new FakeCopilotServer { Authenticated = authenticated, AuthType = authType };
        await Assert.ThrowsAsync<CopilotSignInRequiredException>(() => server.CreateTransport().GetModelsAsync(server.Root, FakeTransport.Personal, default));
        Assert.DoesNotContain(server.Requests, r => r.Method is "models.list" or "session.create");
    }

    [Fact]
    public async Task SdkCancellationAbortsAndDeletesRemoteSession()
    {
        await using var server = new FakeCopilotServer { HoldResponse = true };
        using var cancel = new CancellationTokenSource();
        var task = server.CreateTransport().ProcessAsync(server.Root, FakeTransport.Personal, "s", "u", "model-a", cancel.Token);
        await server.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(server.Requests, r => r.Method == "session.abort");
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
    }

    [Fact]
    public async Task SdkProviderErrorStillDeletesSession()
    {
        await using var server = new FakeCopilotServer { ErrorResponse = true };
        await Assert.ThrowsAnyAsync<Exception>(() => server.CreateTransport().ProcessAsync(server.Root, FakeTransport.Personal, "s", "u", "model-a", default));
        Assert.Contains(server.Requests, r => r.Method == "session.delete");
    }

    [Fact]
    public async Task EveryPermissionAndToolRequestIsDenied()
    {
        var config = CopilotTransport.CreateSessionConfig(Path.GetTempPath(), "s", "m");
#pragma warning disable GHCP001
        var decision = await config.OnPermissionRequest!(null!, null!);
        Assert.IsType<GitHub.Copilot.Rpc.PermissionDecisionReject>(decision);
#pragma warning restore GHCP001
        var hook = await config.Hooks!.OnPreToolUse!(null!, null!);
        Assert.Equal("deny", hook!.PermissionDecision);
        var client = CopilotTransport.CreateClientOptions(Path.GetTempPath());
        Assert.True(client.UseLoggedInUser);
        Assert.Null(client.GitHubToken);
        Assert.Null(client.Telemetry);
        Assert.False(client.EnableRemoteSessions);
        Assert.DoesNotContain("GITHUB_TOKEN", client.Environment!.Keys);
        Assert.DoesNotContain("GH_TOKEN", client.Environment.Keys);
        Assert.DoesNotContain("COPILOT_CLI_PATH", client.Environment.Keys);
        Assert.DoesNotContain("NODE_OPTIONS", client.Environment.Keys);
    }
}

/// <summary>A local LSP-framed JSON-RPC peer exercising the real SDK without credentials or paid calls.</summary>
internal sealed class FakeCopilotServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
    private readonly Task _run;
    internal readonly ConcurrentQueue<(string Method, JsonElement Params)> Requests = new();
    internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "copilot-wire-" + Guid.NewGuid().ToString("N"));
    internal bool Authenticated = true;
    internal string AuthType = "user";
    internal bool HoldResponse;
    internal bool ErrorResponse;
    internal string SelectedLogin = "personal";
    internal bool MismatchedSessionAccount;
    internal bool MissingPersonal;
    internal bool DeferredSwitch;
    internal bool MismatchedModel;

    internal FakeCopilotServer() { _listener.Start(); _run = RunAsync(); }

    internal CopilotTransport CreateTransport(TimeProvider? time = null) => new(options =>
    {
        options.Connection = RuntimeConnection.ForUri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        options.UseLoggedInUser = null; // External fake peer handles auth; production always uses owned stdio.
        return new CopilotClient(options);
    }, time);

    internal JsonElement Parameters(string method) => Requests.Single(r => r.Method == method).Params;

    private async Task RunAsync()
    {
        try
        {
            // Each turn owns and stops its runtime; serve those connections one after another.
            while (!_stop.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                try { await ServeAsync(socket.GetStream()); }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        // Stopping the listener while an accept is starting disposes its socket.
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    internal int Count(string method) => Requests.Count(r => r.Method == method);

    private async Task ServeAsync(NetworkStream stream)
    {
        await using (stream)
        {
            while (!_stop.IsCancellationRequested)
            {
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                    header.Append((char)one[0]);
                }
                var length = int.Parse(header.ToString().Split(':')[1].Trim());
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, _stop.Token);
                using var json = JsonDocument.Parse(body);
                var request = json.RootElement;
                if (!request.TryGetProperty("method", out var methodElement)) continue;
                var method = methodElement.GetString()!;
                var parameters = request.TryGetProperty("params", out var p) ? p.Clone() : default;
                if (parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() > 0) parameters = parameters[0];
                Requests.Enqueue((method, parameters));
                if (!request.TryGetProperty("id", out var id)) continue;
                if (method == "session.gitHubAuth.setCredentials")
                    SelectedLogin = parameters.GetProperty("credentials").GetProperty("login").GetString()!;
                object result = method switch
                {
                    "connect" => new { protocolVersion = 3 },
                    "auth.getStatus" => new { isAuthenticated = Authenticated, authType = AuthType },
                    "account.getAllUsers" => !Authenticated ? Array.Empty<object>() :
                        (MissingPersonal ? new[] { "work" } : new[] { "personal", "work" }).Select(login => (object)new
                        { authInfo = new { type = AuthType, host = "https://github.com", login, token = "fixture-secret-never-forward", envVar = "GH_TOKEN" }, selectionId = "opaque-" + login, token = "fixture-secret-never-forward" }).ToArray(),
                    "session.gitHubAuth.getStatus" => new { isAuthenticated = true, authType = "user", host = "https://github.com", login = MismatchedSessionAccount ? "different-account" : SelectedLogin },
                    "session.model.switchTo" => new { modelId = MismatchedModel ? "different-model" : parameters.GetProperty("modelId").GetString(), deferred = DeferredSwitch, status = "applied" },
                    "models.list" => new { models = new object[] { new { id = "model-a", name = "Model A" }, new { id = "model-a", name = "Duplicate" }, new { id = "disabled", name = "Disabled", policy = new { state = "disabled" } } } },
                    "session.create" => new { sessionId = parameters.GetProperty("sessionId").GetString() },
                    "session.send" => new { messageId = "message-1" },
                    _ => new { success = true }
                };
                if (method == "session.send" && ErrorResponse)
                    await WriteAsync(stream, new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32000, message = "Fixture provider failure" } });
                else await WriteAsync(stream, new { jsonrpc = "2.0", id = id.Clone(), result });
                if (method == "session.send")
                {
                    Sent.TrySetResult();
                    if (HoldResponse || ErrorResponse) continue;
                    var sessionId = parameters.GetProperty("sessionId").GetString();
                    await EventAsync(stream, sessionId, "assistant.message", new { content = "Hallo Welt", messageId = "answer-1" });
                    await EventAsync(stream, sessionId, "session.idle", new { });
                }
            }
        }
    }

    private Task EventAsync(NetworkStream stream, string? sessionId, string type, object data) => WriteAsync(stream,
        new { jsonrpc = "2.0", method = "session.event", @params = new { sessionId, @event = new { id = Guid.NewGuid().ToString(), timestamp = DateTimeOffset.UtcNow, parentId = (string?)null, type, data } } });

    private async Task WriteAsync(NetworkStream stream, object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), _stop.Token);
        await stream.WriteAsync(body, _stop.Token);
        await stream.FlushAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        await _run;
        _stop.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
