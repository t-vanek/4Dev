using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Exercise client packets, target effects, durability messages and persistence snapshots together.</summary>
public sealed class SkillPrerequisiteAndWearTests
{
    private const ushort Strike = 200, Stance = 201, TargetEffect = 202, Proc = 203, Reagent = 8412;
    private const byte Equip = 254, Bag = 255;
    private sealed record Context(TemplateStore T, MapTestHarness H, ClientSession S, FakeClientChannel C,
        Character Ch, Skill Skill, Monster Mon);

    private sealed class Rolls(params int[] values) : Random
    {
        private readonly Queue<int> _values = new(values);
        public override int Next(int maxValue)
        {
            Assert.NotEmpty(_values);
            int value = _values.Dequeue(); Assert.InRange(value, 0, maxValue - 1); return value;
        }
        public void Done() => Assert.Empty(_values);
    }

    private static SkillTemplate Template(ushort previous = 0, ushort target = 0, byte wear = 0)
    {
        var t = new SkillTemplate(Strike, 0, 10, 1, 5, 1, 1, 10, 0, 1000, 0, 1000, 0, 0, 0, 0xFFFF,
            Rate1stX: 100, PrevActiveId: previous, TargetActiveId: target, DuraSlot: wear);
        t.Data.Add(new SkillDataRow(0, 1, 1, 14, 2, 20, 0, 0)); // fixed HP drain, independent of gear
        return t;
    }

    private static async Task<Context> Setup(SkillTemplate? template = null)
    {
        var t = new TemplateStore();
        t.Classes[0] = new(1, 1, 1, 1, 1, 1); t.Races[0] = new(1, 1, 1, 1, 1, 1);
        t.Formulas[8] = new(200, 0, 0); t.Formulas[19] = new(100, 0, 0); t.Formulas[5] = new(10, 0, 0);
        template ??= Template(); t.Skills[Strike] = template;
        t.Items[Reagent] = new(Reagent, 0, new float[4], Kind: 65, Stack: 255, DelayGroup: 77, Delay: 5000);
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Fighter", Level = 10, MaxHp = 200, Hp = 150, MaxMp = 100, Mp = 100 };
        ch.Invens.Add(new Inven { InvenId = Equip }); ch.Invens.Add(new Inven { InvenId = Bag });
        var skill = new Skill { SkillId = Strike, Level = 3, Template = template }; ch.Skills.Add(skill);
        var (s, c) = await h.EnterAsync(1, 1, 123, name: ch.Name, x: 100, z: 100, preSeeded: ch);
        var mon = new Monster { Id = 0x30001, ChartId = 500, Level = 5, Hp = 100, MaxHp = 100,
            Channel = 1, MapId = 0, PosX = 100, PosZ = 100, Country = 3 };
        h.Service.SpawnMonster(mon); h.Service.NowMs = 10000; h.Service.CombatRng = new Random(1);
        ch.DbLoaded = true; h.Service.ApplyItemIdSeed(1000);
        c.Clear(); h.World.Clear();
        return new(t, h, s, c, ch, skill, mon);
    }

    private static Item Gear(Context x, byte slot = 0, uint current = 10, byte type = 1, byte repair = 1, uint max = 10)
    {
        ushort id = (ushort)(500 + slot);
        var template = new ItemTemplate(id, 0, new float[4], Type: type, Kind: 1, CanRepair: repair);
        x.T.Items[id] = template;
        var item = new Item { TemplateId = id, Template = template, ItemSlot = slot, Count = 1,
            DuraMax = max, DuraCur = current, DlId = 100 + slot };
        x.Ch.Equipped!.Items.Add(item); return item;
    }

    private static Task Cast(Context x, uint? target = null, byte type = 2)
        => x.H.Service.DispatchClientAsync(x.S, MapTestHarness.SkillUseReq(1, Strike,
            targets: new[] { (target ?? x.Mon.Id, type) }));

    private static byte[] FinishPacket(ushort skill, uint target, byte type = 2, uint obj = 1, byte actorType = 1)
        => new PacketWriter(Msg.CS_FINISHSKILL_ACK).WriteUInt32(1).WriteUInt32(obj).WriteByte(actorType)
            .WriteFloat(100).WriteFloat(0).WriteFloat(100).WriteUInt16(skill).WriteUInt32(0).WriteUInt32(0)
            .WriteUInt16(0).WriteByte(1).WriteUInt32(target).WriteByte(type).ToArray();

    private static Task Impact(Context x, bool legacy = false, ushort skill = Strike, uint? target = null, byte type = 2)
        => x.H.Service.DispatchClientAsync(x.S, legacy
            ? MapTestHarness.DefendReq(1, target ?? x.Mon.Id, targetType: type, skillId: skill)
            : FinishPacket(skill, target ?? x.Mon.Id, type));

    private static Task Action(Context x) => x.H.Service.DispatchClientAsync(x.S,
        new PacketWriter(Msg.CS_ACTION_REQ).WriteUInt32(1).WriteByte(1).WriteByte(1).WriteUInt32(0)
            .WriteUInt32(0).WriteByte(1).WriteUInt16(0).WriteUInt16(Strike).ToArray());

    private static Task Loop(Context x, uint actor = 1, byte type = 1) => x.H.Service.DispatchClientAsync(x.S,
        new PacketWriter(Msg.CS_LOOPSKILL_REQ).WriteUInt32(actor).WriteByte(type).WriteByte(1).WriteUInt16(0)
            .WriteUInt16(Strike).WriteFloat(100).WriteFloat(0).WriteFloat(100).WriteByte(1)
            .WriteUInt32(x.Mon.Id).WriteByte(2).WriteByte(1).ToArray());

    private static Task ActItem(Context x) => x.H.Service.DispatchClientAsync(x.S,
        new PacketWriter(Msg.CS_ACTITEMSUSE_REQ).WriteUInt32(1).WriteByte(1).WriteByte(1).WriteUInt16(0)
            .WriteUInt16(Strike).WriteByte(1).WriteUInt32(0).WriteUInt32(0).WriteFloat(100).WriteFloat(0).WriteFloat(100)
            .WriteUInt32(x.Mon.Id).WriteByte(2).ToArray());

    private static SkillUseResult Result(Context x, ushort msg = Msg.CS_SKILLUSE_ACK)
        => (SkillUseResult)new PacketReader(x.C.Last(msg)!).ReadByte();
    private static ushort BackSkill(Context x)
    {
        var r = new PacketReader(x.C.Last(Msg.CS_SKILLUSE_ACK)!);
        r.ReadByte(); r.ReadUInt32(); r.ReadByte(); r.ReadUInt16(); return r.ReadUInt16();
    }
    private static void Active(List<MaintainSkill> effects, ushort id)
        => effects.Add(new MaintainSkill { SkillId = id });
    private static void NoCosts(Context x)
    {
        Assert.Equal((150u, 100u), (x.Ch.Hp, x.Ch.Mp)); Assert.True(x.Skill.CanUse(x.H.Service.NowMs));
        Assert.False(x.C.Has(Msg.CS_DURATIONDEC_ACK)); Assert.False(x.C.Has(Msg.CS_DURATIONEND_ACK));
        Assert.Empty(x.H.Service.PendingItemUpserts); Assert.Empty(x.H.Service.PendingItemDeletes);
    }

    [Theory]
    [InlineData(0)] // learned but inactive
    [InlineData(1)] // wrong active effect
    [InlineData(2)] // expired, not yet removed by the maintain timer
    public async Task MissingCasterEffectRejectsActionAndCastWithoutCosts(int reason)
    {
        var x = await Setup(Template(previous: Stance, wear: 2)); Gear(x);
        x.Ch.Skills.Add(new Skill { SkillId = Stance, Level = 1 });
        if (reason == 1) Active(x.Ch.MaintainSkills, TargetEffect);
        if (reason == 2) x.Ch.MaintainSkills.Add(new MaintainSkill { SkillId = Stance, StartTick = 1, MaintainTick = 9999 });
        await Action(x); Assert.Equal(SkillUseResult.NeedPrevAct, Result(x, Msg.CS_ACTION_ACK));
        await Cast(x); Assert.Equal(SkillUseResult.NeedPrevAct, Result(x)); NoCosts(x);
        await Impact(x); Assert.Equal(100u, x.Mon.Hp);
    }

    [Fact]
    public async Task ActiveCasterEffectAllowsTheSkill_WithoutRequiringItToBeLearned()
    {
        var x = await Setup(Template(previous: Stance)); Active(x.Ch.MaintainSkills, Stance);
        await Action(x); Assert.Equal(SkillUseResult.Success, Result(x, Msg.CS_ACTION_ACK));
        await Cast(x); Assert.Equal(SkillUseResult.Success, Result(x));
        Assert.Equal((145u, 90u), (x.Ch.Hp, x.Ch.Mp));
        await Impact(x); Assert.Equal(80u, x.Mon.Hp);
    }

    [Fact]
    public async Task AnEffectRemovedAfterTheAnimationDoesNotAuthorizeTheActualCast()
    {
        var x = await Setup(Template(previous: Stance)); Active(x.Ch.MaintainSkills, Stance);
        await Action(x); Assert.Equal(SkillUseResult.Success, Result(x, Msg.CS_ACTION_ACK));
        x.Ch.MaintainSkills.Clear(); await Cast(x);
        Assert.Equal(SkillUseResult.NeedPrevAct, Result(x)); NoCosts(x);
    }

    [Fact]
    public async Task LoopChecksTheCastersTargetActiveField_AndDoesNotUseTheOrdinaryPreviousField()
    {
        var x = await Setup(Template(previous: Stance, target: TargetEffect));
        Active(x.Ch.MaintainSkills, Stance);
        await Loop(x); Assert.Equal(SkillUseResult.NeedPrevAct, Result(x, Msg.CS_LOOPSKILL_ACK)); NoCosts(x);
        x.Ch.MaintainSkills.Clear(); Active(x.Ch.MaintainSkills, TargetEffect);
        await Loop(x); Assert.Equal(SkillUseResult.Success, Result(x, Msg.CS_LOOPSKILL_ACK));
        Assert.Equal((145u, 90u), (x.Ch.Hp, x.Ch.Mp));
    }

    [Fact]
    public async Task ActItemRejectsAMissingEffectBeforeConsumingTheItemOrItsCooldown()
    {
        var x = await Setup(Template(previous: Stance) with { UseItem = Reagent });
        var item = new Item { TemplateId = Reagent, Template = x.T.Items[Reagent], Count = 2 };
        x.Ch.FindInven(Bag)!.Items.Add(item);
        await ActItem(x); Assert.Equal(SkillUseResult.NeedPrevAct, Result(x));
        Assert.Equal(2, item.Count); Assert.Empty(x.Ch.ItemCoolTime); NoCosts(x);
        Active(x.Ch.MaintainSkills, Stance); await ActItem(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Equal(1, item.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TargetEffectIsRequiredAtImpact_ForFinishAndLegacyDefend(bool legacy, bool expired)
    {
        var x = await Setup(Template(target: TargetEffect));
        if (expired) x.Mon.MaintainSkills.Add(new MaintainSkill { SkillId = TargetEffect, StartTick = 1, MaintainTick = 9999 });
        await Cast(x); Assert.Equal(SkillUseResult.Success, Result(x));
        await Impact(x, legacy); Assert.Equal(100u, x.Mon.Hp);
        x.Mon.MaintainSkills.Clear(); Active(x.Mon.MaintainSkills, TargetEffect);
        x.H.Service.NowMs += 10000; await Cast(x); await Impact(x, legacy);
        Assert.Equal(80u, x.Mon.Hp);
    }

    [Fact]
    public async Task RemovingTheTargetEffectAfterTheCastBlocksTheHit()
    {
        var x = await Setup(Template(target: TargetEffect)); Active(x.Mon.MaintainSkills, TargetEffect);
        await Cast(x); x.Mon.MaintainSkills.Clear(); await Impact(x); Assert.Equal(100u, x.Mon.Hp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositiveCuresAlsoRequireTheTargetsActiveEffect(bool legacy)
    {
        var t = Template(target: TargetEffect) with { Positive = 1, UseHp = 0, UseMp = 0 };
        t.Data.Clear(); t.Data.Add(new SkillDataRow(0, 5, 0, 8, 1, 20, 0, 0));
        var x = await Setup(t);
        await Cast(x, 1, 1); await Impact(x, legacy, target: 1, type: 1); Assert.Equal(150u, x.Ch.Hp);
        Active(x.Ch.MaintainSkills, TargetEffect); x.H.Service.NowMs += 10000;
        await Cast(x, 1, 1); await Impact(x, legacy, target: 1, type: 1); Assert.InRange(x.Ch.Hp, 170u, 173u);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummonUsesItsOwnActiveEffects_ForCastsAndLoops(bool loop)
    {
        var x = await Setup(Template(previous: Stance, target: TargetEffect));
        var m = new RecallMon { Id = 800, OwnerId = 1, RecallType = 1, Channel = 1, MapId = 0,
            Hp = 150, MaxHp = 200, Mp = 100, MaxMp = 100, PosX = 100, PosZ = 100 };
        m.Skills.Add(new Skill { SkillId = Strike, Level = 1, Template = x.Skill.Template });
        x.Ch.Recalls[m.Id] = m; x.H.State.AddRecall(m);
        Active(x.Ch.MaintainSkills, loop ? TargetEffect : Stance);
        Task Send() => loop ? Loop(x, m.Id, 7) : x.H.Service.DispatchClientAsync(x.S,
            MapTestHarness.SkillUseReq(m.Id, Strike, attackType: 7, targets: new[] { (x.Mon.Id, (byte)2) }));
        await Send(); Assert.Equal(SkillUseResult.NeedPrevAct, Result(x, loop ? Msg.CS_LOOPSKILL_ACK : Msg.CS_SKILLUSE_ACK));
        Assert.Equal((150u, 100u), (m.Hp, m.Mp));
        Active(m.MaintainSkills, loop ? TargetEffect : Stance); await Send();
        Assert.Equal(SkillUseResult.Success, Result(x, loop ? Msg.CS_LOOPSKILL_ACK : Msg.CS_SKILLUSE_ACK));
        Assert.Equal((145u, 90u), (m.Hp, m.Mp)); Assert.Equal((150u, 100u), (x.Ch.Hp, x.Ch.Mp));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 9)]
    [InlineData(2, 8)]
    public async Task OrdinaryCastWearsTheSelectedWeapon_AndQueuesItsDurabilitySnapshot(int roll, uint after)
    {
        var x = await Setup(Template(wear: 1)); var weapon = Gear(x, 2); var other = Gear(x);
        var rng = new Rolls(roll); x.H.Service.EquipmentRng = rng;
        await Cast(x); rng.Done(); Assert.Equal(after, weapon.DuraCur); Assert.Equal(10u, other.DuraCur);
        var r = new PacketReader(x.C.Last(Msg.CS_DURATIONDEC_ACK)!);
        Assert.Equal(Equip, r.ReadByte()); Assert.Equal((byte)2, r.ReadByte()); Assert.Equal(after, r.ReadUInt32());
        if (roll == 0) Assert.Empty(x.H.Service.PendingItemUpserts);
        else Assert.Equal(after, x.H.Service.PendingItemUpserts[weapon.DlId].data.DuraCur);
        await Impact(x); await Impact(x, legacy: true); Assert.Equal(after, weapon.DuraCur); Assert.Equal(80u, x.Mon.Hp);
        Assert.Single(x.C.WithId(Msg.CS_DURATIONDEC_ACK));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeleeWearAffectsBothWeapons_AndExcludesShieldsInEitherSlot(bool shieldFirst)
    {
        var x = await Setup(Template(wear: 2));
        var first = Gear(x, 0, type: shieldFirst ? (byte)6 : (byte)1);
        var second = Gear(x, 1, type: shieldFirst ? (byte)1 : (byte)6); var ranged = Gear(x, 2);
        var rng = new Rolls(2); x.H.Service.EquipmentRng = rng;
        await Cast(x); rng.Done();
        Assert.Equal(shieldFirst ? 10u : 8u, first.DuraCur);
        Assert.Equal(shieldFirst ? 8u : 10u, second.DuraCur); Assert.Equal(10u, ranged.DuraCur);
    }

    [Fact]
    public async Task DualWeaponsWearIndependently()
    {
        var x = await Setup(Template(wear: 2)); var first = Gear(x); var second = Gear(x, 1);
        var rng = new Rolls(1, 2); x.H.Service.EquipmentRng = rng;
        await Cast(x); rng.Done(); Assert.Equal((9u, 8u), (first.DuraCur, second.DuraCur));
        Assert.Equal(2, x.C.WithId(Msg.CS_DURATIONDEC_ACK).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShieldSelectorWearsOnlyASecondaryShield(bool shield)
    {
        var x = await Setup(Template(wear: 4)); var first = Gear(x); var second = Gear(x, 1, type: shield ? (byte)6 : (byte)1);
        var rng = shield ? new Rolls(2) : new Rolls(); x.H.Service.EquipmentRng = rng;
        await Cast(x); rng.Done(); Assert.Equal(10u, first.DuraCur); Assert.Equal(shield ? 8u : 10u, second.DuraCur);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 5)]
    [InlineData(2, 6)]
    [InlineData(3, 8)]
    [InlineData(4, 7)]
    [InlineData(5, 4)]
    public async Task ArmorSelectorUsesExactlyOneOfTheSixNativeSlots(int roll, byte selected)
    {
        var x = await Setup(Template(wear: 3));
        var items = new byte[] { 3, 4, 5, 6, 7, 8 }.Select(slot => Gear(x, slot)).ToArray();
        var rng = new Rolls(roll, 1); x.H.Service.EquipmentRng = rng;
        await Cast(x); rng.Done(); Assert.All(items, item => Assert.Equal(item.ItemSlot == selected ? 9u : 10u, item.DuraCur));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task ZeroAndUnknownSelectorsDoNotWearAnything(byte selector)
    {
        var x = await Setup(Template(wear: selector)); var weapon = Gear(x); var rng = new Rolls();
        x.H.Service.EquipmentRng = rng; await Cast(x); rng.Done(); Assert.Equal(10u, weapon.DuraCur);
        Assert.False(x.C.Has(Msg.CS_DURATIONDEC_ACK)); Assert.False(x.C.Has(Msg.CS_DURATIONEND_ACK));
    }

    [Theory]
    [InlineData(0)] // rejected MP gate
    [InlineData(1)] // rejected cooldown
    [InlineData(2)] // missing ammunition
    [InlineData(3)] // missing active effect
    public async Task FailedCastsNeverWearEquipment(int gate)
    {
        var x = await Setup(Template(wear: 2) with { WeaponId = gate == 2 ? 1u : 0u, PrevActiveId = gate == 3 ? Stance : (ushort)0 });
        var weapon = Gear(x); var rng = new Rolls(); x.H.Service.EquipmentRng = rng;
        if (gate == 0) x.Ch.Mp = 0;
        if (gate == 1) x.Skill.SetCooldown(x.H.Service.NowMs, 5000);
        if (gate == 2) weapon.Template = weapon.Template! with { UseItemKind = 24, UseItemCount = 1 };
        await Cast(x); rng.Done(); Assert.NotEqual(SkillUseResult.Success, Result(x)); Assert.Equal(10u, weapon.DuraCur);
        Assert.False(x.C.Has(Msg.CS_DURATIONDEC_ACK)); Assert.Empty(x.H.Service.PendingItemUpserts);
    }

    [Fact]
    public async Task PreviewAndLoopDoNotApplyTheOrdinaryCastWearAgain()
    {
        var x = await Setup(Template(wear: 2)); var weapon = Gear(x); var rng = new Rolls(1);
        x.H.Service.EquipmentRng = rng;
        await Action(x); Assert.Equal(10u, weapon.DuraCur);
        await Cast(x); Assert.Equal(9u, weapon.DuraCur); rng.Done();
        x.H.Service.NowMs += 10000; await Loop(x); Assert.Equal(SkillUseResult.Success, Result(x, Msg.CS_LOOPSKILL_ACK));
        Assert.Equal(9u, weapon.DuraCur); Assert.Single(x.C.WithId(Msg.CS_DURATIONDEC_ACK));
    }

    [Fact]
    public async Task IndestructibleAndAlreadyBrokenItemsAreNotDestroyedByNaturalWear()
    {
        var x = await Setup(Template(wear: 2)); var indestructible = Gear(x, current: 0, max: 0, repair: 0);
        var broken = Gear(x, 1, current: 0); var rng = new Rolls(); x.H.Service.EquipmentRng = rng;
        await Cast(x); rng.Done(); Assert.Contains(indestructible, x.Ch.Equipped!.Items); Assert.Contains(broken, x.Ch.Equipped.Items);
        Assert.False(x.C.Has(Msg.CS_DURATIONEND_ACK)); Assert.Empty(x.H.Service.PendingItemDeletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenItemsAreKeptForRepairOrDeletedAccordingToTheirTemplate(bool repairable)
    {
        var x = await Setup(Template(wear: 2)); var weapon = Gear(x, current: 1, repair: repairable ? (byte)1 : (byte)0);
        weapon.Attr = new ItemAttr(1, 1, 0, 50, 50, 0, 0, 0, 0, 0);
        uint beforeAp = StatEngine.MinAp(x.Ch, false, x.T);
        x.H.Service.EquipmentRng = new Rolls(2); await Cast(x);
        Assert.Equal(0u, weapon.DuraCur); Assert.True(StatEngine.MinAp(x.Ch, false, x.T) < beforeAp);
        var r = new PacketReader(x.C.Last(Msg.CS_DURATIONEND_ACK)!);
        Assert.Equal(Equip, r.ReadByte()); Assert.Equal((byte)0, r.ReadByte()); Assert.Equal(repairable ? (byte)0 : (byte)1, r.ReadByte());
        Assert.Equal(repairable, x.Ch.Equipped!.Items.Contains(weapon)); Assert.True(x.C.Has(Msg.CS_CHARSTATINFO_ACK));
        if (repairable) Assert.Equal(0u, x.H.Service.PendingItemUpserts[weapon.DlId].data.DuraCur);
        else { Assert.Contains(weapon.DlId, x.H.Service.PendingItemDeletes); Assert.True(x.C.Has(Msg.CS_DELITEM_ACK)); }
    }

    [Fact]
    public async Task ExplicitDeleteUserEffectExhaustsTheSelectedItemEvenOnAZeroWearRoll()
    {
        var t = Template(wear: 2); t.Data.Add(new SkillDataRow(0, 8, 0, 1, 0, 0, 0, 0));
        var x = await Setup(t); var weapon = Gear(x); x.H.Service.EquipmentRng = new Rolls(0);
        await Cast(x); Assert.Equal(0u, weapon.DuraCur); Assert.True(x.C.Has(Msg.CS_DURATIONEND_ACK));
    }

    private static void AddProc(Context x, Item weapon, ushort chance = 100, byte optionId = 1, ushort proc = Proc)
    {
        var t = Template() with { Id = proc, UseHp = 0, UseMp = 0 };
        t.Data.Clear(); t.Data.Add(new SkillDataRow(0, 1, 1, 14, 2, 10, 10, 1)); // 10 at level 1, 30 at root level 3
        x.T.Skills[proc] = t;
        var magic = new MagicTemplate(optionId, 0, 1, AutoSkill: proc); x.T.Magics[optionId] = magic;
        weapon.Magic.Add(new MagicOption(optionId, chance, magic));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServerRolledWeaponSkillIsAnnouncedAndAuthorizedAtLevelOne_WithNoReplay(bool legacy)
    {
        var x = await Setup(Template(wear: 2)); var weapon = Gear(x); AddProc(x, weapon);
        var rng = new Rolls(99, 1); x.H.Service.EquipmentRng = rng; await Cast(x); rng.Done();
        Assert.Equal(Proc, BackSkill(x)); Assert.DoesNotContain(x.Ch.Skills, k => k.SkillId == Proc);
        await Impact(x, legacy, Proc); Assert.Equal(90u, x.Mon.Hp);
        await Impact(x, !legacy, Proc); Assert.Equal(90u, x.Mon.Hp);
        await Impact(x, legacy); Assert.Equal(70u, x.Mon.Hp); Assert.Equal(9u, weapon.DuraCur);
    }

    [Fact]
    public async Task AFailedProcRollDoesNotAuthorizeAClientInventedWeaponSkill()
    {
        var x = await Setup(Template(wear: 2)); var weapon = Gear(x); AddProc(x, weapon, chance: 50);
        x.H.Service.EquipmentRng = new Rolls(50, 1); await Cast(x); Assert.Equal((ushort)0, BackSkill(x));
        await Impact(x, skill: Proc); Assert.Equal(100u, x.Mon.Hp);
        await Impact(x, legacy: true, skill: Proc); Assert.Equal(100u, x.Mon.Hp);
        await Impact(x); Assert.Equal(80u, x.Mon.Hp);
    }

    [Fact]
    public async Task FirstSuccessfulMagicOptionByIdWins_AndSecondaryWeaponIsTheFallback()
    {
        var x = await Setup(Template(wear: 2)); var first = Gear(x); var second = Gear(x, 1);
        AddProc(x, first, optionId: 9, proc: 209); AddProc(x, first, chance: 30, optionId: 2, proc: 208);
        AddProc(x, second, optionId: 1, proc: 207);
        var rng = new Rolls(30, 99, 1, 2); x.H.Service.EquipmentRng = rng; await Cast(x); rng.Done();
        Assert.Equal((ushort)209, BackSkill(x));
        x.H.Service.NowMs += 10000; first.Magic.Clear();
        rng = new Rolls(1, 99, 2); x.H.Service.EquipmentRng = rng; await Cast(x); rng.Done();
        Assert.Equal((ushort)207, BackSkill(x));
    }

    [Fact]
    public async Task AnIndestructibleWeaponCanProcWithoutDurabilityLoss()
    {
        var x = await Setup(Template(wear: 2)); var weapon = Gear(x, current: 0, max: 0); AddProc(x, weapon);
        var rng = new Rolls(0); x.H.Service.EquipmentRng = rng; await Cast(x); rng.Done();
        Assert.Equal(Proc, BackSkill(x)); Assert.False(x.C.Has(Msg.CS_DURATIONEND_ACK));
        await Impact(x, skill: Proc); Assert.Equal(90u, x.Mon.Hp);
    }
}
