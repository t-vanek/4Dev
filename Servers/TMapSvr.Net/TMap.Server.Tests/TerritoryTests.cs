using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Territory battles, batch A: the territories, entering one (zone buffs, item cap), peace zones, the war phases and their
/// news, the war-info window, the world's castle news and the castle fields of CS_ENTER_ACK.</summary>
public class TerritoryTests
{
    private const ushort Fort = 1, DeadFort = 2, Castle = 4, Mission = 101, Sky = 200;
    private const ushort Buff1 = 806, Buff2 = 807, Hostile = 950;
    private const byte D = 0, C = 1, Peace = 4;

    private static BattleZone Zone(ushort id, ushort map, ushort castle = 0, ushort boss = 0, ushort s1 = 0, ushort s2 = 0, byte cap = 0)
        => new(id, $"Zone {id}", map, castle, boss, 0, 0, 0, 0, 0, 0, 0, 0, 0, s1, s2, cap, 0, 0, 0, 0, 0, 0, 0);

    private static SkillTemplate Skill(ushort id, byte positive, byte exec)
    {
        var t = new SkillTemplate(id, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, Positive: positive, MapId: 0xFFFF, Duration: 0);
        t.Data.Add(new SkillDataRow(positive == 1 ? (byte)3 : (byte)0, 1, 0, exec, 1, 1, 0, 0));
        return t;
    }

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        foreach (var z in new[]
        {
            Zone(Fort, 0, castle: Castle, boss: 9), Zone(DeadFort, 0, castle: Castle, boss: 99), Zone(Castle, 801),
            Zone(Mission, 700, s1: Buff1, s2: Buff2, cap: 15), Zone(Sky, 2100),
        })
            t.BattleZones[z.Id] = z;
        t.Territories.Add(new TerritoryRow(LocalType.Mission, Mission, C, 0, "", 0, 0, "", 0));
        t.Territories.Add(new TerritoryRow(LocalType.Castle, Castle, D, 2640, "Knights", 0, 1_900_000_000, "", 0));
        t.Territories.Add(new TerritoryRow(LocalType.SkyGarden, Sky, D, 0, "", 0, 0, "", 0));
        t.Territories.Add(new TerritoryRow(LocalType.Occupation, Fort, D, 2609, "", 0, 1_800_000_000, "Hero", 0));
        t.Territories.Add(new TerritoryRow(LocalType.Occupation, DeadFort, D, 0, "", 0, 0, "", 0));
        t.LocalOccupy.Add(new LocalOccupyRow(Fort, 2, 2609, 1));
        t.MonsterSpawns.Add(new MonsterSpawnDef(new MonSpawnRow(9, 0, 0, 0, 0, 0, 0, 1, 0, 100, 0, 0, 1), new()));
        t.Skills[Buff1] = Skill(Buff1, 1, 1);
        t.Skills[Buff2] = Skill(Buff2, 1, 2);
        t.Skills[Hostile] = Skill(Hostile, 0, 3);
        return t;
    }

    private long _now = 1_790_000_000;

    private async Task<(MapTestHarness h, ClientSession a, FakeClientChannel ca, ClientSession b, FakeClientChannel cb)> Setup()
    {
        var h = new MapTestHarness(Store());
        h.Service.UnixNow = () => _now;
        h.Service.InitTerritories();
        var ch = new Character { CharId = 1, Name = "Ann", Level = 20, MaxHp = 100, Hp = 100, MaxMp = 100, Mp = 100 };
        ch.Skills.Add(new Skill { SkillId = Hostile, Level = 1, Template = h.Service.Territories.Count > 0 ? Store().Skills[Hostile] : null });
        var (a, ca) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: ch);
        var (b, cb) = await h.EnterAsync(2, 2, 2, x: 105, z: 100, name: "Bob");
        a.Char!.Country = C; a.Char.AidCountry = 3; b.Char!.Country = D; b.Char.AidCountry = 3;
        ca.Clear(); cb.Clear(); h.World.Clear();
        return (h, a, ca, b, cb);
    }

    private static byte[] Region(uint charId, ushort local, uint region = 77)
    {
        var w = new PacketWriter(Msg.CS_REGION_REQ);
        w.WriteUInt32(charId); w.WriteByte(1); w.WriteByte(1); w.WriteUInt16(0); w.WriteUInt32(region); w.WriteUInt16(local);
        return w.ToArray();
    }

    private static byte[] World(ushort id, Action<PacketWriter> body) { var w = new PacketWriter(id); body(w); return w.ToArray(); }
    private static byte[] LocalEnable(byte status, uint second, uint start = 0, byte castleDay = 0, uint castleStart = 0)
        => World(Msg.MW_LOCALENABLE_REQ, w => { w.WriteByte(status); w.WriteUInt32(second); w.WriteUInt32(start); w.WriteByte(castleDay); w.WriteUInt32(castleStart); });

    private static (byte Type, uint Second) SysMsg(FakeClientChannel c)
    {
        var r = new PacketReader(c.Last(Msg.CS_SYSTEMMSG_ACK)!);
        byte type = r.ReadByte();
        return (type, r.Remaining >= 4 ? r.ReadUInt32() : 0);
    }

    // ================================ the territories ================================

    [Fact]
    public async Task TheTerritories_AreBuiltFromTheirTables()
    {
        var (h, _, _, _, _) = await Setup();
        var t = h.Service.Territories;

        Assert.Equal(5, t.Count);
        Assert.True(t[Fort].Valid);
        Assert.False(t[DeadFort].Valid);                                         // its boss spawn does not exist
        Assert.False(t[Castle].CanBattle);                                       // a castle fights only in its war
        Assert.True(t[Mission].CanBattle);
        Assert.Equal(new[] { Fort, DeadFort }, t[Castle].Locals.Select(l => l.Id).ToArray());
        Assert.Equal((2609u, (byte)1), (t[Fort].OccupyGuild[1], t[Fort].OccupyType[1]));
        Assert.Equal("Knights", t[Castle].GuildName);
    }

    // ================================ entering one ================================

    [Fact]
    public async Task EnteringOnesOwnCountrysTerritory_GivesItsZoneBuffs_AndItsItemCap()
    {
        var (h, a, ca, _, _) = await Setup();

        await h.Service.DispatchClientAsync(a, Region(1, Mission));

        Assert.Contains(a.Char!.MaintainSkills, m => m.SkillId == Buff1);
        Assert.Contains(a.Char.MaintainSkills, m => m.SkillId == Buff2);
        Assert.Equal((byte)15, new PacketReader(ca.Last(Msg.CS_ITEMLEVELREVISION_ACK)!).ReadByte());
        var region = new PacketReader(h.World.Last(Msg.MW_REGION_ACK)!);
        Assert.Equal((1u, a.Key, 77u), (region.ReadUInt32(), region.ReadUInt32(), region.ReadUInt32()));

        ca.Clear();
        await h.Service.DispatchClientAsync(a, Region(1, 0));
        Assert.DoesNotContain(a.Char.MaintainSkills, m => m.SkillId is Buff1 or Buff2);
        Assert.Equal((byte)0, new PacketReader(ca.Last(Msg.CS_ITEMLEVELREVISION_ACK)!).ReadByte());
    }

    [Fact]
    public async Task AnotherCountrysTerritory_GivesNoBuffs()
    {
        var (h, a, _, _, _) = await Setup();
        a.Char!.Country = D;

        await h.Service.DispatchClientAsync(a, Region(1, Mission));

        Assert.DoesNotContain(a.Char.MaintainSkills, m => m.SkillId is Buff1 or Buff2);
    }

    [Fact]
    public async Task TheZoneBuffs_AreNotSaved()
    {
        var (h, a, _, _, _) = await Setup();
        await h.Service.DispatchClientAsync(a, Region(1, Mission));

        var d = MapService.WithRecalls(MapService.BuildCharSave(a.Char!), a.Char!, h.Service.NowMs);

        Assert.DoesNotContain(d.Maintains!, m => m.SkillId is Buff1 or Buff2);
    }

    // ================================ peace zones ================================

    [Fact]
    public async Task InACastleOutOfItsWar_NoHostileSkill()
    {
        var (h, a, ca, _, _) = await Setup();
        await h.Service.DispatchClientAsync(a, Region(1, Castle));

        await h.Service.DispatchClientAsync(a, MapTestHarness.SkillUseReq(1, Hostile, targets: new[] { (2u, (byte)1) }));
        Assert.Equal((byte)SkillUseResult.PeaceZone, new PacketReader(ca.Last(Msg.CS_SKILLUSE_ACK)!).ReadByte());

        await h.Service.DispatchWorldAsync(Scoreboard(def: 2640, atk: 2609));
        await h.Service.DispatchWorldAsync(World(Msg.MW_CASTLEENABLE_REQ, w => { w.WriteByte(1); w.WriteUInt32(2700); }));
        await h.Service.DispatchClientAsync(a, MapTestHarness.SkillUseReq(1, Hostile, targets: new[] { (2u, (byte)1) }));
        Assert.Equal((byte)SkillUseResult.Success, new PacketReader(ca.Last(Msg.CS_SKILLUSE_ACK)!).ReadByte());

        await h.Service.DispatchWorldAsync(World(Msg.MW_CASTLEENABLE_REQ, w => { w.WriteByte(4); w.WriteUInt32(0); }));
        Assert.Equal(((byte)4, false), (h.Service.Territories[Castle].Status, h.Service.Territories[Castle].CanBattle));
    }

    [Fact]
    public async Task ACastleWarWithoutBothSides_EndsAtOnce()
    {
        var (h, _, _, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(Scoreboard(def: 2640, atk: 0));

        await h.Service.DispatchWorldAsync(World(Msg.MW_CASTLEENABLE_REQ, w => { w.WriteByte(1); w.WriteUInt32(2700); }));

        Assert.Equal(((byte)4, false), (h.Service.Territories[Castle].Status, h.Service.Territories[Castle].CanBattle));
    }

    private static byte[] Scoreboard(uint def, uint atk) => World(Msg.MW_CASTLEWARINFO_REQ, w =>
    {
        w.WriteUInt16(Castle); w.WriteUInt32(def); w.WriteString("Def"); w.WriteByte(0); w.WriteUInt16(0);
        w.WriteUInt32(atk); w.WriteString("Atk"); w.WriteUInt16(0); w.WriteUInt32(0); w.WriteByte(0);
    });

    [Fact]
    public async Task APlayerStandingInAPeaceTerritory_IsNotHit()
    {
        var (h, a, _, b, cb) = await Setup();
        await h.Service.DispatchClientAsync(b, Region(2, Castle));
        uint hp = b.Char!.Hp;

        await h.CastAndHitAsync(a, MapTestHarness.DefendReq(1, 2, targetType: 1, skillId: Hostile));

        Assert.Equal(hp, b.Char.Hp);
        Assert.False(cb.Has(Msg.CS_DEFEND_ACK));
    }

    // ================================ the war phases ================================

    [Fact]
    public async Task TheFortsWar_RunsItsPhases_WithTheNews()
    {
        var (h, _, ca, _, _) = await Setup();
        var fort = h.Service.Territories[Fort];

        await h.Service.DispatchWorldAsync(LocalEnable(0, 600));
        Assert.Equal((3 /* SM_BATTLE_START_ALARM */, 600u), SysMsg(ca));

        await h.Service.DispatchWorldAsync(LocalEnable(1, 1800));
        Assert.Equal((byte)2 /* SM_BATTLE_START */, SysMsg(ca).Type);
        Assert.Equal((BsBattle: (byte)1, true), (fort.Status, fort.CanBattle));

        await h.Service.DispatchWorldAsync(LocalEnable(4, 180));
        Assert.Equal((5 /* SM_BATTLE_PEACE */, 180u), SysMsg(ca));
        Assert.False(fort.CanBattle);                                            // the 3 minutes of peace

        await h.Service.DispatchWorldAsync(LocalEnable(0, 0));
        Assert.Equal((byte)1 /* SM_BATTLE_NORMAL */, SysMsg(ca).Type);
        Assert.True(fort.CanBattle);
    }

    [Fact]
    public async Task TheFirstLocalEnable_SetsTheNextWars_AndSendsTheCastlesWeek()
    {
        var (h, _, _, _, _) = await Setup();
        uint clt = (uint)DateTimeOffset.FromUnixTimeSeconds(_now).ToLocalTime().TimeOfDay.TotalSeconds;
        uint start = (clt + 3600) % 86400;

        await h.Service.DispatchWorldAsync(LocalEnable(0, 0, start, castleDay: 1, castleStart: start));

        var fort = h.Service.Territories[Fort];
        Assert.InRange(fort.NextDefend - _now, 3599, 3600 + 86400);              // today at the start (or the day after, off the castle's day)
        Assert.Equal((byte)1, h.Service.Territories[Castle].Day);
        var r = new PacketReader(h.World.Last(Msg.MW_CASTLEWARINFO_ACK)!);
        Assert.Equal((Castle, 2640u, (byte)2), (r.ReadUInt16(), r.ReadUInt32(), r.ReadByte()));
        Assert.Equal(Fort, r.ReadUInt16());
        var week = Enumerable.Range(0, 6).Select(_ => (r.ReadUInt32(), r.ReadByte())).ToArray();
        Assert.Equal((2609u, (byte)1), week[0]);                                 // Monday (Sunday, the castle's day, is skipped)
    }

    [Fact]
    public async Task TheMissionNews_CarryTheStartHour()
    {
        var (h, _, ca, _, _) = await Setup();

        await h.Service.DispatchWorldAsync(World(Msg.MW_MISSIONENABLE_REQ, w => { w.WriteByte(0); w.WriteUInt32(20 * 3600); w.WriteUInt32(300); }));

        var r = new PacketReader(ca.Last(Msg.CS_SYSTEMMSG_ACK)!);
        Assert.Equal(((byte)23 /* SM_MISSION_START_ALARM */, (ushort)20, 300u), (r.ReadByte(), r.ReadUInt16(), r.ReadUInt32()));
    }

    [Fact]
    public async Task TheSkyGarden_FightsOnlyInItsWar()
    {
        var (h, _, _, _, _) = await Setup();
        var sky = h.Service.Territories[Sky];
        byte[] SkyEnable(byte status) => World(Msg.MW_SKYGARDENENABLE_REQ, w => { w.WriteByte(status); w.WriteUInt32(0); w.WriteByte(7); w.WriteUInt32(77400); });

        await h.Service.DispatchWorldAsync(SkyEnable(1));
        Assert.True(sky.CanBattle);
        await h.Service.DispatchWorldAsync(SkyEnable(4));
        Assert.False(sky.CanBattle);
        await h.Service.DispatchWorldAsync(SkyEnable(0));                        // out of the peace: open again
        Assert.True(sky.CanBattle);
    }

    // ================================ the world's castle news and the window ================================

    [Fact]
    public async Task TheWorldsScoreboard_AndTheWindow()
    {
        var (h, a, ca, _, _) = await Setup();
        await h.Service.DispatchWorldAsync(World(Msg.MW_CASTLEWARINFO_REQ, w =>
        {
            w.WriteUInt16(Castle); w.WriteUInt32(2640); w.WriteString("Knights"); w.WriteByte(D); w.WriteUInt16(30);
            w.WriteUInt32(2609); w.WriteString("Rangers"); w.WriteUInt16(20);
            w.WriteUInt32(1); w.WriteUInt32(2640); w.WriteUInt32(66);
            w.WriteByte(1); w.WriteByte(D); w.WriteString("Knights"); w.WriteUInt16(66);
        }));
        await h.Service.DispatchWorldAsync(World(Msg.MW_CASTLEAPPLICANTCOUNT_REQ, w => { w.WriteUInt16(Castle); w.WriteUInt32(2609); w.WriteByte(2); w.WriteByte(7); }));
        await h.Service.DispatchWorldAsync(World(Msg.MW_HEROSELECT_REQ, w => { w.WriteUInt16(Castle); w.WriteString("Arthur"); w.WriteInt64(_now); }));

        await h.Service.DispatchClientAsync(a, World(Msg.CS_GUILDLOCALLIST_REQ, _ => { }));
        Assert.True(h.World.Has(Msg.MW_BATTLEMODESTATUS_REQ));
        await h.Service.DispatchWorldAsync(World(Msg.MW_BATTLEMODESTATUS_ACK, w =>
        {
            w.WriteUInt32(1); w.WriteUInt32(a.Key); w.WriteByte(0); w.WriteUInt32(0); w.WriteByte(3); w.WriteByte(0); w.WriteUInt32(0); w.WriteByte(0);
        }));

        var r = new PacketReader(ca.Last(Msg.CS_GUILDLOCALLIST_ACK)!);
        Assert.Equal((ushort)1, r.ReadUInt16());
        Assert.Equal((Castle, "Zone 4"), (r.ReadUInt16(), r.ReadString()));
        r.ReadByte();                                                            // can apply
        Assert.Equal((2640u, "Knights", D), (r.ReadUInt32(), r.ReadString(), r.ReadByte()));
        r.ReadInt64();
        Assert.Equal(("Arthur", "Knights", "Rangers"), (r.ReadString(), r.ReadString(), r.ReadString()));
        Assert.Equal(((ushort)66, (ushort)30, (byte)0), (r.ReadUInt16(), r.ReadUInt16(), r.ReadByte()));
        Assert.Equal(((ushort)0, (ushort)20, (byte)7), (r.ReadUInt16(), r.ReadUInt16(), r.ReadByte()));
        r.ReadUInt16(); r.ReadByte();                                            // my guild's points, status
        Assert.Equal((byte)1, r.ReadByte());                                     // Defugel's top 3
        Assert.Equal(("Knights", (ushort)66), (r.ReadString(), r.ReadUInt16()));
        Assert.Equal((byte)0, r.ReadByte());
        Assert.Equal((ushort)2, r.ReadUInt16());                                 // its two forts
    }

    // ================================ castle / camp on the wire ================================

    [Fact]
    public async Task InsideACastle_TheEnteringPlayersCastleAndCampShow()
    {
        var h = new MapTestHarness(Store());
        h.Service.InitTerritories();
        var ch = new Character { CharId = 1, Name = "Ann", MaxHp = 100, Hp = 100, MapId = 801, Castle = Castle, Camp = 2 };
        var (_, _) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: ch);
        ch.MapId = 801; ch.Castle = Castle; ch.Camp = 2;                          // the enter packets set them
        var other = new Character { CharId = 2, Name = "Bob", MaxHp = 100, Hp = 100, MapId = 801 };
        var (_, cb) = await h.EnterAsync(2, 2, 2, x: 105, z: 100, name: "Bob", preSeeded: other);

        var enter = Wire.ParseEnterCastle(cb.WithId(Msg.CS_ENTER_ACK).First(p => BitConverter.ToUInt32(p, PacketHeader.Size) == 1));
        Assert.Equal((Castle, (byte)2), enter);
    }
}
