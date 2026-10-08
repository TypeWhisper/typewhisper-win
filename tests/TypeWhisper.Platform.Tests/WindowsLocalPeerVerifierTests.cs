using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;
using Xunit;
using Xunit.Abstractions;

public sealed class WindowsLocalPeerVerifierTests(ITestOutputHelper output)
{
    // Real loopback sockets of this process stand in for an API client; the verifier must find the peer socket's
    // row in the live TCP table and resolve it to the test runner, which runs as the current user.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnConnectionIsRecognizedWhileUnknownClosedOrMismatchedPeersAreRejected(bool ipv6)
    {
        var loopback = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        var listener = new TcpListener(loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient(loopback.AddressFamily);
        await client.ConnectAsync(loopback, port);
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var peer = (IPEndPoint)accepted.Client.RemoteEndPoint!;
        var verifier = new WindowsLocalPeerVerifier();
        Assert.True(verifier.IsOwnUser(peer, port));
        // A row of another connection must not vouch for this one, and a never-connected peer has no owner.
        Assert.False(verifier.IsOwnUser(peer, port == 65535 ? 65534 : port + 1));
        Assert.False(verifier.IsOwnUser(new IPEndPoint(loopback, UnusedPort(loopback)), port));

        const int iterations = 200;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) Assert.True(verifier.IsOwnUser(peer, port));
        var perCall = watch.Elapsed / iterations;
        output.WriteLine($"Peer check over {(ipv6 ? "IPv6" : "IPv4")}: {perCall.TotalMicroseconds:F0} us per call");
        Assert.True(perCall < TimeSpan.FromMilliseconds(5), $"Peer check took {perCall.TotalMilliseconds:F2} ms per call.");

        // An abortive close drops the connection without TIME_WAIT, so the row disappears at once.
        client.LingerState = new LingerOption(true, 0);
        client.Close();
        accepted.Close();
        listener.Stop();
        Assert.False(verifier.IsOwnUser(peer, port));
    }

    // http.sys owns the server side of a connection, so this proves the peer side it reports resolves to us
    // for both listener names, including an IPv6 loopback connection to localhost.
    [Fact]
    public async Task RealListenerAdmitsOwnProcessOverBothLoopbackNames()
    {
        var port = UnusedPort(IPAddress.Loopback);
        await using var server = new LocalHttpApi(port, "a7d18284e6504fe2a1cc070c62850709", (_, _) =>
            Task.FromResult(LocalApiResponse.Json(200, new { })), requireAuthentication: false, peerVerifier: new WindowsLocalPeerVerifier());
        await server.StartAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        foreach (var host in new[] { "127.0.0.1", "localhost" })
        {
            using var response = await client.GetAsync($"http://{host}:{port}/v1/models");
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{host}: {response.StatusCode}");
        }
    }

    private static int UnusedPort(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
