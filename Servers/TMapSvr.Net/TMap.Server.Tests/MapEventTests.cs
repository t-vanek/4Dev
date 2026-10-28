using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Batch S6a: the world's events on the map — the drop, money and exp bonuses, the monster spawn and regen events, the event
/// message and the lucky-ticket draw.</summary>
public class MapEventTests
{
    private const ushort DropId = 300, MonId = 900, SpawnId = 7;

    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        t.Items[DropId] = new ItemTemplate(DropId, 0, new[] { 1f, 0f, 0f, 0f });
        t.MonsterTemplates[MonId] = new MonsterTemplate(MonId, 5, 1, Exp: 100);
        t.MonAttrs[TemplateStore.MonAttrKey(1, 5)] = new MonAttrRow(1, 5, 100, 50, 0);
        t.MonsterSpawns.Add(new MonsterSpawnDef(
            new MonSpawnRow(Id: SpawnId, MapId: 0, PosX: 100, PosY: 0, PosZ: 100, Dir: 0, Country: 0, Count: 1, Range: 0, Prob: 100,
                Region: 7, Delay: 0, Event: 2 /* SE_QUEST: not spawned at start */),
            new List<MapMonRow> { new(SpawnId, MonId, 0, 0, 100) }));
        t.SvrMsgs[6] = "Operator";
        return t;
    }

    private static byte[] Update(byte id, ushort value, uint index = 100, ushort mapId = 0xFF, byte start = 0, byte end = 9,
        ushort[]? spawns = null, (ushort Mon, uint Delay, ushort Map, float X, float Y, float Z)[]? regen = null)
    {
        var w = new PacketWriter(Msg.MW_EVENTUPDATE_REQ);
        w.WriteByte(id); w.WriteUInt16(value);
        w.WriteUInt32(index); w.WriteByte(id); w.WriteByte(1); w.WriteByte(0); w.WriteByte(0); w.WriteByte(0);
        w.WriteInt64(0); w.WriteInt64(0); w.WriteUInt16(value); w.WriteUInt16(mapId); w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteString("start"); w.WriteString("end"); w.WriteString("Title"); w.WriteByte(0); w.WriteString("");
        w.WriteUInt16(0);
        w.WriteByte(start); w.WriteByte(end);
        w.WriteUInt16((ushort)(spawns?.Length ?? 0)); foreach (var s in spawns ?? Array.Empty<ushort>()) w.WriteUInt16(s);
        w.WriteUInt16((ushort)(regen?.Length ?? 0));
        foreach (var g in regen ?? Array.Empty<(ushort, uint, ushort, float, float, float)>())
        { w.WriteUInt16(g.Mon); w.WriteUInt32(g.Delay); w.WriteUInt16(g.Map); w.WriteFloat(g.X); w.WriteFloat(g.Y); w.WriteFloat(g.Z); }
        w.WriteUInt16(0);
        return w.ToArray();
    }

    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, Character Ch)> Open()
    {
        var h = new MapTestHarness(Store());
        var ch = new Character { CharId = 1, Name = "Ann", Level = 5, Country = 1, MaxHp = 100, Hp = 100 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        c.Clear();
        return (h, s, c, ch);
    }

    private static async Task<Monster> Kill(MapTestHarness h, ClientSession s, byte itemProb, uint minMoney = 0)
    {
        var mob = new Monster { Id = 0x60001, ChartId = 500, Level = 5, MaxHp = 10, Hp = 10, MaxMp = 50, Mp = 50, DefendPower = 100,
            PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0, ItemProb = itemProb, DropCount = 1, MaxWeight = 1,
            MoneyProb = 100, MinMoney = minMoney, MaxMoney = minMoney + 1, Exp = 1000,
            DropRows = new[] { new MonItemRow(DropId, 1, 1, 100, 100, 100, 100) } };
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        h.Service.LootRng = new Random(3);
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mob.Id));
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mob.Id));
        return mob;
    }

    [Fact]
    public async Task ItemDropEvent_MakesARareDropSure()
    {
        var (h, s, _, _) = await Open();
        await h.Service.DispatchWorldAsync(Update(3 /* ITEMDROP */, 10_000));
        var mob = await Kill(h, s, itemProb: 1);
        Assert.Single(mob.CorpseInven.Items);
    }

    [Fact]
    public async Task DropEvent_OnAnotherMap_DoesNothing()
    {
        var (h, s, _, _) = await Open();
        await h.Service.DispatchWorldAsync(Update(3, 10_000, mapId: 5));
        var mob = await Kill(h, s, itemProb: 0);
        Assert.Empty(mob.CorpseInven.Items);
    }

    [Fact]
    public async Task MoneyEvent_RaisesTheCorpseMoney()
    {
        var (h, s, _, _) = await Open();
        await h.Service.DispatchWorldAsync(Update(11 /* MONEYDROP */, 50));
        var mob = await Kill(h, s, itemProb: 0, minMoney: 1000);
        Assert.Equal(1500u, mob.CorpseMoney);
    }

    [Fact]
    public async Task ExpEvent_AddsItsShare_AndEndsWithTheEvent()
    {
        var (h, s, _, ch) = await Open();
        await Kill(h, s, itemProb: 0);
        uint plain = ch.Exp;
        Assert.True(plain > 0);
        await h.Service.DispatchWorldAsync(Update(1 /* EXPADD */, 100));
        await Kill(h, s, itemProb: 0);
        Assert.Equal(plain * 3, ch.Exp);                                                       // the second kill gave double
        await h.Service.DispatchWorldAsync(Update(1, 0));
        await Kill(h, s, itemProb: 0);
        Assert.Equal(plain * 4, ch.Exp);
    }

    [Fact]
    public async Task MonSpawnEvent_SpawnsThenKills()
    {
        var (h, _, _, _) = await Open();
        await h.Service.DispatchWorldAsync(Update(12 /* MONSPAWN */, 1, spawns: new[] { SpawnId }, start: 0, end: 9));
        var mon = Assert.Single(h.State.AllMonsters(), m => m.ChartId == MonId);
        Assert.True(mon.Hp > 0);
        await h.Service.DispatchWorldAsync(Update(12, 0, spawns: new[] { SpawnId }, start: 0, end: 9));
        Assert.True(mon.Hp == 0 || mon.Dead);
    }

    [Fact]
    public async Task MonRegenEvent_PlacesMonsters_ThenRemovesThem()
    {
        var (h, _, _, _) = await Open();
        await h.Service.DispatchWorldAsync(Update(13 /* MONREGEN */, 1, index: 200, regen: new[] { (MonId, 0u, (ushort)0, 101f, 0f, 101f), (MonId, 0u, (ushort)0, 102f, 0f, 102f) }));
        Assert.Equal(2, h.State.AllMonsters().Count(m => m.ChartId == MonId));
        await h.Service.DispatchWorldAsync(Update(13, 0, index: 200));
        Assert.DoesNotContain(h.State.AllMonsters(), m => m.ChartId == MonId && m.Hp > 0);
    }

    [Fact]
    public async Task TwoSpawnEvents_KeepEachOther()
    {
        var (h, _, _, _) = await Open();
        await h.Service.DispatchWorldAsync(Update(13, 1, index: 200, regen: new[] { (MonId, 0u, (ushort)0, 101f, 0f, 101f) }));
        await h.Service.DispatchWorldAsync(Update(13, 1, index: 201, regen: new[] { (MonId, 0u, (ushort)0, 102f, 0f, 102f) }));
        await h.Service.DispatchWorldAsync(Update(13, 0, index: 200));
        await h.Service.DispatchWorldAsync(Update(13, 0, index: 201));
        Assert.DoesNotContain(h.State.AllMonsters(), m => m.ChartId == MonId && m.Hp > 0);   // the C++ forgot the first, leaving it
    }

    [Fact]
    public async Task EventMessage_IsAWorldChatFromTheOperator()
    {
        var (h, _, c, _) = await Open();
        var w = new PacketWriter(Msg.MW_EVENTMSG_REQ);
        w.WriteByte(1); w.WriteByte(0); w.WriteString("Double exp tonight!");
        await h.Service.DispatchWorldAsync(w.ToArray());
        var r = new PacketReader(c.Last(Msg.CS_CHAT_ACK)!);
        Assert.Equal((byte)ChatGroup.World, r.ReadByte());
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal("Operator", r.ReadString());
        Assert.EndsWith("Double exp tonight!", r.ReadString());
    }

    [Fact]
    public async Task QuarterDraw_IsShownToEveryone()
    {
        var (h, _, c, ch) = await Open();
        ch.LuckyNumber = 42;
        var w = new PacketWriter(Msg.MW_EVENTQUARTER_REQ);
        w.WriteByte(3); w.WriteByte(20); w.WriteByte(30); w.WriteByte(42); w.WriteString("Cake");
        await h.Service.DispatchWorldAsync(w.ToArray());
        var r = new PacketReader(c.Last(Msg.CS_SYSTEMMSG_ACK)!);
        Assert.Equal((9, 20, 30, "Cake", 42), (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadString(), r.ReadByte()));
        Assert.Equal(0, r.Remaining);
    }
}
