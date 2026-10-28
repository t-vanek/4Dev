using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Phase 15 — the skill-data engine (TSKILLDATA/m_vData): GetValue/Calculate/CalcValue,
/// GetAttackType/IsLongAttack, and the MTYPE_DAMAGE scaling of the CS_DEFEND AP−DP roll.</summary>
public class SkillDataTests
{
    // SKILL_DATA_TYPE / INC / ATTR / MAGIC_TYPE constants used by the rows.
    private const byte SdtAbility = 1;
    private const byte SviIncrease = 1, SviDecrease = 2, SviMultiply = 3, SviDivide = 4, SviPercent = 5;
    private const byte SattPhysic = 1, SattLong = 2, SattMagicNo = 3, SattMagicSr = 4;
    private const byte MtypeLap = 9, MtypeDamage = 30;

    private static SkillTemplate Tmpl(float rate = 1f, byte startLevel = 1, byte nextLevel = 0,
        params SkillDataRow[] rows)
    {
        var t = new SkillTemplate(100, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: startLevel, MaxLevel: 100, NextLevel: nextLevel, ReuseDelay: 0, ReuseDelayInc: 0,
            LoopDelay: 0, KindDelay: 0, SpeedApply: 1, Positive: 0, MapId: 0, Rate1stX: rate);
        foreach (var r in rows) t.Data.Add(r);
        return t;
    }

    private static SkillDataRow Row(byte inc, ushort value, byte calc = 0, ushort valueInc = 0,
        byte type = SdtAbility, byte exec = MtypeDamage, byte attr = SattPhysic)
        => new(Action: 0, Type: type, Attr: attr, Exec: exec, Inc: inc, Value: value, ValueInc: valueInc, Calc: calc);

    // ---------------- GetValue (bCalc 0..3) ----------------

    [Fact]
    public void DataValue_Calc0_IsFlat()
        => Assert.Equal(20, Tmpl(rows: Row(SviIncrease, 20, calc: 0)) is var t ? t.DataValue(t.Data[0], 5) : 0);

    [Fact]
    public void DataValue_Calc1_IsLevelLinear()
    {
        var t = Tmpl(rows: Row(SviIncrease, 10, calc: 1, valueInc: 5));
        Assert.Equal(20, t.DataValue(t.Data[0], 3)); // 10 + (3-1)*5
    }

    [Fact]
    public void DataValue_Calc2_IsPowScaled()
    {
        // exp = StartLevel + (level-1)*NextLevel = 2 + 1 = 3; value*pow(10,3)/100 = 5*1000/100 = 50.
        var t = Tmpl(rate: 10f, startLevel: 2, nextLevel: 1, rows: Row(SviIncrease, 5, calc: 2));
        Assert.Equal(50, t.DataValue(t.Data[0], 2));
    }

    [Fact]
    public void DataValue_Calc3_IsLevelLinearDecrease()
    {
        var t = Tmpl(rows: Row(SviIncrease, 100, calc: 3, valueInc: 5));
        Assert.Equal(90, t.DataValue(t.Data[0], 3)); // 100 - (3-1)*5
    }

    // ---------------- Calculate (SVI_* operators, returns the delta) ----------------

    [Fact]
    public void Calculate_Increase_And_Decrease()
    {
        Assert.Equal(20, Tmpl(rows: Row(SviIncrease, 20)) is var a ? a.Calculate(1, 0, 10) : 0);
        Assert.Equal(-20, Tmpl(rows: Row(SviDecrease, 20)) is var b ? b.Calculate(1, 0, 10) : 0);
    }

    [Fact]
    public void Calculate_Multiply_ReturnsDelta()
    {
        // value*calc - value = 10*3 - 10 = 20.
        var t = Tmpl(rows: Row(SviMultiply, 3));
        Assert.Equal(20, t.Calculate(1, 0, 10));
    }

    [Fact]
    public void Calculate_Divide_ReturnsDelta()
    {
        // value/calc - value = 10/2 - 10 = -5.
        var t = Tmpl(rows: Row(SviDivide, 2));
        Assert.Equal(-5, t.Calculate(1, 0, 10));
    }

    [Fact]
    public void Calculate_Percent_ReturnsDelta()
    {
        // (int)(value*150/100) - value = 15 - 10 = 5.
        var t = Tmpl(rows: Row(SviPercent, 150));
        Assert.Equal(5, t.Calculate(1, 0, 10));
    }

    // ---------------- CalcValue + ScaleDamage ----------------

    [Fact]
    public void CalcValue_SumsMatchingRows()
    {
        var t = Tmpl(rows: new[] { Row(SviIncrease, 20), Row(SviPercent, 150) });
        Assert.Equal(25, t.CalcValue(1, SdtAbility, MtypeDamage, 10)); // +20 and +5
    }

    [Fact]
    public void CalcValue_IgnoresNonMatchingExecOrType()
    {
        var t = Tmpl(rows: new[]
        {
            Row(SviIncrease, 20, exec: MtypeLap),          // wrong exec
            Row(SviIncrease, 7, type: 6 /*SDT_STATUS*/),   // wrong type
            Row(SviIncrease, 3),                           // matches
        });
        Assert.Equal(3, t.CalcValue(1, SdtAbility, MtypeDamage, 10));
    }

    [Fact]
    public void ScaleDamage_AppliesAndClampsAtZero()
    {
        Assert.Equal(15u, Tmpl(rows: Row(SviPercent, 150)).ScaleDamage(1, 10)); // 10 + 5
        Assert.Equal(0u, Tmpl(rows: Row(SviDecrease, 999)).ScaleDamage(1, 10)); // 10 - 999 → clamp 0
        Assert.Equal(10u, Tmpl().ScaleDamage(1, 10));                            // no rows ⇒ unchanged
    }

    // ---------------- GetAttackType / IsLongAttack ----------------

    [Fact]
    public void GetAttackType_Classifies()
    {
        Assert.Equal(SkillTemplate.SatPhysic, Tmpl(rows: Row(SviIncrease, 1, attr: SattPhysic)).GetAttackType());
        Assert.Equal(SkillTemplate.SatMagic, Tmpl(rows: Row(SviIncrease, 1, attr: SattMagicNo)).GetAttackType());
        Assert.Equal(SkillTemplate.SatMagic, Tmpl(rows: Row(SviIncrease, 1, attr: SattMagicSr)).GetAttackType());
        Assert.Equal(SkillTemplate.SatNone, Tmpl().GetAttackType());
    }

    [Fact]
    public void IsLongAttack_DetectsLapExecOrLongAttr()
    {
        Assert.True(Tmpl(rows: Row(SviIncrease, 1, exec: MtypeLap)).IsLongAttack());
        Assert.True(Tmpl(rows: Row(SviIncrease, 1, attr: SattLong)).IsLongAttack());
        Assert.False(Tmpl(rows: Row(SviIncrease, 1, attr: SattPhysic)).IsLongAttack());
    }

    // ---------------- integration: CS_DEFEND applies the skill scaling ----------------

    [Fact]
    public async Task Defend_WithDamageSkill_ScalesTheRoll()
    {
        // A PERCENT-200 MTYPE_DAMAGE row doubles the base roll: delta = value*200/100 - value = value.
        var ch = new Character { CharId = 1, Name = "Hero", Level = 10 };
        ch.Skills.Add(new Skill { SkillId = 100, Level = 1, Template = Tmpl(rows: Row(SviPercent, 200)) });

        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        var mob = new Monster { Id = 0x20001, ChartId = 500, Level = 5, MaxHp = 100, Hp = 100, MaxMp = 50,
            DefendPower = 100, PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0 };
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x20001, skillId: 100));

        // naked AP 0, DP 100 ⇒ base roll 5..6; scaled ×2 ⇒ 10..12.
        Assert.True(c.Has(Msg.CS_DEFEND_ACK));
        Assert.InRange(100u - mob.Hp, 10u, 12u); // damage dealt = 2 × the 5..6 roll
    }

    [Fact]
    public async Task Defend_BasicAttack_Unscaled()
    {
        // No skill (skillId 0) ⇒ raw AP−DP roll (Phase-13 behavior preserved).
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = new Monster { Id = 0x20002, ChartId = 500, Level = 5, MaxHp = 100, Hp = 100, MaxMp = 50,
            DefendPower = 100, PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0 };
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x20002)); // skillId 0

        Assert.InRange(100u - mob.Hp, 5u, 6u);
    }
}
