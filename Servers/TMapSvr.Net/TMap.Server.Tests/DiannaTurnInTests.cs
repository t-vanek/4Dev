using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

// Quest/NPC/objective IDs from live SQL; completion NPC 20012 from pinned TQNode.qpd (migration 049).
public sealed class DiannaTurnInTests
{
    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C)> Setup(bool killed = true, bool crystal = true)
    {
        var t = new TemplateStore();
        t.Items[9657] = new(9657, 0, new float[4], Stack: 1, ClassId: uint.MaxValue);
        t.Quests[8003] = new() { QuestId = 8003, Type = 8, CountMax = 1 };
        var mission = new QuestTemplate { QuestId = 8006, ParentId = 8003, Type = 8, TriggerType = 3, TriggerId = 20012, CountMax = 1 };
        mission.Terms.Add(new(290, 3, 1)); mission.Terms.Add(new(9657, 2, 1));
        mission.Rewards.Add(new(3522, 1, 1, 0, 0)); mission.Rewards.Add(new(4101, 6, 1, 0, 0));
        var complete = new QuestTemplate { QuestId = 8007, ParentId = 8006, Type = 7, TriggerType = 3, TriggerId = 20012 };
        complete.Terms.Add(new(8006, 1, 0)); complete.Conditions.Add(new(8006, 3, 0));
        t.Quests[8006] = mission; t.Quests[8007] = complete;
        var ch = new Character { CharId = 1, Class = 3, Country = 0, Level = 93, Hp = 100, MaxHp = 100 };
        var bag = new Inven { InvenId = 0, SlotCount = 16 }; ch.Invens.Add(bag);
        if (crystal) bag.Items.Add(new() { TemplateId = 9657, Template = t.Items[9657], Count = 1, ItemSlot = 15 });
        ch.Quests[8003] = new() { Template = t.Quests[8003], TriggerCount = 1, CompleteCount = 1 };
        var progress = new QuestProgress { Template = mission, TriggerCount = 1 };
        progress.RunningTerms.Add(new() { TermId = 290, TermType = 3, Count = killed ? (byte)1 : (byte)0 });
        ch.Quests[8006] = progress;
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 11, preSeeded: ch);
        ch.Country = 0; ch.Level = 93; // the harness World packet carries country 1 / level 10
        h.Service.AddNpc(new() { Id = 20012, Country = 0, MapId = 0 });
        h.Service.AddNpc(new() { Id = 20004, Country = 0, MapId = 0 });
        h.Service.InitQuests(); c.Clear(); return (h, s, c);
    }

    [Fact]
    public async Task KarlOffersTheCompletionAndThePreviousNpcDoesNot()
    {
        var x = await Setup();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestListPossibleReq(20012, 20004));
        var r = new PacketReader(x.C.Last(Msg.CS_QUESTLIST_POSSIBLE_ACK)!);
        Assert.Equal(2, r.ReadByte()); Assert.Equal(20012, r.ReadUInt16()); r.ReadByte();
        Assert.Equal(1, r.ReadByte()); Assert.Equal(8007u, r.ReadUInt32()); Assert.Equal(0, r.ReadByte());
        Assert.Equal(20004, r.ReadUInt16()); r.ReadByte(); Assert.Equal(0, r.ReadByte());
    }

    [Fact]
    public async Task ReadyDiannaQuestCompletesAtKarlConsumesTheCrystalAndRewardsOnce()
    {
        var x = await Setup();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(8007));
        Assert.Equal((byte)QuestResult.Success, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(1, x.S.Char!.Quests[8006].CompleteCount);
        Assert.Equal(3522L, x.S.Char.MoneyTotal); Assert.Equal(4101u, x.S.Char.Exp);
        Assert.Empty(x.S.Char.FindInven(0)!.Items);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(8007));
        Assert.Equal(3522L, x.S.Char.MoneyTotal); Assert.Equal(4101u, x.S.Char.Exp);
    }

    [Theory]
    [InlineData(false, true, 290u, (byte)3)]
    [InlineData(true, false, 9657u, (byte)2)]
    public async Task MissingKillOrCrystalStillRejectsWithoutChangingProgressOrRewards(bool killed, bool crystal, uint term, byte type)
    {
        var x = await Setup(killed, crystal);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(8007));
        var r = new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!);
        Assert.Equal((byte)QuestResult.Term, r.ReadByte()); Assert.Equal(8006u, r.ReadUInt32());
        Assert.Equal(term, r.ReadUInt32()); Assert.Equal(type, r.ReadByte());
        Assert.True(x.S.Char!.IsRunningQuest(8006)); Assert.Equal(0L, x.S.Char.MoneyTotal);
        Assert.Equal(crystal ? 1 : 0, x.S.Char.FindInven(0)!.Items.Count);
    }
}
