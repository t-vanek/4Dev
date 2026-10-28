using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Phase 43 — the direct HP/MP transfer &amp; swap execs completing the combat-core vitals family: the cure-path
/// <c>SCT_HPTRANS</c>/<c>SCT_MPTRANS</c> (add HP/MP from the attacker's transferred amount) and the status
/// <c>SDT_STATUS_HPMPCHANGE</c> (swap HP↔MP) / <c>SDT_STATUS_HPTOMP</c> (sacrifice half HP into MP), cast on
/// self/an ally via <c>CS_DEFEND</c>. Byte-observable through the target's HP/MP + <c>CS_HPMP_ACK</c>. DB-free.
/// </summary>
public class HpMpExecTests
{
    private const byte OtPc = 1;
    private const byte SdtCure = 5, SdtStatus = 6;
    private const byte SviMultiply = 3;   // Calculate(value, calc=2) ⇒ value·2 − value = value (so the delta = the input)
    private const byte SctHpTrans = 15, SctMpTrans = 16;
    private const byte StatusHpMpChange = 50, StatusHpToMp = 51;

    // A skill carrying one SDT_CURE transfer row: SVI_MULTIPLY 2 ⇒ the delta equals the transferred amount.
    private static SkillTemplate Transfer(ushort id, byte exec)
    {
        var t = Bare(id);
        t.Data.Add(new SkillDataRow(Action: 0, Type: SdtCure, Attr: 0, Exec: exec, Inc: SviMultiply, Value: 2, ValueInc: 0, Calc: 0));
        return t;
    }

    // A skill carrying one SDT_STATUS HP↔MP row (the exec drives the effect; SVI_MULTIPLY 2 feeds HPTOMP's add).
    private static SkillTemplate Status(ushort id, byte exec)
    {
        var t = Bare(id);
        t.Data.Add(new SkillDataRow(Action: 0, Type: SdtStatus, Attr: 0, Exec: exec, Inc: SviMultiply, Value: 2, ValueInc: 0, Calc: 0));
        return t;
    }

    private static SkillTemplate Bare(ushort id)
        => new(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0, StartLevel: 1, MaxLevel: 10, NextLevel: 1,
            ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0, SpeedApply: 0, Positive: 1, MapId: 0);

    private static Character Char(uint hp = 100, uint mp = 100, uint maxHp = 100, uint maxMp = 100)
        => new() { CharId = 1, Name = "Hero", Level = 5, MaxHp = maxHp, Hp = hp, MaxMp = maxMp, Mp = mp,
                   Invens = { new Inven { InvenId = 0xFF } } };

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch)> Enter(
        Character ch, SkillTemplate skill)
    {
        var h = new MapTestHarness();
        ch.Skills.Add(new Skill { SkillId = skill.Id, Level = 1, Template = skill });
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        h.Service.CombatRng = new Random(1);
        c.Clear();
        return (h, s, c, ch);
    }

    // Read the {maxHp, hp, maxMp, mp} vitals out of a CS_HPMP_ACK (dwID, bType, then the four DWORDs).
    private static (uint maxHp, uint hp, uint maxMp, uint mp) Vitals(byte[] pkt)
    {
        var r = new PacketReader(pkt);
        r.ReadUInt32(); r.ReadByte();
        return (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
    }

    // ==================== SCT_HPTRANS / SCT_MPTRANS ====================

    [Fact]
    public async Task HpTrans_AddsTransferredHp()
    {
        var (h, s, c, ch) = await Enter(Char(hp: 10), Transfer(900, SctHpTrans));

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 900, transHp: 30));

        Assert.Equal(40u, ch.Hp);                                   // 10 + the 30 transferred (no over-heal roll)
        var (_, hp, _, _) = Vitals(c.Last(Msg.CS_HPMP_ACK)!);
        Assert.Equal(40u, hp);                                      // byte-exact on the wire
        Assert.True(c.Has(Msg.CS_DEFEND_ACK));
    }

    [Fact]
    public async Task MpTrans_AddsTransferredMp()
    {
        var (h, s, c, ch) = await Enter(Char(mp: 10), Transfer(901, SctMpTrans));

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 901, transMp: 25));

        Assert.Equal(35u, ch.Mp);                                   // 10 + 25
    }

    [Fact]
    public async Task HpTrans_ClampsToMax()
    {
        var (h, s, c, ch) = await Enter(Char(hp: 90), Transfer(902, SctHpTrans));

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 902, transHp: 50));

        Assert.Equal(100u, ch.Hp);                                  // 90 + 50 clamped to MaxHP
    }

    // ==================== SDT_STATUS_HPMPCHANGE (swap HP↔MP) ====================

    [Fact]
    public async Task HpMpChange_SwapsPools()
    {
        var (h, s, c, ch) = await Enter(Char(hp: 30, mp: 80), Status(910, StatusHpMpChange));

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 910));

        Assert.Equal(80u, ch.Hp);                                   // new HP = min(old MP 80, MaxHP 100)
        Assert.Equal(30u, ch.Mp);                                   // new MP = min(old HP 30, MaxMP 100)
        var (_, hp, _, mp) = Vitals(c.Last(Msg.CS_HPMP_ACK)!);
        Assert.Equal(80u, hp);
        Assert.Equal(30u, mp);
    }

    [Fact]
    public async Task HpMpChange_ClampsEachSideToItsOwnMax()
    {
        // Old MP (150) exceeds MaxHP (100) ⇒ new HP clamps to 100; old HP (40) ≤ MaxMP ⇒ new MP = 40.
        var (h, s, c, ch) = await Enter(Char(hp: 40, mp: 150, maxHp: 100, maxMp: 200), Status(911, StatusHpMpChange));

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 911));

        Assert.Equal(100u, ch.Hp);   // min(150, MaxHP 100)
        Assert.Equal(40u, ch.Mp);    // min(40, MaxMP 200)
    }

    [Fact]
    public async Task HpMpChange_NoMp_FailsSilently()
    {
        var (h, s, c, ch) = await Enter(Char(hp: 50, mp: 0), Status(912, StatusHpMpChange));

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 912));

        Assert.Equal(50u, ch.Hp);    // C++ returns PERFORM_FAIL when MP == 0 ⇒ no swap
        Assert.Equal(0u, ch.Mp);
    }

    // ==================== SDT_STATUS_HPTOMP (sacrifice half HP into MP) ====================

    [Fact]
    public async Task HpToMp_SacrificesHalfHp_IntoMp()
    {
        var (h, s, c, ch) = await Enter(Char(hp: 40, mp: 10), Status(920, StatusHpToMp));

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 920));

        Assert.Equal(20u, ch.Hp);    // 40 − 40/2
        Assert.Equal(30u, ch.Mp);    // 10 + Calculate(dec 20) = 10 + 20
    }

    [Fact]
    public async Task HpToMp_ClampsMpToMax()
    {
        var (h, s, c, ch) = await Enter(Char(hp: 100, mp: 45, maxMp: 50), Status(921, StatusHpToMp));

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: OtPc, skillId: 921));

        Assert.Equal(50u, ch.Hp);    // 100 − 50
        Assert.Equal(50u, ch.Mp);    // 45 + 50 clamped to MaxMP 50
    }
}
