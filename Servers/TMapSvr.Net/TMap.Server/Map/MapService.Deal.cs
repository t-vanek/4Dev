using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Player-to-player DEAL (trade) — the C# port of the C++ inline deal machine (<c>OnCS_DEALITEM*</c>,
/// CSHandler.cpp:10774-11145) + <c>tagDEALITEM</c>. Same-map, in-memory, paired by partner <b>name</b>:
/// <b>ASK</b> (invite) → <b>RLY</b> (accept/decline) → <b>ADD</b> (submit a one-shot offer) → <b>confirm</b>
/// (a two-phase CONFORM lock; the second confirm runs the atomic swap). The offered items are staged as copies
/// and only leave the bag at execution; a full guard (<c>ValidDealItem</c> + double <c>CanPush</c>) precedes any
/// mutation, so a full bag aborts cleanly with no partial trade.
///
/// <para><b>Deferred (documented, PORT_STATUS.md):</b> the <c>IsActionBlock</c>
/// / <c>CanTalk</c> nation gates on the invite; the ~20 whole-player <b>deal-lock</b> guards that block
/// move/sell/use/drop/bank/mail while <c>m_bStatus &gt;= DEAL_START</c> (the execution-time <c>ValidDeal</c>
/// re-check already guarantees trade integrity if an offered item is moved/consumed mid-deal); the cross-map
/// <c>MW_DEALITEMERROR</c> teardown (deal is same-map); and the UDP trade log (a no-op in the C++ build).
/// With SQL enabled, item operations and both complete post-exchange snapshots are durably accepted in
/// one SQL queue record before either player's success packets. Recovery replays both snapshots before
/// admission. This is a single-Map recovery boundary; distributed ownership remains separate work.</para>
/// </summary>
public sealed partial class MapService
{
    // DEAL_STATUS (NetCode.h:233)
    private const byte DealReady = 0, DealWait = 1, DealStart = 2, DealAddItem = 3, DealConform = 4;

    private static long CalcMoney(uint gold, uint silver, uint cooper)
        => cooper + (long)silver * Character.MoneyMultiply + (long)gold * Character.MoneyMultiply * Character.MoneyMultiply;

    private static bool DealParticipantsAvailable(ClientSession a, ClientSession b) =>
        a.IsMain && b.IsMain && a.State == EnterState.InGame && b.State == EnterState.InGame
        && !a.TeleportPending && !b.TeleportPending && a.Char is { } ca && b.Char is { } cb
        && a.Channel == b.Channel && ca.MapId == cb.MapId && !a.Store.IsOpen && !b.Store.IsOpen
        && !IsTournamentChar(ca) && !IsTournamentChar(cb);

    private ClientSession? FindDealPartner(ClientSession s)
    {
        // A session that started loading or ceased being main still needs its trade lock cleared.
        // Bind production pairs to the original session; a relog with the same name cannot inherit it.
        if (s.Deal.TargetId != 0)
            return _state.FindByChar(s.Deal.TargetId) is { } target && target.Key == s.Deal.TargetKey ? target : null;
        return _state.ByChar.Values.FirstOrDefault(p => string.Equals(p.Char?.Name, s.Deal.TargetName, StringComparison.OrdinalIgnoreCase));
    }

    // ==================== handlers ====================

    /// <summary>C++ <c>OnCS_DEALITEMASK_REQ</c> (CSHandler.cpp:10774) — invite <c>strTarget</c> to trade: resolve
    /// the target and push the ask to them. (The block-list / action / nation gates are deferred.)</summary>
    private void OnCS_DEALITEMASK_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        string targetName = r.ReadString();
        var t = FindByName(targetName);
        if (t is null || t == s || !t.IsMain || t.State != EnterState.InGame || t.Char is null) return;  // silent (C++ FindPlayer gate)
        if (!DealParticipantsAvailable(s, t)) return;
        if (IsTournamentChar(t.Char)) return;                                                 // not with a tournament character
        if (!CheckProtected(t.Char, ch.CharId, ProtectedDeal)) { SendCS_DEALITEMEND_ACK(s, DealResult.Deny, targetName); return; }
        SendCS_DEALITEMASK_ACK(t, ch.Name);   // to the target only
    }

    /// <summary>C++ <c>OnCS_DEALITEMRLY_REQ</c> (CSHandler.cpp:10821) — the target accepts (ASK_YES=0) or declines.
    /// Accept pairs both sides (<c>SetTarget</c> → START/WAIT) and opens the window on both; decline ends both.</summary>
    private void OnCS_DEALITEMRLY_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte reply = r.ReadByte();
        string inviterName = r.ReadString();

        if (s.Deal.Status != DealReady) return;                       // responder must be idle
        var inv = FindByName(inviterName);
        if (inv is null || inv == s || !inv.IsMain || inv.State != EnterState.InGame || inv.Char is null
            || inv.Deal.Status != DealReady || !DealParticipantsAvailable(s, inv))
        {
            SendCS_DEALITEMEND_ACK(s, DealResult.Busy, inviterName);
            return;
        }

        if (reply == 0)   // ASK_YES
        {
            s.Deal.SetTarget(inviterName, inv.CharId, inv.Key);
            inv.Deal.SetTarget(ch.Name, s.CharId, s.Key);
            SendCS_DEALITEMSTART_ACK(inv, ch.Name);
            SendCS_DEALITEMSTART_ACK(s, inviterName);
        }
        else              // decline (bReply = ASK_NO=1 = DEALITEM_DENY)
        {
            SendCS_DEALITEMEND_ACK(inv, (DealResult)reply, ch.Name);
            SendCS_DEALITEMEND_ACK(s, (DealResult)reply, ch.Name);
            s.Deal.Clear();
            inv.Deal.Clear();
        }
    }

    /// <summary>C++ <c>OnCS_DEALITEMADD_REQ</c> (CSHandler.cpp:10872) — submit this side's offer (once). Records
    /// the offered money + whole-slot items as copies (staged on self as SendItems, mirrored to the partner as
    /// RecvItems), re-validating each item (owned / tradable / not duplicated) and the partner's bag space, then
    /// pushes <c>CS_DEALITEMADD_ACK</c> to the partner. One-shot: gated on <c>Dealing == WAIT</c>.</summary>
    private void OnCS_DEALITEMADD_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        uint gold = r.ReadUInt32(), silver = r.ReadUInt32(), cooper = r.ReadUInt32();
        byte count = r.ReadByte();
        var adds = new (byte inven, byte slot)[count];
        for (int i = 0; i < count; i++) adds[i] = (r.ReadByte(), r.ReadByte());

        if (s.Deal.Dealing != DealWait) return;                       // one-shot offer
        long money = CalcMoney(gold, silver, cooper);

        var t = FindDealPartner(s);
        if (t?.Char is null) { SendCS_DEALITEMEND_ACK(s, DealResult.NoTarget, s.Deal.TargetName); s.Deal.Clear(); return; }
        if (!DealParticipantsAvailable(s, t) || t.Deal.TargetName != ch.Name) { EndDealBoth(s, t, DealResult.Busy, ch.Name); return; }
        if (!ch.UseMoney(money, commit: false)) { EndDealBoth(s, t, DealResult.NoMoney, ch.Name); return; }

        s.Deal.SendMoney = money;
        DealResult fail = DealResult.Success;
        foreach (var (invenId, slot) in adds)
        {
            var inv = ch.FindInven(invenId);
            if (inv is null) { fail = DealResult.NoInven; break; }
            var live = inv.FindItem(slot);
            if (live is null || live.Count == 0) { fail = DealResult.NoItem; break; }
            if (!live.CanDeal()) { fail = DealResult.NoItem; break; }
            if (s.Deal.SendItems.Any(d => d.Inven == invenId && d.Snapshot.ItemSlot == slot)) { fail = DealResult.InvalidItem; break; } // CheckDealItem dedup
            s.Deal.SendItems.Add(new DealItem(invenId, live.Clone()));   // snapshot (keeps slot + count) for ValidDeal
            t.Deal.RecvItems.Add(live.Clone());                          // partner's fresh copy (DlId 0 → new row on push)
            if (!CanPush(t.Char, t.Deal.RecvItems)) { fail = DealResult.CantRecv; break; }
        }

        if (fail != DealResult.Success)
        {
            string name = fail == DealResult.CantRecv ? s.Deal.TargetName : ch.Name;
            SendCS_DEALITEMEND_ACK(t, fail, name);
            SendCS_DEALITEMEND_ACK(s, fail, name);
            s.Deal.Clear(); t.Deal.Clear();
            return;
        }

        s.Deal.Dealing = DealAddItem;
        t.Deal.RecvMoney = money;
        SendCS_DEALITEMADD_ACK(t, gold, silver, cooper, t.Deal.RecvItems);   // to the PARTNER (what they'll receive)
    }

    /// <summary>C++ <c>OnCS_DEALITEM_REQ</c> (CSHandler.cpp:11004) — confirm (<c>bOkey!=0</c>) or cancel (0). The
    /// first confirm arms both sides (CONFORM); the second runs the atomic exchange.</summary>
    private void OnCS_DEALITEM_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte okey = r.ReadByte();

        var t = FindDealPartner(s);
        if (t?.Char is null) { SendCS_DEALITEMEND_ACK(s, DealResult.NoTarget, s.Deal.TargetName); s.Deal.Clear(); return; }
        if (!DealParticipantsAvailable(s, t) || t.Deal.TargetName != ch.Name) { EndDealBoth(s, t, DealResult.Busy, ch.Name); return; }

        if (okey == 0) { EndDealBoth(s, t, DealResult.Cancel, ch.Name); return; }   // cancel/back-out

        if (s.Deal.Dealing != DealAddItem) return;                                 // must have submitted an offer
        if (s.Deal.Status != DealConform)                                          // FIRST confirmer: arm both
        {
            s.Deal.Dealing = DealConform;
            s.Deal.Status = DealConform;
            t.Deal.Status = DealConform;                                           // arms the partner's gate
            return;
        }
        ExecuteDeal(s, ch, t, t.Char);                                             // SECOND confirmer → swap
    }

    // ==================== exchange ====================

    /// <summary>C++ exchange (CSHandler.cpp:11049-11128): guard both offers (still valid + both can receive),
    /// then commit each side (money + items). All-or-nothing — a failed guard aborts before any mutation.</summary>
    private void ExecuteDeal(ClientSession a, Character ca, ClientSession b, Character cb)
    {
        if (!ValidDeal(a, ca) || !ValidDeal(b, cb)) { EndDealBoth(a, b, DealResult.Busy, ca.Name); return; }
        if (!CanPush(ca, a.Deal.RecvItems)) { EndDealBoth(a, b, DealResult.CantRecv, ca.Name); return; }
        if (!CanPush(cb, b.Deal.RecvItems)) { EndDealBoth(a, b, DealResult.CantRecv, cb.Name); return; }
        if (_gameDb is not null && (!_itemIdReady || !ca.DbLoaded || !cb.DbLoaded
            || ca.TournamentBackup is not null || cb.TournamentBackup is not null))
        { EndDealBoth(a, b, DealResult.Busy, ca.Name); return; }

        // Both item/money responses remain private until the entire exchange is durable. Never let
        // an enqueue failure publish one side or leak its staged inventory through a later logout.
        using var responsesA = a.DeferResponses(); using var responsesB = b.DeferResponses();
        try
        {
            CommitDealSide(a, ca, a.Deal.TargetName);
            CommitDealSide(b, cb, b.Deal.TargetName);
            CheckpointPlayerExchange(ca, cb);
        }
        catch
        {
            responsesA.Discard(); responsesB.Discard();
            a.Deal.Clear(); b.Deal.Clear();
            QuarantinePersistence(a); QuarantinePersistence(b);
            throw;
        }
    }

    /// <summary>C++ <c>ValidDealItem</c> (TPlayer.cpp:669) — every offered item still sits at its recorded
    /// (inven, slot), is identical (<see cref="Item.SameStackAs"/> + count), and the offered money is still
    /// affordable.</summary>
    private static bool ValidDeal(ClientSession s, Character ch)
    {
        foreach (var d in s.Deal.SendItems)
        {
            var live = ch.FindInven(d.Inven)?.FindItem(d.Snapshot.ItemSlot);
            if (live is null || !live.SameStackAs(d.Snapshot) || live.Count != d.Snapshot.Count) return false;
        }
        return ch.UseMoney(s.Deal.SendMoney, commit: false);
    }

    /// <summary>Commit one side: credit received money, debit given money, remove the offered items from the bag
    /// (C++ <c>EraseInvenDealItem</c> — fires <c>CS_DELITEM_ACK</c>), push the received items
    /// (<c>CS_ADDITEM/UPDATEITEM_ACK</c>), then end + money ack + clear.</summary>
    private void CommitDealSide(ClientSession s, Character ch, string partnerName)
    {
        ch.EarnMoney(s.Deal.RecvMoney);
        if (s.Deal.SendMoney != 0) ch.UseMoney(s.Deal.SendMoney, commit: true);
        foreach (var d in s.Deal.SendItems)
        {
            var inv = ch.FindInven(d.Inven);
            var live = inv?.FindItem(d.Snapshot.ItemSlot);
            if (inv is not null && live is not null) { inv.Items.Remove(live); SendCS_DELITEM_ACK(s, d.Inven, live); }
        }
        PushTItem(s, s.Deal.RecvItems);
        SendCS_DEALITEMEND_ACK(s, DealResult.Success, partnerName);
        SendCS_MONEY_ACK(s, ch);
        s.Deal.Clear();
    }

    private void EndDealBoth(ClientSession a, ClientSession b, DealResult result, string name)
    {
        SendCS_DEALITEMEND_ACK(a, result, name);
        SendCS_DEALITEMEND_ACK(b, result, name);
        a.Deal.Clear();
        b.Deal.Clear();
    }

    // Shared by teleport/map exit and socket disconnect while the peer is still registered.
    private void CancelDealOnLeave(ClientSession s)
    {
        if (s.Deal.Status == DealReady) return;
        string name = s.Char?.Name ?? "";
        SendCS_DEALITEMEND_ACK(s, DealResult.NoTarget, name);
        if (FindDealPartner(s) is { } partner && partner.Deal.TargetName == name)
        {
            SendCS_DEALITEMEND_ACK(partner, DealResult.NoTarget, name);
            partner.Deal.Clear();
        }
        s.Deal.Clear();
    }

    // ==================== senders ====================

    /// <summary>C++ <c>SendCS_DEALITEMASK_ACK</c> — the inviter's name, shown to the target.</summary>
    private static void SendCS_DEALITEMASK_ACK(ClientSession s, string inviter)
    {
        var w = new PacketWriter(Msg.CS_DEALITEMASK_ACK, capacity: 64);
        w.WriteString(inviter);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_DEALITEMSTART_ACK</c> (CSSender.cpp:4079) — opens the window (also asserts
    /// <c>m_bStatus = DEAL_START</c>) and carries the partner name.</summary>
    private static void SendCS_DEALITEMSTART_ACK(ClientSession s, string target)
    {
        s.Deal.Status = DealStart;
        var w = new PacketWriter(Msg.CS_DEALITEMSTART_ACK, capacity: 64);
        w.WriteString(target);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_DEALITEMADD_ACK</c> (CSSender.cpp:4091) — the partner's offer preview: the offered
    /// gold/silver/cooper then each item via <see cref="Item.WrapPacketClient"/> with <c>addItemId=false</c>.</summary>
    private static void SendCS_DEALITEMADD_ACK(ClientSession s, uint gold, uint silver, uint cooper, List<Item> items)
    {
        var w = new PacketWriter(Msg.CS_DEALITEMADD_ACK, capacity: 96);
        w.WriteUInt32(gold); w.WriteUInt32(silver); w.WriteUInt32(cooper);
        w.WriteByte((byte)items.Count);
        foreach (var it in items) it.WrapPacketClient(w, s.CharId, addItemId: false);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_DEALITEMEND_ACK</c> (CSSender.cpp:4058) — <c>{ bResult, strTarget }</c>.</summary>
    private static void SendCS_DEALITEMEND_ACK(ClientSession s, DealResult result, string target)
    {
        var w = new PacketWriter(Msg.CS_DEALITEMEND_ACK, capacity: 64);
        w.WriteByte((byte)result);
        w.WriteString(target);
        s.Send(w);
    }
}
