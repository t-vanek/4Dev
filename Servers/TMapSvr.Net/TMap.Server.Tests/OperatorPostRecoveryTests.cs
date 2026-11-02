using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class OperatorPostRecoveryTests
{
    private static OperatorPostIntent Intent() => new(Guid.NewGuid(), 1, "Hero", "00000006Reward", "", 1, 0, 500, 0,
        1793376000, [new(0, 2, 0, 0, 200, 7, 5, 4, 500, 450, 6, 0, 8, [1,2,3,4,5,6], [101,102,103,104,105,106], [11,12,13,14,15,16], 9, 201)]);

    [Fact]
    public async Task RewardIntentFreezesRecipientMoneyAndItemAttributesAcrossQueueReopen()
    {
        var state = new MemorySaveQueueState(); var intent = Intent();
        using (var q = new SqlSaveQueue(new MemorySaveQueueStore(state), "reward"))
        {
            q.Append(SaveQueueEntry.ServerPosts([intent]));
            intent.Items[0].Ext[0] = 999; intent.Items.Clear();
        }
        using var recovered = new SqlSaveQueue(new MemorySaveQueueStore(state), "reward");
        await recovered.DrainAsync(recovered.Boundary, e =>
        {
            Assert.Equal(3, e.Version); var saved = Assert.Single(e.OperatorPosts!);
            Assert.Equal(intent.OperationId, saved.OperationId); Assert.Equal(500u, saved.Silver);
            Assert.Equal(11u, Assert.Single(saved.Items).Ext[0]); Assert.Equal((byte)9, saved.Items[0].Gem);
            Assert.Equal((ushort)201, saved.Items[0].MoggItemId); return Task.CompletedTask;
        });
        Assert.Equal(0, recovered.PendingCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void InvalidOrMixedRewardRecordsCannotEnterTheSqlQueue(int kind)
    {
        var post = Intent();
        post = kind switch
        {
            0 => post with { OperationId = Guid.Empty }, 1 => post with { CharId = 0 },
            2 => post with { Items = [post.Items[0] with { DlId = 55 }] },
            3 => post with { Items = [post.Items[0] with { ItemSlot = 1 }] },
            4 => post with { Items = [post.Items[0] with { Count = 0 }] },
            5 => post with { Items = [post.Items[0] with { Magic = [] }] },
            6 => post with { Items = [post.Items[0] with { StorageId = 12 }] }, _ => post
        };
        var entry = kind switch
        {
            7 => new SaveQueueEntry(1, null, [], [], [post]),
            8 => new SaveQueueEntry(3, null, [], [], [post]),
            9 => SaveQueueEntry.ServerPosts([post, post]),
            10 => SaveQueueEntry.ServerPosts([]), _ => SaveQueueEntry.ServerPosts([post])
        };
        using var q = new SqlSaveQueue(new MemorySaveQueueStore(), "reward");
        Assert.Throws<InvalidDataException>(() => q.Append(entry)); Assert.Equal(0, q.PendingCount);
    }

    private sealed class Store : IOperatorPostStore
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail; public int Calls; public OperatorPostIntent? Intent;
        private bool _delivered;
        public Task VerifyOperatorPostSchemaAsync() => Task.CompletedTask;
        public async Task<OperatorPostResult> DeliverOperatorPostAsync(OperatorPostIntent intent)
        {
            Intent = intent; Calls++; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (Fail) throw new IOException("injected reward SQL failure");
            bool created = !_delivered; _delivered = true; return new(0, 77, intent.CharId, "Hero", created);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public MapService S; public MemorySaveQueueStore Queue = new(); public Store Posts = new();
        public FakeClientChannel Client = new(); public Character Ch; private readonly IDisposable _lease;
        public Fixture()
        {
            var t = new TemplateStore();
            t.Items[200] = new ItemTemplate(200, 0, [1,0,0,0], DuraMax: 123);
            t.BattleZones[9] = new(9, "Garden", 900, 0, 0, 0, 0, 0, 0, 0, 0, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            t.Territories.Add(new(LocalType.SkyGarden, 9, 0, 0, "", 0, 0, "", 0));
            var options = new MapServerOptions(); options.Db.GameConnectionString = "Server=fixture;Database=fixture";
            var state = new MapState();
            S = new(options, state, new FakeWorldSink(), new GameDatabase(options.Db.GameConnectionString), t, NullLogger<MapService>.Instance)
                { SaveQueueStore = Queue, OperatorPostStore = Posts, PostStore = new FakePostStore(), AuctionEnabled = false };
            _lease = S.OpenSaveQueue()!; S.InitTerritories();
            Ch = new() { CharId = 1, Name = "Hero", Country = 1, AidCountry = 3, MapId = 900, Hp = 100, MaxHp = 100 };
            var session = new ClientSession(Client) { CharId = 1, Key = 11, IsMain = true, EntryValidated = true, State = EnterState.InGame, Channel = 1, Char = Ch };
            state.Register(session); state.EnterWorld(session);
        }
        public Task Win() => S.DispatchWorldAsync(new PacketWriter(Msg.MW_SKYGARDENOCCUPY_REQ).WriteByte(1).WriteUInt16(9).WriteByte(1).ToArray());
        public void Dispose() => _lease.Dispose();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealRewardDispatcherCommitsIntentBeforeAsyncDeliveryAndRetainsItOnFailure(bool fail)
    {
        using var f = new Fixture(); f.Posts.Fail = fail;
        await f.Win(); await f.Posts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(1, f.Queue.Count); Assert.False(f.Client.Has(Msg.CS_POSTRECV_ACK));
            Assert.Equal(200, Assert.Single(f.Posts.Intent!.Items).TemplateId);
            Assert.Equal(0, f.Posts.Intent.Items[0].DlId); // native SQL generator allocates IDs only during the atomic delivery
        }
        finally { f.Posts.Release.TrySetResult(); }
        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => f.S.RecoverPendingSavesAsync());
            Assert.Equal(1, f.Queue.Count); Assert.False(f.Client.Has(Msg.CS_POSTRECV_ACK));
            f.Posts.Fail = false;
        }
        await f.S.RecoverPendingSavesAsync(); Assert.Equal(0, f.Queue.Count);
        await f.S.OnTimerAsync(); Assert.True(f.Client.Has(Msg.CS_POSTRECV_ACK));
        var r = new PacketReader(f.Client.Last(Msg.CS_POSTRECV_ACK)!); Assert.Equal(77u, r.ReadUInt32());
        Assert.Equal(123u, f.Posts.Intent!.Items[0].DuraMax);
    }

    [Fact]
    public async Task RejectedDurableEnqueueCannotFallBackToPartialMailOrPayTheFollowingPoints()
    {
        using var f = new Fixture(); f.Queue.FailAppend = true;
        await f.Win(); Assert.Equal(0, f.Queue.Count); Assert.Equal(0, f.Posts.Calls);
        Assert.False(f.Client.Has(Msg.CS_POSTRECV_ACK)); Assert.Equal(0u, f.Ch.PvpUseablePoint);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WolverineEnqueueOwnsNewRewardsWithoutASecondSqlQueueIssuer(bool fail)
    {
        using var f = new Fixture(); OperatorPostIntent? captured = null;
        f.S.OperatorPostEnqueue = intent => { if (fail) throw new IOException("durable broker unavailable"); captured = intent; };
        await f.Win(); Assert.Equal(0, f.Queue.Count); Assert.Equal(0, f.Posts.Calls);
        Assert.Equal(fail, captured is null); Assert.Equal(fail ? 0u : 360u, f.Ch.PvpUseablePoint);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LegacyQueuedRewardsTransferTheirOriginalIdentityOnlyAfterDurableAcceptance(bool fail)
    {
        using var f = new Fixture(); var seen = new List<Guid>(); bool reject = fail;
        f.S.LegacyOperatorPostForward = intent =>
        {
            seen.Add(intent.OperationId);
            return reject ? Task.FromException(new IOException("broker unavailable")) : Task.CompletedTask;
        };
        await f.Win();
        if (fail) { await Assert.ThrowsAsync<IOException>(() => f.S.RecoverPendingSavesAsync()); Assert.Equal(1, f.Queue.Count); reject = false; }
        await f.S.RecoverPendingSavesAsync(); Assert.Equal(0, f.Queue.Count);
        Assert.NotEmpty(seen); Assert.Single(seen.Distinct()); Assert.Equal(0, f.Posts.Calls);
    }
}
