using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Buffs and debuffs on monsters and summons count (C++ <c>CTMonster</c> / <c>CTRecallMon</c> getters → <c>CalcAbilityValue</c>):
/// an attack, defence, level or crit buff changes the figure; a DISWEAPON / DISDEFEND debuff takes away the weapon / armour
/// part; an immunity status stops that kind of damage.
/// </summary>
public class BuffLayerTests
{
    private const byte SaBuff = 3, SdtAbility = 1, SdtStatus = 6, Inc = 1, Dec = 2;
    private const byte Pap = 7, Pdp = 8, Dl = 12, Cr = 13;

    private static MaintainSkill Buff(ushort id, params SkillDataRow[] rows)
    {
        var t = new SkillTemplate(id, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, Positive: 1, MapId: 0xFFFF, Duration: 60_000);
        t.Data.AddRange(rows);
        return new MaintainSkill { SkillId = id, Level = 1, Template = t };
    }

    private static SkillDataRow Ability(byte mtype, byte op, ushort value) => new(SaBuff, SdtAbility, 0, mtype, op, value, 0, 0);
    private static SkillDataRow Status(byte status) => new(SaBuff, SdtStatus, 0, status, 1, 0, 0, 0);

    private static Monster Mob() => new()
    {
        Id = 0x50001, ChartId = 500, Level = 5, MaxHp = 10_000, Hp = 10_000, PosX = 100, PosZ = 100, StartX = 100, StartZ = 100,
        Mode = 1, TargetId = 1, HostId = 1, AtkMin = 20, AtkMax = 20, MinWap = 10, MaxWap = 10, AtkSpeed = 2000,
        DefendPower = 50, MagicDefPower = 40, Wdp = 30, DefendLevel = 20, AttackLevel = 10, CritProb = 5,
        Region = 7, Channel = 1, MapId = 0,
    };

    // ================================ the monster's getters ================================

    [Fact]
    public void WithoutBuffs_TheGettersAreTheChartsFigures()
    {
        var m = Mob();
        Assert.Equal((20u, 20u, 50u, 40u), (m.GetMinAp(), m.GetMaxAp(), m.GetDefendPower(), m.GetMagicDefPower()));
        Assert.Equal((20, 10, 5), (m.GetDefendLevel(), m.GetAttackLevel(), m.GetCritProb()));
    }

    [Fact]
    public void ADefenceDownDebuff_LowersTheMonstersDefence_NotBelowZero()
    {
        var m = Mob();
        m.MaintainSkills.Add(Buff(1, Ability(Pdp, Dec, 30), Ability(Dl, Dec, 50)));
        Assert.Equal(20u, m.GetDefendPower());
        Assert.Equal(0, m.GetDefendLevel());
    }

    [Fact]
    public void AttackAndCritBuffs_RaiseThem()
    {
        var m = Mob();
        m.MaintainSkills.Add(Buff(1, Ability(Pap, Inc, 15), Ability(Cr, Inc, 10)));
        Assert.Equal((35u, 35u, 15), (m.GetMinAp(), m.GetMaxAp(), m.GetCritProb()));
    }

    [Fact]
    public void DisWeaponAndDisDefend_TakeAwayTheWeaponAndArmourParts()
    {
        var m = Mob();
        m.MaintainSkills.Add(Buff(1, Status(BuffLayer.StatusDisWeapon), Status(BuffLayer.StatusDisDefend)));
        Assert.Equal((10u, 10u, 20u, 10u), (m.GetMinAp(), m.GetMaxAp(), m.GetDefendPower(), m.GetMagicDefPower()));
    }

    // ================================ in combat ================================

    private static async Task<(MapTestHarness h, ClientSession s, Character ch, Monster mon)> Setup(Monster mon)
    {
        var t = MapTestHarness.WithMonsterMelee();
        var strike = new SkillTemplate(920, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, Positive: 0, MapId: 0xFFFF);
        strike.Data.Add(new SkillDataRow(0, SdtAbility, 1 /* physic */, 30 /* MTYPE_DAMAGE */, 1, 0, 0, 0));
        t.Skills[920] = strike;
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Victim", MaxHp = 100, Hp = 100, MaxMp = 50, Mp = 50 };
        ch.Skills.Add(new Skill { SkillId = 920, Level = 1, Template = strike });
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 120, z: 100, preSeeded: ch);
        h.Service.SpawnMonster(mon);
        h.Service.CombatRng = new Random(1);
        return (h, s, ch, mon);
    }

    [Fact]
    public async Task AMonstersAttackBuff_HitsHarder()
    {
        var mon = Mob();
        mon.MaintainSkills.Add(Buff(1, Ability(Pap, Inc, 30)));
        var (h, s, ch, _) = await Setup(mon);

        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(mon.Id, 1));

        Assert.Equal(50u, ch.Hp);                                                    // 20 + 30
    }

    [Fact]
    public async Task ADisarmedMonster_HitsWithoutItsWeapon()
    {
        var mon = Mob();
        mon.MaintainSkills.Add(Buff(1, Status(BuffLayer.StatusDisWeapon)));
        var (h, s, ch, _) = await Setup(mon);

        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(mon.Id, 1));

        Assert.Equal(90u, ch.Hp);                                                    // 20 − its 10 weapon
    }

    [Fact]
    public async Task APhysicImmuneMonster_TakesNoPhysicalDamage()
    {
        var mon = Mob();
        mon.MaintainSkills.Add(Buff(1, Status(BuffLayer.StatusExceptPhysic)));
        var (h, s, _, _) = await Setup(mon);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 920, hostId: 1));

        Assert.Equal(10_000u, mon.Hp);
    }

    [Fact]
    public async Task WithoutTheImmunity_TheSameStrikeHurts()
    {
        var mon = Mob();
        var (h, s, _, _) = await Setup(mon);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mon.Id, skillId: 920, hostId: 1));

        Assert.True(mon.Hp < 10_000);
    }
}
