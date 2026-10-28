namespace TMap.Server.Map;

/// <summary>One offered entry in a personal store — a port of C++ <c>tagSTOREITEM</c> (TMapType.h:2120). It
/// only <b>references</b> the seller's own bag slot (<see cref="Inven"/> + <see cref="ItemSlot"/>) — the item is
/// never removed or locked at open, it is looked up live on browse/buy. <see cref="Count"/> is the listed
/// quantity (independent of the live stack). Price is 3 money tiers + a <see cref="Credits"/> (PvP-point)
/// alternate currency.</summary>
public sealed class StoreItem
{
    public byte SlotKey { get; set; }      // m_bID — the offer index (map key)
    public uint Gold { get; set; }
    public uint Silver { get; set; }
    public uint Cooper { get; set; }
    public uint Credits { get; set; }      // PvP-point price (credits path is deferred)
    public byte Inven { get; set; }        // m_bInvenID — the seller's container
    public byte ItemSlot { get; set; }     // m_bItemID — the seller's slot
    public byte Count { get; set; }        // m_bItemCount — listed quantity
    public Item? SourceItem { get; init; }
    public Item? Snapshot { get; init; }

    public bool Matches(Item? item) => item is { Count: > 0 } && ReferenceEquals(item, SourceItem)
        && Snapshot is not null && item.SameStackAs(Snapshot) && item.GradeEffect == Snapshot.GradeEffect
        && item.CanDeal() && item.Count >= Count;

    /// <summary>The unit price in copper (C++ <c>CalcMoney(gold, silver, cooper)</c>).</summary>
    public long UnitPrice => Cooper + (long)Silver * Character.MoneyMultiply + (long)Gold * Character.MoneyMultiply * Character.MoneyMultiply;
}

/// <summary>
/// A player's personal store (vendor) — a port of C++ <c>CTPlayer::m_bStore</c> + <c>m_strStoreName</c> +
/// <c>m_mapStoreItem</c>. A same-map, in-memory listing: the seller opens with a set of offered bag items at
/// prices; nearby players browse (<c>CS_STOREITEMLIST</c>) and buy (<c>CS_STOREITEMBUY</c>). The offered items
/// remain in the seller's inventory until sold. The store auto-closes when the last offer sells out.
/// </summary>
public sealed class Store
{
    public bool IsOpen { get; set; }
    public string Name { get; set; } = "";
    public List<StoreItem> Items { get; } = new();   // C++ map<slotKey, TSTOREITEM>

    /// <summary>The offer with this key (C++ <c>m_mapStoreItem.find</c>), or null.</summary>
    public StoreItem? Find(byte slotKey) => Items.FirstOrDefault(i => i.SlotKey == slotKey);

    /// <summary>C++ <c>ClearStore</c>/<c>StoreClose</c> reset — the broadcast + buff-removal are the caller's.</summary>
    public void Clear() { IsOpen = false; Name = ""; Items.Clear(); }
}
