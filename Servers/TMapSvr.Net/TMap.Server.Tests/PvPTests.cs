using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Player against player (C++ CTPlayer::Defend with a PC defender, CTPlayer::OnDie, CTMapSvrModule::PvPEvent): a hit
/// takes HP off another player, a killing one kills it, and the kill moves PvP points by the kill chart and the victim
/// level's wPvPoint.
/// </summary>
public class PvPTests
{
    private const uint Killer = 1, Victim = 2, Partner = 3;
    private const byte VictimLevel = 19;

    // No AP and no DP: every landed hit rolls the 5..6 floor. Attack level 10 (FTYPE_AL init) so a hit on a PC connects.
    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                       // FTYPE_AL
        t.LevelPvPoint[VictimLevel] = 14;
        t.LevelPvPoint[30] = 19;
        t.PvPointKill[(1, 1)] = (100, 20);                                // PVPS_NORMAL, KILL_H
        t.PvPointKill[(1, 2)] = (80, 20);                                 //              KILL_E
        t.PvPointKill[(1, 3)] = (0, 0);                                   //              KILL_L
        return t;
    }

    private static Character Pc(uint id, string name, byte level, uint hp)
        => new() { CharId = id, Name = name, Level = level, MaxHp = 100, Hp = hp, MaxMp = 50, Mp = 50 };

    private static async Task<(MapTestHarness h, ClientSession sk, FakeClientChannel ck, Character k,
        ClientSession sv, FakeClientChannel cv, Character v)> Duel(byte killerLevel = 19, uint victimHp = 5)
    {
        var h = new MapTestHarness(Store());
        var k = Pc(Killer, "Killer", killerLevel, 100);
        var v = Pc(Victim, "Victim", VictimLevel, victimHp);
        var (sk, ck) = await h.EnterAsync(Killer, 1, 1, x: 100, z: 100, name: "Killer", preSeeded: k);
        var (sv, cv) = await h.EnterAsync(Victim, 2, 2, x: 102, z: 100, name: "Victim", preSeeded: v);
        k.Level = killerLevel; v.Level = VictimLevel; v.Hp = victimHp; v.PvpTotalPoint = 50;
        h.Service.CombatRng = new Random(1);
        ck.Clear(); cv.Clear();
        return (h, sk, ck, k, sv, cv, v);
    }

    private static byte[] Hit(uint targetId) => MapTestHarness.DefendReq(Killer, targetId, attackType: 1, targetType: 1);

    private static (uint Total, uint Useable, byte Event) Points(FakeClientChannel c)
    {
        var r = new PacketReader(c.Last(Msg.CS_PVPPOINT_ACK)!);
        return (r.ReadUInt32(), r.ReadUInt32(), r.ReadByte());
    }

    [Fact]
    public async Task AHitOnAnotherPlayer_TakesItsHp_AndBothSeeIt()
    {
        var (h, sk, ck, _, _, cv, v) = await Duel(victimHp: 80);

        await h.CastAndHitAsync(sk, Hit(Victim));

        Assert.InRange(v.Hp, 74u, 75u);                                   // 80 − (5..6)
        foreach (var c in new[] { ck, cv })
        {
            var r = new PacketReader(c.Last(Msg.CS_DEFEND_ACK)!);
            Assert.Equal((Killer, Victim, (byte)1, (byte)1), (r.ReadUInt32(), r.ReadUInt32(), r.ReadByte(), r.ReadByte()));
        }
        Assert.False(cv.Has(Msg.CS_DIE_ACK));
    }

    [Fact]
    public async Task AKill_CostsTheVictimTotalPoints_AndPaysTheKiller()
    {
        var (h, sk, ck, k, _, cv, v) = await Duel();

        await h.CastAndHitAsync(sk, Hit(Victim));

        Assert.Equal(0u, v.Hp);
        Assert.True(cv.Has(Msg.CS_DIE_ACK));
        // Level 19 is worth 14: an even kill (KILL_E) takes 20% (2) and pays 80% (11).
        Assert.Equal((48u, 0u, (byte)2), Points(cv));
        Assert.Equal((11u, 11u, (byte)2), Points(ck));
        Assert.Equal((11u, 11u), (k.PvpTotalPoint, k.PvpUseablePoint));
    }

    [Fact]
    public async Task KillingSomeoneFarBelow_PaysNothing()
    {
        var (h, sk, ck, k, _, _, v) = await Duel(killerLevel: 30);

        await h.CastAndHitAsync(sk, Hit(Victim));

        Assert.Equal(0u, v.Hp);
        Assert.Equal(0u, k.PvpTotalPoint);                                // KILL_L: 0% / 0%
        Assert.Equal(50u, v.PvpTotalPoint);
        Assert.False(ck.Has(Msg.CS_PVPPOINT_ACK));
    }

    [Fact]
    public async Task TheVictimsDeathPenalty_CutsTheGain()
    {
        var (h, sk, _, k, _, _, v) = await Duel();
        v.Persist.Aftermath = 40;                                          // 100 − 40/2 = 80%

        await h.CastAndHitAsync(sk, Hit(Victim));

        Assert.Equal(8u, k.PvpTotalPoint);                                // 11 × 80 / 100
    }

    [Fact]
    public async Task TheSameVictimTooOften_StopsPaying()
    {
        var (h, sk, _, k, _, _, v) = await Duel();

        for (int i = 0; i < 4; i++)
        {
            v.Hp = 5;
            await h.CastAndHitAsync(sk, Hit(Victim));
        }

        Assert.Equal(33u, k.PvpTotalPoint);                               // 3 × 11, the 4th within 600 s pays 0
    }

    [Fact]
    public async Task InAParty_ThePartnerInViewGets12()
    {
        var (h, sk, _, k, _, _, _) = await Duel();
        var p = Pc(Partner, "Partner", 19, 100);
        var (_, cp) = await h.EnterAsync(Partner, 3, 3, x: 101, z: 100, name: "Partner", preSeeded: p);
        k.PartyId = 9; k.PartyType = 2; p.PartyId = 9; p.PartyType = 2;

        await h.CastAndHitAsync(sk, Hit(Victim));

        Assert.Equal(11u, k.PvpTotalPoint);
        Assert.Equal((12u, 12u, (byte)2), Points(cp));
    }

    [Fact]
    public async Task HittingYourself_IsNotAPvpHit()
    {
        var (h, sk, _, k, _, _, _) = await Duel();

        await h.CastAndHitAsync(sk, Hit(Killer));

        Assert.Equal(100u, k.Hp);
    }

    // ================================ debuffs ================================

    private const ushort Curse = 950;
    private const byte MtypeStr = 1;

    /// <summary>A hostile skill (m_bPositive 0) with one damage row and a 10 s −5 STR debuff.</summary>
    private static SkillTemplate CurseSkill()
    {
        var t = new SkillTemplate(Curse, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 1, NextLevel: 1, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0,
            SpeedApply: 0, Positive: 0, MapId: 0, Duration: 10_000);
        t.Data.Add(new SkillDataRow(Action: 0, Type: 1, Attr: 1, Exec: 30, Inc: 1, Value: 0, ValueInc: 0, Calc: 0));   // damage
        t.Data.Add(new SkillDataRow(Action: 3, Type: 1, Attr: 0, Exec: MtypeStr, Inc: 2, Value: 5, ValueInc: 0, Calc: 0));  // SA_BUFF −5 STR
        return t;
    }

    private static byte[] Cast(uint targetId)
        => MapTestHarness.DefendReq(Killer, targetId, attackType: 1, targetType: 1, skillId: Curse);

    private static (byte IsMaintain, uint Tick) Maintain(byte[] ack)
    {
        var r = new PacketReader(ack);
        r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); r.ReadByte(); r.ReadUInt32(); r.ReadByte(); r.ReadUInt32(); r.ReadUInt32();
        return (r.ReadByte(), r.ReadUInt32());
    }

    [Fact]
    public async Task AHostileSkill_LeavesItsDebuffOnThePlayer()
    {
        var (h, sk, ck, k, _, cv, v) = await Duel(victimHp: 80);
        var curse = CurseSkill();
        k.Skills.Add(new Skill { SkillId = Curse, Level = 1, Template = curse });

        await h.CastAndHitAsync(sk, Cast(Victim));

        var m = Assert.Single(v.MaintainSkills);
        Assert.Equal((Curse, Killer), (m.SkillId, m.AttackId));
        Assert.Equal(((byte)1, 10_000u), Maintain(ck.Last(Msg.CS_DEFEND_ACK)!));
        Assert.Equal(((byte)1, 10_000u), Maintain(cv.Last(Msg.CS_DEFEND_ACK)!));
        Assert.True(cv.Has(Msg.CS_CHARSTATINFO_ACK));                         // the victim sees its lowered stats
        Assert.True(v.Hp < 80);                                              // and the damage still landed
    }

    [Fact]
    public async Task AKillingBlow_LeavesNoDebuff()
    {
        var (h, sk, ck, k, _, _, v) = await Duel(victimHp: 5);
        k.Skills.Add(new Skill { SkillId = Curse, Level = 1, Template = CurseSkill() });

        await h.CastAndHitAsync(sk, Cast(Victim));

        Assert.Equal(0u, v.Hp);
        Assert.Empty(v.MaintainSkills);
        Assert.Equal((byte)0, Maintain(ck.Last(Msg.CS_DEFEND_ACK)!).IsMaintain);
    }

    [Fact]
    public async Task TheSameDebuffAgain_ReplacesTheFirst()
    {
        var (h, sk, _, k, _, _, v) = await Duel(victimHp: 90);
        k.Skills.Add(new Skill { SkillId = Curse, Level = 1, Template = CurseSkill() });

        await h.CastAndHitAsync(sk, Cast(Victim));
        await h.CastAndHitAsync(sk, Cast(Victim));

        Assert.Single(v.MaintainSkills);                                     // UpdateBuffSkill: a debuff replaces its own id
    }
}
