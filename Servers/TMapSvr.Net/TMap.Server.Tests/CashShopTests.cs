using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Batch S7b: the cash shop — the list, buying and presents, the cabinet (list, take out, put in, more time), the world's
/// stop and sales, and cash bonus items.</summary>
public class CashShopTests
{
    private const uint A = 1;
    private const ushort Wing = 400, Hood = 401, Foreign = 402, BonusCard = 403, Junk = 404;
    private const byte Bag = 0;

    private sealed record Setup(TemplateStore T, MapTestHarness H, FakeCashStore Cash, ClientSession S, FakeClientChannel C, Character Ch);

    private static FullItemRow Row(ushort id) => new(0, 0, 0, id, 0, 1, 0, 0, 0, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0, 0);

    private static async Task<Setup> Open()
    {
        var t = new TemplateStore();
        t.Items[Wing] = new ItemTemplate(Wing, 0, new[] { 1f, 0f, 0f, 0f }, Type: 1, Kind: 1, IsSell: 4);
        t.Items[Hood] = new ItemTemplate(Hood, 0, new[] { 1f, 0f, 0f, 0f }, Type: 1, Kind: 1);
        t.Items[Foreign] = new ItemTemplate(Foreign, 0, new[] { 1f, 0f, 0f, 0f }, Type: 1, Kind: 1, ItemCountry: 0);
        t.Items[BonusCard] = new ItemTemplate(BonusCard, 0, new[] { 1f, 0f, 0f, 0f }, Type: 7, Kind: 86, UseValue: 300);
        t.Items[Junk] = new ItemTemplate(Junk, 0, new[] { 1f, 0f, 0f, 0f }, Type: 1, Kind: 1);
        var wing = new CashItem { Id = 10, InfoId = 1, Money = 100, OriMoney = 100, Category = 1, CanSell = 1, UseTime = 30, Item = Row(Wing) };
        var hood = new CashItem { Id = 11, InfoId = 2, Money = 200, OriMoney = 200, Category = 1, CanSell = 0, Item = Row(Hood) };
        var foreign = new CashItem { Id = 12, InfoId = 3, Money = 300, OriMoney = 300, Category = 1, CanSell = 1, Item = Row(Foreign) };
        foreach (var ci in new[] { wing, hood, foreign }) t.CashItems[ci.Id] = ci;
        t.CashCategories.Add(new CashCategory(1, "Wings", new() { wing, hood, foreign }));
        var h = new MapTestHarness(t);
        var cash = new FakeCashStore();
        h.Service.CashStore = cash;
        var ch = new Character { CharId = A, Name = "Ann", Level = 20, Country = 1, MaxHp = 100, Hp = 100 };
        ch.Invens.Add(new Inven { InvenId = Bag });
        var (s, c) = await h.EnterAsync(A, 9, 11, name: "Ann", preSeeded: ch);
        c.Clear();
        return new Setup(t, h, cash, s, c, ch);
    }

    private static Task Send(Setup x, PacketWriter w) => x.H.Service.DispatchClientAsync(x.S, w.ToArray());

    private static Item Give(Setup x, ushort id, byte slot)
    {
        var item = new Item { TemplateId = id, ItemSlot = slot, Count = 1, Template = x.T.Items[id], DlId = 5000 + slot };
        x.Ch.FindInven(Bag)!.Items.Add(item);
        return item;
    }

    [Fact]
    public async Task TheList_ShowsTheItemsForSaleInOnesCountry()
    {
        var x = await Open();
        await Send(x, new PacketWriter(Msg.CS_CASHSHOPITEMLIST_REQ).WriteUInt16(0));
        var r = new PacketReader(x.C.Last(Msg.CS_CASHSHOPITEMLIST_ACK)!);
        Assert.Equal((1000u, 50u, (byte)1), (r.ReadUInt32(), r.ReadUInt32(), r.ReadByte()));
        Assert.Equal("Wings", r.ReadString());
        Assert.Equal(1, r.ReadUInt16());                                                    // not the hood (not for sale) nor the foreign one
        Assert.Equal(((ushort)10, (ushort)1, 100u, (byte)0), (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt32(), r.ReadByte()));
        Assert.Equal(Wing, r.ReadUInt16());

        await Send(x, new PacketWriter(Msg.CS_CASHSHOPITEMLIST_REQ).WriteUInt16(Hood));       // asked for by name, it shows
        r = new PacketReader(x.C.Last(Msg.CS_CASHSHOPITEMLIST_ACK)!);
        r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); r.ReadString();
        Assert.Equal(1, r.ReadUInt16());
        Assert.Equal((ushort)11, r.ReadUInt16());
    }

    [Fact]
    public async Task AStoppedShop_SaysSo()
    {
        var x = await Open();
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_CASHSHOPSTOP_REQ).WriteByte(1).WriteByte(1).ToArray());
        Assert.Equal(1, new PacketReader(x.C.Last(Msg.CS_CASHSHOPSTOP_ACK)!).ReadByte());
        x.C.Clear();
        await Send(x, new PacketWriter(Msg.CS_CASHITEMBUY_REQ).WriteUInt16(10));
        Assert.True(x.C.Has(Msg.CS_CASHSHOPSTOP_ACK));
        Assert.DoesNotContain(x.Cash.Log, l => l.StartsWith("buy"));

        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_CASHSHOPSTOP_REQ).WriteByte(0).WriteByte(0).ToArray());
        await Send(x, new PacketWriter(Msg.CS_CASHITEMBUY_REQ).WriteUInt16(10));
        Assert.True(x.C.Has(Msg.CS_CASHITEMBUY_ACK));
    }

    [Fact]
    public async Task Buying_AnswersWithTheBalance_OrTheReason()
    {
        var x = await Open();
        await Send(x, new PacketWriter(Msg.CS_CASHITEMBUY_REQ).WriteUInt16(10));
        Assert.Contains("buy 10 1 ", x.Cash.Log);
        var r = new PacketReader(x.C.Last(Msg.CS_CASHITEMBUY_ACK)!);
        Assert.Equal((0, 900u, 50u), (r.ReadByte(), r.ReadUInt32(), r.ReadUInt32()));

        x.Cash.Buy = new CashBuyResult(3, 0, 0, 0, 0, "");                                   // CASHSHOP_NEEDCASH
        await Send(x, new PacketWriter(Msg.CS_CASHITEMBUY_REQ).WriteUInt16(10));
        Assert.Equal(3, new PacketReader(x.C.Last(Msg.CS_CASHITEMBUY_ACK)!).ReadByte());
    }

    [Fact]
    public async Task APresent_NeedsAName_AndGoesToTheReceiver()
    {
        var x = await Open();
        await Send(x, new PacketWriter(Msg.CS_CASHITEMPRESENT_REQ).WriteString("").WriteUInt16(10));
        Assert.Equal(2, new PacketReader(x.C.Last(Msg.CS_CASHITEMBUY_ACK)!).ReadByte());
        Assert.Empty(x.Cash.Log);

        await Send(x, new PacketWriter(Msg.CS_CASHITEMPRESENT_REQ).WriteString("Bob").WriteUInt16(10));
        Assert.Contains("buy 10 2 Bob", x.Cash.Log);
        Assert.Equal(0, new PacketReader(x.C.Last(Msg.CS_CASHITEMBUY_ACK)!).ReadByte());
    }

    [Fact]
    public async Task TheCabinet_ListsItsTimeBalanceAndItems()
    {
        var x = await Open();
        x.Cash.Cabinet = new CashCabinet(1_900_000_000, 700, 20, new() { (31, Row(Wing)), (32, Row(Hood)), (33, Row(999)) });
        await Send(x, new PacketWriter(Msg.CS_CASHITEMCABINET_REQ));
        var r = new PacketReader(x.C.Last(Msg.CS_CASHITEMCABINET_ACK)!);
        Assert.Equal((1_900_000_000L, 700u, 20u), (r.ReadInt64(), r.ReadUInt32(), r.ReadUInt32()));
        Assert.Equal(2, r.ReadUInt16());                                                    // an unknown item is left out
        Assert.Equal(31u, r.ReadUInt32());
        Assert.Equal(Wing, r.ReadUInt16());
    }

    [Fact]
    public async Task TakingOut_FillsTheAskedSlot_WithTheDatabasesRow()
    {
        var x = await Open();
        x.Cash.Get = (0, Row(Wing) with { DlId = 8_718_968_878_589_280_300 });
        await Send(x, new PacketWriter(Msg.CS_CASHITEMGET_REQ).WriteUInt32(31).WriteByte(Bag).WriteByte(6));
        var item = x.Ch.FindInven(Bag)!.FindItem(6);
        Assert.Equal((Wing, 8_718_968_878_589_280_300L), (item!.TemplateId, item.DlId));
        var r = new PacketReader(x.C.Last(Msg.CS_CASHITEMGET_ACK)!);
        Assert.Equal((0, 31u), (r.ReadByte(), r.ReadUInt32()));
        Assert.True(x.C.Has(Msg.CS_ADDITEM_ACK));
    }

    [Fact]
    public async Task TakingOut_IntoAFullSlot_IsNotAsked_AndARefusalIsPassedOn()
    {
        var x = await Open();
        Give(x, Junk, 6);
        await Send(x, new PacketWriter(Msg.CS_CASHITEMGET_REQ).WriteUInt32(31).WriteByte(Bag).WriteByte(6));
        Assert.Empty(x.Cash.Log);

        x.Cash.Get = (3, default);
        await Send(x, new PacketWriter(Msg.CS_CASHITEMGET_REQ).WriteUInt32(31).WriteByte(Bag).WriteByte(7));
        Assert.Equal(3, new PacketReader(x.C.Last(Msg.CS_CASHITEMGET_ACK)!).ReadByte());
        Assert.Null(x.Ch.FindInven(Bag)!.FindItem(7));
    }

    [Fact]
    public async Task PuttingIn_MovesACabinetItem_AndRefusesTheRest()
    {
        var x = await Open();
        Give(x, Junk, 2);
        await Send(x, new PacketWriter(Msg.CS_CASHITEMPUTIN_REQ).WriteByte(Bag).WriteByte(2));
        Assert.Equal(8, new PacketReader(x.C.Last(Msg.CS_CASHITEMPUTIN_ACK)!).ReadByte());   // not a cabinet item: CASHSHOP_INTERNAL
        Assert.Empty(x.Cash.Log);

        Give(x, Wing, 3);
        await Send(x, new PacketWriter(Msg.CS_CASHITEMPUTIN_REQ).WriteByte(Bag).WriteByte(3));
        Assert.Contains("putin 0 3", x.Cash.Log);
        Assert.Null(x.Ch.FindInven(Bag)!.FindItem(3));
        var r = new PacketReader(x.C.Last(Msg.CS_CASHITEMPUTIN_ACK)!);
        Assert.Equal((0, 77u, Wing), (r.ReadByte(), r.ReadUInt32(), r.ReadUInt16()));
    }

    [Fact]
    public async Task AFailedPutIn_LeavesTheItemWhereItWas()
    {
        var x = await Open();
        Give(x, Wing, 3);
        x.Cash.Putin = (5, 0);
        await Send(x, new PacketWriter(Msg.CS_CASHITEMPUTIN_REQ).WriteByte(Bag).WriteByte(3));
        Assert.Equal(5, new PacketReader(x.C.Last(Msg.CS_CASHITEMPUTIN_ACK)!).ReadByte());
        Assert.NotNull(x.Ch.FindInven(Bag)!.FindItem(3));
        Assert.False(x.C.Has(Msg.CS_DELITEM_ACK));

        await Send(x, new PacketWriter(Msg.CS_CASHITEMPUTIN_REQ).WriteByte(Bag).WriteByte(9));
        Assert.Equal(5, new PacketReader(x.C.Last(Msg.CS_CASHITEMPUTIN_ACK)!).ReadByte());   // nothing there: CASHSHOP_NOTFOUND
    }

    [Fact]
    public async Task MoreCabinetTime_AnswersWithTheNewTime()
    {
        var x = await Open();
        await Send(x, new PacketWriter(Msg.CS_CASHCABINETBUY_REQ).WriteUInt16(10));
        var r = new PacketReader(x.C.Last(Msg.CS_CASHCABINETBUY_ACK)!);
        Assert.Equal((0, 2_000_000_000L, 800u, 50u), (r.ReadByte(), r.ReadInt64(), r.ReadUInt32(), r.ReadUInt32()));
    }

    [Fact]
    public async Task ASale_CutsTheOriginalPrice_StopsTheShop_AndIsConfirmed()
    {
        var x = await Open();
        byte[] Sale(byte value) => new PacketWriter(Msg.MW_CASHITEMSALE_REQ).WriteUInt32(3).WriteUInt16(1).WriteUInt16(1)
            .WriteUInt16(0).WriteByte(value).ToArray();
        await x.H.Service.DispatchWorldAsync(Sale(20));
        Assert.Equal(80u, x.T.CashItems[10].Money);
        await x.H.Service.DispatchWorldAsync(Sale(50));
        Assert.Equal(50u, x.T.CashItems[10].Money);                                          // from the original price, not 80
        var r = new PacketReader(x.H.World.Last(Msg.MW_CASHITEMSALE_ACK)!);
        Assert.Equal((3u, (ushort)1, (byte)1), (r.ReadUInt32(), r.ReadUInt16(), r.ReadByte()));
        await Send(x, new PacketWriter(Msg.CS_CASHSHOPITEMLIST_REQ).WriteUInt16(0));
        Assert.True(x.C.Has(Msg.CS_CASHSHOPSTOP_ACK));
    }

    [Fact]
    public async Task ACashBonusItem_GivesItsValueToTheBonus()
    {
        var x = await Open();
        Give(x, BonusCard, 4);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.ItemUseReq(BonusCard, Bag, 4));
        Assert.Contains("bonus 255 255 300", x.Cash.Log);
        Assert.Null(x.Ch.FindInven(Bag)!.FindItem(4));
    }

    [Theory]
    [InlineData((byte)1)] [InlineData((byte)3)]
    public async Task CashBonusDispatchWaitsForTheStoreAfterConsumingExactlyOneItem(byte count)
    {
        var x = await Open(); var item = Give(x, BonusCard, 4); item.Count = count;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte? observed = 255;
        x.Cash.GainHandler = async () =>
        {
            observed = x.Ch.FindInven(Bag)!.FindItem(4)?.Count;
            entered.TrySetResult(); await release.Task; return 0;
        };
        var use = x.H.Service.DispatchClientAsync(x.S, MapTestHarness.ItemUseReq(BonusCard, Bag, 4));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(use.IsCompleted);
            Assert.Equal(count == 1 ? null : (byte?)(count - 1), observed);
            Assert.True(x.C.Has(Msg.CS_ITEMUSE_ACK));
        }
        finally { release.TrySetResult(); }
        await use;
        Assert.Single(x.Cash.Log, l => l.StartsWith("bonus "));
    }
}
