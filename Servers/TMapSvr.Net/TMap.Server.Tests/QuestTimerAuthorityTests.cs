using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class QuestTimerAuthorityTests
{
    private static QuestTemplate Timed()
    {
        var q = new QuestTemplate { QuestId = 100, Type = (byte)QuestType.Mission, TriggerType = 3, TriggerId = 500 };
        q.Terms.Add(new(1000, 6, 0)); q.Rewards.Add(new(50, 1, 1, 0, 0)); return q;
    }

    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C)> Setup()
    {
        var t = new TemplateStore(); t.Quests[100] = Timed();
        var complete = new QuestTemplate { QuestId = 101, Type = (byte)QuestType.Complete, TriggerType = 3, TriggerId = 500 };
        complete.Terms.Add(new(100, 1, 0)); t.Quests[101] = complete;
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 11);
        h.Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 }); h.Service.InitQuests();
        h.Service.NowMs = 0;
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(100)); c.Clear();
        return (h, s, c);
    }
    private static byte[] End(uint id = 100) => new PacketWriter(Msg.CS_QUESTENDTIMER_REQ).WriteUInt32(id).ToArray();

    [Theory]
    [InlineData(0u)] [InlineData(999u)] [InlineData(1000u)]
    public async Task EarlyClientNotificationCannotStopOrFailAnActiveTimer(uint now)
    {
        var x = await Setup(); var progress = x.S.Char!.Quests[100]; x.H.Service.NowMs = now;
        await x.H.Service.DispatchClientAsync(x.S, End());
        Assert.False(progress.TimerExpired); Assert.True(progress.HasTimer); Assert.Equal(1000u, progress.TimerTick);
        Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK));
    }

    [Fact]
    public async Task ExpiredClientNotificationIsIdempotentAndCannotEnableTurnIn()
    {
        var x = await Setup(); x.H.Service.NowMs = 1001;
        await x.H.Service.DispatchClientAsync(x.S, End()); await x.H.Service.DispatchClientAsync(x.S, End());
        Assert.Single(x.C.WithId(Msg.CS_QUESTUPDATE_ACK));
        Assert.True(x.S.Char!.Quests[100].TimerExpired);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Term, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(0L, x.S.Char.MoneyTotal); Assert.True(x.S.Char.IsRunningQuest(100));
        Assert.Equal(0u, Assert.Single(MapService.BuildQuestSaves(x.S.Char, x.H.Service.NowMs)).Tick);
    }

    [Fact]
    public async Task ServerExpiresTheQuestEvenWhenTheClientNeverNotifiesIt()
    {
        var x = await Setup();
        await x.H.Service.OnTimerAsync(); // exact deadline: retain the existing strict > boundary
        Assert.False(x.S.Char!.Quests[100].TimerExpired); Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK));
        await x.H.Service.OnTimerAsync();
        Assert.True(x.S.Char.Quests[100].TimerExpired);
        var r = new PacketReader(x.C.Last(Msg.CS_QUESTUPDATE_ACK)!);
        Assert.Equal((100u, 1000u, (byte)6, (byte)0, (byte)QuestTermStatus.Failed),
            (r.ReadUInt32(), r.ReadUInt32(), r.ReadByte(), r.ReadByte(), r.ReadByte()));
        await x.H.Service.OnTimerAsync(); Assert.Single(x.C.WithId(Msg.CS_QUESTUPDATE_ACK));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task NonMainLoadingOrCompletedQuestsDoNotReceiveAnExpiry(int scenario)
    {
        var x = await Setup(); var q = x.S.Char!.Quests[100];
        if (scenario == 0) x.S.IsMain = false;
        if (scenario == 1) x.S.State = EnterState.Entering;
        if (scenario == 2) q.CompleteCount = q.TriggerCount;
        await x.H.Service.OnTimerAsync(); await x.H.Service.OnTimerAsync();
        await x.H.Service.DispatchClientAsync(x.S, End());
        Assert.False(q.TimerExpired); Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK));
    }

    [Fact]
    public async Task AnActiveTimerIsSavedAgainAfterItsDirtyFlagWasCleared()
    {
        var x = await Setup();
        Assert.Equal(800u, Assert.Single(MapService.BuildQuestSaves(x.S.Char!, 200)).Tick);
        Assert.False(x.S.Char!.Quests[100].Save);
        Assert.Equal(300u, Assert.Single(MapService.BuildQuestSaves(x.S.Char, 700)).Tick);
    }

    [Fact]
    public async Task ZeroRemainingTimerIsAlreadyFailedAtTheReloadTick()
    {
        var x = await Setup(); x.S.Char!.Quests.Clear();
        x.H.Service.LoadQuestProgress(x.S.Char, new[] { new QuestLoadRow(100, 0, 0, 1) }, Array.Empty<QuestTermLoadRow>());
        Assert.True(x.S.Char.Quests[100].TimerExpired);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Term, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(0L, x.S.Char.MoneyTotal);
    }

    [Fact]
    public async Task DroppingAnExpiredQuestAllowsANewTimerOnReacceptance()
    {
        var x = await Setup(); x.H.Service.NowMs = 1001;
        await x.H.Service.DispatchClientAsync(x.S, End());
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestDropReq(100));
        Assert.False(x.S.Char!.Quests[100].TimerExpired); Assert.False(x.S.Char.Quests[100].HasTimer);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.False(x.S.Char.Quests[100].TimerExpired);
        x.H.Service.NowMs = 1500;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Success, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(50L, x.S.Char.MoneyTotal);
    }

    [Fact]
    public async Task DeadlineAndRemainingTimeHandleUnsignedClockWrap()
    {
        var x = await Setup(); var q = x.S.Char!.Quests[100]; q.BeginTick = uint.MaxValue - 500;
        x.H.Service.NowMs = 200;
        Assert.Equal(299u, q.RemainingTimer(200)); await x.H.Service.DispatchClientAsync(x.S, End());
        Assert.False(q.TimerExpired);
        x.H.Service.NowMs = 600; await x.H.Service.DispatchClientAsync(x.S, End()); Assert.True(q.TimerExpired);
    }

    [Fact]
    public async Task LoginSendsTheRemainingCountdownInsteadOfTheOriginalDuration()
    {
        var t = new TemplateStore(); var template = Timed(); t.Quests[100] = template;
        var h = new MapTestHarness(t); h.Service.NowMs = 600;
        var ch = new Character { CharId = 1, Hp = 100, MaxHp = 100, Level = 5 };
        ch.Quests[100] = new QuestProgress { Template = template, TriggerCount = 1, TimerActive = true, TimerTick = 1000, BeginTick = 100 };
        var (_, c) = await h.EnterAsync(1, 1, 11, preSeeded: ch);
        var r = new PacketReader(c.Last(Msg.CS_QUESTSTARTTIMER_ACK)!);
        Assert.Equal((100u, 500u), (r.ReadUInt32(), r.ReadUInt32()));
    }
}
