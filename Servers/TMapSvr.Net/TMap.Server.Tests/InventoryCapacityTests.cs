using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class InventoryCapacityTests
{
    private const ushort Herb = 9656, Other = 200, Bag = 3001, Plant = 500;
    private const uint Mission = 8003, Pickup = 8005;
    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        t.Items[3] = new(3, 0, new float[4], Type: 11, SlotCount: 16);
        t.Items[2] = new(2, 0, new float[4], SlotCount: 19);
        t.Items[Bag] = new(Bag, 0, new float[4], Type: 11, SlotCount: 16);
        t.Items[Herb] = new(Herb, 0, new float[4], Type: 41);
        t.Items[Other] = new(Other, 0, new float[4]);
        t.Npcs[Plant] = new(Plant, 7, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var mission = new QuestTemplate { QuestId = Mission, Type = (byte)QuestType.Mission, TriggerType = 3, TriggerId = Plant, CountMax = 1 };
        mission.Terms.Add(new(Herb, 2, 1)); t.Quests[Mission] = mission;
        var pickup = new QuestTemplate { QuestId = Pickup, ParentId = Mission, Type = (byte)QuestType.Routing, TriggerType = 3, TriggerId = Plant };
        pickup.Terms.Add(new(Herb, 5, 1)); pickup.Conditions.Add(new(Mission, 3, 0));
        pickup.Conditions.Add(new(Herb, 13, 1)); t.Quests[Pickup] = pickup;
        return t;
    }

    [Theory]
    [InlineData(255, 0, 16)]
    [InlineData(255, 3, 16)]
    [InlineData(254, 0, 19)]
    [InlineData(0, Bag, 16)]
    public void ContainerCapacity_UsesItsChartIncludingNativeHeaderFallback(byte id, ushort item, byte expected)
    {
        var bag = Inven.FromTemplate(id, item, Store(), 123, 2);
        Assert.Equal(expected, bag.SlotCount); Assert.Equal(123, bag.EndTime); Assert.Equal(2, bag.Eld);
        Assert.False(bag.ContainsSlot(expected)); Assert.True(bag.ContainsSlot((byte)(expected - 1)));
    }

    [Fact]
    public void UnknownContainer_WithLoadedChartsCannotAllocateInvisibleSlots()
    {
        Assert.Equal(0, Inven.FromTemplate(10, 0, Store()).SlotCount);
        Assert.Equal(0, Inven.FromTemplate(0, 999, Store()).SlotCount);
        Assert.Equal(Inven.DefaultSlotCount, Inven.FromTemplate(255, 0, new()).SlotCount);
    }

    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C)> Enter(bool extra)
    {
        var t = Store(); var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Country = 0, AidCountry = 3, MaxHp = 100, Hp = 100 };
        var bag = Inven.FromTemplate(255, 3, t);
        for (byte i = 0; i < 16; i++) bag.Items.Add(new() { ItemSlot = i, TemplateId = Other, Template = t.Items[Other], Count = 1 });
        ch.Invens.Add(bag);
        if (extra) ch.Invens.Add(Inven.FromTemplate(0, Bag, t));
        var (s, c) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        h.Service.InitNpcs(); h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(Mission)); c.Clear();
        return (h, s, c);
    }

    [Fact]
    public async Task HerbPickup_FullDefaultBagUsesVisibleExtraBagSlot()
    {
        var (h, s, c) = await Enter(true);
        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(Plant, Herb, 1, questId: Pickup));
        var added = new PacketReader(c.Last(Msg.CS_ADDITEM_ACK)!);
        Assert.Equal(0, added.ReadByte()); Assert.Equal(0, added.ReadByte()); Assert.Equal(Herb, added.ReadUInt16());
        Assert.Equal(Herb, Assert.Single(s.Char!.FindInven(0)!.Items).TemplateId);
        Assert.Equal(16, s.Char.FindInven(255)!.Items.Count);
        Assert.True(c.Has(Msg.CS_QUESTUPDATE_ACK)); Assert.Equal(0, s.Char.MoneyTotal);
    }

    [Fact]
    public async Task HerbPickup_AllVisibleSlotsFullRejectsWithoutGrantOrQuestCredit()
    {
        var (h, s, c) = await Enter(false);
        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(Plant, Herb, 1, questId: Pickup));
        Assert.Equal((byte)ItemBuyResult.CantPush, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.False(c.Has(Msg.CS_ADDITEM_ACK)); Assert.False(c.Has(Msg.CS_QUESTUPDATE_ACK));
        Assert.Equal(16, s.Char!.FindInven(255)!.Items.Count);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(255)]
    public async Task ExplicitMove_CannotCreateAnInvisibleSlot(byte slot)
    {
        var (h, s, c) = await Enter(true);
        await h.Service.DispatchClientAsync(s, MapTestHarness.MoveItemReq(255, 0, 0, slot, 1));
        Assert.Equal((byte)MoveItemResult.InvenFull, new PacketReader(c.Last(Msg.CS_MOVEITEM_ACK)!).ReadByte());
        Assert.Empty(s.Char!.FindInven(0)!.Items); Assert.Equal(16, s.Char.FindInven(255)!.Items.Count);
    }
}
