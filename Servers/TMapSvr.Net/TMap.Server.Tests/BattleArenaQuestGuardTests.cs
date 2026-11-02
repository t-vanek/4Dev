using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class BattleArenaQuestGuardTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task UnimplementedBattleArenaObjectiveCannotPayOutAsAnEmptyDelivery(uint mode)
    {
        var t=new TemplateStore();
        var q=new QuestTemplate {QuestId=100,Type=8,TriggerType=3,TriggerId=500};
        q.Terms.Add(new(mode,28,1));q.Rewards.Add(new(100,1,1,0,0));
        var end=new QuestTemplate {QuestId=101,Type=7,ParentId=100,TriggerType=3,TriggerId=500};
        end.Terms.Add(new(100,1,0));t.Quests[100]=q;t.Quests[101]=end;
        var h=new MapTestHarness(t);var(s,c)=await h.EnterAsync(1,1,11);
        h.Service.AddNpc(new(){Id=500,MapId=0,Country=3});h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(100));
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(101));
        var r=new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!);
        Assert.Equal((byte)QuestResult.Term,r.ReadByte());
        Assert.Equal(100u,r.ReadUInt32());Assert.Equal(mode,r.ReadUInt32());Assert.Equal(28,r.ReadByte());
        Assert.Equal(0u,s.Char!.MoneyTotal);Assert.True(s.Char.IsRunningQuest(100));
        Assert.Equal(0,s.Char.Quests[100].CompleteCount);
    }
}
