using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class QuestPickupTests
{
    private const ushort Herb = 200, OtherItem = 201, Plant = 500, Giver = 501;
    private const uint Mission = 4000, Pickup = 4001, Complete = 4002;

    private static TemplateStore Store()
    {
        var store = new TemplateStore();
        store.Items[Herb] = new ItemTemplate(Herb, 0, new[] { 0f, 0f, 0f, 0f }, Stack: 1);
        store.Items[OtherItem] = new ItemTemplate(OtherItem, 0, new[] { 0f, 0f, 0f, 0f }, Stack: 1);
        var mission = new QuestTemplate { QuestId = Mission, Type = (byte)QuestType.Mission, TriggerType = 3, TriggerId = Giver };
        mission.Terms.Add(new QuestTerm(Herb, 2, 1));
        mission.Rewards.Add(new QuestReward(20, 1, 1, 0, 0));
        store.Quests[Mission] = mission;
        var pickup = new QuestTemplate { QuestId = Pickup, Type = (byte)QuestType.Routing, TriggerType = 3, TriggerId = Plant, ParentId = Mission };
        pickup.Terms.Add(new QuestTerm(Herb, 5, 1));
        pickup.Conditions.Add(new QuestCondition(Mission, 3, 0));
        pickup.Conditions.Add(new QuestCondition(Herb, 13, 1));
        store.Quests[Pickup] = pickup;
        var complete = new QuestTemplate { QuestId = Complete, Type = (byte)QuestType.Complete, TriggerType = 3, TriggerId = Giver, ParentId = Mission };
        complete.Terms.Add(new QuestTerm(Mission, 1, 0));
        complete.Conditions.Add(new QuestCondition(Mission, 3, 0));
        store.Quests[Complete] = complete;
        return store;
    }

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c)> Enter(TemplateStore store)
    {
        var h = new MapTestHarness(store);
        var hero = new Character { CharId = 1, Country = 1, MaxHp = 100, Hp = 100 };
        hero.Invens.Add(new Inven { InvenId = 0xFF });
        var (s, c) = await h.EnterAsync(1, 1, 1, preSeeded: hero);
        s.Char!.Country = 1; s.Char.AidCountry = 3; s.Char.MapId = 0; s.Char.SetMoneyTotal(0);
        h.Service.AddNpc(new Npc { Id = Plant, Type = 7, Country = 3, MapId = 0 }); // no shop stock
        h.Service.AddNpc(new Npc { Id = Giver, Type = 0, Country = 3, MapId = 0 });
        h.Service.InitQuests();
        c.Clear();
        return (h, s, c);
    }

    [Fact]
    public async Task Pickup_FromQuestObject_UpdatesMission_AndCanBeTurnedInOnce()
    {
        var (h, s, c) = await Enter(Store());
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(Mission));
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(Pickup));
        var list = new PacketReader(c.Last(Msg.CS_NPCITEMLIST_ACK)!);
        Assert.Equal(Plant, list.ReadUInt16()); Assert.Equal((byte)7, list.ReadByte());
        list.ReadByte(); Assert.Equal((byte)1, list.ReadByte()); Assert.Equal(Herb, list.ReadUInt16());

        c.Clear();
        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(Plant, Herb, 1, questId: Pickup));

        Assert.Equal((byte)ItemBuyResult.Success, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Equal(Herb, Assert.Single(s.Char!.FindInven(0xFF)!.Items).TemplateId);
        Assert.Equal(0L, s.Char.MoneyTotal);
        var update = new PacketReader(c.Last(Msg.CS_QUESTUPDATE_ACK)!);
        Assert.Equal(Mission, update.ReadUInt32()); Assert.Equal((uint)Herb, update.ReadUInt32());
        Assert.Equal((byte)2, update.ReadByte()); Assert.Equal((byte)1, update.ReadByte());
        Assert.Equal((byte)QuestTermStatus.Success, update.ReadByte());

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(Plant, Herb, 1, questId: Pickup));
        Assert.Equal((byte)ItemBuyResult.NotFound, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Single(s.Char.FindInven(0xFF)!.Items);
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(Complete));
        Assert.Equal((byte)QuestResult.Success, new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Empty(s.Char.FindInven(0xFF)!.Items);
        Assert.Equal(20L, s.Char.MoneyTotal);
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(Complete));
        Assert.Equal(20L, s.Char.MoneyTotal);
        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(Plant, Herb, 1, questId: Pickup));
        Assert.Equal((byte)ItemBuyResult.NotFound, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Empty(s.Char.FindInven(0xFF)!.Items);
    }

    [Theory]
    [InlineData("unknown-quest")]
    [InlineData("wrong-npc")]
    [InlineData("wrong-map")]
    [InlineData("wrong-item")]
    [InlineData("missing-mission")]
    [InlineData("wrong-quest-type")]
    [InlineData("wrong-term-type")]
    [InlineData("unmet-condition")]
    public async Task Pickup_InvalidRequest_DoesNotGrantItem(string scenario)
    {
        var store = Store();
        if (scenario == "wrong-term-type")
        {
            store.Quests[Pickup].Terms.Clear();
            store.Quests[Pickup].Terms.Add(new QuestTerm(Herb, 2, 1));
        }
        if (scenario == "unmet-condition")
            store.Quests[Pickup].Conditions.Add(new QuestCondition(100, 1, 0));
        var (h, s, c) = await Enter(store);
        if (scenario != "missing-mission") await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(Mission));
        if (scenario == "wrong-map") s.Char!.MapId = 1;
        uint quest = scenario == "unknown-quest" ? 99999u : scenario == "wrong-quest-type" ? Mission : Pickup;
        ushort npc = scenario == "wrong-npc" ? Giver : Plant;
        ushort item = scenario == "wrong-item" ? OtherItem : Herb;
        c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(npc, item, 1, questId: quest));

        Assert.Equal((byte)ItemBuyResult.NotFound, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Empty(s.Char!.FindInven(0xFF)!.Items);
        Assert.Equal(0L, s.Char.MoneyTotal);
        Assert.False(c.Has(Msg.CS_QUESTUPDATE_ACK));
    }

    [Fact]
    public async Task Pickup_NoInventory_CanRetryAfterMakingRoom()
    {
        var (h, s, c) = await Enter(Store());
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(Mission));
        s.Char!.Invens.Clear(); c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(Plant, Herb, 1, questId: Pickup));

        Assert.Equal((byte)ItemBuyResult.CantPush, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.False(c.Has(Msg.CS_QUESTUPDATE_ACK));
        Assert.True(s.Char.IsRunningQuest(Mission));
        s.Char.Invens.Add(new Inven { InvenId = 0xFF });
        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(Plant, Herb, 1, questId: Pickup));
        Assert.Equal((byte)ItemBuyResult.Success, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Single(s.Char.FindInven(0xFF)!.Items);
    }
}
