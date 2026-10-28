using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Stances (C++ UpdateBuffSkill's IsPosture branches): taking a stance replaces the current one and ends one's own buffs
/// tied to another stance (m_wPosture); an ordinary buff never pushes a stance out. Also the ORadius tie-break.
/// </summary>
public class StanceTests
{
    private const byte SaBuff = 3, SdtAbility = 1, SdtStatus = 6, MtypeStr = 1, MtypePap = 7;
    private const ushort Defend = 131, Craze = 132, CrazeBuff = 111, DefendBuff = 124, Plain = 900, Area = 901;

    private static SkillTemplate Skill(ushort id, ushort posture = 0, byte orad = 0, params SkillDataRow[] rows)
    {
        var t = new SkillTemplate(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 10, NextLevel: 1, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0,
            SpeedApply: 0, Positive: 1, MapId: 0, Duration: 60_000, Posture: posture, ORadius: orad);
        t.Data.AddRange(rows);
        return t;
    }

    private static SkillDataRow Buff(byte exec, ushort value) => new(SaBuff, SdtAbility, 0, exec, 1, value, 0, 0);
    private static SkillDataRow Mode(byte status) => new(SaBuff, SdtStatus, 0, status, 0, 0, 0, 0);

    private static readonly SkillTemplate[] All =
    {
        Skill(Defend, rows: new[] { Mode(27) }),                  // SDT_STATUS_DEFENDMODE
        Skill(Craze, rows: new[] { Mode(26) }),                   // SDT_STATUS_CRAZEMODE
        Skill(CrazeBuff, posture: Craze, rows: new[] { Buff(MtypePap, 8) }),
        Skill(DefendBuff, posture: Defend, rows: new[] { Buff(2 /* MTYPE_DEX */, 3) }),
        Skill(Plain, rows: new[] { Buff(MtypeStr, 5) }),
        Skill(Area, orad: 1, rows: new[] { Buff(MtypeStr, 5) }),
    };

    private static async Task<(MapTestHarness h, ClientSession s, Character ch)> Warrior()
    {
        var t = new TemplateStore();
        foreach (var k in All) t.Skills[k.Id] = k;
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Brute", Level = 30, MaxHp = 100, Hp = 100 };
        foreach (var k in All) ch.Skills.Add(new Skill { SkillId = k.Id, Level = 1, Template = k });
        var (s, _) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        return (h, s, ch);
    }

    private static Task Cast(MapTestHarness h, ClientSession s, ushort skill)
        => h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 1, targetType: 1, skillId: skill));

    private static ushort[] Buffs(Character ch) => ch.MaintainSkills.Select(m => m.SkillId).OrderBy(x => x).ToArray();

    [Fact]
    public async Task ANewStance_ReplacesTheOld_AndEndsItsBuffs()
    {
        var (h, s, ch) = await Warrior();
        await Cast(h, s, Craze);
        await Cast(h, s, CrazeBuff);
        await Cast(h, s, Plain);

        await Cast(h, s, Defend);

        Assert.Equal(new[] { Defend, Plain }, Buffs(ch));                // craze and its buff gone; the plain buff stays
    }

    [Fact]
    public async Task RetakingTheSameStance_KeepsItsBuffs()
    {
        var (h, s, ch) = await Warrior();
        await Cast(h, s, Craze);
        await Cast(h, s, CrazeBuff);

        await Cast(h, s, Craze);

        Assert.Equal(new[] { CrazeBuff, Craze }, Buffs(ch));
    }

    [Fact]
    public async Task AnOrdinaryBuff_DoesNotPushAStanceOut()
    {
        var (h, s, ch) = await Warrior();
        await Cast(h, s, Defend);

        await Cast(h, s, DefendBuff);
        await Cast(h, s, Plain);

        Assert.Equal(new[] { DefendBuff, Defend, Plain }, Buffs(ch));
    }

    [Fact]
    public async Task ATieAgainstAnAreaBuff_KeepsTheAreaBuff()
    {
        var (h, s, ch) = await Warrior();
        await Cast(h, s, Area);

        await Cast(h, s, Plain);                                          // same +5 STR, same caster: a tie

        Assert.Equal(new[] { Area }, Buffs(ch));
    }
}
