using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The cash shop, batch S7b (C++ CSHandler.cpp:12826–13030, 14507–14630; SSHandler.cpp:13602–14060, 14495–14760, 16061–16135,
/// 17226–17320; CSSender.cpp:4478–4570, 4925–4960, 5521).
/// <list type="bullet">
/// <item><b>The shop</b> (<c>CS_CASHSHOPITEMLIST_REQ</c>): the balance and the tabs, each with its items for one's country (all of
/// them, or one item when asked for it); an item not for sale shows only when asked for.</item>
/// <item><b>Buying</b> (<c>CS_CASHITEMBUY_REQ</c>) puts the item in one's account cabinet; <b>a present</b>
/// (<c>CS_CASHITEMPRESENT_REQ</c>) puts it in the receiver's, who gets a letter.</item>
/// <item><b>The cabinet</b> (<c>CS_CASHITEMCABINET_REQ</c>): its paid-until time, the balance and the items; <b>take out</b>
/// (<c>CS_CASHITEMGET_REQ</c>) into an empty bag slot, <b>put in</b> (<c>CS_CASHITEMPUTIN_REQ</c>) an item that may go to a cabinet;
/// <b>more time</b> (<c>CS_CASHCABINETBUY_REQ</c>).</item>
/// <item><b>Cash bonus items</b> (<c>IK_CASH</c>): their value goes to the bonus (<c>TGainCashBonus</c>).</item>
/// <item><b>The world</b> stops or resumes the shop (<c>MW_CASHSHOPSTOP_REQ</c>) and runs sales (<c>MW_CASHITEMSALE_REQ</c>, which
/// also stops the shop, as in the C++).</item>
/// </list>
/// <para>Every cash proc runs on the database lane after the pending item writes (C++ <c>SendDM_SAVEITEM_REQ</c> first): take out
/// and put in read the item table by slot. The balance is <c>TGlobal_gsp.TCASHTESTTABLE</c>; migration 022 fixes the baseline procs
/// (buying gave nothing, cabinet time could not be bought, the bonus went to a missing linked server).</para>
/// <para><b>C++ bugs fixed:</b> a sale event cut the already-cut price again (the original price was read from the sale price); a cash
/// bonus item whose proc failed was lost (its letter back was commented out); put in took the item out of the bag before the
/// database answered, and gave it back elsewhere or by mail when it failed — here it stays in its slot until the move is done. The
/// security-code lock the C++ checks first is not ported (no security codes): a player is treated as having none.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte CashShopSuccess = 0, CashShopNoTarget = 4, CashShopNotFound = 5, CashShopInternal = 8;   // CASHITEM_RESULT
    private const byte CashItemBuy = 1, CashItemPresent = 2;                                    // CASHITEM_BUY / CASHITEM_PRESENT
    private const uint MsgPresentTitle = 9, MsgPresentMessage = 10, MsgItemErrorTitle = 11, MsgItemErrorMessage = 12;

    /// <summary>C++ <c>m_bCashShopStop</c>.</summary>
    private byte _cashShopStop;

    /// <summary>A cash proc, after the pending item writes, on the database lane; <paramref name="fallback"/> without a store.</summary>
    private async Task<T> CashCall<T>(Func<ICashStore, Task<T>> call, T fallback)
    {
        if (CashStore is not { } db) return fallback;
        FlushItemDirect();
        T result = fallback;
        await EnqueueDbWrite(async () => result = await call(db));
        return result;
    }

    private void SendCashShopStop(ClientSession s, byte stop) => s.Send(new PacketWriter(Msg.CS_CASHSHOPSTOP_ACK).WriteByte(stop));

    private static void SendCashItemBuyAck(ClientSession s, byte result, uint cash, uint bonus)
        => s.Send(new PacketWriter(Msg.CS_CASHITEMBUY_ACK).WriteByte(result).WriteUInt32(cash).WriteUInt32(bonus));

    // ================================ the shop ================================

    /// <summary>C++ <c>OnCS_CASHSHOPITEMLIST_REQ</c> → <c>TCashGet</c> → <c>SendCS_CASHSHOPITEMLIST_ACK</c>.</summary>
    private async Task OnCS_CASHSHOPITEMLIST_REQ(ClientSession s, PacketReader r)
    {
        ushort itemId = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTutorial(ch)) return;
        if (_cashShopStop != 0) { SendCashShopStop(s, _cashShopStop); return; }

        var (cash, bonus) = await CashCall(db => db.GetCashAsync(s.UserId), (0u, 0u));
        if (s.Char != ch) return;
        var w = new PacketWriter(Msg.CS_CASHSHOPITEMLIST_ACK);
        w.WriteUInt32(cash); w.WriteUInt32(bonus); w.WriteByte((byte)_templates.CashCategories.Count);
        foreach (var cat in _templates.CashCategories)
        {
            w.WriteString(cat.Name);
            var shown = new List<(CashItem Cash, Item Item)>();
            foreach (var ci in cat.Items)
            {
                if (ItemFromRow(ci.Item) is not { Template: { } t } item || ci.Item.TemplateId == 0) continue;
                if ((itemId == 0 && ci.CanSell == 0) || (t.ItemCountry != ch.Country && t.ItemCountry != TcontryN)
                    || (itemId != 0 && itemId != ci.Item.TemplateId)) continue;
                shown.Add((ci, item));
            }
            w.WriteUInt16((ushort)shown.Count);
            foreach (var (ci, item) in shown)
            {
                w.WriteUInt16(ci.Id); w.WriteUInt16(ci.InfoId); w.WriteUInt32(ci.Money); w.WriteByte(ci.Kind);
                item.WrapPacketClient(w, ch.CharId, addItemId: false, cashUseTime: ci.UseTime);
            }
        }
        s.Send(w);
    }

    /// <summary>C++ <c>OnCS_CASHITEMBUY_REQ</c> → <c>TCashItemBuy</c> → <c>OnDM_CASHITEMBUY_ACK</c>.</summary>
    private async Task OnCS_CASHITEMBUY_REQ(ClientSession s, PacketReader r)
    {
        ushort cashItemId = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTutorial(ch)) return;
        if (_cashShopStop != 0) { SendCashShopStop(s, _cashShopStop); return; }
        await CashBuy(s, ch, cashItemId, CashItemBuy, "");
    }

    /// <summary>C++ <c>OnCS_CASHITEMPRESENT_REQ</c>: bought for someone else, who gets a letter.</summary>
    private async Task OnCS_CASHITEMPRESENT_REQ(ClientSession s, PacketReader r)
    {
        string target = r.ReadString();
        ushort cashItemId = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTutorial(ch)) return;
        if (_cashShopStop != 0) { SendCashShopStop(s, _cashShopStop); return; }
        if (target.Length == 0 || target.Length > MaxName) { SendCashItemBuyAck(s, 2, 0, 0); return; }   // (sic) 2 = CASHSHOP_NOCASHINFO
        await CashBuy(s, ch, cashItemId, CashItemPresent, target);
    }

    private async Task CashBuy(ClientSession s, Character ch, ushort cashItemId, byte buyType, string target)
    {
        var res = await CashCall(db => db.CashItemBuyAsync(s.UserId, ch.CharId, cashItemId, buyType, target), null);
        if (s.Char != ch) return;
        byte ret = res is null ? CashShopInternal : (byte)res.Ret;
        if (ret != 0 || res is null) { SendCashItemBuyAck(s, ret, 0, 0); return; }
        if (buyType == CashItemPresent)
            _ = MailFrom(0, SvrMsg(MsgNameOperator), res.TargetCharId, target, FormatSvrMsg(SvrMsg(MsgPresentTitle), ch.Name),
                FormatSvrMsg(SvrMsg(MsgPresentMessage), ch.Name, res.Present), PostNormal);
        SendCashItemBuyAck(s, 0, res.Cash, res.Bonus);
    }

    // ================================ the cabinet ================================

    /// <summary>C++ <c>OnCS_CASHITEMCABINET_REQ</c> → <c>OnDM_CASHITEMCABINET_REQ/ACK</c>.</summary>
    private async Task OnCS_CASHITEMCABINET_REQ(ClientSession s)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTutorial(ch)) return;
        var cab = await CashCall(db => db.CabinetAsync(ch.CharId, s.UserId), new CashCabinet(0, 0, 0, new()));
        if (s.Char != ch) return;
        var w = new PacketWriter(Msg.CS_CASHITEMCABINET_ACK);
        w.WriteInt64(cab.UseTime); w.WriteUInt32(cab.Cash); w.WriteUInt32(cab.Bonus);
        var items = cab.Items.Select(c => (c.Id, Item: ItemFromRow(c.Item))).Where(c => c.Item is not null).ToList();
        w.WriteUInt16((ushort)items.Count);
        foreach (var (id, item) in items) { w.WriteUInt32(id); item!.WrapPacketClient(w, ch.CharId, addItemId: false); }
        s.Send(w);
    }

    /// <summary>C++ <c>OnCS_CASHITEMGET_REQ</c> → <c>TCashItemGet</c> → <c>OnDM_CASHITEMGET_ACK</c>: a cabinet item into an empty bag
    /// slot (the database puts its row there); with the slot gone, anywhere it fits, else by letter.</summary>
    private async Task OnCS_CASHITEMGET_REQ(ClientSession s, PacketReader r)
    {
        uint cashItemId = r.ReadUInt32();
        byte invenId = r.ReadByte(), slot = r.ReadByte();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTutorial(ch) || IsTournamentChar(ch)) return;
        if (invenId == Proto.InvenEquip || ch.FindInven(invenId) is not { } bag || !bag.ContainsSlot(slot) || bag.FindItem(slot) is not null) return;

        var res = await CashCall(db => db.CashItemGetAsync(s.UserId, ch.CharId, cashItemId, invenId, slot), null);
        byte ret = res is null ? CashShopInternal : (byte)res.Value.Ret;
        var item = ret == 0 && res is { } got ? ItemFromRow(got.Item) : null;
        bool here = s.Char == ch && s.State == EnterState.InGame;
        if (ret == 0 && item is null) ret = CashShopInternal;
        if (!here)
        {
            if (item is not null) _ = MailItemBack(ch.CharId, ch.Name, item);
            return;
        }
        if (ret != 0) { SendCashItemGetAck(s, ret, cashItemId); return; }

        if (ch.FindInven(invenId) is { } b && b.ContainsSlot(slot) && b.FindItem(slot) is null)
        {
            item!.ItemSlot = slot;
            b.Items.Add(item);
            SendCS_ADDITEM_ACK(s, invenId, item);
        }
        else if (CanPush(ch, new[] { item! })) PushTItem(s, new[] { item! });
        else _ = MailItemBack(ch.CharId, ch.Name, item!);
        SendCashItemGetAck(s, CashShopSuccess, cashItemId);
    }

    private static void SendCashItemGetAck(ClientSession s, byte result, uint cashItemId)
        => s.Send(new PacketWriter(Msg.CS_CASHITEMGET_ACK).WriteByte(result).WriteUInt32(cashItemId));

    /// <summary>C++ <c>OnCS_CASHITEMPUTIN_REQ</c> → <c>TCashItemPutin</c> → <c>OnDM_CASHITEMPUTIN_ACK</c>: a bag item that may go
    /// to a cabinet moves to the account's cabinet (the database moves its row).</summary>
    private async Task OnCS_CASHITEMPUTIN_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTournamentChar(ch)) return;
        void Ack(byte result, uint newId = 0, Item? item = null)
        {
            var w = new PacketWriter(Msg.CS_CASHITEMPUTIN_ACK);
            w.WriteByte(result); w.WriteUInt32(newId);
            if (result == 0 && item is not null) item.WrapPacketClient(w, ch.CharId, addItemId: false);
            s.Send(w);
        }
        if (IsTutorial(ch)) { Ack(CashShopInternal); return; }
        byte invenId = r.ReadByte(), slot = r.ReadByte();
        if (s.Store.IsOpen || s.Deal.Status >= DealStart) { Ack(CashShopInternal); return; }
        if (ch.FindInven(invenId) is not { } bag || bag.FindItem(slot) is not { Count: > 0 } item) { Ack(CashShopNotFound); return; }
        if (((item.Template?.IsSell ?? 0) & ItemTradeCabinet) == 0) { Ack(CashShopInternal); return; }

        var res = await CashCall(db => db.CashItemPutinAsync(ch.CharId, invenId, slot), null);
        if (s.Char != ch) return;
        byte ret = res is null ? CashShopInternal : (byte)res.Value.Ret;
        if (ret != 0) { Ack(ret); return; }
        if (bag.Items.Remove(item)) SendCS_DELITEM_ACK(s, invenId, item);                      // its row is in the cabinet now
        Ack(CashShopSuccess, res!.Value.NewId, item);
    }

    /// <summary>C++ <c>OnCS_CASHCABINETBUY_REQ</c> → <c>TCashCabinetBuy</c> → <c>SendCS_CASHCABINETBUY_ACK</c>.</summary>
    private async Task OnCS_CASHCABINETBUY_REQ(ClientSession s, PacketReader r)
    {
        ushort shopItemId = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTutorial(ch) || IsTournamentChar(ch)) return;
        var res = await CashCall(db => db.CashCabinetBuyAsync(ch.CharId, shopItemId), null);
        if (s.Char != ch) return;
        var (ret, time, cash, bonus) = res ?? ((int)CashShopInternal, 0L, 0u, 0u);
        s.Send(new PacketWriter(Msg.CS_CASHCABINETBUY_ACK).WriteByte((byte)ret).WriteInt64(time).WriteUInt32(cash).WriteUInt32(bonus));
    }

    // ================================ cash bonus items ================================

    /// <summary>C++ <c>IK_CASH</c> → <c>TGainCashBonus</c> → <c>OnDM_GAINCASHBONUS_ACK</c>: the item (already used up here) gives its
    /// value to the bonus. The proc's own row delete is skipped (slot 0xFF): the map's item writes already persist the use, and the
    /// proc deleted a whole stack. When the proc fails, the item comes back by letter (the C++ dropped it).</summary>
    private async Task GainCashBonus(ClientSession s, Character ch, ItemTemplate t)
    {
        int? ret = await CashCall(db => db.GainCashBonusAsync(ch.CharId, InvalidSlot, InvalidSlot, t.UseValue), (int?)0);
        if (ret is not 0)
            await MailOperatorItem(ch.CharId, ch.Name, NetString(SvrMsg(MsgItemErrorTitle)), NetString(SvrMsg(MsgItemErrorMessage)), t, 1);
    }

    /// <summary>C++ <c>SendDM_POSTRECV_REQ(…, MSG_ITEMERROR_TITLE, MSG_ITEMERROR_MESSAGE, POST_PACKATE, …, pItem)</c>: an item that found
    /// no place, sent back by the operator (its row moves to the letter).</summary>
    private async Task MailItemBack(uint charId, string name, Item item)
    {
        if (PostStore is not { } db) return;
        string sender = SvrMsg(MsgNameOperator), title = NetString(SvrMsg(MsgItemErrorTitle)), body = NetString(SvrMsg(MsgItemErrorMessage));
        long now = UnixNow();
        (int saved, uint postId, _) = await Safe(() => db.SavePostAsync(0, charId, name, sender, title, body, 0, PostPackage, 0, 0, 0, now),
            (PostInternal, 0u, 0u));
        if (saved != 0 || postId == 0) return;
        if (item.DlId == 0) item.DlId = GenItemId();
        var row = BuildItemSave(0, item) with { StorageType = Proto.StoragePost, StorageId = postId, ItemSlot = 0 };
        await Safe(async () => { await db.SavePostItemAsync(charId, row); return 0; }, 0);
        NotifyPostRecv(postId, sender, name, title, PostPackage, now);
    }

    // ================================ the world ================================

    /// <summary>C++ <c>OnMW_CASHSHOPSTOP_REQ</c>: stop (or resume) the shop; tell everyone when asked to.</summary>
    private void OnMW_CASHSHOPSTOP_REQ(PacketReader r)
    {
        byte stop = r.ReadByte(), sendPlayer = r.ReadByte();
        _cashShopStop = stop;
        if (sendPlayer == 0) return;
        foreach (var s in _state.AllInGame()) if (s.IsMain) SendCashShopStop(s, 1);
    }

    /// <summary>C++ <c>OnMW_CASHITEMSALE_REQ</c>: the sale's items (id 0: every item) at their original price less the sale, the
    /// shop stopped, the world told.</summary>
    private void OnMW_CASHITEMSALE_REQ(PacketReader r)
    {
        uint index = r.ReadUInt32();
        ushort value = r.ReadUInt16(), count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            ushort id = r.ReadUInt16();
            byte sale = r.ReadByte();
            var items = id == 0 ? _templates.CashItems.Values.ToList()
                : _templates.CashItems.TryGetValue(id, out var one) ? new List<CashItem> { one } : new List<CashItem>();
            foreach (var ci in items) { ci.Money = (uint)((ulong)ci.OriMoney * (100u - Math.Min((byte)100, sale)) / 100); ci.SaleValue = sale; }
        }
        _cashShopStop = 1;
        var w = new PacketWriter(Msg.MW_CASHITEMSALE_ACK);
        w.WriteUInt32(index); w.WriteUInt16(value); w.WriteByte(1);
        _world.Send(w);
    }
}
