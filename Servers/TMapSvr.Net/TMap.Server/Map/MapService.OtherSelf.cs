using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    /// <summary>The client's missing-owner request after a template-zero ADDRECALLMON (client
    /// CSHandler.cpp:7524, 7848; native server CSHandler.cpp:12112). The summon, rather than its
    /// owner, must be in view: the owner may legitimately be several cells away. Do not expose
    /// arbitrary player inventories through guessed host/monster IDs.</summary>
    private void OnCS_OTHERSELF_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.State != EnterState.InGame || s.Char is null) return;
        uint ownerId = r.ReadUInt32(), monId = r.ReadUInt32();
        if (_state.FindByChar(ownerId) is not { State: EnterState.InGame, Char: { } owner }
            || !owner.Recalls.TryGetValue(monId, out var mon)
            || mon.OwnerId != ownerId || mon.ObjType != RecallMon.OtRecall || mon.ChartId != 0
            || !mon.InMap || mon.DeleteAsked || mon.Expired(NowMs)
            || !ReferenceEquals(_state.FindRecall(RecallMon.OtRecall, monId), mon)
            || !_state.RecallsInView(s).Contains(mon)) return;

        var w = new PacketWriter(Msg.CS_OTHERSELF_ACK);
        w.WriteUInt32(monId); w.WriteUInt32(ownerId); w.WriteString(owner.Name);
        w.WriteUInt32(owner.GuildId); w.WriteUInt32(owner.Fame); w.WriteUInt32(owner.FameColor);
        w.WriteString(owner.GuildName); w.WriteByte(owner.GuildPeer);
        w.WriteUInt32(owner.TacticsId); w.WriteString(owner.TacticsName);
        w.WriteByte(owner.Class); w.WriteByte(owner.Race); w.WriteByte(owner.Country); w.WriteByte(owner.AidCountry);
        w.WriteByte(owner.Sex); w.WriteByte(owner.Hair); w.WriteByte(owner.Face); w.WriteByte(owner.Body);
        w.WriteByte(owner.Pants); w.WriteByte(owner.Hand); w.WriteByte(owner.Foot);
        w.WriteByte(owner.HelmetHide); w.WriteByte(owner.Level);
        w.WriteUInt32(owner.PartyChiefId); w.WriteUInt16(owner.PartyId); w.WriteUInt16((ushort)owner.CommanderId);
        var equip = owner.Equipped;
        w.WriteByte((byte)(equip?.Items.Count ?? 0));
        if (equip is not null)
            foreach (var item in equip.Items.OrderBy(i => i.ItemSlot)) item.WrapPacketClient(w, ownerId);
        s.Send(w);
        SendCS_UPDATESPAWNEDCOMPANION_REQ(s, s.Char.CompanionSlot);
    }
}
