using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class QuestPointRewardTests
{
    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, Character Ch, QuestTemplate Q)> Open(uint reward, uint initial = 5, byte method = 1)
    {
        var t = new TemplateStore();
        var q = new QuestTemplate { QuestId=100,Type=8,TriggerType=3,TriggerId=500,CountMax=1 };
        q.Rewards.Add(new(reward,10,method,0,0));
        var complete = new QuestTemplate { QuestId=101,Type=7,TriggerType=3,TriggerId=500,ParentId=100 };
        complete.Terms.Add(new(100,1,0)); t.Quests[100]=q;t.Quests[101]=complete;
        var h=new MapTestHarness(t);var(s,c)=await h.EnterAsync(1,1,11);
        var ch=s.Char!;ch.PvpUseablePoint=initial;ch.PvpTotalPoint=500;ch.MonthPvPoint=300;
        h.Service.AddNpc(new(){Id=500,Country=3,MapId=0});h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(100));c.Clear();
        return(h,s,c,ch,q);
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(150u)]
    public async Task ExactAchievementRewardOnlyChangesSpendableBalanceAndCannotReplay(uint amount)
    {
        var x=await Open(amount);
        // Quest points must not enter the kill/companion reward path.
        x.Ch.CompanionSlot=0;
        await x.H.Service.DispatchClientAsync(x.S,MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Success,new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(5+amount,x.Ch.PvpUseablePoint);Assert.Equal(500u,x.Ch.PvpTotalPoint);Assert.Equal(300u,x.Ch.MonthPvPoint);
        var r=new PacketReader(x.C.Last(Msg.CS_PVPPOINT_ACK)!);
        Assert.Equal(500u,r.ReadUInt32());Assert.Equal(5+amount,r.ReadUInt32());Assert.Equal(6,r.ReadByte());Assert.Equal(300u,r.ReadUInt32());Assert.Equal(0,r.Remaining);
        Assert.Equal(5+amount,MapService.BuildCharSave(x.Ch).PvpUseablePoint);
        x.C.Clear();await x.H.Service.DispatchClientAsync(x.S,MapTestHarness.QuestExecReq(101));
        Assert.Equal(5+amount,x.Ch.PvpUseablePoint);Assert.False(x.C.Has(Msg.CS_PVPPOINT_ACK));
    }

    [Fact]
    public async Task AggregateOverflowRejectsBeforeAnyRewardOrCompletionChanges()
    {
        var x=await Open(60,uint.MaxValue-100);x.Q.Rewards.Add(new(60,10,1,0,0));x.Q.Rewards.Add(new(50,1,1,0,0));
        await x.H.Service.DispatchClientAsync(x.S,MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Term,new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(uint.MaxValue-100,x.Ch.PvpUseablePoint);Assert.Equal(0u,x.Ch.MoneyTotal);Assert.True(x.Ch.IsRunningQuest(100));
        Assert.False(x.C.Has(Msg.CS_PVPPOINT_ACK));
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public async Task UndocumentedPointMethodsRemainRejected(byte method)
    {
        var x=await Open(100,method:method);
        await x.H.Service.DispatchClientAsync(x.S,MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Term,new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(5u,x.Ch.PvpUseablePoint);Assert.True(x.Ch.IsRunningQuest(100));
    }

    [Fact]
    public async Task UnsupportedArenaObjectiveStillBlocksAPointReward()
    {
        var x=await Open(150);x.Q.Terms.Add(new(2,28,1));
        await x.H.Service.DispatchClientAsync(x.S,MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Term,new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(5u,x.Ch.PvpUseablePoint);Assert.False(x.C.Has(Msg.CS_PVPPOINT_ACK));
    }
}
