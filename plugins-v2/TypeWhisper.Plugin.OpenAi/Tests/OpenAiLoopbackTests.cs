using System.Net;
using System.Net.Sockets;
using System.Text;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiLoopbackTests
{
    [Fact]
    public async Task ProviderDenialEndsLoginAndReleasesTheListeners()
    {
        await using var server = new OpenAiLoopbackOAuthServer("fixture-state", 0);
        server.Start();
        var port = server.Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var callback = server.WaitForCodeAsync(timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await client.GetStream().WriteAsync("GET /auth/callback?state=fixture-state&error=access_denied HTTP/1.1\r\n\r\n"u8.ToArray(), timeout.Token);
        using var reader = new StreamReader(client.GetStream());
        Assert.Contains("Login failed", await reader.ReadToEndAsync(timeout.Token));
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => callback.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind);
        AssertPortsReleased(port);
    }

    [Fact]
    public async Task SimultaneousLoopbackConnectionsPreserveTheValidCallback()
    {
        if (!Socket.OSSupportsIPv6) return;
        await using var server = new OpenAiLoopbackOAuthServer("fixture-state", 0);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var preconnect = new TcpClient(AddressFamily.InterNetwork);
        using var redirect = new TcpClient(AddressFamily.InterNetworkV6);
        await Task.WhenAll(preconnect.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token).AsTask(),
            redirect.ConnectAsync(IPAddress.IPv6Loopback, server.Port, timeout.Token).AsTask());
        await redirect.GetStream().WriteAsync(Encoding.UTF8.GetBytes("GET /auth/callback?state=fixture-state&code=valid HTTP/1.1\r\n\r\n"), timeout.Token);
        Assert.Equal("valid", await server.WaitForCodeAsync(timeout.Token));
    }

    [Fact]
    public async Task IdlePreconnectDoesNotBlockTheRealCallback()
    {
        await using var server = new OpenAiLoopbackOAuthServer("fixture-state", 0);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var callback = server.WaitForCodeAsync(timeout.Token);
        using var preconnect = new TcpClient();
        await preconnect.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        // Wait for the idle request deadline, rather than racing two accepts.
        var byteBuffer = new byte[1];
        Assert.Equal(0, await preconnect.GetStream().ReadAsync(byteBuffer, timeout.Token));
        using var redirect = new TcpClient();
        await redirect.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        await redirect.GetStream().WriteAsync(Encoding.UTF8.GetBytes("GET /auth/callback?state=fixture-state&code=valid HTTP/1.1\r\n\r\n"), timeout.Token);
        Assert.Equal("valid", await callback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackAcceptsBothLocalhostAddresses(bool ipv6)
    {
        if (ipv6 && !Socket.OSSupportsIPv6) return;
        await using var server = new OpenAiLoopbackOAuthServer("fixture-state", 0);
        server.Start();
        var port = server.Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var callback = server.WaitForCodeAsync(timeout.Token);
        using var client = new TcpClient(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        await client.ConnectAsync(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, port, timeout.Token);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("GET /auth/callback?state=fixture-state&code=fixture-code HTTP/1.1\r\n\r\n"), timeout.Token);
        using var reader = new StreamReader(client.GetStream());
        Assert.Contains("Login complete", await reader.ReadToEndAsync(timeout.Token));
        Assert.Equal("fixture-code", await callback);
        AssertPortsReleased(port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReleasesBothListeners(bool connectWithoutCompletingRequest)
    {
        await using var server = new OpenAiLoopbackOAuthServer("fixture-state", 0);
        server.Start();
        var port = server.Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var callback = server.WaitForCodeAsync(cancelled.Token);
        using var client = new TcpClient();
        if (connectWithoutCompletingRequest)
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await client.GetStream().WriteAsync("GET /auth"u8.ToArray(), timeout.Token);
        }
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callback);
        AssertPortsReleased(port);
    }

    [Theory]
    [InlineData("GET /auth/callback?state=wrong&code=fixture HTTP/1.1\r\n\r\n")]
    [InlineData("GET /auth/callback?state=wrong&error=access_denied HTTP/1.1\r\n\r\n")]
    [InlineData(null)]
    public async Task InvalidCallbackDoesNotConsumeLoginAttempt(string? request)
    {
        await using var server = new OpenAiLoopbackOAuthServer("fixture-state", 0);
        server.Start();
        var port = server.Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var callback = server.WaitForCodeAsync(timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        request ??= new string('x', 8192);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token);
        using (var reader = new StreamReader(client.GetStream()))
            await reader.ReadToEndAsync(timeout.Token);
        Assert.False(callback.IsCompleted);
        using var redirect = new TcpClient();
        await redirect.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await redirect.GetStream().WriteAsync(Encoding.UTF8.GetBytes("GET /auth/callback?state=fixture-state&code=valid HTTP/1.1\r\n\r\n"), timeout.Token);
        Assert.Equal("valid", await callback);
        AssertPortsReleased(port);
    }

    private static void AssertPortsReleased(int port)
    {
        using var ipv4 = new TcpListener(IPAddress.Loopback, port);
        ipv4.Start();
        if (!Socket.OSSupportsIPv6) return;
        using var ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
        ipv6.Server.DualMode = false;
        ipv6.Start();
    }
}
