using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>One magic/enchant option on an item: the persisted (bMagic[i], wValue[i]) pair (C++
/// <c>TMAGIC{m_wValue, m_pMagic}</c>). <see cref="Template"/> is the resolved magic-option template
/// (<c>m_pMagic</c>) used to compute the client display value; null when templates aren't loaded (DB-free),
/// in which case the raw stored <see cref="Value"/> is emitted.</summary>
public readonly record struct MagicOption(byte Id, ushort Value, MagicTemplate? Template = null);

/// <summary>
/// An item instance — a focused port of <c>CTItem</c>: the persisted fields plus the byte-exact client
/// serializer (<see cref="WrapPacketClient"/>). The stat/price/repair/catalyzer math is deferred
/// (PORT_STATUS.md). The six <see cref="Ext"/> slots are the <c>m_dwExtValue[]</c> array (the TITEMTABLE
/// <c>dwTime1..6</c> columns — extension values, not timers), indexed by the <c>IEV_*</c> constants.
/// </summary>
public sealed class Item
{
    // m_dwExtValue[] indices (NetCode.h enum, IEV_*).
    public const int IevEld = 0, IevWrap = 1, IevColor = 2, IevGuild = 3, IevCompanion = 4, IevCustomTex = 5;

    /// <summary>m_dlID — the item's unique DB row id (TITEMTABLE PK). Preserved across load/save; 0 for an
    /// in-session item until stamped by the item-id generator at save time.</summary>
    public long DlId { get; set; }

    /// <summary>C++ <c>m_dwOwnerID</c> — for a corpse loot item, the char id it is owner-locked to (0 = public).
    /// A quest <c>DropItem</c> sets this to the quest holder so only they see/take it (Phase 33).</summary>
    public uint OwnerId { get; set; }

    /// <summary>C++ <c>m_dwStItemID</c> — for a cabinet (warehouse) item, its per-cabinet stored-item key
    /// (the <c>m_mapCabinetItem</c> map key + the persisted <c>dwStorageID</c>). Unused (0) for a bag item.
    /// Phase 37.</summary>
    public uint StItemId { get; set; }

    /// <summary>C++ <c>m_bMaxRouting</c> / <c>m_mapRoutingJoiner</c> — a corpse item being drawn for in a lottery party
    /// (MapService.LootLottery.cs): how many may still join, and who joined (char id → key). <see cref="RoutingAsked"/> holds the
    /// members asked who haven't answered yet (added by the port: the C++ took answers from anyone, more than once).</summary>
    public byte MaxRouting { get; set; }
    public Dictionary<uint, uint>? RoutingJoiner { get; set; }
    public HashSet<uint>? RoutingAsked { get; set; }

    public byte ItemSlot { get; set; }      // bItemID — slot within its inventory container
    public ushort TemplateId { get; set; }  // wItemID — template id (joins to TItem.tcd)
    public byte Level { get; set; }
    public byte Gem { get; set; }
    public ushort MoggItemId { get; set; }
    public byte Count { get; set; }
    public uint DuraMax { get; set; }
    public uint DuraCur { get; set; }
    public byte RefineMax { get; set; }     // from the item template; not persisted per-instance
    public byte RefineCur { get; set; }
    public byte GLevel { get; set; }
    public long EndTime { get; set; }        // __time64_t (seconds since 1970); 0 = no expiry
    public byte GradeEffect { get; set; }

    /// <summary>m_dwExtValue[6] (ELD/WRAP/COLOR/GUILD/COMPANION/CUSTOMTEX), the dwTime1..6 columns.</summary>
    public uint[] Ext { get; } = new uint[6];

    public List<MagicOption> Magic { get; } = new();

    /// <summary>The linked item template (C++ <c>m_pTITEM</c>), resolved from <see cref="TemplateId"/> at
    /// load time. Null when the chart is unavailable (DB-free); serialization then falls back to the
    /// per-instance <see cref="RefineMax"/> and the raw magic values.</summary>
    public ItemTemplate? Template { get; set; }

    /// <summary>The linked item-attribute row (C++ <c>m_pTITEMATTR</c>), resolved by <c>SetItemAttr</c> at
    /// load from (attr id + level grade + gem). Null when the attr chart is unavailable.</summary>
    public ItemAttr? Attr { get; set; }

    /// <summary>
    /// Serializes this item to a client exactly as C++ <c>CTItem::WrapPacketClient</c> does
    /// (bCashItem=FALSE): the item slot, template, gem/mogg/companion, count, durability,
    /// refine, glevel, __time64_t expiry, grade, the ELD/WRAP/COLOR/CUSTOMTEX ext values, the
    /// guild-registration flag, then the magic-option list. <paramref name="addItemId"/> is the C++
    /// <c>bAddItemID</c>: when false (the cabinet item-list block) the leading slot byte is <b>omitted</b>.
    /// <paramref name="cashUseTime"/> is the C++ <c>bCashItem</c> form (the cash shop's list): the item's days instead of its expiry.
    /// </summary>
    public void WrapPacketClient(PacketWriter w, uint ownerCharId, bool addItemId = true, ushort? cashUseTime = null)
    {
        byte regGuild = Ext[IevGuild] != 0 && Ext[IevGuild] == ownerCharId ? (byte)1 : (byte)0;

        if (addItemId) w.WriteByte(ItemSlot);       // C++ if(bAddItemID) << m_bItemID
        w.WriteUInt16(TemplateId);
        w.WriteByte(Level);
        w.WriteByte(Gem);
        w.WriteUInt16(MoggItemId);
        w.WriteUInt16((ushort)Ext[IevCompanion]);
        w.WriteByte(Count);
        w.WriteUInt32(DuraMax);
        w.WriteUInt32(DuraCur);
        w.WriteByte(Template?.RefineMax ?? RefineMax); // C++ m_pTITEM->m_bRefineMax; raw fallback when DB-free
        w.WriteByte(RefineCur);
        w.WriteByte(GLevel);
        if (cashUseTime is { } useTime) w.WriteUInt16(useTime);  // C++ if(bCashItem) << wUseTime
        else w.WriteInt64(EndTime);
        w.WriteByte(GradeEffect);
        w.WriteByte((byte)Ext[IevEld]);
        w.WriteByte((byte)Ext[IevWrap]);
        w.WriteUInt16((ushort)Ext[IevColor]);
        w.WriteUInt16((ushort)Ext[IevCustomTex]);
        w.WriteByte(regGuild);
        w.WriteByte((byte)Magic.Count);
        // C++ stores magic in a map<BYTE magicId, ...>, so it emits ascending by id — sort to match the
        // on-wire byte sequence (the client keys by id, so this is byte-sequence parity, not correctness).
        foreach (var m in Magic.OrderBy(m => m.Id))
        {
            w.WriteByte(m.Id);
            w.WriteUInt16(MagicValue(m));
        }
    }

    /// <summary>C++ <c>CTItem::GetMagicValue(BYTE bMagicID)</c> (TItem.cpp:442) — the computed value of this
    /// item's enchant of the given magic type (an <c>MTYPE_*</c> id), or 0 if it has none. Used by the stat
    /// engine's equipped-gear ability sums (<c>CalcItemAbility</c>).</summary>
    public int GetMagicValue(byte magicType)
    {
        foreach (var m in Magic)
            if (m.Id == magicType) return MagicValue(m);
        return 0;
    }

    /// <summary>
    /// Populates <paramref name="item"/>'s magic list from the persisted (id, value) slots, applying the C++
    /// <c>CreateItem</c> filter (TMapSvr.cpp:9542-9567): skip a slot whose id or value is 0, skip an id with
    /// no <c>TITEMMAGICCHART</c> template (only when the magic chart is loaded — DB-free keeps it raw), and
    /// de-duplicate by id (last slot wins, matching the C++ <c>find→erase→insert</c> on <c>m_mapTMAGIC</c>).
    /// Each surviving entry carries its resolved <see cref="MagicTemplate"/>.
    /// </summary>
    public static void AddPersistedMagic(Item item, IReadOnlyList<byte> ids, IReadOnlyList<ushort> values, TemplateStore templates)
    {
        bool chartLoaded = templates.Magics.Count > 0;
        int n = Math.Min(ids.Count, values.Count);
        for (int i = 0; i < n; i++)
        {
            byte id = ids[i];
            ushort value = values[i];
            if (id == 0 || value == 0) continue;      // C++ if(m_bMagic[k] && m_wValue[k])
            var mt = templates.Magic(id);
            if (mt is null && chartLoaded) continue;  // C++ GetItemMagic(id) != NULL (chart-gated so DB-free keeps it)
            item.Magic.RemoveAll(m => m.Id == id);    // C++ map dedup → last slot wins
            item.Magic.Add(new MagicOption(id, value, mt));
        }
    }

    /// <summary>
    /// Port of C++ <c>CTItem::GetMagicValue(LPTMAGIC)</c> (TItem.cpp:451):
    /// <c>max( WORD(fRevision * m_wValue * m_wMaxValue) / 100, 1 )</c>, where
    /// <c>fRevision = m_bRvType==0 ? 1.0 : m_pTITEM->m_fRevision[m_bRvType-1]</c>.
    /// Byte-exact: the <c>WORD(...)</c> cast (truncate-to-int32 then mask to 16 bits) is applied to the
    /// FLOAT product BEFORE the integer <c>/100</c> — compute the product in float, cast, then divide.
    /// Falls back to the raw stored value when either template is missing (DB-free / unknown id).
    /// </summary>
    /// <summary>The value the client shows for one option (see <see cref="MagicValue"/>).</summary>
    public ushort DisplayValue(in MagicOption m) => MagicValue(m);

    private ushort MagicValue(in MagicOption m)
    {
        var itemT = Template;
        var magT = m.Template;
        if (itemT is null || magT is null) return m.Value;

        float revision = magT.RvType != 0 && magT.RvType - 1 < itemT.Revision.Length
            ? itemT.Revision[magT.RvType - 1] : 1.0f;
        float product = revision * m.Value * magT.MaxValue;      // FLOAT * WORD * WORD, all in float
        ushort word = unchecked((ushort)(int)product);            // WORD(float): trunc-to-int32, mask 16 bits
        int value = word / 100;                                   // integer divide AFTER the cast
        return (ushort)Math.Max(value, 1);
    }

    // ---- Phase 6: per-item AP/DP getters (C++ CTItem::GetMaxAP/… — enchant term + attr-row term) ----

    // TITEM_TYPE (NetCode.h): the item types the AP/DP getters gate on.
    private const byte ItWeapon = 1, ItLong = 4, ItShield = 6;
    // MAGIC_TYPE enchant ids used by the getters.
    private const byte MPap = 7, MPdp = 8, MLap = 9, MMdp = 16, MMap = 17,
        MPMinAp = 61, MPMaxAp = 62, MLMinAp = 63, MLMaxAp = 64, MMpMinAp = 65, MMpMaxAp = 66;

    /// <summary>C++ <c>CTItem::HavePower</c> — a broken item (has a max durability but 0 current) contributes
    /// nothing; an item with no durability concept always counts.</summary>
    public bool HasPower() => (DuraMax != 0 && DuraCur != 0) || DuraMax == 0;

    /// <summary>C++ <c>CTItem::CanDeal</c> (TItem.cpp:699) — tradable player-to-player: a wrapped item
    /// (<c>IEV_WRAP</c> set) or a template carrying the <c>ITEMTRADE_DEAL</c> (=1) <c>m_bIsSell</c> bit.
    /// Chart-gated so a template-less (DB-free) item is permissive.</summary>
    public bool CanDeal() => Ext[IevWrap] != 0 || Template is not { } t || (t.IsSell & 1) != 0;

    /// <summary>C++ <c>CTItem::GetEquipLevel</c> (TItem.cpp:629) — the required level reduced by the ELD
    /// enchant, but ONLY when it would stay positive: <c>DefaultLevel > ELD ? DefaultLevel − ELD :
    /// DefaultLevel</c> (never dips to 0/negative when the ELD enchant meets or exceeds the requirement).</summary>
    public int EquipLevel
    {
        get
        {
            int def = Template?.DefaultLevel ?? 0;
            int eld = (int)Ext[IevEld];
            return def > eld ? def - eld : def;
        }
    }

    /// <summary>
    /// C++ <c>CTItem::operator==</c> — two items are the same mergeable stack only when every persisted
    /// attribute matches (template, level, gem, mogg, glevel, durability, refine, expiry, ext values, the
    /// attr row, and the full magic set). Drives the swap-vs-merge decision in CS_MOVEITEM.
    /// </summary>
    public bool SameStackAs(Item o)
    {
        if (TemplateId != o.TemplateId || Level != o.Level || Gem != o.Gem || MoggItemId != o.MoggItemId
            || GLevel != o.GLevel || DuraMax != o.DuraMax || DuraCur != o.DuraCur || RefineCur != o.RefineCur
            || EndTime != o.EndTime || Magic.Count != o.Magic.Count || !ReferenceEquals(Attr, o.Attr))
            return false;
        for (int i = 0; i < Ext.Length; i++)
            if (Ext[i] != o.Ext[i]) return false;
        foreach (var a in Magic)
        {
            bool found = false;
            foreach (var b in o.Magic)
                if (b.Id == a.Id && b.Value == a.Value) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>A copy for a stack split (C++ <c>new CTItem</c> + <c>Copy</c>); shares the immutable
    /// template / attr references.</summary>
    public Item Clone()
    {
        var c = new Item
        {
            ItemSlot = ItemSlot, TemplateId = TemplateId, Level = Level, Gem = Gem, MoggItemId = MoggItemId,
            Count = Count, DuraMax = DuraMax, DuraCur = DuraCur, RefineMax = RefineMax, RefineCur = RefineCur,
            GLevel = GLevel, EndTime = EndTime, GradeEffect = GradeEffect, Template = Template, Attr = Attr,
        };
        for (int i = 0; i < Ext.Length; i++) c.Ext[i] = Ext[i];
        c.Magic.AddRange(Magic);
        return c;
    }

    public uint GetMaxAP() => Attr is null || Template is null ? 0u
        : (uint)(GetMagicValue(MPap) + GetMagicValue(MPMaxAp)) + (Template.Type == ItWeapon ? Attr.MaxAp : 0u);
    public uint GetMinAP() => Attr is null || Template is null ? 0u
        : (uint)(GetMagicValue(MPap) + GetMagicValue(MPMinAp)) + (Template.Type == ItWeapon ? Attr.MinAp : 0u);
    public uint GetMaxLAP() => Attr is null || Template is null ? 0u
        : (uint)(GetMagicValue(MLap) + GetMagicValue(MLMaxAp)) + (Template.Type == ItLong ? Attr.MaxAp : 0u);
    public uint GetMinLAP() => Attr is null || Template is null ? 0u
        : (uint)(GetMagicValue(MLap) + GetMagicValue(MLMinAp)) + (Template.Type == ItLong ? Attr.MinAp : 0u);
    public uint GetMaxMagicAP() => Attr is null || Template is null ? 0u
        : (uint)(GetMagicValue(MMap) + GetMagicValue(MMpMaxAp)) + (Template.Type == ItWeapon ? Attr.MaxMagicAp : 0u);
    public uint GetMinMagicAP() => Attr is null || Template is null ? 0u
        : (uint)(GetMagicValue(MMap) + GetMagicValue(MMpMinAp)) + (Template.Type == ItWeapon ? Attr.MinMagicAp : 0u);
    public uint GetDefendPower() => Attr is null || Template is null ? 0u
        : (uint)GetMagicValue(MPdp) + (Template.Type != ItShield ? Attr.Dp : 0u);
    public uint GetMagicDefPower() => Attr is null || Template is null ? 0u
        : (uint)GetMagicValue(MMdp) + (Template.Type != ItShield ? Attr.MagicDp : 0u);

    /// <summary>C++ <c>CTItem::CanUse</c> (TItem.cpp:550) — a wrapped item cannot be used or crafted.</summary>
    public bool CanUse => Ext[IevWrap] == 0;

    private const byte ItDefensive = 2, FtypeItemPower = 21, FtypeWeaponPower = 22;

    // C++ GetWeaponPowerLevel / GetShieldPowerLevel rate tables, by item kind (IK_NONE .. IK_SARROW) and equip slot.
    private static readonly float[] WeaponKindRate =
        { 0f, 0.75f, 0.2f, 1f, 1f, 1.1f, 0.8f, 1f, 0.8f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
    private static readonly float[] ShieldKindRate =
        { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.8f, 1f, 1.3f, 1.3f, 1.3f, 1.3f, 1.3f, 1.3f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
    private static readonly float[] ShieldPartRate =
        { 0f, 0.3f, 0f, 0.2f, 0f, 0.3f, 0.25f, 0.15f, 0.1f, 0f, 0f, 0f, 0f, 0f, 0f };

    /// <summary>C++ <c>CTItem::GetPowerLevel</c> (TItem.cpp:296): an item carrying attack or defence options is
    /// rated by what it actually does (the weapon/shield log formulas); any other item by its attr grade.</summary>
    public byte GetPowerLevel(TemplateStore t)
    {
        if (Template is null || Attr is null) return 1;
        if (GetMagicValue(MPap) == 0 && GetMagicValue(MLap) == 0 && GetMagicValue(MPdp) == 0
            && GetMagicValue(MMap) == 0 && GetMagicValue(MMdp) == 0)
            return Attr.Grade;
        return Template.Type switch
        {
            ItWeapon or ItLong => WeaponPowerLevel(t),
            ItDefensive or ItShield => ShieldPowerLevel(t),
            _ => 1,
        };
    }

    // C++ casts these floats straight to BYTE; a non-finite value (a zero power) is taken as 0.
    private static byte ToByte(float v) => float.IsFinite(v) ? unchecked((byte)(int)v) : (byte)0;

    private byte WeaponPowerLevel(TemplateStore t)
    {
        byte kind = Template!.Kind;
        if (kind >= WeaponKindRate.Length || WeaponKindRate[kind] == 0f) return 1;
        float levelConst = 1f / MathF.Log(t.Rate1st);
        float minRate = t.Formula(FtypeWeaponPower)?.RateX ?? 1f, maxRate = t.Formula(FtypeWeaponPower)?.RateY ?? 1f;
        uint minAp = (Template.Type == ItLong ? GetMinLAP() : GetMinAP()) + GetMinMagicAP();
        uint maxAp = (Template.Type == ItLong ? GetMaxLAP() : GetMaxAP()) + GetMaxMagicAP();
        byte lo = (byte)(ToByte(MathF.Log(minAp / (WeaponKindRate[kind] * minRate)) * levelConst) + 1);
        byte hi = (byte)(ToByte(MathF.Log(maxAp / (WeaponKindRate[kind] * maxRate)) * levelConst) + 1);
        return (byte)Math.Max((lo + hi) / 2, 1);
    }

    private byte ShieldPowerLevel(TemplateStore t)
    {
        byte kind = Template!.Kind, slot = Template.PrmSlot;
        if (slot >= ShieldPartRate.Length || ShieldPartRate[slot] == 0f || kind >= ShieldKindRate.Length || ShieldKindRate[kind] == 0f)
            return 1;
        float levelConst = 1f / MathF.Log(t.Rate1st);
        float itemRate = t.Formula(FtypeItemPower)?.RateX ?? 1f;
        uint dp = GetDefendPower() + GetMagicDefPower();
        if (Template.Type == ItShield) dp += (uint)(Attr!.Dp + Attr.MagicDp);
        float r = dp / (ShieldKindRate[kind] * ShieldPartRate[slot] * itemRate);
        r = MathF.Log(r) * levelConst + 1;
        return Math.Max(ToByte(r), (byte)1);
    }
}

/// <summary>
/// One inventory container — a port of <c>CTInven</c>. A character has several containers keyed by
/// <see cref="InvenId"/> (backpack 0xFF, equipped 0xFE, plus timed/event bags). <see cref="TemplateId"/>
/// and <see cref="EndTime"/> describe a container that is itself a (bag) item with an expiry.
/// </summary>
public sealed class Inven
{
    /// <summary>The DB-free default backpack capacity. The live server derives the real value from the bag
    /// container's own item template (<c>m_pTITEM->m_bSlotCount</c>); with no chart we default to this so the
    /// blank-slot finder (<see cref="GetBlankPos"/>) has a bound. Tests set a smaller value to exercise
    /// <c>MI_INVENFULL</c>.</summary>
    public const byte DefaultSlotCount = 100;

    public byte InvenId { get; set; }
    public ushort TemplateId { get; set; }
    public long EndTime { get; set; }        // __time64_t
    public byte Eld { get; set; }            // bELD — bag-enhancement level (persisted, TSaveInven)
    public byte SlotCount { get; set; } = DefaultSlotCount;   // C++ m_pTITEM->m_bSlotCount
    public List<Item> Items { get; } = new();

    public static Inven FromTemplate(byte id, ushort templateId, TemplateStore templates, long endTime = 0, byte eld = 0)
    {
        // Same fallback item IDs as the native CHARINFO inventory headers.
        ushort resolved = templateId != 0 ? templateId : id == Proto.InvenDefault ? (ushort)3 : id == Proto.InvenEquip ? (ushort)2 : (ushort)0;
        return new Inven { InvenId = id, TemplateId = templateId, EndTime = endTime, Eld = eld,
            SlotCount = templates.Item(resolved)?.SlotCount ?? (templates.HasItems ? (byte)0 : DefaultSlotCount) };
    }

    public bool ContainsSlot(byte slot) => slot < SlotCount;

    /// <summary>Finds the item in the given slot (C++ <c>CTInven::FindTItem</c>), or null.</summary>
    public Item? FindItem(byte slot) => Items.FirstOrDefault(it => it.ItemSlot == slot);

    /// <summary>C++ <c>CTInven::GetBlankPos</c> — the lowest free slot in <c>[0, SlotCount)</c>, or
    /// <c>INVALID_SLOT</c> when the container is full.</summary>
    public byte GetBlankPos()
    {
        for (byte i = 0; i < SlotCount; i++)
            if (FindItem(i) is null) return i;
        return Proto.InvalidSlot;
    }

    /// <summary>C++ <c>CTInven::GetEasePos</c> — the slot of the first same-stack item (map key order) that
    /// still has room under its template stack cap, or <c>INVALID_SLOT</c>. Non-stackable gear never
    /// matches.</summary>
    public byte GetEasePos(Item it)
    {
        foreach (var e in Items.OrderBy(i => i.ItemSlot))
            if (e.SameStackAs(it) && e.Count < (e.Template?.Stack ?? 1)) return e.ItemSlot;
        return Proto.InvalidSlot;
    }
}
