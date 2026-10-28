using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>The death penalty (C++ SetAftermath/ResetAftermath, TPlayer.cpp:3445-3545) and priest resurrection
/// (SCT_REVIVAL → CS_REVIVALASK_ACK → CS_REVIVALASK_REQ, TObjBase.cpp:3433 / CSHandler.cpp:10569).</summary>
public class AftermathTests
{
    private const ushort Resurrect = 600, Purify = 601;

    private static SkillTemplate Cure(ushort id, byte exec, ushort value)
    {
        var t = new SkillTemplate(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 5, NextLevel: 0, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0,
            SpeedApply: 0, Positive: 1, MapId: 0xFFFF);
        t.Data.Add(new SkillDataRow(Action: 0, Type: SkillTemplate.SdtCure, Attr: 0, Exec: exec, Inc: 0, Calc: 0, Value: value, ValueInc: 0));
        return t;
    }

    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        t.Skills[Resurrect] = Cure(Resurrect, 1 /* SCT_REVIVAL */, 0);
        t.Skills[Purify] = Cure(Purify, 13 /* SCT_AFTERMATH */, 15);
        t.Skills[800] = new SkillTemplate(800, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,   // TREVIVAL_SKILL
            StartLevel: 1, MaxLevel: 1, NextLevel: 0, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0,
            SpeedApply: 0, Positive: 1, MapId: 0xFFFF, Duration: 5000);
        t.Skills[800].Data.Add(new SkillDataRow(Action: SkillTemplate.SaBuff, Type: 0, Attr: 0, Exec: 0, Inc: 0, Calc: 0, Value: 0, ValueInc: 0));
        return t;
    }

    private static Character Hero(uint id, byte level, uint hp) => new()
    {
        CharId = id, Name = "Hero" + id, Level = level, MaxHp = 1000, Hp = hp, MaxMp = 500, Mp = 0,
    };

    private static byte[] Revival(byte type)
    {
        var w = new PacketWriter(Msg.CS_REVIVAL_REQ);
        w.WriteFloat(100); w.WriteFloat(0); w.WriteFloat(100); w.WriteByte(type);
        return w.ToArray();
    }

    private static byte AftermathOf(FakeClientChannel c) { var r = new PacketReader(c.Last(Msg.CS_AFTERMATH_ACK)!); r.ReadUInt32(); return r.ReadByte(); }

    // ================= the penalty =================

    [Fact]
    public async Task RevivingInTown_AddsTwentySteps()
    {
        var h = new MapTestHarness(Store());
        var ch = Hero(1, 20, 0);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        ch.Level = 20; ch.Hp = 0; c.Clear();

        await h.Service.DispatchClientAsync(s, Revival(0 /* REVIVAL_NPC */));

        Assert.Equal(20, ch.Persist.Aftermath);
        Assert.Equal(20, AftermathOf(c));
        Assert.Contains(ch.MaintainSkills, m => m.SkillId == 800);   // the revival-protection buff
    }

    [Fact]
    public async Task RevivingOnTheSpot_AddsTen_AndStacks()
    {
        var h = new MapTestHarness(Store());
        var ch = Hero(1, 20, 0);
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        ch.Level = 20; ch.Persist.Aftermath = 95; ch.Hp = 0;

        await h.Service.DispatchClientAsync(s, Revival(1 /* REVIVAL_GHOST */));

        Assert.Equal(100, ch.Persist.Aftermath);                      // capped at 100
    }

    [Fact]
    public async Task BelowLevelTen_ThereIsNoPenalty()
    {
        var h = new MapTestHarness(Store());
        var ch = Hero(1, 9, 0);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        ch.Level = 9; ch.Hp = 0; c.Clear();

        await h.Service.DispatchClientAsync(s, Revival(0));

        Assert.Equal(0, ch.Persist.Aftermath);
        Assert.False(c.Has(Msg.CS_AFTERMATH_ACK));
    }

    [Fact]
    public void ThePenalty_LowersPrimaryStats_ByPointThreePercentPerStep()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[1] = new StatSeed(10, 10, 10, 10, 10, 10);
        t.Races[1] = new StatSeed(0, 0, 0, 0, 0, 0);
        var ch = new Character { Class = 1, Race = 1, Level = 1 };
        float full = StatEngine.Stat(ch, StatEngine.MtypeStr, t);

        ch.Persist.Aftermath = 50;                                     // 15 %

        Assert.Equal(full - full * 15f / 100f, StatEngine.Stat(ch, StatEngine.MtypeStr, t), 3);
    }

    [Fact]
    public async Task OneStepRecovers_WhenItsTimeComes()
    {
        var h = new MapTestHarness(Store());
        var ch = Hero(1, 20, 1000);
        var (_, c) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        ch.Level = 20; ch.Persist.Aftermath = 10; ch.AftermathTick = 5_000;
        c.Clear();

        h.Service.RunAftermath(4_000);
        Assert.Equal(10, ch.Persist.Aftermath);                        // not yet

        h.Service.RunAftermath(6_000);
        Assert.Equal(9, ch.Persist.Aftermath);
        Assert.Equal(9, AftermathOf(c));
        Assert.Equal(5_000u + 29_000u, ch.AftermathTick);             // next in (27 + 9·0.3 → 29) s
    }

    // ================= resurrection =================

    private static byte[] CastOn(uint targetId, ushort skill)
        => MapTestHarness.DefendReq(1, targetId, attackType: 1, targetType: 1, skillId: skill);

    private static Character Priest(TemplateStore t)
    {
        var p = Hero(1, 30, 1000);
        p.Skills.Add(new Skill { SkillId = Resurrect, Level = 1, Template = t.Skills[Resurrect] });
        p.Skills.Add(new Skill { SkillId = Purify, Level = 1, Template = t.Skills[Purify] });
        return p;
    }

    [Fact]
    public async Task ResurrectingSomeoneElse_AsksThemFirst()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var (priest, _) = await h.EnterAsync(1, 1, 1, preSeeded: Priest(t));
        var dead = Hero(2, 20, 0);
        var (_, deadClient) = await h.EnterAsync(2, 2, 2, name: "Hero2", preSeeded: dead);
        dead.Hp = 0; deadClient.Clear();

        await h.CastAndHitAsync(priest, CastOn(2, Resurrect));

        Assert.Equal(0u, dead.Hp);                                     // not yet
        var r = new PacketReader(deadClient.Last(Msg.CS_REVIVALASK_ACK)!);
        Assert.Equal(1u, r.ReadUInt32());                              // the priest
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(Resurrect, r.ReadUInt16());
    }

    private static byte[] Answer(byte reply, uint priestId)
    {
        var w = new PacketWriter(Msg.CS_REVIVALASK_REQ);
        w.WriteByte(reply); w.WriteUInt32(priestId); w.WriteByte(1); w.WriteUInt16(Resurrect); w.WriteByte(1);
        return w.ToArray();
    }

    [Fact]
    public async Task SayingYes_RevivesInPlace_WithTheSmallerPenalty()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        await h.EnterAsync(1, 1, 1, preSeeded: Priest(t));
        var dead = Hero(2, 20, 0);
        var (ds, _) = await h.EnterAsync(2, 2, 2, name: "Hero2", preSeeded: dead);
        dead.Level = 20; dead.Hp = 0;

        await h.Service.DispatchClientAsync(ds, Answer(0 /* ASK_YES */, 1));

        Assert.True(dead.Hp > 0);
        Assert.Equal(12, dead.Persist.Aftermath);                      // AFTERMATH_HELP
    }

    [Fact]
    public async Task Reviving_TellsEveryoneAroundTheModeIsNormalAgain()
    {
        // C++ CTPlayer::Revival → ChgMode(MT_NORMAL) → CS_CHGMODE_ACK to the neighbours: without it the others
        // keep the revived player in battle mode.
        var t = Store();
        var h = new MapTestHarness(t);
        var (_, priestClient) = await h.EnterAsync(1, 1, 1, preSeeded: Priest(t));
        var dead = Hero(2, 20, 0);
        var (ds, _) = await h.EnterAsync(2, 2, 2, name: "Hero2", preSeeded: dead);
        dead.Level = 20; dead.Hp = 0; dead.Mode = 1; priestClient.Clear();

        await h.Service.DispatchClientAsync(ds, Answer(0 /* ASK_YES */, 1));

        var r = new PacketReader(priestClient.Last(Msg.CS_CHGMODE_ACK)!);
        Assert.Equal(2u, r.ReadUInt32());
        Assert.Equal(1, r.ReadByte());   // OT_PC
        Assert.Equal(0, r.ReadByte());   // MT_NORMAL
        Assert.Equal(0, dead.Mode);
    }

    [Fact]
    public async Task SayingNo_TellsThePriest()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var (_, priestClient) = await h.EnterAsync(1, 1, 1, preSeeded: Priest(t));
        var dead = Hero(2, 20, 0);
        var (ds, _) = await h.EnterAsync(2, 2, 2, name: "Hero2", preSeeded: dead);
        dead.Hp = 0; priestClient.Clear();

        await h.Service.DispatchClientAsync(ds, Answer(1 /* ASK_NO */, 1));

        Assert.Equal(0u, dead.Hp);
        var r = new PacketReader(priestClient.Last(Msg.CS_REVIVALREPLY_ACK)!);
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(2u, r.ReadUInt32());
    }

    [Fact]
    public async Task ALivePlayer_CannotAcceptAResurrection()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var alive = Hero(2, 20, 100);
        var (s, _) = await h.EnterAsync(2, 2, 2, name: "Hero2", preSeeded: alive);
        alive.Hp = 100;

        await h.Service.DispatchClientAsync(s, Answer(0, 1));

        Assert.Equal(100u, alive.Hp);                                  // no free refill
    }

    [Fact]
    public async Task APurifyingCure_TakesStepsOff()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var priest = Priest(t);
        var (ps, pc) = await h.EnterAsync(1, 1, 1, preSeeded: priest);
        priest.Persist.Aftermath = 40; pc.Clear();

        await h.CastAndHitAsync(ps, CastOn(1, Purify));   // on itself

        Assert.Equal(25, priest.Persist.Aftermath);                    // 40 − 15
        Assert.Equal(25, AftermathOf(pc));
    }
}
