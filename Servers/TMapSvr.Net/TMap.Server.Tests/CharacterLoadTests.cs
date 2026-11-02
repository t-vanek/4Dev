using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class CharacterLoadTests
{
    private sealed class Faults
    {
        public string? Fail, Wait;
        public List<string> Calls = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Step(string stage)
        {
            Calls.Add(stage);
            if (stage == Wait) { Entered.TrySetResult(); await Release.Task; }
            if (stage == Fail) throw new IOException("Saved state unavailable: " + stage);
        }
    }
    private sealed class Auxiliary(Faults faults) : ICharacterAuxiliaryStore
    {
        public List<HotkeyLoadRow> Hotkeys = [];
        public List<RecallSaveRow> Recalls = [];
        public List<RecallMaintainRow> Buffs = [];
        public DuringLoad During = new([], [], 0, 0, 0);
        public async Task<List<HotkeyLoadRow>> LoadHotkeysAsync(uint id, CancellationToken ct = default)
        { await faults.Step("hotkeys"); return Hotkeys; }
        public async Task<(List<RecallSaveRow> Recalls, List<RecallMaintainRow> Buffs)> LoadRecallsAsync(uint id, CancellationToken ct = default)
        { await faults.Step("recalls"); return (Recalls, Buffs); }
        public async Task<(byte OriCountry, byte AidCountry, long AidDate)> LoadAidAsync(uint id, CancellationToken ct = default)
        { await faults.Step("aid"); return (1, 2, 123); }
        public async Task<DuringLoad> LoadDuringItemsAsync(uint id, uint user, int date, CancellationToken ct = default)
        { await faults.Step("during"); return During; }
    }
    private sealed class Pets(Faults faults) : IPetStore
    {
        public List<PetRow> Rows = [];
        public SaddleRow Saddle;
        public async Task<List<PetRow>> LoadPetsAsync(uint id) { await faults.Step("pets"); return Rows; }
        public async Task<SaddleRow> GetSaddleAsync(uint id) { await faults.Step("saddle"); return Saddle; }
        public Task SavePetAsync(uint id, PetRow row) => Task.CompletedTask;
        public Task DeletePetAsync(uint id, ushort pet) => Task.CompletedTask;
        public Task SetSaddleAsync(uint id, SaddleRow row) => Task.CompletedTask;
        public Task DeleteSaddleAsync(uint id) => Task.CompletedTask;
    }
    private sealed class Companions(Faults faults) : ICompanionStore
    {
        public CompanionLoad Data = new([], 255, 0);
        public async Task<CompanionLoad> LoadCompanionsAsync(uint id) { await faults.Step("companions"); return Data; }
        public Task SaveCompanionAsync(uint id, CompanionRow row) => Task.CompletedTask;
        public Task DeleteCompanionAsync(uint id, byte slot) => Task.CompletedTask;
        public Task SaveLastCompanionAsync(uint id, byte slot) => Task.CompletedTask;
        public Task SaveMedalsAsync(uint id, uint medals) => Task.CompletedTask;
    }
    private static (MapTestHarness H, Faults F, Auxiliary A, Pets P, Companions C) Setup(string? fail = null, string? wait = null)
    {
        var h = new MapTestHarness(); var f = new Faults { Fail = fail, Wait = wait };
        var a = new Auxiliary(f); var p = new Pets(f); var c = new Companions(f);
        h.Service.CharacterAuxiliaryStore = a; h.Service.PetStore = p; h.Service.CompanionStore = c;
        return (h, f, a, p, c);
    }

    [Theory]
    [InlineData("recalls")] [InlineData("hotkeys")] [InlineData("aid")] [InlineData("during")]
    [InlineData("saddle")] [InlineData("pets")] [InlineData("companions")]
    public async Task FailedSavedPartCannotPublishAPlayableOrSaveableCharacter(string stage)
    {
        var x = Setup(fail: stage);
        var (s, c) = await x.H.EnterAsync(17, 9, 10);
        Assert.True(c.Closed); Assert.Null(s.Char); Assert.Null(x.H.State.FindByChar(17));
        Assert.False(s.IsMain); Assert.False(s.EntryValidated); Assert.NotEqual(EnterState.InGame, s.State);
        Assert.False(x.H.World.Has(Msg.MW_ADDCHAR_ACK)); Assert.False(x.H.World.Has(Msg.MW_ENTERSVR_ACK));
        Assert.False(c.Has(Msg.CS_CHARINFO_ACK));
        Assert.Equal(ConnectResult.Internal, (ConnectResult)new PacketReader(c.Last(Msg.CS_CONNECT_ACK)!).ReadByte());
        Assert.Equal(stage, x.F.Calls.Last());
        await x.H.Service.OnClientDisconnectAsync(s);
        Assert.Null(s.Char); Assert.Empty(x.H.Service.PendingItemUpserts);
    }

    [Theory]
    [InlineData("during")] [InlineData("pets")] [InlineData("companions")]
    public async Task DelayedSavedPartMustFinishBeforeWorldAnnouncement(string stage)
    {
        var x = Setup(wait: stage); var c = new FakeClientChannel(); var s = new ClientSession(c);
        var loading = x.H.Service.DispatchClientAsync(s, MapTestHarness.ConnectReq(17, 9, 10));
        await x.F.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(s.Char); Assert.False(x.H.World.Has(Msg.MW_ADDCHAR_ACK));
        x.F.Release.SetResult(); await loading;
        Assert.NotNull(s.Char); Assert.False(c.Closed); Assert.True(x.H.World.Has(Msg.MW_ADDCHAR_ACK));
        await x.H.Service.DispatchWorldAsync(MapTestHarness.EnterSvrReq(17, 10));
        Assert.Equal(1, x.F.Calls.Count(s => s == stage));
    }

    [Theory]
    [InlineData("recalls")] [InlineData("companions")]
    public async Task WorldDrivenFailureUsesTheSameAdmissionGate(string stage)
    {
        var x = Setup(fail: stage); var c = new FakeClientChannel();
        var s = new ClientSession(c) { CharId = 17, UserId = 9, Key = 10, Channel = 1, State = EnterState.Entering };
        x.H.State.Register(s);
        await x.H.Service.DispatchWorldAsync(MapTestHarness.EnterSvrReq(17, 10));
        await x.H.Service.DispatchWorldAsync(MapTestHarness.ConResultReq(17, 10, ConnectResult.Success));
        Assert.True(c.Closed); Assert.Null(s.Char); Assert.Null(x.H.State.FindByChar(17));
        Assert.False(s.EntryValidated); Assert.False(x.H.World.Has(Msg.MW_ENTERSVR_ACK));
        Assert.NotEqual(EnterState.Granted, s.State);
    }

    [Fact]
    public async Task ValidEmptyPartsAllowNewCharacters()
    {
        var x = Setup(); var (s, c) = await x.H.EnterAsync(17, 9, 10);
        Assert.False(c.Closed); Assert.Equal(EnterState.InGame, s.State);
        Assert.Empty(s.Char!.Companions); Assert.Empty(s.Char.Pets); Assert.Empty(s.Char.HotkeyPages);
        Assert.Equal(255, s.Char.CompanionSlot); Assert.Equal(0u, s.Char.Medals);
        Assert.Equal(new[] { "recalls", "hotkeys", "aid", "during", "saddle", "pets", "companions" }, x.F.Calls);
    }

    private static CompanionRow Companion() => new(1, 200, 3, "Saved", 123, 15000, 2, 4,
        [1, 2, 3, 4, 5, 6], 11, [100, 101], [2000001000, 2000002000], 77);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IncompleteRowsFailInsteadOfKeepingOnlyTheSuccessfullyReadParts(bool companion)
    {
        var x = Setup();
        if (companion) x.C.Data = new([Companion(), Companion() with { Slot = 2, Stats = [] }], 1, 987);
        else x.A.Hotkeys = [new(7, new (byte, ushort)[12]), new(8, [])];
        var (s, c) = await x.H.EnterAsync(17, 9, 10);
        Assert.True(c.Closed); Assert.Null(s.Char); Assert.Null(x.H.State.FindByChar(17));
    }

    [Fact]
    public async Task RetryAfterCompanionReadFailureRestoresMedalsEquipmentAndMountOwnership()
    {
        var x = Setup(fail: "companions");
        x.C.Data = new([Companion()], 1, 987); x.P.Saddle = new(300, 0, 1); x.P.Rows = [new(5, "Mount", 0, 7)];
        var (failed, closed) = await x.H.EnterAsync(17, 9, 10);
        Assert.True(closed.Closed); Assert.Null(failed.Char);
        x.F.Fail = null;
        // A mount template is required for a valid owned licence, as in the original load path.
        var templates = new TemplateStore(); templates.Mounts[5] = new(5, 100, 101);
        var h = new MapTestHarness(templates);
        h.Service.CharacterAuxiliaryStore = x.A; h.Service.PetStore = x.P; h.Service.CompanionStore = x.C;
        var client = new FakeClientChannel(); var s = new ClientSession(client);
        await h.Service.DispatchClientAsync(s, MapTestHarness.ConnectReq(17, 9, 11));
        Assert.False(client.Closed); var ch = Assert.IsType<Character>(s.Char);
        Assert.Equal(987u, ch.Medals); Assert.Equal(1, ch.CompanionSlot);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, ch.Companions[1].Stats);
        Assert.Equal(new ushort[] { 100, 101 }, ch.Companions[1].ItemIds);
        Assert.Equal(77u, ch.Companions[1].Tick);
        Assert.Equal("Mount", Assert.Single(ch.Pets).Value.Name); Assert.Equal((ushort)300, ch.Saddle!.Value.ItemId);
    }

    [Fact]
    public async Task SuccessfulAuxiliaryReadPreservesSavePayloadsAndAllegiance()
    {
        var templates = new TemplateStore();
        templates.Items[400] = new(400, 0, new float[4], Type: 7, Kind: 40, UseType: 1);
        templates.Items[401] = new(401, 0, new float[4], Type: 7, Kind: 56, UseType: 1);
        var h = new MapTestHarness(templates); h.Service.UnixNow = () => 2000000000;
        var f = new Faults(); var store = new Auxiliary(f);
        var slots = new (byte, ushort)[12]; slots[0] = (1, 600); slots[11] = (2, 601);
        store.Hotkeys = [new(7, slots)];
        store.Recalls = [new(8, 200, 0, 1001, 3, 90, 80, 1, 1, 2, 3, 1000, 4)];
        store.Buffs = [new(8, 500, 2, 900, 1, 17, 1, 17, 2)];
        store.During = new([new(400, 1, 5000, 2000005000)], [new(401, 1, 1000, 2000001000)], 2, 77, 3, 9);
        h.Service.CharacterAuxiliaryStore = store;
        var c = new FakeClientChannel(); var s = new ClientSession(c);
        await h.Service.DispatchClientAsync(s, MapTestHarness.ConnectReq(17, 9, 10));
        Assert.False(c.Closed); var ch = Assert.IsType<Character>(s.Char);
        Assert.Equal((1, 2, 123L), ((int)ch.OriCountry, (int)ch.AidCountry, ch.AidDate));
        Assert.Equal((byte)2, ch.InPcBang); Assert.Equal(77u, ch.PcBangTime); Assert.Equal((byte)9, ch.LuckyNumber);
        Assert.Equal((ushort)600, ch.HotkeyPages[0].Slots[0].Id); Assert.Equal((ushort)601, ch.HotkeyPages[0].Slots[11].Id);
        var snapshot = h.Service.WithDuringItems(MapService.WithRecalls(MapService.BuildCharSave(ch), ch, 0), ch, 2000000000);
        Assert.Equal(store.Recalls[0], Assert.Single(snapshot.Recalls!)); Assert.Equal(store.Buffs[0], Assert.Single(snapshot.RecallMaintains!));
        Assert.Equal(store.During.Premium[0], snapshot.Premium); Assert.Equal(store.During.Exp[0], snapshot.ExpItem);
        Assert.Empty(MapService.BuildHotkeySaves(ch)); // loaded pages are not new inserts
    }
}
