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

public sealed class AuthorizationTests
{
    private sealed class AuthenticationStore(params LoginResult[] results) : ILoginAuthenticationStore
    {
        private int _index;
        public int PasswordResult = 1, Releases;
        public Task<int> CheckPasswordAsync(uint userId, string password, CancellationToken ct) => Task.FromResult(PasswordResult);
        public Task ReleaseCurrentUserAsync(uint userId, CancellationToken ct) { Releases++; return Task.CompletedTask; }
        public Task<int> CheckIpAsync(string ip, CancellationToken ct) => Task.FromResult(0);
        public Task<uint> ValidateCredentialsAsync(string user, string password, CancellationToken ct) => Task.FromResult(42u);
        public Task<LoginRow> LoginAsync(string user, string password, string ip, byte check, CancellationToken ct) =>
            Task.FromResult(new LoginRow((int)results[_index++], 987654, 123, 42, "127.0.0.1", 5816, 2, 0, 0));
        public Task<LoginRow> LoginJpAsync(string user, string password, string ip, byte check, byte channel, CancellationToken ct) =>
            LoginAsync(user, password, ip, check, ct);
    }

    private static PacketWriter Login() => new PacketWriter(Msg.CS_LOGIN_REQ).WriteUInt16(Proto.ClientVersion)
        .WriteString("").WriteString("password").WriteString("").WriteString("").WriteString("tester")
        .WriteInt64(0).WriteInt64(0);

    [Theory]
    [InlineData(LoginResult.Security)]
    [InlineData(LoginResult.Duplicate)]
    [InlineData(LoginResult.Block)]
    public async Task FailedOrSecondFactorPendingLoginDoesNotGrantIdentityOrExposeMapKey(LoginResult result)
    {
        await ExerciseAsync(new[] { result }, (client, state) =>
        {
            client.Send(Login());
            var reply = client.Receive(TimeSpan.FromSeconds(5));
            Assert.Equal(Msg.CS_LOGIN_ACK, reply.Id);
            Assert.Equal((byte)result, reply.ReadByte());
            Assert.Equal(0u, reply.ReadUInt32()); // account
            Assert.Equal(0u, reply.ReadUInt32()); // character
            Assert.Equal(0u, reply.ReadUInt32()); // login key
            Assert.False(state.LoggedIn);
            Assert.Empty(state.Password);
        });
    }

    [Fact]
    public async Task DuplicateRecoveryRequiresPasswordAndStillEnforcesSecondFactor()
    {
        var auth = new AuthenticationStore(LoginResult.Duplicate, LoginResult.Security) { PasswordResult = 0 };
        await ExerciseAsync(Array.Empty<LoginResult>(), (client, state) =>
        {
            client.Send(Login());
            var reply = client.Receive(TimeSpan.FromSeconds(5));
            Assert.Equal((byte)LoginResult.Security, reply.ReadByte());
            Assert.Equal(0u, reply.ReadUInt32());
            Assert.False(state.LoggedIn);
            Assert.Equal(1, auth.Releases);
        }, auth);
    }

    [Fact]
    public async Task FailedReloginClearsPreviousAccountAuthentication()
    {
        await ExerciseAsync(new[] { LoginResult.Success, LoginResult.Security }, (client, state) =>
        {
            client.Send(Login());
            var first = client.Receive(TimeSpan.FromSeconds(5));
            Assert.Equal((byte)LoginResult.Success, first.ReadByte());
            Assert.Equal(42u, first.ReadUInt32());
            client.Send(Login());
            var second = client.Receive(TimeSpan.FromSeconds(5));
            Assert.Equal((byte)LoginResult.Security, second.ReadByte());
            Assert.Equal(0u, second.ReadUInt32());
            Assert.False(state.LoggedIn);
            Assert.False(state.Agreement);
        });
    }

    private static async Task ExerciseAsync(LoginResult[] results, Action<TcpTestClient, LoginSessionState> exercise,
        AuthenticationStore? authentication = null)
    {
        var service = new LoginService(new GlobalDatabase("unused"), new Dictionary<byte, (GroupConfig, GameDatabase)>(),
            Nation.Us, new Dictionary<byte, uint>(), new AutoPassHwidValidator(),
            false, NullLogger<LoginService>.Instance,
            authentication: authentication ?? new AuthenticationStore(results));
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var connected = new TaskCompletionSource<LoginSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listen = new PacketServer(NullLogger<PacketServer>.Instance).ListenAsync(port, service.DispatchAsync,
            async conn => { await service.OnConnectedAsync(conn); connected.SetResult((LoginSessionState)conn.State!); }, cts.Token);
        try
        {
            using var client = new TcpTestClient("127.0.0.1", port);
            exercise(client, await connected.Task.WaitAsync(cts.Token));
        }
        finally { cts.Cancel(); try { await listen; } catch (OperationCanceledException) { } }
    }
}
