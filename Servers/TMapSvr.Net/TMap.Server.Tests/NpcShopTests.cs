using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Phase 23 — NPC shops: talk to a shop NPC (CS_NPCTALK), buy stocked items for gold
/// (CS_ITEMBUY, price = level-money·fPrice), and sell inventory items back at ¼ price (CS_ITEMSELL). Country
/// gating (<see cref="Npc.CanTalk"/>) blocks a hostile NPC. Quests / discount / PvP-point / BoW are deferred.</summary>
public class NpcShopTests
{
    private const ushort ShopItemId = 200;   // sellable consumable, default level 5, price 2.0, stack 10
    private const ushort NoSellItemId = 201; // same but ITEMTRADE_SELL clear ⇒ cannot be sold
    private const ushort NpcId = 500;

    // grade-5 base money 100 × price 2.0 ⇒ unit price (uint)(100·2.0 + 0.99) = 200; sell = 200/4 = 50.
    private static TemplateStore ShopStore()
    {
        var t = new TemplateStore();
        t.Items[ShopItemId] = new ItemTemplate(ShopItemId, 0, new[] { 0f, 0f, 0f, 0f },
            DefaultLevel: 5, Stack: 10, Price: 2.0f, IsSell: 2);   // IsSell = ITEMTRADE_SELL
        t.Items[NoSellItemId] = new ItemTemplate(NoSellItemId, 0, new[] { 0f, 0f, 0f, 0f },
            DefaultLevel: 5, Stack: 10, Price: 2.0f, IsSell: 0);   // not sellable
        t.LevelMoney[5] = 100;
        return t;
    }

    private static Character Shopper()
    {
        var ch = new Character { CharId = 1, Name = "Buyer", MaxHp = 100, Hp = 100, Country = 1 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        return ch;
    }

    // A neutral (TCONTRY_N) item NPC stocking ShopItemId, registered on the running service.
    private static Npc ShopNpc(TemplateStore store, byte country = 3)
    {
        var npc = new Npc { Id = NpcId, Type = 2, Country = country, MapId = 0 };
        npc.Items[ShopItemId] = store.Item(ShopItemId)!;
        return npc;
    }

    private async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c)> Enter(
        TemplateStore store, Character ch)
    {
        var h = new MapTestHarness(store);
        var (s, c) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        s.Char!.Country = 1; s.Char.AidCountry = 0;
        c.Clear();
        return (h, s, c);
    }

    [Fact]
    public async Task Talk_NeutralNpc_SendsAck()
    {
        var store = ShopStore();
        var (h, s, c) = await Enter(store, Shopper());
        h.Service.AddNpc(ShopNpc(store));

        await h.Service.DispatchClientAsync(s, MapTestHarness.NpcTalkReq(NpcId));

        var r = new PacketReader(c.Last(Msg.CS_NPCTALK_ACK)!);
        Assert.Equal(0u, r.ReadUInt32());        // dwQuestID (quests unported ⇒ plain talk)
        Assert.Equal(NpcId, r.ReadUInt16());
    }

    [Fact]
    public async Task Talk_HostileCountry_NoAck()
    {
        var store = ShopStore();
        var (h, s, c) = await Enter(store, Shopper());
        h.Service.AddNpc(ShopNpc(store, country: 2)); // TCONTRY_B, player is country 1 / aid 0 ⇒ CanTalk false

        await h.Service.DispatchClientAsync(s, MapTestHarness.NpcTalkReq(NpcId));

        Assert.False(c.Has(Msg.CS_NPCTALK_ACK));
    }

    [Fact]
    public async Task Buy_DeductsMoney_AddsItem()
    {
        var store = ShopStore();
        var (h, s, c) = await Enter(store, Shopper());
        s.Char!.SetMoneyTotal(1000);
        h.Service.AddNpc(ShopNpc(store));

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(NpcId, ShopItemId, count: 2));

        Assert.Equal((byte)ItemBuyResult.Success, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Equal(1000L - 400L, s.Char.MoneyTotal);   // 2 × unit-price 200
        var bag = s.Char.FindInven(0xFF)!;
        Assert.Single(bag.Items);
        Assert.Equal(ShopItemId, bag.Items[0].TemplateId);
        Assert.Equal((byte)2, bag.Items[0].Count);
    }

    [Fact]
    public async Task Buy_NotEnoughMoney_NeedMoney()
    {
        var store = ShopStore();
        var (h, s, c) = await Enter(store, Shopper());
        s.Char!.SetMoneyTotal(0);
        h.Service.AddNpc(ShopNpc(store));

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(NpcId, ShopItemId, count: 1));

        Assert.Equal((byte)ItemBuyResult.NeedMoney, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Empty(s.Char.FindInven(0xFF)!.Items);
        Assert.Equal(0L, s.Char.MoneyTotal);
    }

    [Fact]
    public async Task Buy_ItemNotStocked_NotFound()
    {
        var store = ShopStore();
        var (h, s, c) = await Enter(store, Shopper());
        s.Char!.SetMoneyTotal(1000);
        h.Service.AddNpc(ShopNpc(store)); // stocks ShopItemId only

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(NpcId, NoSellItemId, count: 1));

        Assert.Equal((byte)ItemBuyResult.NotFound, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Empty(s.Char.FindInven(0xFF)!.Items);
    }

    [Fact]
    public async Task Buy_UnknownNpc_NotFound()
    {
        var store = ShopStore();
        var (h, s, c) = await Enter(store, Shopper());
        s.Char!.SetMoneyTotal(1000);   // no NPC registered

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(NpcId, ShopItemId, count: 1));

        Assert.Equal((byte)ItemBuyResult.NotFound, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
    }

    [Fact]
    public async Task Buy_UnknownQuestId_DoesNotBypassPayment()
    {
        var store = ShopStore();
        var (h, s, c) = await Enter(store, Shopper());
        s.Char!.SetMoneyTotal(0);
        h.Service.AddNpc(ShopNpc(store));

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(NpcId, ShopItemId, count: 1, questId: 42));

        Assert.Equal((byte)ItemBuyResult.NotFound, new PacketReader(c.Last(Msg.CS_ITEMBUY_ACK)!).ReadByte());
        Assert.Empty(s.Char.FindInven(0xFF)!.Items);
        Assert.Equal(0L, s.Char.MoneyTotal);
    }

    private static Item SellItem(TemplateStore store, byte slot, byte count, ushort tempId = ShopItemId)
        => new() { ItemSlot = slot, TemplateId = tempId, Count = count,
            Template = store.Item(tempId), Attr = new ItemAttr(0, 0, 5, 0, 0, 0, 0, 0, 0, 0) };

    [Fact]
    public async Task Sell_FullStack_EarnsMoney_RemovesItem()
    {
        var store = ShopStore();
        var ch = Shopper();
        ch.FindInven(0xFF)!.Items.Add(SellItem(store, slot: 3, count: 1));
        var (h, s, c) = await Enter(store, ch);
        s.Char!.SetMoneyTotal(0);

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemSellReq(0xFF, 3, count: 1));

        Assert.Equal((byte)ItemSellResult.Success, new PacketReader(c.Last(Msg.CS_ITEMSELL_ACK)!).ReadByte());
        Assert.Equal(50L, s.Char.MoneyTotal);   // unit 200 / 4 × 1
        Assert.Empty(s.Char.FindInven(0xFF)!.Items);
        Assert.True(c.Has(Msg.CS_DELITEM_ACK));
    }

    [Fact]
    public async Task Sell_PartialStack_DecrementsAndEarns()
    {
        var store = ShopStore();
        var ch = Shopper();
        ch.FindInven(0xFF)!.Items.Add(SellItem(store, slot: 3, count: 5));
        var (h, s, c) = await Enter(store, ch);
        s.Char!.SetMoneyTotal(0);

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemSellReq(0xFF, 3, count: 2));

        Assert.Equal((byte)ItemSellResult.Success, new PacketReader(c.Last(Msg.CS_ITEMSELL_ACK)!).ReadByte());
        Assert.Equal(100L, s.Char.MoneyTotal);   // 50 × 2
        var it = s.Char.FindInven(0xFF)!.FindItem(3)!;
        Assert.Equal((byte)3, it.Count);
        Assert.True(c.Has(Msg.CS_UPDATEITEM_ACK));
    }

    [Fact]
    public async Task Sell_NotSellable_CantSell()
    {
        var store = ShopStore();
        var ch = Shopper();
        ch.FindInven(0xFF)!.Items.Add(SellItem(store, slot: 3, count: 1, tempId: NoSellItemId));
        var (h, s, c) = await Enter(store, ch);
        s.Char!.SetMoneyTotal(0);

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemSellReq(0xFF, 3, count: 1));

        Assert.Equal((byte)ItemSellResult.CantSell, new PacketReader(c.Last(Msg.CS_ITEMSELL_ACK)!).ReadByte());
        Assert.Single(s.Char.FindInven(0xFF)!.Items);   // still there
        Assert.Equal(0L, s.Char.MoneyTotal);
    }

    [Fact]
    public async Task Sell_EquippedContainer_Ignored()
    {
        var store = ShopStore();
        var (h, s, c) = await Enter(store, Shopper());

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemSellReq(0xFE, 0, count: 1)); // INVEN_EQUIP

        Assert.False(c.Has(Msg.CS_ITEMSELL_ACK));
    }

    [Fact]
    public void InitNpcs_ResolvesShopStock()
    {
        var store = ShopStore();
        var def = new NpcDef(NpcId, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        def.ItemIds.Add(ShopItemId);
        def.ItemIds.Add(9999);   // no template ⇒ silently skipped
        store.Npcs[NpcId] = def;
        var h = new MapTestHarness(store);

        h.Service.InitNpcs();

        var npc = h.State.FindNpc(NpcId);
        Assert.NotNull(npc);
        Assert.Single(npc!.Items);
        Assert.True(npc.Items.ContainsKey(ShopItemId));
    }
}
