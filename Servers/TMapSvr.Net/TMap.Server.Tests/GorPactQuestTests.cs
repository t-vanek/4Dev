using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class GorPactQuestTests
{
    [Fact]
    public async Task PactRequiresLevel90AndItsDestinationThenAwardsOnce()
    {
        var t = new TemplateStore();
        t.LevelExp[89] = 0; t.LevelExp[90] = 1_000_000;
        var q = new QuestTemplate { QuestId=547, Type=8, TriggerType=3, TriggerId=26200, CountMax=1 };
        q.Terms.Add(new(547,1,0)); q.Conditions.Add(new(90,1,0));
        q.Rewards.Add(new(1782,1,1,0,0)); q.Rewards.Add(new(855,6,1,0,0)); q.Rewards.Add(new(167,9,1,0,0));
        var end = new QuestTemplate { QuestId=548, ParentId=547, Type=7, TriggerType=3, TriggerId=26436 };
        end.Terms.Add(new(547,1,0)); end.Conditions.Add(new(547,3,0));
        t.Quests[547]=q; t.Quests[548]=end;
        var h=new MapTestHarness(t);
        var ch=new Character { CharId=1,Level=89,Hp=100,MaxHp=100 };
        var(s,c)=await h.EnterAsync(1,1,11,preSeeded:ch);ch.Level=89;
        h.Service.AddNpc(new(){Id=26200,Country=3,MapId=0});
        h.Service.AddNpc(new(){Id=26436,Country=3,MapId=1});
        h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(547));
        Assert.False(ch.IsRunningQuest(547));
        ch.Level=90; ch.Exp=0;
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(547));
        Assert.True(ch.IsRunningQuest(547));
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(548));
        Assert.Equal(0,ch.Quests[547].CompleteCount);Assert.Equal(0u,ch.SoulLotExp);
        h.Service.AddNpc(new(){Id=26436,Country=3,MapId=0});
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(548));
        Assert.Equal((byte)QuestResult.Success,new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(1782u,ch.MoneyTotal);Assert.Equal(855u,ch.Exp);Assert.Equal(1022u,ch.SoulLotExp);
        Assert.Equal(1,ch.Quests[547].CompleteCount);Assert.False(ch.IsRunningQuest(547));
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(548));
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(547));
        Assert.Equal(1782u,ch.MoneyTotal);Assert.Equal(1022u,ch.SoulLotExp);Assert.False(ch.IsRunningQuest(547));
    }
}
