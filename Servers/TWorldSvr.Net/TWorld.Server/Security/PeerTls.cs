using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TWorld.Server.Security;

public enum PeerRole { Map = 1, Control = 2 }
public sealed record PeerIdentity(PeerRole Role, ushort ServerId);
public sealed record AuthenticatedPeer(Stream Stream, PeerIdentity Identity);

public interface IPeerAuthenticator
{
    Task<AuthenticatedPeer> AuthenticateAsync(Socket socket, CancellationToken token);
}

public class PeerCertificateOptions
{
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
}

public sealed class PeerTlsClientOptions : PeerCertificateOptions
{
    public string ServerName { get; set; } = "";
    public string ServerSha256 { get; set; } = "";
}

public sealed class PeerCertificate
{
    public string Sha256 { get; set; } = "";
    public PeerRole Role { get; set; }
    public ushort ServerId { get; set; }
}

public sealed class PeerTlsServerOptions : PeerCertificateOptions
{
    public List<PeerCertificate> Peers { get; set; } = new();
}

// Kept identical (except namespace) in independently publishable server projects.
// Trust is an explicit SHA-256 leaf-certificate allowlist, with dates, usage and server name checked.
// TLS itself proves possession of the corresponding private key; no application packets precede it.
public sealed class TlsPeerAuthenticator : IPeerAuthenticator, IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly Dictionary<string, PeerIdentity> _peers = new(StringComparer.OrdinalIgnoreCase);

    public TlsPeerAuthenticator(PeerTlsServerOptions options)
    {
        foreach (var peer in options.Peers)
        {
            PeerTls.ValidatePin(peer.Sha256);
            if (peer.Role is not (PeerRole.Map or PeerRole.Control)
                || (peer.Role == PeerRole.Map && (peer.ServerId >> 8 != 4 || (peer.ServerId & 255) == 0))
                || (peer.Role == PeerRole.Control && peer.ServerId != 0)
                || !_peers.TryAdd(peer.Sha256, new(peer.Role, peer.ServerId)))
                throw new InvalidOperationException("Peer TLS identities must be unique and have a valid role/server id.");
        }
        if (_peers.Count == 0) throw new InvalidOperationException("Peer TLS allowlist is empty.");
        _certificate = PeerTls.LoadCertificate(options, server: true);
    }

    public async Task<AuthenticatedPeer> AuthenticateAsync(Socket socket, CancellationToken token)
    {
        PeerIdentity? identity = null;
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false,
            (_, certificate, _, errors) =>
            {
                if (!PeerTls.ValidCertificate(certificate, server: false, errors)) return false;
                var pin = certificate!.GetCertHashString(HashAlgorithmName.SHA256);
                return _peers.TryGetValue(pin, out identity);
            });
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false,
                AllowTlsResume = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, deadline.Token);
            if (identity is null || !tls.IsMutuallyAuthenticated)
                throw new AuthenticationException("Peer did not authenticate.");
            return new(tls, identity);
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    public void Dispose() => _certificate.Dispose();
}

public static class PeerTls
{
    public static async Task<SslStream> ConnectAsync(Socket socket, PeerTlsClientOptions options, CancellationToken token)
    {
        ValidatePin(options.ServerSha256);
        if (string.IsNullOrWhiteSpace(options.ServerName))
            throw new InvalidOperationException("Peer TLS server name is required.");
        using var certificate = LoadCertificate(options, server: false);
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false,
            (_, remote, _, errors) => ValidCertificate(remote, server: true, errors)
                && string.Equals(remote!.GetCertHashString(HashAlgorithmName.SHA256),
                    options.ServerSha256, StringComparison.OrdinalIgnoreCase));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.ServerName,
                ClientCertificates = new X509CertificateCollection { certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false,
                AllowTlsResume = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, deadline.Token);
            if (!tls.IsMutuallyAuthenticated) throw new AuthenticationException("Mutual peer TLS is required.");
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    internal static void ValidatePin(string pin)
    {
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit))
            throw new InvalidOperationException("A peer certificate needs a 64-character SHA-256 fingerprint.");
    }

    internal static X509Certificate2 LoadCertificate(PeerCertificateOptions options, bool server)
    {
        if (string.IsNullOrWhiteSpace(options.CertificatePath))
            throw new InvalidOperationException("Peer TLS certificate path is required.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath,
            options.CertificatePassword, OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        if (certificate.HasPrivateKey && ValidCertificate(certificate, server, SslPolicyErrors.None)) return certificate;
        certificate.Dispose();
        throw new InvalidOperationException("Peer TLS certificate needs a private key, current validity and the correct extended key usage.");
    }

    internal static bool ValidCertificate(X509Certificate? certificate, bool server, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
        using var cert = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var now = DateTime.UtcNow;
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime()) return false;
        string usage = server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2";
        return cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == usage));
    }
}
