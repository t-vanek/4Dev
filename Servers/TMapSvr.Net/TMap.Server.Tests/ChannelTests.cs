using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class ChannelTests
{
    private static byte[] Request(byte channel) => new PacketWriter(Msg.CS_CHGCHANNEL_REQ).WriteByte(channel).ToArray();

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c)> Setup(ushort map = 0, bool worldConnected = true)
    {
        var h = new MapTestHarness();
        h.Options.Channels = new byte[] { 1, 2, 3 };
        if (worldConnected) await h.Service.OnWorldConnectedAsync();
        var hero = new Character { CharId = 1, Name = "Hero", Hp = 73, MaxHp = 100, Mp = 80, MaxMp = 100 };
        var (s, c) = await h.EnterAsync(1, 10, 123, x: 100, y: 4, z: 100, mapId: map, preSeeded: hero);
        c.Clear(); h.World.Clear();
        return (h, s, c);
    }

    private static PacketWriter PositionPacket(ushort message, ClientSession s, byte channel)
    {
        var ch = s.Char!;
        return new PacketWriter(message).WriteUInt32(s.CharId).WriteUInt32(s.Key).WriteByte(channel)
            .WriteUInt16(ch.MapId).WriteFloat(ch.PosX).WriteFloat(ch.PosY).WriteFloat(ch.PosZ);
    }

    private static async Task Complete(MapTestHarness h, ClientSession s, byte channel)
    {
        await h.Service.DispatchWorldAsync(PositionPacket(Msg.MW_STARTTELEPORT_REQ, s, channel).ToArray());
        await h.FinishTeleportAsync(s);
    }

    private static void AssertRefused(ClientSession s, FakeClientChannel c, MapTestHarness h)
    {
        var reply = new PacketReader(Assert.Single(c.WithId(Msg.CS_CHGCHANNEL_ACK)));
        Assert.Equal(2, reply.ReadByte()); // CHC_NOCHANNEL
        Assert.Equal(1, reply.ReadByte());
        Assert.Equal(0, reply.Remaining);
        Assert.Empty(h.World.Sent);
        Assert.False(c.Has(Msg.CS_BEGINTELEPORT_ACK));
        Assert.Equal((byte)1, s.Channel);
        Assert.Equal(EnterState.InGame, s.State);
        Assert.False(s.TeleportPending);
        Assert.NotNull(s.Grid);
        Assert.Equal(73u, s.Char!.Hp);
    }

    [Fact]
    public async Task AChannelChangeStartsTheWorldCycle_WithTheSameMapAndPosition()
    {
        var (h, s, c) = await Setup();

        await h.Service.DispatchClientAsync(s, Request(2));

        var world = new PacketReader(Assert.Single(h.World.Sent));
        Assert.Equal(Msg.MW_BEGINTELEPORT_ACK, world.Id);
        Assert.Equal(1u, world.ReadUInt32());
        Assert.Equal(123u, world.ReadUInt32());
        Assert.Equal(0, world.ReadByte()); // full transfer, not a shared-map channel-label update
        Assert.Equal(2, world.ReadByte());
        Assert.Equal(0, world.ReadUInt16());
        Assert.Equal((100f, 4f, 100f), (world.ReadFloat(), world.ReadFloat(), world.ReadFloat()));
        Assert.Equal(0, world.Remaining);
        var client = new PacketReader(Assert.Single(c.Sent));
        Assert.Equal(Msg.CS_BEGINTELEPORT_ACK, client.Id);
        Assert.Equal(2, client.ReadByte());
        Assert.Equal(0, client.ReadUInt16());
        Assert.Equal(0, client.Remaining);
        Assert.True(s.TeleportPending);
        Assert.Equal((byte)1, s.Channel); // stays in its old grid until STARTTELEPORT
    }

    [Fact]
    public async Task SelectingTheCurrentChannelIsANoOp()
    {
        var (h, s, c) = await Setup();
        await h.Service.DispatchClientAsync(s, Request(1));
        Assert.Empty(c.Sent);
        Assert.Empty(h.World.Sent);
        Assert.False(s.TeleportPending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public async Task AnUnconfiguredChannelIsRejectedBeforeLeaving(byte channel)
    {
        var (h, s, c) = await Setup();
        await h.Service.DispatchClientAsync(s, Request(channel));
        AssertRefused(s, c, h);
    }

    [Fact]
    public async Task ADisconnectedWorldIsRejectedBeforeLoading()
    {
        var (h, s, c) = await Setup(worldConnected: false);
        await h.Service.DispatchClientAsync(s, Request(2));
        AssertRefused(s, c, h);
    }

    [Theory]
    [InlineData(1101)]
    [InlineData(1105)]
    public async Task APrivateMeetingRoomDoesNotAllowChannelChanges(ushort map)
    {
        var (h, s, c) = await Setup(map);
        await h.Service.DispatchClientAsync(s, Request(2));
        AssertRefused(s, c, h);
    }

    [Fact]
    public async Task AnArenaParticipantCannotChangeChannel()
    {
        var (h, s, c) = await Setup();
        s.Char!.ArenaId = 42;
        await h.Service.DispatchClientAsync(s, Request(2));
        AssertRefused(s, c, h);
        Assert.Equal((ushort)42, s.Char.ArenaId);
    }

    [Theory]
    [InlineData(EnterState.Connected, true)]
    [InlineData(EnterState.Entering, true)]
    [InlineData(EnterState.Granted, true)]
    [InlineData(EnterState.InGame, false)]
    public async Task OnlyALiveMainSessionCanRequestTheChange(EnterState state, bool main)
    {
        var (h, s, c) = await Setup();
        s.State = state; s.IsMain = main;
        await h.Service.DispatchClientAsync(s, Request(2));
        Assert.Empty(c.Sent);
        Assert.Empty(h.World.Sent);
        Assert.Equal((byte)1, s.Channel);
    }

    [Fact]
    public async Task RepeatedRequestsAndPrematureReadyCannotBypassTheTransfer()
    {
        var (h, s, c) = await Setup();
        await h.Service.DispatchClientAsync(s, Request(2));
        await h.Service.DispatchClientAsync(s, Request(3));
        await h.Service.DispatchClientAsync(s, MapTestHarness.ConReady());
        await h.Service.DispatchClientAsync(s, MapTestHarness.MoveReq(0, 999, 0, 999, dir: 0, speed: 1));
        Assert.Single(h.World.WithId(Msg.MW_BEGINTELEPORT_ACK));
        Assert.True(s.TeleportPending);
        Assert.Equal((100f, 100f), (s.Char!.PosX, s.Char.PosZ));

        await h.Service.DispatchWorldAsync(PositionPacket(Msg.MW_STARTTELEPORT_REQ, s, 2).ToArray());
        Assert.Equal(EnterState.Entering, s.State);
        Assert.Null(s.Grid);
        await h.Service.DispatchClientAsync(s, MapTestHarness.ConReady());
        await h.Service.DispatchClientAsync(s, Request(3));
        Assert.Null(s.Grid);
        Assert.True(s.TeleportPending);
        Assert.Single(h.World.WithId(Msg.MW_BEGINTELEPORT_ACK));
    }

    [Fact]
    public async Task TransferExchangesTheChannelView_AndPreservesTheCharacterAndSummon()
    {
        var (h, s, c) = await Setup();
        var ch = s.Char!;
        ch.Gold = 4; ch.Silver = 5; ch.Cooper = 6; ch.Exp = 12345; ch.PartyId = 7; ch.GuildId = 8;
        var item = new Item { TemplateId = 100, ItemSlot = 0, Count = 3, DlId = 9876 };
        var bag = new Inven { InvenId = 0 }; bag.Items.Add(item); ch.Invens.Add(bag);
        var (old, oldClient) = await h.EnterAsync(2, 20, 222, x: 100, z: 100, channel: 1);
        var (next, nextClient) = await h.EnterAsync(3, 30, 333, x: 100, z: 100, channel: 2);
        var oldMonster = new Monster { Id = 0x10001, ChartId = 500, Hp = 50, MaxHp = 100,
            Channel = 1, MapId = 0, PosX = 100, PosZ = 100, InvenLock = ch.CharId };
        var nextMonster = new Monster { Id = 0x20001, ChartId = 500, Hp = 50, MaxHp = 100,
            Channel = 2, MapId = 0, PosX = 100, PosZ = 100 };
        h.Service.SpawnMonster(oldMonster); h.Service.SpawnMonster(nextMonster);
        ch.LockedMonId = oldMonster.Id;
        var summon = new RecallMon { Id = 9000, ChartId = 501, OwnerId = 1, Hp = 44, MaxHp = 60,
            Mp = 20, MaxMp = 30, Channel = 1, MapId = 0, PosX = 100, PosZ = 100, RecallType = 1 };
        ch.Recalls[summon.Id] = summon; h.State.AddRecall(summon);
        c.Clear(); oldClient.Clear(); nextClient.Clear(); h.World.Clear();

        await h.Service.DispatchClientAsync(s, Request(2));
        await h.Service.DispatchWorldAsync(PositionPacket(Msg.MW_STARTTELEPORT_REQ, s, 2).ToArray());

        Assert.Null(s.Grid);
        Assert.Contains(oldClient.WithId(Msg.CS_LEAVE_ACK), p => new PacketReader(p).ReadUInt32() == 1);
        Assert.Empty(nextClient.WithId(Msg.CS_ENTER_ACK));
        Assert.Contains(c.WithId(Msg.CS_DELMON_ACK), p => new PacketReader(p).ReadUInt32() == oldMonster.Id);
        Assert.Equal(0u, oldMonster.InvenLock);
        Assert.Equal(0u, ch.LockedMonId);
        Assert.False(summon.InMap);
        Assert.DoesNotContain(h.State.Neighbors(old), p => p == s);

        await h.Service.DispatchWorldAsync(PositionPacket(Msg.MW_TELEPORT_REQ, s, 2).WriteByte(0).ToArray());
        Assert.Equal(0, new PacketReader(c.Last(Msg.CS_TELEPORT_ACK)!).ReadByte());
        await h.Service.DispatchWorldAsync(PositionPacket(Msg.MW_CONLIST_REQ, s, 2).ToArray());
        var connections = new PacketReader(h.World.Last(Msg.MW_CONLIST_ACK)!);
        Assert.Equal(1u, connections.ReadUInt32()); Assert.Equal(s.Key, connections.ReadUInt32());
        Assert.Equal(1, connections.ReadByte()); Assert.Equal(h.Options.ServerId, connections.ReadByte());
        await h.Service.DispatchWorldAsync(MapTestHarness.CheckMainReq(1, s.Key, 2, 0, 100, 4, 100));
        await h.Service.DispatchWorldAsync(MapTestHarness.ConResultReq(1, s.Key, ConnectResult.Success));
        Assert.Equal(EnterState.Granted, s.State);
        await h.Service.DispatchClientAsync(s, MapTestHarness.ConReady());

        Assert.Equal(EnterState.InGame, s.State);
        Assert.False(s.TeleportPending);
        Assert.Equal((byte)2, s.Channel);
        Assert.Equal((byte)2, s.Grid!.Channel);
        Assert.Contains(next, h.State.Neighbors(s));
        Assert.DoesNotContain(old, h.State.Neighbors(s));
        Assert.Contains(nextClient.WithId(Msg.CS_ENTER_ACK), p => new PacketReader(p).ReadUInt32() == 1);
        Assert.Contains(c.WithId(Msg.CS_ADDMON_ACK), p => new PacketReader(p).ReadUInt32() == nextMonster.Id);
        Assert.Same(ch, s.Char);
        Assert.Equal((73u, 80u, 4u, 5u, 6u, 12345u), (ch.Hp, ch.Mp, ch.Gold, ch.Silver, ch.Cooper, ch.Exp));
        Assert.Equal(((ushort)7, 8u), (ch.PartyId, ch.GuildId));
        Assert.Same(item, Assert.Single(Assert.Single(ch.Invens).Items));
        Assert.Equal((3, 9876L), ((int)item.Count, item.DlId));
        Assert.True(summon.InMap);
        Assert.Equal((byte)2, summon.Channel);
        Assert.Equal((44u, 20u), (summon.Hp, summon.Mp));
        Assert.DoesNotContain(summon, h.State.RecallsInView(old));
        Assert.Contains(summon, h.State.RecallsInView(next));

        await h.Service.DispatchClientAsync(s, Request(1));
        await Complete(h, s, 1);
        Assert.Equal((byte)1, s.Channel);
        Assert.Contains(old, h.State.Neighbors(s));
        Assert.DoesNotContain(next, h.State.Neighbors(s));
        Assert.False(c.Closed);
        Assert.Equal(73u, ch.Hp);
    }

    [Fact]
    public async Task ReturningToTheOldChannelDoesNotRestoreOldCastAuthorizations()
    {
        var (h, s, _) = await Setup();
        var skill = new SkillTemplate(200, 0, 10, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0xFFFF, Rate1stX: 100);
        skill.Data.Add(new SkillDataRow(0, 1, 1, 14, 2, 20, 0, 0)); // direct HP damage
        s.Char!.Skills.Add(new Skill { SkillId = 200, Level = 1, Template = skill });
        var mob = new Monster { Id = 0x10001, ChartId = 500, Hp = 100, MaxHp = 100,
            Channel = 1, MapId = 0, PosX = 100, PosZ = 100, Country = 3 };
        h.Service.SpawnMonster(mob);
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200, targets: new[] { (mob.Id, (byte)2) }));
        Assert.Equal(70u, s.Char.Mp);

        await h.Service.DispatchClientAsync(s, Request(2)); await Complete(h, s, 2);
        await h.Service.DispatchClientAsync(s, Request(1)); await Complete(h, s, 1);
        await h.Service.DispatchClientAsync(s, MapTestHarness.DefendReq(1, mob.Id, skillId: 200));

        Assert.Equal(100u, mob.Hp);
        Assert.Equal(70u, s.Char.Mp);

        // A fresh paid cast after returning still works; rejection above is not a broken combat fixture.
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mob.Id, skillId: 200));
        Assert.Equal(80u, mob.Hp);
        Assert.Equal(60u, s.Char.Mp);
    }

    [Fact]
    public async Task ADeadCharacterCanTransferWithoutBeingRevivedOrRefilled()
    {
        var (h, s, c) = await Setup();
        s.Char!.Hp = 0; s.Char.Mp = 23;

        await h.Service.DispatchClientAsync(s, Request(2));
        await Complete(h, s, 2);

        Assert.Equal((byte)2, s.Channel);
        Assert.Equal((0u, 23u), (s.Char.Hp, s.Char.Mp));
        Assert.False(c.Has(Msg.CS_REVIVAL_ACK));
    }

    [Fact]
    public async Task LosingTheWorldDuringLoadingClosesThePendingSessionForRelog()
    {
        var (h, s, c) = await Setup();
        var (_, otherClient) = await h.EnterAsync(2, 20, 222);
        await h.Service.DispatchClientAsync(s, Request(2));

        h.Service.OnWorldDisconnect();

        Assert.True(c.Closed);
        Assert.False(otherClient.Closed);
        Assert.False(h.Service.WorldReady);
    }
}
