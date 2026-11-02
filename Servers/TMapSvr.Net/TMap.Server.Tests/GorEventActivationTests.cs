using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class GorEventActivationTests
{
    public static TheoryData<uint, uint, ushort, ushort[], byte[], ushort[], byte[]> Cases => new()
    {
        {233,238,26567,[18540,18541,18542,18543],[1,1,1,1],[18061],[1]},
        {234,239,26567,[18541,18542],[3,3],[18540],[1]},
        {235,240,26567,[18540,18542],[3,3],[18541],[1]},
        {236,241,26567,[18540,18541],[3,3],[18542],[1]},
        {237,242,26567,[18540,18541,18542],[5,5,5],[18543],[1]},
        {2398,2458,20040,[18323],[5],[18322,7844],[5,1]}
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RestoredNpcOffersAcceptsConsumesAndRewardsOncePerAcceptance(uint id, uint completeId, ushort npcId,
        ushort[] ingredients, byte[] needed, ushort[] rewards, byte[] counts)
    {
        var t = new TemplateStore();
        foreach (var item in ingredients.Concat(rewards).Distinct()) t.Items[item] = new(item,0,new float[4],Stack:200,ClassId:uint.MaxValue);
        var mission = new QuestTemplate { QuestId=id,Type=8,TriggerType=3,TriggerId=npcId };
        for (int i=0;i<ingredients.Length;i++) mission.Terms.Add(new(ingredients[i],2,needed[i]));
        for (int i=0;i<rewards.Length;i++) mission.Rewards.Add(new(rewards[i],2,1,0,counts[i]));
        var complete = new QuestTemplate { QuestId=completeId,ParentId=id,Type=7,TriggerType=3,TriggerId=npcId };
        complete.Terms.Add(new(id,1,0)); complete.Conditions.Add(new(id,3,0));
        t.Quests[id]=mission;t.Quests[completeId]=complete;
        var ch = new Character {CharId=1,Country=4,Level=1,Hp=100,MaxHp=100};
        var bag = new Inven {InvenId=255};ch.Invens.Add(bag);
        for(int i=0;i<ingredients.Length;i++) bag.Items.Add(new(){TemplateId=ingredients[i],Template=t.Items[ingredients[i]],Count=needed[i],ItemSlot=(byte)i});
        var h = new MapTestHarness(t);var(s,c)=await h.EnterAsync(1,1,11,preSeeded:ch);ch.Country=4;
        h.Service.AddNpc(new(){Id=npcId,Country=3,MapId=0});h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestListPossibleReq(npcId));
        var list=new PacketReader(c.Last(Msg.CS_QUESTLIST_POSSIBLE_ACK)!);
        Assert.Equal(1,list.ReadByte());Assert.Equal(npcId,list.ReadUInt16());list.ReadByte();Assert.Equal(1,list.ReadByte());Assert.Equal(id,list.ReadUInt32());
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(id));Assert.True(ch.IsRunningQuest(id));
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(completeId));
        Assert.Equal((byte)QuestResult.Success,new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.False(ch.IsRunningQuest(id));Assert.Equal(rewards.Length,bag.Items.Count);
        for(int i=0;i<rewards.Length;i++) Assert.Equal(counts[i],bag.Items.Single(x=>x.TemplateId==rewards[i]).Count);
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(completeId));Assert.Equal(rewards.Length,bag.Items.Count);
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(id));Assert.True(ch.IsRunningQuest(id));
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(completeId));
        Assert.Equal((byte)QuestResult.Term,new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(1,ch.Quests[id].CompleteCount);Assert.Equal(rewards.Length,bag.Items.Count);
    }
}
