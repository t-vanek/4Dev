using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Protocol;
using TMap.Server.Map;
using TMap.Server.Net;
using TMap.Server.Security;
using Xunit;

namespace TMap.Server.Tests;

public sealed class PeerTransportTests
{
    private sealed class Certificates : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "4dev-map-tls-" + Guid.NewGuid().ToString("N"));
        private readonly List<X509Certificate2> _certificates = new();
        public readonly PeerTlsServerOptions Server;
        public readonly PeerTlsClientOptions Client;
        public Certificates(PeerRole role)
        {
            Directory.CreateDirectory(_root);
            var server = Create("server", true); var client = Create("client", false);
            Server = new() { CertificatePath = Path.Combine(_root, "server.pfx"), CertificatePassword = "test-only",
                Peers = { new() { Sha256 = client.GetCertHashString(HashAlgorithmName.SHA256), Role = role, ServerId = role == PeerRole.Map ? (ushort)0x0401 : (ushort)0 } } };
            Client = new() { CertificatePath = Path.Combine(_root, "client.pfx"), CertificatePassword = "test-only",
                ServerName = "localhost", ServerSha256 = server.GetCertHashString(HashAlgorithmName.SHA256) };
        }
        private X509Certificate2 Create(string name, bool server)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
                { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(Path.Combine(_root, name + ".pfx"), cert.Export(X509ContentType.Pfx, "test-only"));
            _certificates.Add(cert); return cert;
        }
        public void Dispose() { foreach (var c in _certificates) c.Dispose(); Directory.Delete(_root, true); }
    }

    [Fact]
    public async Task MatchingLegacyControlIpNeverGrantsPrivilegesOnGamePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            await using var connection = new ClientConnection(await listener.AcceptSocketAsync(), NullLogger.Instance);
            var options = new MapServerOptions { ControlIp = "127.0.0.1" };
            var service = new MapService(options, new(), new FakeWorldSink(), null, new(), NullLogger<MapService>.Instance);
            await service.OnClientConnectedAsync(connection);
            Assert.False(((ClientSession)connection.State!).IsControl);
            Assert.True(connection.UseCrypt);
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(PeerRole.Control, true)]
    [InlineData(PeerRole.Map, false)]
    public async Task DedicatedControlListenerRequiresControlCertificate(PeerRole role, bool allowed)
    {
        using var certificates = new Certificates(role);
        using var tls = new TlsPeerAuthenticator(certificates.Server);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = FreePort();
        var service = new MapService(new(), new(), new FakeWorldSink(), null, new(), NullLogger<MapService>.Instance);
        var listener = new ClientListener(NullLogger.Instance) { ControlAuthenticator = tls };
        var listen = listener.ListenAsync(port,
            (conn, packet) => new ValueTask(service.DispatchClientAsync((ClientSession)conn.State!, packet)),
            service.OnClientConnectedAsync, null, cts.Token);
        try
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            await using var stream = await PeerTls.ConnectAsync(client.Client, certificates.Client, cts.Token);
            var request = new PacketWriter(Msg.CT_SERVICEMONITOR_ACK).WriteUInt32(123).ToArray();
            await stream.WriteAsync(request, cts.Token);
            if (allowed)
            {
                var reply = new byte[PacketHeader.Size + 16];
                await stream.ReadExactlyAsync(reply, cts.Token);
                var r = new PacketReader(reply);
                Assert.Equal(Msg.CT_SERVICEMONITOR_REQ, r.Id);
                Assert.Equal(123u, r.ReadUInt32());
            }
            else
            {
                var error = await Record.ExceptionAsync(async () =>
                { await stream.ReadExactlyAsync(new byte[PacketHeader.Size], cts.Token); });
                Assert.NotNull(error);
                Assert.False(error is OperationCanceledException);
            }
        }
        finally { cts.Cancel(); await listen; }
    }

    [Fact]
    public async Task WorldLinkSendsAndReceivesOnlyAfterMutualTlsHandshake()
    {
        using var certificates = new Certificates(PeerRole.Map);
        using var tls = new TlsPeerAuthenticator(certificates.Server);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var link = new WorldLink("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, NullLogger.Instance, certificates.Client);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = link.RunAsync(() => { link.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x0401).WriteByte(0)); return ValueTask.CompletedTask; },
            p => { received.TrySetResult(p); return ValueTask.CompletedTask; }, () => ValueTask.CompletedTask, cts.Token);
        try
        {
            using var socket = await listener.AcceptSocketAsync(cts.Token);
            var peer = await tls.AuthenticateAsync(socket, cts.Token); await using var stream = peer.Stream;
            var packet = new byte[PacketHeader.Size + 3]; await stream.ReadExactlyAsync(packet, cts.Token);
            Assert.Equal(Msg.MW_CONNECT_ACK, PacketHeader.ReadId(packet));
            var response = new PacketWriter(Msg.MW_CHARDATA_REQ).WriteUInt32(20).WriteUInt32(30).ToArray();
            await stream.WriteAsync(response, cts.Token);
            Assert.Equal(response, await received.Task.WaitAsync(cts.Token));
        }
        finally { cts.Cancel(); listener.Stop(); await run; }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
}
