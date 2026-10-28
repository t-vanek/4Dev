using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Phase 42 — the CalcDamage completion: the exec-aware per-data-row damage dispatch (C++
/// <c>CTObjBase::CalcDamage</c>). A skill's <c>SDT_ABILITY</c> rows resolve by attr (physical/magic → AP band +
/// DP) then exec: <c>MTYPE_DAMAGE</c> (30, HP damage), <c>MTYPE_MDAMAGE</c> (88, MP damage), <c>MTYPE_HP</c>
/// (14) / <c>MTYPE_MP</c> (22, direct heal/drain). The per-exec damage map fills <c>CS_DEFEND_ACK</c>; HP/MP
/// apply via an <c>OnDamage</c> floor/clamp; and <c>MTYPE_HI</c>/<c>MI</c> lifedrain fires an
/// <c>MW_GETBLOOD_ACK</c> to the world. All assertions are <b>byte-exact against the wire</b> (the damage map
/// and the world packet are parsed field-by-field). DB-free.
/// </summary>
public class CombatCoreTests
{
    private const byte OtPc = 1;
    private const byte SdtAbility = 1, SaOnce = 0;
    private const byte SviIncrease = 1, SviDecrease = 2;
    private const byte SattNone = 0, SattPhysic = 1, SattMagicNo = 3;
    private const byte MtypeDamage = 30, MtypeMdamage = 88, MtypeHp = 14, MtypeMp = 22, MtypeHi = 38, MtypeMi = 40;

    private static SkillTemplate Skill(ushort id, params SkillDataRow[] rows)
    {
        var t = new SkillTemplate(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 10, NextLevel: 1, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0,
            KindDelay: 0, SpeedApply: 0, Positive: 0, MapId: 0);
        foreach (var r in rows) t.Data.Add(r);
        return t;
    }

    private static SkillDataRow DRow(byte exec, byte attr, byte inc, ushort value)
        => new(Action: SaOnce, Type: SdtAbility, Attr: attr, Exec: exec, Inc: inc, Value: value, ValueInc: 0, Calc: 0);

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Monster mon)> Setup(
        SkillTemplate skill, uint monHp = 100, uint monMp = 100, uint monDp = 0, uint monMdp = 0)
    {
        var ch = new Character { CharId = 1, Name = "Hero", Level = 10 };
        ch.Skills.Add(new Skill { SkillId = skill.Id, Level = 1, Template = skill });
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        var mon = new Monster
        {
            Id = 0x30001, ChartId = 500, Level = 5, MaxHp = monHp, Hp = monHp, MaxMp = monMp, Mp = monMp,
            DefendPower = monDp, MagicDefPower = monMdp, PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0,
        };
        h.Service.SpawnMonster(mon);
        h.Service.CombatRng = new Random(1);
        c.Clear();
        return (h, s, c, mon);
    }

    // Walk the CS_DEFEND_ACK body to the trailing damage map and read every {exec, value} entry.
    private static List<(byte exec, uint value)> DamageMap(byte[] pkt)
    {
        var r = new PacketReader(pkt);
        r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); r.ReadByte();       // attack/target ids + types
        r.ReadUInt32(); r.ReadByte();                                     // host id + type
        r.ReadUInt32(); r.ReadUInt32();                                   // act / ani
        r.ReadByte(); r.ReadUInt32();                                     // bIsMaintain / dwMaintainTick
        r.ReadByte(); r.ReadByte();                                       // bHit / bAtkHit
        r.ReadUInt16(); r.ReadByte();                                     // wAttackLevel / bAttackerLevel
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();   // pys/mg power band
        r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();           // canSelect/cancel/country/aid
        r.ReadUInt16(); r.ReadByte(); r.ReadUInt16(); r.ReadByte();       // skillId/skillLevel/backSkill/perform
        for (int i = 0; i < 6; i++) r.ReadFloat();                        // atk/def positions
        byte count = r.ReadByte();
        var map = new List<(byte, uint)>();
        for (int i = 0; i < count; i++) map.Add((r.ReadByte(), r.ReadUInt32()));
        return map;
    }

    // ==================== direct HP/MP execs (deterministic — no AP−DP roll) ====================

    [Fact]
    public async Task MtypeHp_Drain_ReducesHp_MapKeyed14()
    {
        var (h, s, c, mon) = await Setup(Skill(200, DRow(MtypeHp, SattPhysic, SviDecrease, 20)), monHp: 100);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 200));

        Assert.Equal(80u, mon.Hp);                                         // exactly −20 (direct, off MaxHP)
        Assert.Equal(new (byte, uint)[] { (MtypeHp, 20) }, DamageMap(c.Last(Msg.CS_DEFEND_ACK)!).ToArray());
    }

    [Fact]
    public async Task MtypeMp_Drain_ReducesMp_HpUntouched_MapKeyed22()
    {
        var (h, s, c, mon) = await Setup(Skill(201, DRow(MtypeMp, SattPhysic, SviDecrease, 15)), monMp: 50);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 201));

        Assert.Equal(35u, mon.Mp);                                         // −15 MP
        Assert.Equal(100u, mon.Hp);                                        // HP pool untouched
        Assert.Equal(new (byte, uint)[] { (MtypeMp, 15) }, DamageMap(c.Last(Msg.CS_DEFEND_ACK)!).ToArray());
    }

    [Fact]
    public async Task MtypeHp_Heal_RestoresWoundedMonster_NotInMap()
    {
        // A +20 MTYPE_HP row heals the defender (nInc > 0 ⇒ negative nDamageHP ⇒ OnDamage restores); heals
        // are not reported in the damage map (C++ dwValue = 0 for the heal branch).
        var (h, s, c, mon) = await Setup(Skill(202, DRow(MtypeHp, SattPhysic, SviIncrease, 20)), monHp: 100);
        mon.Hp = 50;

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 202));

        Assert.Equal(70u, mon.Hp);                                         // 50 + 20, clamped to MaxHP 100
        Assert.Empty(DamageMap(c.Last(Msg.CS_DEFEND_ACK)!));               // heal not shown
    }

    [Fact]
    public async Task MtypeHp_Heal_ClampsToMax()
    {
        var (h, s, c, mon) = await Setup(Skill(203, DRow(MtypeHp, SattPhysic, SviIncrease, 80)), monHp: 100);
        mon.Hp = 90;

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 203));

        Assert.Equal(100u, mon.Hp);                                        // 90 + 80 clamped to MaxHP
    }

    // ==================== MTYPE_MDAMAGE (MP damage via the AP−DP roll, key 88) ====================

    [Fact]
    public async Task MtypeMdamage_DrainsMp_MapKeyed88_HpUntouched()
    {
        var (h, s, c, mon) = await Setup(Skill(210, DRow(MtypeMdamage, SattMagicNo, SviIncrease, 0)),
            monHp: 100, monMp: 100, monMdp: 0);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 210));

        var map = DamageMap(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Single(map);
        Assert.Equal(MtypeMdamage, map[0].exec);          // key = MTYPE_MDAMAGE (88), not MTYPE_DAMAGE
        Assert.InRange(map[0].value, 5u, 6u);             // magic AP(0) − MDP(0) floors to a=5,b=7 ⇒ roll 5..6
        Assert.Equal(100u - map[0].value, mon.Mp);        // MP reduced by the rolled amount
        Assert.Equal(100u, mon.Hp);                       // HP pool untouched
    }

    // ==================== multi-row (HP + MP components → two map entries) ====================

    [Fact]
    public async Task MultiRow_HpAndMp_ProducesTwoMapEntries()
    {
        var (h, s, c, mon) = await Setup(Skill(220,
            DRow(MtypeDamage, SattPhysic, SviIncrease, 0),    // physical HP damage
            DRow(MtypeMdamage, SattMagicNo, SviIncrease, 0)), // magic MP damage
            monHp: 100, monMp: 100);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 220));

        var map = DamageMap(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal(2, map.Count);
        var hp = Assert.Single(map, e => e.exec == MtypeDamage);   // one MTYPE_DAMAGE (HP) entry
        var mp = Assert.Single(map, e => e.exec == MtypeMdamage);  // one MTYPE_MDAMAGE (MP) entry
        Assert.Equal(100u - hp.value, mon.Hp);
        Assert.Equal(100u - mp.value, mon.Mp);
    }

    // ==================== MTYPE_DAMAGE byte-fidelity: full roll on a kill ====================

    [Fact]
    public async Task MtypeDamage_OnKill_MapShowsFullRoll_HpFloorsAtZero()
    {
        // A +100 MTYPE_DAMAGE scaling deals ~105 on a 3-HP monster: HP floors at 0, but the damage map reports
        // the FULL rolled value (C++ nDamageHP is uncapped; OnDamage floors HP) — not the 3 HP actually removed.
        var (h, s, c, mon) = await Setup(Skill(230, DRow(MtypeDamage, SattPhysic, SviIncrease, 100)), monHp: 3);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 230));

        var map = DamageMap(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Single(map);
        Assert.Equal(MtypeDamage, map[0].exec);
        Assert.InRange(map[0].value, 105u, 106u);          // full roll (5..6 + 100), NOT clamped to 3
        Assert.Equal(0u, mon.Hp);                          // HP floored at 0
        Assert.True(c.Has(Msg.CS_DIE_ACK));
    }

    // ==================== SATT_NONE row deals nothing (C++ switch case SATT_NONE: break) ====================

    [Fact]
    public async Task SattNone_DamageRow_DealsNoDamage()
    {
        var (h, s, c, mon) = await Setup(Skill(240, DRow(MtypeDamage, SattNone, SviIncrease, 100)), monHp: 100);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 240));

        Assert.Equal(100u, mon.Hp);                        // attr NONE ⇒ the component is skipped
        Assert.Empty(DamageMap(c.Last(Msg.CS_DEFEND_ACK)!));
    }

    // ==================== lifedrain (MTYPE_HI / MI → MW_GETBLOOD_ACK), byte-exact ====================

    [Fact]
    public async Task Lifedrain_Hi_SendsGetBloodToWorld_ByteExact()
    {
        // A physical HP-damage row (so DamageHp > 0) + an MTYPE_HI leech row of a flat 7.
        var (h, s, c, mon) = await Setup(Skill(250,
            DRow(MtypeDamage, SattPhysic, SviIncrease, 0),
            DRow(MtypeHi, SattPhysic, SviIncrease, 7)), monHp: 100);
        h.World.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 250, hostId: 0xABCD));

        var b = new PacketReader(h.World.Last(Msg.MW_GETBLOOD_ACK)!);
        Assert.Equal(1u, b.ReadUInt32());          // dwAtkID = the attacker
        Assert.Equal(OtPc, b.ReadByte());          // bAtkType = OT_PC
        Assert.Equal(1u, b.ReadUInt32());          // dwHostID is the authenticated attacker
        Assert.Equal(MtypeHi, b.ReadByte());       // bBloodType = MTYPE_HI (38)
        Assert.Equal(7u, b.ReadUInt32());          // dwBlood = the flat leech amount
    }

    [Fact]
    public async Task Lifedrain_Mi_LeechesMp_WhenMpDamageDealt()
    {
        var (h, s, c, mon) = await Setup(Skill(251,
            DRow(MtypeMdamage, SattMagicNo, SviIncrease, 0),
            DRow(MtypeMi, SattMagicNo, SviIncrease, 3)), monMp: 100, monMdp: 0);
        h.World.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 251));

        var b = new PacketReader(h.World.Last(Msg.MW_GETBLOOD_ACK)!);
        b.ReadUInt32(); b.ReadByte(); b.ReadUInt32();   // atkId / atkType / hostId
        Assert.Equal(MtypeMi, b.ReadByte());            // bBloodType = MTYPE_MI (40)
        Assert.Equal(3u, b.ReadUInt32());               // dwBlood
    }

    [Fact]
    public async Task Lifedrain_NoHpDamage_NoGetBlood()
    {
        // An MTYPE_HI row on a skill that deals only MP damage ⇒ no HP damage ⇒ no HP leech packet.
        var (h, s, c, mon) = await Setup(Skill(252,
            DRow(MtypeMdamage, SattMagicNo, SviIncrease, 0),
            DRow(MtypeHi, SattPhysic, SviIncrease, 7)), monMp: 100);
        h.World.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 252));

        Assert.False(h.World.Has(Msg.MW_GETBLOOD_ACK));
    }
}
