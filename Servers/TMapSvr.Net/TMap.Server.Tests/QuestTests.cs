using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Phase 24 — the quest engine: accept a mission (CS_QUESTEXEC → CS_QUESTADD_ACK), advance its
/// objectives as the universal CheckQuest hook fires on get-item / kill / talk (CS_QUESTUPDATE_ACK), turn it
/// in via a QT_COMPLETE quest (CS_QUESTCOMPLETE_ACK + reward grant), and drop it (QR_DROP). Eligibility gates
/// (level / parent), fetch-item hand-over, and the possible-quest list are covered too.</summary>
public class QuestTests
{
    // trigger / term / reward / condition / type bytes (mirror the NetCode enums)
    private const byte TtTalkNpc = 3, TtKillMon = 5;
    private const byte QttCompQuest = 1, QttGetItem = 2, QttHunt = 3, QttItemId = 5, QttTalk = 13;
    private const byte RtGold = 1, RtItem = 2, RtExp = 6;
    private const byte RmDefault = 1;
    private const byte QctUpperLevel = 1;
    private const byte Mission = (byte)QuestType.Mission, Complete = (byte)QuestType.Complete;

    private const ushort CollectItem = 200;   // the fetch/collect item (also a shop item)
    private const ushort FetchItem = 201;      // handed over on accept
    private const ushort RewardItem = 300;     // an item reward
    private const ushort MonChart = 700;       // the hunt-target monster chart id
    private const ushort Giver = 500, Turnin = 501;

    private static QuestTemplate Q(uint id, byte type, uint trigId, uint parent = 0,
        (uint id, byte type, byte count)[]? terms = null,
        (uint id, byte type, byte method, byte data, byte count)[]? rewards = null,
        (uint id, byte type, byte count)[]? conds = null)
    {
        var q = new QuestTemplate { QuestId = id, Type = type, TriggerType = TtTalkNpc, TriggerId = trigId, ParentId = parent };
        if (terms is not null) foreach (var t in terms) q.Terms.Add(new QuestTerm(t.id, t.type, t.count));
        if (rewards is not null) foreach (var r in rewards) q.Rewards.Add(new QuestReward(r.id, r.type, r.method, r.data, r.count));
        if (conds is not null) foreach (var c in conds) q.Conditions.Add(new QuestCondition(c.id, c.type, c.count));
        return q;
    }

    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        t.Items[CollectItem] = new ItemTemplate(CollectItem, 0, new[] { 0f, 0f, 0f, 0f }, DefaultLevel: 5, Stack: 10, Price: 2f, IsSell: 2);
        t.Items[FetchItem] = new ItemTemplate(FetchItem, 0, new[] { 0f, 0f, 0f, 0f }, DefaultLevel: 5, Stack: 10);
        t.Items[RewardItem] = new ItemTemplate(RewardItem, 0, new[] { 0f, 0f, 0f, 0f }, DefaultLevel: 5, Stack: 10, ClassId: 0xFFFFFFFF);
        t.LevelMoney[5] = 100;
        return t;
    }

    private static Character Hero()
    {
        var ch = new Character { CharId = 1, Name = "Hero", Class = 0, Country = 1, Level = 5, MaxHp = 100, Hp = 100 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        return ch;
    }

    // Enters the world (map 0), registers the giver + turn-in NPCs, builds the quest trigger index.
    private async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c)> Enter(TemplateStore store, Character? ch = null)
    {
        var h = new MapTestHarness(store);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch ?? Hero());
        s.Char!.Country = 1; s.Char.Class = 0; s.Char.Level = 5; s.Char.MapId = 0;
        h.Service.AddNpc(new Npc { Id = Giver, Type = 2, Country = 3, MapId = 0 });
        h.Service.AddNpc(new Npc { Id = Turnin, Type = 2, Country = 3, MapId = 0 });
        h.Service.InitQuests();
        c.Clear();
        return (h, s, c);
    }

    [Fact]
    public async Task Craft_UsesInventoryWithoutAcceptingMission_AndCanRepeat()
    {
        var store = Store();
        store.Quests[6000] = Q(6000, (byte)QuestType.Craft, Giver,
            terms: new[] { ((uint)CollectItem, QttGetItem, (byte)2) },
            rewards: new[] { ((uint)RewardItem, RtItem, RmDefault, (byte)0, (byte)1) });
        var ch = Hero();
        ch.FindInven(0xFF)!.Items.Add(new Item { ItemSlot = 0, TemplateId = CollectItem, Count = 4, Template = store.Item(CollectItem) });
        var (h, s, c) = await Enter(store, ch);

        for (int i = 0; i < 2; i++)
        {
            await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(6000));
            var ack = new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!);
            Assert.Equal((byte)QuestResult.Success, ack.ReadByte());
            Assert.Equal(6000u, ack.ReadUInt32());
        }

        Assert.Equal(2, ch.FindInven(0xFF)!.Items.Where(i => i.TemplateId == RewardItem).Sum(i => i.Count));
        Assert.DoesNotContain(ch.FindInven(0xFF)!.Items, i => i.TemplateId == CollectItem);
        Assert.Null(ch.FindQuest(6000));
        Assert.False(c.Has(Msg.CS_QUESTADD_ACK));
    }

    [Fact]
    public async Task Craft_MissingIngredients_ReportsUnmetTermWithoutSpendingItems()
    {
        var store = Store();
        store.Quests[6000] = Q(6000, (byte)QuestType.Craft, Giver,
            terms: new[] { ((uint)CollectItem, QttGetItem, (byte)2) },
            rewards: new[] { ((uint)RewardItem, RtItem, RmDefault, (byte)0, (byte)1) });
        var ch = Hero();
        ch.FindInven(0xFF)!.Items.Add(new Item { ItemSlot = 0, TemplateId = CollectItem, Count = 1, Template = store.Item(CollectItem) });
        var (h, s, c) = await Enter(store, ch);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(6000));

        var ack = new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!);
        Assert.Equal((byte)QuestResult.Term, ack.ReadByte());
        Assert.Equal(6000u, ack.ReadUInt32());
        Assert.Equal((uint)CollectItem, ack.ReadUInt32());
        Assert.Equal(1, ch.FindInven(0xFF)!.Items.Single().Count);
        Assert.Null(ch.FindQuest(6000));
    }

    [Fact]
    public async Task Craft_ClassCondition_PreventsConsumptionAndReward()
    {
        var store = Store();
        store.Quests[6000] = Q(6000, (byte)QuestType.Craft, Giver,
            terms: new[] { ((uint)CollectItem, QttGetItem, (byte)1) },
            rewards: new[] { ((uint)RewardItem, RtItem, RmDefault, (byte)0, (byte)1) },
            conds: new[] { (2u, (byte)5, (byte)0) }); // class 1 only; the hero is class 0
        var ch = Hero();
        ch.FindInven(0xFF)!.Items.Add(new Item { ItemSlot = 0, TemplateId = CollectItem, Count = 1, Template = store.Item(CollectItem) });
        var (h, s, c) = await Enter(store, ch);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(6000));

        Assert.False(c.Has(Msg.CS_QUESTCOMPLETE_ACK));
        Assert.Equal(CollectItem, ch.FindInven(0xFF)!.Items.Single().TemplateId);
    }

    [Fact]
    public async Task Craft_FullInventory_LeavesIngredientsIntact()
    {
        var store = Store();
        store.Quests[6000] = Q(6000, (byte)QuestType.Craft, Giver,
            terms: new[] { ((uint)CollectItem, QttGetItem, (byte)1) },
            rewards: new[] { ((uint)RewardItem, RtItem, RmDefault, (byte)0, (byte)1) });
        var ch = Hero();
        ch.FindInven(0xFF)!.SlotCount = 1;
        ch.FindInven(0xFF)!.Items.Add(new Item { ItemSlot = 0, TemplateId = CollectItem, Count = 2, Template = store.Item(CollectItem) });
        var (h, s, c) = await Enter(store, ch);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(6000));

        var ack = new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!);
        Assert.Equal((byte)QuestResult.InventoryFull, ack.ReadByte());
        Assert.Equal(2, ch.FindInven(0xFF)!.Items.Single().Count);
        Assert.Null(ch.FindQuest(6000));
    }

    [Fact]
    public async Task Craft_ForeignCountryNpc_RejectsDirectQuestRequest()
    {
        var store = Store();
        store.Quests[6000] = Q(6000, (byte)QuestType.Craft, Giver,
            rewards: new[] { ((uint)RewardItem, RtItem, RmDefault, (byte)0, (byte)1) });
        var (h, s, c) = await Enter(store);
        h.Service.AddNpc(new Npc { Id = Giver, Type = 20, Country = 0, MapId = 0 });

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(6000));

        Assert.False(c.Has(Msg.CS_QUESTCOMPLETE_ACK));
        Assert.DoesNotContain(s.Char!.FindInven(0xFF)!.Items, i => i.TemplateId == RewardItem);
    }

    [Fact]
    public async Task Exec_Mission_AddsQuest_AndAnnounces()
    {
        var store = Store();
        store.Quests[1000] = Q(1000, Mission, Giver, terms: new[] { ((uint)CollectItem, QttGetItem, (byte)2) });
        var (h, s, c) = await Enter(store);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1000));

        var r = new PacketReader(c.Last(Msg.CS_QUESTADD_ACK)!);
        Assert.Equal(1000u, r.ReadUInt32());
        Assert.Equal(Mission, r.ReadByte());
        Assert.True(s.Char!.IsRunningQuest(1000));
    }

    [Fact]
    public async Task Exec_Mission_LevelGated_NotAccepted()
    {
        var store = Store();
        store.Quests[4000] = Q(4000, Mission, Giver, terms: new[] { ((uint)CollectItem, QttGetItem, (byte)1) },
            conds: new[] { (10u, QctUpperLevel, (byte)0) });   // needs level 10, hero is 5
        var (h, s, c) = await Enter(store);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(4000));

        Assert.False(s.Char!.IsRunningQuest(4000));
        Assert.False(c.Has(Msg.CS_QUESTADD_ACK));
    }

    [Fact]
    public async Task GetItem_Objective_AdvancesOnBuy()
    {
        var store = Store();
        store.Quests[1000] = Q(1000, Mission, Giver, terms: new[] { ((uint)CollectItem, QttGetItem, (byte)2) });
        var (h, s, c) = await Enter(store);
        s.Char!.SetMoneyTotal(1000);
        var npc = new Npc { Id = Giver, Type = 2, Country = 3, MapId = 0 };
        npc.Items[CollectItem] = store.Item(CollectItem)!;
        h.Service.AddNpc(npc);                                   // give NPC 500 shop stock
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1000));
        c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(Giver, CollectItem, count: 1));

        var r = new PacketReader(c.Last(Msg.CS_QUESTUPDATE_ACK)!);
        Assert.Equal(1000u, r.ReadUInt32());               // dwQuestID
        Assert.Equal((uint)CollectItem, r.ReadUInt32());   // dwTermID
        Assert.Equal(QttGetItem, r.ReadByte());            // bType
        Assert.Equal((byte)1, r.ReadByte());               // bCount (now 1 in bags)
        Assert.Equal((byte)QuestTermStatus.Run, r.ReadByte()); // still short of 2
    }

    [Fact]
    public async Task Complete_AllTermsMet_GrantsReward_ConsumesItems()
    {
        var store = Store();
        store.Quests[1000] = Q(1000, Mission, Giver, terms: new[] { ((uint)CollectItem, QttGetItem, (byte)2) },
            rewards: new[] { (100u, RtExp, RmDefault, (byte)0, (byte)0), (50u, RtGold, RmDefault, (byte)0, (byte)0),
                             ((uint)RewardItem, RtItem, RmDefault, (byte)0, (byte)1) });
        store.Quests[1001] = Q(1001, Complete, Turnin, parent: 1000, terms: new[] { (1000u, QttCompQuest, (byte)0) });

        var ch = Hero();
        ch.FindInven(0xFF)!.Items.Add(new Item { ItemSlot = 0, TemplateId = CollectItem, Count = 2, Template = store.Item(CollectItem) });
        var (h, s, c) = await Enter(store, ch);
        s.Char!.SetMoneyTotal(0); s.Char.Exp = 0;
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1000)); // accept the mission
        c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1001)); // turn in at NPC 501

        Assert.Equal((byte)QuestResult.Success, new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(1, s.Char!.FindQuest(1000)!.CompleteCount);
        Assert.False(s.Char.IsRunningQuest(1000));                    // trigger 1 == complete 1
        Assert.Equal(100u, s.Char.Exp);                               // +100 exp (no level chart ⇒ no level-up)
        Assert.Equal(50L, s.Char.MoneyTotal);                         // +50 gold
        var bag = s.Char.FindInven(0xFF)!;
        Assert.Contains(bag.Items, i => i.TemplateId == RewardItem);  // item reward granted
        Assert.DoesNotContain(bag.Items, i => i.TemplateId == CollectItem); // fetch items consumed
    }

    [Fact]
    public async Task Complete_TermUnmet_ReportsTerm()
    {
        var store = Store();
        store.Quests[1000] = Q(1000, Mission, Giver, terms: new[] { ((uint)CollectItem, QttGetItem, (byte)2) });
        store.Quests[1001] = Q(1001, Complete, Turnin, parent: 1000, terms: new[] { (1000u, QttCompQuest, (byte)0) });
        var (h, s, c) = await Enter(store);
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1000)); // accept, but collect nothing
        c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1001));

        var r = new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!);
        Assert.Equal((byte)QuestResult.Term, r.ReadByte());
        Assert.Equal(1000u, r.ReadUInt32());                 // dwQuestID
        Assert.Equal((uint)CollectItem, r.ReadUInt32());     // the unmet term id
        Assert.Equal(0, s.Char!.FindQuest(1000)!.CompleteCount);
    }

    [Fact]
    public async Task Drop_RemovesActiveTrigger()
    {
        var store = Store();
        store.Quests[1000] = Q(1000, Mission, Giver, terms: new[] { ((uint)CollectItem, QttGetItem, (byte)2) });
        var (h, s, c) = await Enter(store);
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1000));
        Assert.True(s.Char!.IsRunningQuest(1000));
        c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestDropReq(1000));

        Assert.Equal((byte)QuestResult.Drop, new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.False(s.Char.IsRunningQuest(1000));    // trigger count dropped back below complete
    }

    [Fact]
    public async Task Kill_Hunt_Objective_Advances()
    {
        var store = Store();
        store.Quests[2000] = Q(2000, Mission, Giver, terms: new[] { ((uint)MonChart, QttHunt, (byte)1) });
        var (h, s, c) = await Enter(store);
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(2000));
        h.Service.CombatRng = new Random(1);
        h.Service.LootRng = new Random(1);
        var mob = new Monster { Id = 0x70001, ChartId = MonChart, Level = 5, MaxHp = 10, Hp = 10,
            DefendPower = 100, PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0 };
        h.Service.SpawnMonster(mob);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mob.Id));
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mob.Id)); // kills it → hunt advances

        var r = new PacketReader(c.Last(Msg.CS_QUESTUPDATE_ACK)!);
        Assert.Equal(2000u, r.ReadUInt32());
        Assert.Equal((uint)MonChart, r.ReadUInt32());
        Assert.Equal(QttHunt, r.ReadByte());
        Assert.Equal((byte)1, r.ReadByte());                       // 1 killed
        Assert.Equal((byte)QuestTermStatus.Success, r.ReadByte()); // needed 1 ⇒ done
    }

    [Fact]
    public async Task Talk_Objective_Advances_AndEchoesQuestId()
    {
        var store = Store();
        store.Quests[3000] = Q(3000, Mission, Giver, terms: new[] { ((uint)Turnin, QttTalk, (byte)1) });
        var (h, s, c) = await Enter(store);
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(3000));
        c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.NpcTalkReq(Turnin)); // talk to NPC 501

        Assert.Equal(3000u, new PacketReader(c.Last(Msg.CS_NPCTALK_ACK)!).ReadUInt32());  // questId echoed
        var u = new PacketReader(c.Last(Msg.CS_QUESTUPDATE_ACK)!);
        Assert.Equal(3000u, u.ReadUInt32());
        Assert.Equal((uint)Turnin, u.ReadUInt32());
        Assert.Equal(QttTalk, u.ReadByte());
        Assert.Equal((byte)1, u.ReadByte());
        Assert.Equal((byte)QuestTermStatus.Success, u.ReadByte());
    }

    [Fact]
    public async Task NpcDialogue_QuestExecution_EchoesTriggerNpcForClientTargetMatching()
    {
        var store = Store();
        store.Quests[3100] = Q(3100, (byte)QuestType.NpcTalk, Giver);
        var (h, s, c) = await Enter(store);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(3100));

        var response = new PacketReader(c.Last(Msg.CS_NPCTALK_ACK)!);
        Assert.Equal(3100u, response.ReadUInt32());
        Assert.Equal(Giver, response.ReadUInt16());
    }

    [Fact]
    public async Task NpcDialogue_UnmetConditions_ReturnsDefaultTalkForTheSameNpc()
    {
        var store = Store();
        store.Quests[3100] = Q(3100, (byte)QuestType.NpcTalk, Giver,
            conds: new[] { (10u, QctUpperLevel, (byte)0) });
        var (h, s, c) = await Enter(store);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(3100));

        var response = new PacketReader(c.Last(Msg.CS_NPCTALK_ACK)!);
        Assert.Equal(0u, response.ReadUInt32());
        Assert.Equal(Giver, response.ReadUInt16());
    }

    [Fact]
    public async Task NpcDialogue_QuestExecutionOnAnotherMap_DoesNotOpenDialogue()
    {
        var store = Store();
        store.Quests[3100] = Q(3100, (byte)QuestType.NpcTalk, Giver);
        var (h, s, c) = await Enter(store);
        s.Char!.MapId = 1;

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(3100));

        Assert.False(c.Has(Msg.CS_NPCTALK_ACK));
    }

    [Fact]
    public async Task Mission_GivesFetchItem_OnAccept()
    {
        var store = Store();
        store.Quests[5000] = Q(5000, Mission, Giver,
            terms: new[] { ((uint)FetchItem, QttGetItem, (byte)1), ((uint)FetchItem, QttItemId, (byte)1) });
        var (h, s, c) = await Enter(store);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(5000));

        Assert.Contains(s.Char!.FindInven(0xFF)!.Items, i => i.TemplateId == FetchItem);
        Assert.True(c.Has(Msg.CS_QUESTADD_ACK));
    }

    [Fact]
    public async Task ListPossible_ListsRunnableQuestForNpc()
    {
        var store = Store();
        store.Quests[1000] = Q(1000, Mission, Giver, terms: new[] { ((uint)CollectItem, QttGetItem, (byte)2) });
        var (h, s, c) = await Enter(store);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestListPossibleReq(Giver));

        var r = new PacketReader(c.Last(Msg.CS_QUESTLIST_POSSIBLE_ACK)!);
        Assert.Equal((byte)1, r.ReadByte());        // one NPC
        Assert.Equal(Giver, r.ReadUInt16());        // wNpcID
        r.ReadByte();                                // bCountry
        Assert.Equal((byte)1, r.ReadByte());        // one runnable quest
        Assert.Equal(1000u, r.ReadUInt32());        // dwQuestID
    }

    [Fact]
    public async Task Complete_AlreadyDone_NotRunnable_NoReward()
    {
        var store = Store();
        store.Quests[1000] = Q(1000, Mission, Giver,  // no terms ⇒ completes on turn-in
            rewards: new[] { (50u, RtGold, RmDefault, (byte)0, (byte)0) });
        store.Quests[1001] = Q(1001, Complete, Turnin, parent: 1000, terms: new[] { (1000u, QttCompQuest, (byte)0) });
        var (h, s, c) = await Enter(store);
        s.Char!.SetMoneyTotal(0);
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1000));
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1001)); // first turn-in succeeds
        Assert.Equal(50L, s.Char.MoneyTotal);
        c.Clear();

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(1001)); // second turn-in — quest no longer running

        Assert.Equal(50L, s.Char.MoneyTotal);       // no second gold grant
        Assert.False(c.Has(Msg.CS_QUESTCOMPLETE_ACK));
    }
}
