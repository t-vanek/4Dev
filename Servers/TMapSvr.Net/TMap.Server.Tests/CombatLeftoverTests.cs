using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// The buffs actions end (C++ EraseBuffByAttack / EraseBuffByDefend), a dispel cast at an enemy (PerformSkill
/// SCT_POSREMOVE on a hostile hit) and a main summon taking part of its owner's damage (DistributeSkill).
/// </summary>
public class CombatLeftoverTests
{
    private const uint A = 1, B = 2;
    private const byte SaBuff = 3, SdtAbility = 1, SdtCure = 5, SdtStatus = 6, SdtRecall = 2;
    private const byte BeaAttack = 1, BeaDefend = 2;
    private const ushort Strike = 900, Dispel = 901, Stance = 902, Summon = 903;
    private const ushort Guard = 950, Hide = 951, Sleep = 952, Might = 953, Share = 954;

    private static SkillTemplate Skill(ushort id, byte positive = 0, byte eraseAct = 0, byte eraseHide = 0, bool hide = false,
        params SkillDataRow[] rows)
    {
        var t = new SkillTemplate(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 1, NextLevel: 1, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0,
            SpeedApply: 0, Positive: positive, MapId: 0, Duration: 60_000, IsHideSkill: hide, EraseAct: eraseAct, EraseHide: eraseHide);
        t.Data.AddRange(rows);
        return t;
    }

    private static SkillDataRow Row(byte action, byte type, byte exec, ushort value = 0, byte attr = 0)
        => new(action, type, attr, exec, 1, value, 0, 0);

    private static readonly SkillDataRow Damage = Row(0, SdtAbility, 30, attr: 1);

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                        // FTYPE_AL: a hit on a PC connects
        foreach (var s in new[]
        {
            Skill(Strike, rows: new[] { Damage }),
            Skill(Dispel, rows: new[] { Damage, Row(0, SdtCure, 6 /* SCT_POSREMOVE */) }),
            Skill(Stance, positive: 1, rows: new[] { Row(SaBuff, SdtStatus, 7 /* ATKMODE */) }),
            Skill(Summon, positive: 1, rows: new[] { Row(0, SdtRecall, 1 /* SER_FIREBALL */) }),
        })
            t.Skills[s.Id] = s;
        return t;
    }

    private static MaintainSkill Buff(ushort id, uint caster, byte eraseAct = 0, bool hide = false, byte positive = 1,
        params SkillDataRow[] rows)
        => new() { SkillId = id, Level = 1, AttackId = caster, AttackType = 1,
                   Template = Skill(id, positive, eraseAct, hide: hide, rows: rows.Length > 0 ? rows : new[] { Row(SaBuff, SdtAbility, 1, 5) }) };

    private sealed record Two(MapTestHarness H, ClientSession Sa, Character A, ClientSession Sb, FakeClientChannel Cb, Character B);

    private static async Task<Two> Setup(uint hpB = 100)
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var a = new Character { CharId = A, Name = "Ann", Level = 19, MaxHp = 100, Hp = 100, MaxMp = 50, Mp = 50 };
        var b = new Character { CharId = B, Name = "Ben", Level = 19, MaxHp = 100, Hp = hpB, MaxMp = 50, Mp = 50 };
        foreach (var id in new[] { Strike, Dispel, Stance, Summon }) a.Skills.Add(new Skill { SkillId = id, Level = 1, Template = t.Skills[id] });
        var (sa, _) = await h.EnterAsync(A, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: a);
        var (sb, cb) = await h.EnterAsync(B, 2, 2, x: 102, z: 100, name: "Ben", preSeeded: b);
        b.Hp = hpB;
        h.Service.CombatRng = new Random(1);
        cb.Clear();
        return new Two(h, sa, a, sb, cb, b);
    }

    private static byte[] Hit(ushort skill, uint target = B)
        => MapTestHarness.DefendReq(A, target, attackType: 1, targetType: 1, skillId: skill, hostId: A);

    private static byte[] Use(ushort skill) => MapTestHarness.SkillUseReq(A, skill, targets: new[] { (B, (byte)1) });

    private static ushort[] Buffs(Character ch) => ch.MaintainSkills.Select(m => m.SkillId).OrderBy(x => x).ToArray();

    // ================================ buffs an attack ends ================================

    [Fact]
    public async Task UsingASkill_EndsOnesBuffsThatStopOnAttacking()
    {
        var x = await Setup();
        x.A.MaintainSkills.Add(Buff(Guard, A, eraseAct: BeaAttack));
        x.A.MaintainSkills.Add(Buff(Might, A));

        await x.H.Service.DispatchClientAsync(x.Sa, Use(Strike));

        Assert.Equal(new[] { Might }, Buffs(x.A));
    }

    [Fact]
    public async Task AHideBuff_OnlyEndsWhenTheSkillUsedSaysSo()
    {
        var x = await Setup();
        x.A.MaintainSkills.Add(Buff(Hide, A, eraseAct: BeaAttack, hide: true));

        await x.H.Service.DispatchClientAsync(x.Sa, Use(Strike));        // Strike has no m_bEraseHide bit

        Assert.Equal(new[] { Hide }, Buffs(x.A));
    }

    [Fact]
    public async Task TakingAStanceOrSummoning_EndsNothing()
    {
        var x = await Setup();
        x.A.MaintainSkills.Add(Buff(Guard, A, eraseAct: BeaAttack));

        await x.H.Service.DispatchClientAsync(x.Sa, MapTestHarness.SkillUseReq(A, Stance, targets: new[] { (A, (byte)1) }));
        await x.H.Service.DispatchClientAsync(x.Sa, MapTestHarness.SkillUseReq(A, Summon, targets: new[] { (A, (byte)1) }));

        Assert.Equal(new[] { Guard }, Buffs(x.A));
    }

    // ================================ buffs being hit ends ================================

    [Fact]
    public async Task AHostileHit_EndsTheTargetsBuffsThatStopOnBeingHit()
    {
        var x = await Setup();
        x.B.MaintainSkills.Add(Buff(Sleep, 99, eraseAct: BeaDefend, positive: 0));   // a sleep someone put on Ben
        x.B.MaintainSkills.Add(Buff(Might, B));

        await x.H.CastAndHitAsync(x.Sa, Hit(Strike));

        Assert.Equal(new[] { Might }, Buffs(x.B));                            // woken up
        Assert.True(x.Cb.Has(Msg.CS_SKILLEND_ACK));
    }

    [Fact]
    public async Task HittingAMonster_WakesItUp()
    {
        var x = await Setup();
        var mob = new Monster { Id = 0x70001, ChartId = 500, Level = 5, MaxHp = 1000, Hp = 1000, MaxMp = 10, Mp = 10,
            PosX = 101, PosZ = 100, Region = 7, Channel = 1, MapId = 0 };
        mob.MaintainSkills.Add(Buff(Sleep, A, eraseAct: BeaDefend, positive: 0));
        x.H.Service.SpawnMonster(mob);

        await x.H.CastAndHitAsync(x.Sa, MapTestHarness.DefendReq(A, mob.Id, skillId: Strike, hostId: A));

        Assert.Empty(mob.MaintainSkills);
    }

    // ================================ a dispel at an enemy ================================

    [Fact]
    public async Task ADispel_StripsTheEnemysBuffs_NotItsDebuffs()
    {
        var x = await Setup();
        x.B.MaintainSkills.Add(Buff(Might, B));
        x.B.MaintainSkills.Add(Buff(Guard, 99, positive: 0));                 // a debuff stays

        await x.H.CastAndHitAsync(x.Sa, Hit(Dispel));

        Assert.Equal(new[] { Guard }, Buffs(x.B));
    }

    // ================================ the summon shares the damage ================================

    private static RecallMon MainSummon(Character owner, uint sharePct)
    {
        var m = new RecallMon { Id = 0x5001, OwnerId = owner.CharId, RecallType = 1 /* TRECALLTYPE_MAIN */, MaxHp = 500, Hp = 500 };
        if (sharePct != 0) m.MaintainSkills.Add(Buff(Share, owner.CharId, rows: Row(SaBuff, SdtStatus, 20 /* DISTRIBUTE */, (ushort)sharePct)));
        owner.Recalls[m.Id] = m;
        return m;
    }

    [Fact]
    public async Task WithTheShareBuff_TheMainSummonTakesItsPartOfTheDamage()
    {
        var x = await Setup();
        var pet = MainSummon(x.B, 100);                                       // 100%: all of it

        await x.H.CastAndHitAsync(x.Sa, Hit(Strike));

        Assert.Equal(100u, x.B.Hp);                                           // Ben loses nothing
        Assert.InRange(pet.Hp, 494u, 495u);                                   // the 5..6 hit went to the summon
    }

    [Fact]
    public async Task WithoutTheShareBuff_TheOwnerTakesItAll()
    {
        var x = await Setup();
        var pet = MainSummon(x.B, 0);

        await x.H.CastAndHitAsync(x.Sa, Hit(Strike));

        Assert.InRange(x.B.Hp, 94u, 95u);
        Assert.Equal(500u, pet.Hp);
    }

    [Fact]
    public async Task ABuffCastOnOnesOwnSummon_LandsOnIt()
    {
        var x = await Setup();
        var pet = MainSummon(x.A, 0);
        pet.InMap = true;
        var share = Skill(Share, positive: 1, rows: new[] { Row(SaBuff, SdtStatus, 20, 50) });
        x.A.Skills.Add(new Skill { SkillId = Share, Level = 1, Template = share });

        await x.H.CastAndHitAsync(x.Sa, MapTestHarness.DefendReq(A, pet.Id, attackType: 1, targetType: 7, skillId: Share, hostId: A));

        Assert.Contains(pet.MaintainSkills, m => m.SkillId == Share);
    }
}
