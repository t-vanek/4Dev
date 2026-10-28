using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Phase 44 — the monster aggro/hate table (C++ <c>m_mapAggro</c>): skill-driven hate (warrior ×1.5,
/// accumulation, floor at 0), highest-cumulative-aggro target with a 10% sticky-target hysteresis, drop-and-
/// re-pick the next in-view attacker when the current target leaves, the same-country reject, and the
/// <c>CS_MONHOST_ACK</c> retarget broadcast. Unit tests drive <see cref="Monster.SetAggro"/> directly (exact
/// arithmetic, DB-free); integration tests drive the wired path (<c>CS_DEFEND</c> → aggro → retarget /
/// <c>RunMonsterAI</c> leash re-pick).</summary>
public class MonsterAggroTableTests
{
    private const byte OtPc = 1;                  // OBJ_TYPE OT_PC
    private const byte Warrior = 0, Wizard = 3;   // TCLASS_TYPE
    private const byte Neutral = 3;               // TCONTRY_N — a field mob's country

    private static Monster NeutralMob(uint id, float x, float z)
        => new() { Id = id, ChartId = 500, Level = 5, MaxHp = 100, Hp = 100, MaxMp = 50, Mp = 50,
            DefendPower = 100, PosX = x, PosZ = z, StartX = x, StartY = 0, StartZ = z,
            Mode = 0, Country = Neutral, Region = 7, Channel = 1, MapId = 0, RoamNextMs = 0 };

    // ---------------- unit: SetAggro arithmetic ----------------

    [Fact]
    public void SetAggro_WarriorClass_ScalesByThreeHalves()
    {
        var m = new Monster { Country = Neutral, Mode = 0 };
        m.SetAggro(hostId: 1, attackId: 1, attackType: OtPc, attackCountry: 0, attackClass: Warrior,
            target: 0, targetType: 0, nAggro: 10, active: true);
        Assert.Equal(15u, m.FindAggro(1, OtPc));   // 10 × 3 / 2 (int)
    }

    [Fact]
    public void SetAggro_NonWarrior_NoScaling()
    {
        var m = new Monster { Country = Neutral, Mode = 0 };
        m.SetAggro(1, 1, OtPc, 0, Wizard, 0, 0, 10, true);
        Assert.Equal(10u, m.FindAggro(1, OtPc));
    }

    [Fact]
    public void SetAggro_AccumulatesThenFloorsAtZero()
    {
        var m = new Monster { Country = Neutral, Mode = 0 };
        m.SetAggro(1, 1, OtPc, 0, Wizard, 0, 0, 10, true);
        m.SetAggro(1, 1, OtPc, 0, Wizard, 0, 0, 5, true);
        Assert.Equal(15u, m.FindAggro(1, OtPc));
        m.SetAggro(1, 1, OtPc, 0, Wizard, 0, 0, -100, true);
        Assert.Equal(0u, m.FindAggro(1, OtPc));    // floored at 0, not unsigned-wrapped
    }

    [Fact]
    public void SetAggro_SameCountry_NoAggroNoTarget()
    {
        var m = new Monster { Country = 0, Mode = 0 };                        // war-country 0
        var dec = m.SetAggro(1, 1, OtPc, attackCountry: 0, Wizard, 0, 0, 10, true); // 0 == war-country → reject
        Assert.Null(dec);
        Assert.Equal(0u, m.FindAggro(1, OtPc));                              // no entry created
    }

    [Fact]
    public void SetAggro_StealsTarget_OnlyPastTenPercent()
    {
        var m = new Monster { Country = Neutral, Mode = 1, TargetId = 1, TargetType = OtPc }; // MT_BATTLE on #1
        m.SetAggro(1, 1, OtPc, 0, Wizard, 0, 0, 100, true);                  // current target = 100
        Assert.Null(m.SetAggro(2, 2, OtPc, 0, Wizard, 0, 0, 105, true));     // 105 ≤ 110 (100×1.1) ⇒ no steal
        Assert.Equal(105u, m.FindAggro(2, OtPc));                            // …but the hate still accrued
        var steal = m.SetAggro(2, 2, OtPc, 0, Wizard, 0, 0, 6, true);        // 111 > 110 ⇒ steal
        Assert.NotNull(steal);
        Assert.Equal(2u, steal!.Value.ObjId);
    }

    [Fact]
    public void LeaveAggro_DropsLeaver_ReturnsHighestHostileSurvivor()
    {
        var m = new Monster { Country = Neutral, Mode = 1, TargetId = 1, TargetType = OtPc };
        m.SetAggro(1, 1, OtPc, 0, Wizard, 0, 0, 50, true);   // A = 50 (current target)
        m.SetAggro(2, 2, OtPc, 0, Wizard, 0, 0, 30, true);   // B = 30
        var survivor = m.LeaveAggro(1, OtPc);                // A leaves
        Assert.Equal(0u, m.FindAggro(1, OtPc));              // A's hate dropped
        Assert.NotNull(survivor);
        Assert.Equal(2u, survivor!.Value.ObjId);             // B is the top survivor
    }

    // ---------------- unit: GetAggro magnitude formula ----------------

    [Fact]
    public void GetAggro_Formula_ZeroWhenColumnUnset()
    {
        var t = new SkillTemplate(900, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 10, NextLevel: 1, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0,
            KindDelay: 0, SpeedApply: 0, Positive: 0, MapId: 0, Rate1stX: 1f, Aggro: 1000);
        Assert.Equal(10u, t.GetAggro(1));                    // 1000 × pow(1,1) / 100
        Assert.Equal(0u, (t with { Aggro = 0 }).GetAggro(1)); // unset column ⇒ 0 (caller floors to 1)
    }

    // ---------------- integration: wired path ----------------

    [Fact]
    public async Task Hit_BroadcastsMonHost_TrueToAttacker()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = NeutralMob(0x40021, 100, 100);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        // A PC attack carries its own id as dwHostID (the client sends it; C++ only overwrites host for a monster
        // attacker, CSHandler.cpp:1523) — that host is the TRUE recipient of CS_MONHOST_ACK.
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x40021, hostId: 1));

        Assert.Equal(1u, mob.TargetId);    // aggroed the attacker
        Assert.Equal((byte)1, mob.Mode);   // MT_BATTLE
        var r = new PacketReader(c.Last(Msg.CS_MONHOST_ACK)!);
        Assert.Equal(0x40021u, r.ReadUInt32());   // dwMonID
        Assert.Equal((byte)1, r.ReadByte());        // bSet TRUE — this player is the new host
    }

    [Fact]
    public async Task Leash_ReselectsInViewSurvivor_InsteadOfGoingHome()
    {
        var h = new MapTestHarness();
        var (sA, _) = await h.EnterAsync(1, 1, 1, x: 2000, z: 2000);  // A: far — past the leash from the anchor (100,100)
        var (sB, _) = await h.EnterAsync(2, 2, 2, x: 150, z: 150);    // B: in view of the monster
        var mob = NeutralMob(0x40022, 100, 100);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);

        await h.CastAndHitAsync(sA, MapTestHarness.DefendReq(1, 0x40022, hostId: 1)); // A pulls the monster
        Assert.Equal(1u, mob.TargetId);
        await h.CastAndHitAsync(sB, MapTestHarness.DefendReq(2, 0x40022, hostId: 2)); // B adds hate (below the steal threshold)
        Assert.Equal(1u, mob.TargetId);   // A still the target (B hasn't beaten the 10% rule)

        h.Service.RunMonsterAI(1_000);    // A is past the leash ⇒ drop A, switch to the in-view survivor B

        Assert.Equal(2u, mob.TargetId);   // retargeted to B…
        Assert.Equal((byte)1, mob.Mode);  // …still in battle — did NOT go home
    }
}
