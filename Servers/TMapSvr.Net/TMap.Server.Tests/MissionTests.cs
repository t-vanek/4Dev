using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Territory battles, batch B: a mission area's war — its suspended spawns, the gatekeeper capture, the reward, the
/// world's news, and a war nobody wins.</summary>
public class MissionTests
{
    private const ushort Mission = 101, Map = 700, MonId = 500, AttrId = 900;
    private const ushort GateD = 30123, GateC = 30130, Grunt = 30124;
    private const byte D = 0, C = 1, N = 3;

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.MonsterTemplates[MonId] = new MonsterTemplate(MonId, 5, AttrId);
        t.MonAttrs[TemplateStore.MonAttrKey(AttrId, 5)] = new MonAttrRow(AttrId, 5, 10, 50, 0);
        foreach (var (id, country) in new[] { (GateD, D), (GateC, C), (Grunt, D) })
            t.MonsterSpawns.Add(new MonsterSpawnDef(new MonSpawnRow(id, Map, 100, 0, 100, 0, country, 1, 0, 100, 0, 30_000, 2, LocalId: Mission),
                new List<MapMonRow> { new(id, MonId, 0, 0, 100) }));
        t.BattleZones[Mission] = new BattleZone(Mission, "Yesod", Map, 0, 0, GateD, GateC, 0, 0, 0, 0, 0, 0, 1, 806, 807, 15,
            0, 0, 0, 0, 0, 0, 0);
        t.Territories.Add(new TerritoryRow(LocalType.Mission, Mission, N, 0, "", 0, 0, "", 0));
        return t;
    }

    private static byte[] World(ushort id, Action<PacketWriter> body) { var w = new PacketWriter(id); body(w); return w.ToArray(); }
    private static byte[] MissionEnable(byte status) => World(Msg.MW_MISSIONENABLE_REQ, w => { w.WriteByte(status); w.WriteUInt32(72000); w.WriteUInt32(0); });

    private static async Task<(MapTestHarness h, ClientSession a, FakeClientChannel ca, ClientSession b, FakeClientChannel cb)> Setup()
    {
        var h = new MapTestHarness(Store());
        h.Service.InitTerritories();
        var ca0 = new Character { CharId = 1, Name = "Ann", Level = 10, MaxHp = 100, Hp = 100, MapId = Map };
        var cb0 = new Character { CharId = 2, Name = "Bob", Level = 10, MaxHp = 100, Hp = 100, MapId = Map };
        var (a, ca) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: ca0);
        var (b, cb) = await h.EnterAsync(2, 2, 2, x: 101, z: 100, name: "Bob", preSeeded: cb0);
        foreach (var (s, country) in new[] { (a, C), (b, D) }) { s.Char!.MapId = Map; s.Char.Country = country; s.Char.AidCountry = N; }
        h.Service.CombatRng = new Random(1);
        ca.Clear(); cb.Clear(); h.World.Clear();
        return (h, a, ca, b, cb);
    }

    private static Monster? Live(MapTestHarness h, ushort spawnId) => h.State.FindMonster(Monster.MakeId(spawnId, 1, 0));

    private static async Task Kill(MapTestHarness h, ClientSession s, Monster mon)
    {
        for (int i = 0; i < 20 && mon.Hp > 0; i++) await h.CastAndHitAsync(s, MapTestHarness.DefendReq(s.CharId, mon.Id));
    }

    [Fact]
    public async Task AtWar_TheAreasSpawnsComeOut_AndADeadOneStaysDead()
    {
        var (h, a, _, _, _) = await Setup();
        Assert.Null(Live(h, Grunt));

        await h.Service.DispatchWorldAsync(MissionEnable(1));
        var grunt = Live(h, Grunt)!;
        Assert.NotNull(Live(h, GateD));
        await Kill(h, a, grunt);
        h.Service.RunMonsterRegen(10_000_000);

        Assert.Equal(0u, grunt.Hp);
        Assert.True(Live(h, Grunt) is null or { Hp: 0 });                         // no new one: the spawn is suspended
    }

    [Fact]
    public async Task KillingAGatekeeper_TakesTheAreaForTheOtherCountry()
    {
        var (h, a, ca, _, cb) = await Setup();
        await h.Service.DispatchWorldAsync(MissionEnable(1));

        await Kill(h, a, Live(h, GateD)!);                                       // Defugel's gatekeeper falls ⇒ Craxion takes it

        var r = new PacketReader(h.World.Last(Msg.MW_MISSIONOCCUPY_ACK)!);
        Assert.Equal(((byte)1, Mission, C), (r.ReadByte(), r.ReadUInt16(), r.ReadByte()));
        var m = h.Service.Territories[Mission];
        Assert.Equal((C, true, (byte)4), (m.Country, m.Occupied, m.Status));
        Assert.Null(Live(h, GateC));                                              // its monsters are gone
        Assert.True(ca.Has(Msg.CS_PVPPOINT_ACK));                                 // Craxion's player on the map: 200 points
        Assert.False(cb.Has(Msg.CS_PVPPOINT_ACK));
    }

    [Fact]
    public async Task AMonsterOnAnotherMap_CannotBeHit()
    {
        var (h, a, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(MissionEnable(1));
        a.Char!.MapId = 0;                                                       // the gatekeeper is on map 700
        var gate = Live(h, GateD)!;

        await Kill(h, a, gate);

        Assert.Equal(gate.MaxHp, gate.Hp);
        Assert.False(h.Service.Territories[Mission].Occupied);
    }

    [Fact]
    public async Task AWarNobodyWins_LeavesItToNoCountry()
    {
        var (h, _, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(MissionEnable(1));

        await h.Service.DispatchWorldAsync(MissionEnable(4));

        var r = new PacketReader(h.World.Last(Msg.MW_MISSIONOCCUPY_ACK)!);
        Assert.Equal(((byte)0, Mission, N), (r.ReadByte(), r.ReadUInt16(), r.ReadByte()));
        Assert.Null(Live(h, GateD));
    }

    [Fact]
    public async Task BackToNormal_TheSpawnsAreTakenAway()
    {
        var (h, _, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(MissionEnable(1));
        await h.Service.DispatchWorldAsync(MissionEnable(4));

        await h.Service.DispatchWorldAsync(MissionEnable(0));
        h.Service.RunMonsterRegen(10_000_000);

        Assert.Null(Live(h, Grunt));
    }

    [Fact]
    public async Task TheWorldsNews_SayWhoTookIt()
    {
        var (h, _, ca, _, _) = await Setup();

        await h.Service.DispatchWorldAsync(World(Msg.MW_MISSIONOCCUPY_REQ, w => { w.WriteByte(1); w.WriteUInt16(Mission); w.WriteByte(C); }));

        var r = new PacketReader(ca.Last(Msg.CS_SYSTEMMSG_ACK)!);
        Assert.Equal(((byte)22 /* SM_MISSION_BOSSDIE */, "Yesod", (ushort)C, (uint)Map), (r.ReadByte(), r.ReadString(), r.ReadUInt16(), r.ReadUInt32()));
        Assert.Equal(C, h.Service.Territories[Mission].Country);
    }

    [Fact]
    public async Task NobodyTookIt_IsTheTimeoutNews()
    {
        var (h, _, ca, _, _) = await Setup();

        await h.Service.DispatchWorldAsync(World(Msg.MW_MISSIONOCCUPY_REQ, w => { w.WriteByte(0); w.WriteUInt16(Mission); w.WriteByte(N); }));

        Assert.Equal((byte)26 /* SM_MISSION_TIMEOUT */, new PacketReader(ca.Last(Msg.CS_SYSTEMMSG_ACK)!).ReadByte());
    }
}
