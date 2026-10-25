using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>Custom and guild cloaks, CSHandler.cpp:16352,20874. Item extension slots retain their existing wire format.</summary>
public sealed partial class MapService
{
    public ICloakStore? CloakStore { get; set; }
    private const byte CloakSlot = 4, CloakType = 2, CloakKind = 19;

    private async Task OnCS_CUSTOMCLOAK_LIST_REQ(ClientSession s)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is null) return;
        try
        {
            var rows = CloakStore is { } db ? await db.LoadCustomCloaksAsync(s.UserId) : new List<CustomCloakRow>();
            // Each entry is WORD + INT64; respect the 16-bit packet size even if the account has malformed data.
            var ordered = rows.DistinctBy(x => x.Id).OrderBy(x => x.Id).Take((PacketHeader.MaxPacketSize - 18) / 10).ToList();
            var w = new PacketWriter(Msg.CS_CUSTOMCLOAK_LIST_ACK).WriteUInt16((ushort)ordered.Count);
            foreach (var row in ordered) w.WriteUInt16(row.Id).WriteInt64(row.CreatedAt);
            s.Send(w);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Custom cloak list failed for account {User}.", s.UserId);
            s.Send(new PacketWriter(Msg.CS_CUSTOMCLOAK_LIST_ACK).WriteUInt16(0));
        }
    }

    private async Task OnCS_CUSTOMCLOAK_CHANGE_REQ(ClientSession s, PacketReader r)
    {
        ushort id = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        void Refuse() => s.Send(new PacketWriter(Msg.CS_CUSTOMCLOAK_CHANGE_ACK).WriteUInt32(s.CharId).WriteUInt16(0).WriteByte(0));
        if (s.Store.IsOpen || s.Deal.InProgress || ch.Medals < 4000 || CloakStore is not { } db
            || ch.Equipped?.FindItem(CloakSlot) is not { Count: > 0, Template: { Type: CloakType, Kind: CloakKind } } item)
        { Refuse(); return; }
        try
        {
            FlushItemDirect();
            uint? balance = null;
            await EnqueueDbWrite(async () => balance = await db.ChangeCustomCloakAsync(s.UserId, ch.CharId, item.DlId, id));
            if (balance is null) { Refuse(); return; }
            item.Ext[Item.IevCustomTex] = id;
            ch.Medals = balance.Value;
            s.Send(new PacketWriter(Msg.CS_UPDATEMEDALS_REQ).WriteUInt32(ch.Medals));
            EnqueueItemSave(s, Proto.InvenEquip, item);
            var packet = new PacketWriter(Msg.CS_CUSTOMCLOAK_CHANGE_ACK).WriteUInt32(s.CharId).WriteUInt16(id).WriteByte(1).ToArray();
            s.Send(packet);
            foreach (var other in _state.Neighbors(s)) other.Send(packet);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Custom cloak change failed for char {Char}.", s.CharId); Refuse(); }
    }

    private void OnCS_REGGUILDCLOAK_REQ(ClientSession s, PacketReader r)
    {
        byte inven = r.ReadByte(), slot = r.ReadByte(), register = r.ReadByte();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { GuildId: not 0 } ch
            || s.Store.IsOpen || s.Deal.InProgress || register > 1
            || ch.FindInven(inven)?.FindItem(slot) is not { Count: > 0, Template: { Type: CloakType, Kind: CloakKind } } item) return;
        item.Ext[Item.IevGuild] = register != 0 ? ch.CharId : 0;
        EnqueueItemSave(s, inven, item);
        s.Send(new PacketWriter(Msg.CS_REGGUILDCLOAK_ACK).WriteByte(0).WriteByte(inven).WriteByte(slot).WriteByte(register));
        if (inven == Proto.InvenEquip)
        {
            byte[] appearance = BuildCS_EQUIP_ACK(ch);
            foreach (var other in _state.Neighbors(s)) other.Send(appearance);
        }
    }
}
