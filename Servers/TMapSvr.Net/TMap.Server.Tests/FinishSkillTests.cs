using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// CS_FINISHSKILL_ACK — the packet that carries every ordinary player attack in this build. The key property
/// is equivalence: it is a new front door onto the same per-target hit as CS_DEFEND_REQ, so the same skill on
/// the same monster with the same RNG must land the same damage through either packet.
/// </summary>
public class FinishSkillTests
{
    private const byte OtPc = 1, OtMon = 2, TcontryN = 3;
    private const byte SdtAbility = 1, SaOnce = 0, SviDecrease = 2, SattPhysic = 1, MtypeHp = 14;
    private const ushort Sid = 200;

    // A deterministic skill: MTYPE_HP drain of 20 (no AP−DP roll), so damage is exact.
    private static SkillTemplate DrainSkill(ushort id = Sid, ushort mapId = 0xFFFF, byte positive = 0)
    {
        var t = new SkillTemplate(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 10, NextLevel: 1, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0,
            KindDelay: 0, SpeedApply: 0, Positive: positive, MapId: mapId);
        t.Data.Add(new SkillDataRow(Action: SaOnce, Type: SdtAbility, Attr: SattPhysic, Exec: MtypeHp,
            Inc: SviDecrease, Value: 20, ValueInc: 0, Calc: 0));
        return t;
    }

    private static Monster Mob(uint id, byte country = TcontryN, uint hp = 100)
        => new()
        {
            Id = id, ChartId = 500, Level = 5, MaxHp = hp, Hp = hp, MaxMp = 100, Mp = 100,
            PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0, Country = country,
        };

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c)> Setup(
        SkillTemplate skill, params Monster[] mobs)
    {
        var store = new TemplateStore();
        store.Skills[skill.Id] = skill;
        // AidCountry = TCONTRY_N mirrors a live session (the world sends it in MW_ENTERCHAR).
        var ch = new Character { CharId = 1, Name = "Hero", Level = 10, AidCountry = TcontryN };
        ch.Skills.Add(new Skill { SkillId = skill.Id, Level = 1, Template = skill });
        var h = new MapTestHarness(store);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        foreach (var m in mobs) h.Service.SpawnMonster(m);
        h.Service.CombatRng = new Random(1);
        c.Clear();
        return (h, s, c);
    }

    /// <summary>CS_FINISHSKILL_ACK exactly as the client writes it (TClient CSSender.cpp:2996). IsLinked and IsFake
    /// are C++ BOOL = 4 bytes — if the server read them as 1 byte the target list would misparse and no hit land,
    /// so every damage assertion below also pins the wire width.</summary>
    private static byte[] FinishSkill(uint attackId, ushort skillId, params (uint id, byte type)[] targets)
    {
        var w = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        w.WriteUInt32(attackId);             // dwAttackID
        w.WriteUInt32(attackId);             // dwID
        w.WriteByte(OtPc);                   // bType
        w.WriteFloat(100); w.WriteFloat(0); w.WriteFloat(100);
        w.WriteUInt16(skillId);
        w.WriteUInt32(0);                    // IsLinked (BOOL)
        w.WriteUInt32(0);                    // IsFake   (BOOL)
        w.WriteUInt16(0);                    // wPartyID
        w.WriteByte((byte)targets.Length);
        foreach (var (id, type) in targets) { w.WriteUInt32(id); w.WriteByte(type); }
        return w.ToArray();
    }

    // ---- it lands ----

    [Fact]
    public async Task PlayerAttack_DamagesTheMonster()
    {
        var mon = Mob(0x30001);
        var (h, s, _) = await Setup(DrainSkill(), mon);

        await h.CastAndHitAsync(s, FinishSkill(1, Sid, (mon.Id, OtMon)));

        Assert.Equal(80u, mon.Hp);
    }

    [Fact]
    public async Task PlayerAttack_BroadcastsTheHit()
    {
        var mon = Mob(0x30001);
        var (h, s, c) = await Setup(DrainSkill(), mon);

        await h.CastAndHitAsync(s, FinishSkill(1, Sid, (mon.Id, OtMon)));

        Assert.True(c.Has(Msg.CS_DEFEND_ACK), "the attacker must see the hit result");
    }

    [Fact]
    public async Task SameDamageAsTheClassicDefendPath()
    {
        var viaFinish = Mob(0x30001);
        var (h1, s1, c1) = await Setup(DrainSkill(), viaFinish);
        await h1.CastAndHitAsync(s1, FinishSkill(1, Sid, (viaFinish.Id, OtMon)));

        var viaDefend = Mob(0x30001);
        var (h2, s2, c2) = await Setup(DrainSkill(), viaDefend);
        await h2.CastAndHitAsync(s2, MapTestHarness.DefendReq(1, viaDefend.Id, skillId: Sid, hostId: 1));   // a real client sends its own id as host

        Assert.Equal(viaDefend.Hp, viaFinish.Hp);
        // Two fields legitimately differ between the doors: dwActID/dwAniID (literal 0 on the FINISHSKILL path, per
        // the C++) and the six positions (CS_DEFEND_REQ carries the attacker's position, FINISHSKILL the ground
        // point). Blank exactly those; every other byte — hit type, attack-power band, levels, skill, and the
        // per-exec damage map — must be identical.
        Assert.Equal(Blank(c2.Last(Msg.CS_DEFEND_ACK)!), Blank(c1.Last(Msg.CS_DEFEND_ACK)!));
    }

    // CS_DEFEND_ACK body offsets (CombatCoreTests.DamageMap walks the same layout): ids+types+host = 15, then
    // dwActID(4) dwAniID(4) at 15..22; bIsMaintain..bPerform run 23..58; the six position floats are 59..82.
    private static byte[] Blank(byte[] ack)
    {
        var copy = (byte[])ack.Clone();
        Array.Clear(copy, PacketHeader.Size + 15, 8);    // dwActID, dwAniID
        Array.Clear(copy, PacketHeader.Size + 59, 24);   // atk X/Y/Z, def X/Y/Z
        return copy;
    }

    [Fact]
    public async Task EveryListedTargetIsHit()
    {
        var a = Mob(0x30001); var b = Mob(0x30002);
        var (h, s, _) = await Setup(DrainSkill(), a, b);

        await h.CastAndHitAsync(s, FinishSkill(1, Sid, (a.Id, OtMon), (b.Id, OtMon)));

        Assert.Equal(80u, a.Hp);
        Assert.Equal(80u, b.Hp);
    }

    // ---- what it refuses ----

    [Fact]
    public async Task UnknownSkillTemplate_IsIgnored()
    {
        var mon = Mob(0x30001);
        var (h, s, _) = await Setup(DrainSkill(), mon);

        await h.CastAndHitAsync(s, FinishSkill(1, 999, (mon.Id, OtMon)));

        Assert.Equal(100u, mon.Hp);
    }

    [Fact]
    public async Task AnotherPlayerCannotBeNamedAsTheAttacker()
    {
        var mon = Mob(0x30001);
        var (h, s, _) = await Setup(DrainSkill(), mon);

        await h.CastAndHitAsync(s, FinishSkill(attackId: 42, Sid, (mon.Id, OtMon)));

        Assert.Equal(100u, mon.Hp);
    }

    [Fact]
    public async Task MapRestrictedSkill_OffItsMap_IsIgnored()
    {
        var mon = Mob(0x30001);
        var (h, s, _) = await Setup(DrainSkill(mapId: 550), mon);   // usable only on map 550; hero is on 0

        await h.CastAndHitAsync(s, FinishSkill(1, Sid, (mon.Id, OtMon)));

        Assert.Equal(100u, mon.Hp);
    }

    [Fact]
    public async Task NegativeSkill_SkipsAMonsterOfTheAttackersOwnFaction()
    {
        var own = Mob(0x30001, country: 1);   // hero is country 1 (the enter packet's) — a same-faction guard
        var (h, s, _) = await Setup(DrainSkill(positive: 0), own);

        await h.CastAndHitAsync(s, FinishSkill(1, Sid, (own.Id, OtMon)));

        Assert.Equal(100u, own.Hp);
    }
}
