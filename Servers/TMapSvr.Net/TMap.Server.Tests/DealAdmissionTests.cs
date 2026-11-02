using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class DealAdmissionTests
{
    private static async Task<(MapTestHarness h, ClientSession a, ClientSession b, FakeClientChannel ca, FakeClientChannel cb)> Setup()
    {
        var t = new TemplateStore(); t.Items[300] = new(300, 0, new float[4], IsSell: 1);
        var h = new MapTestHarness(t);
        Character Player(uint id)
        {
            var ch = new Character { CharId = id, Hp = 100, MaxHp = 100 };
            var bag = new Inven { InvenId = 255, SlotCount = 16 }; ch.Invens.Add(bag);
            bag.Items.Add(new Item { TemplateId = 300, Template = t.Items[300], Count = 1 }); return ch;
        }
        var (a, ca) = await h.EnterAsync(1, 1, 1, name: "Alice", x: 100, z: 100, preSeeded: Player(1));
        var (b, cb) = await h.EnterAsync(2, 2, 2, name: "Bob", x: 100, z: 100, preSeeded: Player(2));
        ca.Clear(); cb.Clear(); return (h, a, b, ca, cb);
    }
    private static void Unavailable(ClientSession b, string state)
    {
        switch (state)
        {
            case "other map": b.Char!.MapId = 7; break;
            case "other channel": b.Channel = 2; break;
            case "loading": b.State = EnterState.Entering; break;
            case "secondary": b.IsMain = false; break;
            case "store": b.Store.IsOpen = true; break;
        }
    }
    [Theory]
    [InlineData("other map")]
    [InlineData("other channel")]
    [InlineData("loading")]
    [InlineData("secondary")]
    [InlineData("store")]
    public async Task UnavailableTargetCannotBeInvitedOrAccept(string unavailable)
    {
        var (h, a, b, ca, cb) = await Setup(); Unavailable(b, unavailable);
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealAskReq("Bob"));
        Assert.False(cb.Has(Msg.CS_DEALITEMASK_ACK));
        await h.Service.DispatchClientAsync(b, MapTestHarness.DealRlyReq(0, "Alice"));
        Assert.False(a.Deal.InProgress); Assert.False(b.Deal.InProgress);
        Assert.False(ca.Has(Msg.CS_DEALITEMSTART_ACK)); Assert.False(cb.Has(Msg.CS_DEALITEMSTART_ACK));
    }
    [Theory]
    [InlineData("other map")]
    [InlineData("other channel")]
    [InlineData("loading")]
    [InlineData("secondary")]
    [InlineData("store")]
    public async Task PartnerLeavingPlayableStateBeforeSecondConfirmationCannotExchangeItems(string unavailable)
    {
        var (h, a, b, ca, cb) = await Setup();
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealAskReq("Bob"));
        await h.Service.DispatchClientAsync(b, MapTestHarness.DealRlyReq(0, "Alice"));
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealAddReq(0, 0, 0, (255, 0)));
        await h.Service.DispatchClientAsync(b, MapTestHarness.DealAddReq(0, 0, 0));
        await h.Service.DispatchClientAsync(b, MapTestHarness.DealReq(1));
        ca.Clear(); cb.Clear(); Unavailable(b, unavailable);
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealReq(1));
        Assert.Single(a.Char!.FindInven(255)!.Items); Assert.Single(b.Char!.FindInven(255)!.Items);
        Assert.False(a.Deal.InProgress); Assert.False(b.Deal.InProgress);
        Assert.NotEqual(0, new PacketReader(Assert.Single(ca.WithId(Msg.CS_DEALITEMEND_ACK))).ReadByte());
    }
    [Fact]
    public async Task DisconnectClearsTheRemainingPlayersTradeLockAndPreservesItems()
    {
        var (h, a, b, _, cb) = await Setup();
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealAskReq("Bob"));
        await h.Service.DispatchClientAsync(b, MapTestHarness.DealRlyReq(0, "Alice")); cb.Clear();
        h.Service.OnClientDisconnect(a);
        Assert.False(a.Deal.InProgress); Assert.False(b.Deal.InProgress);
        Assert.Single(b.Char!.FindInven(255)!.Items);
        Assert.Equal((byte)DealResult.NoTarget, new PacketReader(Assert.Single(cb.WithId(Msg.CS_DEALITEMEND_ACK))).ReadByte());
    }

    [Fact]
    public async Task SessionReplacementWithTheSameCharacterNameCannotInheritAnOldTrade()
    {
        var (h, a, b, ca, _) = await Setup();
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealAskReq("Bob"));
        await h.Service.DispatchClientAsync(b, MapTestHarness.DealRlyReq(0, "Alice"));
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealAddReq(0, 0, 0, (255, 0)));
        h.State.Remove(b); // simulate a stale pair while the routing table already contains a new session
        var (replacement, client) = await h.EnterAsync(2, 2, 99, name: "Bob", x: 100, z: 100);
        ca.Clear(); client.Clear();
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealReq(1));
        Assert.False(a.Deal.InProgress); Assert.False(replacement.Deal.InProgress);
        Assert.False(client.Has(Msg.CS_DEALITEMEND_ACK)); Assert.Single(a.Char!.FindInven(255)!.Items);
        Assert.Equal((byte)DealResult.NoTarget, new PacketReader(Assert.Single(ca.WithId(Msg.CS_DEALITEMEND_ACK))).ReadByte());
    }

    [Fact]
    public async Task OfferAlsoRejectsAPartnerWhoChangedMapsAfterOpeningTheTrade()
    {
        var (h, a, b, ca, _) = await Setup();
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealAskReq("Bob"));
        await h.Service.DispatchClientAsync(b, MapTestHarness.DealRlyReq(0, "Alice")); ca.Clear();
        b.Char!.MapId = 7;
        await h.Service.DispatchClientAsync(a, MapTestHarness.DealAddReq(0, 0, 0, (255, 0)));
        Assert.False(a.Deal.InProgress); Assert.False(b.Deal.InProgress);
        Assert.Single(a.Char!.FindInven(255)!.Items);
        Assert.NotEqual(0, new PacketReader(Assert.Single(ca.WithId(Msg.CS_DEALITEMEND_ACK))).ReadByte());
    }
}
