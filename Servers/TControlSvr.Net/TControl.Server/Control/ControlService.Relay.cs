using TControl.Protocol;

namespace TControl.Server.Control;

/// <summary>
/// GM-command relays: a manager command is authority-checked, forwarded to the matching game server(s),
/// and each server's reply is routed back to the originating manager (found by the <c>managerId</c> token
/// threaded through the round trip). Ported from <c>Handler.cpp</c>. Target selection is by server-type +
/// group id (<c>bGroupID</c>/<c>bWorld</c>; 0 = all groups). Announcement / char-message use the C++
/// relay-preferred fan, with map/world fallback for groups without a connected relay (including
/// current .NET deployments that have no relay-server topology rows).
/// </summary>
public sealed partial class ControlService
{
    // ---------- announcement / kick / move / position ----------

    /// <summary>C++ OnCT_ANNOUNCEMENT_REQ.</summary>
    private void OnCT_ANNOUNCEMENT_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.GmLevel1)) return;
        uint nId = r.ReadUInt32();
        string announce = r.ReadString();

        foreach (var group in _state.Connected.Where(s => nId == 0 || s.Group.GroupId == nId).GroupBy(s => s.Group.GroupId))
        {
            var relays = group.Where(s => s.IsRelay).ToList();
            foreach (var target in relays.Count > 0 ? relays : group.Where(s => s.IsMap))
                target.Conn!.Send(BuildAnnouncement(announce));
        }
    }

    /// <summary>C++ OnCT_USERKICKOUT_REQ — kick a user from every connected map server.</summary>
    private void OnCT_USERKICKOUT_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.GmLevel2)) return;
        string user = r.ReadString();
        foreach (var map in _state.ConnectedOfType(Proto.SVRGRP_MAPSVR))
            map.Conn!.Send(new PacketWriter(Msg.CT_USERKICKOUT_ACK).WriteString(user).ToArray());
    }

    /// <summary>C++ OnCT_USERMOVE_REQ — teleport listed users; relay to the group's world server.</summary>
    private void OnCT_USERMOVE_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.GmLevel2)) return;
        byte world = r.ReadByte();
        byte channel = r.ReadByte();
        ushort mapId = r.ReadUInt16();
        float px = r.ReadFloat(), py = r.ReadFloat(), pz = r.ReadFloat();
        ushort count = r.ReadUInt16();

        var svc = _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).FirstOrDefault(s => s.Group.GroupId == world);
        for (int i = 0; i < count; i++)
        {
            string user = r.ReadString();
            svc?.Conn!.Send(new PacketWriter(Msg.CT_USERMOVE_ACK)
                .WriteString(user).WriteByte(channel).WriteUInt16(mapId)
                .WriteFloat(px).WriteFloat(py).WriteFloat(pz).ToArray());
        }
    }

    /// <summary>C++ OnCT_USERPOSITION_REQ — move listed users to a target user; relay to the group's world.</summary>
    private void OnCT_USERPOSITION_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.GmLevel2)) return;
        byte world = r.ReadByte();
        string target = r.ReadString();
        ushort count = r.ReadUInt16();

        var svc = _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).FirstOrDefault(s => s.Group.GroupId == world);
        for (int i = 0; i < count; i++)
        {
            string user = r.ReadString();
            // C++ SendCT_USERPOSITION_ACK(strUser=strTargetName, strTarget=strMoveUser) -> << target << user.
            svc?.Conn!.Send(new PacketWriter(Msg.CT_USERPOSITION_ACK).WriteString(target).WriteString(user).ToArray());
        }
    }

    // ---------- monster spawn / action ----------

    /// <summary>C++ OnCT_MONSPAWNFIND_REQ — ask the group's map servers to locate a spawn.</summary>
    private void OnCT_MONSPAWNFIND_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.GmLevel1)) return;
        byte group = r.ReadByte();
        byte channel = r.ReadByte();
        ushort mapId = r.ReadUInt16();
        ushort spawnId = r.ReadUInt16();
        foreach (var map in _state.ConnectedOfType(Proto.SVRGRP_MAPSVR).Where(s => s.Group.GroupId == group))
            map.Conn!.Send(new PacketWriter(Msg.CT_MONSPAWNFIND_ACK)
                .WriteUInt32(mgr.Id).WriteByte(channel).WriteUInt16(mapId).WriteUInt16(spawnId).ToArray());
    }

    /// <summary>C++ OnCT_MONSPAWNFIND_ACK — aggregate a map server's spawn hits, relay to the manager.</summary>
    private void OnCT_MONSPAWNFIND_ACK(PacketReader r)
    {
        uint manager = r.ReadUInt32();
        ushort mapId = r.ReadUInt16();
        ushort spawnId = r.ReadUInt16();
        byte count = r.ReadByte();

        var w = new PacketWriter(Msg.CT_MONSPAWNFIND_ACK).WriteUInt16(mapId).WriteUInt16(spawnId).WriteByte(count);
        for (int i = 0; i < count; i++)
        {
            uint monId = r.ReadUInt32(), hostId = r.ReadUInt32();
            byte stat = r.ReadByte();
            float px = r.ReadFloat(), py = r.ReadFloat(), pz = r.ReadFloat();
            w.WriteUInt32(monId).WriteUInt32(hostId).WriteByte(stat).WriteFloat(px).WriteFloat(py).WriteFloat(pz);
        }
        _state.FindManager(manager)?.Send(w.ToArray());
    }

    /// <summary>C++ OnCT_MONACTION_REQ — send a monster action to the group's map servers.</summary>
    private void OnCT_MONACTION_REQ(ManagerSession mgr, PacketReader r)
    {
        byte group = r.ReadByte();
        byte channel = r.ReadByte();
        ushort mapId = r.ReadUInt16();
        uint monId = r.ReadUInt32();
        byte action = r.ReadByte();
        uint triggerId = r.ReadUInt32(), hostId = r.ReadUInt32(), rhId = r.ReadUInt32();
        byte rhType = r.ReadByte();
        ushort spawnId = r.ReadUInt16();
        foreach (var map in _state.ConnectedOfType(Proto.SVRGRP_MAPSVR).Where(s => s.Group.GroupId == group))
            map.Conn!.Send(new PacketWriter(Msg.CT_MONACTION_ACK)
                .WriteByte(channel).WriteUInt16(mapId).WriteUInt32(monId).WriteByte(action)
                .WriteUInt32(triggerId).WriteUInt32(hostId).WriteUInt32(rhId).WriteByte(rhType).WriteUInt16(spawnId).ToArray());
    }

    // ---------- account ban / char message / chat ban ----------

    /// <summary>C++ OnCT_USERPROTECTED_REQ — account ban via TUserProtectedAdd.</summary>
    private async Task OnCT_USERPROTECTED_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.GmLevel1)) return;
        string userId = r.ReadString();
        uint duration = r.ReadUInt32();
        string reason = r.ReadString();
        byte permanent = r.ReadByte();

        int ret = 0;
        if (_db is not null)
        {
            try { ret = await _db.UserProtectedAddAsync(userId, duration, reason, permanent, mgr.StrId, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "TUserProtectedAdd failed."); }
        }
        mgr.Send(new PacketWriter(Msg.CT_USERPROTECTED_ACK).WriteByte((byte)ret).ToArray());
    }

    /// <summary>C++ OnCT_CHARMSG_REQ — system message to a char (relay-preferred, world fallback per group).</summary>
    private void OnCT_CHARMSG_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.GmLevel2)) return;
        string name = r.ReadString();
        string msg = r.ReadString();

        foreach (var group in _state.Connected.GroupBy(s => s.Group.GroupId))
        {
            var relays = group.Where(s => s.IsRelay).ToList();
            foreach (var target in relays.Count > 0 ? relays : group.Where(s => s.IsWorld))
                target.Conn!.Send(BuildCharMsg(name, msg));
        }
    }

    /// <summary>C++ OnCT_CHATBAN_REQ — chat-ban a char across world/relay servers; track pending acks.</summary>
    private void OnCT_CHATBAN_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.GmLevel3)) return;
        string name = r.ReadString();
        ushort min = r.ReadUInt16();
        string reason = r.ReadString();

        _state.ChatBanSendCount = 0;
        _state.ChatBanSuccess = false;
        _state.ChatBanSeq++;

        bool sent = false;
        foreach (var svc in _state.Services.Values.Where(s => s.Conn is not null && (s.IsWorld || s.IsRelay)))
        {
            svc.Conn!.Send(new PacketWriter(Msg.CT_CHATBAN_REQ)
                .WriteString(name).WriteUInt16(min).WriteUInt32(_state.ChatBanSeq).WriteUInt32(mgr.Id).ToArray());
            sent = true;
            if (svc.IsWorld) _state.ChatBanSendCount++;
        }
        if (!sent) return;

        _state.Bans[_state.ChatBanSeq] = new BanInfo
        {
            OpName = mgr.StrId, BanName = name, Min = min, Reason = reason,
            Id = _state.ChatBanSeq, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    /// <summary>C++ OnCT_CHATBAN_ACK — collect world acks; when all in, notify the manager.</summary>
    private void OnCT_CHATBAN_ACK(PacketReader r)
    {
        byte ret = r.ReadByte();
        uint banSeq = r.ReadUInt32();
        uint managerId = r.ReadUInt32();

        if (_state.ChatBanSendCount > 0) _state.ChatBanSendCount--;
        if (ret != 0) _state.ChatBanSuccess = true;
        if (_state.ChatBanSendCount != 0) return;

        if (!_state.Bans.TryGetValue(banSeq, out _)) _state.ChatBanSuccess = false;
        else if (!_state.ChatBanSuccess) _state.Bans.Remove(banSeq);

        if (_state.FindManager(managerId) is { } mgr)
            mgr.Send(new PacketWriter(Msg.CT_CHATBAN_ACK).WriteByte(_state.ChatBanSuccess ? (byte)1 : (byte)0).ToArray());
    }

    // ---------- item find / state ----------

    /// <summary>C++ OnCT_ITEMFIND_REQ — relay an item search to the group's world server.</summary>
    private void OnCT_ITEMFIND_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        ushort itemId = r.ReadUInt16();
        string name = r.ReadString();
        byte worldId = r.ReadByte();
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => s.Group.GroupId == worldId))
            svc.Conn!.Send(new PacketWriter(Msg.CT_ITEMFIND_REQ).WriteUInt32(mgr.Id).WriteUInt16(itemId).WriteString(name).ToArray());
    }

    /// <summary>C++ OnCT_ITEMFIND_ACK — relay world item-find results to the manager (drops the id token).</summary>
    private void OnCT_ITEMFIND_ACK(PacketReader r)
    {
        ushort count = r.ReadUInt16();
        uint managerId = r.ReadUInt32();
        var w = new PacketWriter(Msg.CT_ITEMFIND_ACK).WriteUInt16(count);
        for (int i = 0; i < count; i++)
        {
            ushort itemId = r.ReadUInt16();
            byte initState = r.ReadByte();
            string name = r.ReadString();
            w.WriteUInt16(itemId).WriteByte(initState).WriteString(name);
        }
        _state.FindManager(managerId)?.Send(w.ToArray());
    }

    /// <summary>C++ OnCT_ITEMSTATE_REQ — relay item-state changes to the group's world server.</summary>
    private void OnCT_ITEMSTATE_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte worldId = r.ReadByte();
        uint managerId = r.ReadUInt32();
        ushort count = r.ReadUInt16();
        var body = new PacketWriter(Msg.CT_ITEMSTATE_REQ).WriteUInt32(managerId).WriteUInt16(count);
        for (int i = 0; i < count; i++) { body.WriteUInt16(r.ReadUInt16()); body.WriteByte(r.ReadByte()); }
        byte[] pkt = body.ToArray();
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => s.Group.GroupId == worldId))
            svc.Conn!.Send(pkt);
    }

    /// <summary>C++ OnCT_ITEMSTATE_ACK — relay applied item-states to the manager (whole body, incl. id token).</summary>
    private void OnCT_ITEMSTATE_ACK(PacketReader r, byte[] packet)
    {
        uint id = r.ReadUInt32();
        _state.FindManager(id)?.Send(Reframe(Msg.CT_ITEMSTATE_ACK, packet));
    }

    // ---------- castle ----------

    /// <summary>C++ OnCT_CASTLEINFO_REQ — request castle info from the group's map server.</summary>
    private void OnCT_CASTLEINFO_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte world = r.ReadByte();
        var svc = _state.ConnectedOfType(Proto.SVRGRP_MAPSVR).FirstOrDefault(s => s.Group.GroupId == world);
        svc?.Conn!.Send(new PacketWriter(Msg.CT_CASTLEINFO_REQ).WriteUInt32(mgr.Id).ToArray());
    }

    /// <summary>C++ OnCT_CASTLEINFO_ACK.</summary>
    private void OnCT_CASTLEINFO_ACK(PacketReader r, byte[] packet)
    {
        uint managerId = r.ReadUInt32();
        _state.FindManager(managerId)?.Send(Reframe(Msg.CT_CASTLEINFO_ACK, packet));
    }

    /// <summary>C++ OnCT_CASTLEGUILDCHG_REQ — force a castle's def/atk guilds on the group's world server.</summary>
    private void OnCT_CASTLEGUILDCHG_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte world = r.ReadByte();
        ushort castle = r.ReadUInt16();
        uint def = r.ReadUInt32(), atk = r.ReadUInt32();
        long time = r.ReadInt64();
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => s.Group.GroupId == world))
            svc.Conn!.Send(new PacketWriter(Msg.CT_CASTLEGUILDCHG_REQ)
                .WriteUInt16(castle).WriteUInt32(def).WriteUInt32(atk).WriteUInt32(mgr.Id).WriteInt64(time).ToArray());
    }

    /// <summary>C++ OnCT_CASTLEGUILDCHG_ACK.</summary>
    private void OnCT_CASTLEGUILDCHG_ACK(PacketReader r, byte[] packet)
    {
        uint managerId = r.ReadUInt32();
        _state.FindManager(managerId)?.Send(Reframe(Msg.CT_CASTLEGUILDCHG_ACK, packet));
    }

    /// <summary>C++ OnCT_CASTLEENABLE_REQ — enable a castle siege via SM_BATTLESTATUS_REQ on the group's world.</summary>
    private void OnCT_CASTLEENABLE_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte world = r.ReadByte();
        byte status = r.ReadByte();
        uint second = r.ReadUInt32();
        const byte BT_CASTLE = 1;
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => s.Group.GroupId == world))
            svc.Conn!.Send(new PacketWriter(Msg.SM_BATTLESTATUS_REQ)
                .WriteByte(BT_CASTLE).WriteByte(status).WriteUInt32(0).WriteUInt32(second).ToArray());
    }

    // ---------- help / RPS / tournament-event / cash-mall gift (group-targeted body pass-through) ----------

    /// <summary>C++ OnCT_HELPMESSAGE_REQ — forward to the group's world server(s), group byte stripped
    /// (C++ CopyData(pkt, sizeof(BYTE))).</summary>
    private void OnCT_HELPMESSAGE_REQ(PacketReader r, byte[] packet)
    {
        byte group = r.ReadByte();
        SendToWorlds(group, ReframeSkip(Msg.CT_HELPMESSAGE_REQ, packet, 1));
    }

    /// <summary>C++ OnCT_RPSGAMEDATA_REQ — read RPS config; the world re-reads the group byte, so the whole
    /// body is forwarded (C++ Copy).</summary>
    private void OnCT_RPSGAMEDATA_REQ(PacketReader r, byte[] packet)
    {
        byte group = r.ReadByte();
        SendToWorlds(group, Reframe(Msg.CT_RPSGAMEDATA_REQ, packet));
    }

    /// <summary>C++ OnCT_RPSGAMECHANGE_REQ — change RPS config; whole body forwarded (C++ Copy).</summary>
    private void OnCT_RPSGAMECHANGE_REQ(PacketReader r, byte[] packet)
    {
        byte group = r.ReadByte();
        SendToWorlds(group, Reframe(Msg.CT_RPSGAMECHANGE_REQ, packet));
    }

    /// <summary>C++ OnCT_RPSGAMEDATA_ACK — broadcast the world's RPS config to all managers.</summary>
    private void OnCT_RPSGAMEDATA_ACK(byte[] packet) => BroadcastToManagers(Reframe(Msg.CT_RPSGAMEDATA_ACK, packet));

    /// <summary>C++ OnCT_TOURNAMENTEVENT_REQ — forward to the group's world server (group byte stripped).</summary>
    private void OnCT_TOURNAMENTEVENT_REQ(PacketReader r, byte[] packet)
    {
        byte group = r.ReadByte();
        byte[] fwd = ReframeSkip(Msg.CT_TOURNAMENTEVENT_REQ, packet, 1);
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => s.Group.GroupId == group))
            svc.Conn!.Send(fwd);
    }

    /// <summary>C++ OnCT_TOURNAMENTEVENT_ACK.</summary>
    private void OnCT_TOURNAMENTEVENT_ACK(PacketReader r, byte[] packet)
    {
        uint managerId = r.ReadUInt32();
        _state.FindManager(managerId)?.Send(Reframe(Msg.CT_TOURNAMENTEVENT_ACK, packet));
    }

    /// <summary>C++ OnCT_EVENTQUARTERLIST_REQ — forward to the group's world server (manager-id gated,
    /// group byte stripped).</summary>
    private void OnCT_EVENTQUARTERLIST_REQ(ManagerSession mgr, PacketReader r, byte[] packet)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte group = r.ReadByte();
        uint managerId = r.ReadUInt32();
        if (mgr.Id != managerId) return;
        byte[] fwd = ReframeSkip(Msg.CT_EVENTQUARTERLIST_REQ, packet, 1);
        var svc = _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).FirstOrDefault(s => s.Group.GroupId == group);
        svc?.Conn!.Send(fwd);
    }

    /// <summary>C++ OnCT_EVENTQUARTERLIST_ACK.</summary>
    private void OnCT_EVENTQUARTERLIST_ACK(PacketReader r, byte[] packet)
    {
        uint managerId = r.ReadUInt32();
        _state.FindManager(managerId)?.Send(Reframe(Msg.CT_EVENTQUARTERLIST_ACK, packet));
    }

    /// <summary>C++ OnCT_EVENTQUARTERUPDATE_REQ — authority-gated + manager-id gated; forward to ALL the
    /// group's connected world servers (group byte stripped).</summary>
    private void OnCT_EVENTQUARTERUPDATE_REQ(ManagerSession mgr, PacketReader r, byte[] packet)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte group = r.ReadByte();
        uint managerId = r.ReadUInt32();
        if (mgr.Id != managerId) return;
        byte[] fwd = ReframeSkip(Msg.CT_EVENTQUARTERUPDATE_REQ, packet, 1);
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => s.Group.GroupId == group))
            svc.Conn!.Send(fwd);
    }

    /// <summary>C++ OnCT_EVENTQUARTERUPDATE_ACK — world sends [bRet:BYTE][dwManagerID:DWORD]…; read the
    /// leading bRet before the id, then forward the whole body to the manager.</summary>
    private void OnCT_EVENTQUARTERUPDATE_ACK(PacketReader r, byte[] packet)
    {
        _ = r.ReadByte(); // bRet
        uint managerId = r.ReadUInt32();
        _state.FindManager(managerId)?.Send(Reframe(Msg.CT_EVENTQUARTERUPDATE_ACK, packet));
    }

    /// <summary>C++ OnCT_CMGIFT_REQ — cash-mall gift to a char on the group's world server(s).</summary>
    private void OnCT_CMGIFT_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte group = r.ReadByte();
        string target = r.ReadString();
        ushort giftId = r.ReadUInt16();
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => group == 0 || s.Group.GroupId == group))
            svc.Conn!.Send(new PacketWriter(Msg.CT_CMGIFT_REQ).WriteString(target).WriteUInt16(giftId).WriteUInt32(mgr.Id).ToArray());
    }

    /// <summary>C++ OnCT_CMGIFT_ACK.</summary>
    private void OnCT_CMGIFT_ACK(PacketReader r)
    {
        byte ret = r.ReadByte();
        uint managerId = r.ReadUInt32();
        _state.FindManager(managerId)?.Send(new PacketWriter(Msg.CT_CMGIFT_ACK).WriteByte(ret).ToArray());
    }

    /// <summary>C++ OnCT_CMGIFTLIST_REQ — request the gift catalog from the group's world server(s).</summary>
    private void OnCT_CMGIFTLIST_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte group = r.ReadByte();
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => group == 0 || s.Group.GroupId == group))
            svc.Conn!.Send(new PacketWriter(Msg.CT_CMGIFTLIST_REQ).WriteUInt32(mgr.Id).ToArray());
    }

    /// <summary>C++ OnCT_CMGIFTLIST_ACK — relay the world's gift catalog (dropping the id token) to the manager.</summary>
    private void OnCT_CMGIFTLIST_ACK(PacketReader r)
    {
        uint managerId = r.ReadUInt32();
        _state.FindManager(managerId)?.Send(ReframeFrom(r, Msg.CT_CMGIFTLIST_ACK));
    }

    /// <summary>C++ OnCT_CMGIFTCHARTUPDATE_REQ — forward gift-catalog edits to the group's world server(s):
    /// the group byte is stripped and the manager id is appended (C++ CopyData(pkt, sizeof(BYTE)) &lt;&lt; dwManagerID).</summary>
    private void OnCT_CMGIFTCHARTUPDATE_REQ(ManagerSession mgr, PacketReader r, byte[] packet)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        byte group = r.ReadByte();
        var w = new PacketWriter(Msg.CT_CMGIFTCHARTUPDATE_REQ);
        w.WriteRaw(packet.AsSpan(PacketHeader.Size + 1)); // body after the group byte
        w.WriteUInt32(mgr.Id);
        byte[] pkt = w.ToArray();
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => group == 0 || s.Group.GroupId == group))
            svc.Conn!.Send(pkt);
    }

    // ---------- helpers ----------

    private static byte[] BuildAnnouncement(string s) => new PacketWriter(Msg.CT_ANNOUNCEMENT_ACK).WriteString(s).ToArray();
    private static byte[] BuildCharMsg(string name, string msg) => new PacketWriter(Msg.CT_CHARMSG_ACK).WriteString(name).WriteString(msg).ToArray();

    private void SendToWorlds(byte group, byte[] packet)
    {
        foreach (var svc in _state.ConnectedOfType(Proto.SVRGRP_WORLDSVR).Where(s => group == 0 || s.Group.GroupId == group))
            svc.Conn!.Send(packet);
    }

    /// <summary>Forward the reader's remaining bytes under <paramref name="id"/> (the id token already
    /// consumed) — the C++ CT_CMGIFTLIST_ACK path (CopyData(pkt, sizeof(DWORD)) drops the manager-id token).</summary>
    private static byte[] ReframeFrom(PacketReader r, ushort id)
    {
        var w = new PacketWriter(id);
        w.WriteRaw(r.ReadRemaining());
        return w.ToArray();
    }
}
