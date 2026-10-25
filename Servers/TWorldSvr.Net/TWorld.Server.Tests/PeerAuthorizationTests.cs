using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TWorld.Protocol;
using TWorld.Server.Security;
using Xunit;

namespace TWorld.Server.Tests;

public sealed class PeerAuthorizationTests
{
    private sealed class Certificates : IDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "4dev-peer-" + Guid.NewGuid().ToString("N"));
        public readonly string Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public readonly X509Certificate2 Server, Map, Control, Unknown;
        public Certificates()
        {
            Directory.CreateDirectory(DirectoryPath);
            Server = Create("world", server: true);
            Map = Create("map", server: false);
            Control = Create("control", server: false);
            Unknown = Create("unknown", server: false);
        }
        private X509Certificate2 Create(string name, bool server)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
                { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            if (server) { var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build()); }
            var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(Path.Combine(DirectoryPath, name + ".pfx"), cert.Export(X509ContentType.Pfx, Password));
            return cert;
        }
        public PeerTlsServerOptions ServerOptions => new()
        {
            CertificatePath = Path.Combine(DirectoryPath, "world.pfx"), CertificatePassword = Password,
            Peers = new()
            {
                new() { Sha256 = Pin(Map), Role = PeerRole.Map, ServerId = 0x0401 },
                new() { Sha256 = Pin(Control), Role = PeerRole.Control },
            },
        };
        public PeerTlsClientOptions Client(string name) => new()
        {
            CertificatePath = Path.Combine(DirectoryPath, name + ".pfx"), CertificatePassword = Password,
            ServerName = "localhost", ServerSha256 = Pin(Server),
        };
        public static string Pin(X509Certificate2 c) => c.GetCertHashString(HashAlgorithmName.SHA256);
        public void Dispose()
        {
            Server.Dispose(); Map.Dispose(); Control.Dispose(); Unknown.Dispose();
            Directory.Delete(DirectoryPath, true);
        }
    }

    [Fact]
    public void MissingTlsConfigurationFailsClosed() =>
        Assert.Throws<InvalidOperationException>(() => new TlsPeerAuthenticator(new()));

    [Fact]
    public async Task AuthenticatedMapCanRegisterAndEnterButCannotBecomeControl()
    {
        using var certs = new Certificates();
        using var tls = new TlsPeerAuthenticator(certs.ServerOptions);
        await using var host = new WorldTestHost(authenticator: tls, enforceAuthorization: true);
        using var client = await host.ConnectAsync(certs.Client("map"));
        client.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x0401).WriteByte(1).WriteByte(1));
        client.Send(new PacketWriter(Msg.MW_ADDCHAR_ACK).WriteUInt32(20).WriteUInt32(30)
            .WriteUInt32(0x0100007F).WriteUInt16(5816).WriteUInt32(10));
        Assert.Equal(Msg.MW_ENTERSVR_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);
        client.Send(new PacketWriter(Msg.CT_CTRLSVR_REQ));
        Assert.ThrowsAny<IOException>(() => client.Receive(TimeSpan.FromSeconds(5)));
        Assert.Null(host.State.ControlServer);
    }

    [Fact]
    public async Task AuthenticatedControlMustRegisterAndCannotImpersonateMap()
    {
        using var certs = new Certificates();
        using var tls = new TlsPeerAuthenticator(certs.ServerOptions);
        await using var host = new WorldTestHost(authenticator: tls, enforceAuthorization: true);
        using var client = await host.ConnectAsync(certs.Client("control"));
        client.Send(new PacketWriter(Msg.CT_CTRLSVR_REQ));
        var ev = new TWorld.Server.World.EventInfo { Index = 100, Id = 1, State = 1, Title = "test" };
        var update = new PacketWriter(Msg.CT_EVENTUPDATE_REQ).WriteByte(1).WriteUInt16(7);
        ev.WriteTo(update); client.Send(update);
        client.Send(new PacketWriter(Msg.SM_BATTLESTATUS_REQ).WriteByte(0).WriteByte(0).WriteUInt32(0).WriteUInt32(0));
        client.Send(new PacketWriter(Msg.CT_SERVICEMONITOR_ACK).WriteUInt32(123));
        var reply = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.CT_SERVICEMONITOR_REQ, reply.Id);
        Assert.Equal(123u, reply.ReadUInt32());
        Assert.True(host.State.EventInfos.ContainsKey(100));
        client.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x0401).WriteByte(0));
        Assert.ThrowsAny<IOException>(() => client.Receive(TimeSpan.FromSeconds(5)));
        Assert.Empty(host.State.Servers);
    }

    [Fact]
    public async Task MapCertificateIsBoundToConfiguredServerIdentity()
    {
        using var certs = new Certificates();
        using var tls = new TlsPeerAuthenticator(certs.ServerOptions);
        await using var host = new WorldTestHost(authenticator: tls, enforceAuthorization: true);
        using var client = await host.ConnectAsync(certs.Client("map"));
        client.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x0402).WriteByte(0));
        Assert.ThrowsAny<IOException>(() => client.Receive(TimeSpan.FromSeconds(5)));
        Assert.Empty(host.State.Servers);
    }

    [Theory]
    [InlineData("unknown", false, false)]
    [InlineData("map", true, false)]
    [InlineData("map", false, true)]
    public async Task UnknownClientWrongServerPinOrWrongServerNameCannotDispatch(string name, bool wrongPin, bool wrongName)
    {
        using var certs = new Certificates();
        using var tls = new TlsPeerAuthenticator(certs.ServerOptions);
        await using var host = new WorldTestHost(authenticator: tls, enforceAuthorization: true);
        var options = certs.Client(name);
        if (wrongPin) options.ServerSha256 = Certificates.Pin(certs.Unknown);
        if (wrongName) options.ServerName = "other.invalid";
        var error = await Record.ExceptionAsync(async () =>
        {
            using var client = await host.ConnectAsync(options);
            client.Send(new PacketWriter(Msg.CT_CTRLSVR_REQ));
            client.Receive(TimeSpan.FromSeconds(5));
        });
        Assert.NotNull(error);
        Assert.Null(host.State.ControlServer);
        Assert.Empty(host.State.Servers);
    }

    [Fact]
    public async Task PlainTcpCannotRegisterAsControl()
    {
        using var certs = new Certificates();
        using var tls = new TlsPeerAuthenticator(certs.ServerOptions);
        await using var host = new WorldTestHost(authenticator: tls, enforceAuthorization: true);
        using var client = await host.ConnectAsync();
        client.Send(new PacketWriter(Msg.CT_CTRLSVR_REQ));
        Assert.NotNull(Record.Exception(() => client.Receive(TimeSpan.FromSeconds(3))));
        Assert.Null(host.State.ControlServer);
    }
}
