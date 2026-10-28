using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The NPC-shop vertical — <c>OnCS_NPCTALK_REQ</c> (CSHandler.cpp:3506), <c>OnCS_ITEMBUY_REQ</c> (6247) and
/// <c>OnCS_ITEMSELL_REQ</c> (6623): open a dialog with a shop NPC, buy stocked items for gold, and sell
/// inventory items back at ¼ price. NPCs are a static server-side registry (<see cref="InitNpcs"/> builds
/// them from <c>TNPCCHART</c>/<c>TNPCITEMCHART</c>) — the client already knows their positions, so nothing is
/// broadcast; the server validates by id + country (<see cref="Npc.CanTalk"/>) and holds the stock.
///
/// <para>Quest pickups resolve a runnable routing quest's QTT_ITEMID term against the item chart, bound to
/// its trigger NPC and map. Ordinary purchases resolve shop stock and pay the shop's price.
/// Deferred (documented — PORT_STATUS.md): <b>BoW-mode</b> pricing (a gold NPC is assumed); the price-up
/// buff on sell (<c>SDT_STATUS_PRICEUP</c>); the secure-code, player-store, deal (trade) and tournament guards;
/// the item-count / quest / UDP logging. No DB save (the C++ buy/sell path saves nothing back).</para>
/// </summary>
public sealed partial class MapService
{
    private const byte TnpcItem = 2;        // TNPC_TYPE TNPC_ITEM (gold shop)
    private const byte TnpcPvPoint = 21;    // TNPC_TYPE TNPC_PVPOINT (PvP-point shop)
    private const byte TnpcReturn = 14;     // TNPC_TYPE TNPC_RETURN (sets the return point)
    private const byte TnpcPortal = 8;      // TNPC_TYPE TNPC_PORTAL (teleporter)
    private const byte ItemtradeSell = 2;   // ITEMTRADE_SELL bit of m_bIsSell

    /// <summary>Builds the runtime NPC registry from the loaded charts (C++ startup <c>CTBLNpc</c> loop +
    /// <c>m_mapItem</c> resolution). Shop stock is resolved only for the item-shop types (TNPC_ITEM /
    /// TNPC_PVPOINT); other NPC types repurpose their id list (skills/portals/…) — deferred. Empty in DB-free
    /// mode. Idempotent.</summary>
    public void InitNpcs()
    {
        InitArenas();                                                                            // MapService.Arena.cs
        foreach (var def in _templates.Npcs.Values)
        {
            var npc = new Npc
            {
                Id = def.Id, Type = def.Type, Country = def.Country,
                DiscountCondition = def.DiscountCondition, DiscountRate = def.DiscountRate, AddProb = def.AddProb, LocalId = def.LocalId,
                MapId = def.MapId, PosX = def.PosX, PosY = def.PosY, PosZ = def.PosZ,
            };
            if (def.Type is TnpcItem or TnpcPvPoint)
                foreach (var itemId in def.ItemIds)
                    if (_templates.Item(itemId) is { } t) npc.Items[itemId] = t;
            if (def.Type is TnpcSkillMaster or TnpcSkillRent)
                foreach (var skillId in def.ItemIds)
                    if (_templates.Skills.TryGetValue(skillId, out var st)) npc.Skills[skillId] = st;
            if (def.Type == TnpcReturn)
                foreach (var spawnPos in def.ItemIds) npc.SpawnPosId = spawnPos;
            if (def.Type == TnpcPortal)
                foreach (var portal in def.ItemIds)
                    if (_templates.Portals.ContainsKey(portal)) npc.PortalId = portal;
            if (def.Type == TnpcMonster) InitMonsterShop(npc);
            if (def.Type == TnpcArena)                                                           // MapService.Arena.cs
                npc.ArenaId = (ushort)def.ItemIds.FirstOrDefault(id => _arenas.ContainsKey((ushort)id));
            if (def.Type == TnpcMagicItem)                                                       // MapService.Gamble.cs
                foreach (var qid in def.ItemIds)
                    if (_templates.QuestItems.TryGetValue(qid, out var qi)) npc.MagicItems[qid] = qi;
            npc.RequiredItemId = def.ItemId;
            _state.AddNpc(npc);
        }
        if (_templates.Npcs.Count > 0)
            _log.LogInformation("Initialized {Npcs} NPCs.", _templates.Npcs.Count);
    }

    /// <summary>Registers a single NPC (test injection / bring-up). Mirrors <see cref="SpawnMonster"/> but with
    /// no view broadcast — NPCs are static fixtures the client already knows.</summary>
    public void AddNpc(Npc n) => _state.AddNpc(n);

    // ---- handlers ----

    private void OnCS_NPCTALK_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;
        ushort npcId = r.ReadUInt16();

        if (_state.FindNpc(npcId) is not { } npc) return;
        if (!CanTalk(npc, ch)) return; // disguise buff unported ⇒ 0
        // C++ advances any QTT_TALK objective keyed by this NPC and echoes the matched quest id (0 = none).
        uint questId = PlayerCheckQuest(s, ch, npcId, QttTalk, 0, 1);
        SendCS_NPCTALK_ACK(s, questId, npcId);
    }

    private void OnCS_ITEMBUY_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;
        ushort npcId = r.ReadUInt16();
        uint questId = r.ReadUInt32();
        ushort itemId = r.ReadUInt16();
        byte count = r.ReadByte();
        byte npcInven = r.ReadByte();
        byte npcItem = r.ReadByte();
        // (secure-code / deal / tournament guards deferred.)

        var npc = _state.FindNpc(npcId);
        if (npc is null || count == 0) { SendCS_ITEMBUY_ACK(s, ch, ItemBuyResult.NotFound, itemId); return; }
        if (!CanTalk(npc, ch)) return;

        ItemTemplate? t;
        if (questId != 0)
        {
            var quest = _templates.Quest(questId);
            bool canCollect = quest is not null && quest.Type == (byte)QuestType.Routing
                && quest.TriggerType == TtTalkNpc && quest.TriggerId == npcId && npc.MapId == ch.MapId
                && CanRunQuest(ch, quest, out _) == QctNone
                && quest.Terms.Any(term => term.TermType == QttItemId && term.TermId == itemId);
            t = canCollect ? _templates.Item(itemId) : null;
        }
        else
            t = npc.GetItem(itemId);
        if (t is null) { SendCS_ITEMBUY_ACK(s, ch, ItemBuyResult.NotFound, itemId); return; }

        if (t.Stack < count) count = t.Stack;

        uint buyPrice = 0;
        if (questId == 0 && npc.Type == TnpcPvPoint)
        {
            // A PvP shop takes useable PvP points (GetItemPvPrice), answering ITEMBUY_NEEDMONEY when they fall short.
            buyPrice = GetItemPvPrice(t) * count;
            buyPrice -= buyPrice * DiscountRate(ch, npc) / 100;
            if (ch.PvpUseablePoint < buyPrice) { SendCS_ITEMBUY_ACK(s, ch, ItemBuyResult.NeedMoney, itemId); return; }
        }
        else if (questId == 0) // C++: !dwQuestID && npc not on the BoW map (BoW pricing deferred ⇒ gold)
        {
            buyPrice = GetItemPrice(t) * count;
            byte discount = DiscountRate(ch, npc);                         // GetDiscountRate (MapService.Fort.cs)
            buyPrice -= buyPrice * discount / 100;
            if (!ch.UseMoney(buyPrice, commit: false))
            {
                SendCS_ITEMBUY_ACK(s, ch, ItemBuyResult.NeedMoney, itemId);
                return;
            }
        }

        var bought = new Item { TemplateId = itemId, Count = count, Template = t };
        LinkItemAttr(bought);
        var one = new[] { bought };
        if (!CanPush(ch, one)) { SendCS_ITEMBUY_ACK(s, ch, ItemBuyResult.CantPush, itemId); return; }

        // Portable-NPC-call item (only when a slot is supplied): map must be 0 or 8; consume one IK_NPCCALL.
        if (npcInven != Proto.InvenNull && npcItem != Proto.InvalidSlot)
        {
            if (ch.MapId != 0 && ch.MapId != 8) { SendCS_ITEMBUY_ACK(s, ch, ItemBuyResult.InvalidPos, 0); return; }
            var callInv = ch.FindInven(npcInven);
            var call = callInv?.FindItem(npcItem);
            if (call is null || call.Count == 0 || call.Template is not { Kind: IkNpcCall })
            {
                SendCS_ITEMBUY_ACK(s, ch, ItemBuyResult.NpcCallError, 0);
                return;
            }
            call.Count -= 1;
            if (call.Count == 0) { callInv!.Items.Remove(call); SendCS_DELITEM_ACK(s, npcInven, call); }
            else SendCS_UPDATEITEM_ACK(s, npcInven, call);
        }

        PushTItem(s, one);
        if (questId == 0 && npc.Type == TnpcPvPoint) UsePvPoint(s, ch, buyPrice, PvpeBuyItem, PvpUseable);
        else if (questId == 0) ch.UseMoney(buyPrice, commit: true); // actual deduction (C++ UseMoney(...,TRUE))
        SendCS_ITEMBUY_ACK(s, ch, ItemBuyResult.Success, itemId);
        CheckQuest(s, 0, ch.PosX, ch.PosY, ch.PosZ, itemId, QttGetItem, TtGetItem, count); // advance/trigger get-item quests
    }

    private void OnCS_ITEMSELL_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;
        byte invenId = r.ReadByte();
        byte pos = r.ReadByte();
        byte count = r.ReadByte();
        byte npcInven = r.ReadByte();
        byte npcItem = r.ReadByte();
        // (secure-code / player-store / deal / tournament guards deferred.)

        if (invenId == Proto.InvenEquip) return; // equipped gear can't be sold

        var inv = ch.FindInven(invenId);
        if (inv is null) { SendCS_ITEMSELL_ACK(s, ch, ItemSellResult.NotFound); return; }
        var it = inv.FindItem(pos);
        if (it is null || count == 0 || it.Count == 0) { SendCS_ITEMSELL_ACK(s, ch, ItemSellResult.NotFound); return; }
        ushort wItemId = it.TemplateId;
        if (it.Count < count) { SendCS_ITEMSELL_ACK(s, ch, ItemSellResult.NotFound); return; }
        if (it.Template is not { } t || (t.IsSell & ItemtradeSell) == 0)
        {
            SendCS_ITEMSELL_ACK(s, ch, ItemSellResult.CantSell);
            return;
        }

        // Portable-NPC-call item (only when a slot is supplied): map must be 0 or 8; consume one IK_NPCCALL.
        if (npcInven != Proto.InvenNull && npcItem != Proto.InvalidSlot)
        {
            if (ch.MapId != 0 && ch.MapId != 8) { SendCS_ITEMSELL_ACK(s, ch, ItemSellResult.InvalidPos); return; }
            var callInv = ch.FindInven(npcInven);
            var call = callInv?.FindItem(npcItem);
            if (call is null || call.Count == 0 || call.Template is not { Kind: IkNpcCall })
            {
                SendCS_ITEMSELL_ACK(s, ch, ItemSellResult.NpcCallError);
                return;
            }
            call.Count -= 1;
            if (call.Count == 0) { callInv!.Items.Remove(call); SendCS_DELITEM_ACK(s, npcInven, call); }
            else SendCS_UPDATEITEM_ACK(s, npcInven, call);
        }

        uint sellPrice = GetSellUnitPrice(it) / 4 * count; // C++ GetPrice()/4 * bCount (int /4 then ×count)
        // Price-up buff (SDT_STATUS_PRICEUP) deferred ⇒ no bonus.
        ch.EarnMoney(sellPrice);

        if (it.Count == count) { inv.Items.Remove(it); SendCS_DELITEM_ACK(s, invenId, it); }
        else { it.Count -= count; SendCS_UPDATEITEM_ACK(s, invenId, it); }

        SendCS_ITEMSELL_ACK(s, ch, ItemSellResult.Success);
        PlayerCheckQuest(s, ch, wItemId, QttGetItem, 0, 0); // refresh any get-item objective (count re-read from bags)
    }

    // ---- pricing ----

    /// <summary>C++ <c>CTMapSvrModule::GetItemPrice(LPTITEM)</c> (TMapSvr.cpp:9605) — the shop (template) buy
    /// price: <c>DWORD(m_dwMoney[grade]·m_fPrice + 0.99)</c>, where grade is the template's attr-row grade
    /// (<c>m_mapTItemAttr.find(m_wAttrID)</c>) or, when it has no attr, its default level. A missing money row
    /// ⇒ 0 (DB-free / unknown grade).</summary>
    private uint GetItemPrice(ItemTemplate t)
    {
        byte grade = t.AttrId != 0 ? (_templates.Attr(t.AttrId)?.Grade ?? 0) : t.DefaultLevel;
        return _templates.LevelMoneyOf(grade) is { } money ? (uint)(money * t.Price + 0.99) : 0u;
    }

    /// <summary>C++ <c>CTItem::GetPrice()</c> (TItem.cpp:560) — the per-unit sell price of an item instance:
    /// 0 when it has no template or resolved attr row (the C++ guard), else
    /// <c>DWORD(m_dwMoney[grade]·m_fPrice + 0.99)</c> with grade = the item's power level (attr grade fallback —
    /// the enchant-based weapon/shield level is deferred, as in repair) or, when it has no attr id, its default
    /// level. The caller divides by 4.</summary>
    private uint GetSellUnitPrice(Item it)
    {
        var t = it.Template;
        if (t is null || it.Attr is null) return 0; // C++ if(!m_pTITEM || !m_pTITEMATTR) return 0
        byte grade = t.AttrId != 0 ? it.Attr.Grade : t.DefaultLevel; // GetPowerLevel fallback = attr grade
        return _templates.LevelMoneyOf(grade) is { } money ? (uint)(money * t.Price + 0.99) : 0u;
    }

    // ---- senders ----

    /// <summary>C++ <c>CTPlayer::SendCS_NPCTALK_ACK</c> — dwQuestID (the quest this NPC offers, 0 = none), wNpcID.</summary>
    private static void SendCS_NPCTALK_ACK(ClientSession s, uint questId, ushort npcId)
    {
        var w = new PacketWriter(Msg.CS_NPCTALK_ACK, capacity: 8);
        w.WriteUInt32(questId);
        w.WriteUInt16(npcId);
        s.Send(w);
    }

    /// <summary>C++ <c>CTPlayer::SendCS_ITEMBUY_ACK</c> — bRet, wItemID, then the three currency tiers.</summary>
    private static void SendCS_ITEMBUY_ACK(ClientSession s, Character ch, ItemBuyResult result, ushort itemId)
    {
        var w = new PacketWriter(Msg.CS_ITEMBUY_ACK, capacity: 16);
        w.WriteByte((byte)result);
        w.WriteUInt16(itemId);
        w.WriteUInt32(ch.Gold);
        w.WriteUInt32(ch.Silver);
        w.WriteUInt32(ch.Cooper);
        s.Send(w);
    }

    /// <summary>C++ <c>CTPlayer::SendCS_ITEMSELL_ACK</c> — bResult, then the three currency tiers.</summary>
    private static void SendCS_ITEMSELL_ACK(ClientSession s, Character ch, ItemSellResult result)
    {
        var w = new PacketWriter(Msg.CS_ITEMSELL_ACK, capacity: 16);
        w.WriteByte((byte)result);
        w.WriteUInt32(ch.Gold);
        w.WriteUInt32(ch.Silver);
        w.WriteUInt32(ch.Cooper);
        s.Send(w);
    }
}
