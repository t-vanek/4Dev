using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>One offered item in a deal (C++ <c>m_vSendItem</c> entry) — a snapshot of the offered slot's item
/// (for the execution-time identity re-check) tagged with its source container (<c>m_bInven</c>). The whole
/// slot is offered (there is no partial-count deal), so <see cref="Snapshot"/> carries the full count + slot.</summary>
public readonly record struct DealItem(byte Inven, Item Snapshot);

/// <summary>
/// The per-player deal (player-to-player trade) state — a port of C++ <c>tagDEALITEM</c> (<c>m_dealItem</c> on
/// <c>CTPlayer</c>, TMapType.h:2109). A deal is a same-map, in-memory two-party state machine paired by partner
/// <b>name</b> on the wire, bound to the partner's character ID and session key on acceptance.
/// <see cref="Status"/> is the shared paired state (READY/START/CONFORM —
/// <c>&gt;= START</c> is the "in a deal" lock threshold); <see cref="Dealing"/> is this side's offer sub-state
/// (READY/WAIT/ADDITEM/CONFORM). Confirmation is state-encoded (no bOkey field): the first confirm sets both
/// sides' <see cref="Status"/> to CONFORM; the second executes.
/// </summary>
public sealed class Deal
{
    public byte Status { get; set; }        // DealStatus (Ready/Start/Conform)
    public byte Dealing { get; set; }        // DealStatus (Ready/Wait/AddItem/Conform)
    public string TargetName { get; set; } = "";
    public uint TargetId { get; private set; }
    public uint TargetKey { get; private set; }
    public long SendMoney { get; set; }      // money this player offers
    public long RecvMoney { get; set; }      // money this player receives
    public List<DealItem> SendItems { get; } = new();   // items this player offers (+ src inven)
    public List<Item> RecvItems { get; } = new();        // items this player receives (partner's offer copies)

    /// <summary>C++ <c>ClearDealItem</c> (TPlayer.cpp:543) — reset to idle and drop both offers.</summary>
    public void Clear()
    {
        Status = (byte)DealStatus.Ready;
        Dealing = (byte)DealStatus.Ready;
        TargetName = "";
        TargetId = 0; TargetKey = 0;
        SendMoney = 0; RecvMoney = 0;
        SendItems.Clear(); RecvItems.Clear();
    }

    /// <summary>C++ <c>SetDealItemTarget</c> (TPlayer.cpp:450) — pair with <paramref name="partner"/> and open the
    /// window (START / WAIT), clearing any stale offer. Returns false if not idle (already in a deal).</summary>
    public bool SetTarget(string partner, uint charId = 0, uint key = 0)
    {
        if (Status != (byte)DealStatus.Ready) return false;
        TargetName = partner;
        TargetId = charId; TargetKey = key;
        SendMoney = 0; RecvMoney = 0; SendItems.Clear(); RecvItems.Clear();
        Status = (byte)DealStatus.Start;
        Dealing = (byte)DealStatus.Wait;
        return true;
    }

    /// <summary>Whether a deal is in progress (C++ <c>m_bStatus &gt;= DEAL_START</c> — the guard that blocks
    /// inventory-mutating ops elsewhere).</summary>
    public bool InProgress => Status >= (byte)DealStatus.Start;
}
