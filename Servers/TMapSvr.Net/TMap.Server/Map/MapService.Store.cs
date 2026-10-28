using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Personal STORE (player vendor) — the C# port of the C++ inline store handlers (<c>OnCS_STORE*</c>,
/// CSHandler.cpp:11147-11545) + <c>m_bStore</c>/<c>m_strStoreName</c>/<c>m_mapStoreItem</c>. A same-map,
/// in-memory listing: the seller <b>opens</b> a store over a set of their own bag items at prices (the items
/// stay in the bag, only referenced), nearby players <b>browse</b> (<c>CS_STOREITEMLIST</c>) and <b>buy</b>
/// (<c>CS_STOREITEMBUY</c> — money buyer→seller, the item copied to the buyer, the offer + seller stack
/// decremented). Open/close broadcast to the 3×3 neighbors; the store flag also rides <c>CS_ENTER_ACK</c> so a
/// late arrival sees the icon. The store auto-closes when the last offer sells out.
///
/// <para><b>Deferred (documented, PORT_STATUS.md):</b> the account secure-code gate, the riding/transform open
/// guards, and the movement-lock while storing; the <c>TSTORE_SKILL</c> (804) "storing" buff (visual — the buff
/// engine no-ops an unknown skill DB-free); the faction/free-trade-zone (<c>GetWarCountry</c>) browse/buy gate;
/// and the <b>credits</b> (PvP-point) alternate price path — a credits-priced offer can't be bought here
/// (returns NEEDMONEY). A self-buy is rejected (a port safety; the C++ has no such guard).</para>
/// </summary>
public sealed partial class MapService
{
    // ==================== open / close ====================

    /// <summary>C++ <c>OnCS_STOREOPEN_REQ</c> (CSHandler.cpp:11147) — open a store over the offered bag items.
    /// Validates each (owned / tradable / not duplicated / enough count); records references (items stay in the
    /// bag). Broadcasts <c>CS_STOREOPEN_ACK</c> to the neighbors (self gets it + its own item list).</summary>
    private void OnCS_STOREOPEN_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        string name = r.ReadString();
        byte count = r.ReadByte();
        var e = new (uint gold, uint silver, uint cooper, uint credits, byte inven, byte slot, byte cnt)[count];
        for (int i = 0; i < count; i++)
            e[i] = (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadByte(), r.ReadByte(), r.ReadByte());

        if (s.Store.IsOpen || s.Deal.InProgress || ch.Riding != 0) return;        // C++ silent (riding/store/deal)
        if (string.IsNullOrEmpty(name) || count == 0) { SendCS_STOREOPEN_ACK(s, StoreResult.Fail, 0, ""); return; }

        s.Store.Clear();
        StoreResult result = StoreResult.Success;
        for (byte i = 0; i < count; i++)
        {
            var inv = ch.FindInven(e[i].inven);
            if (inv is null) { result = StoreResult.ItemNoItem; break; }
            var item = inv.FindItem(e[i].slot);
            if (item is null) { result = StoreResult.ItemNoItem; break; }
            if (!item.CanDeal()) { result = StoreResult.ItemNotDeal; break; }
            if (s.Store.Items.Any(x => x.Inven == e[i].inven && x.ItemSlot == e[i].slot)) { result = StoreResult.ItemNotDeal; break; } // CheckStoreItem dedup
            if (e[i].cnt == 0 || item.Count < e[i].cnt) { result = StoreResult.ItemNoItemCount; break; }
            s.Store.Items.Add(new StoreItem
            {
                SlotKey = i, Gold = e[i].gold, Silver = e[i].silver, Cooper = e[i].cooper, Credits = e[i].credits,
                Inven = e[i].inven, ItemSlot = e[i].slot, Count = e[i].cnt,
                SourceItem = item, Snapshot = item.Clone(),
            });
        }

        if (result != StoreResult.Success) { s.Store.Clear(); SendCS_STOREOPEN_ACK(s, result, 0, ""); return; }

        s.Store.IsOpen = true;
        s.Store.Name = name;
        SendCS_STOREOPEN_ACK(s, StoreResult.Success, ch.CharId, name);            // to self
        SendCS_STOREITEMLIST_ACK(s, s);                                           // the seller's own list
        foreach (var p in _state.Neighbors(s)) SendCS_STOREOPEN_ACK(p, StoreResult.Success, ch.CharId, name);  // neighbors (excl self)
    }

    /// <summary>C++ <c>OnCS_STORECLOSE_REQ</c> → <c>StoreClose</c> (TPlayer.cpp:4135) — tear the store down and
    /// broadcast <c>CS_STORECLOSE_ACK</c> to the 3×3 neighbors <b>including self</b>. Offered items were never
    /// removed, so there is nothing to return.</summary>
    private void OnCS_STORECLOSE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is null) return;
        StoreClose(s);
    }

    private void StoreClose(ClientSession s)
    {
        if (!s.Store.IsOpen) return;
        uint id = s.CharId;
        s.Store.Clear();
        foreach (var p in _state.InView(s)) SendCS_STORECLOSE_ACK(p, StoreResult.Success, id);   // neighbors + self
    }

    // ==================== browse / buy ====================

    /// <summary>C++ <c>OnCS_STOREITEMLIST_REQ</c> (CSHandler.cpp:11321) — browse a seller's store by name. Silent
    /// if the target isn't storing. (The faction/free-trade-zone gate is deferred.)</summary>
    private void OnCS_STOREITEMLIST_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is null) return;
        var t = FindByName(r.ReadString());
        if (t is null || !t.IsMain || t.Char is null || !t.Store.IsOpen) return;
        SendCS_STOREITEMLIST_ACK(s, t);
    }

    /// <summary>C++ <c>OnCS_STOREITEMBUY_REQ</c> (CSHandler.cpp:11349) — buy <c>count</c> of the seller's offer
    /// <c>slotKey</c>: guard money + bag space (<see cref="CanPush"/>) before any mutation, then transfer the
    /// item copy to the buyer and the money buyer→seller, decrement the offer + the seller's live stack, notify
    /// the seller (<c>SELL</c> + item ack + money), and auto-close the store when its last offer is gone.</summary>
    private void OnCS_STOREITEMBUY_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } buyer) return;
        string target = r.ReadString();
        byte slotKey = r.ReadByte();
        byte count = r.ReadByte();

        var t = FindByName(target);
        if (t is null || t == s || !t.IsMain || t.Char is not { } seller || !t.Store.IsOpen)   // self-buy rejected (port safety)
        { SendCS_STOREITEMBUY_ACK(s, StoreResult.Fail, 0, 0); return; }

        var offer = t.Store.Find(slotKey);
        if (offer is null) { SendCS_STOREITEMBUY_ACK(s, StoreResult.ItemNoItem, 0, 0); return; }
        var sellInv = seller.FindInven(offer.Inven);
        var sellItem = sellInv?.FindItem(offer.ItemSlot);
        if (!offer.Matches(sellItem))
        {
            StoreClose(t);
            SendCS_STOREITEMBUY_ACK(s, StoreResult.ItemNoItem, 0, 0);
            return;
        }
        if (count == 0 || offer.Count < count) { SendCS_STOREITEMBUY_ACK(s, StoreResult.ItemNoItemCount, 0, 0); return; }
        if (offer.Credits != 0) { SendCS_STOREITEMBUY_ACK(s, StoreResult.ItemNeedMoney, 0, 0); return; }   // credits path deferred

        long money = offer.UnitPrice * count;
        if (!buyer.UseMoney(money, commit: false)) { SendCS_STOREITEMBUY_ACK(s, StoreResult.ItemNeedMoney, 0, 0); return; }

        var bought = sellItem!.Clone();
        bought.Count = count;
        bought.DlId = 0;                                   // fresh row for the buyer
        ushort itemId = bought.TemplateId;
        var one = new[] { bought };
        if (!CanPush(buyer, one)) { SendCS_STOREITEMBUY_ACK(s, StoreResult.ItemInvenFull, 0, 0); return; }

        // Commit (money proven affordable + bag proven to fit — no rollback needed).
        PushTItem(s, one);
        buyer.UseMoney(money, commit: true);
        SendCS_MONEY_ACK(s, buyer);

        offer.Count -= count;
        sellItem.Count -= count;
        if (sellItem.Count == 0) { sellInv!.Items.Remove(sellItem); SendCS_DELITEM_ACK(t, offer.Inven, sellItem); }
        else SendCS_UPDATEITEM_ACK(t, offer.Inven, sellItem);
        seller.EarnMoney(money);
        SendCS_MONEY_ACK(t, seller);
        SendCS_STOREITEMSELL_ACK(t, slotKey, count);

        if (offer.Count == 0)
        {
            t.Store.Items.Remove(offer);
            if (t.Store.Items.Count == 0) StoreClose(t);
        }
        if (t.Store.IsOpen) SendCS_STOREITEMLIST_ACK(s, t);          // refresh the buyer's list (C++ 11541)
        SendCS_STOREITEMBUY_ACK(s, StoreResult.Success, itemId, count);
    }

    // ==================== senders ====================

    /// <summary>C++ <c>SendCS_STOREOPEN_ACK</c> (CSSender.cpp:4113) — <c>{ bResult, dwCharID, strName }</c>.</summary>
    private static void SendCS_STOREOPEN_ACK(ClientSession s, StoreResult result, uint charId, string name)
    {
        var w = new PacketWriter(Msg.CS_STOREOPEN_ACK, capacity: 64);
        w.WriteByte((byte)result);
        w.WriteUInt32(charId);
        w.WriteString(name);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_STORECLOSE_ACK</c> (CSSender.cpp:4125) — <c>{ bResult, dwCharID }</c>.</summary>
    private static void SendCS_STORECLOSE_ACK(ClientSession s, StoreResult result, uint charId)
    {
        var w = new PacketWriter(Msg.CS_STORECLOSE_ACK, capacity: 8);
        w.WriteByte((byte)result);
        w.WriteUInt32(charId);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_STOREITEMLIST_ACK</c> (CSSender.cpp:4136) — <c>dwCharID</c> (seller), <c>strName</c>,
    /// <c>bCount</c>, then per live offer <c>{ slotKey, credits, gold, silver, cooper, item block }</c> (the item
    /// block via <see cref="Item.WrapPacketClient"/> with <c>addItemId=false</c>). Only offers whose seller item
    /// still exists are listed (the C++ <c>FindTInven</c>/<c>FindTItem</c> gate).</summary>
    private static void SendCS_STOREITEMLIST_ACK(ClientSession recipient, ClientSession owner)
    {
        var st = owner.Store;
        var seller = owner.Char!;
        var live = st.Items.Where(o => o.Matches(seller.FindInven(o.Inven)?.FindItem(o.ItemSlot))).ToList();
        var w = new PacketWriter(Msg.CS_STOREITEMLIST_ACK, capacity: 128);
        w.WriteUInt32(owner.CharId);
        w.WriteString(st.Name);
        w.WriteByte((byte)live.Count);
        foreach (var o in live)
        {
            var item = seller.FindInven(o.Inven)!.FindItem(o.ItemSlot)!;
            w.WriteByte(o.SlotKey);
            w.WriteUInt32(o.Credits);
            w.WriteUInt32(o.Gold);
            w.WriteUInt32(o.Silver);
            w.WriteUInt32(o.Cooper);
            item.WrapPacketClient(w, recipient.CharId, addItemId: false);
        }
        recipient.Send(w);
    }

    /// <summary>C++ <c>SendCS_STOREITEMBUY_ACK</c> (CSSender.cpp:4181) — <c>{ bResult, wItemID, bCount }</c> (buyer).</summary>
    private static void SendCS_STOREITEMBUY_ACK(ClientSession s, StoreResult result, ushort itemId, byte count)
    {
        var w = new PacketWriter(Msg.CS_STOREITEMBUY_ACK, capacity: 8);
        w.WriteByte((byte)result);
        w.WriteUInt16(itemId);
        w.WriteByte(count);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_STOREITEMSELL_ACK</c> (CSSender.cpp:4193) — <c>{ bItem(slotKey), bCount }</c> (seller).</summary>
    private static void SendCS_STOREITEMSELL_ACK(ClientSession s, byte item, byte count)
    {
        var w = new PacketWriter(Msg.CS_STOREITEMSELL_ACK, capacity: 4);
        w.WriteByte(item);
        w.WriteByte(count);
        s.Send(w);
    }
}
