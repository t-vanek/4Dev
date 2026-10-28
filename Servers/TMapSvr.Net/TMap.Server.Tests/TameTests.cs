using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Taming — the sorcerer's Enslave Monster (SDT_AI/SDT_TEMPT on a corpse, old-source bTame rule) and Evocate Monster
/// (SDT_RECALL/SER_MONSTER on oneself: the tamed monster as a main summon, old sources), plus the tamed monster
/// summoned at login (InitCharInfo). Both skills mirror the live chart: max level 0, held at level 0.
/// </summary>
public class TameTests
{
    private const byte OtPc = 1, OtMon = 2, TcontryN = 3, Sorcerer = 5;
    private const ushort Enslave = 617, Evocate = 618, Plain = 700, Leveled = 701;
    private const ushort TameMon = 500, WildMon = 501, SummonAttr = 1001, MonSkill = 702;
    private const byte Level = 10;
    private const uint Key = 4, EvocateLife = 5000;

    private static SkillTemplate Skill(ushort id, byte maxLevel, uint duration = 0)
        => new(id, 0, 0, 0, 0, 0, 0, maxLevel, 0, 0, 0, 0, 0, 0, 0 /* negative */, 0xFFFF, Duration: duration);

    private static TemplateStore Store()
    {
        var t = MapTestHarness.WithMonsterMelee(chartId: TameMon);
        MapTestHarness.WithMonsterMelee(t, WildMon);
        t.MonsterTemplates[TameMon] = t.MonsterTemplates[TameMon] with
        {
            RecallType = 1, SummonAttr = SummonAttr, CanSelect = 1, Class = 2, Race = 3, Skill1 = MonSkill, Tame = 1,
        };
        var enslave = Skill(Enslave, 0);
        enslave.Data.Add(new SkillDataRow(0, 7 /* SDT_AI */, 3, 4 /* SDT_TEMPT */, 1, 0, 0, 0));
        t.Skills[Enslave] = enslave;
        var evocate = Skill(Evocate, 0, duration: EvocateLife);
        evocate.Data.Add(new SkillDataRow(0, 2 /* SDT_RECALL */, 3, 7 /* SER_MONSTER */, 1, 0, 0, 0));
        t.Skills[Evocate] = evocate;
        foreach (var id in new[] { Plain, Leveled })
        {
            var s = Skill(id, id == Leveled ? (byte)5 : (byte)0);
            s.Data.Add(new SkillDataRow(0, 1 /* SDT_ABILITY */, 1, 30 /* MTYPE_DAMAGE */, 0, 0, 0, 0));
            t.Skills[id] = s;
        }
        t.MonAttrs[TemplateStore.MonAttrKey(SummonAttr, Level)] = new MonAttrRow(SummonAttr, Level, 400, 100, 0,
            AttackLevel: 30, Ap: 50, MinWap: 0, MaxWap: 0, CritProb: 0);
        return t;
    }

    private static Monster Mob(ushort chart = TameMon, byte level = 5, bool dead = false) => new()
    {
        Id = 0x40001, ChartId = chart, Level = level, MaxHp = 1000, Hp = dead ? 0u : 1000u, MaxMp = 10, Mp = 10,
        PosX = 110, PosZ = 120, StartX = 110, StartZ = 120, Region = 7, Channel = 1, MapId = 0, Country = TcontryN,
        AtkMin = 40, AtkMax = 40, AttackLevel = 10, AtkSpeed = 2000, Dead = dead, Status = dead ? (byte)3 : (byte)1,
    };

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch)> Setup(
        byte cls = Sorcerer, ushort tempted = 0)
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Tamer", MaxHp = 500, Hp = 500, AidCountry = TcontryN, Class = cls };
        foreach (var id in new[] { Enslave, Evocate, Plain, Leveled })
            ch.Skills.Add(new Map.Skill { SkillId = id, Level = 0, Template = t.Skills[id] });   // TSTARTSKILL level 0
        ch.Persist.TemptedMon = tempted;
        var (s, c) = await h.EnterAsync(1, 1, Key, x: 100, z: 100, preSeeded: ch);
        ch.Level = Level;
        h.Service.CombatRng = new FixedRandom(0);
        c.Clear(); h.World.Clear();
        return (h, s, c, ch);
    }

    private static Monster Spawn(MapTestHarness h, Monster m) { h.Service.SpawnMonster(m); return m; }

    private static byte[] Finish(ushort skillId, uint target, byte targetType = OtMon)
    {
        var w = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        w.WriteUInt32(1); w.WriteUInt32(1); w.WriteByte(OtPc);
        w.WriteFloat(100); w.WriteFloat(0); w.WriteFloat(100);
        w.WriteUInt16(skillId); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt16(0);
        w.WriteByte(1); w.WriteUInt32(target); w.WriteByte(targetType);
        return w.ToArray();
    }

    /// <summary>bPerform of a CS_DEFEND_ACK (after the fixed 50-byte head).</summary>
    private static byte Perform(byte[] ack)
    {
        var r = new PacketReader(ack);
        r.ReadBytes(4 + 4 + 1 + 1 + 4 + 1 + 4 + 4 + 1 + 4 + 1 + 1 + 2 + 1 + 16 + 1 + 1 + 1 + 1 + 2 + 1 + 2);
        return r.ReadByte();
    }

    private static RecallRecordView ReadCreate(byte[] p)
    {
        var r = new PacketReader(p);
        uint charId = r.ReadUInt32(); r.ReadUInt32(); uint monId = r.ReadUInt32(); ushort mon = r.ReadUInt16(); uint attr = r.ReadUInt32();
        r.ReadUInt16(); r.ReadByte(); r.ReadString();
        byte level = r.ReadByte(), cls = r.ReadByte(), race = r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();
        for (int i = 0; i < 4; i++) r.ReadUInt32();
        byte hit = r.ReadByte(), skillLevel = r.ReadByte();
        float x = r.ReadFloat(); r.ReadFloat(); float z = r.ReadFloat(); r.ReadUInt16(); uint time = r.ReadUInt32();
        byte auto = r.ReadByte(); uint target = r.ReadUInt32(); byte targetType = r.ReadByte();
        byte n = r.ReadByte(); var skills = new List<ushort>();
        for (int i = 0; i < n; i++) skills.Add(r.ReadUInt16());
        Assert.Equal(0, r.Remaining);
        return new(charId, monId, mon, attr, level, cls, race, hit, skillLevel, x, z, time, auto, target, targetType, skills);
    }

    private sealed record RecallRecordView(uint CharId, uint MonId, ushort Mon, uint Attr, byte Level, byte Class, byte Race,
        byte Hit, byte SkillLevel, float X, float Z, uint Time, byte Auto, uint Target, byte TargetType, List<ushort> Skills);

    // ================================ Enslave ================================

    [Fact]
    public async Task Enslave_OnATameableCorpse_TamesItsKind()
    {
        var (h, s, c, ch) = await Setup();
        var mob = Spawn(h, Mob(dead: true));

        await h.CastAndHitAsync(s, Finish(Enslave, mob.Id));

        Assert.Equal(TameMon, ch.Persist.TemptedMon);
        Assert.Equal(1, Perform(c.Last(Msg.CS_DEFEND_ACK)!));
        Assert.Empty(mob.AggroTable);                  // a corpse takes no hate
        Assert.True(mob.Dead); Assert.Equal(0u, mob.Hp);
        Assert.False(c.Has(Msg.CS_DIE_ACK));            // and does not die again
    }

    [Fact]
    public async Task Enslave_OnAMonsterThatCannotBeTamed_DoesNothing()
    {
        var (h, s, c, ch) = await Setup();
        var mob = Spawn(h, Mob(chart: WildMon, dead: true));

        await h.CastAndHitAsync(s, Finish(Enslave, mob.Id));

        Assert.Equal(0, ch.Persist.TemptedMon);
        Assert.Equal(0, Perform(c.Last(Msg.CS_DEFEND_ACK)!));   // PERFORM_FAIL
    }

    [Fact]
    public async Task Enslave_OnAMonsterAboveTheCastersLevel_DoesNothing()
    {
        var (h, s, _, ch) = await Setup();
        var mob = Spawn(h, Mob(level: Level + 1, dead: true));

        await h.CastAndHitAsync(s, Finish(Enslave, mob.Id));

        Assert.Equal(0, ch.Persist.TemptedMon);
    }

    [Fact]
    public async Task Enslave_AtTheCastersOwnLevel_Tames()
    {
        var (h, s, _, ch) = await Setup();
        var mob = Spawn(h, Mob(level: Level, dead: true));

        await h.CastAndHitAsync(s, Finish(Enslave, mob.Id));

        Assert.Equal(TameMon, ch.Persist.TemptedMon);
    }

    [Fact]
    public async Task AnOrdinarySkill_CannotLandOnACorpse()
    {
        var (h, s, c, _) = await Setup();
        var mob = Spawn(h, Mob(dead: true));

        await h.CastAndHitAsync(s, Finish(Plain, mob.Id));

        Assert.False(c.Has(Msg.CS_DEFEND_ACK));        // C++ OS_DEAD && !CanDefendAtDie
    }

    // ================================ innate skills ================================

    [Fact]
    public async Task ASkillWithNoLevels_IsUsableAtLevelZero()
    {
        var (h, s, c, _) = await Setup();
        var mob = Spawn(h, Mob());

        await h.CastAndHitAsync(s, Finish(Plain, mob.Id));   // max level 0, held at 0

        Assert.True(c.Has(Msg.CS_DEFEND_ACK));
        Assert.True(mob.Hp < 1000);
    }

    [Fact]
    public async Task ALevelledSkillHeldAtZero_IsStillNotLearned()
    {
        var (h, s, c, _) = await Setup();
        var mob = Spawn(h, Mob());

        await h.CastAndHitAsync(s, Finish(Leveled, mob.Id));   // max level 5, held at 0

        Assert.False(c.Has(Msg.CS_DEFEND_ACK));
        Assert.Equal(1000u, mob.Hp);
    }

    // ================================ Evocate ================================

    [Fact]
    public async Task Evocate_OnAMonster_CallsNothing()
    {
        var (h, s, c, _) = await Setup(tempted: TameMon);
        var mob = Spawn(h, Mob(chart: WildMon));

        await h.CastAndHitAsync(s, Finish(Evocate, mob.Id));

        Assert.False(h.World.Has(Msg.MW_CREATERECALLMON_ACK));   // the 5.0 three-copies flow is not kept
        Assert.Equal(0, Perform(c.Last(Msg.CS_DEFEND_ACK)!));      // old sources: PERFORM_FAIL off a player
    }

    [Fact]
    public async Task Evocate_OnOneself_CallsTheTamedMonsterAsAMainSummon()
    {
        var (h, s, _, ch) = await Setup(tempted: TameMon);

        await h.CastAndHitAsync(s, Finish(Evocate, 1, OtPc));

        var a = ReadCreate(Assert.Single(h.World.WithId(Msg.MW_CREATERECALLMON_ACK)));
        Assert.Equal((TameMon, (byte)0, 0u), (a.Mon, a.Auto, a.Target));     // one plain main summon, no target
        Assert.Equal(EvocateLife, a.Time);                                     // the skill's duration (old sources)
        float rad = ch.Dir * MathF.PI / 900f;
        Assert.Equal(ch.PosX - 2f * MathF.Sin(rad), a.X, 3);
        Assert.Equal(ch.PosZ - 2f * MathF.Cos(rad), a.Z, 3);
    }

    [Fact]
    public async Task Evocate_OnOneself_WithNothingTamed_CallsNothing()
    {
        var (h, s, _, _) = await Setup();

        await h.CastAndHitAsync(s, Finish(Evocate, 1, OtPc));

        Assert.False(h.World.Has(Msg.MW_CREATERECALLMON_ACK));
    }

    [Fact]
    public async Task Evocate_OnOneself_SendsTheOldMainSummonAway()
    {
        var (h, s, _, ch) = await Setup(tempted: TameMon);
        await h.CastAndHitAsync(s, Finish(Evocate, 1, OtPc));
        var raw = (byte[])h.World.Last(Msg.MW_CREATERECALLMON_ACK)!.Clone();
        PacketHeader.WriteId(raw, Msg.MW_CREATERECALLMON_REQ);
        BitConverter.GetBytes(900u).CopyTo(raw, PacketHeader.Size + 8);
        await h.Service.DispatchWorldAsync(raw);
        h.World.Clear();

        await h.CastAndHitAsync(s, Finish(Evocate, 1, OtPc));

        var del = new PacketReader(h.World.Last(Msg.MW_RECALLMONDEL_ACK)!);
        del.ReadUInt32(); del.ReadUInt32();
        Assert.Equal(900u, del.ReadUInt32());                                 // CheckRecallMon: one main summon at a time
        Assert.True(h.World.Has(Msg.MW_CREATERECALLMON_ACK));
        Assert.Contains(900u, ch.Recalls.Keys);
    }

    [Fact]
    public async Task StartingEvocate_WithNothingTamed_AnswersNeedPrevAct()
    {
        var (h, s, c, _) = await Setup();
        await h.Service.DispatchClientAsync(s, ActionReq(Evocate));
        Assert.Equal((byte)SkillUseResult.NeedPrevAct, c.Last(Msg.CS_ACTION_ACK)![PacketHeader.Size]);

        var (h2, s2, c2, _) = await Setup(tempted: TameMon);
        await h2.Service.DispatchClientAsync(s2, ActionReq(Evocate));
        Assert.Equal((byte)SkillUseResult.Success, c2.Last(Msg.CS_ACTION_ACK)![PacketHeader.Size]);
    }

    private static byte[] ActionReq(ushort skillId)
    {
        var w = new PacketWriter(Msg.CS_ACTION_REQ);
        w.WriteUInt32(1); w.WriteByte(OtPc); w.WriteByte(3); w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteByte(1); w.WriteUInt16(0); w.WriteUInt16(skillId);
        return w.ToArray();
    }

    // ================================ login ================================

    [Fact]
    public async Task ASorcererLogsInWithItsTamedMonster()
    {
        var (h, _, _, ch) = await Setup(tempted: TameMon);

        await h.Service.DispatchWorldAsync(MapTestHarness.CharInfoReq(1, Key));

        var a = ReadCreate(h.World.Last(Msg.MW_CREATERECALLMON_ACK)!);
        Assert.Equal((TameMon, 0u, 0), (a.Mon, a.Time, a.Auto));          // lasting, a plain main summon
        Assert.Equal((100, 1), (a.Hit, a.SkillLevel));
        Assert.Equal(SummonAttr | ((uint)Level << 16), a.Attr);
        float rad = ch.Dir * MathF.PI / 900f;
        Assert.Equal(ch.PosX - 2f * MathF.Sin(rad), a.X, 3);
    }

    [Fact]
    public async Task NoTamedMonsterAtLogin_ForAnotherClassOrNoneTamed()
    {
        var (h, _, _, _) = await Setup(cls: 3, tempted: TameMon);
        await h.Service.DispatchWorldAsync(MapTestHarness.CharInfoReq(1, Key));
        Assert.False(h.World.Has(Msg.MW_CREATERECALLMON_ACK));

        var (h2, _, _, _) = await Setup();
        await h2.Service.DispatchWorldAsync(MapTestHarness.CharInfoReq(1, Key));
        Assert.False(h2.World.Has(Msg.MW_CREATERECALLMON_ACK));
    }
}
