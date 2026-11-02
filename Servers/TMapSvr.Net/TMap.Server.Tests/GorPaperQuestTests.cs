using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class GorPaperQuestTests
{
    [Theory]
    [InlineData(8883u, 8884u, (ushort)18133, (ushort)18129)]
    [InlineData(8885u, 8886u, (ushort)18131, (ushort)18130)]
    [InlineData(8887u, 8888u, (ushort)18132, (ushort)18128)]
    public async Task HoubaOffersAcceptsAndCompletesThePaperQuestWithoutASecondReward(uint id, uint completion, ushort fetch, ushort reward)
    {
        var t = new TemplateStore();
        t.Items[fetch] = new(fetch, 0, new float[4], Stack: 200, ClassId: uint.MaxValue);
        t.Items[reward] = new(reward, 0, new float[4], Stack: 1, ClassId: uint.MaxValue);
        var mission = new QuestTemplate { QuestId = id, Type = 8, TriggerType = 3, TriggerId = 22919 };
        mission.Terms.Add(new(fetch, 2, 20)); mission.Rewards.Add(new(reward, 2, 1, 0, 1));
        var complete = new QuestTemplate { QuestId = completion, ParentId = id, Type = 7, TriggerType = 3, TriggerId = 22919 };
        complete.Terms.Add(new(id, 1, 0)); complete.Conditions.Add(new(id, 3, 0));
        t.Quests[id] = mission; t.Quests[completion] = complete;
        var ch = new Character { CharId = 1, Country = 4, Class = 3, Level = 1, Hp = 100, MaxHp = 100 };
        var bag = new Inven { InvenId = 255 }; ch.Invens.Add(bag);
        bag.Items.Add(new() { TemplateId = fetch, Template = t.Items[fetch], ItemSlot = 0, Count = 20 });
        var h = new MapTestHarness(t); var (s, c) = await h.EnterAsync(1, 1, 11, preSeeded: ch); ch.Country = 4;
        h.Service.AddNpc(new() { Id = 22919, Country = 3, MapId = 0 });
        h.Service.AddNpc(new() { Id = 26306, Country = 3, MapId = 0 }); h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestListPossibleReq(22919, 26306));
        var list = new PacketReader(c.Last(Msg.CS_QUESTLIST_POSSIBLE_ACK)!);
        Assert.Equal(2, list.ReadByte()); Assert.Equal(22919, list.ReadUInt16()); list.ReadByte();
        Assert.Equal(1, list.ReadByte()); Assert.Equal(id, list.ReadUInt32()); list.ReadByte();
        Assert.Equal(26306, list.ReadUInt16()); list.ReadByte(); Assert.Equal(0, list.ReadByte());
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(id));
        Assert.True(ch.IsRunningQuest(id));
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(completion));
        Assert.Equal((byte)QuestResult.Success, new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.False(ch.IsRunningQuest(id)); Assert.DoesNotContain(bag.Items, item => item.TemplateId == fetch);
        Assert.Equal(reward, Assert.Single(bag.Items).TemplateId);
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(completion));
        Assert.Single(bag.Items); Assert.Equal(1, ch.Quests[id].CompleteCount);
    }
}
