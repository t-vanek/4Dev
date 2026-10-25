using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class EntryAuthorizationTests
{
    private sealed class EntryStore : IMapEntryStore
    {
        public bool Available = true, EnterAllowed = true;
        public int EnterCalls;
        public Task<bool> ValidateAsync(MapEntry e) => Available
            ? Task.FromResult(e.UserId == 10 && e.CharId == 20 && e.Key == 30)
            : throw new IOException("database unavailable");
        public Task<bool> EnterAsync(MapEntry e) { EnterCalls++; return Task.FromResult(EnterAllowed); }
    }

    [Theory]
    [InlineData(11u, 20u, 30u)]
    [InlineData(10u, 21u, 30u)]
    [InlineData(10u, 20u, 31u)]
    public async Task PublicChecksumDoesNotAuthorizeForeignAccountCharacterOrKey(uint user, uint character, uint key)
    {
        var h = new MapTestHarness(entryStore: new EntryStore());
        var client = new FakeClientChannel();
        var session = new ClientSession(client);
        await h.Service.DispatchClientAsync(session, MapTestHarness.ConnectReq(character, user, key));
        Assert.True(client.Closed);
        Assert.Null(session.Char);
        Assert.Empty(h.State.ByChar);
        Assert.False(h.World.Has(Msg.MW_ADDCHAR_ACK));
    }

    [Fact]
    public async Task MissingOrFailedDatabaseDeniesEntry()
    {
        var channel = new FakeClientChannel();
        var service = new MapService(new(), new(), new FakeWorldSink(), null, new(), NullLogger<MapService>.Instance);
        await service.DispatchClientAsync(new ClientSession(channel), MapTestHarness.ConnectReq(20, 10, 30));
        Assert.True(channel.Closed);
        var h = new MapTestHarness(entryStore: new EntryStore { Available = false });
        var failed = new FakeClientChannel();
        await h.Service.DispatchClientAsync(new ClientSession(failed), MapTestHarness.ConnectReq(20, 10, 30));
        Assert.True(failed.Closed);
        Assert.Empty(h.State.ByChar);
    }

    [Fact]
    public async Task WorldCannotGrantEntryWithoutSuccessfulProcedureOrWithAnotherKey()
    {
        var store = new EntryStore { EnterAllowed = false };
        var h = new MapTestHarness(entryStore: store);
        var client = new FakeClientChannel();
        var s = new ClientSession(client);
        await h.Service.DispatchClientAsync(s, MapTestHarness.ConnectReq(20, 10, 30));
        await h.Service.DispatchWorldAsync(MapTestHarness.ConResultReq(20, 30, ConnectResult.Success));
        Assert.Equal(EnterState.Entering, s.State);
        await h.Service.DispatchWorldAsync(MapTestHarness.EnterSvrReq(20, 31));
        Assert.Equal(0, store.EnterCalls);
        await h.Service.DispatchWorldAsync(MapTestHarness.EnterSvrReq(20, 30));
        Assert.Equal(1, store.EnterCalls);
        Assert.True(client.Closed);
        Assert.False(h.World.Has(Msg.MW_ENTERSVR_ACK));
        Assert.Empty(h.State.ByChar);
    }

    [Fact]
    public async Task SecondaryMapEntryUsesWorldAuthorizationWithoutReplacingMainDatabaseRoute()
    {
        var store = new EntryStore { EnterAllowed = false };
        var h = new MapTestHarness(entryStore: store);
        var client = new FakeClientChannel();
        var session = new ClientSession(client);
        await h.Service.DispatchClientAsync(session, MapTestHarness.ConnectReq(20, 10, 30));
        await h.Service.DispatchWorldAsync(MapTestHarness.EnterCharReq(20, 30, "Hero", 0, 1, 0, 1));
        await h.Service.DispatchWorldAsync(MapTestHarness.ConResultReq(20, 30, ConnectResult.Success));
        await h.Service.DispatchClientAsync(session, MapTestHarness.ConReady());
        Assert.Equal(EnterState.InGame, session.State);
        Assert.Equal(0, store.EnterCalls);
        Assert.False(client.Closed);
        // Promotion to main must run TEnterServer even though this socket was already in game.
        store.EnterAllowed = true;
        await h.Service.DispatchWorldAsync(MapTestHarness.EnterSvrReq(20, 30));
        Assert.Equal(1, store.EnterCalls);
        Assert.True(h.World.Has(Msg.MW_ENTERSVR_ACK));
    }

    [Fact]
    public async Task RevokedCredentialsCannotEnterThroughSecondaryMapHandshake()
    {
        var store = new EntryStore();
        var h = new MapTestHarness(entryStore: store);
        var client = new FakeClientChannel();
        var session = new ClientSession(client);
        await h.Service.DispatchClientAsync(session, MapTestHarness.ConnectReq(20, 10, 30));
        store.Available = false;
        await h.Service.DispatchWorldAsync(MapTestHarness.EnterCharReq(20, 30, "Hero", 0, 1, 0, 1));
        Assert.True(client.Closed);
        Assert.Empty(h.State.ByChar);
        Assert.False(h.World.Has(Msg.MW_ENTERCHAR_ACK));
    }

    [Fact]
    public async Task ValidEntryCallsProcedureOnceAndStillCompletesExistingHandshake()
    {
        var store = new EntryStore();
        var h = new MapTestHarness(entryStore: store);
        var (session, client) = await h.EnterAsync(20, 10, 30);
        Assert.Equal(1, store.EnterCalls);
        Assert.Equal(EnterState.InGame, session.State);
        Assert.False(client.Closed);
    }
}
