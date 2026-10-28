using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Real cast/loop/impact packets, with item counts, rejection atomicity and persistence snapshots.</summary>
public sealed class SkillItemTests
{
    private const ushort SkillId = 200, Bow = 701, Crossbow = 901, Sword = 101, Arrow = 8400, Bolt = 8401, Reagent = 8412;
    private const byte Backpack = 255, Equip = 254;
    private const uint Ranged = (1u << 5) | (1u << 6);

    private sealed record Context(TemplateStore T, MapTestHarness H, ClientSession S, FakeClientChannel C,
        Character Ch, Skill Skill, Monster Mon);

    private static SkillTemplate Template(uint weapon = Ranged, ushort item = 0, byte missiles = 0)
    {
        var t = new SkillTemplate(SkillId, 0, 10, 1, 5, 1, 1, 1, 0, 1000, 0, 1000, 0, 0, 0, 0xFFFF,
            Rate1stX: 100, WeaponId: weapon, UseItem: item, TargetHit: 3);
        t.Data.Add(new SkillDataRow(0, 1, 1, 14, 2, 20, 0, 0)); // direct HP drain
        if (missiles != 0) t.Data.Add(new SkillDataRow(0, 1, 2, 36, 1, missiles, 0, 0));
        return t;
    }

    private static async Task<Context> Setup(SkillTemplate? template = null)
    {
        var t = new TemplateStore();
        t.Classes[0] = new(1, 1, 1, 1, 1, 1); t.Races[0] = new(1, 1, 1, 1, 1, 1);
        t.Formulas[8] = new(200, 0, 0); t.Formulas[19] = new(100, 0, 0); t.Formulas[5] = new(10, 0, 0);
        t.Items[Bow] = new(Bow, 0, new float[4], Type: 4, Kind: 6, Stack: 1, SlotId: 1u << 2, ClassId: uint.MaxValue,
            PrmSlot: 2, UseItemKind: 24, UseItemCount: 1);
        t.Items[Crossbow] = t.Items[Bow] with { ItemId = Crossbow, Kind = 7, UseItemKind = 25 };
        t.Items[Sword] = t.Items[Bow] with { ItemId = Sword, Type = 1, Kind = 1, SlotId = 1, PrmSlot = 0, UseItemKind = 0, UseItemCount = 0 };
        t.Items[Arrow] = new(Arrow, 0, new float[4], Kind: 24, Stack: 255);
        t.Items[Bolt] = t.Items[Arrow] with { ItemId = Bolt, Kind = 25 };
        t.Items[Reagent] = new(Reagent, 0, new float[4], Kind: 65, Stack: 255, DelayGroup: 77, Delay: 5000);
        template ??= Template(); t.Skills[template.Id] = template;
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Archer", Level = 10, MaxHp = 200, Hp = 150, MaxMp = 100, Mp = 100 };
        // Intentionally not in inventory-id order: consumption must use the native sorted map order.
        ch.Invens.Add(new Inven { InvenId = Backpack }); ch.Invens.Add(new Inven { InvenId = Equip });
        ch.Invens.Add(new Inven { InvenId = 0 });
        var skill = new Skill { SkillId = template.Id, Level = 1, Template = template }; ch.Skills.Add(skill);
        var (s, c) = await h.EnterAsync(1, 10, 123, name: ch.Name, x: 100, z: 100, preSeeded: ch);
        var mon = new Monster { Id = 0x30001, ChartId = 500, Level = 5, Hp = 100, MaxHp = 100,
            Channel = 1, MapId = 0, PosX = 100, PosZ = 100, Country = 3 };
        h.Service.SpawnMonster(mon); h.Service.NowMs = 10000; h.Service.CombatRng = new Random(1);
        c.Clear(); h.World.Clear();
        return new(t, h, s, c, ch, skill, mon);
    }

    private static Item Give(Context x, ushort id, byte bag = Backpack, byte slot = 0, byte count = 1, long dlId = 0)
    {
        var item = new Item { TemplateId = id, Template = x.T.Items[id], ItemSlot = slot, Count = count, DlId = dlId };
        if (bag == Equip) { item.DuraMax = 100; item.DuraCur = 100; }
        if (x.Ch.FindInven(bag) is not { } inv) { inv = new Inven { InvenId = bag }; x.Ch.Invens.Add(inv); }
        inv.Items.Add(item);
        return item;
    }

    private static Task Cast(Context x, params (uint Id, byte Type)[] targets)
    {
        if (targets.Length == 0) targets = new[] { (x.Mon.Id, (byte)2) };
        return x.H.Service.DispatchClientAsync(x.S, MapTestHarness.SkillUseReq(1, SkillId, targets: targets));
    }

    private static Task Loop(Context x, params (uint Id, byte Type, bool IsTarget)[] targets)
    {
        if (targets.Length == 0) targets = new[] { (x.Mon.Id, (byte)2, true) };
        var w = new PacketWriter(Msg.CS_LOOPSKILL_REQ).WriteUInt32(1).WriteByte(1).WriteByte(1).WriteUInt16(0)
            .WriteUInt16(SkillId).WriteFloat(100).WriteFloat(0).WriteFloat(100).WriteByte((byte)targets.Length);
        foreach (var target in targets) w.WriteUInt32(target.Id).WriteByte(target.Type).WriteByte(target.IsTarget ? (byte)1 : (byte)0);
        return x.H.Service.DispatchClientAsync(x.S, w.ToArray());
    }

    private static Task Act(Context x) => x.H.Service.DispatchClientAsync(x.S,
        new PacketWriter(Msg.CS_ACTITEMSUSE_REQ).WriteUInt32(1).WriteByte(1).WriteByte(1).WriteUInt16(0)
            .WriteUInt16(SkillId).WriteByte(1).WriteUInt32(0).WriteUInt32(0).WriteFloat(100).WriteFloat(0).WriteFloat(100)
            .WriteUInt32(x.Mon.Id).WriteByte(2).ToArray());

    private static SkillUseResult Result(Context x, bool loop = false) =>
        (SkillUseResult)new PacketReader(x.C.Last(loop ? Msg.CS_LOOPSKILL_ACK : Msg.CS_SKILLUSE_ACK)!).ReadByte();

    private static void Untouched(Context x)
    {
        Assert.Equal((150u, 100u), (x.Ch.Hp, x.Ch.Mp));
        Assert.True(x.Skill.CanUse(x.H.Service.NowMs));
        Assert.False(x.C.Has(Msg.CS_UPDATEITEM_ACK)); Assert.False(x.C.Has(Msg.CS_DELITEM_ACK));
        Assert.False(x.C.Has(Msg.CS_MOVEITEM_ACK));
    }

    [Theory]
    [InlineData(0)] // no weapon
    [InlineData(1)] // weapon in backpack
    [InlineData(2)] // wrong kind
    [InlineData(3)] // weapon outside the three weapon slots
    [InlineData(4)] // broken weapon
    [InlineData(5)] // kind 38 must not wrap onto kind 6 in a 32-bit mask
    [InlineData(6)] // an empty stack is not equipped gear
    public async Task InvalidWeaponsRejectTheCastWithoutCostsOrAuthorizedDamage(int scenario)
    {
        var x = await Setup();
        var ammo = Give(x, Arrow, count: 10);
        switch (scenario)
        {
            case 1: Give(x, Bow, slot: 1); break;
            case 2: Give(x, Sword, Equip); break;
            case 3: Give(x, Bow, Equip, slot: 3); break;
            case 4: Give(x, Bow, Equip, slot: 2).DuraCur = 0; break;
            case 5: x.T.Items[Bow] = x.T.Items[Bow] with { Kind = 38 }; Give(x, Bow, Equip, slot: 2); break;
            case 6: Give(x, Bow, Equip, slot: 2, count: 0); break;
        }

        await Cast(x);

        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x));
        Untouched(x); Assert.Equal(10, ammo.Count);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.DefendReq(1, x.Mon.Id, skillId: SkillId));
        Assert.Equal(100u, x.Mon.Hp);
    }

    [Theory]
    [InlineData(Bow, Arrow)]
    [InlineData(Crossbow, Bolt)]
    public async Task AWorkingRangedWeaponConsumesItsOwnAmmoOnce_AtCastTime(ushort weapon, ushort ammunition)
    {
        var x = await Setup(); Give(x, weapon, Equip, slot: 2);
        var ammo = Give(x, ammunition, count: 10);

        await Cast(x);

        Assert.Equal(SkillUseResult.Success, Result(x));
        Assert.Equal(9, ammo.Count); Assert.Equal((145u, 90u), (x.Ch.Hp, x.Ch.Mp));
        Assert.True(x.C.Has(Msg.CS_UPDATEITEM_ACK));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.DefendReq(1, x.Mon.Id, skillId: SkillId));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.DefendReq(1, x.Mon.Id, skillId: SkillId));
        Assert.Equal(80u, x.Mon.Hp); Assert.Equal(9, ammo.Count);
    }

    [Fact]
    public async Task AWeaponlessSkillDoesNotSpendAmmoJustBecauseABowIsEquipped()
    {
        var x = await Setup(Template(weapon: 0)); Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 10);
        await Cast(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Equal(10, ammo.Count);
        Assert.False(x.C.Has(Msg.CS_UPDATEITEM_ACK));
    }

    [Fact]
    public async Task IndestructibleWeaponsWorkWithZeroCurrentDurability()
    {
        var x = await Setup(); var bow = Give(x, Bow, Equip, slot: 2); bow.DuraMax = bow.DuraCur = 0;
        Give(x, Arrow, count: 1);
        await Cast(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Empty(x.Ch.FindInven(Backpack)!.Items);
        Assert.True(x.C.Has(Msg.CS_DELITEM_ACK));
    }

    [Fact]
    public async Task AUsableAlternativeCanSatisfyTheWeaponMaskWhenTheFirstWeaponIsBroken()
    {
        var x = await Setup(Template(weapon: 1u | Ranged));
        Give(x, Sword, Equip).DuraCur = 0; Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 2);
        await Cast(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Equal(1, ammo.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongAmmoDoesNotPayResources(bool wrongKind)
    {
        var x = await Setup(); Give(x, Bow, Equip, slot: 2);
        if (wrongKind) Give(x, Bolt, count: 10);
        await Cast(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x)); Untouched(x);
    }

    [Fact]
    public async Task SeveralStacksInOneBagSupplyTheShot_AndQueueTheCorrectSaves()
    {
        var x = await Setup(Template(missiles: 3)); Give(x, Bow, Equip, slot: 2, dlId: 10);
        x.Ch.DbLoaded = true; x.H.Service.ApplyItemIdSeed(10000);
        var tail = Give(x, Arrow, slot: 9, count: 3, dlId: 12);
        var head = Give(x, Arrow, slot: 5, count: 2, dlId: 11);

        await Cast(x);

        Assert.Equal(SkillUseResult.Success, Result(x));
        Assert.DoesNotContain(head, x.Ch.FindInven(Backpack)!.Items); Assert.Equal(2, tail.Count);
        Assert.Contains(11L, x.H.Service.PendingItemDeletes);
        Assert.Equal((byte)2, x.H.Service.PendingItemUpserts[12].data.Count);
        Assert.Single(x.C.WithId(Msg.CS_DELITEM_ACK)); Assert.Single(x.C.WithId(Msg.CS_UPDATEITEM_ACK));
        Assert.Single(x.C.WithId(Msg.CS_MOVEITEM_ACK));
        var (_, saved) = x.H.Service.BuildInvenSaves(x.Ch);
        Assert.DoesNotContain(saved, i => i.DlId == 11);
        Assert.Equal((byte)2, Assert.Single(saved, i => i.DlId == 12).Count);
    }

    [Fact]
    public async Task AnInsufficientBagIsUntouched_AndTheNextSufficientBagIsUsed()
    {
        var x = await Setup(Template(missiles: 3)); Give(x, Bow, Equip, slot: 2);
        var first = Give(x, Arrow, bag: 0, count: 2);
        var last = Give(x, Arrow, count: 2);
        await Cast(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x));
        Assert.Equal((2, 2), ((int)first.Count, last.Count)); Untouched(x);

        Give(x, Arrow, slot: 1, count: 1);
        await Cast(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Equal(2, first.Count);
        Assert.Empty(x.Ch.FindInven(Backpack)!.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiAttacksSpendTheServerEmittedMissileCount_NotBystanders(bool loop)
    {
        var x = await Setup(Template(missiles: 4)); Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 10);
        if (loop) await Loop(x, (x.Mon.Id, 2, true), (999, 2, false));
        else
        {
            var request = MapTestHarness.SkillUseReq(1, SkillId, targets: new[] { (x.Mon.Id, (byte)2), (999u, (byte)2) });
            request[^1] = 0; // final entry is a bystander, not an emitted target
            await x.H.Service.DispatchClientAsync(x.S, request);
        }
        Assert.Equal(SkillUseResult.Success, Result(x, loop)); Assert.Equal(6, ammo.Count);
    }

    [Fact]
    public async Task AnEmptyTargetListStillRequiresTheWeaponsBaseAmmo()
    {
        var x = await Setup(); Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 10);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.SkillUseReq(1, SkillId));
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Equal(9, ammo.Count);
    }

    [Fact]
    public async Task AmmoCountsDoNotWrapAt255()
    {
        var x = await Setup(Template(missiles: 16)); x.T.Items[Bow] = x.T.Items[Bow] with { UseItemCount = 250 };
        Give(x, Bow, Equip, slot: 2); Give(x, Arrow, count: 255); Give(x, Arrow, slot: 1, count: 10);
        await Cast(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Empty(x.Ch.FindInven(Backpack)!.Items); // 250 + 15 = 265
    }

    [Fact]
    public async Task ARequiredSkillItemIsSpentOnceWithoutAWeaponRequirement()
    {
        var x = await Setup(Template(weapon: 0, item: Reagent)); var reagent = Give(x, Reagent, count: 2);
        await Cast(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Equal(1, reagent.Count);
    }

    [Fact]
    public async Task ARequiredItemMissingFromTheInventoryDoesNotConsumeAvailableAmmo()
    {
        var x = await Setup(Template(item: Reagent)); Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 10);
        await Cast(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x)); Assert.Equal(10, ammo.Count); Untouched(x);
    }

    [Fact]
    public async Task AFailedAmmoCheckDoesNotConsumeTheSkillsRequiredItem()
    {
        var x = await Setup(Template(item: Reagent)); Give(x, Bow, Equip, slot: 2); var reagent = Give(x, Reagent, count: 2);
        await Cast(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x)); Assert.Equal(2, reagent.Count); Untouched(x);
        Give(x, Arrow, slot: 1, count: 1);
        await Cast(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Equal(1, reagent.Count);
        Assert.DoesNotContain(x.Ch.FindInven(Backpack)!.Items, i => i.TemplateId == Arrow);
    }

    [Fact]
    public async Task AStackUsedAsBothReagentAndAmmoCannotBeCountedTwice()
    {
        var x = await Setup(Template(item: Arrow)); Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 1);
        await Cast(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x)); Assert.Equal(1, ammo.Count); Untouched(x);
        ammo.Count = 2;
        await Cast(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Empty(x.Ch.FindInven(Backpack)!.Items);
        Assert.Single(x.C.WithId(Msg.CS_DELITEM_ACK));
    }

    [Fact]
    public async Task EachLoopRechecksTheWeaponAndAmmo_AndARejectedLoopDoesNotArmCooldown()
    {
        var x = await Setup(); var bow = Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 3);
        await Cast(x); Assert.Equal(2, ammo.Count);
        x.H.Service.NowMs += 10000;
        x.Ch.FindInven(Equip)!.Items.Remove(bow);
        await Loop(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x, true)); Assert.Equal(2, ammo.Count);
        Assert.Equal((145u, 90u), (x.Ch.Hp, x.Ch.Mp));
        x.Ch.FindInven(Equip)!.Items.Add(bow);
        await Loop(x);
        Assert.Equal(SkillUseResult.Success, Result(x, true)); Assert.Equal(1, ammo.Count);
        await Loop(x);
        Assert.Equal(SkillUseResult.SpeedyUse, Result(x, true)); Assert.Equal(1, ammo.Count);
    }

    [Theory]
    [InlineData(0)] // MP
    [InlineData(1)] // HP
    [InlineData(2)] // cooldown
    public async Task EarlierRejectionGatesLeaveAllAmmoUntouched(int reason)
    {
        var x = await Setup(); Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 10);
        if (reason == 0) x.Ch.Mp = 0;
        if (reason == 1) x.Ch.Hp = 5;
        if (reason == 2) x.Skill.SetCooldown(x.H.Service.NowMs, 5000);
        var before = (x.Ch.Hp, x.Ch.Mp);
        await Cast(x);
        Assert.Equal(reason == 0 ? SkillUseResult.NeedMp : reason == 1 ? SkillUseResult.NeedHp : SkillUseResult.SpeedyUse, Result(x));
        Assert.Equal(10, ammo.Count); Assert.Equal(before, (x.Ch.Hp, x.Ch.Mp));
        Assert.False(x.C.Has(Msg.CS_UPDATEITEM_ACK));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StoreAndTradeLocksBlockCastsAndLoops(bool store, bool loop)
    {
        var x = await Setup(); Give(x, Bow, Equip, slot: 2); var ammo = Give(x, Arrow, count: 10);
        if (store) x.S.Store.IsOpen = true; else x.S.Deal.SetTarget("Partner");
        if (loop) await Loop(x); else await Cast(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x, loop)); Untouched(x); Assert.Equal(10, ammo.Count);
    }

    [Fact]
    public async Task ActItemsUseTheSameWeaponAndAmmoChecks_WithoutSpendingTheirItemTwice()
    {
        var x = await Setup(Template(item: Reagent)); Give(x, Bow, Equip, slot: 2); var reagent = Give(x, Reagent, count: 3);
        await Act(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x)); Assert.Equal(3, reagent.Count);
        Assert.Empty(x.Ch.ItemCoolTime); Assert.Equal((byte)0, x.Ch.Mode); Untouched(x);

        var ammo = Give(x, Arrow, slot: 1, count: 2);
        await Act(x);
        Assert.Equal(SkillUseResult.Success, Result(x)); Assert.Equal(2, reagent.Count); Assert.Equal(1, ammo.Count);
        Assert.Equal((150u, 100u), (x.Ch.Hp, x.Ch.Mp)); // act items use their item cooldown, not learned-skill MP/HP
        Assert.Equal(15000u, x.Ch.ItemCoolTime[77]);
        await Act(x);
        Assert.Equal(SkillUseResult.SpeedyUse, Result(x)); Assert.Equal(2, reagent.Count); Assert.Equal(1, ammo.Count);
    }

    [Theory]
    [InlineData(25020)]
    [InlineData(25021)]
    [InlineData(25022)]
    public async Task UnverifiedLegacyCashItemIdsDoNotBypassAWeaponOrAmmoRequirement(ushort id)
    {
        var x = await Setup(); x.T.Items[id] = new(id, 0, new float[4]);
        Give(x, Bow, Equip, slot: 2); var card = Give(x, id);
        await Cast(x);
        Assert.Equal(SkillUseResult.UnsuitWeapon, Result(x)); Assert.Equal(1, card.Count); Untouched(x);
    }
}
