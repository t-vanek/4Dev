using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class QuestRewardChoiceTests
{
    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, QuestTemplate Mission)> Setup(byte type = 2, uint id = 200)
    {
        var t = new TemplateStore();
        foreach (ushort item in new ushort[] { 200, 201, 202 })
            t.Items[item] = new(item, 0, new float[4], Stack: 10, ClassId: item == 202 ? 2u : uint.MaxValue);
        t.Skills[400] = new(400, 0, 0, 0, 0, 0, 1, 10, 1, 0, 0, 0, 0, 0, 1, 0, ClassId: uint.MaxValue);
        t.QuestItems[300] = new(300, new FullItemRow(0, 0, 0, 200, 5, 1, 0, 100, 90, 1, 0, 2, new byte[6], new ushort[6], new uint[6], 0, 0, 0), 1, 0);
        var q = new QuestTemplate { QuestId = 100, Type = (byte)QuestType.Mission, TriggerType = 3, TriggerId = 500, CountMax = 1 };
        q.Terms.Add(new(201, 2, 1)); q.Rewards.Add(new(id, type, 2, 0, 1)); q.Rewards.Add(new(50, 1, 1, 0, 0));
        var complete = new QuestTemplate { QuestId = 101, Type = (byte)QuestType.Complete, TriggerType = 3, TriggerId = 500, ParentId = 100 };
        complete.Terms.Add(new(100, 1, 0)); t.Quests[100] = q; t.Quests[101] = complete;
        var h = new MapTestHarness(t); var ch = new Character { CharId = 1, Class = 0, Country = 1, Level = 5, Hp = 100, MaxHp = 100 };
        ch.Invens.Add(new Inven { InvenId = 255 });
        ch.Invens[0].Items.Add(new Item { TemplateId = 201, Template = t.Items[201], Count = 2, ItemSlot = 0 });
        var (s, c) = await h.EnterAsync(1, 9, 11, preSeeded: ch);
        h.Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 }); h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(100)); c.Clear();
        return (h, s, c, q);
    }

    [Theory]
    [InlineData((byte)0, 0u)] [InlineData((byte)2, 999u)] [InlineData((byte)7, 200u)]
    public async Task MissingOrSpoofedSelectionPreservesTheRunningQuestAndIngredients(byte type, uint id)
    {
        var x = await Setup(); await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101, type, id));
        Assert.Equal((byte)QuestResult.Term, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.True(x.S.Char!.IsRunningQuest(100)); Assert.Equal(0L, x.S.Char.MoneyTotal);
        Assert.Equal(2, x.S.Char.FindInven(255)!.Items.Single().Count);
        Assert.False(x.C.Has(Msg.CS_MONEY_ACK));
    }

    [Theory]
    [InlineData((byte)2, 200u)] [InlineData((byte)7, 300u)] [InlineData((byte)3, 400u)]
    public async Task ValidItemMagicAndSkillChoicesCompleteOnce(byte type, uint id)
    {
        var x = await Setup(type, id);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101, type, id));
        Assert.False(x.S.Char!.IsRunningQuest(100)); Assert.Equal(50L, x.S.Char.MoneyTotal);
        Assert.Equal(1, x.S.Char.FindInven(255)!.Items.Single(i => i.TemplateId == 201).Count);
        if (type == 3) Assert.Equal((ushort)400, Assert.Single(x.S.Char.Skills).SkillId);
        else Assert.Single(x.S.Char.FindInven(255)!.Items, i => i.TemplateId == 200);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101, type, id));
        Assert.Equal(50L, x.S.Char.MoneyTotal);
    }

    [Fact]
    public async Task AForeignClassChoiceCannotConsumeTheQuestWithoutItsItem()
    {
        var x = await Setup(2, 202);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101, 2, 202));
        Assert.True(x.S.Char!.IsRunningQuest(100)); Assert.Equal(0L, x.S.Char.MoneyTotal);
        Assert.Equal(2, x.S.Char.FindInven(255)!.Items.Single().Count);
    }

    [Fact]
    public async Task NoApplicableClassChoiceKeepsTheExistingZeroSelectionSequence()
    {
        var x = await Setup(2, 202);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.False(x.S.Char!.IsRunningQuest(100)); Assert.Equal(50L, x.S.Char.MoneyTotal);
        Assert.DoesNotContain(x.S.Char.FindInven(255)!.Items, i => i.TemplateId == 202);
    }
}
