using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class DealPersistenceTests
{
    private sealed class Store : ICharacterSaveQueueStore
    {
        public readonly MemorySaveQueueStore Inner = new();
        public bool FailAppend, BlockDrain;
        public Action? BeforeAppend;
        public void Open(string identity) => Inner.Open(identity);
        public void AssertLease() { if (BlockDrain) throw new IOException("Hold background application"); Inner.AssertLease(); }
        public long Boundary => Inner.Boundary;
        public int Count => Inner.Count;
        public long Append(Guid id, string checksum, string payload)
        {
            BeforeAppend?.Invoke();
            if (FailAppend) throw new IOException("SQL queue unavailable");
            long seq = Inner.Append(id, checksum, payload); BlockDrain = true; return seq;
        }
        public IReadOnlyList<StoredCharacterSave> Read(long boundary) => Inner.Read(boundary);
        public void Acknowledge(long sequence) => Inner.Acknowledge(sequence);
        public void Dispose() => Inner.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Store Queue = new();
        public readonly FakeClientChannel AClient = new(), BClient = new();
        public readonly MapService Service;
        public readonly ClientSession A, B;
        private readonly IDisposable Lease;
        public Fixture(bool seeded = true, bool loaded = true)
        {
            var options = new TMap.Server.MapServerOptions();
            options.Db.GameConnectionString = "Server=127.0.0.1,1;Database=4DevQuestTest_unreachable;User Id=fixture;Password=unused;Encrypt=False;Connect Timeout=1;ConnectRetryCount=0;Pooling=False";
            var state = new MapState(); var templates = new TemplateStore();
            templates.Items[300] = new(300, 0, new float[4], IsSell: 1);
            templates.Items[301] = new(301, 0, new float[4], IsSell: 1);
            Service = new(options, state, new FakeWorldSink(), new GameDatabase(options.Db.GameConnectionString), templates, NullLogger<MapService>.Instance);
            Service.SaveQueueStore = Queue; Lease = Service.OpenSaveQueue()!;
            if (seeded) Service.ApplyItemIdSeed(1000000);
            ClientSession Player(uint id, string name, FakeClientChannel client, ushort itemId)
            {
                var ch = new Character { CharId = id, Name = name, DbLoaded = loaded, Hp = 100, MaxHp = 100, Country = 1, AidCountry = 3 };
                ch.EarnMoney(id * 100);
                var bag = new Inven { InvenId = 255, SlotCount = 16 };
                bag.Items.Add(new Item { DlId = 100 + id, TemplateId = itemId, Template = templates.Items[itemId], Count = 1 }); ch.Invens.Add(bag);
                var session = new ClientSession(client) { CharId = id, UserId = id, Key = id, Channel = 1, IsMain = true, State = EnterState.InGame, Char = ch };
                state.Register(session); return session;
            }
            A = Player(1, "Alice", AClient, 300); B = Player(2, "Bob", BClient, 301);
        }
        public async Task Offer()
        {
            await Service.DispatchClientAsync(A, MapTestHarness.DealAskReq("Bob"));
            await Service.DispatchClientAsync(B, MapTestHarness.DealRlyReq(0, "Alice"));
            await Service.DispatchClientAsync(A, MapTestHarness.DealAddReq(0, 0, 30, (255, 0)));
            await Service.DispatchClientAsync(B, MapTestHarness.DealAddReq(0, 0, 50, (255, 0)));
            await Service.DispatchClientAsync(A, MapTestHarness.DealReq(1));
            AClient.Clear(); BClient.Clear();
        }
        public Task Confirm() => Service.DispatchClientAsync(B, MapTestHarness.DealReq(1));
        public SaveQueueEntry Entry() => JsonSerializer.Deserialize<SaveQueueEntry>(Assert.Single(Queue.Inner.State.Rows.Values).Payload, SqlSaveQueue.Json)!;
        public void Dispose() => Lease.Dispose();
    }

    [Fact]
    public async Task BothInventoriesMoneyAndDirectOperationsCommitInOneFrozenRecordBeforeSuccess()
    {
        using var f = new Fixture(); await f.Offer();
        f.Service.EnqueueItemSave(f.A, 255, f.A.Char!.FindInven(255)!.Items[0]);
        f.Queue.BeforeAppend = () => { Assert.False(f.AClient.Has(Msg.CS_DEALITEMEND_ACK)); Assert.False(f.BClient.Has(Msg.CS_DEALITEMEND_ACK)); };
        await f.Confirm();
        var entry = f.Entry(); Assert.Equal(4, entry.Version); Assert.Null(entry.Character);
        var a = Assert.Single(entry.Exchange!, s => s.Character.CharId == 1);
        var b = Assert.Single(entry.Exchange!, s => s.Character.CharId == 2);
        Assert.Equal(120u, a.Character.Cooper); Assert.Equal(180u, b.Character.Cooper);
        Assert.Equal(301, Assert.Single(a.Items!).TemplateId); Assert.Equal(300, Assert.Single(b.Items!).TemplateId);
        Assert.Contains(101L, entry.Deletes!); Assert.Contains(102L, entry.Deletes!);
        Assert.Equal(2, entry.Upserts!.Count); Assert.Empty(f.Service.PendingItemUpserts); Assert.Empty(f.Service.PendingItemDeletes);
        Assert.Equal(0, new PacketReader(Assert.Single(f.AClient.WithId(Msg.CS_DEALITEMEND_ACK))).ReadByte());
        Assert.Equal(0, new PacketReader(Assert.Single(f.BClient.WithId(Msg.CS_DEALITEMEND_ACK))).ReadByte());
        f.A.Char!.SetMoneyTotal(0); f.B.Char!.FindInven(255)!.Items.Clear();
        Assert.Equal(120u, f.Entry().Exchange!.Single(s => s.Character.CharId == 1).Character.Cooper);
        Assert.Single(f.Entry().Exchange!.Single(s => s.Character.CharId == 2).Items!);
        await Assert.ThrowsAsync<IOException>(() => f.Service.RecoverPendingSavesAsync()); // queued failure denies fresh admission
    }

    [Fact]
    public async Task EnqueueFailureQuarantinesBothPlayersAndCannotLeakThroughDisconnectOrItemFlush()
    {
        using var f = new Fixture(); await f.Offer(); f.Queue.FailAppend = true;
        await f.Confirm();
        Assert.Empty(f.Queue.Inner.State.Rows); Assert.False(f.A.Char!.DbLoaded); Assert.False(f.B.Char!.DbLoaded);
        Assert.True(f.AClient.Closed); Assert.True(f.BClient.Closed);
        Assert.False(f.AClient.Has(Msg.CS_DEALITEMEND_ACK)); Assert.False(f.BClient.Has(Msg.CS_DEALITEMEND_ACK));
        Assert.False(f.AClient.Has(Msg.CS_ADDITEM_ACK)); Assert.False(f.BClient.Has(Msg.CS_ADDITEM_ACK));
        Assert.Empty(f.Service.PendingItemUpserts); Assert.Empty(f.Service.PendingItemDeletes);
        f.Service.OnClientDisconnect(f.A); f.Service.OnClientDisconnect(f.B); f.Service.FlushItemDirect();
        Assert.Empty(f.Queue.Inner.State.Rows);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingInventorySeedOrCompleteCharacterStateRejectsBeforeMutation(bool seeded, bool loaded)
    {
        using var f = new Fixture(seeded, loaded); await f.Offer(); await f.Confirm();
        Assert.Empty(f.Queue.Inner.State.Rows);
        Assert.Equal(100, f.A.Char!.MoneyTotal); Assert.Equal(200, f.B.Char!.MoneyTotal);
        Assert.Equal(300, Assert.Single(f.A.Char.FindInven(255)!.Items).TemplateId);
        Assert.Equal(301, Assert.Single(f.B.Char.FindInven(255)!.Items).TemplateId);
        Assert.NotEqual(0, new PacketReader(Assert.Single(f.AClient.WithId(Msg.CS_DEALITEMEND_ACK))).ReadByte());
    }
}
