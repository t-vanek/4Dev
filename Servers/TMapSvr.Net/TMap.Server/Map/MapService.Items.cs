using TMap.Protocol;
using TMap.Data;

namespace TMap.Server.Map;

/// <summary>
/// Item manipulation — <c>OnCS_MOVEITEM_REQ</c> (CSHandler.cpp:1782). Moves/swaps/splits/merges an item
/// between inventory slots, drops it (dest <c>INVEN_NULL</c>), or equips/unequips it (dest/src
/// <c>INVEN_EQUIP</c>) with <c>CanEquip</c> validation. On an equip/unequip the appearance is broadcast to
/// the 3×3 view via <c>CS_EQUIP_ACK</c> and the stat sheet is recomputed (live).
///
/// <para>Two-handed auto-eviction (Phase 9) is now implemented: equipping a 2H weapon evicts whatever
/// occupies the off-hand (<c>ES_SNDWEAPON</c>) back into the bags via the C++ <c>CanPush</c>/<c>PushTItem</c>
/// blank-slot allocator (<c>MI_INVENFULL</c> when the bags can't hold it — never auto-dropped), and the
/// unequip-into-occupied-slot case is normalized to the equip path exactly as the C++ does.</para>
///
/// <para>Deferred (documented): the skill-requirement gate (<c>MI_NOSKILL</c> — skills not ported, treated
/// as satisfied), warrior stance auto-buffs, the deal/store/secure-code guards, the special-bag
/// <c>MI_CANTDROP</c> rule, and all DB persistence (the C++ move path saves nothing; inventory is flushed by
/// the deferred periodic/logout <c>DM_SAVEITEM</c>).</para>
/// </summary>
public sealed partial class MapService
{
    private void OnCS_MOVEITEM_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;

        byte src = r.ReadByte();
        byte srcSlot = r.ReadByte();
        byte dst = r.ReadByte();
        byte dstSlot = r.ReadByte();
        byte count = r.ReadByte();

        var srcInv = ch.FindInven(src);
        if (srcInv is null) { SendCS_MOVEITEM_ACK(s, MoveItemResult.NoSrcInven); return; }

        var srcItem0 = srcInv.FindItem(srcSlot);
        if (srcItem0 is null || srcItem0.Count == 0 || count == 0)
        {
            SendCS_MOVEITEM_ACK(s, MoveItemResult.NoSrcItem);
            return;
        }
        Item srcItem = srcItem0;

        // ---- DROP / DESTROY ----
        if (dst == Proto.InvenNull)
        {
            // C++ (CSHandler.cpp:1885): a full-stack drop deletes THIS item; a partial (or over-count) drop
            // routes through UseItem(wItemID, count) — consuming `count` of the template across all bags.
            if (count != srcItem.Count)
            {
                UseItemByTemplate(s, ch, srcItem.TemplateId, count);
            }
            else
            {
                srcInv.Items.Remove(srcItem);
                SendCS_DELITEM_ACK(s, src, srcItem);
            }
            PlayerCheckQuest(s, ch, srcItem.TemplateId, QttGetItem, 0, 0);
            SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
            return;
        }

        var dstInv = ch.FindInven(dst);
        if (dstInv is null) { SendCS_MOVEITEM_ACK(s, MoveItemResult.NoDestInven); return; }
        if (dst != Proto.InvenEquip && !dstInv.ContainsSlot(dstSlot)) { SendCS_MOVEITEM_ACK(s, MoveItemResult.InvenFull); return; }
        if (src == dst && srcSlot == dstSlot) { SendCS_MOVEITEM_ACK(s, MoveItemResult.SamePos); return; }

        bool dstEquip = dst == Proto.InvenEquip;

        // Equipped items never stack; the sub-slot canonicalizes to the primary slot (C++ 1926-1935).
        if (dstEquip)
        {
            if (srcItem.Template is { } tpl && dstSlot == tpl.SubSlot) dstSlot = tpl.PrmSlot;
            count = 1;
        }
        if (src == Proto.InvenEquip) count = 1;

        var dstItem = dstInv.FindItem(dstSlot);

        // MI_BOTHHANDWEAPON: can't fill an empty off-hand while a 2H in the primary slot reserves it (1938-1948).
        if (dstItem is null && dstEquip && dstSlot == Proto.EsSndWeapon)
        {
            var prm = dstInv.FindItem(Proto.EsPrmWeapon);
            if (prm?.Template is { } pt && pt.SubSlot == dstSlot)
            {
                SendCS_MOVEITEM_ACK(s, MoveItemResult.BothHandWeapon);
                return;
            }
        }

        // Normalization (C++ 1950-1967): unequipping INTO an occupied non-equip slot swaps the src/dst roles
        // so the rest of the handler reads uniformly as "equip the bag item, swapping out the equipped one".
        if (dstItem is not null && src == Proto.InvenEquip && !dstEquip)
        {
            (srcInv, dstInv) = (dstInv, srcInv);
            (src, dst) = (dst, src);
            (srcItem, dstItem) = (dstItem, srcItem);
            dstSlot = dstItem!.ItemSlot;   // pTItemDEST->m_bItemID (the equipped item's slot)
            srcSlot = srcItem.ItemSlot;    // pTItemSRC->m_bItemID (the bag item's slot)
            dstEquip = true;               // dst is now INVEN_EQUIP
        }

        // ---- equip validation (C++ CanEquip, 1969-1986) ----
        if (dstEquip)
        {
            var can = CanEquip(ch, srcItem, dstSlot);
            if (can != MoveItemResult.Success) { SendCS_MOVEITEM_ACK(s, can); return; }
        }
        if (dstItem is not null && src == Proto.InvenEquip)   // only the equip↔equip case survives normalization
        {
            var can = CanEquip(ch, dstItem, srcSlot);
            if (can != MoveItemResult.Success) { SendCS_MOVEITEM_ACK(s, can); return; }
        }

        // ---- two-handed / stack-into-equip eviction list (C++ 1988-2013) ----
        var evict = new List<Item>();
        if (dstEquip)
        {
            // Equipping one off a >1 stack into an occupied slot can't swap back into a stack ⇒ evict the dest.
            if (dstItem is not null && srcItem.Count > 1 && !dstItem.SameStackAs(srcItem)) evict.Add(dstItem);
            // A two-hander (real sub-slot) evicts whatever holds its off-hand slot.
            if (srcItem.Template is { SubSlot: var sub } && sub != Proto.InvalidSlot &&
                dstInv.FindItem(sub) is { } offHand) evict.Add(offHand);
        }
        if (src == Proto.InvenEquip && dstItem?.Template is { SubSlot: var dsub } && dsub != Proto.InvalidSlot &&
            srcInv.FindItem(dsub) is { } offHand2) evict.Add(offHand2);

        if (!CanPush(ch, evict)) { SendCS_MOVEITEM_ACK(s, MoveItemResult.InvenFull); return; }

        if (evict.Count > 0)
        {
            var equip = ch.FindInven(Proto.InvenEquip)!;
            foreach (var it in evict) { SendCS_DELITEM_ACK(s, Proto.InvenEquip, it); equip.Items.Remove(it); }
            PushTItem(s, evict);
        }

        // ---- placement (C++ 2041-2123) ----
        dstItem = dstInv.FindItem(dstSlot);
        count = (byte)Math.Min((int)count, srcItem.Count);

        if (dstItem is null)
        {
            // move to empty / split (count < the source stack)
            var moved = srcItem.Clone();
            moved.ItemSlot = dstSlot;
            moved.Count = count;
            srcItem.Count -= count;
            if (srcItem.Count == 0) { srcInv.Items.Remove(srcItem); SendCS_DELITEM_ACK(s, src, srcItem); }
            else SendCS_UPDATEITEM_ACK(s, src, srcItem);
            dstInv.Items.Add(moved);
            SendCS_ADDITEM_ACK(s, dst, moved);
        }
        else if (!dstItem.SameStackAs(srcItem))
        {
            // swap (also every equip-into-occupied-slot case; equipped items never merge)
            srcItem.ItemSlot = dstSlot;
            dstItem.ItemSlot = srcSlot;
            if (src != dst)
            {
                srcInv.Items.Remove(srcItem);
                dstInv.Items.Remove(dstItem);
                dstInv.Items.Add(srcItem);
                srcInv.Items.Add(dstItem);
            }
            SendCS_UPDATEITEM_ACK(s, dst, srcItem);
            SendCS_UPDATEITEM_ACK(s, src, dstItem);
        }
        else if (!dstEquip)
        {
            // merge identical stacks (non-equip), up to the destination template's stack cap
            int room = (dstItem.Template?.Stack ?? 1) - dstItem.Count;
            int mv = Math.Min((int)count, Math.Max(room, 0));
            dstItem.Count += (byte)mv;
            srcItem.Count -= (byte)mv;
            if (srcItem.Count == 0) { srcInv.Items.Remove(srcItem); SendCS_DELITEM_ACK(s, src, srcItem); }
            else SendCS_UPDATEITEM_ACK(s, src, srcItem);
            SendCS_UPDATEITEM_ACK(s, dst, dstItem);
        }
        // (else: identical stacks into the equip inven ⇒ no-op, matching the C++ fall-through)

        if (dstEquip || src == Proto.InvenEquip) ChangeEquipItem(s);
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
    }

    /// <summary>C++ <c>CTObjBase::CanEquip</c> — wrapped→Wrap, slot-mask→CannotEquip, class-mask→NoMatchClass,
    /// level→LowLevel, and before the slot the mastery gate: an <c>m_bEquipSkill</c> item needs a learned weapon
    /// mastery for its kind (MI_NOSKILL). A template-less item (DB-free) is allowed.</summary>
    private static MoveItemResult CanEquip(Character ch, Item item, byte slot)
    {
        if (item.Ext[Item.IevWrap] != 0) return MoveItemResult.Wrap;   // C++ !CanUse() (sealed/wrapped)
        if (item.Template is not { } t) return MoveItemResult.Success;  // DB-free: nothing to validate against
        if (t.EquipSkill != 0 && !ch.Skills.Any(k => k.Template?.CanEquip(t.Kind) == true)) return MoveItemResult.NoSkill;
        if ((t.SlotId & (1u << slot)) == 0) return MoveItemResult.CannotEquip;
        if ((t.ClassId & (1u << ch.Class)) == 0) return MoveItemResult.NoMatchClass;
        if (ch.Level < item.EquipLevel) return MoveItemResult.LowLevel;
        return MoveItemResult.Success;
    }

    /// <summary>C++ <c>CTPlayer::ChangeEquipItem</c> (TPlayer.cpp:4901), in order: broadcast the appearance
    /// (<c>CS_EQUIP_ACK</c>) to the 3×3 view (incl. self); send the actor its own <c>CS_MOVEITEM_ACK</c>
    /// (MI_SUCCESS) — the outer move handler sends a <b>second</b> one at its tail, so an equip/unequip yields
    /// two, matching the C++; send the recomputed stat sheet; clamp current HP/MP to the (possibly lower) max;
    /// and send the actor its own <c>CS_HPMP_ACK</c>; then drop the self-buffs the new gear no longer allows
    /// (<c>CheckEquipSkill</c>). (Warrior stance auto-buff deferred.)</summary>
    private void ChangeEquipItem(ClientSession s)
    {
        var ch = s.Char!;
        var equipAck = BuildCS_EQUIP_ACK(ch);
        foreach (var other in _state.InView(s)) other.Send(equipAck);
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
        SendCS_CHARSTATINFO_ACK(s, ch);
        uint maxHp = MaxHpFor(ch), maxMp = MaxMpFor(ch);
        ch.Hp = Math.Min(ch.Hp, maxHp);
        ch.Mp = Math.Min(ch.Mp, maxMp);
        SendSelfHpMp(s, ch.CharId, maxHp, ch.Hp, maxMp, ch.Mp);
        CheckEquipSkill(s, ch);
    }

    /// <summary>C++ <c>CTPlayer::UseItem(WORD wItemID, BYTE bCount)</c> (TPlayer.cpp:5798) — consumes
    /// <paramref name="count"/> copies of a template across all inventories in id order (then slot order),
    /// emitting <c>CS_DELITEM_ACK</c> per emptied stack and <c>CS_UPDATEITEM_ACK</c> for the final partial one,
    /// stopping once <paramref name="count"/> is exhausted.</summary>
    private void UseItemByTemplate(ClientSession s, Character ch, ushort templateId, int count)
    {
        foreach (var inv in ch.Invens.OrderBy(i => i.InvenId))
            foreach (var it in inv.Items.Where(x => x.TemplateId == templateId).OrderBy(x => x.ItemSlot).ToList())
            {
                if (count <= 0) return;
                if (it.Count > count)
                {
                    it.Count -= (byte)count;
                    SendCS_UPDATEITEM_ACK(s, inv.InvenId, it);
                    return;
                }
                count -= it.Count;
                inv.Items.Remove(it);
                SendCS_DELITEM_ACK(s, inv.InvenId, it);
            }
    }

    /// <summary>C++ <c>SendCS_HPMP_ACK</c> to the actor only (the <c>bLevel</c> arg is not on the wire).</summary>
    private void SendSelfHpMp(ClientSession s, uint id, uint maxHp, uint hp, uint maxMp, uint mp)
    {
        var w = new PacketWriter(Msg.CS_HPMP_ACK, capacity: 24);
        w.WriteUInt32(id);
        w.WriteByte(OtPc);
        w.WriteUInt32(maxHp);
        w.WriteUInt32(hp);
        w.WriteUInt32(maxMp);
        w.WriteUInt32(mp);
        s.Send(w);
        NotifyPartyManStat(s, id, OtPc, maxHp, hp, maxMp, mp);
    }

    /// <summary>C++ <c>CTObjBase::CanPush(vector)</c> — can every item (and any stack remainder) be placed by
    /// the ease-pos-then-blank-pos search across <c>INVEN_DEFAULT</c> then the other non-equip bags? A
    /// non-mutating dry run of <see cref="PushTItem"/>. Empty list ⇒ trivially true. (Note: the ease-pos
    /// merge considers only pre-existing stacks, not partial stacks created earlier in the same push — only
    /// reachable when a single pushed stack overflows one slot, which no equip-eviction produces.)</summary>
    private static bool CanPush(Character ch, IReadOnlyList<Item> items)
    {
        if (items.Count == 0) return true;
        var bags = ch.PushBags();
        var addedToStack = new Dictionary<(Inven, byte), int>();     // extra count virtually merged in
        var claimedBlank = new Dictionary<Inven, HashSet<byte>>();   // blank slots virtually taken

        foreach (var src in items)
        {
            int remaining = src.Count;
            while (remaining > 0)
            {
                bool step = false;
                foreach (var bag in bags)                            // ease-pos: fill existing same-stacks
                {
                    foreach (var e in bag.Items.OrderBy(i => i.ItemSlot))
                    {
                        if (!e.SameStackAs(src)) continue;
                        int used = e.Count + (addedToStack.GetValueOrDefault((bag, e.ItemSlot)));
                        int room = (e.Template?.Stack ?? 1) - used;
                        if (room <= 0) continue;
                        int fill = Math.Min(remaining, room);
                        addedToStack[(bag, e.ItemSlot)] = addedToStack.GetValueOrDefault((bag, e.ItemSlot)) + fill;
                        remaining -= fill; step = true; break;
                    }
                    if (step) break;
                }
                if (step) continue;
                foreach (var bag in bags)                            // blank-pos: claim a free slot
                {
                    for (byte i = 0; i < bag.SlotCount; i++)
                    {
                        if (bag.FindItem(i) is not null) continue;
                        if (claimedBlank.TryGetValue(bag, out var set) && set.Contains(i)) continue;
                        if (!claimedBlank.TryGetValue(bag, out set)) claimedBlank[bag] = set = new();
                        set.Add(i);
                        remaining -= Math.Min(remaining, src.Template?.Stack ?? 1);
                        step = true; break;
                    }
                    if (step) break;
                }
                if (!step) return false;                             // MI_INVENFULL
            }
        }
        return true;
    }

    /// <summary>C++ <c>CTPlayer::PushTItem</c> — places each item into the bags (ease-pos then blank-pos,
    /// re-queried each step exactly as the C++ does), emitting <c>CS_UPDATEITEM_ACK</c> for a stack merge and
    /// <c>CS_ADDITEM_ACK</c> for a fresh slot. The caller must have gated on <see cref="CanPush"/>.</summary>
    private void PushTItem(ClientSession s, IReadOnlyList<Item> items)
    {
        var ch = s.Char!;
        foreach (var src in items)
        {
            int remaining = src.Count;
            while (remaining > 0)
            {
                var (bag, slot) = FindPushSlot(ch, src);
                if (bag is null) break;                              // CanPush-gated; defensive
                var dest = bag.FindItem(slot);
                if (dest is not null)
                {
                    int fill = Math.Min(remaining, (dest.Template?.Stack ?? 1) - dest.Count);
                    dest.Count += (byte)fill; remaining -= fill;
                    SendCS_UPDATEITEM_ACK(s, bag.InvenId, dest);
                }
                else
                {
                    int put = Math.Min(remaining, src.Template?.Stack ?? 1);
                    var placed = (put == remaining && remaining == src.Count) ? src : src.Clone();
                    placed.ItemSlot = slot; placed.Count = (byte)put;
                    bag.Items.Add(placed); remaining -= put;
                    SendCS_ADDITEM_ACK(s, bag.InvenId, placed);
                }
            }
        }
    }

    /// <summary>The C++ ease-pos-then-blank-pos search order over <see cref="Character.PushBags"/>: an
    /// existing same-stack with room first (default bag then others), then the lowest blank slot (default bag
    /// then others).</summary>
    private static (Inven? bag, byte slot) FindPushSlot(Character ch, Item src)
    {
        var bags = ch.PushBags();
        foreach (var bag in bags) { var e = bag.GetEasePos(src); if (e != Proto.InvalidSlot) return (bag, e); }
        foreach (var bag in bags) { var b = bag.GetBlankPos(); if (b != Proto.InvalidSlot) return (bag, b); }
        return (null, Proto.InvalidSlot);
    }

    // ---- senders ----

    private static void SendCS_MOVEITEM_ACK(ClientSession s, MoveItemResult result)
    {
        var w = new PacketWriter(Msg.CS_MOVEITEM_ACK);
        w.WriteByte((byte)result);
        s.Send(w);
    }

    // These three are the item-mutation choke points: every add/change/removal of a player item flows through
    // them, so they double as the incremental-persistence hook (EnqueueItemSave/Delete, gated + no-op DB-free).
    private void SendCS_UPDATEITEM_ACK(ClientSession s, byte invenId, Item item)
    {
        var w = new PacketWriter(Msg.CS_UPDATEITEM_ACK, capacity: 64);
        w.WriteByte(invenId);
        item.WrapPacketClient(w, s.CharId);
        s.Send(w);
        EnqueueItemSave(s, invenId, item);
    }

    private void SendCS_ADDITEM_ACK(ClientSession s, byte invenId, Item item)
    {
        var w = new PacketWriter(Msg.CS_ADDITEM_ACK, capacity: 64);
        w.WriteByte(invenId);
        item.WrapPacketClient(w, s.CharId);
        s.Send(w);
        EnqueueItemSave(s, invenId, item);
    }

    // Takes the removed Item (not just its slot) so the incremental delete has its dlID; the wire still carries
    // only invenId + slot (the item's own slot — unchanged from removal).
    private void SendCS_DELITEM_ACK(ClientSession s, byte invenId, Item removed)
    {
        var w = new PacketWriter(Msg.CS_DELITEM_ACK);
        w.WriteByte(invenId);
        w.WriteByte(removed.ItemSlot);
        s.Send(w);
        EnqueueItemDelete(s, removed);
    }

    /// <summary>C++ <c>SendCS_EQUIP_ACK</c> — the actor's full equipped set (appearance), re-sent whole:
    /// <c>dwCharID</c>, <c>BYTE count</c>, then each equipped item via <c>WrapPacketClient</c> (slot-ordered).</summary>
    private static byte[] BuildCS_EQUIP_ACK(Character ch)
    {
        var w = new PacketWriter(Msg.CS_EQUIP_ACK, capacity: 128);
        w.WriteUInt32(ch.CharId);
        var items = ch.Equipped?.Items;
        w.WriteByte((byte)(items?.Count ?? 0));
        if (items is not null)
            foreach (var it in items.OrderBy(i => i.ItemSlot))
                it.WrapPacketClient(w, ch.CharId);
        return w.ToArray();
    }

    // ---- CS_ITEMUSE (HP/MP potions) ----

    // TITEM_KIND (NetCode.h) — the potion use-effect kinds this phase handles.
    private const byte IkHp = 26, IkMp = 27, IkMaxHp = 42, IkMaxMp = 43, IkCredits = 87;
    private const byte OtPc = 1; // OBJ_TYPE OT_PC

    /// <summary>
    /// C++ <c>OnCS_ITEMUSE_REQ</c> (CSHandler.cpp:9092) for HP/MP potions: validate → heal
    /// <c>m_dwHP/m_dwMP += m_wUseValue</c> (or to max for the IK_MAXHP/IK_MAXMP full-restore kinds) clamped
    /// to <c>GetMaxHP/MP</c>, broadcast the new bar to the 3×3 view, consume one, and reply. Already-full →
    /// <c>IU_FULL</c> and no consume. Non-HP/MP kinds, the server-side cooldown, targeted/buff items, the
    /// deal/store/riding/secure guards and the tournament map are deferred (documented — PORT_STATUS.md).
    /// </summary>
    private async Task OnCS_ITEMUSE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;

        ushort tempId = r.ReadUInt16();
        byte invenId = r.ReadByte();
        byte itemSlot = r.ReadByte();
        ushort delayGroup = r.ReadUInt16();
        byte count = r.ReadByte();
        for (int i = 0; i < count; i++) { r.ReadUInt32(); r.ReadByte(); } // targets (unused for self-potions)

        var inv = ch.FindInven(invenId);
        var item = inv?.FindItem(itemSlot);
        if (item is null || item.Count == 0 || item.TemplateId != tempId)
        {
            SendCS_ITEMUSE_ACK(s, ItemUseResult.NotFound, delayGroup, 0, 0);
            return;
        }
        if (item.Ext[Item.IevWrap] != 0)
        {
            SendCS_ITEMUSE_ACK(s, ItemUseResult.Wrapping, delayGroup, 0, 0);
            return;
        }

        byte kind = item.Template?.Kind ?? 0;
        uint delay = item.Template?.Delay ?? 0;
        if (TournamentItemBanned(ch, item)) { SendCS_ITEMUSE_ACK(s, ItemUseResult.NeedTime, delayGroup, kind, 0); return; }   // MapService.TournamentMatch.cs
        if (ch.Riding != 0 && kind != IkWhip) { SendCS_ITEMUSE_ACK(s, ItemUseResult.Riding, delayGroup, kind, 0); return; }

        // C++ anti-tamper (CSHandler.cpp:9202): the item's own delay-group must match the request — chart-gated
        // (a DB-free item has no template, so the guard is skipped rather than rejecting on the default 0).
        if (item.Template is { } dgt && dgt.DelayGroup != delayGroup)
        {
            SendCS_ITEMUSE_ACK(s, ItemUseResult.NotFound, delayGroup, kind, 0);
            return;
        }

        // C++: a revival item only when dead, anything else only when alive → IU_NOTFOUND
        if ((ch.Hp == 0) != (kind == IkRevival))
        {
            SendCS_ITEMUSE_ACK(s, ItemUseResult.NotFound, delayGroup, kind, 0);
            return;
        }
        if (item.Template is { } t && t.DefaultLevel > ch.Level)
        {
            SendCS_ITEMUSE_ACK(s, ItemUseResult.NeedLevel, delayGroup, kind, 0);
            return;
        }

        // The item delay: a delay group still cooling → IU_NOTFOUND (C++ m_mapItemCoolTime).
        ushort group = item.Template?.DelayGroup ?? delayGroup;
        if (delay != 0 && ch.ItemCoolTime.TryGetValue(group, out uint ready) && unchecked((int)(ready - NowMs)) > 0)
        {
            SendCS_ITEMUSE_ACK(s, ItemUseResult.NotFound, delayGroup, kind, 0);
            return;
        }

        // C++ IT_ACTITEMS: its skill's action shown around — nothing used up, no answer (MapService.DuringItem.cs).
        if (item.Template is { Type: ItActItems } act) { PlayActItem(s, ch, act); return; }

        uint heal = item.Template?.UseValue ?? 0u;
        ItemTemplate? cashBonus = null;
        bool used;
        if (kind is IkHp or IkMaxHp)
        {
            uint maxHp = MaxHpFor(ch);
            used = ch.Hp < maxHp;
            if (used) { ch.Hp = kind == IkHp ? Math.Min(ch.Hp + heal, maxHp) : maxHp; BroadcastHpMp(s, ch); }
        }
        else if (kind is IkMp or IkMaxMp)
        {
            uint maxMp = MaxMpFor(ch);
            used = ch.Mp < maxMp;
            if (used) { ch.Mp = kind == IkMp ? Math.Min(ch.Mp + heal, maxMp) : maxMp; BroadcastHpMp(s, ch); }
        }
        else if (kind == IkCredits)
        {
            used = true;                                                   // C++ IK_CREDITS: the item's wUseValue in points
            GainPvPoint(s, ch, heal, PvpeBuyItem, PvpUseable);
        }
        else if (kind is IkSkill or IkRevival && item.Template is { } st)
        {
            // MapService.ItemSkill.cs — the C++ answers IU_NOTFOUND when the skill cannot be used.
            if (!UseSkillItem(s, ch, st)) { SendCS_ITEMUSE_ACK(s, ItemUseResult.NotFound, delayGroup, kind, 0); return; }
            used = true;
        }
        else if (kind == IkReturn && item.Template is { } rt) used = UseReturnItem(s, ch, rt);
        else if (IsLookKind(kind)) used = ChangeLook(s, ch, kind);
        else if (kind is IkGoldPremium or IkGoldPremium2)
        {
            if (ch.Premium is not null) { SendCS_ITEMUSE_ACK(s, ItemUseResult.OverlapPremium, delayGroup, kind, 0); return; }
            used = true;
        }
        else if (kind == IkExpBonus)
        {
            if (IsExpBenefit(ch, out _) != 0) { SendCS_ITEMUSE_ACK(s, ItemUseResult.OverlapExpBonus, delayGroup, kind, 0); return; }
            used = true;
        }
        else if (kind == IkGainExp) { GainExp(s, ch, item.Template?.SpeedInc ?? 0); used = true; }
        else if (kind == IkMoney && item.Template is { } mt) { OpenMoneyPouch(s, ch, mt); used = true; }
        else if (kind == IkSpecialBox && item.Template is { } bt) used = OpenSpecialBox(s, ch, bt);
        else if (kind == IkCash && item.Template is { } ct) { cashBonus = ct; used = true; }
        else if (kind == IkAp || item.Template?.Type == ItUse) used = true;   // C++ default: just used up
        else
        {
            // DB-free items of no known kind.
            SendCS_ITEMUSE_ACK(s, ItemUseResult.NotFound, delayGroup, kind, delay);
            return;
        }

        if (!used)
        {
            SendCS_ITEMUSE_ACK(s, ItemUseResult.Full, delayGroup, kind, delay);   // C++: IU_FULL for any item that did not take
            return;
        }
        if (delay != 0) ch.ItemCoolTime[group] = NowMs + delay;
        if (item.Template is { } du) StartDuringItem(s, ch, du);                 // premium / exp boost (DURINGTYPE_USE)

        // Consume one — C++ gates on m_bConsumable (default 1 ⇒ still consumes for DB-free/synth items).
        if ((item.Template?.Consumable ?? 1) != 0)
        {
            item.Count -= 1;
            if (item.Count == 0) { inv!.Items.Remove(item); SendCS_DELITEM_ACK(s, invenId, item); }
            else SendCS_UPDATEITEM_ACK(s, invenId, item);
        }

        SendCS_ITEMUSE_ACK(s, ItemUseResult.Success, delayGroup, kind, delay);
        CheckQuest(s, 0, ch.PosX, ch.PosY, ch.PosZ, tempId, QttUseItem, TtUseItem, 1);
        // Queue the consumed item before the cash procedure, and finish its callback on the batch task.
        // Fire-and-forget let this work outlive dispatch/logout and flush the pre-consumption item state.
        if (cashBonus is not null) await GainCashBonus(s, ch, cashBonus);
    }

    /// <summary>C++ <c>SendCS_HPMP_ACK</c> broadcast to the 3×3 view (incl. self): the actor's id + type +
    /// max/current HP + max/current MP. Note: no level byte on the wire.</summary>
    private void BroadcastHpMp(ClientSession s, Character ch)
    {
        var w = new PacketWriter(Msg.CS_HPMP_ACK, capacity: 24);
        w.WriteUInt32(ch.CharId);
        w.WriteByte(OtPc);
        w.WriteUInt32(MaxHpFor(ch));
        w.WriteUInt32(ch.Hp);
        w.WriteUInt32(MaxMpFor(ch));
        w.WriteUInt32(ch.Mp);
        var ack = w.ToArray();
        foreach (var other in _state.InView(s))
        {
            other.Send(ack);
            NotifyPartyManStat(other, ch.CharId, OtPc, MaxHpFor(ch), ch.Hp, MaxMpFor(ch), ch.Mp);
        }
    }

    private static void SendCS_ITEMUSE_ACK(ClientSession s, ItemUseResult result, ushort delayGroup, byte kind, uint delay)
    {
        var w = new PacketWriter(Msg.CS_ITEMUSE_ACK, capacity: 12);
        w.WriteByte((byte)result);
        w.WriteUInt16(delayGroup);
        w.WriteByte(kind);
        w.WriteUInt32(delay);
        s.Send(w);
    }
}
