using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class GuildDestinationTests
{
    private static (MapTestHarness H, ClientSession S, FakeClientChannel C, TemplateStore T) Setup(uint guild = 10, bool restricted = true, bool returnSpawn = true)
    {
        var t = new TemplateStore();
        t.BattleZones[1] = new(1, "Castle", 801, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        t.Territories.Add(new(LocalType.Castle, 1, 0, 10, "Old", 0, 0, "", 0));
        var portal = new PortalRow(10, 3, 0, 1, 0);
        portal.Destinations[20] = new(20, 0, 1, [new(restricted ? (byte)8 : (byte)0, 0), default, default]);
        t.Portals[10] = portal; t.Portals[20] = new(20, 3, 1, 2, 0);
        if (returnSpawn) t.SpawnPositions[1] = new(1, 0, 10, 0, 10, 0);
        t.SpawnPositions[2] = new(2, 600, 100, 0, 100, 0);
        var h = new MapTestHarness(t); h.Service.InitTerritories();
        var c = new FakeClientChannel();
        var ch = new Character { CharId = 1, GuildId = guild, Country = 0, MapId = 600, Hp = 100, MaxHp = 100, PosX = 100, PosZ = 100 };
        ch.Persist.LastDestination = 10u | (20u << 16); ch.Persist.LastSpawnId = 1;
        var s = new ClientSession(c) { CharId = 1, Key = 11, IsMain = true, EntryValidated = true, Channel = 1, State = EnterState.Granted, Char = ch };
        h.State.Register(s); return (h, s, c, t);
    }
    [Theory]
    [InlineData(10u, true, false)] [InlineData(20u, true, true)] [InlineData(20u, false, false)]
    public async Task EntryRechecksTheGuildOfTheSavedPortalDestination(uint guild, bool restricted, bool returned)
    {
        var x = Setup(guild, restricted);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.ConReady());
        Assert.Equal(returned, x.S.TeleportPending);
        Assert.Equal(returned, x.H.World.Last(Msg.MW_BEGINTELEPORT_ACK) is not null);
    }
    [Fact]
    public async Task MissingReturnSpawnCannotAdmitAnUnauthorizedGuildRoom()
    {
        var x = Setup(20, returnSpawn: false);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.ConReady());
        Assert.True(x.C.Closed); Assert.Equal(EnterState.Entering, x.S.State);
    }
    [Theory]
    [InlineData((byte)1, 10u, true)] [InlineData((byte)1, 20u, false)] [InlineData((byte)0, 10u, false)]
    public async Task CastleCaptureEvictsOnlyAFormerOwnerFromItsGuildRoom(byte type, uint guild, bool returned)
    {
        var x = Setup(guild); x.S.State = EnterState.InGame; x.H.State.EnterWorld(x.S);
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_CASTLEOCCUPY_REQ).WriteByte(type).WriteUInt16(1)
            .WriteUInt32(20).WriteByte(1).WriteString("New").ToArray());
        Assert.Equal(returned, x.S.TeleportPending);
    }
    [Fact]
    public async Task ARegisteredOperatorKeepsTheNativeCastleEntryException()
    {
        var x = Setup(0); x.S.Char!.MapId = 801; x.S.Char.Persist.LastDestination = 0;
        // Operator membership is chart data, independent of the castle's guild/camp application.
        x.T.Operators.Add(1);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.ConReady());
        Assert.True(x.C.Has(Msg.CS_ENTERCASTLE_ACK)); Assert.False(x.S.TeleportPending);
    }
}
