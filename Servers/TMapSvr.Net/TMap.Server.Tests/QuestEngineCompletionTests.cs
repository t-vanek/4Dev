using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class QuestEngineCompletionTests
{
    private sealed class MailStore : IQuestPostStore
    {
        public List<(uint Quest, uint Character, IReadOnlyList<ItemSaveData> Items)> Calls = new();
        public int Result;
        public bool Throw;
        public Task<QuestPostResult> ReserveQuestPostAsync(uint quest, uint character, IReadOnlyList<ItemSaveData> items, byte countMax = 0)
        {
            Calls.Add((quest, character, items));
            return Throw ? Task.FromException<QuestPostResult>(new IOException("DB unavailable"))
                : Task.FromResult(new QuestPostResult(Result, (byte)Calls.Count));
        }
    }

    private static QuestTemplate Quest(uint id, QuestType type, byte trigger = 3, uint triggerId = 500)
        => new() { QuestId = id, Type = (byte)type, TriggerType = trigger, TriggerId = triggerId };

    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, TemplateStore T, MailStore Mail)> Enter()
    {
        var t = new TemplateStore();
        t.Items[200] = new ItemTemplate(200, 0, new float[4], DefaultLevel: 5, Stack: 10, ClassId: uint.MaxValue);
        t.LevelExp[5] = 1000000;
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Hero", Class = 0, Country = 1, AidCountry = 3, Level = 5, Hp = 100, MaxHp = 100 };
        ch.Invens.Add(new Inven { InvenId = 255 });
        var (s, c) = await h.EnterAsync(1, 1, 11, x: 100, z: 100, preSeeded: ch);
        ch.Level = 5; ch.Class = 0;
        h.Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 });
        var mail = new MailStore(); h.Service.QuestPostStore = mail; c.Clear();
        return (h, s, c, t, mail);
    }

    private static void MailQuest(TemplateStore t, byte method = 1, byte data = 0, byte count = 25)
    {
        var q = Quest(100, QuestType.SendPost);
        q.Rewards.Add(new(200, 2, method, data, count)); t.Quests[100] = q;
        t.Quests[101] = Quest(101, QuestType.Mission, 1, 100);
    }

    [Fact]
    public async Task MailSplitsStacksIgnoresFullBagAndRunsChildrenAfterCommit()
    {
        var x = await Enter(); MailQuest(x.T); x.H.Service.InitQuests(); x.S.Char!.FindInven(255)!.SlotCount = 0;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        var job = Assert.Single(x.Mail.Calls);
        Assert.Equal((100u, 1u), (job.Quest, job.Character));
        Assert.Equal(new byte[] { 10, 10, 5 }, job.Items.Select(i => i.Count));
        Assert.All(job.Items, i => Assert.Equal((ushort)200, i.TemplateId));
        Assert.Empty(x.S.Char.FindInven(255)!.Items);
        Assert.True(x.S.Char.IsRunningQuest(101));
    }

    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(0, true)]
    public async Task RejectedOrFailedMailDoesNotAdvanceChildren(int result, bool fail)
    {
        var x = await Enter(); MailQuest(x.T); x.H.Service.InitQuests(); x.Mail.Result = result; x.Mail.Throw = fail;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Single(x.Mail.Calls); Assert.Null(x.S.Char!.FindQuest(101));
        Assert.Empty(x.S.Char.FindInven(255)!.Items);
    }

    [Theory]
    [InlineData(2, 100, false)] [InlineData(3, 0, false)] [InlineData(3, 100, true)]
    public async Task MailUsesNativeNonSelectedRewardMethods(byte method, byte probability, bool sent)
    {
        var x = await Enter(); MailQuest(x.T, method, probability, 1); x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100, 2, 200));
        Assert.Equal(sent ? 1 : 0, x.Mail.Calls.Count);
        Assert.Equal(sent, x.S.Char!.IsRunningQuest(101));
    }

    [Fact]
    public async Task MailKeepsMagicOptionsDurabilityLifetimeAndOneRandomReward()
    {
        var x = await Enter();
        x.T.QuestItems[300] = new QuestItemRow(300,
            new FullItemRow(0, 0, 0, 200, 7, 1, 2, 400, 350, 3, 0, 4,
                new byte[] { 1, 2, 0, 0, 0, 0 }, new ushort[] { 123, 456, 0, 0, 0, 0 }, new uint[6], 5, 201, 0), 1, 0);
        var q = Quest(100, QuestType.SendPost);
        q.Rewards.Add(new(300, 7, 4, 100, 1)); q.Rewards.Add(new(200, 2, 4, 100, 1)); x.T.Quests[100] = q;
        x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        var item = Assert.Single(Assert.Single(x.Mail.Calls).Items);
        Assert.Equal(((byte)7, (byte)2, 400u, 350u, (byte)3, (byte)4, (byte)5, (ushort)201),
            (item.Level, item.GLevel, item.DuraMax, item.DuraCur, item.RefineCur, item.GradeEffect, item.Gem, item.MoggItemId));
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 0 }, item.Magic);
        Assert.Equal((ushort)456, item.Value[1]);
        // Existing SetEndTime uses the chart's midnight/day rounding, not a rolling 24-hour expiry.
        Assert.InRange(item.EndTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 86400L, 172800L);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task InvalidMailHasNoSideEffects(int scenario)
    {
        var x = await Enter(); MailQuest(x.T);
        if (scenario == 0) x.T.Items.Clear();
        if (scenario == 1) x.T.Quests[100].Conditions.Add(new(2, 5, 0));
        if (scenario == 2) x.S.IsMain = false;
        if (scenario == 3) x.H.Service.QuestPostStore = null;
        x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Empty(x.Mail.Calls); Assert.Null(x.S.Char!.FindQuest(101));
    }

    [Fact]
    public async Task LevelEventSendsMailButClientCannotForgeTheEvent()
    {
        var x = await Enter(); x.T.LevelExp[5] = 100; x.T.LevelExp[6] = 1000;
        var q = Quest(100, QuestType.SendPost, 12, 5); q.Rewards.Add(new(200, 2, 1, 0, 1)); x.T.Quests[100] = q;
        x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Empty(x.Mail.Calls);
        await x.H.Service.DispatchWorldAsync(RewardEvent(500, exp: 100));
        Assert.Equal(6, x.S.Char!.Level); Assert.Single(x.Mail.Calls);
    }

    [Theory]
    [InlineData((byte)3, 1u, true)] [InlineData((byte)0, 0u, true)] [InlineData((byte)0, 1u, false)]
    public async Task CountryConditionUsesWarAllegiance(byte aid, uint wanted, bool accepted)
    {
        var x = await Enter(); x.S.Char!.Country = 1; x.S.Char.AidCountry = aid;
        var q = Quest(100, QuestType.Mission); q.Conditions.Add(new(wanted, 14, 0)); x.T.Quests[100] = q;
        x.H.Service.InitQuests(); await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(accepted, x.S.Char.IsRunningQuest(100));
    }

    [Theory]
    [InlineData((byte)0, true)] [InlineData((byte)1, true)] [InlineData((byte)2, false)]
    public async Task QuestDropsStopWhenTheirOwnFetchObjectiveIsSatisfied(byte held, bool dropped)
    {
        var x = await Enter(); var parent = Quest(100, QuestType.Mission);
        parent.Terms.Add(new(500, 3, 5)); parent.Terms.Add(new(200, 2, 2)); x.T.Quests[100] = parent;
        x.S.Char!.Quests[100] = new QuestProgress { Template = parent, TriggerCount = 1 };
        if (held != 0) x.S.Char.FindInven(255)!.Items.Add(new Item { TemplateId = 200, Count = held, Template = x.T.Items[200] });
        var drop = Quest(101, QuestType.DropItem, 5, 500); drop.Conditions.Add(new(100, 3, 0));
        drop.Terms.Add(new(200, 5, 1)); x.T.Quests[101] = drop;
        x.H.Service.SpawnMonster(new Monster { Id = 0x70001, ChartId = 500, MapId = 0, Channel = 1, PosX = 100, PosZ = 100 });
        x.H.Service.InitQuests(); await x.H.Service.DispatchWorldAsync(RewardEvent(500));
        Assert.Equal(dropped ? 1 : 0, x.H.State.FindMonster(0x70001)!.CorpseInven.Items.Count);
    }

    [Theory]
    [InlineData(0, 100u)] [InlineData(50, 150u)] [InlineData(100, 200u)]
    public async Task QuestExperienceIncludesTheQuestBuff(int percent, uint expected)
    {
        var x = await Enter(); var q = Quest(100, QuestType.Craft); q.Rewards.Add(new(100, 6, 1, 0, 0)); x.T.Quests[100] = q;
        var skill = new SkillTemplate(900, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 0xFFFF);
        skill.Data.Add(new SkillDataRow(3, 6, 0, 63, 0, (ushort)percent, 0, 0)); x.T.Skills[900] = skill;
        x.S.Char!.MaintainSkills.Add(new MaintainSkill { SkillId = 900, Template = skill });
        x.H.Service.InitQuests(); await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Equal(expected, x.S.Char.Exp);
    }

    [Fact]
    public async Task PositionTriggerRunsOnlyInsideItsConditionsAndDoesNotRepeatAnActiveMission()
    {
        var x = await Enter(); var q = Quest(100, QuestType.Mission, 2, 0);
        q.Conditions.Add(new(110, 8, 0)); q.Conditions.Add(new(120, 10, 0)); x.T.Quests[100] = q;
        x.H.Service.InitQuests();
        foreach (float position in new[] { 100f, 115f, 116f })
            await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.MoveReq(0, position, 0, 100, 0, 1));
        Assert.Equal(1, x.S.Char!.Quests[100].TriggerCount);
    }

    [Fact]
    public async Task CyclicBaseQuestChainCannotOverflowTheServerStack()
    {
        var x = await Enter(); x.T.Quests[100] = Quest(100, QuestType.None, 1, 101);
        x.T.Quests[101] = Quest(101, QuestType.None, 1, 100); x.H.Service.InitQuests();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.Empty(x.S.Char!.Quests); Assert.Empty(x.Mail.Calls);
    }

    [Fact]
    public async Task SavedHuntProgressResumesAndTurnInIsNotRewardedTwice()
    {
        var x = await Enter(); var mission = Quest(100, QuestType.Mission); mission.Terms.Add(new(500, 3, 2));
        mission.Rewards.Add(new(50, 1, 1, 0, 0)); x.T.Quests[100] = mission;
        var turn = Quest(101, QuestType.Complete); turn.Terms.Add(new(100, 1, 0)); x.T.Quests[101] = turn;
        x.H.Service.InitQuests(); await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        await x.H.Service.DispatchWorldAsync(RewardEvent(500));
        var row = Assert.Single(MapService.BuildQuestSaves(x.S.Char!, x.H.Service.NowMs));
        x.S.Char!.Quests.Clear();
        x.H.Service.LoadQuestProgress(x.S.Char, new[] { new QuestLoadRow(row.QuestId, row.Tick, row.CompleteCount, row.TriggerCount) },
            row.Terms.Select(t => new QuestTermLoadRow(row.QuestId, t.TermId, t.TermType, t.Count)).ToList());
        await x.H.Service.DispatchWorldAsync(RewardEvent(500));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal(50L, x.S.Char.MoneyTotal); Assert.Equal(1, x.S.Char.Quests[100].CompleteCount);
        Assert.Equal(1, Assert.Single(MapService.BuildQuestSaves(x.S.Char, x.H.Service.NowMs)).CompleteCount);
    }

    [Theory]
    [InlineData(15f, (byte)1, (ushort)0, 1)]
    [InlineData(16f, (byte)1, (ushort)0, 0)]
    [InlineData(5f, (byte)2, (ushort)0, 0)]
    [InlineData(5f, (byte)1, (ushort)1, 0)]
    public async Task InstanceObjectiveUsesSpawnAnchorsWithinSixteenUnitsOnTheSameMapAndChannel(
        float distance, byte channel, ushort map, int expected)
    {
        var x = await Enter(); var q = Quest(100, QuestType.Mission); q.Terms.Add(new(600, 21, 3));
        x.S.Char!.Quests[100] = new QuestProgress { Template = q, TriggerCount = 1 };
        // The running term stores the required monster kind; event ids may differ (native type-21 wildcard).
        x.S.Char.Quests[100].RunningTerms.Add(new RunningTerm { TermId = 600, TermType = 21 });
        foreach (var (spawn, px) in new[] { ((ushort)7, 100f), ((ushort)8, 100f + distance) })
            x.T.MonsterSpawns.Add(new(new MonSpawnRow(spawn, map, px, 0, 100, 0, 3, 1, 0, 100, 0, 0, 0), new()));
        x.H.Service.SpawnMonster(new Monster { Id = 0x70001, ChartId = 500, MapId = 0, Channel = 1 });
        x.H.Service.SpawnMonster(new Monster { Id = 0x80001, ChartId = 600, MapId = map, Channel = channel });
        await x.H.Service.DispatchWorldAsync(RewardEvent(999, termType: 21, count: 0));
        Assert.Equal(expected, x.S.Char.Quests[100].RunningTerms.Single().Count);
    }

    [Fact]
    public async Task LeavingMapRunsTheOldMapTriggerBeforeTakingTheDestination()
    {
        var x = await Enter(); x.T.Quests[100] = Quest(100, QuestType.Mission, 11, 0);
        x.H.Service.InitQuests();
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_STARTTELEPORT_REQ).WriteUInt32(1).WriteUInt32(11)
            .WriteByte(1).WriteUInt16(3).WriteFloat(900).WriteFloat(0).WriteFloat(700).ToArray());
        Assert.True(x.S.Char!.IsRunningQuest(100)); Assert.Equal(3, x.S.Char.MapId);
    }

    [Fact]
    public async Task LogoutRunsTheLeaveTriggerAndCommitsMailBeforeRemovingTheSession()
    {
        var x = await Enter(); var mail = Quest(100, QuestType.SendPost, 11, 0);
        mail.Rewards.Add(new(200, 2, 1, 0, 1)); x.T.Quests[100] = mail;
        x.T.Quests[101] = Quest(101, QuestType.Mission, 1, 100); x.H.Service.InitQuests();
        await x.H.Service.OnClientDisconnectAsync(x.S);
        Assert.Single(x.Mail.Calls); Assert.True(x.S.Char!.IsRunningQuest(101));
        Assert.Null(x.H.State.FindByChar(1));
    }

    [Fact]
    public async Task RoutedRegenUsesTheCarriedDeathPositionWhenTheCorpseIsOnAnotherMapServer()
    {
        var x = await Enter(); x.S.Char!.PosX = 200; x.S.Char.PosZ = 210;
        var q = Quest(101, QuestType.Regen, 5, 500); q.Terms.Add(new(600, 7, 0)); x.T.Quests[101] = q;
        x.T.MonsterTemplates[600] = new MonsterTemplate(600, 5, 0);
        x.T.MonAttrs[TemplateStore.MonAttrKey(0, 5)] = new MonAttrRow(0, 5, 100, 50, 100);
        x.H.Service.InitQuests(); await x.H.Service.DispatchWorldAsync(RewardEvent(500));
        var replacement = Assert.Single(x.H.State.AllMonsters(), m => m.ChartId == 600);
        Assert.Equal((100f, 100f), (replacement.PosX, replacement.PosZ));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PartyKillRunsRegenQuestOnceButOrdinaryHuntsForEveryMember(bool regen)
    {
        var x = await Enter(); var (second, _) = await x.H.EnterAsync(2, 2, 22, x: 100, z: 100);
        foreach (var pc in new[] { x.S.Char!, second.Char! })
        {
            pc.Level = 5; pc.PartyId = 42;
            var q = Quest(100, QuestType.Mission); q.Terms.Add(new(500, 3, 5));
            pc.Quests[100] = new QuestProgress { Template = q, TriggerCount = 1 };
        }
        if (regen)
        {
            var q = Quest(101, QuestType.Regen, 5, 500); q.Terms.Add(new(600, 7, 0)); x.T.Quests[101] = q;
            x.T.MonsterTemplates[600] = new MonsterTemplate(600, 5, 0);
            x.T.MonAttrs[TemplateStore.MonAttrKey(0, 5)] = new MonAttrRow(0, 5, 100, 50, 100);
        }
        x.H.Service.InitQuests(); x.H.Service.CombatRng = new Random(1);
        var mon = new Monster { Id = 0x70001, ChartId = 500, Level = 5, Hp = 1, MaxHp = 1,
            Country = 3, AidCountry = 3, Channel = 1, MapId = 0, PosX = 100, PosZ = 100 };
        x.H.Service.SpawnMonster(mon);
        for (int i = 0; i < 10 && mon.Hp != 0; i++) await x.H.CastAndHitAsync(x.S, MapTestHarness.DefendReq(1, mon.Id));
        Assert.Equal(0u, mon.Hp);
        Assert.Equal(1, x.S.Char!.Quests[100].RunningTerms.Single().Count);
        Assert.Equal(regen ? 0 : 1, second.Char!.Quests[100].RunningTerms.FirstOrDefault()?.Count ?? 0);
        if (regen) Assert.Single(x.H.State.AllMonsters(), m => m.ChartId == 600);
    }

    private static void TimedQuest(TemplateStore t)
    {
        var mission = Quest(100, QuestType.Mission); mission.Terms.Add(new(1000, 6, 0));
        mission.Rewards.Add(new(50, 1, 1, 0, 0)); t.Quests[100] = mission;
        var turn = Quest(101, QuestType.Complete); turn.Terms.Add(new(100, 1, 0)); t.Quests[101] = turn;
    }

    [Fact]
    public async Task TimerStartedAtServerTickZeroStillSavesItsRemainingTime()
    {
        var x = await Enter(); TimedQuest(x.T); x.H.Service.InitQuests(); x.H.Service.NowMs = 0;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.True(x.S.Char!.Quests[100].HasTimer);
        Assert.Equal(500u, Assert.Single(MapService.BuildQuestSaves(x.S.Char, 500)).Tick);
    }

    [Theory]
    [InlineData(1000u, true)] [InlineData(1001u, false)]
    public async Task TimerStartedAtZeroUsesTheExistingStrictExpiryBoundary(uint now, bool success)
    {
        var x = await Enter(); TimedQuest(x.T); x.H.Service.InitQuests(); x.H.Service.NowMs = 0;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100)); x.H.Service.NowMs = now;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal(success ? 50L : 0L, x.S.Char!.MoneyTotal);
        Assert.Equal(success ? (byte)QuestResult.Success : (byte)QuestResult.Term,
            new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        if (success) Assert.False(x.S.Char.Quests[100].HasTimer);
    }

    [Theory]
    [InlineData(0u)] [InlineData(100u)]
    public async Task ExpiredSavedTimerCannotBecomeAnUntimedQuestAfterReload(uint loadTime)
    {
        var x = await Enter(); TimedQuest(x.T); x.H.Service.InitQuests(); x.H.Service.NowMs = loadTime;
        x.H.Service.LoadQuestProgress(x.S.Char!, new[] { new QuestLoadRow(100, 0, 0, 1) }, Array.Empty<QuestTermLoadRow>());
        Assert.True(x.S.Char!.Quests[100].HasTimer); x.H.Service.NowMs = loadTime + 1;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Term, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(0L, x.S.Char.MoneyTotal); Assert.Equal(0u, Assert.Single(MapService.BuildQuestSaves(x.S.Char, loadTime + 1)).Tick);
    }

    [Fact]
    public async Task AbandoningClearsTheTimerAndReacceptingStartsANewOne()
    {
        var x = await Enter(); TimedQuest(x.T); x.H.Service.InitQuests(); x.H.Service.NowMs = 0;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestDropReq(100));
        Assert.False(x.S.Char!.Quests[100].HasTimer); x.H.Service.NowMs = 500;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(100));
        Assert.True(x.S.Char.Quests[100].HasTimer);
        Assert.Equal(900u, Assert.Single(MapService.BuildQuestSaves(x.S.Char, 600)).Tick);
    }

    private static byte[] RewardEvent(ushort term, uint exp = 0, byte termType = 3, byte count = 1)
        => new PacketWriter(Msg.MW_MONSTERDIE_REQ).WriteUInt32(1).WriteUInt32(11).WriteUInt32(exp).WriteUInt32(0x70001)
            .WriteFloat(100).WriteFloat(0).WriteFloat(100).WriteUInt16(term).WriteByte(termType).WriteByte(5).WriteByte(count).ToArray();
}
