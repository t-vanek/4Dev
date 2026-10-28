using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Territory battles, batch C: the sky garden — coming in, its war's spawns, the three points, the bosses, the result and
/// the reward.</summary>
public class SkyGardenTests
{
    private const ushort Sky = 200, Map = 2100, MonId = 500, AttrId = 900;
    private const ushort GateL = 32391, GateR = 32392, GateC = 32393, Mid = 32388, Right = 32389, Left = 32390;
    private const ushort ValDef = 32396, ValAtk = 32397, DerDef = 32394, DerAtk = 32395, CampDD = 40001, CampCA = 40002, CampCD = 40003;
    private const byte D = 0, C = 1, N = 3, CampDefend = 1, CampAttack = 2;

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.MonsterTemplates[MonId] = new MonsterTemplate(MonId, 5, AttrId);
        t.MonAttrs[TemplateStore.MonAttrKey(AttrId, 5)] = new MonAttrRow(AttrId, 5, 10, 50, 0);
        foreach (var (id, country, ev) in new[]
        {
            (GateL, N, (byte)2), (GateR, N, (byte)2), (GateC, N, (byte)1), (Mid, N, (byte)1), (Right, N, (byte)1), (Left, N, (byte)1),
            (ValDef, D, (byte)1), (ValAtk, D, (byte)2), (DerDef, C, (byte)1), (DerAtk, C, (byte)2),
            (CampDD, D, (byte)1), (CampCA, C, (byte)2), (CampCD, C, (byte)1),
        })
            t.MonsterSpawns.Add(new MonsterSpawnDef(new MonSpawnRow(id, Map, 100, 0, 100, 0, country, 1, 0, 100, 0, 30_000, ev, LocalId: Sky),
                new List<MapMonRow> { new(id, MonId, 0, 0, 100) }));
        t.BattleZones[Sky] = new BattleZone(Sky, "Avalon", Map, 0, 0, GateL, GateR, GateC, 501, 502, 500, 0, 0, 1, 0, 0, 0,
            ValDef, ValAtk, DerDef, DerAtk, Mid, Right, Left);
        t.Territories.Add(new TerritoryRow(LocalType.SkyGarden, Sky, D, 0, "", 0, 0, "", 0));
        return t;
    }

    private static byte[] World(ushort id, Action<PacketWriter> body) { var w = new PacketWriter(id); body(w); return w.ToArray(); }
    private static byte[] SkyEnable(byte status) => World(Msg.MW_SKYGARDENENABLE_REQ, w => { w.WriteByte(status); w.WriteUInt32(0); w.WriteByte(7); w.WriteUInt32(77400); });

    /// <summary>A (Craxion, the attacker) and B (Defugel, the defender) on the garden's map.</summary>
    private static async Task<(MapTestHarness h, ClientSession a, FakeClientChannel ca, ClientSession b, FakeClientChannel cb)> Setup()
    {
        var h = new MapTestHarness(Store());
        h.Service.InitTerritories();
        var (a, ca) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: new Character { CharId = 1, Name = "Ann", Level = 10, MaxHp = 100, Hp = 100, MapId = Map });
        var (b, cb) = await h.EnterAsync(2, 2, 2, x: 101, z: 100, name: "Bob", preSeeded: new Character { CharId = 2, Name = "Bob", Level = 10, MaxHp = 100, Hp = 100, MapId = Map });
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
    public async Task ComingIn_GivesOnesCampAndTheGardensState()
    {
        var h = new MapTestHarness(Store());
        h.Service.InitTerritories();
        var ch = new Character { CharId = 1, Name = "Ann", MaxHp = 100, Hp = 100, MapId = Map, Country = C, AidCountry = N };
        var (_, c) = await h.EnterAsync(1, 1, 1, name: "Ann", mapId: Map, x: 100, z: 100, preSeeded: ch);   // the enter packet: Craxion

        var r = new PacketReader(c.Last(Msg.CS_ENTERSKYGARDEN_ACK)!);
        Assert.Equal((Sky, CampAttack, D, CampDefend, CampDefend, CampDefend, C),
            (r.ReadUInt16(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()));
    }

    [Fact]
    public async Task AtWar_TheGatekeepersGuardiansBossesAndEachSidesCamp_ComeOut()
    {
        var (h, _, _, _, _) = await Setup();

        await h.Service.DispatchWorldAsync(SkyEnable(1));

        foreach (var id in new[] { GateL, GateR, GateC, Mid, Left, Right, CampDD, CampCA, ValDef, DerAtk }) Assert.NotNull(Live(h, id));
        foreach (var id in new[] { CampCD, ValAtk, DerDef }) Assert.Null(Live(h, id));
        Assert.Equal(D, Live(h, Mid)!.Country);                                   // the guardians fight for the owner
    }

    [Fact]
    public async Task KillingAGuardian_TurnsItsPointOver()
    {
        var (h, a, ca, _, cb) = await Setup();
        await h.Service.DispatchWorldAsync(SkyEnable(1));

        await Kill(h, a, Live(h, Mid)!);

        Assert.Equal(CampAttack, h.Service.Territories[Sky].MiddleOwner);
        Assert.Equal(CampAttack, new PacketReader(cb.Last(Msg.CS_SKYGARDEN_OCCUPY_CENTER_ACK)!).ReadByte());
        Assert.True(ca.Has(Msg.CS_SKYGARDEN_OCCUPY_CENTER_ACK));
    }

    [Fact]
    public async Task AtTheEnd_WhoHoldsTwoPointsWins()
    {
        var (h, a, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(SkyEnable(1));
        await Kill(h, a, Live(h, Mid)!);
        await Kill(h, a, Live(h, Left)!);

        await h.Service.DispatchWorldAsync(SkyEnable(4));

        var r = new PacketReader(h.World.Last(Msg.MW_SKYGARDENOCCUPY_ACK)!);
        Assert.Equal(((byte)1, Sky, C), (r.ReadByte(), r.ReadUInt16(), r.ReadByte()));
    }

    [Fact]
    public async Task HoldingOnToTwoPoints_TheDefendersKeepIt()
    {
        var (h, a, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(SkyEnable(1));
        await Kill(h, a, Live(h, Mid)!);

        await h.Service.DispatchWorldAsync(SkyEnable(4));

        var r = new PacketReader(h.World.Last(Msg.MW_SKYGARDENOCCUPY_ACK)!);
        Assert.Equal(((byte)1, Sky, D), (r.ReadByte(), r.ReadUInt16(), r.ReadByte()));
    }

    [Fact]
    public async Task TheNextWar_HasAResultToo()
    {
        var (h, _, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(SkyEnable(1));
        await h.Service.DispatchWorldAsync(SkyEnable(4));
        await h.Service.DispatchWorldAsync(SkyEnable(0));
        h.World.Clear();

        await h.Service.DispatchWorldAsync(SkyEnable(1));
        await h.Service.DispatchWorldAsync(SkyEnable(4));

        Assert.NotNull(h.World.Last(Msg.MW_SKYGARDENOCCUPY_ACK));
    }

    [Fact]
    public async Task KillingTheDefendersBoss_WinsAtOnce()
    {
        var (h, a, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(SkyEnable(1));

        await Kill(h, a, Live(h, ValDef)!);                                        // Valorian's boss ⇒ Craxion wins

        var r = new PacketReader(h.World.Last(Msg.MW_SKYGARDENOCCUPY_ACK)!);
        Assert.Equal(((byte)1, Sky, C), (r.ReadByte(), r.ReadUInt16(), r.ReadByte()));
        Assert.True(h.Service.Territories[Sky].Occupied);
    }

    [Fact]
    public async Task TheWorldsResult_ResetsTheGarden_TellsEveryone_AndRewards()
    {
        var (h, _, ca, _, cb) = await Setup();
        await h.Service.DispatchWorldAsync(SkyEnable(1));

        await h.Service.DispatchWorldAsync(World(Msg.MW_SKYGARDENOCCUPY_REQ, w => { w.WriteByte(1); w.WriteUInt16(Sky); w.WriteByte(C); }));

        var g = h.Service.Territories[Sky];
        Assert.Equal((C, (byte)4, false, true), (g.Country, g.Status, g.CanBattle, g.Occupied));
        Assert.Null(Live(h, Mid));
        var r = new PacketReader(ca.Last(Msg.CS_SYSTEMMSG_ACK)!);
        Assert.Equal(((byte)34 /* SM_SKYGARDEN_END */, "Avalon", (ushort)C, (uint)Map), (r.ReadByte(), r.ReadString(), r.ReadUInt16(), r.ReadUInt32()));
        Assert.True(ca.Has(Msg.CS_PVPPOINT_ACK));                                 // the winner: 360 (WIN)
        Assert.True(cb.Has(Msg.CS_PVPPOINT_ACK));                                 // the other side: 360 (DEFEND)
    }
}
