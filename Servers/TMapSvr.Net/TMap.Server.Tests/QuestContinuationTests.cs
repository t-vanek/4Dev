using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class QuestContinuationTests
{
    private sealed class MailStore : IQuestPostStore
    {
        public int Calls;
        public byte Issued;
        public bool Fail;
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<QuestPostResult> ReserveQuestPostAsync(uint quest, uint character,
            IReadOnlyList<ItemSaveData> items, byte countMax = 0)
        {
            Calls++; Entered.TrySetResult();
            if (Hold is { } hold) await hold.Task;
            if (Fail) throw new IOException("Reservation failed");
            if (countMax != 0 && Issued >= countMax) return new(QuestPostResult.AlreadyIssued, Issued);
            Issued = Issued == byte.MaxValue ? (byte)1 : (byte)(Issued + 1);
            return new(0, Issued);
        }
    }

    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, TemplateStore T, MailStore Store)> Setup()
    {
        var t = new TemplateStore();
        t.Items[200] = new ItemTemplate(200, 0, new float[4], Stack: 10, ClassId: uint.MaxValue);
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 11, x: 100, z: 100);
        s.Char!.Class = 0; s.Char.Country = 1;
        h.Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 });
        var store = new MailStore(); h.Service.QuestPostStore = store; c.Clear();
        return (h, s, c, t, store);
    }

    private static void AddMail(TemplateStore t, byte max)
    {
        var mail = new QuestTemplate { QuestId = 100, Type = (byte)QuestType.SendPost,
            TriggerType = 3, TriggerId = 500, CountMax = max };
        mail.Rewards.Add(new(200, 2, 1, 0, 1)); t.Quests[100] = mail;
        t.Quests[101] = new QuestTemplate { QuestId = 101, Type = (byte)QuestType.Mission,
            TriggerType = 1, TriggerId = 100, ParentId = 100 };
    }

    [Theory]
    [InlineData((byte)0, 3)] [InlineData((byte)1, 1)] [InlineData((byte)2, 2)]
    public async Task MailHonorsTheChartLimitAndLeavesACompletedProgressEntry(byte max, int expected)
    {
        var x = await Setup(); AddMail(x.T, max); x.H.Service.InitQuests();
        for (int i = 0; i < 3; i++) await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(expected, x.Store.Calls);
        var progress = x.S.Char!.Quests[100];
        Assert.Equal(expected, progress.TriggerCount); Assert.Equal(expected, progress.CompleteCount);
        Assert.False(progress.IsRunning); Assert.False(progress.Save);
        Assert.DoesNotContain(MapService.BuildQuestSaves(x.S.Char, x.H.Service.NowMs), q => q.QuestId == 100);
    }

    [Fact]
    public async Task ReloadedCompletedMailCannotBeIssuedAgain()
    {
        var x = await Setup(); AddMail(x.T, 1); x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        x.S.Char!.Quests.Clear();
        x.H.Service.LoadQuestProgress(x.S.Char, new[] { new QuestLoadRow(100, 0, 1, 1) }, Array.Empty<QuestTermLoadRow>());
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(1, x.Store.Calls); Assert.False(x.S.Char.IsRunningQuest(100));
    }

    [Fact]
    public async Task TemporaryTournamentProfileCannotIssuePersistentQuestMail()
    {
        var x = await Setup(); AddMail(x.T, 1); x.H.Service.InitQuests();
        x.S.Char!.TournamentBackup = new MapService.TournamentBackup();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(0, x.Store.Calls);
        Assert.Null(x.S.Char.FindQuest(100)); Assert.Null(x.S.Char.FindQuest(101));
    }

    [Fact]
    public async Task StaleMemorySynchronizesTheDurableCounterWithoutReplayingChildren()
    {
        var x = await Setup(); AddMail(x.T, 1); x.Store.Issued = 1; x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(1, x.Store.Calls); Assert.Equal(1, x.S.Char!.Quests[100].CompleteCount);
        Assert.Null(x.S.Char.FindQuest(101));
    }

    [Fact]
    public async Task FailedReservationDoesNotConsumeTheCapAndCanBeRetried()
    {
        var x = await Setup(); AddMail(x.T, 1); x.Store.Fail = true; x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Null(x.S.Char!.FindQuest(100)); Assert.Null(x.S.Char.FindQuest(101));
        x.Store.Fail = false;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(2, x.Store.Calls); Assert.Equal(1, x.S.Char.Quests[100].CompleteCount);
        Assert.True(x.S.Char.IsRunningQuest(101));
    }

    [Fact]
    public async Task MailIsCompletedBeforeTheParentGatedChildMissionStarts()
    {
        var x = await Setup(); AddMail(x.T, 1); x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.False(x.S.Char!.IsRunningQuest(100)); Assert.True(x.S.Char.IsRunningQuest(101));
    }

    [Fact]
    public async Task WaitingForReservationDoesNotExposeProgressOrChildrenBeforeTheCommit()
    {
        var x = await Setup(); AddMail(x.T, 1); x.H.Service.InitQuests();
        x.Store.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task dispatch = x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        await x.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(x.S.Char!.FindQuest(100)); Assert.Null(x.S.Char.FindQuest(101));
        x.Store.Hold.SetResult(); await dispatch;
        Assert.Equal(1, x.S.Char.Quests[100].CompleteCount); Assert.True(x.S.Char.IsRunningQuest(101));
    }

    [Fact]
    public async Task UncappedMailHandlesTheByteCounterBoundary()
    {
        var x = await Setup(); AddMail(x.T, 0); x.Store.Issued = byte.MaxValue; x.H.Service.InitQuests();
        x.H.Service.LoadQuestProgress(x.S.Char!, new[] { new QuestLoadRow(100, 0, 255, 255) }, Array.Empty<QuestTermLoadRow>());
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(1, x.S.Char!.Quests[100].CompleteCount);
        Assert.Equal(1, x.S.Char.Quests[100].TriggerCount); Assert.False(x.S.Char.IsRunningQuest(100));
    }

    [Theory]
    [InlineData((byte)3)] [InlineData((byte)13)] [InlineData((byte)17)] [InlineData((byte)21)]
    public async Task DroppingAndReacceptingAQuestClearsEveryCountingObjective(byte termType)
    {
        var x = await Setup();
        var q = new QuestTemplate { QuestId = 100, Type = (byte)QuestType.Mission, TriggerType = 3, TriggerId = 500 };
        q.Terms.Add(new(600, termType, 2)); x.T.Quests[100] = q; x.H.Service.InitQuests();
        var progress = new QuestProgress { Template = q, TriggerCount = 1 };
        progress.RunningTerms.Add(new RunningTerm { TermId = 600, TermType = termType, Count = 2 });
        x.S.Char!.Quests[100] = progress;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestDropReq(100));
        Assert.Equal(0, Assert.Single(progress.RunningTerms).Count);
        var saved = Assert.Single(MapService.BuildQuestSaves(x.S.Char, x.H.Service.NowMs));
        Assert.Equal(0, Assert.Single(saved.Terms).Count);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.True(progress.IsRunning); Assert.Equal(0, Assert.Single(progress.RunningTerms).Count);
    }

    [Theory]
    [InlineData((byte)0, (byte)1)] [InlineData((byte)3, (byte)3)]
    public async Task GiveSkillActivatesALevelZeroCatalogueEntryAndRunsChildren(byte requested, byte expected)
    {
        var x = await Setup(); var skill = Skill(10, uint.MaxValue);
        x.T.Skills[300] = skill; var placeholder = new Skill { SkillId = 300, Level = 0 };
        x.S.Char!.Skills.Add(placeholder); AddSkillQuest(x.T, requested); x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Same(placeholder, Assert.Single(x.S.Char.Skills, k => k.SkillId == 300));
        Assert.Same(skill, placeholder.Template); Assert.Equal(expected, placeholder.Level);
        Assert.True(x.C.Has(Msg.CS_SKILLBUY_ACK)); Assert.True(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
        Assert.Equal(expected, Assert.Single(MapService.BuildSkillSaves(x.S.Char, x.H.Service.NowMs), k => k.SkillId == 300).Level);
        x.C.Clear(); await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.False(x.C.Has(Msg.CS_SKILLBUY_ACK)); Assert.False(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task GiveSkillKeepsLearnedSkillsAndRejectsAnUnlearnablePlaceholder(int scenario)
    {
        var x = await Setup(); var skill = Skill(scenario == 2 ? (byte)0 : (byte)10, scenario == 1 ? 2u : uint.MaxValue);
        x.T.Skills[300] = skill; byte before = scenario == 0 ? (byte)4 : (byte)0;
        var held = new Skill { SkillId = 300, Level = before, Template = skill }; x.S.Char!.Skills.Add(held);
        AddSkillQuest(x.T, 1); x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(before, held.Level); Assert.Single(x.S.Char.Skills, k => k.SkillId == 300);
        Assert.False(x.C.Has(Msg.CS_SKILLBUY_ACK)); Assert.False(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
    }

    private static SkillTemplate Skill(byte max, uint classes)
        => new(300, 0, 0, 0, 0, 0, 1, max, 1, 0, 0, 0, 0, 0, 1, 0, ClassId: classes);

    private static void AddSkillQuest(TemplateStore t, byte level)
    {
        var q = new QuestTemplate { QuestId = 100, Type = (byte)QuestType.GiveSkill, TriggerType = 3, TriggerId = 500 };
        q.Terms.Add(new(300, 4, level)); t.Quests[100] = q;
        t.Quests[101] = new QuestTemplate { QuestId = 101, Type = (byte)QuestType.ChapterMsg, TriggerType = 1, TriggerId = 100 };
    }
}
