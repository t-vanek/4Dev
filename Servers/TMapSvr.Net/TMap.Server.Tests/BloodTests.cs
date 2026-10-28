using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>The previously missing World-to-Map half of HP/MP lifedrain and non-lethal reflection.</summary>
public sealed class BloodTests
{
    private sealed record Context(MapTestHarness H, ClientSession S, FakeClientChannel C, Character Ch, Monster Mon);
    private static async Task<Context> Setup()
    {
        var h = new MapTestHarness();
        var ch = new Character { CharId = 1, Name = "Owner", MaxHp = 100, Hp = 80, MaxMp = 50, Mp = 30 };
        var (s, c) = await h.EnterAsync(1, 1, 123, x: 100, z: 100, preSeeded: ch);
        ch.Hp = 80; ch.Mp = 30;
        var mon = new Monster { Id = 0x50001, ChartId = 500, Hp = 80, MaxHp = 100, Mp = 30, MaxMp = 50,
            HostId = 1, MapId = 0, Channel = 1, PosX = 100, PosZ = 100 };
        h.Service.SpawnMonster(mon); c.Clear(); h.World.Clear();
        return new(h, s, c, ch, mon);
    }
    private static byte[] Request(uint id, byte type, byte bloodType, uint amount = 20, uint key = 123, uint route = 1)
        => new PacketWriter(Msg.MW_GETBLOOD_REQ).WriteUInt32(route).WriteUInt32(key).WriteUInt32(id)
            .WriteByte(type).WriteByte(bloodType).WriteUInt32(amount).ToArray();
    private static RecallMon Recall(Context x, byte type)
    {
        var m = new RecallMon { Id = 800, ObjType = type, OwnerId = 1, Hp = 80, MaxHp = 100, Mp = 30, MaxMp = 50,
            Channel = 1, MapId = 0, PosX = 100, PosZ = 100 };
        if (type == 7) x.Ch.Recalls[m.Id] = m;
        else if (type == 11) x.Ch.SelfObjs[m.Id] = m;
        else x.Ch.CompanionObjs[m.Id] = m;
        x.H.State.AddRecall(m); return m;
    }

    [Theory]
    [InlineData(38, 20, 100, 30)]
    [InlineData(40, 20, 80, 50)]
    [InlineData(30, 20, 60, 30)]
    [InlineData(30, 80, 1, 30)]
    [InlineData(30, uint.MaxValue, 1, 30)]
    [InlineData(38, uint.MaxValue, 100, 30)]
    [InlineData(40, uint.MaxValue, 80, 50)]
    public async Task PlayerBloodGainsCapAtMax_AndReflectionLeavesOneHp(byte type, uint amount, uint hp, uint mp)
    {
        var x = await Setup();
        await x.H.Service.DispatchWorldAsync(Request(1, 1, type, amount));
        Assert.Equal((hp, mp), (x.Ch.Hp, x.Ch.Mp)); Assert.True(x.C.Has(Msg.CS_HPMP_ACK));
        Assert.False(x.C.Has(Msg.CS_DIE_ACK)); Assert.False(x.H.World.Has(Msg.MW_GETBLOOD_ACK));
        var r = new PacketReader(x.C.Last(Msg.CS_HPMP_ACK)!);
        Assert.Equal(1u, r.ReadUInt32()); Assert.Equal((byte)1, r.ReadByte());
        Assert.Equal(100u, r.ReadUInt32()); Assert.Equal(hp, r.ReadUInt32()); Assert.Equal(50u, r.ReadUInt32()); Assert.Equal(mp, r.ReadUInt32());
    }

    [Theory]
    [InlineData(38, 100, 30)]
    [InlineData(40, 80, 50)]
    [InlineData(30, 60, 30)]
    public async Task MonsterBloodUsesTheHostedMonstersOwnBars(byte type, uint hp, uint mp)
    {
        var x = await Setup(); await x.H.Service.DispatchWorldAsync(Request(x.Mon.Id, 2, type));
        Assert.Equal((hp, mp), (x.Mon.Hp, x.Mon.Mp)); Assert.Equal((80u, 30u), (x.Ch.Hp, x.Ch.Mp));
        var r = new PacketReader(x.C.Last(Msg.CS_HPMP_ACK)!);
        Assert.Equal(x.Mon.Id, r.ReadUInt32()); Assert.Equal((byte)2, r.ReadByte());
    }

    [Theory]
    [InlineData(7, 38, 100, 30)]
    [InlineData(7, 40, 80, 50)]
    [InlineData(7, 30, 60, 30)]
    [InlineData(11, 38, 100, 30)]
    [InlineData(11, 40, 80, 50)]
    [InlineData(11, 30, 60, 30)]
    [InlineData(18, 38, 100, 30)]
    [InlineData(18, 40, 80, 50)]
    [InlineData(18, 30, 60, 30)]
    public async Task RecallsPlacedObjectsAndCompanionsReceiveTheirOwnBlood(byte objectType, byte type, uint hp, uint mp)
    {
        var x = await Setup(); var recall = Recall(x, objectType);
        await x.H.Service.DispatchWorldAsync(Request(recall.Id, objectType, type));
        Assert.Equal((hp, mp), (recall.Hp, recall.Mp)); Assert.Equal((80u, 30u), (x.Ch.Hp, x.Ch.Mp));
        var r = new PacketReader(x.C.Last(Msg.CS_HPMP_ACK)!);
        Assert.Equal(recall.Id, r.ReadUInt32()); Assert.Equal(objectType, r.ReadByte());
    }

    [Theory]
    [InlineData(0)] // wrong session key
    [InlineData(1)] // route does not exist
    [InlineData(2)] // secondary route
    [InlineData(3)] // route loading another map
    [InlineData(4)] // PC id is not this routed player
    [InlineData(5)] // dead player
    [InlineData(6)] // unsupported effect type
    [InlineData(7)] // unsupported object type
    public async Task InvalidOrStalePlayerRoutesCannotChangeBars(int reason)
    {
        var x = await Setup(); uint id = reason == 4 ? 2u : 1u;
        if (reason == 2) x.S.IsMain = false;
        if (reason == 3) x.S.State = EnterState.Entering;
        if (reason == 5) x.Ch.Hp = 0;
        var before = (x.Ch.Hp, x.Ch.Mp);
        await x.H.Service.DispatchWorldAsync(Request(id, reason == 7 ? (byte)255 : (byte)1,
            reason == 6 ? (byte)255 : (byte)38, key: reason == 0 ? 999u : 123u, route: reason == 1 ? 999u : 1u));
        Assert.Equal(before, (x.Ch.Hp, x.Ch.Mp)); Assert.False(x.C.Has(Msg.CS_HPMP_ACK));
    }

    [Theory]
    [InlineData(0)] // other host
    [InlineData(1)] // other map
    [InlineData(2)] // other channel
    [InlineData(3)] // dead monster
    [InlineData(4)] // absent monster
    public async Task AWorldReplyCannotCreditAForeignOrDeadMonster(int reason)
    {
        var x = await Setup();
        if (reason == 0) x.Mon.HostId = 2;
        if (reason == 1) x.Mon.MapId = 1;
        if (reason == 2) x.Mon.Channel = 2;
        if (reason == 3) x.Mon.Hp = 0;
        var before = (x.Mon.Hp, x.Mon.Mp);
        await x.H.Service.DispatchWorldAsync(Request(reason == 4 ? 999u : x.Mon.Id, 2, 38));
        Assert.Equal(before, (x.Mon.Hp, x.Mon.Mp)); Assert.False(x.C.Has(Msg.CS_HPMP_ACK));
    }

    [Theory]
    [InlineData(0)] // other owner
    [InlineData(1)] // other map
    [InlineData(2)] // other channel
    [InlineData(3)] // dead recall
    [InlineData(4)] // recall off the map
    [InlineData(5)] // wrong object namespace, same numeric id
    public async Task AWorldReplyCannotCreditAForeignOrDeadRecall(int reason)
    {
        var x = await Setup(); var recall = Recall(x, 7);
        if (reason == 0) recall.OwnerId = 2;
        if (reason == 1) recall.MapId = 1;
        if (reason == 2) recall.Channel = 2;
        if (reason == 3) recall.Hp = 0;
        if (reason == 4) recall.InMap = false;
        var before = (recall.Hp, recall.Mp);
        await x.H.Service.DispatchWorldAsync(Request(recall.Id, reason == 5 ? (byte)11 : (byte)7, 38));
        Assert.Equal(before, (recall.Hp, recall.Mp)); Assert.False(x.C.Has(Msg.CS_HPMP_ACK));
    }

    [Fact]
    public async Task AClientCannotSendTheWorldOnlyBloodInstruction()
    {
        var x = await Setup(); await x.H.Service.DispatchClientAsync(x.S, Request(1, 1, 38, uint.MaxValue));
        Assert.Equal((80u, 30u), (x.Ch.Hp, x.Ch.Mp)); Assert.False(x.C.Has(Msg.CS_HPMP_ACK));
    }
}
