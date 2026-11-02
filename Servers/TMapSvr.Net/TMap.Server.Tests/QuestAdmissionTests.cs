using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class QuestAdmissionTests
{
    private sealed class Store : IQuestProgressStore
    {
        public Exception? Failure;
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<uint> Calls = [];
        public List<QuestLoadRow> Headers = [];
        public List<QuestTermLoadRow> Terms = [];
        public async Task<(List<QuestLoadRow> quests, List<QuestTermLoadRow> terms)> LoadQuestsAsync(uint id, CancellationToken ct = default)
        {
            Calls.Add(id); Entered.TrySetResult();
            if (Hold is { } hold) await hold.Task;
            if (Failure is { } failure) throw failure;
            return (Headers, Terms);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task FailedRequiredQuestReadCannotBecomeAnEmptyPlayableLog(int error)
    {
        var store = new Store { Failure = error switch
        {
            0 => new IOException("SQL unavailable"),
            1 => new InvalidDataException("Invalid quest rows"),
            _ => new OperationCanceledException()
        } };
        var h = new MapTestHarness(); h.Service.QuestProgressStore = store;
        // The harness continues sending all world/client entry packets even after the fake socket closes.
        var (s, c) = await h.EnterAsync(17, 9, 10);
        Assert.True(c.Closed); Assert.Null(s.Char); Assert.Null(h.State.FindByChar(17));
        Assert.False(s.IsMain); Assert.False(s.EntryValidated); Assert.NotEqual(EnterState.InGame, s.State);
        Assert.False(h.World.Has(Msg.MW_ADDCHAR_ACK)); Assert.False(h.World.Has(Msg.MW_ENTERSVR_ACK));
        Assert.False(c.Has(Msg.CS_CHARINFO_ACK)); Assert.False(c.Has(Msg.CS_QUESTLIST_ACK));
        Assert.Equal(ConnectResult.Internal, (ConnectResult)new PacketReader(c.Last(Msg.CS_CONNECT_ACK)!).ReadByte());
        Assert.Equal(new uint[] { 17 }, store.Calls);
    }

    [Fact]
    public async Task WaitingForRequiredRowsDoesNotPublishAPartiallyLoadedCharacter()
    {
        var store = new Store { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var h = new MapTestHarness(); h.Service.QuestProgressStore = store;
        var c = new FakeClientChannel(); var s = new ClientSession(c);
        var connect = h.Service.DispatchClientAsync(s, MapTestHarness.ConnectReq(17, 9, 10));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(s.Char); Assert.False(h.World.Has(Msg.MW_ADDCHAR_ACK)); Assert.False(c.Closed);
        store.Hold.SetResult(); await connect;
        Assert.NotNull(s.Char); Assert.True(h.World.Has(Msg.MW_ADDCHAR_ACK)); Assert.False(c.Closed);
        await h.Service.DispatchWorldAsync(MapTestHarness.EnterSvrReq(17, 10));
        Assert.True(h.World.Has(Msg.MW_ENTERSVR_ACK)); Assert.Single(store.Calls);
    }

    [Fact]
    public async Task ValidEmptyLogStillAllowsANewCharacter()
    {
        var store = new Store(); var h = new MapTestHarness(); h.Service.QuestProgressStore = store;
        var (s, c) = await h.EnterAsync(17, 9, 10);
        Assert.Equal(EnterState.InGame, s.State); Assert.False(c.Closed);
        Assert.Empty(s.Char!.Quests); Assert.Equal(new uint[] { 17 }, store.Calls);
    }

    [Fact]
    public async Task RestoredDatabaseAllowsAFreshSessionAndRetainsCompletedQuestEligibility()
    {
        var t = new TemplateStore();
        var mission = new QuestTemplate { QuestId = 100, Type = (byte)QuestType.Mission, TriggerType = 3, TriggerId = 500, CountMax = 1 };
        var complete = new QuestTemplate { QuestId = 101, Type = (byte)QuestType.Complete, TriggerType = 3, TriggerId = 500, ParentId = 100 };
        mission.Rewards.Add(new(50, 1, 1, 0, 1)); complete.Terms.Add(new(100, 1, 0));
        t.Quests[100] = mission; t.Quests[101] = complete;
        var store = new Store { Failure = new IOException("temporary outage"), Headers = [new(100, 0, 1, 1)] };
        var h = new MapTestHarness(t); h.Service.QuestProgressStore = store; h.Service.InitQuests();
        var (failed, rejected) = await h.EnterAsync(17, 9, 10);
        Assert.True(rejected.Closed); Assert.Null(failed.Char);
        store.Failure = null;
        var (s, c) = await h.EnterAsync(17, 9, 11);
        Assert.Equal(EnterState.InGame, s.State); Assert.False(c.Closed);
        h.Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 });
        long before = s.Char!.MoneyTotal; c.Clear();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(100));
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(101));
        Assert.False(c.Has(Msg.CS_QUESTADD_ACK)); Assert.False(s.Char.IsRunningQuest(100));
        Assert.Equal(before, s.Char.MoneyTotal); Assert.Equal((byte)1, s.Char.Quests[100].CompleteCount);
        Assert.Equal(new uint[] { 17, 17 }, store.Calls);
        // Positive control: losing this saved completion would make these same valid packets pay again.
        s.Char.Quests.Clear(); c.Clear();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(100));
        Assert.True(c.Has(Msg.CS_QUESTADD_ACK));
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(101));
        Assert.Equal(before + 50, s.Char.MoneyTotal);
    }

    [Fact]
    public async Task WorldDrivenLoadFailureAlsoClosesAndUnregistersTheSession()
    {
        var h = new MapTestHarness(); h.Service.QuestProgressStore = new Store { Failure = new IOException("SQL unavailable") };
        var c = new FakeClientChannel();
        var s = new ClientSession(c) { CharId = 17, UserId = 9, Key = 10, Channel = 1, State = EnterState.Entering };
        h.State.Register(s);
        await h.Service.DispatchWorldAsync(MapTestHarness.EnterSvrReq(17, 10));
        Assert.True(c.Closed); Assert.Null(s.Char); Assert.Null(h.State.FindByChar(17));
        Assert.False(s.EntryValidated); Assert.False(s.IsMain); Assert.False(h.World.Has(Msg.MW_ENTERSVR_ACK));
        await h.Service.DispatchWorldAsync(MapTestHarness.ConResultReq(17, 10, ConnectResult.Success));
        Assert.NotEqual(EnterState.Granted, s.State);
    }

    [Fact]
    public async Task RunningTermsAndExpiredTimersArePresentBeforeEntryPackets()
    {
        var t = new TemplateStore();
        var mission = new QuestTemplate { QuestId = 100, Type = (byte)QuestType.Mission };
        mission.Terms.Add(new(500, 3, 4)); mission.Terms.Add(new(1000, 6, 0)); t.Quests[100] = mission;
        var store = new Store { Headers = [new(100, 0, 0, 1)], Terms = [new(100, 500, 3, 2)] };
        var h = new MapTestHarness(t); h.Service.QuestProgressStore = store; h.Service.InitQuests();
        var (s, c) = await h.EnterAsync(17, 9, 10);
        Assert.False(c.Closed); Assert.True(c.Has(Msg.CS_QUESTLIST_ACK));
        Assert.Equal(2, Assert.Single(s.Char!.Quests[100].RunningTerms).Count);
        Assert.True(s.Char.Quests[100].TimerExpired); Assert.Equal(0u, s.Char.Quests[100].RemainingTimer(0));
        Assert.False(c.Has(Msg.CS_QUESTSTARTTIMER_ACK));
    }
}
