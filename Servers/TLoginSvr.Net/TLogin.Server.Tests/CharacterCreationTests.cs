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

public sealed class CharacterCreationTests
{
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 4)]
    [InlineData(255, 255)]
    public async Task NewAndLegacyClientsCreateNeutralLevelOneCharacters(byte requestedTier, byte requestedCountry)
    {
        await using var host = new Harness();
        using var client = await host.ConnectAsync();
        client.Send(Create(requestedTier, requestedCountry));
        var reply = ReadCreation(client);

        Assert.Equal(CreateResult.Success, reply.Result);
        Assert.Equal(123u, reply.CharId);
        Assert.Equal((byte)4, reply.Country);
        Assert.Equal((byte)1, reply.Level);
        Assert.Equal((byte)3, reply.Count);
        Assert.Equal((byte)3, host.State!.CreateCount);
        Assert.Equal(new CreateCharArgs("NewHero", 42, 7, 2, 3, 1, 4, 0, 1, 2, 3, 4, 5, 6, 0), host.Store.Last);
        Assert.Equal(1, host.Store.Calls);
    }

    [Fact]
    public async Task LegacyVeteranDialogOffersOnlyAnOrdinaryStart()
    {
        await using var host = new Harness();
        using var client = await host.ConnectAsync();
        client.Send(new PacketWriter(Msg.CS_VETERAN_REQ));
        var reply = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.CS_VETERAN_ACK, reply.Id);
        Assert.Equal((byte)0, reply.ReadByte()); // Legacy UI disables the second and third buttons.
        Assert.Equal((byte)1, reply.ReadByte());
        Assert.Equal((byte)0, reply.ReadByte());
        Assert.Equal((byte)0, reply.ReadByte());
        Assert.Equal(0, reply.Remaining);
        Assert.Equal(0, host.Store.Calls);
    }

    [Fact]
    public async Task RejectedCreationDoesNotAnnounceANewLevelOrChangeTheAccountCount()
    {
        await using var host = new Harness();
        host.Store.Result = new CreateCharRow((int)CreateResult.DupName, 0, 2);
        using var client = await host.ConnectAsync();
        client.Send(Create(3, 0));
        var reply = ReadCreation(client);
        Assert.Equal(CreateResult.DupName, reply.Result);
        Assert.Equal(0u, reply.CharId);
        Assert.Equal((byte)0, reply.Level);
        Assert.Equal((byte)2, host.State!.CreateCount);
    }

    [Fact]
    public async Task AnUnauthenticatedBoostRequestCannotReachCharacterStorage()
    {
        await using var host = new Harness(authenticated: false);
        using var client = await host.ConnectAsync();
        client.Send(Create(3, 0));
        client.Send(new PacketWriter(Msg.CS_TESTVERSION_REQ));
        Assert.Equal(Msg.CS_TESTVERSION_ACK, client.Receive(TimeSpan.FromSeconds(5)).Id);
        Assert.Equal(0, host.Store.Calls);
    }

    private static PacketWriter Create(byte tier, byte country) => new PacketWriter(Msg.CS_CREATECHAR_REQ)
        .WriteByte(7).WriteString("NewHero").WriteByte(2).WriteByte(3).WriteByte(1).WriteByte(country)
        .WriteByte(0).WriteByte(1).WriteByte(2).WriteByte(3).WriteByte(4).WriteByte(5).WriteByte(6).WriteByte(tier);

    private static (CreateResult Result, uint CharId, byte Country, byte Count, byte Level) ReadCreation(TcpTestClient client)
    {
        var reply = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.CS_CREATECHAR_ACK, reply.Id);
        var result = (CreateResult)reply.ReadByte();
        uint charId = reply.ReadUInt32();
        Assert.Equal("NewHero", reply.ReadString());
        Assert.Equal((byte)2, reply.ReadByte());
        Assert.Equal((byte)3, reply.ReadByte());
        Assert.Equal((byte)1, reply.ReadByte());
        byte country = reply.ReadByte();
        for (byte expected = 0; expected <= 6; expected++) Assert.Equal(expected, reply.ReadByte());
        byte count = reply.ReadByte(), level = reply.ReadByte();
        Assert.Equal(0, reply.Remaining);
        return (result, charId, country, count, level);
    }

    private sealed class Store : ICharacterCreationStore
    {
        public CreateCharArgs? Last;
        public int Calls;
        public CreateCharRow Result = new(0, 123, 3);
        public Task<CreateCharRow> CreateCharAsync(CreateCharArgs args, CancellationToken ct = default)
        {
            Calls++;
            Last = args;
            return Task.FromResult(Result);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
        private readonly Task _listen;
        private readonly TaskCompletionSource<LoginSessionState> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _port;
        public readonly Store Store = new();
        public LoginSessionState? State;

        public Harness(bool authenticated = true)
        {
            var groups = new Dictionary<byte, (GroupConfig, GameDatabase)>
            {
                [7] = (new GroupConfig(7, 0, "Test", "unused", "", ""), new GameDatabase("unused"))
            };
            var service = new LoginService(new GlobalDatabase("unused"), groups, Nation.Us,
                new Dictionary<byte, uint>(), new AutoPassHwidValidator(), false,
                NullLogger<LoginService>.Instance, characterCreation: Store);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); _port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            _listen = new PacketServer(NullLogger.Instance).ListenAsync(_port, service.DispatchAsync,
                async connection =>
                {
                    await service.OnConnectedAsync(connection);
                    State = (LoginSessionState)connection.State!;
                    State.UserId = authenticated ? 42u : 0;
                    State.GroupId = 7;
                    State.CreateCount = 2;
                    _connected.SetResult(State);
                }, _cts.Token);
        }

        public async Task<TcpTestClient> ConnectAsync()
        {
            var client = new TcpTestClient("127.0.0.1", _port);
            await _connected.Task.WaitAsync(_cts.Token);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _listen; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }
}
