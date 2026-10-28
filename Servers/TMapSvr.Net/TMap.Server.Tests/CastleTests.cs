using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Territory battles, batch E: the castles — sign-up, the war and its power race, the god balls and towers, the end, the
/// result.</summary>
public class CastleTests
{
    private const ushort Castle = 4, Map = 801, MonId = 500, AttrId = 900, Fort = 1;
    private const ushort GateL = 29937, GateR = 29938, GateC = 29939;
    private const uint DefGuild = 10, AtkGuild = 20, Owner = 2640;
    private const byte D = 0, C = 1, N = 3, CampDefend = 1, CampAttack = 2;

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                       // FTYPE_AL
        t.MonsterTemplates[MonId] = new MonsterTemplate(MonId, 5, AttrId);
        t.MonAttrs[TemplateStore.MonAttrKey(AttrId, 5)] = new MonAttrRow(AttrId, 5, 10, 50, 0);
        foreach (var id in new[] { GateL, GateR, GateC })
            t.MonsterSpawns.Add(new MonsterSpawnDef(new MonSpawnRow(id, Map, 100, 0, 100, 0, N, 1, 0, 100, 0, 30_000, 1, LocalId: Castle),
                new List<MapMonRow> { new(id, MonId, 0, 0, 100) }));
        t.BattleZones[Castle] = new BattleZone(Castle, "Chesed", Map, 0, 0, GateL, GateR, GateC, 295, 301, 302, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0);
        t.BattleZones[Fort] = new BattleZone(Fort, "Moswood", 0, Castle, 25001, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        t.Territories.Add(new TerritoryRow(LocalType.Castle, Castle, C, Owner, "Owners", 0, 0, "", 0));
        t.Territories.Add(new TerritoryRow(LocalType.Occupation, Fort, D, 0, "", 0, 0, "", 0));
        t.LocalOccupy.Add(new LocalOccupyRow(Fort, 2, 77, 1));
        for (ushort i = 1; i <= 4; i++) t.GodTowers.Add(new GodTowerRow(i, Map, 100 + i, 0, 100));
        t.GodBallSpots.Add(new GodBallSpotRow(1, CampDefend, Map, 100, 0, 100));
        t.GodBallSpots.Add(new GodBallSpotRow(2, CampDefend, Map, 101, 0, 100));
        t.GodBallSpots.Add(new GodBallSpotRow(3, CampAttack, Map, 102, 0, 100));
        t.GodBallSpots.Add(new GodBallSpotRow(4, CampAttack, Map, 103, 0, 100));
        t.LevelPvPoint[19] = 14;
        t.PvPointKill[(1, 2)] = (80, 20);
        t.LocalPvPoints[(Castle, 2, 6)] = (1770, 900);                     // WIN
        return t;
    }

    private static byte[] World(ushort id, Action<PacketWriter> body) { var w = new PacketWriter(id); body(w); return w.ToArray(); }
    private static byte[] CastleEnable(byte status, uint second = 2700) => World(Msg.MW_CASTLEENABLE_REQ, w => { w.WriteByte(status); w.WriteUInt32(second); });
    private static byte[] Sides(uint def, uint atk) => World(Msg.MW_CASTLEGUILDCHG_REQ, w =>
    {
        w.WriteUInt16(Castle); w.WriteUInt32(def); w.WriteString("Blue"); w.WriteUInt32(atk); w.WriteString("Red");
        w.WriteInt64(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400);
    });
    private static byte[] Cs(ushort id, Action<PacketWriter> body) { var w = new PacketWriter(id); body(w); return w.ToArray(); }

    /// <summary>A (the defenders' chief) and B (an attacker) in the castle, signed up.</summary>
    private static async Task<(MapTestHarness h, ClientSession a, FakeClientChannel ca, ClientSession b, FakeClientChannel cb)> Setup(bool sides = true)
    {
        var h = new MapTestHarness(Store());
        h.Service.InitTerritories();
        var (a, ca) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: new Character { CharId = 1, Name = "Ann", Level = 19, MaxHp = 100, Hp = 100 });
        var (b, cb) = await h.EnterAsync(2, 2, 2, x: 101, z: 100, name: "Bob", preSeeded: new Character { CharId = 2, Name = "Bob", Level = 19, MaxHp = 100, Hp = 100 });
        foreach (var (s, guild, camp, country) in new[] { (a, DefGuild, CampDefend, D), (b, AtkGuild, CampAttack, C) })
        {
            var ch = s.Char!;
            (ch.MapId, ch.GuildId, ch.Castle, ch.Camp, ch.Country, ch.AidCountry, ch.Level) = (Map, guild, Castle, camp, country, N, 19);
        }
        a.Char!.GuildDuty = 2;                                              // GUILD_DUTY_CHIEF
        if (sides) await h.Service.DispatchWorldAsync(Sides(DefGuild, AtkGuild));
        h.Service.Territories[Fort].NextDefend = long.MaxValue;              // its forts' wars come after: signing up is open
        h.Service.CombatRng = new Random(1);
        ca.Clear(); cb.Clear(); h.World.Clear();
        return (h, a, ca, b, cb);
    }

    private static CastleWar War(MapTestHarness h) => h.Service.Territories[Castle].War!;

    private static async Task Ticks(MapTestHarness h, int n) { for (int i = 0; i < n; i++) await h.Service.OnTimerAsync(); }

    private static (byte Type, uint Win) EndWar(FakeClientChannel c)
    {
        var r = new PacketReader(c.Last(Msg.CS_ENDWAR_ACK)!);
        return (r.ReadByte(), r.ReadUInt32());
    }

    [Fact]
    public async Task WithoutBothGuilds_TheWarEndsAtOnce_AndNothingIsSaved()
    {
        var (h, _, ca, _, _) = await Setup(sides: false);

        await h.Service.DispatchWorldAsync(CastleEnable(1));

        Assert.Equal(((byte)3 /* WIN_NOWAR */, 0u), EndWar(ca));
        Assert.Null(h.World.Last(Msg.MW_CASTLEOCCUPY_ACK));                // taken by no guild: the proc refuses
        var c = h.Service.Territories[Castle];
        Assert.False(c.CanBattle);
        Assert.Equal((byte)4, c.Status);
    }

    [Fact]
    public async Task AtWar_TheGatekeepersComeOut_AndEachSideGetsTwoGodBalls()
    {
        var (h, _, ca, _, _) = await Setup();

        await h.Service.DispatchWorldAsync(CastleEnable(1));

        Assert.NotNull(h.State.FindMonster(Monster.MakeId(GateC, 1, 0)));
        Assert.Equal(new ushort[] { 1, 2, 5, 6 }, War(h).Balls.Keys.ToArray());   // defence 1..4, attack 5..8
        Assert.Equal(4, ca.WithId(Msg.CS_ADDGODBALL_ACK).Count());
        Assert.Equal((2200u, 2200u), (War(h).DefPower, War(h).AtkPower));         // 1800 + half of the 800 forts' bonus each
    }

    [Fact]
    public async Task AMountedBall_MovesThePowerEverySecond_AndAFreshOneComesOut()
    {
        var (h, a, _, _, cb) = await Setup();
        await h.Service.DispatchWorldAsync(CastleEnable(1));

        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_TAKEGODBALL_REQ, w => w.WriteUInt16(1)));
        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_MOUNTGODBALL_REQ, w => w.WriteUInt16(1)));
        await Ticks(h, 1);

        Assert.True(cb.Has(Msg.CS_TAKEGODBALL_ACK));
        Assert.True(cb.Has(Msg.CS_MOUNTGODBALL_ACK));
        Assert.Equal((ushort)1, War(h).Towers[1].Ball!.Id);
        Assert.Equal((2201u, 2199u), (War(h).DefPower, War(h).AtkPower));
        var r = new PacketReader(cb.Last(Msg.CS_BALANCEOFPOWER_ACK)!);
        Assert.InRange(r.ReadFloat(), 50.0f, 50.1f);

        await Ticks(h, 15);
        Assert.Equal(2, War(h).Balls.Values.Count(x => x.Camp == CampDefend));   // a fresh defence ball
    }

    [Fact]
    public async Task ACarrierKnocksTheOtherSidesBallOff_ItGoesBackToItsSpotLater()
    {
        var (h, a, ca, b, _) = await Setup();
        await h.Service.DispatchWorldAsync(CastleEnable(1));
        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_TAKEGODBALL_REQ, w => w.WriteUInt16(1)));
        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_MOUNTGODBALL_REQ, w => w.WriteUInt16(1)));
        await h.Service.DispatchClientAsync(b, Cs(Msg.CS_TAKEGODBALL_REQ, w => w.WriteUInt16(5)));

        await h.Service.DispatchClientAsync(b, Cs(Msg.CS_DEMOUNTGODBALL_REQ, w => w.WriteUInt16(1)));

        Assert.Null(War(h).Towers[1].Ball);
        Assert.True(ca.Has(Msg.CS_DEMOUNTGODBALL_ACK));
        await Ticks(h, 15);
        Assert.True(War(h).Balls[1].Ground);
    }

    [Fact]
    public async Task OneMayNotTakeTheOtherGuildsBall()
    {
        var (h, a, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(CastleEnable(1));

        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_TAKEGODBALL_REQ, w => w.WriteUInt16(5)));

        Assert.Equal((ushort)0, a.Char!.GodBall);
    }

    [Fact]
    public async Task ACarrierLeaving_DropsTheBallWhereItStood()
    {
        var (h, a, _, _, cb) = await Setup();
        await h.Service.DispatchWorldAsync(CastleEnable(1));
        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_TAKEGODBALL_REQ, w => w.WriteUInt16(1)));
        cb.Clear();

        h.Service.OnClientDisconnect(a);

        var ball = War(h).Balls[1];
        Assert.True(ball.Ground);
        Assert.Equal("", ball.Owner);
        Assert.True(cb.Has(Msg.CS_REMOVEGODBALL_ACK));
        Assert.True(cb.Has(Msg.CS_ADDGODBALL_ACK));
    }

    [Fact]
    public async Task ADeathInTheCastleAtWar_IsAKillPointForTheOtherSide()
    {
        var (h, a, _, b, _) = await Setup();
        await h.Service.DispatchWorldAsync(CastleEnable(1));
        a.Char!.Hp = 5;

        for (int i = 0; i < 5 && a.Char.Hp > 0; i++)
            await h.CastAndHitAsync(b, MapTestHarness.DefendReq(b.CharId, a.CharId, attackType: 1, targetType: 1));

        Assert.Equal((ushort)1, War(h).AtkKillPoint);
    }

    [Fact]
    public async Task ASideOutOfPower_Loses()
    {
        var (h, a, ca, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(CastleEnable(1));
        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_TAKEGODBALL_REQ, w => w.WriteUInt16(1)));
        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_MOUNTGODBALL_REQ, w => w.WriteUInt16(1)));
        War(h).AtkPower = 1;

        await Ticks(h, 1);

        Assert.Equal(((byte)2 /* WIN_POWER */, DefGuild), EndWar(ca));
        var r = new PacketReader(h.World.Last(Msg.MW_CASTLEOCCUPY_ACK)!);
        Assert.Equal(((byte)1, Castle, DefGuild, D, AtkGuild), (r.ReadByte(), r.ReadUInt16(), r.ReadUInt32(), r.ReadByte(), r.ReadUInt32()));
        Assert.Empty(War(h).Balls);
        Assert.Equal((ushort)0, a.Char!.GodBall);
    }

    [Fact]
    public async Task WhenTimeRunsOut_TheStrongerSideWins()
    {
        var (h, _, ca, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(CastleEnable(1));
        War(h).AtkPower = 3000;

        await h.Service.DispatchWorldAsync(CastleEnable(4));

        Assert.Equal(((byte)1 /* WIN_TIME */, AtkGuild), EndWar(ca));
    }

    [Fact]
    public async Task TheWorldsResult_NewOwner_FortsWeekWiped_NewsAndGuildPoints()
    {
        var (h, _, ca, _, _) = await Setup();

        await h.Service.DispatchWorldAsync(World(Msg.MW_CASTLEOCCUPY_REQ, w =>
        {
            w.WriteByte(1); w.WriteUInt16(Castle); w.WriteUInt32(AtkGuild); w.WriteByte(C); w.WriteString("Red");
        }));

        var c = h.Service.Territories[Castle];
        Assert.Equal((AtkGuild, C, "Red", 0u, 0u), (c.Guild, c.Country, c.GuildName, c.DefGuildId, c.AtkGuildId));
        Assert.Equal(0u, h.Service.Territories[Fort].OccupyGuild[1]);
        var r = new PacketReader(ca.Last(Msg.CS_SYSTEMMSG_ACK)!);
        Assert.Equal(((byte)16 /* SM_CASTLE_END */, "Chesed", "Red"), (r.ReadByte(), r.ReadString(), r.ReadString()));
        var g = new PacketReader(h.World.Last(Msg.MW_GAINPVPPOINT_ACK)!);
        Assert.Equal(((byte)1, AtkGuild, 1770u), (g.ReadByte(), g.ReadUInt32(), g.ReadUInt32()));
    }

    [Fact]
    public async Task TheChiefSignsAMemberUp_ForItsSide()
    {
        var (h, a, ca, b, cb) = await Setup();

        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_CASTLEAPPLY_REQ, w => { w.WriteUInt16(Castle); w.WriteUInt32(7); }));
        await h.Service.DispatchClientAsync(b, Cs(Msg.CS_CASTLEAPPLY_REQ, w => { w.WriteUInt16(Castle); w.WriteUInt32(8); }));   // not a chief

        var r = new PacketReader(h.World.Last(Msg.MW_CASTLEAPPLY_ACK)!);
        Assert.Equal((a.CharId, a.Key, Castle, 7u, CampDefend), (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt16(), r.ReadUInt32(), r.ReadByte()));
        Assert.Single(h.World.WithId(Msg.MW_CASTLEAPPLY_ACK));

        a.Char!.GuildId = 99;                                               // a guild out of this war
        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_CASTLEAPPLY_REQ, w => { w.WriteUInt16(Castle); w.WriteUInt32(7); }));
        Assert.Equal((byte)4 /* CBS_CANTAPPLY */, new PacketReader(ca.Last(Msg.CS_CASTLEAPPLY_ACK)!).ReadByte());
    }

    [Fact]
    public async Task TheWorldsAnswer_GivesTheChiefItsCastleAndCamp()
    {
        var (h, a, ca, _, _) = await Setup();
        a.Char!.Castle = 0; a.Char.Camp = 0;

        await h.Service.DispatchWorldAsync(World(Msg.MW_CASTLEAPPLY_REQ, w =>
        {
            w.WriteUInt32(a.CharId); w.WriteUInt32(a.Key); w.WriteByte(0); w.WriteUInt16(Castle); w.WriteUInt32(a.CharId); w.WriteByte(CampDefend);
        }));

        Assert.Equal((Castle, CampDefend), (a.Char.Castle, a.Char.Camp));
        Assert.True(ca.Has(Msg.CS_CASTLEAPPLY_ACK));
    }
}
