using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Phase 35 — the cure/dispel layer completing the buff engine: a cure skill (one with <c>SDT_CURE</c> rows)
/// cast on self/an ally via <c>CS_DEFEND</c> strips positive/negative maintained skills (<c>SCT_POSREMOVE</c>/
/// <c>SCT_NEGREMOVE</c>) and instant-heals HP/MP (<c>SCT_HP</c>/<c>SCT_MP</c>). All DB-free.
/// </summary>
public class CureDispelTests
{
    private const byte OtPc = 1;
    private const byte SctPosRemove = 6, SctNegRemove = 7, SctHp = 8, SctMp = 19;
    private const byte SdtCure = 5, SviIncrease = 1;
    private const byte SptNegative = 0, SptPositive = 1, SptNone = 2;

    // A bare maintained-skill template with a given SPT_* positivity (enough to be dispel-classified).
    private static SkillTemplate Maint(ushort id, byte positive)
        => new(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0, StartLevel: 1, MaxLevel: 10, NextLevel: 1,
            ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0, SpeedApply: 0, Positive: positive, MapId: 0);

    // A cure skill carrying one SDT_CURE row per (exec, value) — SVI_INCREASE ⇒ a flat value.
    private static SkillTemplate Cure(ushort id, params (byte exec, ushort value)[] rows)
    {
        var t = new SkillTemplate(id, 0, 0, 0, 0, 0, 1, 10, 1, 0, 0, 0, 0, 0, Positive: SptPositive, MapId: 0);
        foreach (var (exec, value) in rows)
            t.Data.Add(new SkillDataRow(Action: 0, Type: SdtCure, Attr: 0, Exec: exec, Inc: SviIncrease,
                Value: value, ValueInc: 0, Calc: 0));
        return t;
    }

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch)> Enter(
        Character ch, params SkillTemplate[] learned)
    {
        var h = new MapTestHarness();
        foreach (var t in learned) ch.Skills.Add(new Skill { SkillId = t.Id, Level = 1, Template = t });
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        h.Service.CombatRng = new Random(1);
        c.Clear();
        return (h, s, c, ch);
    }

    private static Character Wounded(uint hp = 10, uint mp = 10)
        => new() { CharId = 1, Name = "Hero", Level = 5, MaxHp = 100, Hp = hp, MaxMp = 100, Mp = mp,
                   Invens = { new Inven { InvenId = 0xFF } } };

    // ==================== instant heal ====================

    [Fact]
    public async Task Cure_Hp_HealsSelf_AndBroadcasts()
    {
        var cure = Cure(900, (SctHp, 50));
        var (h, s, c, ch) = await Enter(Wounded(hp: 10), cure);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(attackerId: 1, targetId: 1, targetType: OtPc, skillId: 900));

        Assert.InRange(ch.Hp, 60u, 67u);              // 10 + 50 + 0–15% over-heal, clamped ≤ 100
        Assert.True(c.Has(Msg.CS_HPMP_ACK));
        Assert.True(c.Has(Msg.CS_DEFEND_ACK));        // the cure ack (bIsMaintain 0)
    }

    [Fact]
    public async Task Cure_Mp_RestoresSelf()
    {
        var cure = Cure(900, (SctMp, 40));
        var (h, s, _, ch) = await Enter(Wounded(mp: 10), cure);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 900));

        Assert.InRange(ch.Mp, 50u, 56u);              // 10 + 40 + 0–15%
    }

    [Fact]
    public async Task Cure_Hp_ClampsToMax()
    {
        var cure = Cure(900, (SctHp, 200));
        var (h, s, _, ch) = await Enter(Wounded(hp: 90), cure);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 900));

        Assert.Equal(100u, ch.Hp);                    // clamped to MaxHp
    }

    // ==================== dispel ====================

    [Fact]
    public async Task Cure_PosRemove_StripsBuffs_KeepsDebuffsAndNone()
    {
        var cure = Cure(900, (SctPosRemove, 0));
        var ch = Wounded();
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 10, Level = 1, Template = Maint(10, SptPositive) });
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 11, Level = 1, Template = Maint(11, SptNegative) });
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 12, Level = 1, Template = Maint(12, SptNone) });
        var (h, s, c, _) = await Enter(ch, cure);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 900));

        Assert.DoesNotContain(ch.MaintainSkills, m => m.SkillId == 10);   // positive buff stripped
        Assert.Contains(ch.MaintainSkills, m => m.SkillId == 11);         // debuff kept
        Assert.Contains(ch.MaintainSkills, m => m.SkillId == 12);         // SPT_NONE kept
        Assert.True(c.Has(Msg.CS_SKILLEND_ACK));
    }

    [Fact]
    public async Task Cure_NegRemove_StripsDebuffs_KeepsBuffsAndNone()
    {
        var cure = Cure(900, (SctNegRemove, 0));
        var ch = Wounded();
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 10, Level = 1, Template = Maint(10, SptPositive) });
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 11, Level = 1, Template = Maint(11, SptNegative) });
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 12, Level = 1, Template = Maint(12, SptNone) });
        var (h, s, c, _) = await Enter(ch, cure);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 900));

        Assert.Contains(ch.MaintainSkills, m => m.SkillId == 10);         // buff kept
        Assert.DoesNotContain(ch.MaintainSkills, m => m.SkillId == 11);   // debuff stripped
        Assert.Contains(ch.MaintainSkills, m => m.SkillId == 12);         // SPT_NONE kept (strict == SPT_NEGATIVE)
        Assert.True(c.Has(Msg.CS_SKILLEND_ACK));
    }

    [Fact]
    public async Task Cure_HealAndDispel_Combined()
    {
        var cure = Cure(900, (SctPosRemove, 0), (SctHp, 30));
        var ch = Wounded(hp: 10);
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 10, Level = 1, Template = Maint(10, SptPositive) });
        var (h, s, _, _) = await Enter(ch, cure);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 900));

        Assert.DoesNotContain(ch.MaintainSkills, m => m.SkillId == 10);   // buff stripped
        Assert.InRange(ch.Hp, 40u, 44u);                                 // and healed (10 + 30 + 0–15%)
    }
}
