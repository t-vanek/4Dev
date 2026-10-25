using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using TLogin.Data;
using TLogin.Protocol;
using TLogin.Server.Login;
using TLogin.Server.Net;
using TLogin.Server.Security;

namespace TLogin.Server.Tests;

internal sealed class EmailTwoFactorHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
    private readonly Task _listen;
    private readonly System.Threading.Channels.Channel<LoginSessionState> _connected =
        System.Threading.Channels.Channel.CreateUnbounded<LoginSessionState>();
    private readonly List<TcpTestClient> _clients = new();
    public readonly AuthenticationStore Authentication = new();
    public readonly ChallengeStore Store = new();
    public readonly Sender Email = new();
    public readonly LoginService Service;
    public readonly int Port;

    public EmailTwoFactorHarness()
    {
        var twoFactor = new EmailTwoFactorService(Store, Email, new(), NullLogger<EmailTwoFactorService>.Instance);
        Service = new LoginService(new GlobalDatabase("unused"), new Dictionary<byte, (GroupConfig, GameDatabase)>(),
            Nation.Us, Array.Empty<VeteranRow>(), new Dictionary<byte, uint>(), new AutoPassHwidValidator(), false,
            NullLogger<LoginService>.Instance, authentication: Authentication, twoFactor: twoFactor);
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        _listen = new PacketServer(NullLogger.Instance).ListenAsync(Port, Service.DispatchAsync,
            async connection => { await Service.OnConnectedAsync(connection); await _connected.Writer.WriteAsync((LoginSessionState)connection.State!); }, _cts.Token);
    }

    public async Task<(TcpTestClient Client, LoginSessionState State)> ConnectAsync()
    {
        var client = new TcpTestClient("127.0.0.1", Port); _clients.Add(client);
        return (client, await _connected.Reader.ReadAsync(_cts.Token));
    }

    public static PacketWriter LoginPacket(string user = "tester", string password = "password") =>
        new PacketWriter(Msg.CS_LOGIN_REQ).WriteUInt16(Proto.ClientVersion).WriteString("").WriteString(password)
            .WriteString("").WriteString("").WriteString(user).WriteInt64(0).WriteInt64(0);
    public static PacketWriter Confirm(string code) => new PacketWriter(Msg.CS_SECURITYCONFIRM_ACK).WriteString(code);
    public static PacketReader Read(TcpTestClient client) => client.Receive(TimeSpan.FromSeconds(5));

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients) client.Dispose();
        _cts.Cancel(); try { await _listen; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    internal sealed class AuthenticationStore : ILoginAuthenticationStore
    {
        public int Logins, Releases;
        public LoginResult Result = LoginResult.Success;
        public bool DuplicateOnce;
        public string CurrentPassword = "password";
        private static uint Id(string name) => name == "tester" ? 42u : name == "other" ? 43u : 0;
        public Task<uint> ValidateCredentialsAsync(string name, string password, CancellationToken ct) =>
            Task.FromResult(password == CurrentPassword ? Id(name) : 0);
        public Task<int> CheckIpAsync(string ip, CancellationToken ct) => Task.FromResult(0);
        public Task<int> CheckPasswordAsync(uint user, string password, CancellationToken ct) => Task.FromResult(password == CurrentPassword ? 0 : 1);
        public Task ReleaseCurrentUserAsync(uint user, CancellationToken ct) { Releases++; return Task.CompletedTask; }
        public Task<LoginRow> LoginAsync(string user, string password, string ip, byte check, CancellationToken ct)
        {
            Logins++;
            var result = password != CurrentPassword ? LoginResult.InvalidPasswd : DuplicateOnce && Logins == 1 ? LoginResult.Duplicate : Result;
            return Task.FromResult(new LoginRow((int)result, 987654, 123, Id(user), "127.0.0.1", 5816, 2, 0, 0));
        }
        public Task<LoginRow> LoginJpAsync(string user, string password, string ip, byte check, byte channel, CancellationToken ct) =>
            LoginAsync(user, password, ip, check, ct);
    }

    internal sealed class Sender : IEmailSender
    {
        public string Code = "";
        public string Recipient = "";
        public bool Fail;
        public int Calls;
        public Task SendSecurityCodeAsync(string email, string code, CancellationToken ct)
        {
            Calls++; Code = code; Recipient = email;
            return Fail ? Task.FromException(new IOException("SMTP rejected message")) : Task.CompletedTask;
        }
    }

    // Minimal storage double for encrypted wire tests. Real SQL tests cover atomicity and durable limits.
    internal sealed class ChallengeStore : ITwoFactorStore
    {
        public bool Required = true, Unavailable, DenyIssue, Delivered, FailDeliveryCommit;
        public int Invalidations, Consumes;
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        private uint _user;
        private Guid _session, _challenge;
        private byte[]? _hash;
        private DateTimeOffset _expires, _grant;
        public Task<bool> IsRequiredAsync(uint user, CancellationToken ct) => Unavailable
            ? Task.FromException<bool>(new IOException("database unavailable")) : Task.FromResult(Required);
        public Task<string?> TryIssueAsync(uint user, Guid session, Guid challenge, byte[] hash, TwoFactorLimits limits, CancellationToken ct)
        {
            if (DenyIssue) return Task.FromResult<string?>(null);
            _user = user; _session = session; _challenge = challenge; _hash = hash;
            _expires = Now.AddSeconds(limits.CodeLifetimeSeconds); _grant = default; Delivered = false;
            return Task.FromResult<string?>("owner@example.test");
        }
        private bool Matches(uint user, Guid session, Guid challenge) => _user == user && _session == session && _challenge == challenge;
        public Task<bool> MarkDeliveredAsync(uint user, Guid session, Guid challenge, CancellationToken ct)
        { Delivered = !FailDeliveryCommit && Matches(user, session, challenge); return Task.FromResult(Delivered); }
        public Task<bool> VerifyAsync(uint user, Guid session, Guid challenge, byte[] hash, TwoFactorLimits limits, CancellationToken ct)
        {
            if (Unavailable) return Task.FromException<bool>(new IOException("database unavailable"));
            bool accepted = Matches(user, session, challenge) && Delivered && _hash is not null && _expires > Now
                && CryptographicOperations.FixedTimeEquals(hash, _hash);
            if (accepted) { _hash = null; _grant = Now.AddSeconds(limits.GrantLifetimeSeconds); }
            return Task.FromResult(accepted);
        }
        public Task<bool> ConsumeAsync(uint user, Guid session, Guid challenge, CancellationToken ct)
        {
            Consumes++;
            bool accepted = Matches(user, session, challenge) && _grant > Now;
            if (accepted) _grant = default;
            return Task.FromResult(accepted);
        }
        public Task InvalidateAsync(uint user, Guid session, Guid challenge, CancellationToken ct)
        { Invalidations++; if (Matches(user, session, challenge)) { _hash = null; Delivered = false; _grant = default; } return Task.CompletedTask; }
    }
}
