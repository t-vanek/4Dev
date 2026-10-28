using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Skill items, random skills, the item delay, the FINISHSKILL special cases (Sixth Sense / Inner Eye, Deadly Poison) and
/// the look-change items (C++ OnCS_ITEMUSE_REQ IK_SKILL / IK_REVIVAL / IK_FACE…IK_SEX, RandTransSkill / RandBuffSkill,
/// ChangeCharBase).
/// </summary>
public class ItemSkillTests
{
    private const byte Backpack = 0xFF;
    private const byte IkSkill = 33, IkRevival = 41, IkFace = 45, IkSex = 49;
    private const byte SaBuff = 3, SdtAbility = 1, SdtTrans = 3, SdtStatus = 6;
    private const ushort Tid = 400;
    private const ushort Might = 1100, Lucky = 1101, RandomBag = 1102, BagA = 1103, BagB = 1104, Morph = 1105, MorphA = 1106;
    private const ushort SixthSense = 128, SixthEffect = 129, Poison = 3603, PoisonDot = 3604;

    private static SkillTemplate Skill(ushort id, byte positive, params SkillDataRow[] rows)
    {
        var t = new SkillTemplate(id, 0, 0, 0, 0, 0, 1, 10, 1, 0, 0, 0, 0, 0, Positive: positive, MapId: 0xFFFF, Duration: 60_000);
        t.Data.AddRange(rows);
        return t;
    }

    private static SkillDataRow Buff(ushort v = 5) => new(SaBuff, SdtAbility, 0, 1 /* MTYPE_STR */, 1, v, 0, 0);

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);
        foreach (var s in new[]
        {
            Skill(Might, 1, Buff()),
            Skill(Lucky, 1, Buff(), new SkillDataRow(SaBuff, SdtStatus, 0, 42 /* ITEMUPGRADE */, 1, 0, 0, 0)),
            Skill(RandomBag, 1, new SkillDataRow(SaBuff, SdtStatus, 0, 61 /* RANDOM */, 1, BagA, 2, 0)),
            Skill(BagA, 1, Buff(7)),
            Skill(BagB, 1, Buff(9)),
            Skill(Morph, 0, new SkillDataRow(SaBuff, SdtTrans, 0, 3 /* TRANS_RANDOM */, 1, MorphA, 1, 0)),
            Skill(MorphA, 0, Buff(1)),                                           // not a transformation: refused
            Skill(SixthSense, 1, Buff()), Skill(SixthEffect, 1, new SkillDataRow(SaBuff, SdtAbility, 0, 2 /* MTYPE_DEX */, 1, 5, 0, 0)),
            Skill(PoisonDot, 0, new SkillDataRow(0, SdtAbility, 1, 30, 1, 0, 0, 0)),
            Skill(Poison, 0),
        })
            t.Skills[s.Id] = s;
        return t;
    }

    private static Item ItemOf(byte kind, ushort useValue, uint delay = 0, ushort group = 0)
        => new() { ItemSlot = 0, TemplateId = Tid, Count = 5,
                   Template = new ItemTemplate(Tid, 0, new[] { 1f, 0f, 0f, 0f }, Kind: kind, UseValue: useValue, DefaultLevel: 1,
                       Delay: delay, DelayGroup: group) };

    private TemplateStore _t = Store();

    private async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch)> Setup(Item? item = null)
    {
        var t = _t;
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Ann", Level = 20, MaxHp = 100, Hp = 100, MaxMp = 50, Mp = 50 };
        ch.Invens.Add(new Inven { InvenId = Backpack });
        if (item is not null) ch.FindInven(Backpack)!.Items.Add(item);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: ch);
        h.Service.CombatRng = new FixedRandom(0);
        c.Clear(); h.World.Clear();
        return (h, s, c, ch);
    }

    private static ItemUseResult Result(FakeClientChannel c) => (ItemUseResult)new PacketReader(c.Last(Msg.CS_ITEMUSE_ACK)!).ReadByte();
    private static byte[] Use(ushort group = 0) => MapTestHarness.ItemUseReq(Tid, Backpack, 0, group);

    // ================================ skill items ================================

    [Fact]
    public async Task ASkillItem_PutsItsBuffOnItsUser_AndIsUsedUp()
    {
        var (h, s, c, ch) = await Setup(ItemOf(IkSkill, Might));

        await h.Service.DispatchClientAsync(s, Use());

        Assert.Equal(ItemUseResult.Success, Result(c));
        Assert.Contains(ch.MaintainSkills, m => m.SkillId == Might);
        Assert.Equal(4, ch.FindInven(Backpack)!.FindItem(0)!.Count);
    }

    [Fact]
    public async Task TheSameBuffTwice_IsRefused()
    {
        var (h, s, c, ch) = await Setup(ItemOf(IkSkill, Might));
        await h.Service.DispatchClientAsync(s, Use());

        await h.Service.DispatchClientAsync(s, Use());

        Assert.Equal(ItemUseResult.NotFound, Result(c));
        Assert.Equal(4, ch.FindInven(Backpack)!.FindItem(0)!.Count);
    }

    [Fact]
    public async Task ARandomBuffItem_LandsAsOneOfItsRun()
    {
        var (h, s, c, ch) = await Setup(ItemOf(IkSkill, RandomBag));

        await h.Service.DispatchClientAsync(s, Use());

        Assert.Equal(ItemUseResult.Success, Result(c));
        Assert.Equal(new[] { BagA }, ch.MaintainSkills.Select(m => m.SkillId).ToArray());   // FixedRandom(0): the first
    }

    [Fact]
    public async Task ARandomTransformation_ThatPicksNoTransformation_IsRefused()
    {
        var (h, s, c, ch) = await Setup(ItemOf(IkSkill, Morph));

        await h.Service.DispatchClientAsync(s, Use());

        Assert.Equal(ItemUseResult.NotFound, Result(c));
        Assert.Empty(ch.MaintainSkills);
    }

    [Fact]
    public async Task ARevivalItem_IsOnlyForTheDead()
    {
        var (h, s, c, _) = await Setup(ItemOf(IkRevival, Might));

        await h.Service.DispatchClientAsync(s, Use());

        Assert.Equal(ItemUseResult.NotFound, Result(c));
    }

    [Fact]
    public async Task TheItemDelay_HoldsItsGroupBack()
    {
        var (h, s, c, ch) = await Setup(ItemOf(IkSkill, RandomBag, delay: 10_000, group: 7));
        await h.Service.DispatchClientAsync(s, Use(7));
        ch.MaintainSkills.Clear();

        await h.Service.DispatchClientAsync(s, Use(7));
        Assert.Equal(ItemUseResult.NotFound, Result(c));

        h.Service.NowMs += 10_000;
        await h.Service.DispatchClientAsync(s, Use(7));
        Assert.Equal(ItemUseResult.Success, Result(c));
    }

    // ================================ FINISHSKILL ================================

    private static byte[] Finish(ushort skill, uint target, byte type = 1)
    {
        var w = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        w.WriteUInt32(1); w.WriteUInt32(1); w.WriteByte(1);
        w.WriteFloat(100); w.WriteFloat(0); w.WriteFloat(100);
        w.WriteUInt16(skill); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt16(0);
        w.WriteByte(1); w.WriteUInt32(target); w.WriteByte(type);
        return w.ToArray();
    }

    [Fact]
    public async Task ARandomSkillCast_LandsAsOneOfItsRun()
    {
        var (h, s, _, ch) = await Setup();
        ch.Skills.Add(new Skill { SkillId = RandomBag, Level = 1, Template = _t.Skills[RandomBag] });

        await h.CastAndHitAsync(s, Finish(RandomBag, 1));

        Assert.Equal(new[] { BagA }, ch.MaintainSkills.Select(m => m.SkillId).ToArray());
    }

    [Fact]
    public async Task SixthSense_FirstTakesAwayItsOwnEffect()
    {
        var (h, s, _, ch) = await Setup();
        ch.Skills.Add(new Skill { SkillId = SixthSense, Level = 1, Template = _t.Skills[SixthSense] });
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = SixthEffect, Level = 1, Template = _t.Skills[SixthEffect] });

        await h.CastAndHitAsync(s, Finish(SixthSense, 1));

        Assert.DoesNotContain(ch.MaintainSkills, m => m.SkillId == SixthEffect);
        Assert.Contains(ch.MaintainSkills, m => m.SkillId == SixthSense);
    }

    [Fact]
    public async Task DeadlyPoisonsDamage_NeedsDeadlyPoison_AndHitsAtItsLevel()
    {
        var (h, s, _, ch) = await Setup();
        var mob = new Monster { Id = 0x70001, ChartId = 500, Level = 5, MaxHp = 1000, Hp = 1000, PosX = 101, PosZ = 100,
            Region = 7, Channel = 1, MapId = 0, Country = 3 };
        h.Service.SpawnMonster(mob);

        await h.CastAndHitAsync(s, Finish(PoisonDot, mob.Id, Monster.OtMon));
        Assert.Equal(1000u, mob.Hp);                                                   // no Deadly Poison: nothing

        ch.Skills.Add(new Skill { SkillId = Poison, Level = 3, Template = _t.Skills[Poison] });
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, Poison, targets: new[] { (mob.Id, Monster.OtMon) }));
        await h.Service.DispatchClientAsync(s, Finish(PoisonDot, mob.Id, Monster.OtMon));
        Assert.True(mob.Hp < 1000);
    }

    // ================================ look changes ================================

    [Fact]
    public async Task AFaceItem_AsksTheWorldForADifferentFace_AndIsUsedUp()
    {
        var (h, s, c, ch) = await Setup(ItemOf(IkFace, 0));
        ch.Face = 0;

        await h.Service.DispatchClientAsync(s, Use());

        Assert.Equal(ItemUseResult.Success, Result(c));
        var r = new PacketReader(h.World.Last(Msg.MW_CHANGECHARBASE_ACK)!);
        Assert.Equal(1u, r.ReadUInt32()); r.ReadUInt32();
        Assert.Equal(IkFace, r.ReadByte());
        Assert.NotEqual(0, r.ReadByte());                                              // FixedRandom(0) picks 0 → the next one
    }

    [Fact]
    public async Task TheWorldsAnswer_ChangesTheLook_AndShowsItAround()
    {
        var (h, s, c, ch) = await Setup();
        var w = new PacketWriter(Msg.MW_CHANGECHARBASE_REQ);
        w.WriteUInt32(1); w.WriteUInt32(s.Key); w.WriteByte(IkSex); w.WriteByte(1); w.WriteUInt16(0); w.WriteString("Ann");

        await h.Service.DispatchWorldAsync(w.ToArray());

        Assert.Equal(1, ch.Sex);
        var r = new PacketReader(c.Last(Msg.CS_CHANGECHARBASE_ACK)!);
        r.ReadByte(); Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(IkSex, r.ReadByte()); Assert.Equal(1, r.ReadByte());
    }

    [Fact]
    public async Task NoLookChange_WhileTransformed()
    {
        var (h, s, c, ch) = await Setup(ItemOf(IkFace, 0));
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = Morph, Level = 1, Template = _t.Skills[Morph] });

        await h.Service.DispatchClientAsync(s, Use());

        Assert.Equal(ItemUseResult.Full, Result(c));                                   // C++: IU_FULL for an item that did not take
        Assert.False(h.World.Has(Msg.MW_CHANGECHARBASE_ACK));
    }
}
