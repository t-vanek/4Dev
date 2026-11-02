using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The player CABINET (item warehouse) — the C# port of C++ <c>CTPlayer::m_mapCabinet</c> and the five
/// <c>OnCS_CABINET*</c> handlers (CSHandler.cpp:5528-5902) + the transfer core (<c>PutinCabinetItem</c>/
/// <c>TakeoutStorageItem</c>, TPlayer.cpp:1542-1715). Per-character, in-memory (no world round-trip):
/// up to <see cref="CabinetCount"/> cabinets (ids 0/1/2), each holding up to <see cref="CabinetStorageMax"/>
/// items keyed by a per-cabinet <see cref="Item.StItemId"/>.
///
/// <para><b>Persistence:</b> loaded on enter (<see cref="MapService.LoadCharDataAsync"/> — headers from
/// <c>TCABINETTABLE</c>, items from <c>TITEMTABLE</c> <c>bStorageType=STORAGE_CABINET</c>) and saved
/// incrementally: a deposited/merged cabinet item is enqueued as an incremental item upsert with
/// <c>STORAGE_CABINET</c>/<c>StItemId</c>/cabinetId (<see cref="EnqueueCabinetItemSave"/>), a withdrawn one as an
/// incremental delete; the cabinet open-state (<c>bUse</c>) is written on open (<c>TSaveCabinet</c>). The full
/// inventory snapshot is scoped to <c>bStorageType=0</c>, so it never clobbers cabinet rows.</para>
///
/// <para><b>Deferred (documented, PORT_STATUS.md):</b> the account secure-code gate; the store/trade/tournament
/// gates (state the port doesn't model — treated as always-idle); and the <b>remote NPC-call scroll</b> path
/// (<c>bNpcInvenID/bNpcItemID</c> set → the map 0/8 restriction + the <c>IK_NPCCALL</c> scroll validate/consume):
/// the normal cabinet-NPC-adjacent access (the client sends <c>INVEN_NULL</c>/<c>INVALID_SLOT</c>) is fully
/// ported; the scroll path performs the transfer but skips the scroll consume.</para>
/// </summary>
public sealed partial class MapService
{
    /// <summary>C++ <c>CABINET_COUNT</c> (NetCode.h:2176) — a character has at most 3 cabinets (ids 0/1/2).</summary>
    public const byte CabinetCount = 3;
    /// <summary>C++ <c>CABINET_STORAGE_MAX</c> (NetCode.h:97) — 16 stored items per cabinet.</summary>
    public const byte CabinetStorageMax = 16;
    /// <summary>C++ <c>ITEMTRADE_CABINET</c> (TMapType.h:66) — the <c>m_bIsSell</c> bit that lets an item be
    /// stored in a cabinet.</summary>
    private const byte ItemTradeCabinet = 4;

    // Open cost per cabinet id (CABINET_COST_OPEN1/2/3) and take-out fee per cabinet id (CABINET_COST_USE1/2/3).
    private static readonly uint[] CabinetOpenCost = { 0, 10000, 1000000 };
    private static readonly uint[] CabinetUseCost = { 100, 100, 300 };

    // ==================== handlers ====================

    /// <summary>C++ <c>OnCS_CABINETOPEN_REQ</c> (CSHandler.cpp:5828) — unlock a cabinet: reject an already-open
    /// one (<c>CABINET_ALREADY</c>) or a full/out-of-range new one (<c>CABINET_MAX</c>), charge the per-id open
    /// cost, then mark it used.</summary>
    private void OnCS_CABINETOPEN_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte cabinetId = r.ReadByte();

        var existing = ch.FindCabinet(cabinetId);
        if (existing is { Use: true }) { SendCS_CABINETOPEN_ACK(s, CabinetResult.Already, cabinetId); return; }
        if (existing is null && (ch.Cabinets.Count == CabinetCount || cabinetId >= CabinetCount))
        {
            SendCS_CABINETOPEN_ACK(s, CabinetResult.Max, cabinetId);
            return;
        }

        uint cost = cabinetId < CabinetOpenCost.Length ? CabinetOpenCost[cabinetId] : 0;
        if (cost != 0)
        {
            if (!ch.UseMoney(cost, false)) { SendCS_CABINETOPEN_ACK(s, CabinetResult.NeedMoney, cabinetId); return; }
            ch.UseMoney(cost, true);
            SendCS_MONEY_ACK(s, ch);
        }

        var cab = existing ?? ch.GetOrCreateCabinet(cabinetId);
        cab.Use = true;
        PersistCabinetHeader(s, cab);
        SendCS_CABINETOPEN_ACK(s, CabinetResult.Success, cabinetId);
    }

    /// <summary>C++ <c>OnCS_CABINETLIST_REQ</c> (CSHandler.cpp:5781) — the per-cabinet open-state list.</summary>
    private void OnCS_CABINETLIST_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        SendCS_CABINETLIST_ACK(s, ch);
    }

    /// <summary>C++ <c>OnCS_CABINETITEMLIST_REQ</c> (CSHandler.cpp:5797) — the contents of one cabinet; a missing
    /// cabinet is silent, an unopened one replies <c>CABINET_NOTUSE</c>.</summary>
    private void OnCS_CABINETITEMLIST_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte cabinetId = r.ReadByte();

        if (ch.FindCabinet(cabinetId) is not { } cab) return;         // silent (C++ 5814)
        if (!cab.Use) { SendCS_CABINETITEMLIST_ACK(s, CabinetResult.NotUse, null); return; }
        SendCS_CABINETITEMLIST_ACK(s, CabinetResult.Success, cab);
    }

    /// <summary>C++ <c>OnCS_CABINETPUTIN_REQ</c> (CSHandler.cpp:5528) — deposit <c>count</c> of a bag item into a
    /// cabinet. Validates the cabinet (exists + open), the source item, and the 16-slot full-with-no-mergeable-
    /// stack case (<c>CABINET_FULL</c>), then hands to <see cref="PutinCabinetItem"/>.</summary>
    private void OnCS_CABINETPUTIN_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte cabinetId = r.ReadByte();
        byte invenId = r.ReadByte();
        byte itemSlot = r.ReadByte();
        byte count = r.ReadByte();
        _ = r.ReadByte();   // bNpcInvenID — remote-scroll path deferred
        _ = r.ReadByte();   // bNpcItemID

        if (ch.FindCabinet(cabinetId) is not { } cab) return;          // silent (C++ 5581)
        if (!cab.Use) { SendCS_CABINETITEMLIST_ACK(s, CabinetResult.NotUse, null); return; }

        var srcInv = ch.FindInven(invenId);
        if (srcInv is null) { SendCS_CABINETITEMLIST_ACK(s, CabinetResult.NotUse, null); return; }
        var srcItem = srcInv.FindItem(itemSlot);
        if (srcItem is null || srcItem.Count == 0 || count == 0)
        {
            SendCS_CABINETITEMLIST_ACK(s, CabinetResult.NotUse, null);
            return;
        }

        // Full check: at 16 items, deposit is only possible by merging into a same-stack that still has room.
        if (cab.Items.Count == CabinetStorageMax &&
            !cab.Items.Any(e => e.SameStackAs(srcItem) && e.Count < (e.Template?.Stack ?? 1)))
        {
            SendCS_CABINETITEMLIST_ACK(s, CabinetResult.Full, null);
            return;
        }

        PutinCabinetItem(s, ch, cab, srcInv, srcItem, count);
    }

    /// <summary>C++ <c>OnCS_CABINETTAKEOUT_REQ</c> (CSHandler.cpp:5662) — withdraw a whole stored stack back to a
    /// bag. Charges the per-id use fee up front (affordability only), requires an exact whole-stack count match,
    /// then hands to <see cref="TakeoutStorageItem"/> (which deducts the fee only on a successful move).</summary>
    private void OnCS_CABINETTAKEOUT_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte cabinetId = r.ReadByte();
        uint stItemId = r.ReadUInt32();
        byte count = r.ReadByte();
        byte invenId = r.ReadByte();
        byte itemSlot = r.ReadByte();
        _ = r.ReadByte();   // bNpcInvenID — remote-scroll path deferred
        _ = r.ReadByte();   // bNpcItemID

        uint fee = cabinetId < CabinetUseCost.Length ? CabinetUseCost[cabinetId] : 0;
        if (fee != 0 && !ch.UseMoney(fee, false))
        {
            SendCS_CABINETITEMLIST_ACK(s, CabinetResult.NeedMoney, null);
            return;
        }

        if (ch.FindCabinet(cabinetId) is not { } cab) return;          // GetCabinetItem null ⇒ silent
        var item = cab.FindItem(stItemId);
        if (item is null || count == 0 || item.Count != count) return; // exact whole-stack match (C++ 5741)

        TakeoutStorageItem(s, ch, cab, item, count, invenId, itemSlot, fee);
    }

    // ==================== transfer core ====================

    /// <summary>C++ <c>CTPlayer::PutinCabinetItem</c> (TPlayer.cpp:1542) — move up to <paramref name="count"/> of
    /// a bag item into the cabinet: reject equipped source; gate on the <c>ITEMTRADE_CABINET</c> bit; fill
    /// existing same-stacks (map order), then create <b>one</b> new slot for up to a stack of the remainder (only
    /// if under 16 slots); reduce the source (DEL/UPDATE); refresh the cabinet (<c>CABINETITEMLIST_ACK</c>).</summary>
    private bool PutinCabinetItem(ClientSession s, Character ch, Cabinet cab, Inven srcInv, Item srcItem, byte count)
    {
        if (srcInv.InvenId == Proto.InvenEquip) return false;
        if (srcItem.Count < count) return false;

        // Tradable gate: chart-gated so a template-less (DB-free) item is permissive; a real template must carry
        // the ITEMTRADE_CABINET bit (C++ if(!(m_bIsSell & ITEMTRADE_CABINET))).
        if (srcItem.Template is { } t && (t.IsSell & ItemTradeCabinet) == 0)
        {
            SendCS_CABINETITEMLIST_ACK(s, CabinetResult.NotUse, cab);
            return false;
        }

        int remaining = count;

        // Merge pass — fill existing same-stacks (ascending StItemId = C++ map order).
        foreach (var e in cab.Items.Where(e => e.SameStackAs(srcItem)).OrderBy(e => e.StItemId).ToList())
        {
            int room = (e.Template?.Stack ?? 1) - e.Count;
            int fill = Math.Min(remaining, Math.Max(0, room));
            if (fill <= 0) continue;
            e.Count += (byte)fill;
            srcItem.Count -= (byte)fill;
            remaining -= fill;
            EnqueueCabinetItemSave(s, cab.CabinetId, e);
            if (remaining == 0) break;
        }

        // New-slot pass — the C++ creates exactly ONE new slot for up to a stack of the remainder.
        if (remaining > 0 && cab.Items.Count < CabinetStorageMax)
        {
            int put = Math.Min(remaining, srcItem.Template?.Stack ?? 1);
            var stored = srcItem.Clone();
            stored.Count = (byte)put;
            stored.StItemId = cab.NextStItemId();
            stored.DlId = 0;                          // fresh cabinet row (minted on first persist)
            cab.Items.Add(stored);
            srcItem.Count -= (byte)put;
            EnqueueCabinetItemSave(s, cab.CabinetId, stored);
        }

        if (srcItem.Count == 0)
        {
            srcInv.Items.Remove(srcItem);
            SendCS_DELITEM_ACK(s, srcInv.InvenId, srcItem);   // bag row deleted incrementally
        }
        else
        {
            SendCS_UPDATEITEM_ACK(s, srcInv.InvenId, srcItem);
        }

        SendCS_CABINETITEMLIST_ACK(s, CabinetResult.Success, cab);
        return true;
    }

    /// <summary>C++ <c>CTPlayer::TakeoutStorageItem</c> (TPlayer.cpp:1629) — move a whole stored stack to a bag:
    /// if the requested dest slot is free place it there (<c>ADDITEM_ACK</c>), else general-push into the bags
    /// (<see cref="CanPush"/>/<see cref="PushTItem"/>) and bail with no move if it won't fit. On success remove
    /// the stored item, refresh the cabinet, and deduct the use fee.</summary>
    private bool TakeoutStorageItem(ClientSession s, Character ch, Cabinet cab, Item item, byte count,
        byte invenId, byte destSlot, uint fee)
    {
        if (!cab.Use) { SendCS_CABINETITEMLIST_ACK(s, CabinetResult.NotUse, null); return false; }
        if (invenId == Proto.InvenEquip) return false;
        var destInv = ch.FindInven(invenId);
        if (destInv is null || !destInv.ContainsSlot(destSlot)) return false;

        bool moved;
        if (destInv.FindItem(destSlot) is null)
        {
            var placed = item.Clone();
            placed.ItemSlot = destSlot;
            placed.Count = count;
            placed.DlId = 0;                          // fresh bag row
            destInv.Items.Add(placed);
            SendCS_ADDITEM_ACK(s, destInv.InvenId, placed);
            moved = true;
        }
        else
        {
            var clone = item.Clone();
            clone.Count = count;
            clone.DlId = 0;
            var one = new[] { clone };
            if (CanPush(ch, one)) { PushTItem(s, one); moved = true; }
            else moved = false;
        }

        if (!moved) return false;

        cab.Items.Remove(item);
        EnqueueItemDelete(s, item);                   // cabinet row removed incrementally
        SendCS_CABINETITEMLIST_ACK(s, CabinetResult.Success, cab);

        if (fee != 0) { ch.UseMoney(fee, true); SendCS_MONEY_ACK(s, ch); }
        return true;
    }

    // ==================== senders ====================

    /// <summary>C++ <c>SendCS_CABINETLIST_ACK</c> (CSSender.cpp:2641) — <c>BYTE count</c> then per cabinet
    /// (ascending id) <c>{ bCabinetID, bUse }</c>.</summary>
    private static void SendCS_CABINETLIST_ACK(ClientSession s, Character ch)
    {
        var w = new PacketWriter(Msg.CS_CABINETLIST_ACK, capacity: 16);
        var cabs = ch.Cabinets.OrderBy(c => c.CabinetId).ToList();
        w.WriteByte((byte)cabs.Count);
        foreach (var c in cabs)
        {
            w.WriteByte(c.CabinetId);
            w.WriteByte((byte)(c.Use ? 1 : 0));
        }
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_CABINETITEMLIST_ACK</c> (CSSender.cpp:2658) — <c>bResult</c>, and when
    /// <paramref name="cab"/> is non-null: <c>bCabinetID</c>, <c>DWORD count</c>, then per item (ascending
    /// <c>StItemId</c>) <c>dwStItemID</c> + the item block via <see cref="Item.WrapPacketClient"/> with
    /// <c>addItemId=false</c> (the leading slot byte is omitted).</summary>
    private static void SendCS_CABINETITEMLIST_ACK(ClientSession s, CabinetResult result, Cabinet? cab)
    {
        var w = new PacketWriter(Msg.CS_CABINETITEMLIST_ACK, capacity: 128);
        w.WriteByte((byte)result);
        if (cab is not null)
        {
            var items = cab.Items.OrderBy(i => i.StItemId).ToList();
            w.WriteByte(cab.CabinetId);
            w.WriteUInt32((uint)items.Count);
            foreach (var it in items)
            {
                w.WriteUInt32(it.StItemId);
                it.WrapPacketClient(w, s.CharId, addItemId: false);
            }
        }
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_CABINETOPEN_ACK</c> (CSSender.cpp:2685) — <c>{ bResult, bCabinetID }</c>.</summary>
    private static void SendCS_CABINETOPEN_ACK(ClientSession s, CabinetResult result, byte cabinetId)
    {
        var w = new PacketWriter(Msg.CS_CABINETOPEN_ACK, capacity: 4);
        w.WriteByte((byte)result);
        w.WriteByte(cabinetId);
        s.Send(w);
    }
}
