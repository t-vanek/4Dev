using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Summons in PvP (C++ OnCS_FINISHSKILL_ACK with a summon on either side): a player's hostile skill on someone else's
/// summon, a summon's hit on a player (its owner's kill), a summon on a summon, and buffs on anyone's summon.
/// </summary>
public class SummonPvPTests
{
    private const uint A = 1, B = 2, PetA = 0x6001, PetB = 0x6002;
    private const byte OtPc = 1, OtRecall = 7, SaBuff = 3, SdtAbility = 1;
    private const ushort Strike = 910, Slow = 911, Bless = 912;
    private const ushort Attr = 3001;

    private static SkillTemplate Skill(ushort id, byte positive, params SkillDataRow[] rows)
    {
        var t = new SkillTemplate(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 1, NextLevel: 1, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0,
            SpeedApply: 0, Positive: positive, MapId: 0xFFFF, Duration: 60_000);
        t.Data.AddRange(rows);
        return t;
    }

    private static readonly SkillDataRow Damage = new(0, SdtAbility, 1 /* physic */, 30 /* MTYPE_DAMAGE */, 1, 0, 0, 0);

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                                   // FTYPE_AL: a hit on a PC connects
        t.LevelPvPoint[19] = 14;
        t.PvPointKill[(1, 2)] = (80, 20);                                             // PVPS_NORMAL, KILL_E
        t.Skills[Strike] = Skill(Strike, 0, Damage);
        t.Skills[Slow] = Skill(Slow, 0, Damage, new SkillDataRow(SaBuff, SdtAbility, 0, 1, 1, 5, 0, 0));
        t.Skills[Bless] = Skill(Bless, 1, new SkillDataRow(SaBuff, SdtAbility, 0, 1, 1, 5, 0, 0));
        return t;
    }

    private sealed record World(MapTestHarness H, ClientSession Sa, FakeClientChannel Ca, Character A,
        ClientSession Sb, FakeClientChannel Cb, Character B);

    private static async Task<World> Setup()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var a = new Character { CharId = A, Name = "Ann", Level = 19, MaxHp = 100, Hp = 100, MaxMp = 50, Mp = 50 };
        var b = new Character { CharId = B, Name = "Ben", Level = 19, MaxHp = 100, Hp = 100, MaxMp = 50, Mp = 50 };
        foreach (var id in new[] { Strike, Slow, Bless }) a.Skills.Add(new Skill { SkillId = id, Level = 1, Template = t.Skills[id] });
        var (sa, ca) = await h.EnterAsync(A, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: a);
        var (sb, cb) = await h.EnterAsync(B, 2, 2, x: 102, z: 100, name: "Ben", preSeeded: b);
        b.Hp = 100; a.Level = b.Level = 19;                                           // the enter resets them
        h.Service.CombatRng = new FixedRandom(0);
        ca.Clear(); cb.Clear(); h.World.Clear();
        return new World(h, sa, ca, a, sb, cb, b);
    }

    private static readonly SkillTemplate StrikeTpl = Skill(Strike, 0, Damage);

    private static RecallMon Pet(MapTestHarness h, Character owner, uint id, uint hp = 500)
    {
        var m = new RecallMon
        {
            Id = id, OwnerId = owner.CharId, RecallType = 1, Level = 10, MaxHp = 500, Hp = hp, MaxMp = 50, Mp = 50,
            Channel = 1, MapId = 0, PosX = 101, PosZ = 101,
            Attr = new MonAttrRow(Attr, 10, 500, 50, 0, Ap: 50, AttackLevel: 30),
        };
        m.Skills.Add(new Skill { SkillId = Strike, Level = 1, Template = StrikeTpl });
        owner.Recalls[id] = m;
        h.State.AddRecall(m);
        return m;
    }

    private static byte[] Finish(uint host, uint objId, byte type, ushort skill, params (uint id, byte type)[] targets)
    {
        var w = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        w.WriteUInt32(host); w.WriteUInt32(objId); w.WriteByte(type);
        w.WriteFloat(100); w.WriteFloat(0); w.WriteFloat(100);
        w.WriteUInt16(skill); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt16(0);
        w.WriteByte((byte)targets.Length);
        foreach (var (id, tt) in targets) { w.WriteUInt32(id); w.WriteByte(tt); }
        return w.ToArray();
    }

    // ================================ a player on someone else's summon ================================

    [Fact]
    public async Task APlayersStrike_HurtsAnEnemysSummon()
    {
        var x = await Setup();
        var pet = Pet(x.H, x.B, PetB);

        await x.H.CastAndHitAsync(x.Sa, Finish(A, A, OtPc, Strike, (PetB, OtRecall)));

        Assert.True(pet.Hp < 500);
        var r = new PacketReader(x.Cb.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal(A, r.ReadUInt32()); Assert.Equal(PetB, r.ReadUInt32());
    }

    [Fact]
    public async Task OnesOwnSummon_NeverTakesOnesHostileSkill()
    {
        var x = await Setup();
        var pet = Pet(x.H, x.A, PetA);

        await x.H.CastAndHitAsync(x.Sa, Finish(A, A, OtPc, Strike, (PetA, OtRecall)));

        Assert.Equal(500u, pet.Hp);
    }

    [Fact]
    public async Task KillingAnEnemysSummon_SendsItAway()
    {
        var x = await Setup();
        Pet(x.H, x.B, PetB, hp: 1);

        await x.H.CastAndHitAsync(x.Sa, Finish(A, A, OtPc, Strike, (PetB, OtRecall)));

        Assert.True(x.Cb.Has(Msg.CS_DIE_ACK));
        var del = new PacketReader(x.H.World.Last(Msg.MW_RECALLMONDEL_ACK)!);
        Assert.Equal(B, del.ReadUInt32()); del.ReadUInt32(); Assert.Equal(PetB, del.ReadUInt32());
    }

    [Fact]
    public async Task AHostileSkillsDebuff_StaysOnTheSummon()
    {
        var x = await Setup();
        var pet = Pet(x.H, x.B, PetB);

        await x.H.CastAndHitAsync(x.Sa, Finish(A, A, OtPc, Slow, (PetB, OtRecall)));

        Assert.Contains(pet.MaintainSkills, m => m.SkillId == Slow);
    }

    [Fact]
    public async Task ABuff_LandsOnSomeoneElsesSummon()
    {
        var x = await Setup();
        var pet = Pet(x.H, x.B, PetB);

        await x.H.CastAndHitAsync(x.Sa, Finish(A, A, OtPc, Bless, (PetB, OtRecall)));

        Assert.Contains(pet.MaintainSkills, m => m.SkillId == Bless);
        Assert.Equal(500u, pet.Hp);
    }

    // ================================ a summon on a player / a summon ================================

    [Fact]
    public async Task ASummonsHit_HurtsAPlayer()
    {
        var x = await Setup();
        Pet(x.H, x.A, PetA);

        await x.H.CastAndHitAsync(x.Sa, Finish(A, PetA, OtRecall, Strike, (B, OtPc)));

        Assert.True(x.B.Hp < 100);
        var r = new PacketReader(x.Cb.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal(PetA, r.ReadUInt32()); Assert.Equal(B, r.ReadUInt32());
    }

    [Fact]
    public async Task ASummonsKill_IsItsOwners()
    {
        var x = await Setup();
        Pet(x.H, x.A, PetA);
        x.B.Hp = 1;

        await x.H.CastAndHitAsync(x.Sa, Finish(A, PetA, OtRecall, Strike, (B, OtPc)));

        Assert.Equal(0u, x.B.Hp);
        Assert.Equal(1, x.A.MonthWin);                                                // the owner's win
        Assert.Equal(1, x.B.MonthLose);
        Assert.True(x.A.PvpTotalPoint > 0);                                            // and its points
    }

    [Fact]
    public async Task ASummon_FightsAnEnemysSummon_NotItsOwnersOtherOne()
    {
        var x = await Setup();
        Pet(x.H, x.A, PetA);
        var enemy = Pet(x.H, x.B, PetB);
        var mine = Pet(x.H, x.A, 0x6003);

        await x.H.CastAndHitAsync(x.Sa, Finish(A, PetA, OtRecall, Strike, (PetB, OtRecall), (0x6003, OtRecall)));

        Assert.True(enemy.Hp < 500);
        Assert.Equal(500u, mine.Hp);
    }
    // ================================ the summon's own buffs ================================

    private static MaintainSkill BuffOf(SkillDataRow row)
    {
        var tpl = Skill(990, 1, row);
        return new MaintainSkill { SkillId = 990, Level = 1, Template = tpl };
    }

    [Fact]
    public async Task ASummonsAttackBuff_MakesItHitHarder()
    {
        var x = await Setup();
        var pet = Pet(x.H, x.A, PetA);
        await x.H.CastAndHitAsync(x.Sa, Finish(A, PetA, OtRecall, Strike, (B, OtPc)));
        uint plain = 100 - x.B.Hp;

        x.B.Hp = 100;
        pet.MaintainSkills.Add(BuffOf(new SkillDataRow(SaBuff, SdtAbility, 0, 7 /* MTYPE_PAP */, 1, 40, 0, 0)));
        await x.H.CastAndHitAsync(x.Sa, Finish(A, PetA, OtRecall, Strike, (B, OtPc)));

        Assert.Equal(plain + 40, 100 - x.B.Hp);
    }

    [Fact]
    public async Task APhysicImmuneSummon_TakesNoPhysicalDamage()
    {
        var x = await Setup();
        var pet = Pet(x.H, x.B, PetB);
        pet.MaintainSkills.Add(BuffOf(new SkillDataRow(SaBuff, 6 /* SDT_STATUS */, 0, BuffLayer.StatusExceptPhysic, 1, 0, 0, 0)));

        await x.H.CastAndHitAsync(x.Sa, Finish(A, A, OtPc, Strike, (PetB, OtRecall)));

        Assert.Equal(500u, pet.Hp);
    }
}
