using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class QuestCheckpointTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly MemorySaveQueueStore Store = new();
        public readonly TemplateStore T = new();
        public readonly MapState State = new();
        public readonly FakeClientChannel Client = new();
        public readonly MapService Service;
        public readonly ClientSession S;
        private readonly IDisposable Lease;
        public Character Ch => S.Char!;
        public Fixture(bool main = true, bool loaded = true, bool seeded = true)
        {
            var opt = new TMap.Server.MapServerOptions();
            opt.Db.GameConnectionString = "Server=127.0.0.1,1;Database=4DevQuestTest_unreachable;User Id=fixture;Password=unused;Encrypt=False;Connect Timeout=1;ConnectRetryCount=0;Pooling=False";
            Service = new(opt, State, new FakeWorldSink(), new GameDatabase(opt.Db.GameConnectionString), T, NullLogger<MapService>.Instance);
            Service.SaveQueueStore = Store;
            Lease = Service.OpenSaveQueue()!;
            if (seeded) Service.ApplyItemIdSeed(1000000000);
            T.Items[200] = new(200, 0, new float[4], Stack: 10, ClassId: uint.MaxValue);
            T.Items[201] = new(201, 0, new float[4], Stack: 10, ClassId: uint.MaxValue);
            T.LevelExp[5] = 1000000;
            T.Skills[300] = new(300, 0, 0, 0, 0, 0, 1, 10, 1, 0, 0, 0, 0, 0, 1, 0, ClassId: uint.MaxValue);
            var ch = new Character { CharId = 1, Name = "QuestFixture", DbLoaded = loaded, Class = 0, Country = 1,
                AidCountry = 3, Level = 5, Hp = 100, MaxHp = 100, PosX = 100, PosZ = 100 };
            ch.Invens.Add(new Inven { InvenId = 255 });
            S = new(Client) { CharId = 1, UserId = 9, Key = 11, Channel = 1, IsMain = main, State = EnterState.InGame, Char = ch };
            State.Register(S);
            Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 });
        }
        public QuestTemplate Add(uint id, QuestType type, byte trigger = 3, uint triggerId = 500, uint parent = 0)
        {
            var q = new QuestTemplate { QuestId = id, Type = (byte)type, TriggerType = trigger, TriggerId = triggerId, ParentId = parent, CountMax = 1 };
            T.Quests[id] = q; return q;
        }
        public async Task Execute(uint id)
        { Service.InitQuests(); await Service.DispatchClientAsync(S, MapTestHarness.QuestExecReq(id)); }
        public List<CharacterSaveSnapshot> Snapshots()
        {
            var result = new List<CharacterSaveSnapshot>();
            foreach (var row in Store.State.Rows.Values.ToArray())
            {
                var entry = JsonSerializer.Deserialize<SaveQueueEntry>(row.Payload, SqlSaveQueue.Json)!;
                if (entry.Character is { } snapshot) result.Add(snapshot);
            }
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            try { await Service.RecoverPendingSavesAsync(); } catch (IOException) { }
            Lease.Dispose();
        }
    }

    [Fact]
    public async Task AcceptanceAndChildMissionsAreDurableAtDispatchEndWithoutLogout()
    {
        await using var f = new Fixture();
        f.Add(100, QuestType.Mission);
        f.Add(101, QuestType.Mission, 1, 100);
        await f.Execute(100);
        var saved = Assert.Single(f.Snapshots());
        Assert.Equal(new uint[] { 100, 101 }, saved.Quests.Select(q => q.QuestId));
        Assert.All(saved.Quests, q => { Assert.Equal(1, q.TriggerCount); Assert.Equal(0, q.CompleteCount); });
        Assert.All(f.Ch.Quests.Values, q => Assert.False(q.Save));
        Assert.True(f.Client.Has(Msg.CS_QUESTADD_ACK));
        await Assert.ThrowsAsync<IOException>(() => f.Service.RecoverPendingSavesAsync());
        Assert.Single(f.Snapshots()); // SQL failure retains the accepted mission/chain
    }

    [Fact]
    public async Task TurnInSnapshotsCompletionRewardsConsumedItemsAndNextMissionTogether()
    {
        await using var f = new Fixture(); var mission = f.Add(100, QuestType.Mission);
        mission.Terms.Add(new(200, 2, 1)); mission.Rewards.Add(new(201, 2, 1, 0, 2));
        mission.Rewards.Add(new(50, 1, 1, 0, 0)); mission.Rewards.Add(new(300, 3, 1, 0, 0));
        f.Add(101, QuestType.Complete, parent: 100).Terms.Add(new(100, 1, 0));
        f.Add(102, QuestType.Mission, 1, 101, parent: 100);
        await f.Execute(100);
        f.Ch.FindInven(255)!.Items.Add(new Item { TemplateId = 200, Template = f.T.Items[200], Count = 1, ItemSlot = 0, DlId = 900000001 });
        await f.Execute(101);
        var saves = f.Snapshots(); Assert.Equal(2, saves.Count); var saved = saves.Last();
        Assert.Equal((byte)1, saved.Quests.Single(q => q.QuestId == 100).CompleteCount);
        Assert.Equal((byte)1, saved.Quests.Single(q => q.QuestId == 102).TriggerCount);
        Assert.DoesNotContain(saved.Items!, i => i.TemplateId == 200);
        Assert.Equal((byte)2, Assert.Single(saved.Items!, i => i.TemplateId == 201).Count);
        Assert.Equal(50u, saved.Character.Cooper);
        Assert.Equal((byte)1, Assert.Single(saved.Skills, k => k.SkillId == 300).Level);
        await f.Execute(101); Assert.Equal(2, f.Snapshots().Count);
    }

    [Fact]
    public async Task RepeatableCraftSnapshotsRewardsEvenWithoutAStoredMission()
    {
        await using var f = new Fixture(); var craft = f.Add(100, QuestType.Craft);
        craft.Terms.Add(new(200, 2, 1)); craft.Rewards.Add(new(20, 1, 1, 0, 0));
        f.Ch.FindInven(255)!.Items.Add(new Item { TemplateId = 200, Template = f.T.Items[200], Count = 2, ItemSlot = 0, DlId = 900000001 });
        await f.Execute(100); await f.Execute(100);
        var saved = f.Snapshots(); Assert.Equal(2, saved.Count);
        Assert.Empty(saved.Last().Quests); Assert.Empty(saved.Last().Items!);
        Assert.Equal(40u, saved.Last().Character.Cooper);
    }

    [Fact]
    public async Task WorldHuntAndClientPositionProgressAreCheckpointed()
    {
        await using var f = new Fixture(); f.Add(100, QuestType.Mission).Terms.Add(new(500, 3, 3));
        await f.Execute(100);
        await f.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_MONSTERDIE_REQ).WriteUInt32(1).WriteUInt32(11).WriteUInt32(0).WriteUInt32(0x70001)
            .WriteFloat(100).WriteFloat(0).WriteFloat(100).WriteUInt16(500).WriteByte(3).WriteByte(5).WriteByte(1).ToArray());
        Assert.Equal(1, Assert.Single(f.Snapshots().Last().Quests.Single().Terms).Count);
        await f.Service.DispatchClientAsync(f.S, new PacketWriter(Msg.CS_QUESTPOSEXEC_REQ).WriteUInt32(100).WriteUInt32(500).ToArray());
        Assert.Equal(3, f.Snapshots().Count);
    }

    [Fact]
    public async Task TimerFailureAndAbandonmentPersistAtTheirEventBoundary()
    {
        await using var f = new Fixture(); f.Add(100, QuestType.Mission).Terms.Add(new(100, 6, 0));
        await f.Execute(100); await f.Service.OnTimerAsync();
        Assert.Equal(0u, f.Snapshots().Last().Quests.Single().Tick);
        await f.Service.DispatchClientAsync(f.S, new PacketWriter(Msg.CS_QUESTDROP_REQ).WriteUInt32(100).ToArray());
        Assert.Equal(0, f.Snapshots().Last().Quests.Single().TriggerCount);
        Assert.Equal(3, f.Snapshots().Count);
    }

    [Theory]
    [InlineData(QuestType.GiveItem)] [InlineData(QuestType.DeleteItem)] [InlineData(QuestType.GiveSkill)]
    public async Task DirectQuestActionsWithoutMissionCountersStillCheckpoint(QuestType type)
    {
        await using var f = new Fixture(); var q = f.Add(100, type);
        q.Terms.Add(type == QuestType.GiveSkill ? new(300, 4, 1) : new(200, 5, 1));
        if (type == QuestType.DeleteItem)
            f.Ch.FindInven(255)!.Items.Add(new Item { TemplateId = 200, Template = f.T.Items[200], Count = 1, ItemSlot = 0, DlId = 900000001 });
        await f.Execute(100); var snapshot = Assert.Single(f.Snapshots()); Assert.Empty(snapshot.Quests);
        if (type == QuestType.GiveItem) Assert.Equal((ushort)200, Assert.Single(snapshot.Items!).TemplateId);
        if (type == QuestType.DeleteItem) Assert.Empty(snapshot.Items!);
        if (type == QuestType.GiveSkill) Assert.Equal((ushort)300, Assert.Single(snapshot.Skills).SkillId);
    }

    [Theory]
    [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, false)]
    public async Task ReplicasSyntheticCharactersAndUnseededInventoryCannotCreateUnsafeCheckpoints(bool main, bool loaded, bool seeded)
    {
        await using var f = new Fixture(main, loaded, seeded); f.Add(100, QuestType.Mission);
        await f.Execute(100); Assert.Empty(f.Snapshots());
        if (!main || !seeded) Assert.Empty(f.Ch.Quests);
    }

    [Fact]
    public async Task FailedRequirementsAndReadOnlyRequestsDoNotCreateCheckpoints()
    {
        await using var f = new Fixture(); var q = f.Add(100, QuestType.Craft);
        q.Terms.Add(new(200, 2, 1)); q.Rewards.Add(new(50, 1, 1, 0, 0));
        await f.Execute(100); Assert.Empty(f.Snapshots());
        Assert.Equal(0L, f.Ch.MoneyTotal);
        f.Add(101, QuestType.NpcTalk); await f.Execute(101); Assert.Empty(f.Snapshots());
    }

    [Fact]
    public async Task JournalFailureDoesNotSendQuestSuccessAndQuarantinesTheSession()
    {
        await using var f = new Fixture(); f.Add(100, QuestType.Mission);
        f.Store.FailAppend = true;
        await Assert.ThrowsAsync<IOException>(() => f.Execute(100));
        Assert.True(f.Client.Closed); Assert.Equal(EnterState.Entering, f.S.State);
        Assert.False(f.Client.Has(Msg.CS_QUESTADD_ACK)); Assert.True(f.Ch.Quests[100].Save);
        f.Store.FailAppend = false;
    }

    [Fact]
    public async Task BufferedQuestRepliesKeepTheirOriginalOrderAfterCheckpoint()
    {
        await using var f = new Fixture(); var q = f.Add(100, QuestType.Craft);
        q.Rewards.Add(new(50, 1, 1, 0, 0)); f.Add(101, QuestType.Mission, 1, 100);
        await f.Execute(100);
        var ids = f.Client.Sent.Select(p => PacketHeader.ReadId(p)).ToList();
        Assert.True(ids.IndexOf(Msg.CS_MONEY_ACK) < ids.IndexOf(Msg.CS_QUESTCOMPLETE_ACK));
        Assert.True(ids.IndexOf(Msg.CS_QUESTCOMPLETE_ACK) < ids.IndexOf(Msg.CS_QUESTADD_ACK));
        Assert.Single(f.Snapshots());
    }
}
