using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Territory battles, batch D: the forts — their war, gates and boss, the capture, the result, the points and records,
/// and the owner's say over its NPCs and portals.</summary>
public class FortTests
{
    private const ushort Fort = 1, Map = 0, MonId = 500, AttrId = 900, NpcId = 4000, Shop = 4001;
    private const ushort Boss = 25001, GateR = 25002, GateL = 25003, Guard = 25019;
    private const uint SwL = 9, SwR = 10;
    private const byte D = 0, C = 1, N = 3, VictimLevel = 19;

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                       // FTYPE_AL
        t.MonsterTemplates[MonId] = new MonsterTemplate(MonId, 5, AttrId);
        t.MonAttrs[TemplateStore.MonAttrKey(AttrId, 5)] = new MonAttrRow(AttrId, 5, 10, 50, 0);
        foreach (var (id, ev) in new[] { (Boss, (byte)1), (GateR, (byte)1), (GateL, (byte)1), (Guard, (byte)0) })
            t.MonsterSpawns.Add(new MonsterSpawnDef(new MonSpawnRow(id, Map, 100, 0, 100, 0, N, 1, 0, 100, 0, ev == 0 ? 0u : 30_000u, ev, LocalId: Fort),
                new List<MapMonRow> { new(id, MonId, 0, 0, 100) }));
        t.BattleZones[Fort] = new BattleZone(Fort, "Moswood", Map, 0, Boss, GateL, GateR, GateR, SwL, SwR, SwR, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0);
        t.Territories.Add(new TerritoryRow(LocalType.Occupation, Fort, D, 0, "", 0, 0, "", 0));
        foreach (var sw in new[] { SwL, SwR }) t.Switches.Add(new SwitchDef(sw, Map, 100, 0, 100, 1, 0, 0, 0));
        t.LocalPvPoints[(Fort, 2, 4)] = (150, 0);                          // GODMONKILL
        t.LocalPvPoints[(Fort, 2, 5)] = (50, 0);                           // ENTRY
        t.LocalPvPoints[(Fort, 2, 6)] = (590, 300);                        // WIN
        t.LocalPvPoints[(Fort, 2, 7)] = (1060, 0);                         // DEFEND
        t.LevelPvPoint[VictimLevel] = 14;
        t.PvPointKill[(1, 2)] = (80, 20);                                  // PVPS_NORMAL, KILL_E
        t.PvPointKill[(2, 2)] = (100, 0);                                  // PVPS_LOCAL,  KILL_E
        t.Npcs[NpcId] = new NpcDef(NpcId, 2 /* TNPC_ITEM */, N, Fort, 1 /* DCC_GUILD */, 10, 0, 0, Map, 0, 0, 0);
        return t;
    }

    private static byte[] World(ushort id, Action<PacketWriter> body) { var w = new PacketWriter(id); body(w); return w.ToArray(); }
    private static byte[] LocalEnable(byte status) => World(Msg.MW_LOCALENABLE_REQ, w =>
    {
        w.WriteByte(status); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteByte(0); w.WriteUInt32(0);
    });
    private static byte[] Occupy(byte type, byte country, uint guild, string name) => World(Msg.MW_LOCALOCCUPY_REQ, w =>
    {
        w.WriteByte(type); w.WriteUInt16(Fort); w.WriteByte(country); w.WriteUInt32(guild); w.WriteString(name);
    });

    /// <summary>A (Craxion) standing in the fort, B (Defugel) outside it, both by the fort's monsters.</summary>
    private static async Task<(MapTestHarness h, ClientSession a, FakeClientChannel ca, ClientSession b, FakeClientChannel cb)> Setup()
    {
        var h = new MapTestHarness(Store());
        h.Service.InitSwitches();
        h.Service.InitNpcs();
        h.Service.InitTerritories();
        h.Service.InitMonsterSpawns();
        await h.Service.OnTimerAsync();                                     // the fort's own monsters come out
        var (a, ca) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: new Character { CharId = 1, Name = "Ann", Level = 10, MaxHp = 100, Hp = 100 });
        var (b, cb) = await h.EnterAsync(2, 2, 2, x: 101, z: 100, name: "Bob", preSeeded: new Character { CharId = 2, Name = "Bob", Level = VictimLevel, MaxHp = 100, Hp = 100 });
        foreach (var (s, country) in new[] { (a, C), (b, D) }) { s.Char!.Country = country; s.Char.AidCountry = N; }
        a.Char!.LocalId = Fort;
        h.Service.CombatRng = new Random(1);
        ca.Clear(); cb.Clear(); h.World.Clear();
        return (h, a, ca, b, cb);
    }

    private static Monster? Live(MapTestHarness h, ushort spawnId) => h.State.FindMonster(Monster.MakeId(spawnId, 1, 0));

    private static async Task Kill(MapTestHarness h, ClientSession s, Monster mon)
    {
        for (int i = 0; i < 20 && mon.Hp > 0; i++) await h.CastAndHitAsync(s, MapTestHarness.DefendReq(s.CharId, mon.Id));
    }

    private static (byte Type, ushort Id, byte Country, uint Guild, byte Cur) OccupyAck(MapTestHarness h)
    {
        var r = new PacketReader(h.World.Last(Msg.MW_LOCALOCCUPY_ACK)!);
        return (r.ReadByte(), r.ReadUInt16(), r.ReadByte(), r.ReadUInt32(), r.ReadByte());
    }

    [Fact]
    public async Task AtWar_TheGatesClose_AndTheGatekeepersComeOut()
    {
        var (h, _, _, _, _) = await Setup();

        await h.Service.DispatchWorldAsync(LocalEnable(1));

        Assert.NotNull(Live(h, GateL)); Assert.NotNull(Live(h, GateR));
        Assert.Null(Live(h, Boss));
        Assert.False(h.State.FindSwitch(1, Map, SwL)!.Opened);
        Assert.False(h.State.FindSwitch(1, Map, SwR)!.Opened);
    }

    [Fact]
    public async Task AGatekeepersDeath_OpensItsGate_BringsTheBossOut_AndTellsThoseInTheFort()
    {
        var (h, a, ca, _, cb) = await Setup();
        await h.Service.DispatchWorldAsync(LocalEnable(1));
        ca.Clear(); cb.Clear();                                             // the war news

        await Kill(h, a, Live(h, GateL)!);

        Assert.True(h.State.FindSwitch(1, Map, SwL)!.Opened);
        Assert.NotNull(Live(h, Boss));
        var news = ca.Last(Msg.CS_SYSTEMMSG_ACK);
        Assert.NotNull(news);
        var r = new PacketReader(news!);
        Assert.Equal(((byte)6 /* SM_BATTLE_OPENGATE */, "Moswood"), (r.ReadByte(), r.ReadString()));
        Assert.False(cb.Has(Msg.CS_SYSTEMMSG_ACK));                       // B is not in the fort
    }

    [Fact]
    public async Task TheBossFalls_TheFortGoesToTheGuildThatHurtItMost()
    {
        var (h, a, _, b, _) = await Setup();
        a.Char!.GuildId = 77; b.Char!.GuildId = 88;
        await h.Service.DispatchWorldAsync(LocalEnable(1));
        await Kill(h, a, Live(h, GateL)!);
        var boss = Live(h, Boss)!;

        await h.CastAndHitAsync(b, MapTestHarness.DefendReq(b.CharId, boss.Id));
        await Kill(h, a, boss);

        Assert.Equal(((byte)1, Fort, C, 77u, D), OccupyAck(h));
        Assert.True(h.Service.Territories[Fort].Occupied);
    }

    [Fact]
    public async Task AGuildlessKiller_TakesItForItsCountry_AndTheGodMonsterPoints()
    {
        var (h, a, ca, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(LocalEnable(1));
        await Kill(h, a, Live(h, GateL)!);

        await Kill(h, a, Live(h, Boss)!);

        Assert.Equal(((byte)1, Fort, C, 0u, D), OccupyAck(h));
        var r = new PacketReader(ca.Last(Msg.CS_PVPPOINT_ACK)!);
        r.ReadUInt32();
        Assert.Equal((150u, (byte)4 /* PVPE_GODMONKILL */), (r.ReadUInt32(), r.ReadByte()));
    }

    [Fact]
    public async Task TheCapture_TurnsTheFortsMonsters()
    {
        var (h, a, ca, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(LocalEnable(1));
        await Kill(h, a, Live(h, GateL)!);

        await Kill(h, a, Live(h, Boss)!);

        Assert.Equal(C, Live(h, Guard)!.Country);
        var r = new PacketReader(ca.Last(Msg.CS_CHANGECOLOR_ACK)!);
        r.ReadByte(); r.ReadUInt32(); r.ReadByte();
        Assert.Equal(C, r.ReadByte());
    }

    [Fact]
    public async Task TheCaptureColorsTheSameGuardAsAllyOrEnemyForEachViewer()
    {
        var (h, a, ca, _, cb) = await Setup();
        await h.Service.DispatchWorldAsync(LocalEnable(1));
        await Kill(h, a, Live(h, GateL)!);
        ca.Clear(); cb.Clear();
        await Kill(h, a, Live(h, Boss)!);

        var guard = Live(h, Guard)!;
        foreach (var (channel, expected) in new[] { (ca, (byte)0), (cb, (byte)3) })
        {
            var updates = channel.Sent.Where(p => PacketHeader.ReadId(p) == Msg.CS_CHANGECOLOR_ACK)
                .Select(p =>
                {
                    var r = new PacketReader(p);
                    return (Type: r.ReadByte(), Id: r.ReadUInt32(), Color: r.ReadByte(), Country: r.ReadByte());
                }).Where(p => p.Id == guard.Id).ToArray();
            Assert.NotEmpty(updates);
            Assert.Equal((Monster.OtMon, guard.Id, expected, C), updates[^1]);
        }
    }

    [Fact]
    public async Task AtPeace_AFortNobodyTook_IsHeld_AndOpensAgain()
    {
        var (h, _, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(LocalEnable(1));

        await h.Service.DispatchWorldAsync(LocalEnable(4));

        Assert.Equal(((byte)0, Fort, D, 0u, D), OccupyAck(h));
        Assert.True(h.State.FindSwitch(1, Map, SwL)!.Opened);
        Assert.False(h.Service.Territories[Fort].CanBattle);
    }

    [Fact]
    public async Task TheNextWar_IsHeldToo()                               // the C++ skipped every other one
    {
        var (h, _, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(LocalEnable(1));
        await h.Service.DispatchWorldAsync(LocalEnable(4));
        await h.Service.DispatchWorldAsync(Occupy(0, D, 0, ""));
        await h.Service.DispatchWorldAsync(LocalEnable(0));
        h.World.Clear();

        await h.Service.DispatchWorldAsync(LocalEnable(1));
        await h.Service.DispatchWorldAsync(LocalEnable(4));

        Assert.NotNull(h.World.Last(Msg.MW_LOCALOCCUPY_ACK));
    }

    [Fact]
    public async Task AKillInTheFortAtWar_GoesByTheLocalChart_AndIsWrittenDown()
    {
        var (h, a, ca, b, _) = await Setup();
        b.Char!.LocalId = Fort; b.Char.Hp = 5; b.Char.PvpTotalPoint = 50;
        a.Char!.Level = b.Char.Level = VictimLevel;                         // an even kill (PVPE_KILL_E)
        await h.Service.DispatchWorldAsync(LocalEnable(1));

        for (int i = 0; i < 5 && b.Char.Hp > 0; i++)
            await h.CastAndHitAsync(a, MapTestHarness.DefendReq(a.CharId, b.CharId, attackType: 1, targetType: 1));

        var r = new PacketReader(ca.Last(Msg.CS_PVPPOINT_ACK)!);
        Assert.Equal((14u, 14u), (r.ReadUInt32(), r.ReadUInt32()));        // 100% of 14, no death-penalty cut
        var t = h.Service.Territories[Fort];
        Assert.Equal((ushort)1, t.Records[0][a.CharId].Kills);
        Assert.Equal((ushort)1, t.Records[0][b.CharId].Deaths);
        Assert.Equal(1, t.Kills[a.CharId]);
    }

    [Fact]
    public async Task TheWorldsResult_TellsEveryone_AndPaysTheParticipants()
    {
        var (h, a, ca, _, cb) = await Setup();
        h.Service.Territories[Fort].Records[0] = new() { [a.CharId] = new EntryRecord(a.CharId) { Kills = 2 } };

        await h.Service.DispatchWorldAsync(Occupy(1, C, 77, "Wolves"));

        var t = h.Service.Territories[Fort];
        Assert.Equal((C, 77u, "Wolves", (byte)1), (t.Country, t.Guild, t.GuildName, t.LastOccType));
        var r = new PacketReader(cb.Last(Msg.CS_SYSTEMMSG_ACK)!);
        Assert.Equal(((byte)7 /* SM_BATTLE_BOSSDIE */, "Moswood", "Wolves", (ushort)C), (r.ReadByte(), r.ReadString(), r.ReadString(), r.ReadUInt16()));
        Assert.True(cb.Has(Msg.CS_LOCALOCCUPY_ACK));
        var p = new PacketReader(ca.Last(Msg.CS_PVPPOINT_ACK)!);
        p.ReadUInt32(); p.ReadUInt32();
        Assert.Equal((byte)5 /* PVPE_ENTRY */, p.ReadByte());
        var rec = new PacketReader(h.World.Last(Msg.MW_LOCALRECORD_ACK)!);
        Assert.Equal((77u, 590u /* WIN */, (ushort)1, 0u, (ushort)1, a.CharId, (ushort)2),
            (rec.ReadUInt32(), rec.ReadUInt32(), rec.ReadUInt16(), rec.ReadUInt32(), rec.ReadUInt16(), rec.ReadUInt32(), rec.ReadUInt16()));
        var g = new PacketReader(h.World.Last(Msg.MW_GAINPVPPOINT_ACK)!);
        Assert.Equal(((byte)1 /* TOWNER_GUILD */, 77u, 590u, (byte)6), (g.ReadByte(), g.ReadUInt32(), g.ReadUInt32(), g.ReadByte()));
        Assert.Empty(t.Records);
    }

    [Fact]
    public async Task TheFortsNpc_ServesItsCountry_AndDiscountsItsGuild()
    {
        var (h, a, ca, b, cb) = await Setup();
        var t = h.Service.Territories[Fort];
        t.Guild = 77; a.Char!.GuildId = 77;
        var list = new PacketWriter(Msg.CS_NPCITEMLIST_REQ).WriteUInt16(NpcId).ToArray();

        await h.Service.DispatchClientAsync(b, list);                       // Defugel's fort: B may talk, no discount
        await h.Service.DispatchClientAsync(a, list);                       // a Craxion: not served

        var r = new PacketReader(cb.Last(Msg.CS_NPCITEMLIST_ACK)!);
        r.ReadUInt16(); r.ReadByte();
        Assert.Equal((byte)0, r.ReadByte());
        Assert.False(ca.Has(Msg.CS_NPCITEMLIST_ACK));

        t.Country = C;
        await h.Service.DispatchClientAsync(a, list);
        r = new PacketReader(ca.Last(Msg.CS_NPCITEMLIST_ACK)!);
        r.ReadUInt16(); r.ReadByte();
        Assert.Equal((byte)10, r.ReadByte());                              // the fort's guild: 10% off
    }
}
