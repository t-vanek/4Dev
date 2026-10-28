using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Phase 31 — the maintained-skill (buff/debuff) engine: <see cref="StatEngine.CalcAbilityValue"/> stat
/// modification, the apply paths (<c>ForceMaintain</c>, buff-type <c>CS_DEFEND</c> self-buff + monster
/// debuff), the <c>UpdateBuffSkill</c> stack resolution, per-tick expiry (<c>RunMaintainSkills</c> →
/// <c>CS_SKILLEND_ACK</c>), the client-driven <c>CS_SKILLEND_REQ</c>, death cleanup, and the quest
/// DefendSkill grant. All DB-free.
/// </summary>
public class BuffEngineTests
{
    private const byte OtPc = 1, OtMon = 2;
    private const byte SaBuff = 3, SdtAbility = 1, SviIncrease = 1;
    private const byte MtypeStr = 1, MtypePap = 7, MtypeMhp = 50;

    /// <summary>A maintain-type skill: one <c>SA_BUFF</c> <c>SDT_ABILITY</c> row raising <paramref name="exec"/>
    /// by <paramref name="value"/> (per-level <paramref name="valueInc"/> when <c>calc = 1</c>).</summary>
    private static SkillTemplate Buff(ushort id, byte exec, ushort value, uint duration = 10_000,
        byte positive = 1, byte priority = 0, byte calc = 0, ushort valueInc = 0, byte staticFlag = 0)
    {
        var t = new SkillTemplate(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 10, NextLevel: 1, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0,
            SpeedApply: 0, Positive: positive, MapId: 0, Duration: duration, Priority: priority, StaticFlag: staticFlag);
        t.Data.Add(new SkillDataRow(Action: SaBuff, Type: SdtAbility, Attr: 0, Exec: exec, Inc: SviIncrease,
            Value: value, ValueInc: valueInc, Calc: calc));
        return t;
    }

    // FTYPE_HP=8; a CON→MaxHP formula so a +MHP buff is observable on the vitals.
    private static TemplateStore StatStore()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Formulas[8] = new FormulaRow(50, 5.0f, 0f);   // MaxHP = 50 + CON*5
        t.Classes[1] = new StatSeed(3, 0, 5, 0, 0, 2);
        t.Races[1] = new StatSeed(5, 0, 10, 0, 0, 8);
        return t;
    }

    private static (byte isMaintain, uint tick) ReadMaintainFields(byte[] pkt)
    {
        var r = new PacketReader(pkt);
        r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); r.ReadByte();   // attack/target ids + types
        r.ReadUInt32(); r.ReadByte();                                  // host id + type
        r.ReadUInt32(); r.ReadUInt32();                                // act / ani
        byte isM = r.ReadByte();
        uint tick = r.ReadUInt32();
        return (isM, tick);
    }

    // ==================== CalcAbilityValue — the stat layer ====================

    [Fact]
    public void CalcAbilityValue_SumsActiveBuffAbilityRows()
    {
        var ch = new Character { CharId = 1 };
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 900, Level = 1, Template = Buff(900, MtypeStr, 5) });
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 901, Level = 1, Template = Buff(901, MtypeStr, 3) });

        Assert.Equal(8, StatEngine.CalcAbilityValue(ch, 0, MtypeStr));   // 5 + 3, stacked additively
        Assert.Equal(0, StatEngine.CalcAbilityValue(ch, 0, MtypePap));   // a different ability is untouched
    }

    [Fact]
    public void CalcAbilityValue_ScalesPerLevel()
    {
        var ch = new Character { CharId = 1 };
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 900, Level = 3, Template = Buff(900, MtypeStr, 5, calc: 1, valueInc: 4) });
        Assert.Equal(5 + 2 * 4, StatEngine.CalcAbilityValue(ch, 0, MtypeStr));  // Value + (level-1)*ValueInc
    }

    [Fact]
    public void Buff_RaisesMaxHp_ThroughTheGetter()
    {
        var t = StatStore();
        var ch = new Character { Class = 1, Race = 1, Level = 1 };
        Assert.Equal(130u, StatEngine.MaxHp(ch, t));   // 50 + 16*5, no buff

        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 900, Level = 1, Template = Buff(900, MtypeMhp, 40) });
        Assert.Equal(170u, StatEngine.MaxHp(ch, t));   // +40 MHP buff layered on top
    }

    [Fact]
    public void UnlinkedBuff_DoesNotAffectStats()   // DB-free: a buff with no chart template contributes nothing
    {
        var ch = new Character { CharId = 1 };
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 900, Level = 1, Template = null });
        Assert.Equal(0, StatEngine.CalcAbilityValue(ch, 0, MtypeStr));
    }

    [Fact]
    public void IsMaintainType_RequiresAnSaBuffRow()
    {
        Assert.True(Buff(900, MtypeStr, 5).IsMaintainType());

        var dmgOnly = new SkillTemplate(901, 0, 0, 0, 0, 0, 1, 10, 1, 0, 0, 0, 0, 0, Positive: 0, MapId: 0);
        dmgOnly.Data.Add(new SkillDataRow(Action: 0 /*SA_ONCE*/, Type: SdtAbility, Attr: 0, Exec: 30 /*MTYPE_DAMAGE*/,
            Inc: SviIncrease, Value: 10, ValueInc: 0, Calc: 0));
        Assert.False(dmgOnly.IsMaintainType());
    }

    // ==================== ForceMaintain (the direct-grant path) ====================

    private static TemplateStore StoreWith(params SkillTemplate[] skills)
    {
        var store = new TemplateStore();
        foreach (var sk in skills) store.Skills[sk.Id] = sk;
        return store;
    }

    [Fact]
    public async Task ForceMaintain_GrantsBuff_AndBroadcastsMaintainDefendAck()
    {
        var h = new MapTestHarness(StoreWith(Buff(900, MtypeStr, 5)));
        var (s, c) = await h.EnterAsync(1, 1, 1);
        c.Clear();

        bool ok = h.Service.ForceMaintain(s, s.Char!, 900, s.Char!.CharId, OtPc, s.Char!.CharId, OtPc, 0);

        Assert.True(ok);
        Assert.Contains(s.Char!.MaintainSkills, m => m.SkillId == 900);
        var (isMaintain, tick) = ReadMaintainFields(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal((byte)1, isMaintain);
        Assert.Equal(10_000u, tick);        // the template duration
    }

    [Fact]
    public async Task ForceMaintain_UnknownSkill_NoOp()
    {
        var h = new MapTestHarness(StoreWith(Buff(900, MtypeStr, 5)));
        var (s, _) = await h.EnterAsync(1, 1, 1);
        Assert.False(h.Service.ForceMaintain(s, s.Char!, 12345, 1, OtPc, 1, OtPc, 0));  // not in the chart
        Assert.Empty(s.Char!.MaintainSkills);
    }

    // ==================== apply via CS_DEFEND ====================

    [Fact]
    public async Task DefendSelfBuff_AppliesMaintain_AndAnnouncesIt()
    {
        var tpl = Buff(900, MtypeStr, 5);
        var h = new MapTestHarness(StoreWith(tpl));
        var ch = new Character { CharId = 1, Name = "Hero", Level = 5, MaxHp = 100, Hp = 100 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        ch.Skills.Add(new Skill { SkillId = 900, Level = 1, Template = tpl });   // C++ CTSkill::m_pTSKILL link
        var (s, c) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        c.Clear();

        // Cast the positive buff on self: CS_DEFEND with the caster as its own target.
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(ch.CharId, ch.CharId, targetType: OtPc, skillId: 900));

        Assert.Contains(ch.MaintainSkills, m => m.SkillId == 900);
        var (isMaintain, _) = ReadMaintainFields(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal((byte)1, isMaintain);
    }

    [Fact]
    public async Task DefendDebuff_LandsOnTargetMonster()
    {
        var dbt = Buff(910, MtypeStr, 5, positive: 0);   // negative ⇒ debuff
        var h = new MapTestHarness(StoreWith(dbt));
        var ch = new Character { CharId = 1, Name = "Hero", Level = 5, MaxHp = 100, Hp = 100 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        ch.Skills.Add(new Skill { SkillId = 910, Level = 1, Template = dbt });
        var (s, c) = await h.EnterAsync(1, 1, 1, preSeeded: ch);

        var mob = new Monster
        {
            Id = 0x50001, ChartId = 500, Level = 5, MaxHp = 100, Hp = 100, DefendPower = 2,
            PosX = 3663, PosY = 0, PosZ = 557, Channel = 1, MapId = 0, Region = 7,
        };
        h.Service.SpawnMonster(mob);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(ch.CharId, mob.Id, targetType: OtMon, skillId: 910));

        Assert.Contains(mob.MaintainSkills, m => m.SkillId == 910);
        var (isMaintain, _) = ReadMaintainFields(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal((byte)1, isMaintain);
    }

    // ==================== UpdateBuffSkill (stacking) ====================

    [Fact]
    public async Task SameSkillRecast_RefreshesInPlace()
    {
        var h = new MapTestHarness(StoreWith(Buff(900, MtypeStr, 5)));
        var (s, _) = await h.EnterAsync(1, 1, 1);
        uint id = s.Char!.CharId;

        h.Service.ForceMaintain(s, s.Char!, 900, id, OtPc, id, OtPc, 0);
        h.Service.ForceMaintain(s, s.Char!, 900, id, OtPc, id, OtPc, 0);

        Assert.Single(s.Char!.MaintainSkills);   // recast replaced the prior instance (not stacked)
    }

    [Fact]
    public async Task HigherPriorityBuff_ReplacesLower_AndLowerIsRejected()
    {
        // Two buffs contending over the same ability (MTYPE_STR); priorities 0 (low) and 5 (high).
        var h = new MapTestHarness(StoreWith(Buff(900, MtypeStr, 5, priority: 0), Buff(901, MtypeStr, 5, priority: 5)));
        var (s, _) = await h.EnterAsync(1, 1, 1);
        uint id = s.Char!.CharId;

        h.Service.ForceMaintain(s, s.Char!, 900, id, OtPc, id, OtPc, 0);           // low priority
        bool hi = h.Service.ForceMaintain(s, s.Char!, 901, id, OtPc, id, OtPc, 0); // high ⇒ replaces the low
        Assert.True(hi);
        Assert.Single(s.Char!.MaintainSkills);
        Assert.Equal(901, s.Char!.MaintainSkills[0].SkillId);

        bool lo = h.Service.ForceMaintain(s, s.Char!, 900, id, OtPc, id, OtPc, 0); // low vs active high ⇒ rejected
        Assert.False(lo);
        Assert.Single(s.Char!.MaintainSkills);
        Assert.Equal(901, s.Char!.MaintainSkills[0].SkillId);
    }

    // ==================== expiry + CS_SKILLEND ====================

    [Fact]
    public async Task Buff_ExpiresOnTick_AndBroadcastsSkillEnd()
    {
        var h = new MapTestHarness(StoreWith(Buff(900, MtypeStr, 5, duration: 10_000)));
        var (s, c) = await h.EnterAsync(1, 1, 1);
        h.Service.ForceMaintain(s, s.Char!, 900, s.Char!.CharId, OtPc, s.Char!.CharId, OtPc, 0);
        c.Clear();

        h.Service.RunMaintainSkills(5_000);                    // within duration ⇒ still active
        Assert.Single(s.Char!.MaintainSkills);
        Assert.False(c.Has(Msg.CS_SKILLEND_ACK));

        h.Service.RunMaintainSkills(20_000);                   // past duration ⇒ expired
        Assert.Empty(s.Char!.MaintainSkills);
        Assert.True(c.Has(Msg.CS_SKILLEND_ACK));
    }

    [Fact]
    public async Task PermanentBuff_NeverExpires()
    {
        var h = new MapTestHarness(StoreWith(Buff(900, MtypeStr, 5, duration: 0)));  // 0 duration ⇒ permanent
        var (s, _) = await h.EnterAsync(1, 1, 1);
        h.Service.ForceMaintain(s, s.Char!, 900, s.Char!.CharId, OtPc, s.Char!.CharId, OtPc, 0);

        h.Service.RunMaintainSkills(1_000_000);
        Assert.Single(s.Char!.MaintainSkills);
    }

    [Fact]
    public async Task SkillEndReq_RemovesTheMatchingBuff()
    {
        var h = new MapTestHarness(StoreWith(Buff(900, MtypeStr, 5)));
        var (s, c) = await h.EnterAsync(1, 1, 1);
        uint id = s.Char!.CharId;
        h.Service.ForceMaintain(s, s.Char!, 900, id, OtPc, id, OtPc, 0);
        c.Clear();

        var w = new PacketWriter(Msg.CS_SKILLEND_REQ);
        w.WriteUInt32(id);        // dwObjID (self)
        w.WriteByte(OtPc);        // bObjType
        w.WriteUInt32(id);        // dwHostID
        w.WriteUInt32(id);        // dwAttackID (matches the buff's attacker)
        w.WriteByte(OtPc);        // bAttackType
        w.WriteUInt16(900);       // wSkillID
        w.WriteUInt16(0);         // wMapID
        w.WriteByte(1);           // bChannelID
        await h.Service.DispatchClientAsync(s, w.ToArray());

        Assert.Empty(s.Char!.MaintainSkills);
        Assert.True(c.Has(Msg.CS_SKILLEND_ACK));
    }

    // ==================== death cleanup ====================

    [Fact]
    public async Task PlayerDeath_DropsNonStaticBuffs_KeepsStatic()
    {
        var h = new MapTestHarness(MapTestHarness.WithMonsterMelee(StoreWith(
            Buff(900, MtypeStr, 5, staticFlag: 0),   // transient
            Buff(901, MtypeStr, 5, staticFlag: 1)))); // permanent/static
        var ch = new Character { CharId = 1, Name = "Victim", MaxHp = 100, Hp = 20, MaxMp = 50, Mp = 50 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        var (s, _) = await h.EnterAsync(1, 1, 1, preSeeded: ch, x: 100, z: 100);
        h.Service.ForceMaintain(s, ch, 900, ch.CharId, OtPc, ch.CharId, OtPc, 0);
        h.Service.ForceMaintain(s, ch, 901, ch.CharId, OtPc, ch.CharId, OtPc, 0);

        // A monster in melee kills the 20-HP victim (reuses the Phase-20 monster-attack path).
        var mob = new Monster
        {
            Id = 0x50002, ChartId = 500, Level = 5, MaxHp = 100, Hp = 100, PosX = 100, PosZ = 100,
            StartX = 100, StartZ = 100, Mode = 1, TargetId = ch.CharId, AtkMin = 40, AtkMax = 40, AtkSpeed = 2000,
            AtkNextMs = 0, AttackLevel = 10, CritProb = 0, Region = 7, Channel = 1, MapId = 0,
        };
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        await h.MonsterTurnAsync(1_000);

        Assert.Equal(0u, ch.Hp);                                           // dead
        Assert.DoesNotContain(ch.MaintainSkills, m => m.SkillId == 900);   // non-static dropped
        Assert.Contains(ch.MaintainSkills, m => m.SkillId == 901);         // static survives
    }

    // ==================== quest DefendSkill ====================

    [Fact]
    public async Task QuestDefendSkill_GrantsTheTermSkillAsABuff()
    {
        const ushort Giver = 500;
        var store = StoreWith(Buff(900, MtypeStr, 5));
        // A runnable DefendSkill quest (TT_TALKNPC trigger) whose QTT_SKILLID term names skill 900. The C++
        // defend-skill gate grants the buff when the quest is runnable (CanRunQuest == QCT_NONE).
        var q = new QuestTemplate { QuestId = 7000, Type = (byte)QuestType.DefendSkill, TriggerType = 3, TriggerId = Giver };
        q.Terms.Add(new QuestTerm(900, 4 /*QTT_SKILLID*/, 1));
        store.Quests[7000] = q;

        var ch = new Character { CharId = 1, Name = "Hero", Class = 0, Country = 1, Level = 5, MaxHp = 100, Hp = 100 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        var h = new MapTestHarness(store);
        var (s, _) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        s.Char!.Country = 1; s.Char.Class = 0; s.Char.Level = 5; s.Char.MapId = 0;
        h.Service.AddNpc(new Npc { Id = Giver, Type = 2, Country = 3, MapId = 0 });
        h.Service.InitQuests();

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(7000));

        Assert.Contains(s.Char!.MaintainSkills, m => m.SkillId == 900);
    }
}
