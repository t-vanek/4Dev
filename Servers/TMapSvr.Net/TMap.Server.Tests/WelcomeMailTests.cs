using System.Text.Json;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class WelcomeMailTests
{
    private sealed class DeliveryStore : IOperatorPostStore
    {
        public int Result = 2;
        public readonly List<Guid> Attempts = new();
        public Task VerifyOperatorPostSchemaAsync() => Task.CompletedTask;
        public Task<OperatorPostResult> DeliverOperatorPostAsync(OperatorPostIntent intent)
        {
            Attempts.Add(intent.OperationId);
            return Task.FromResult(new OperatorPostResult(Result, 0, intent.CharId, intent.Receiver, false));
        }
    }
    private sealed class Store : IWelcomeMailStore
    {
        public WelcomeMailSettings Settings = new(true, "Welcome", "Enjoy the game!", [new(8001, 5), new(8101, 5)]);
        public readonly HashSet<uint> Eligible = [1];
        public readonly Dictionary<uint, OperatorPostIntent> Prepared = new();
        public readonly HashSet<Guid> Dispatched = new();
        public int Calls; public bool FailPrepare, FailAck;
        public Task<WelcomeMailSettings> VerifyWelcomeMailStorageAsync() => Task.FromResult(Settings);
        public Task<OperatorPostIntent?> PrepareWelcomeMailAsync(uint id, Func<WelcomeMailSettings, WelcomeMailIdentity, OperatorPostIntent> build)
        {
            Calls++;
            if (FailPrepare) throw new IOException("Welcome SQL unavailable");
            if (!Eligible.Contains(id)) return Task.FromResult<OperatorPostIntent?>(null);
            if (!Prepared.TryGetValue(id, out var intent))
            {
                if (!Settings.Enabled) { Eligible.Remove(id); return Task.FromResult<OperatorPostIntent?>(null); }
                intent = build(Settings, new(id, Guid.NewGuid(), "Hero", 1793440800));
                // Model SQL's immutable payload, independently of mutable chart/settings objects.
                intent = JsonSerializer.Deserialize<OperatorPostIntent>(JsonSerializer.Serialize(intent))!; Prepared[id] = intent;
            }
            return Task.FromResult(Dispatched.Contains(intent.OperationId) ? null : intent);
        }
        public Task<IReadOnlyList<OperatorPostIntent>> PendingWelcomeMailAsync()
            => Task.FromResult<IReadOnlyList<OperatorPostIntent>>(Prepared.Values.Where(p => !Dispatched.Contains(p.OperationId)).ToArray());
        public Task MarkWelcomeMailDispatchedAsync(Guid id)
        { if (FailAck) throw new IOException("Acknowledgement failed"); Dispatched.Add(id); return Task.CompletedTask; }
    }
    private static (MapTestHarness H, Store Db, List<OperatorPostIntent> Sent) Setup()
    {
        var t = new TemplateStore();
        t.Items[8001] = new(8001, 0, new float[4], Stack: 30, DuraMax: 70);
        t.Items[8101] = new(8101, 0, new float[4], Stack: 30);
        var h = new MapTestHarness(t); var db = new Store(); var sent = new List<OperatorPostIntent>();
        h.Service.WelcomeMailStore = db; h.Service.OperatorPostEnqueue = sent.Add;
        return (h, db, sent);
    }
    private static Character Character(uint id = 1, bool loaded = true) => new() { CharId = id, DbLoaded = loaded, Hp = 100, MaxHp = 100 };

    [Fact]
    public async Task FirstPlayableEntryQueuesOnePackageWithBothConfiguredGiftsAndNativeText()
    {
        var x = Setup();
        var (s, _) = await x.H.EnterAsync(1, 9, 11, preSeeded: Character());
        var mail = Assert.Single(x.Sent); Assert.Equal(1u, mail.CharId); Assert.Equal(1, mail.Type);
        Assert.Equal(MapService.NetString("Welcome"), mail.Title); Assert.Equal(MapService.NetString("Enjoy the game!"), mail.Message);
        Assert.Equal(new ushort[] { 8001, 8101 }, mail.Items.Select(i => i.TemplateId));
        Assert.All(mail.Items, i => { Assert.Equal(5, i.Count); Assert.Equal(0, i.DlId); Assert.Equal(2, i.StorageType); });
        Assert.Equal(new byte[] { 0, 1 }, mail.Items.Select(i => i.ItemSlot));
        Assert.Equal(70u, mail.Items[0].DuraCur); Assert.Equal(EnterState.InGame, s.State);
        Assert.Contains(mail.OperationId, x.Db.Dispatched);
    }
    [Fact]
    public async Task DuplicateReadyPacketsAndRelogCannotSendAnotherWelcome()
    {
        var x = Setup(); var (s, _) = await x.H.EnterAsync(1, 9, 11, preSeeded: Character());
        await x.H.Service.DispatchClientAsync(s, MapTestHarness.ConReady());
        x.H.Service.OnClientDisconnect(s);
        await x.H.EnterAsync(1, 9, 22, preSeeded: Character());
        Assert.Single(x.Sent); Assert.Equal(2, x.Db.Calls); // once per real connection, SQL receipt suppresses relog
    }
    [Fact]
    public async Task ExistingCharactersAreNeverEnrolledByLoggingIn()
    {
        var x = Setup(); await x.H.EnterAsync(2, 9, 11, preSeeded: Character(2));
        Assert.Empty(x.Sent); Assert.Empty(x.Db.Prepared);
    }
    [Fact]
    public async Task DbFreeCharacterCannotReceiveARealGift()
    {
        var x = Setup(); await x.H.EnterAsync(1, 9, 11, preSeeded: Character(loaded: false));
        Assert.Equal(0, x.Db.Calls); Assert.Empty(x.Sent);
    }
    [Theory]
    [InlineData("secondary")]
    [InlineData("loading")]
    [InlineData("not registered")]
    [InlineData("tournament")]
    public async Task NonMainOrIncompleteEntryCannotIssueTheWelcome(string invalid)
    {
        var x = Setup(); x.H.Service.WelcomeMailStore = null;
        var (s, _) = await x.H.EnterAsync(1, 9, 11, preSeeded: Character());
        x.H.Service.WelcomeMailStore = x.Db; s.State = EnterState.Granted;
        switch (invalid)
        {
            case "secondary": s.IsMain = false; break;
            case "loading": s.State = EnterState.Entering; break;
            case "not registered": x.H.State.Remove(s); break;
            case "tournament": s.Char!.TournamentBackup = new MapService.TournamentBackup(); break;
        }
        await x.H.Service.DispatchClientAsync(s, MapTestHarness.ConReady());
        Assert.Empty(x.Sent); Assert.Equal(0, x.Db.Calls);
    }
    [Fact]
    public async Task FailedSqlPreparationRetriesOnTheActorWithoutPreventingPlay()
    {
        var x = Setup(); x.Db.FailPrepare = true;
        var (s, c) = await x.H.EnterAsync(1, 9, 11, preSeeded: Character());
        Assert.Equal(EnterState.InGame, s.State); Assert.False(c.Closed); Assert.Empty(x.Sent);
        x.Db.FailPrepare = false;
        for (int i = 0; i < 30; i++) await x.H.Service.OnTimerAsync();
        Assert.Single(x.Sent);
    }
    [Fact]
    public async Task InterruptedDispatchRecoversTheFrozenSqlPayloadWithItsOriginalIdentity()
    {
        var x = Setup(); x.H.Service.OperatorPostEnqueue = _ => throw new IOException("SQL transport unavailable");
        await x.H.EnterAsync(1, 9, 11, preSeeded: Character());
        var frozen = Assert.Single(x.Db.Prepared.Values); Assert.Empty(x.Db.Dispatched);
        x.Db.Settings = x.Db.Settings with { Title = "Changed later", Items = [new(8101, 99)] };
        var restarted = new MapTestHarness(); restarted.Service.WelcomeMailStore = x.Db; restarted.Service.OperatorPostEnqueue = x.Sent.Add;
        await restarted.Service.RecoverWelcomeMailAsync(); await restarted.Service.RecoverWelcomeMailAsync();
        Assert.Equal(frozen.OperationId, Assert.Single(x.Sent).OperationId); Assert.Equal(MapService.NetString("Welcome"), x.Sent[0].Title);
        Assert.Equal(2, x.Sent[0].Items.Count);
    }
    [Fact]
    public async Task AmbiguousAcknowledgementRetriesTheSameLetterRatherThanAllocatingAnotherIdentity()
    {
        var x = Setup(); x.Db.FailAck = true;
        await x.H.EnterAsync(1, 9, 11, preSeeded: Character()); x.Db.FailAck = false;
        await x.H.Service.RecoverWelcomeMailAsync();
        Assert.Equal(2, x.Sent.Count); Assert.Equal(x.Sent[0].OperationId, x.Sent[1].OperationId);
    }
    [Fact]
    public async Task UnacceptedNativeDeliveryKeepsItsSqlIntentUntilADurableResult()
    {
        var x = Setup(); var delivery = new DeliveryStore();
        x.H.Service.OperatorPostEnqueue = null; x.H.Service.OperatorPostStore = delivery;
        await x.H.EnterAsync(1, 9, 11, preSeeded: Character());
        Assert.Empty(x.Db.Dispatched);
        var intent = Assert.Single(x.Db.Prepared.Values);
        delivery.Result = 1; // permanent missing-recipient receipt is a completed delivery decision
        await x.H.Service.RecoverWelcomeMailAsync();
        Assert.Contains(intent.OperationId, x.Db.Dispatched);
        Assert.Equal(new[] { intent.OperationId, intent.OperationId }, delivery.Attempts);
    }
    [Fact]
    public async Task SettingsCannotActivateAnUnknownGift()
    {
        var x = Setup(); x.Db.Settings = x.Db.Settings with { Items = [new(9999, 1)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => x.H.Service.VerifyWelcomeMailStorageAsync());
    }
    [Fact]
    public async Task LargerGiftCountsSplitAtTheActualTemplateStackLimit()
    {
        var x = Setup(); x.Db.Settings = x.Db.Settings with { Items = [new(8001, 35)] };
        await x.H.EnterAsync(1, 9, 11, preSeeded: Character());
        Assert.Equal(new byte[] { 30, 5 }, Assert.Single(x.Sent).Items.Select(i => i.Count));
    }
}
