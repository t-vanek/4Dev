using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TLogin.Data;
using TLogin.Protocol;
using TLogin.Server.Login;
using TLogin.Server.Net;
using TLogin.Server.Security;
using Xunit;

namespace TLogin.Server.Tests;

/// <summary>
/// End-to-end tests that boot the real <see cref="PacketServer"/> + <see cref="LoginService"/> and drive
/// a TCP client through the encrypted pipeline. The version-reject path runs before any DB access, so
/// these need no database — they validate framing, the asymmetric cipher, dispatch and the LOGIN_ACK
/// layout together.
/// </summary>
public class EndToEndLoginTests
{
    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static LoginService BuildService()
    {
        // The dummy connection string is never used on the version-reject path.
        var global = new GlobalDatabase("Server=localhost;Database=none;Integrated Security=false;User ID=x;Password=x;TrustServerCertificate=True");
        return new LoginService(
            global,
            new Dictionary<byte, (GroupConfig, GameDatabase)>(),
            Nation.Us,
            Array.Empty<VeteranRow>(),
            new Dictionary<byte, uint>(),
            new AutoPassHwidValidator(),
            validateChecksum: true,
            NullLogger<LoginService>.Instance);
    }

    [Fact]
    public async Task WrongVersion_GetsLoginAckWithVersionError()
    {
        int port = FreeTcpPort();
        var svc = BuildService();
        var server = new PacketServer(NullLogger<PacketServer>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listen = server.ListenAsync(port, svc.DispatchAsync, svc.OnConnectedAsync, cts.Token);

        // Wait until the listener is accepting.
        using var client = await ConnectWithRetryAsync("127.0.0.1", port, cts.Token);

        var w = new PacketWriter(Msg.CS_LOGIN_REQ);
        w.WriteUInt16(0x1234);            // wrong version (TVERSION is 0x2918)
        w.WriteString("z3");
        w.WriteString("pw");
        w.WriteString("z1");
        w.WriteString("z2");
        w.WriteString("tester");
        w.WriteInt64(0);
        w.WriteInt64(0);
        client.Send(w);

        var r = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.CS_LOGIN_ACK, r.Id);
        Assert.Equal((byte)LoginResult.Version, r.ReadByte());

        cts.Cancel();
        try { await listen; } catch (OperationCanceledException) { }
    }

    private static async Task<TcpTestClient> ConnectWithRetryAsync(string host, int port, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try { return new TcpTestClient(host, port); }
            catch (SocketException) { await Task.Delay(50, ct); }
        }
        throw new TimeoutException("Server did not start listening.");
    }
}
