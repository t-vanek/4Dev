using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Phase 16 — HP/MP regen (Recover): players regen HP (NORMAL-only) + MP (always) by the flat
/// GetHPR/GetMPR amount every RECOVER_TIME; monsters regen 25%/tick (non-BATTLE); combat suppresses HP via
/// battle mode + anchor reset; a change broadcasts CS_HPMP_ACK.</summary>
public class RecoverTests
{
    private const uint RecoverInit = 5000;

    // MaxHP 200 / MaxMP 100; HP regen +10 (FTYPE_HPR init 10), MP regen +5 (FTYPE_MPR init 5).
    private static TemplateStore VitalStore()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Formulas[8] = new FormulaRow(200, 0f, 0f);   // FTYPE_HP
        t.Formulas[9] = new FormulaRow(10, 0f, 0f);    // FTYPE_HPR
        t.Formulas[19] = new FormulaRow(100, 0f, 0f);  // FTYPE_MP
        t.Formulas[20] = new FormulaRow(5, 0f, 0f);    // FTYPE_MPR
        t.Classes[1] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[1] = new StatSeed(1, 1, 1, 1, 1, 1);
        return t;
    }

    private static Character Wounded(uint hp, uint mp)
        => new() { CharId = 9, Name = "Hurt", Class = 1, Race = 1, Hp = hp, Mp = mp };

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c)> Enter(Character ch, TemplateStore? templates = null)
    {
        var h = new MapTestHarness(templates ?? VitalStore());
        var (s, c) = await h.EnterAsync(9, 1, 1, name: "Hurt", preSeeded: ch);
        c.Clear();
        return (h, s, c);
    }

    private static Monster Mob(uint id, uint maxHp = 100, uint hp = 50)
        => new() { Id = id, ChartId = 500, Level = 5, MaxHp = maxHp, Hp = hp, MaxMp = 100, Mp = 50,
            DefendPower = 100, PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0, Mode = 0,
            Country = 3 /* TCONTRY_N — a neutral field mob, so a faction player's hit aggros it (Phase 44) */ };

    private static MaintainSkill Buff(params SkillDataRow[] rows)
    {
        var template = new SkillTemplate(900, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0,
            Positive: 1, MapId: 0xFFFF, Duration: 3000);
        template.Data.AddRange(rows);
        var buff = new MaintainSkill { SkillId = template.Id, Template = template };
        buff.SetEndTick(1000);
        return buff;
    }

    private static SkillDataRow Status(byte status) => new(3, 6, 0, status, 1, 0, 0, 0);
    private static SkillDataRow Ability(byte ability, byte operation, ushort value) => new(3, 1, 0, ability, operation, value, 0, 0);

    // ---------------- players ----------------

    [Fact]
    public async Task Player_RegensHpAndMp_OnDueTick()
    {
        var ch = Wounded(hp: 50, mp: 50);
        var (h, s, c) = await Enter(ch);

        h.Service.RunRecover(3000); // now >= 0 + RECOVER_TIME(3000)

        Assert.Equal(60u, ch.Hp);   // 50 + 10
        Assert.Equal(55u, ch.Mp);   // 50 + 5
        Assert.True(c.Has(Msg.CS_HPMP_ACK));
    }

    [Fact]
    public async Task Player_HpClampsToMax()
    {
        var ch = Wounded(hp: 195, mp: 100);
        var (h, s, c) = await Enter(ch);

        h.Service.RunRecover(3000);

        Assert.Equal(200u, ch.Hp);  // 195 + 10 clamped to MaxHP 200
    }

    [Fact]
    public async Task Player_NoRegenWhenFull_NoBroadcast()
    {
        var ch = Wounded(hp: 200, mp: 100);
        var (h, s, c) = await Enter(ch);

        h.Service.RunRecover(3000);

        Assert.Equal(200u, ch.Hp);
        Assert.Equal(100u, ch.Mp);
        Assert.False(c.Has(Msg.CS_HPMP_ACK)); // nothing changed
    }

    [Fact]
    public async Task Player_NotYetDue_NoRegen()
    {
        var ch = Wounded(hp: 50, mp: 50);
        var (h, s, c) = await Enter(ch);

        h.Service.RunRecover(2999); // just under RECOVER_TIME

        Assert.Equal(50u, ch.Hp);
        Assert.Equal(50u, ch.Mp);
    }

    [Fact]
    public async Task Player_HpSuppressedInBattle_ButMpContinues()
    {
        var ch = Wounded(hp: 50, mp: 50);
        var (h, s, c) = await Enter(ch);
        ch.Mode = 1;                 // MT_BATTLE (MP ungated, HP gated to NORMAL)

        h.Service.RunRecover(3000);

        Assert.Equal(50u, ch.Hp);    // HP suppressed while in battle
        Assert.Equal(55u, ch.Mp);    // MP still regenerates
    }

    [Fact]
    public async Task Player_LeavesBattleAfterTimeout_ThenHpResumes()
    {
        var ch = Wounded(hp: 50, mp: 100);
        var (h, s, c) = await Enter(ch);
        ch.EnterBattle(1000, RecoverInit); // Mode=BATTLE, anchors=6000, lastAtk=1000

        h.Service.RunRecover(3000);
        Assert.Equal((byte)1, ch.Mode);    // still in battle (1000 + 5000 not < 3000)
        Assert.Equal(50u, ch.Hp);          // HP suppressed

        h.Service.RunRecover(6001);        // 1000 + 5000 < 6001 ⇒ battle→normal (after the recover pass)
        Assert.Equal((byte)0, ch.Mode);
        Assert.Equal(50u, ch.Hp);          // anchor is 6000; 6001 < 6000+3000 ⇒ not yet due

        h.Service.RunRecover(9000);        // now NORMAL and due (9000 >= 6000+3000)
        Assert.Equal(60u, ch.Hp);
    }

    [Fact]
    public async Task Player_StopRecoverBlocksBothResources_UntilTheAppliedEffectExpires()
    {
        var templates = VitalStore();
        var stop = Buff(Status(BuffLayer.StatusStopRecover));
        templates.Skills[stop.SkillId] = stop.Template!;
        var ch = Wounded(50, 50);
        var (h, s, c) = await Enter(ch, templates);
        h.Service.NowMs = 1000;
        Assert.True(h.Service.ForceMaintain(s, ch, stop.SkillId, ch.CharId, 1, ch.CharId, 1, 0));
        c.Clear();

        h.Service.RunMaintainSkills(3000);
        h.Service.RunRecover(3000);

        Assert.Equal((50u, 50u), (ch.Hp, ch.Mp));
        Assert.Equal((0u, 0u), (ch.RecoverHpTick, ch.RecoverMpTick));
        Assert.False(c.Has(Msg.CS_HPMP_ACK));

        h.Service.NowMs = 4000;
        h.Service.RunMaintainSkills(4000);
        h.Service.RunRecover(4000);

        Assert.Empty(ch.MaintainSkills);
        Assert.True(c.Has(Msg.CS_SKILLEND_ACK));
        Assert.Equal((60u, 55u), (ch.Hp, ch.Mp));
        Assert.True(c.Has(Msg.CS_HPMP_ACK));
    }

    [Fact]
    public async Task Player_StopRecoverDoesNotKeepThePlayerInBattle()
    {
        var ch = Wounded(50, 50);
        var (h, _, _) = await Enter(ch);
        ch.MaintainSkills.Add(Buff(Status(BuffLayer.StatusStopRecover)));
        ch.EnterBattle(1000, RecoverInit);

        h.Service.RunRecover(9000);

        Assert.Equal((byte)0, ch.Mode);
        Assert.Equal((50u, 50u), (ch.Hp, ch.Mp));
        ch.MaintainSkills.Clear();
        h.Service.RunRecover(9001);
        Assert.Equal((60u, 55u), (ch.Hp, ch.Mp));
    }

    [Fact]
    public async Task Player_HoldAloneDoesNotSuppressNaturalRecovery()
    {
        var ch = Wounded(50, 50);
        var (h, _, _) = await Enter(ch);
        ch.MaintainSkills.Add(Buff(Status(8))); // players do not use the monster CheckAttack recovery gate

        h.Service.RunRecover(3000);

        Assert.Equal((60u, 55u), (ch.Hp, ch.Mp));
    }

    // ---------------- monsters ----------------

    [Fact]
    public async Task Monster_Regens25PercentOfMax_AndBroadcasts()
    {
        var h = new MapTestHarness(VitalStore());
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100); // watcher in view
        var mob = Mob(0x20001, maxHp: 100, hp: 50);
        h.Service.SpawnMonster(mob);
        c.Clear();

        h.Service.RunRecover(3000);

        Assert.Equal(75u, mob.Hp);   // 50 + 100/4
        Assert.True(c.Has(Msg.CS_HPMP_ACK));
    }

    [Fact]
    public async Task Monster_HpSuppressedInBattle()
    {
        var h = new MapTestHarness(VitalStore());
        await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = Mob(0x20002, maxHp: 100, hp: 40);
        mob.Mode = 1; // MT_BATTLE
        h.Service.SpawnMonster(mob);

        h.Service.RunRecover(3000);

        Assert.Equal(40u, mob.Hp);   // monster HP regen suppressed while MT_BATTLE
    }

    [Theory]
    [InlineData(6, 3)]  // block
    [InlineData(6, 8)]  // hold
    [InlineData(6, 12)] // flash
    [InlineData(6, 45)] // darkness
    [InlineData(6, 48)] // stop recovery
    [InlineData(6, 53)] // hypnosis
    [InlineData(7, 1)]  // AI fear
    public async Task Monster_RecoveryBlockedByTheEffect_ResumesAfterExpiry(byte type, byte exec)
    {
        var h = new MapTestHarness(VitalStore());
        var (_, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100,
            preSeeded: new Character { CharId = 1, Hp = 200, Mp = 100, Class = 1, Race = 1 });
        var mob = Mob(0x20004);
        mob.MaintainSkills.Add(Buff(new SkillDataRow(3, type, 0, exec, 1, 0, 0, 0)));
        h.Service.SpawnMonster(mob);
        c.Clear();

        h.Service.RunMaintainSkills(3000);
        h.Service.RunRecover(3000);

        Assert.Equal((50u, 50u), (mob.Hp, mob.Mp));
        Assert.Equal((0u, 0u), (mob.RecoverHpTick, mob.RecoverMpTick));
        Assert.False(c.Has(Msg.CS_HPMP_ACK));

        h.Service.RunMaintainSkills(4000);
        h.Service.RunRecover(4000);

        Assert.Empty(mob.MaintainSkills);
        Assert.True(c.Has(Msg.CS_SKILLEND_ACK));
        Assert.Equal((75u, 75u), (mob.Hp, mob.Mp));
        Assert.True(c.Has(Msg.CS_HPMP_ACK));
    }

    [Theory]
    [InlineData(50, 75u)]
    [InlineData(150, 125u)]
    public void Monster_RecoveryModifiersUseTheSeparateHpAndMpAmounts(ushort hpPercent, uint expectedHp)
    {
        var h = new MapTestHarness(VitalStore());
        var mob = Mob(0x20005, maxHp: 200);
        mob.MaintainSkills.Add(Buff(Ability(BuffLayer.Hpr, 5, hpPercent), Ability(BuffLayer.Mpr, 2, 10)));
        h.Service.SpawnMonster(mob);

        h.Service.RunRecover(3000);

        Assert.Equal(expectedHp, mob.Hp); // percentage scales the base 200/4 = 50 recovery amount
        Assert.Equal(65u, mob.Mp);  // base 100/4 = 25, -10 = 15
    }

    [Theory]
    [InlineData(1, 100u)] // large increase clamps at max
    [InlineData(2, 50u)]  // large decrease floors the recovery amount at zero
    public void Monster_RecoveryModifiersRespectResourceBounds(byte operation, uint expected)
    {
        var h = new MapTestHarness(VitalStore());
        var mob = Mob(0x20006);
        mob.MaintainSkills.Add(Buff(Ability(BuffLayer.Hpr, operation, 100), Ability(BuffLayer.Mpr, operation, 100)));
        h.Service.SpawnMonster(mob);

        h.Service.RunRecover(3000);

        Assert.Equal((expected, expected), (mob.Hp, mob.Mp));
    }

    [Fact]
    public async Task Hit_EntersBattle_SuppressesMonsterRegen_ThenHealsWhenDisengaged()
    {
        var h = new MapTestHarness(VitalStore());
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = Mob(0x20003, maxHp: 100, hp: 100);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        h.Service.NowMs = 1000;
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x20003)); // damages + battle
        Assert.Equal((byte)1, mob.Mode);      // monster entered battle on being hit
        uint hurt = mob.Hp;
        Assert.True(hurt < 100);

        h.Service.RunRecover(3000);
        Assert.Equal(hurt, mob.Hp);            // suppressed while in battle

        mob.Mode = 0;                          // disengaged (Phase-19 AI drops aggro → MT_NORMAL)
        h.Service.RunRecover(9000);            // now NORMAL and due ⇒ HP regen
        Assert.True(mob.Hp > hurt);
    }

    // ---------------- direct: GetHPR/GetMPR formula ----------------

    [Fact]
    public void HpMpRecover_ComesFromFormula()
    {
        var t = VitalStore();
        var ch = Wounded(50, 50);
        Assert.Equal(10u, StatEngine.HpRecover(ch, t)); // FTYPE_HPR init 10
        Assert.Equal(5u, StatEngine.MpRecover(ch, t));  // FTYPE_MPR init 5
    }
}
