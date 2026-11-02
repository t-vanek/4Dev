using TMap.Data;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class SqlSaveQueueTests
{
    private readonly MemorySaveQueueState _state = new();
    private SqlSaveQueue Open(string identity = "fixture") => new(new MemorySaveQueueStore(_state), identity);
    private static ItemSaveData Item(long id = 1) => new(id, 0, 255, 2, 200, 7, 3, 4, 500, 450, 6, 0, 8,
        [1, 2, 3, 4, 5, 6], [101, 102, 103, 104, 105, 106], [11, 12, 13, 14, 15, 16], 9, 201);
    private static SaveQueueEntry Direct(long id) => SaveQueueEntry.Direct([new(17, Item(id))], []);

    private static SaveQueueEntry Exchange()
    {
        CharacterSaveSnapshot Snapshot(uint id) => new(MapService.BuildCharSave(new Character { CharId = id }), [],
            [new(255, 0, 0, 0)], [Item(id)], [], [], [], 255, 0, []);
        return SaveQueueEntry.PlayerExchange([Snapshot(1), Snapshot(2)], [], []);
    }

    [Fact]
    public async Task ExchangeInterruptedAfterOneParticipantReplaysBothBeforeLaterRecords()
    {
        using (var queue = Open())
        {
            var entry = Exchange(); queue.Append(entry); queue.Append(Direct(3));
            entry.Exchange![0].Items![0].Ext[0] = 999;
            await Assert.ThrowsAsync<IOException>(() => queue.DrainAsync(queue.Boundary, e =>
            {
                Assert.Equal(11u, e.Exchange![0].Items![0].Ext[0]);
                throw new IOException("Second participant failed after the first committed");
            }));
            Assert.Equal(2, queue.PendingCount);
        }
        using var recovered = Open(); var order = new List<uint>();
        await recovered.DrainAsync(recovered.Boundary, e =>
        {
            if (e.Exchange is { } pair) order.AddRange(pair.Select(s => s.Character.CharId)); else order.Add(3);
            return Task.CompletedTask;
        });
        Assert.Equal(new uint[] { 1, 2, 3 }, order); Assert.Equal(0, recovered.PendingCount);
    }

    [Theory]
    [InlineData("one participant")]
    [InlineData("duplicate participant")]
    [InlineData("missing inventory")]
    [InlineData("shared item identity")]
    [InlineData("missing item identity")]
    [InlineData("wrong version")]
    [InlineData("missing operations")]
    public void IncompleteOrAmbiguousExchangeCannotEnterTheDurableQueue(string fault)
    {
        using var queue = Open(); var entry = Exchange();
        entry = fault switch
        {
            "one participant" => entry with { Exchange = [entry.Exchange![0]] },
            "duplicate participant" => entry with { Exchange = [entry.Exchange![0], entry.Exchange[0]] },
            "missing inventory" => entry with { Exchange = [entry.Exchange![0], entry.Exchange[1] with { Inventory = null, Items = null }] },
            "shared item identity" => entry with { Exchange = [entry.Exchange![0], entry.Exchange[1] with { Items = [Item(1)] }] },
            "missing item identity" => entry with { Exchange = [entry.Exchange![0], entry.Exchange[1] with { Items = [Item(0)] }] },
            "wrong version" => entry with { Version = 1 },
            "missing operations" => entry with { Upserts = null },
            _ => throw new ArgumentException(fault)
        };
        Assert.Throws<InvalidDataException>(() => queue.Append(entry)); Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task FailedHeadSurvivesReopen_AndLaterUpdatesCannotOvertakeIt()
    {
        using (var journal = Open())
        {
            journal.Append(Direct(1)); journal.Append(Direct(2));
            var attempted = new List<long>();
            await Assert.ThrowsAsync<IOException>(() => journal.DrainAsync(journal.Boundary, e =>
            {
                attempted.Add(e.Upserts![0].Item.DlId);
                throw new IOException("SQL unavailable");
            }));
            Assert.Equal(new long[] { 1 }, attempted);
            Assert.Equal(2, journal.PendingCount);
        }
        using (var restarted = Open())
        {
            var applied = new List<long>();
            await restarted.DrainAsync(restarted.Boundary, e => { applied.Add(e.Upserts![0].Item.DlId); return Task.CompletedTask; });
            Assert.Equal(new long[] { 1, 2 }, applied);
            Assert.Equal(0, restarted.PendingCount);
        }
        using var empty = Open(); Assert.Equal(0, empty.PendingCount);
    }

    [Fact]
    public async Task EnqueueBoundaryDoesNotReplayFutureSnapshotsAheadOfAnOlderDbOperation()
    {
        using var journal = Open();
        long boundary = journal.Append(Direct(1));
        journal.Append(Direct(2));
        var order = new List<long>();
        Task Apply(SaveQueueEntry e) { order.Add(e.Upserts![0].Item.DlId); return Task.CompletedTask; }
        await journal.DrainAsync(boundary, Apply);
        order.Add(99); // a non-journaled operation already queued before record 2
        await journal.DrainAsync(journal.Boundary, Apply);
        Assert.Equal(new long[] { 1, 99, 2 }, order);
    }

    [Fact]
    public async Task CharacterSnapshotFreezesEveryPayloadIncludingTupleFieldsAndRewardInventory()
    {
        var ch = new Character { CharId = 17, Gold = 23, Exp = 42, MapId = 10, PosX = 3.5f };
        ch.Titles[500] = true;
        var item = Item();
        var data = MapService.BuildCharSave(ch) with
        {
            Recalls = [new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13)],
            RecallMaintains = [new(1, 2, 3, 4, 5, 6, 7, 8, 9)],
            Maintains = [new(1, 2, 3, 4, 5, 6, 7, 8)],
            Premium = new(2, 1, 300, 999), ExpItem = new(3, 2, 400, 1000), SaveDuring = true
        };
        var entry = SaveQueueEntry.Full(new(data, [new(100, 300, 1, 2, [new(99, 3, 4)])],
            [new(255, 10, 0, 2)], [item], [new(2, 7, [(1, 600)])], [new(5, "Pet", 99, 2)],
            [new(1, 2, 3, "Companion", 4, 5, 6, 7, [8, 9], 10, [11, 12], [13, 14], 15)],
            1, 44, [new(700, 2, 1000)]));
        var expected = System.Text.Json.JsonSerializer.Serialize(entry, SqlSaveQueue.Json);
        using var journal = Open(); journal.Append(entry);
        item.Ext[0] = 999; item.Magic[0] = 99; ch.Titles.Clear();
        await journal.DrainAsync(journal.Boundary, actual =>
        {
            Assert.Equal(expected, System.Text.Json.JsonSerializer.Serialize(actual, SqlSaveQueue.Json));
            Assert.Equal((Id: (ushort)500, Selected: true), Assert.Single(actual.Character!.Character.Titles!));
            Assert.Equal(((byte)1, (ushort)600), actual.Character.Hotkeys[0].Slots[0]);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AmbiguousCommitRetainsRecordForIdempotentAbsoluteReplay()
    {
        using var journal = Open(); journal.Append(Direct(1));
        int count = 0;
        await Assert.ThrowsAsync<IOException>(() => journal.DrainAsync(journal.Boundary, _ =>
        { count = 3; throw new IOException("Connection lost after commit"); }));
        Assert.Equal(1, journal.PendingCount);
        await journal.DrainAsync(journal.Boundary, _ => { count = 3; return Task.CompletedTask; });
        Assert.Equal(3, count); Assert.Equal(0, journal.PendingCount);
    }

    [Fact]
    public void CorruptionOrDifferentDatabasePreventsRecovery()
    {
        using (var journal = Open()) journal.Append(Direct(1));
        Assert.Throws<InvalidDataException>(() => Open("other database"));
        var row = Assert.Single(_state.Rows.Values);
        _state.Rows[row.Sequence] = row with { Checksum = "bad" };
        Assert.Throws<InvalidDataException>(() => Open());
        Assert.Single(_state.Rows);
    }

    [Fact]
    public async Task SqlWriterIsExclusiveAndAFailedEnqueueIsNotAcknowledged()
    {
        using var journal = Open(); journal.Append(Direct(1));
        Assert.Throws<IOException>(() => Open());
        await journal.DrainAsync(journal.Boundary, _ => Task.CompletedTask);
        Assert.Equal(0, journal.PendingCount);
        var store = new MemorySaveQueueStore { FailAppend = true };
        using var unavailable = new SqlSaveQueue(store, "fixture");
        Assert.Throws<IOException>(() => unavailable.Append(Direct(2)));
        Assert.Equal(0, unavailable.Boundary); Assert.Equal(0, unavailable.PendingCount);
    }

    [Fact]
    public async Task LeaseLossBetweenApplicationAndAcknowledgementKeepsTheSqlRecord()
    {
        var store = new MemorySaveQueueStore();
        using var queue = new SqlSaveQueue(store, "fixture"); queue.Append(Direct(1));
        await Assert.ThrowsAsync<IOException>(() => queue.DrainAsync(queue.Boundary, _ =>
        { store.LoseLease = true; return Task.CompletedTask; }));
        Assert.Single(store.State.Rows);
        Assert.Throws<IOException>(() => queue.Append(Direct(2)));
    }

    [Fact]
    public void InvalidSchemaIsRejectedBeforeAcknowledgement()
    {
        using var journal = Open();
        Assert.Throws<InvalidDataException>(() => journal.Append(Direct(1) with { Version = 2 }));
        Assert.Throws<InvalidDataException>(() => journal.Append(new(1, null, null, null)));
        Assert.Throws<InvalidDataException>(() => journal.Append(new(1, null, [], null)));
        Assert.Equal(0, journal.PendingCount);
    }

    [Fact]
    public void MissingIdentityCannotAttachExistingSavesToAnotherDatabase()
    {
        using (var journal = Open()) journal.Append(Direct(1));
        _state.Identity = null;
        Assert.Throws<InvalidDataException>(() => Open("different database"));
        Assert.Single(_state.Rows);
    }

    [Fact]
    public void SnapshotWithoutAcknowledgementKeepsDirtyQuestAndHotkeyFlags()
    {
        var ch = new Character { CharId = 17 };
        var q = new QuestProgress { Template = new QuestTemplate { QuestId = 100 }, Save = true };
        ch.Quests[100] = q;
        var page = new HotkeyPage { InvenKey = 7, Save = HotkeyPage.SaveInsert };
        page.Slots[0] = new(1, 600); ch.HotkeyPages.Add(page);
        Assert.Single(MapService.BuildQuestSaves(ch, 0, clearFlags: false));
        Assert.Single(MapService.BuildHotkeySaves(ch, clearFlags: false));
        Assert.True(q.Save); Assert.Equal(HotkeyPage.SaveInsert, page.Save);
    }

    [Fact]
    public async Task LogoutRetainsFullSnapshotAfterSqlFailure_AndAdmissionBarrierRejectsPartialState()
    {
        var options = new TMap.Server.MapServerOptions();
        options.Db.GameConnectionString = "Server=127.0.0.1,1;Database=4DevQuestTest_unreachable;User Id=fixture;Password=unused;Encrypt=False;Connect Timeout=1;ConnectRetryCount=0;Pooling=False";
        var state = new MapState();
        var service = new MapService(options, state, new FakeWorldSink(), new GameDatabase(options.Db.GameConnectionString),
            new TemplateStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MapService>.Instance);
        service.SaveQueueStore = new MemorySaveQueueStore(_state);
        using var lease = service.OpenSaveQueue();
        service.ApplyItemIdSeed(1000);
        var ch = new Character { CharId = 17, DbLoaded = true, Gold = 23, Exp = 42 };
        ch.Quests[100] = new() { Template = new() { QuestId = 100 }, Save = true, CompleteCount = 1, TriggerCount = 1 };
        var session = new ClientSession(new FakeClientChannel()) { CharId = 17, IsMain = true, State = EnterState.InGame, Char = ch };
        state.Register(session);
        service.OnClientDisconnect(session);
        Assert.Null(state.FindByChar(17)); // the source object no longer owns the only copy
        Assert.False(ch.Quests[100].Save);
        await Assert.ThrowsAsync<IOException>(() => service.RecoverPendingSavesAsync());
        Assert.Equal(1, ((SqlSaveQueue)lease!).PendingCount);
        var record = Assert.Single(((SqlSaveQueue)lease!).ReadPending());
        Assert.Equal(23u, record.Character!.Character.Gold);
        Assert.Equal(42u, record.Character.Character.Exp);
        Assert.Equal((byte)1, Assert.Single(record.Character.Quests).CompleteCount);
        Assert.NotNull(record.Character.Inventory);
        Assert.NotNull(record.Character.Items);
    }

    [Fact]
    public void ConfiguredSqlCannotStartThroughDbFreeFallback()
    {
        var options = new TMap.Server.MapServerOptions();
        options.Db.GameConnectionString = "Server=unavailable;Database=fixture";
        var service = new MapService(options, new MapState(), new FakeWorldSink(), null, new TemplateStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MapService>.Instance);
        Assert.Throws<InvalidOperationException>(() => service.OpenSaveQueue());
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task ShutdownPreservesMainDbLoadedAndTournamentGates(bool main, bool loaded, bool tournament)
    {
        var options = new TMap.Server.MapServerOptions();
        options.Db.GameConnectionString = "Server=127.0.0.1,1;Database=4DevQuestTest_unreachable;Connect Timeout=1;ConnectRetryCount=0";
        var state = new MapState();
        var service = new MapService(options, state, new FakeWorldSink(), new GameDatabase(options.Db.GameConnectionString),
            new TemplateStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MapService>.Instance);
        service.SaveQueueStore = new MemorySaveQueueStore(_state);
        using var lease = service.OpenSaveQueue();
        var ch = new Character { CharId = 17, DbLoaded = loaded, LastSaveMs = 123,
            TournamentBackup = tournament ? new MapService.TournamentBackup() : null };
        ch.Quests[100] = new() { Template = new() { QuestId = 100 }, Save = true };
        state.Register(new ClientSession(new FakeClientChannel()) { CharId = 17, IsMain = main, State = EnterState.InGame, Char = ch });
        await service.SaveAllCharDataAsync();
        Assert.Equal(0, ((SqlSaveQueue)lease!).PendingCount);
        Assert.True(ch.Quests[100].Save);
        Assert.Equal(123u, ch.LastSaveMs);
    }

    private static CharacterSaveSnapshot PostSnapshot(uint owner, List<QuestPostIntent>? requests, List<Guid>? receipts)
        => new(MapService.BuildCharSave(new Character { CharId = owner }), [], [], [], [], [], [], 255, 0, [], requests, receipts);

    [Fact]
    public async Task MailIntentsAndReceiptsAreFrozenTogetherAndSurviveReopen()
    {
        var id = Guid.NewGuid();
        var request = new QuestPostIntent(id, 100, 17, 0, 45, 2, 1, 3, 4, 5, new string('A', 64), [99], [Item()]);
        using (var journal = Open())
        {
            var entry = SaveQueueEntry.Full(PostSnapshot(17, [request], [id]), [new(17, Item(55))], [56]);
            Assert.Equal(2, entry.Version); journal.Append(entry);
            request.Items[0].Ext[0] = 999; request.Ancestors.Clear();
        }
        using var reopened = Open();
        await reopened.DrainAsync(reopened.Boundary, e =>
        {
            var actual = Assert.Single(e.Character!.QuestPostRequests!);
            Assert.Equal(id, Assert.Single(e.Character.QuestPostReceipts!));
            Assert.Equal(11u, actual.Items[0].Ext[0]); Assert.Equal(99u, Assert.Single(actual.Ancestors));
            Assert.Equal((ushort)2, actual.MapId); Assert.Equal(45u, actual.Target);
            Assert.Equal(55L, Assert.Single(e.Upserts!).Item.DlId);
            Assert.Equal(56L, Assert.Single(e.Deletes!));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void UnsafeMailMetadataCannotBePublished(int kind)
    {
        var id = Guid.NewGuid();
        var request = new QuestPostIntent(id, 100, kind == 0 ? 18u : 17u, 0, 0, 0, 1, 0, 0, 0,
            new string('A', 64), [], [Item()]);
        var snapshot = PostSnapshot(17, [request], kind == 1 ? [Guid.Empty] : kind == 2 ? [id, id] : [id]);
        var entry = kind == 3 ? new SaveQueueEntry(1, snapshot, null, null) : SaveQueueEntry.Full(snapshot);
        using var journal = Open();
        Assert.Throws<InvalidDataException>(() => journal.Append(entry));
        Assert.Equal(0, journal.PendingCount);
    }

}
