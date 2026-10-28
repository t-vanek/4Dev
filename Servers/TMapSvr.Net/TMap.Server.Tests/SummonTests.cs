using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Summoning by skill and summon combat (C++ PerformSkill SDT_RECALL, TObjBase.cpp:3134; OnCS_SKILLUSE_REQ /
/// OnCS_FINISHSKILL_ACK with a summon attacker; OnCS_DEFEND_REQ with a summon target). Fixtures mirror the live data:
/// an arrow-rain placed object (recall type 4), the sorcerer's main summon (1), an auto-AI summon (2), a crystal (5).
/// </summary>
public class SummonTests
{
    private const byte OtPc = 1, OtMon = 2, OtRecall = 7, OtSelf = 11, TcontryN = 3;
    private const ushort RainSkill = 600, RitualSkill = 601, CrystalSkill = 602, AutoSkill = 603, EyeSkill = 604;
    private const ushort RainMon = 20001, RitualMon = 21100, CrystalMon = 22200, AutoMon = 22300, EyeMon = 22201;
    private const ushort SummonHit = 700, RainHit = 425, AuraSkill = 643, BombSkill = 626;
    private const ushort RainAttr = 301, RitualAttr = 2001, CrystalAttr = 1001;
    private const byte Level = 10, SkillLevel = 3;
    private const uint Key = 4;

    private static SkillTemplate Skill(ushort id, byte positive = 1, uint duration = 0, uint durationInc = 0, byte range = 0,
        byte maxLevel = 10)
        => new(id, 0, 0, 0, 0, 0, 1, maxLevel, 1, 0, 0, 0, 0, 0, positive, 0xFFFF, Duration: duration, DurationInc: durationInc,
            TargetRange: range);

    private static TemplateStore Store()
    {
        var t = MapTestHarness.WithMonsterMelee();
        void Summon(ushort id, ushort mon, uint duration, uint inc, byte range)
        {
            var s = Skill(id, duration: duration, durationInc: inc, range: range);
            s.Data.Add(new SkillDataRow(0, 2 /* SDT_RECALL */, 0, 12, 0, mon, 0, 0));
            t.Skills[id] = s;
        }
        Summon(RainSkill, RainMon, 8000, 1000, 1);
        Summon(RitualSkill, RitualMon, 0, 0, 0);
        Summon(CrystalSkill, CrystalMon, 30000, 10000, 1);
        Summon(AutoSkill, AutoMon, 0, 0, 0);
        Summon(EyeSkill, EyeMon, 600000, 300000, 1);
        foreach (var id in new[] { SummonHit, RainHit })
        {
            var s = Skill(id, positive: 0);                                    // a hostile skill: it aggros
            s.Data.Add(new SkillDataRow(0, 1 /* SDT_ABILITY */, 1 /* SATT_PHYSIC */, 30 /* MTYPE_DAMAGE */, 0, 0, 0, 0));
            t.Skills[id] = s;
        }
        var aura = Skill(AuraSkill);                                           // the Protection Crystal's buff
        aura.Data.Add(new SkillDataRow(3 /* SA_BUFF */, 1 /* SDT_ABILITY */, 1, 12, 5, 180, 0, 0));
        t.Skills[AuraSkill] = aura;
        var bomb = Skill(BombSkill, positive: 0);                              // the Chaos Eye's bomb: only a link row
        bomb.Data.Add(new SkillDataRow(0, 6 /* SDT_STATUS */, 3, 29 /* SDT_STATUS_LINK */, 1, 627, 0, 0));
        t.Skills[BombSkill] = bomb;
        t.MonsterTemplates[RainMon] = new MonsterTemplate(RainMon, 1, 0, Skill1: RainHit, RecallType: 4, SummonAttr: RainAttr, IsSelf: 1);
        t.MonsterTemplates[RitualMon] = new MonsterTemplate(RitualMon, 1, 0, Skill1: SummonHit, RecallType: 1, SummonAttr: RitualAttr, CanSelect: 1);
        t.MonsterTemplates[AutoMon] = new MonsterTemplate(AutoMon, 1, 0, Skill1: SummonHit, RecallType: 2, SummonAttr: RitualAttr, CanSelect: 1);
        t.MonsterTemplates[EyeMon] = new MonsterTemplate(EyeMon, 1, 0, Skill1: SummonHit, RecallType: 2, SummonAttr: CrystalAttr,
            IsSelf: 1, CanSelect: 1);                                          // the Chaos Eye: a selectable auto-AI object
        t.MonsterTemplates[CrystalMon] = new MonsterTemplate(CrystalMon, 1, 0, Skill1: AuraSkill, RecallType: 5, SummonAttr: CrystalAttr, IsSelf: 1);
        foreach (var attr in new[] { RainAttr, RitualAttr, CrystalAttr })
            t.MonAttrs[TemplateStore.MonAttrKey(attr, Level)] = new MonAttrRow(attr, Level, 400, 100, 0,
                AttackLevel: 30, Ap: 50, MinWap: 0, MaxWap: 0, CritProb: 0);
        return t;
    }

    private static Monster Mob(uint hp = 1000) => new()
    {
        Id = 0x40001, ChartId = 500, Level = 5, MaxHp = hp, Hp = hp, MaxMp = 10, Mp = 10, PosX = 110, PosZ = 110,
        StartX = 110, StartZ = 110, Region = 7, Channel = 1, MapId = 0, Country = TcontryN, AtkMin = 40, AtkMax = 40,
        AttackLevel = 10, AtkSpeed = 2000,
    };

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch, Monster mob)> Setup()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Summoner", MaxHp = 500, Hp = 500, AidCountry = TcontryN };
        foreach (var id in new[] { RainSkill, RitualSkill, CrystalSkill, AutoSkill, EyeSkill })
            ch.Skills.Add(new Map.Skill { SkillId = id, Level = SkillLevel, Template = t.Skills[id] });
        var (s, c) = await h.EnterAsync(1, 1, Key, x: 100, z: 100, preSeeded: ch);
        ch.Level = Level;
        var mob = Mob();
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new FixedRandom(0);
        c.Clear(); h.World.Clear();
        return (h, s, c, ch, mob);
    }

    private static byte[] Finish(uint objId, byte type, ushort skillId, (uint id, byte type)[] targets, float x = 100, float z = 100,
        bool fake = false)
    {
        var w = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        w.WriteUInt32(1); w.WriteUInt32(objId); w.WriteByte(type);
        w.WriteFloat(x); w.WriteFloat(0); w.WriteFloat(z);
        w.WriteUInt16(skillId); w.WriteUInt32(0); w.WriteUInt32(fake ? 1u : 0u); w.WriteUInt16(0);
        w.WriteByte((byte)targets.Length);
        foreach (var (id, tt) in targets) { w.WriteUInt32(id); w.WriteByte(tt); }
        return w.ToArray();
    }

    private static byte[] Cast(ushort skillId, float x = 100, float z = 100) => Finish(1, OtPc, skillId, new[] { (1u, OtPc) }, x, z);

    /// <summary>Casts a world summon and plays the world's answer (the record with an id).</summary>
    private static async Task<RecallMon> Summon(MapTestHarness h, ClientSession s, Character ch, ushort skill, uint id)
    {
        await h.CastAndHitAsync(s, Cast(skill));
        var raw = (byte[])h.World.Last(Msg.MW_CREATERECALLMON_ACK)!.Clone();
        PacketHeader.WriteId(raw, Msg.MW_CREATERECALLMON_REQ);
        BitConverter.GetBytes(id).CopyTo(raw, PacketHeader.Size + 8);
        await h.Service.DispatchWorldAsync(raw);
        return ch.Recalls[id];
    }

    // ================================ creation ================================

    [Fact]
    public async Task APlacedObjectSkill_PutsTheObjectOnTheGroundPoint()
    {
        var (h, s, c, ch, _) = await Setup();

        await h.CastAndHitAsync(s, Cast(RainSkill, x: 120, z: 130));

        var obj = Assert.Single(ch.SelfObjs.Values);
        Assert.Equal((120f, 130f), (obj.PosX, obj.PosZ));
        var r = new PacketReader(c.Last(Msg.CS_ADDSELFOBJ_ACK)!);
        Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(obj.Id, r.ReadUInt32()); Assert.Equal(RainMon, r.ReadUInt16());
        r.ReadByte(); r.ReadByte(); r.ReadByte(); Assert.Equal(Level, r.ReadByte());
        Assert.Equal(400u, r.ReadUInt32()); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
        Assert.Equal(120f, r.ReadFloat()); r.ReadFloat(); Assert.Equal(130f, r.ReadFloat()); r.ReadUInt16(); r.ReadUInt16();
        r.ReadByte(); r.ReadByte(); Assert.Equal(1, r.ReadByte());   // action, mode, bNewMember
        r.ReadUInt32(); Assert.Equal(4, r.ReadByte()); r.ReadByte(); Assert.Equal(SkillLevel, r.ReadByte());
        r.ReadUInt16(); Assert.Equal(Level, r.ReadByte());
        for (int i = 0; i < 4; i++) r.ReadUInt32();
        Assert.Equal(8000u + 1000u * (SkillLevel - 1), r.ReadUInt32());   // the skill's buff duration at its level
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.Remaining);
        Assert.False(h.World.Has(Msg.MW_CREATERECALLMON_ACK));   // made locally, no world round trip
    }

    [Fact]
    public async Task APlacedObject_DiesWhenItsTimeIsUp()
    {
        var (h, s, c, ch, _) = await Setup();
        await h.CastAndHitAsync(s, Cast(RainSkill));
        uint id = ch.SelfObjs.Keys.Single();

        for (int i = 0; i < 11; i++) await h.Service.OnTimerAsync();

        Assert.Empty(ch.SelfObjs);
        var r = new PacketReader(c.Last(Msg.CS_DELSELFOBJ_ACK)!);
        Assert.Equal(id, r.ReadUInt32()); Assert.Equal(1, r.ReadByte());
    }

    [Fact]
    public async Task AMainSummon_IsAskedOfTheWorld()
    {
        var (h, s, _, _, _) = await Setup();
        await h.CastAndHitAsync(s, Cast(RitualSkill));

        var r = new PacketReader(h.World.Last(Msg.MW_CREATERECALLMON_ACK)!);
        Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(Key, r.ReadUInt32()); Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(RitualMon, r.ReadUInt16());
        Assert.Equal(RitualAttr | ((uint)Level << 16), r.ReadUInt32());   // MAKELONG(wSummonAttr, min(level, 140))
    }

    [Fact]
    public async Task ANewMainSummon_SendsTheOldOneAway()
    {
        var (h, s, _, ch, _) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        h.World.Clear();

        await h.CastAndHitAsync(s, Cast(RitualSkill));

        var del = new PacketReader(h.World.Last(Msg.MW_RECALLMONDEL_ACK)!);
        del.ReadUInt32(); del.ReadUInt32(); Assert.Equal(900u, del.ReadUInt32());
    }

    [Fact]
    public async Task OnlyOneCrystal_IsKept()
    {
        var (h, s, _, ch, _) = await Setup();
        await h.CastAndHitAsync(s, Cast(CrystalSkill));
        uint first = ch.SelfObjs.Keys.Single();

        await h.CastAndHitAsync(s, Cast(CrystalSkill));

        uint second = Assert.Single(ch.SelfObjs.Keys);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task APlacedObject_CanBeDismissed_AndDiesWithItsOwner()
    {
        var (h, s, _, ch, _) = await Setup();
        await h.CastAndHitAsync(s, Cast(RainSkill));
        uint id = ch.SelfObjs.Keys.Single();

        var del = new PacketWriter(Msg.CS_DELRECALLMON_REQ);
        del.WriteUInt32(id); del.WriteByte(OtSelf);
        await h.Service.DispatchClientAsync(s, del.ToArray());
        Assert.Empty(ch.SelfObjs);
    }

    // ================================ summon attacks ================================

    [Fact]
    public async Task ASummonsSkill_IsAnnouncedWithItsOwnPower()
    {
        var (h, s, c, ch, _) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(900, SummonHit, attackType: OtRecall));

        var r = new PacketReader(c.Last(Msg.CS_SKILLUSE_ACK)!);
        Assert.Equal(0, r.ReadByte()); Assert.Equal(900u, r.ReadUInt32()); Assert.Equal(OtRecall, r.ReadByte());
        Assert.Equal(SummonHit, r.ReadUInt16()); r.ReadUInt16(); r.ReadByte(); r.ReadUInt32(); r.ReadUInt32();
        Assert.Equal(SkillLevel, r.ReadByte());
        Assert.Equal(30, r.ReadUInt16());                               // the stats row's attack level
        r.ReadByte();
        Assert.Equal(50u, r.ReadUInt32());                              // wAP + wMinWAP (no gear on the owner)

        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(900, RainHit, attackType: OtRecall));
        Assert.Equal((byte)SkillUseResult.NotFound, new PacketReader(c.Last(Msg.CS_SKILLUSE_ACK)!).ReadByte());
    }

    [Fact]
    public async Task ASummonsHit_DamagesTheMonster_AndTheOwnerGetsTheCredit()
    {
        var (h, s, c, ch, mob) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        mob.MaxHp = mob.Hp = 400;                                        // 50 is past the 10% keeper mark
        c.Clear();

        await h.CastAndHitAsync(s, Finish(900, OtRecall, SummonHit, new[] { (mob.Id, OtMon) }));

        Assert.Equal(400u - 50u, mob.Hp);
        Assert.Equal(1u, mob.KeeperId);                                  // the owner's kill
        Assert.True(mob.AggroTable.ContainsKey(Monster.AggroKey(900, OtRecall)));   // the old games: a selectable summon
        Assert.Equal((900u, OtRecall), (mob.TargetId, mob.TargetType));  // is hated itself (not 5.0's always-the-owner)
        var r = new PacketReader(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal(900u, r.ReadUInt32()); Assert.Equal(mob.Id, r.ReadUInt32()); Assert.Equal(OtRecall, r.ReadByte());
        r.ReadByte(); Assert.Equal(1u, r.ReadUInt32());                 // host = the owner
    }

    /// <summary>The monster fighting the summon (hate seeded straight into its table).</summary>
    private static void HateSummon(Monster mob, uint summonId)
    {
        mob.EnterBattle(0, 0);
        mob.HostId = 1; mob.TargetId = summonId; mob.TargetType = OtRecall;
        mob.AddAggro(1, summonId, OtRecall, 0, 5);
    }

    [Fact]
    public async Task AStormsHit_AngersTheMonsterAtItsOwner()
    {
        var (h, s, _, ch, mob) = await Setup();
        await h.CastAndHitAsync(s, Cast(RainSkill));       // a "skill" object whose template is not selectable
        uint id = ch.SelfObjs.Keys.Single();

        await h.CastAndHitAsync(s, Finish(id, OtSelf, RainHit, new[] { (mob.Id, OtMon) }));

        Assert.Equal((1u, OtPc), (mob.TargetId, mob.TargetType));       // never SKILLUSE'd: keeps its template's flag
    }

    [Fact]
    public async Task AnEyesHit_AngersTheMonsterAtTheEye()
    {
        var (h, s, _, ch, mob) = await Setup();
        await h.CastAndHitAsync(s, Cast(EyeSkill));
        uint id = ch.SelfObjs.Keys.Single();

        await h.CastAndHitAsync(s, Finish(id, OtSelf, SummonHit, new[] { (mob.Id, OtMon) }));

        Assert.Equal((id, OtSelf), (mob.TargetId, mob.TargetType));     // SKILLUSE's TRUE for OT_SELF
    }

    [Fact]
    public async Task AnUnlearnedSummonSkill_SummonsNothing()
    {
        var (h, s, _, ch, _) = await Setup();
        ch.Skills.Single(k => k.SkillId == RitualSkill).Level = 0;       // listed (TSTARTSKILL) but not learned

        await h.CastAndHitAsync(s, Cast(RitualSkill));

        Assert.False(h.World.Has(Msg.MW_CREATERECALLMON_ACK));
    }

    [Fact]
    public async Task AnUnlearnedSkill_IsNotFound()
    {
        var (h, s, c, ch, _) = await Setup();
        ch.Skills.Single(k => k.SkillId == RitualSkill).Level = 0;

        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, RitualSkill));

        Assert.Equal((byte)SkillUseResult.NotFound, new PacketReader(c.Last(Msg.CS_SKILLUSE_ACK)!).ReadByte());
    }

    [Fact]
    public async Task AnAutoAiSummon_HitsThreeTimesAsHard()
    {
        var (h, s, _, ch, mob) = await Setup();
        await Summon(h, s, ch, AutoSkill, 901);

        await h.CastAndHitAsync(s, Finish(901, OtRecall, SummonHit, new[] { (mob.Id, OtMon) }));

        Assert.Equal(1000u - 150u, mob.Hp);
    }

    [Fact]
    public async Task ADoppelgangersFakeHit_AlwaysMisses()
    {
        var (h, s, _, ch, mob) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        await h.CastAndHitAsync(s, Finish(900, OtRecall, SummonHit, new[] { (mob.Id, OtMon) }, fake: true));
        Assert.Equal(1000u, mob.Hp);
    }

    [Fact]
    public async Task ASkillPlacedObject_HitsWithItsOwnersFigures()
    {
        var (h, s, _, ch, mob) = await Setup();
        await h.CastAndHitAsync(s, Cast(RainSkill));
        uint id = ch.SelfObjs.Keys.Single();

        await h.CastAndHitAsync(s, Finish(id, OtSelf, RainHit, new[] { (mob.Id, OtMon) }));

        Assert.Equal(1000u - 5u, mob.Hp);   // the owner (no charts ⇒ no power): the 5 floor, not the row's 50
    }

    [Fact]
    public async Task OnlyTheOwner_CanMakeItsSummonAttack()
    {
        var (h, s, _, ch, mob) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        var (s2, _) = await h.EnterAsync(2, 2, 2, name: "Other", x: 100, z: 100);

        var w = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        w.WriteUInt32(2); w.WriteUInt32(900); w.WriteByte(OtRecall); w.WriteFloat(0); w.WriteFloat(0); w.WriteFloat(0);
        w.WriteUInt16(SummonHit); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt16(0); w.WriteByte(1);
        w.WriteUInt32(mob.Id); w.WriteByte(OtMon);
        await h.Service.DispatchClientAsync(s2, w.ToArray());

        Assert.Equal(1000u, mob.Hp);
    }

    // ================================ summons as targets ================================

    [Fact]
    public async Task AMonsterHittingASummon_IsReportedByItsOwner()
    {
        var (h, s, c, ch, mob) = await Setup();
        var summon = await Summon(h, s, ch, RitualSkill, 900);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(mob.Id, 900, attackType: OtMon, targetType: OtRecall,
            skillId: MapTestHarness.MonsterMelee));

        Assert.Equal(400u - 40u, summon.Hp);                            // the monster's 40 vs the summon's 0 defence
        Assert.Equal((byte)1, summon.Mode);                             // into battle
        var hp = new PacketReader(c.Last(Msg.CS_HPMP_ACK)!);
        Assert.Equal(900u, hp.ReadUInt32()); Assert.Equal(OtRecall, hp.ReadByte());
    }

    [Fact]
    public async Task AKilledSummon_Dies_AndTheMonsterTurnsOnItsOwner()
    {
        var (h, s, c, ch, mob) = await Setup();
        var summon = await Summon(h, s, ch, RitualSkill, 900);
        HateSummon(mob, 900);
        summon.Hp = 10;
        h.World.Clear(); c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(mob.Id, 900, attackType: OtMon, targetType: OtRecall,
            skillId: MapTestHarness.MonsterMelee));

        Assert.Equal(0u, summon.Hp);
        Assert.True(c.Has(Msg.CS_DIE_ACK));
        var del = new PacketReader(h.World.Last(Msg.MW_RECALLMONDEL_ACK)!);
        del.ReadUInt32(); del.ReadUInt32(); Assert.Equal(900u, del.ReadUInt32());

        var gone = new PacketWriter(Msg.MW_RECALLMONDEL_REQ);
        gone.WriteUInt32(1); gone.WriteUInt32(Key); gone.WriteUInt32(900); gone.WriteByte(1);
        await h.Service.DispatchWorldAsync(gone.ToArray());

        Assert.Empty(ch.Recalls);
        Assert.False(mob.AggroTable.ContainsKey(Monster.AggroKey(900, OtRecall)));
        Assert.True(mob.FindAggro(1, OtPc) > 0);                        // its hate went to the owner
    }

    [Fact]
    public async Task AMonsterAfterASummon_SwingsAtIt_ThroughItsOwner()
    {
        var (h, s, c, ch, mob) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        HateSummon(mob, 900);
        c.Clear();

        await h.MonsterTurnAsync(60_000);

        var r = new PacketReader(c.Last(Msg.CS_MONATTACK_ACK)!);
        Assert.Equal(mob.Id, r.ReadUInt32()); Assert.Equal(900u, r.ReadUInt32()); r.ReadByte();
        Assert.Equal(OtRecall, r.ReadByte());
    }

    [Fact]
    public async Task AMonsterChasingASummon_KeepsItAsItsHostMovesIt()
    {
        var (h, s, _, ch, mob) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        HateSummon(mob, 900);

        await h.Service.DispatchClientAsync(s, MonMove(mob.Id, 105, 105));

        Assert.Equal((900u, OtRecall), (mob.TargetId, mob.TargetType));   // not dropped as a "missing player"
        Assert.Equal((byte)1, mob.Mode);
    }

    [Fact]
    public async Task LosingItsTarget_TheMonsterTurnsOnASummonInView()
    {
        var (h, s, _, ch, mob) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        HateSummon(mob, 900);
        mob.TargetId = 77; mob.TargetType = OtPc;                        // a player who is gone
        mob.AddAggro(77, 77, OtPc, 0, 1);

        await h.Service.DispatchClientAsync(s, MonMove(mob.Id, 105, 105));

        Assert.Equal((900u, OtRecall), (mob.TargetId, mob.TargetType));   // C++ FindNeighbor finds summons too
    }

    private static byte[] MonMove(uint monId, float x, float z)
    {
        var w = new PacketWriter(Msg.CS_MONMOVE_REQ);
        w.WriteUInt16(1); w.WriteUInt32(monId); w.WriteByte(OtMon); w.WriteByte(1); w.WriteUInt16(0);
        w.WriteFloat(x); w.WriteFloat(0); w.WriteFloat(z); w.WriteUInt16(0); w.WriteUInt16(0);
        w.WriteByte(1); w.WriteByte(1); w.WriteByte(3);
        return w.ToArray();
    }

    // ================================ the crystal's aura (CS_DEFEND_REQ from a summon) ================================

    /// <summary>What a client sends for a summon's buff (CheckAutoSKILL / CheckMaintainOBJ): <c>CS_DEFEND_REQ</c>
    /// naming the summon's owner as host, lasting <paramref name="remain"/> ms.</summary>
    private static byte[] AuraReq(uint crystal, uint target, byte targetType, uint remain, ushort skill = AuraSkill)
    {
        var raw = MapTestHarness.DefendReq(crystal, target, attackType: OtSelf, targetType: targetType, skillId: skill, hostId: 1);
        BitConverter.GetBytes(remain).CopyTo(raw, raw.Length - 4);      // dwRemainTick
        return raw;
    }

    [Fact]
    public async Task TheCrystal_BuffsItself_ForWhatIsLeftOfItsLife()
    {
        var (h, s, c, ch, _) = await Setup();
        await h.CastAndHitAsync(s, Cast(CrystalSkill));
        var crystal = ch.SelfObjs.Values.Single();
        c.Clear();

        await h.Service.DispatchClientAsync(s, AuraReq(crystal.Id, crystal.Id, OtSelf, 25_000));

        var buff = Assert.Single(crystal.MaintainSkills);
        Assert.Equal(AuraSkill, buff.SkillId);
        Assert.Equal(25_000u, buff.MaintainTick);
        var r = new PacketReader(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal(crystal.Id, r.ReadUInt32()); Assert.Equal(crystal.Id, r.ReadUInt32()); Assert.Equal(OtSelf, r.ReadByte());
    }

    [Fact]
    public async Task APlayerInTheCrystalsAura_GetsItsBuff_UntilTheyLeaveIt()
    {
        var (h, s, _, ch, _) = await Setup();
        await h.CastAndHitAsync(s, Cast(CrystalSkill));
        var crystal = ch.SelfObjs.Values.Single();

        await h.Service.DispatchClientAsync(s, AuraReq(crystal.Id, 1, OtPc, 25_000));

        var buff = Assert.Single(ch.MaintainSkills);
        Assert.Equal((AuraSkill, crystal.Id, OtSelf, 25_000u), (buff.SkillId, buff.AttackId, buff.AttackType, buff.MaintainTick));
        Assert.Equal((byte)SkillLevel, buff.Level);                     // the crystal's copy of the skill

        var end = new PacketWriter(Msg.CS_SKILLEND_REQ);                 // walked out of range (CheckMaintainOBJ)
        end.WriteUInt32(1); end.WriteByte(OtPc); end.WriteUInt32(1); end.WriteUInt32(crystal.Id); end.WriteByte(OtSelf);
        end.WriteUInt16(AuraSkill); end.WriteUInt16(0); end.WriteByte(1);
        await h.Service.DispatchClientAsync(s, end.ToArray());
        Assert.Empty(ch.MaintainSkills);
    }

    [Fact]
    public async Task OnlyTheReporter_CanBeTheAurasPlayerTarget()
    {
        var (h, s, _, ch, _) = await Setup();
        await h.CastAndHitAsync(s, Cast(CrystalSkill));
        var crystal = ch.SelfObjs.Values.Single();
        var (other, _) = await h.EnterAsync(2, 2, 2, name: "Other", x: 100, z: 100);

        await h.Service.DispatchClientAsync(s, AuraReq(crystal.Id, 2, OtPc, 25_000));   // C++ FindTarget(pPlayer, OT_PC, id)

        Assert.Empty(other.Char!.MaintainSkills);
    }

    [Fact]
    public async Task ASkillWithoutDamageRows_DealsNoDamage()
    {
        var (h, s, _, ch, mob) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);

        await h.CastAndHitAsync(s, Finish(900, OtRecall, BombSkill, new[] { (mob.Id, OtMon) }));

        Assert.Equal(1000u, mob.Hp);        // C++ CalcDamage only walks the skill's rows: the linked skill does the damage
    }

    [Fact]
    public async Task ASummonsAction_IsRelayed()
    {
        var (h, s, c, ch, _) = await Setup();
        await Summon(h, s, ch, RitualSkill, 900);
        c.Clear();
        var w = new PacketWriter(Msg.CS_ACTION_REQ);
        w.WriteUInt32(900); w.WriteByte(OtRecall); w.WriteByte(3); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteByte(1);
        w.WriteUInt16(0); w.WriteUInt16(0);

        await h.Service.DispatchClientAsync(s, w.ToArray());

        var r = new PacketReader(c.Last(Msg.CS_ACTION_ACK)!);
        Assert.Equal(0, r.ReadByte()); Assert.Equal(900u, r.ReadUInt32()); Assert.Equal(OtRecall, r.ReadByte());
    }
}
