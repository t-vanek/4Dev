using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TLogin.Protocol;
using TLogin.Server.Net;
using Xunit;

namespace TLogin.Server.Tests;

public sealed class VerificationShutdownTests
{
    [Fact]
    public async Task ListenerShutdownWaitsForInFlightVerificationCleanup()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listen = new PacketServer(NullLogger.Instance).ListenAsync(port, async (connection, _) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, connection.StoppingToken); }
            catch (OperationCanceledException)
            { cleaning.TrySetResult(); await release.Task; }
        }, null, cts.Token);
        try
        {
            using var client = new TcpTestClient("127.0.0.1", port);
            client.Send(new PacketWriter(Msg.CS_SECURITYCONFIRM_ACK).WriteString("123456"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel(); await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(listen, await Task.WhenAny(listen, Task.Delay(100)));
        }
        finally { cts.Cancel(); release.TrySetResult(); await listen.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
