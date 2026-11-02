using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Extra bags and the return point — C++ <c>OnCS_INVENADD_REQ</c> / <c>OnCS_INVENDEL_REQ</c> /
/// <c>OnCS_INVENMOVE_REQ</c> (CSHandler.cpp:9886-10111) and <c>OnCS_SETRETURNPOS_REQ</c> (CSHandler.cpp:10743).
///
/// <para>A bag is an <c>IT_INVEN</c> item. Equipping it turns the item into a container (the item is deleted,
/// a <see cref="Inven"/> takes its id, template, expiry and ELD); unequipping an empty bag turns it back into
/// an item. Moving swaps two bag ids. The bag rows themselves persist with the full inventory save, as in the
/// C++ (<c>TSaveInven</c> at char save); the items inside go through the incremental path.</para>
///
/// <para><b>Not ported:</b> the secure-code lock on removing/moving bags (secure codes are unported), and the
/// other mode-specific bag restrictions.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte InvenSuccess = 0, InvenExist = 1, InvenNotEmpty = 2, InvenFull = 3, InvenLevel = 4, InvenFail = 5;
    private const byte ItInven = 11;   // TITEM_TYPE IT_INVEN (NetCode.h:1147 — IT_MONEY is 10)

    private void OnCS_INVENADD_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte desInven = r.ReadByte(), srcInven = r.ReadByte(), itemSlot = r.ReadByte();
        if (s.Store.IsOpen) return;
        if (desInven >= 5) { SendCS_INVENADD_ACK(s, InvenFail, 0, 0, 0); return; }

        if (ch.FindInven(desInven) is not null) { SendCS_INVENADD_ACK(s, InvenExist, 0, 0, 0); return; }
        if (ch.FindInven(srcInven) is not { } src) { SendCS_INVENADD_ACK(s, InvenFail, 0, 0, 0); return; }
        if (src.Items.FirstOrDefault(i => i.ItemSlot == itemSlot) is not { Count: > 0 } item)
        {
            SendCS_INVENADD_ACK(s, InvenFail, 0, 0, 0);
            return;
        }
        if (item.Template?.Type != ItInven) { SendCS_INVENADD_ACK(s, InvenFail, 0, 0, 0); return; }
        if (item.EquipLevel > ch.Level) { SendCS_INVENADD_ACK(s, InvenLevel, 0, 0, 0); return; }

        var bag = Inven.FromTemplate(desInven, item.TemplateId, _templates, item.EndTime, (byte)item.Ext[Item.IevEld]);
        SendCS_DELITEM_ACK(s, srcInven, item);
        src.Items.Remove(item);
        ch.Invens.Add(bag);
        SendCS_INVENADD_ACK(s, InvenSuccess, bag.InvenId, bag.TemplateId, bag.EndTime);
    }

    private void OnCS_INVENDEL_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte srcInven = r.ReadByte(), desInven = r.ReadByte(), pos = r.ReadByte();
        if (srcInven >= 5 || desInven == Proto.InvenEquip) { SendCS_INVENDEL_ACK(s, InvenFail, 0); return; }

        var src = ch.FindInven(srcInven);
        var des = ch.FindInven(desInven);
        if (src is null || des is null || src == des) { SendCS_INVENDEL_ACK(s, InvenFail, 0); return; }
        if (src.Items.Count != 0) { SendCS_INVENDEL_ACK(s, InvenNotEmpty, 0); return; }

        if (!des.ContainsSlot(pos) || des.Items.Any(i => i.ItemSlot == pos)) pos = des.GetBlankPos();
        if (pos == Proto.InvalidSlot) { SendCS_INVENDEL_ACK(s, InvenFull, 0); return; }

        var item = new Item
        {
            ItemSlot = pos, TemplateId = src.TemplateId, Template = _templates.Item(src.TemplateId), Count = 1,
            EndTime = src.EndTime,
        };
        item.Ext[Item.IevEld] = src.Eld;
        LinkItemAttr(item);                       // C++ SetItemAttr(pItem, 0)

        des.Items.Add(item);
        SendCS_ADDITEM_ACK(s, desInven, item);    // stamps the dlID and queues the row
        ch.Invens.Remove(src);
        SendCS_INVENDEL_ACK(s, InvenSuccess, srcInven);
    }

    private void OnCS_INVENMOVE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte srcInven = r.ReadByte(), desInven = r.ReadByte();
        if (srcInven >= 5 || desInven >= 5) { SendCS_INVENMOVE_ACK(s, InvenFail, 0, 0); return; }

        if (ch.FindInven(srcInven) is not { } src) { SendCS_INVENMOVE_ACK(s, InvenFail, 0, 0); return; }
        var des = ch.FindInven(desInven);

        if (des is not null) des.InvenId = srcInven;
        src.InvenId = desInven;

        // Not in the C++ (which only saves at char save): the items now live under a different bag id, so their
        // rows are re-queued, keeping the incremental path from writing them back under the old one.
        foreach (var bag in des is null ? new[] { src } : new[] { src, des })
            foreach (var it in bag.Items) EnqueueItemSave(s, bag.InvenId, it);

        SendCS_INVENMOVE_ACK(s, InvenSuccess, srcInven, desInven);
    }

    private void OnCS_SETRETURNPOS_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        ushort npcId = r.ReadUInt16();

        if (_state.FindNpc(npcId) is not { Type: TnpcReturn } npc) { SendCS_SETRETURNPOS_ACK(s, 0); return; }
        if (!CanTalk(npc, ch)) return;   // disguise buff unported ⇒ 0

        if (npc.SpawnPosId != 0)
        {
            ch.Persist.SpawnId = npc.SpawnPosId;                   // m_wSpawnID, saved with the char
            SendCS_SETRETURNPOS_ACK(s, 1);
        }
    }

    private static void SendCS_INVENADD_ACK(ClientSession s, byte result, byte invenId, ushort itemId, long endTime)
    {
        var w = new PacketWriter(Msg.CS_INVENADD_ACK);
        w.WriteByte(result); w.WriteByte(invenId); w.WriteUInt16(itemId); w.WriteInt64(endTime);
        w.WriteByte(1);                           // bShowIF (default TRUE)
        s.Send(w);
    }

    private static void SendCS_INVENDEL_ACK(ClientSession s, byte result, byte invenId)
    {
        var w = new PacketWriter(Msg.CS_INVENDEL_ACK);
        w.WriteByte(result); w.WriteByte(invenId);
        w.WriteByte(1);                           // bShowIF (default TRUE)
        s.Send(w);
    }

    private static void SendCS_INVENMOVE_ACK(ClientSession s, byte result, byte src, byte des)
    {
        var w = new PacketWriter(Msg.CS_INVENMOVE_ACK);
        w.WriteByte(result); w.WriteByte(src); w.WriteByte(des);
        s.Send(w);
    }

    private static void SendCS_SETRETURNPOS_ACK(ClientSession s, byte result)
    {
        var w = new PacketWriter(Msg.CS_SETRETURNPOS_ACK);
        w.WriteByte(result);
        s.Send(w);
    }
}
