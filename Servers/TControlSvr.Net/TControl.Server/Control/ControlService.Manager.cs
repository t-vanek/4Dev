using TControl.Protocol;

namespace TControl.Server.Control;

/// <summary>
/// Operator login / authority, topology lists, and service monitoring/control — the manager-facing
/// admin surface (C++ <c>OnCT_OPLOGIN_REQ</c>, <c>OnCT_SERVICESTAT_REQ</c>, <c>OnCT_SERVICECONTROL_REQ</c>,
/// <c>OnCT_SERVICEMONITOR_REQ</c>, …) plus the ACK builders from <c>Sender.cpp</c>.
/// </summary>
public sealed partial class ControlService
{
    /// <summary>Operator login: authenticate via TOPLogin, register the manager, and burst the topology
    /// lists + auto-start flag. C++ OnCT_OPLOGIN_REQ.</summary>
    private async Task OnCT_OPLOGIN_REQ(ManagerSession mgr, PacketReader r)
    {
        string id = r.ReadString();
        string pw = r.ReadString();

        int authority = await OpLoginAuthorityAsync(id, pw);
        if (authority == 0)
        {
            mgr.Send(BuildOpLoginAck(1, 0, 0));
            return;
        }

        if (_state.FindManagerByName(id) is { } already)
        {
            // C++: duplicate operator -> close the already-logged-in session and reject this one.
            already.Conn.Close();
            mgr.Conn.Close();
            return;
        }

        _state.ManagerSeq++;
        mgr.IsManager = true;
        mgr.Authority = (byte)authority;
        mgr.Id = _state.ManagerSeq;
        mgr.StrId = id;
        _state.Managers.Add(mgr);

        mgr.Send(BuildOpLoginAck(0, mgr.Authority, mgr.Id));
        SendManagerTopology(mgr);
        _log.LogInformation("Operator '{Id}' logged in (authority {Auth}).", id, authority);
    }

    private void SendManagerTopology(ManagerSession mgr)
    {
        mgr.Send(BuildGroupList());
        mgr.Send(BuildMachineList());
        mgr.Send(BuildSvrTypeList());
        mgr.Send(new PacketWriter(Msg.CT_SERVICEAUTOSTART_ACK).WriteByte(_state.AutoStart ? (byte)1 : (byte)0).ToArray());
    }

    /// <summary>Status-tool login (read-only). C++ OnCT_STLOGIN_REQ.</summary>
    private async Task OnCT_STLOGIN_REQ(ManagerSession mgr, PacketReader r)
    {
        string id = r.ReadString();
        string pw = r.ReadString();
        int authority = await OpLoginAuthorityAsync(id, pw);
        if (authority == 0)
        {
            mgr.Send(new PacketWriter(Msg.CT_STLOGIN_ACK).WriteByte(1).WriteByte(0).ToArray());
            return;
        }
        mgr.IsManager = true;
        mgr.ReadOnly = true;
        mgr.Authority = (byte)authority;
        mgr.StrId = id;
        mgr.Id = ++_state.ManagerSeq;
        _state.Managers.Add(mgr);
        mgr.Send(new PacketWriter(Msg.CT_STLOGIN_ACK).WriteByte(0).WriteByte(mgr.Authority).ToArray());
        SendManagerTopology(mgr);
    }

    private async Task<int> OpLoginAuthorityAsync(string id, string pw)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 50 || pw.Length is 0 or > 50) return 0;
        try
        {
            int authority = await _authenticator.AuthenticateAsync(id, pw, CancellationToken.None);
            return authority is >= (int)ManagerClass.All and <= (int)ManagerClass.GmLevel3 ? authority : 0;
        }
        catch (Exception ex) { _log.LogWarning(ex, "TOPLogin failed."); return 0; }
    }

    /// <summary>Send the full service-status list. C++ OnCT_SERVICESTAT_REQ.</summary>
    private void OnCT_SERVICESTAT_REQ(ManagerSession mgr) => mgr.Send(BuildServiceStat());

    /// <summary>Start/stop a configured local process; world-stop cascades manager-control
    /// off for the group. C++ OnCT_SERVICECONTROL_REQ.</summary>
    private void OnCT_SERVICECONTROL_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.Service)) return;

        uint serviceId = r.ReadUInt32();
        byte start = r.ReadByte();
        if (_state.FindService(serviceId) is not { } svc) return;
        if (_pendingStops.ContainsKey(svc.Id)) { SendControlResult(mgr, false); return; }

        // C++ inits bRet=0; only the default (non STOPPED/RUNNING) case sets ACK_FAILED(1).
        byte ret = 0;
        uint status = _svcCtl.GetStatus(svc) ?? svc.Status;
        switch (status)
        {
            case Proto.SvcStopped:
                if (start != 0 && _svcCtl.Start(svc))
                {
                    SetRestartIntent(svc, true);
                    SetServiceStatus(svc, Proto.SvcRunning, 0);
                    ret = 1;
                    _log.LogInformation("Start service {Name}", svc.Name);
                }
                break;
            case Proto.SvcRunning:
                if (start == 0)
                {
                    SetServiceStatus(svc, Proto.SvcStopPending, 0);
                    BeginStop(svc, mgr);
                    return;
                }
                else if (!svc.ManagerControl) { SetRestartIntent(svc, true); ret = 1; }
                break;
            default:
                ret = Proto.AckFailed;
                break;
        }
        mgr.Send(new PacketWriter(Msg.CT_SERVICECONTROL_ACK).WriteByte(ret).ToArray());
    }

    /// <summary>Toggle auto-start; broadcast the new state to managers. C++ OnCT_SERVICEAUTOSTART_REQ.</summary>
    private void OnCT_SERVICEAUTOSTART_REQ(PacketReader r)
    {
        _state.AutoStart = r.ReadByte() != 0;
        BroadcastToManagers(new PacketWriter(Msg.CT_SERVICEAUTOSTART_ACK).WriteByte(_state.AutoStart ? (byte)1 : (byte)0).ToArray());
    }

    /// <summary>Reset per-service stats and notify connected servers. C++ OnCT_SERVICEDATACLEAR_REQ.</summary>
    private void OnCT_SERVICEDATACLEAR_REQ()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var svc in _state.Services.Values)
        {
            svc.MaxUser = 0;
            svc.StopCount = 0;
            svc.LatestStop = 0;
            svc.PickTime = 0;
            svc.ActiveUser = 0;
            if (svc.Conn is { } conn)
            {
                svc.LatestStop = now;
                svc.PickTime = now;
                conn.Send(new PacketWriter(Msg.CT_SERVICEDATACLEAR_ACK).ToArray());
            }
        }
    }

    /// <summary>Force the connector to re-dial a service by dropping any live connection. C++ OnCT_RECONNECT_REQ
    /// re-queued a CT_NEWCONNECT_REQ; here the connector loop re-dials disconnected services automatically.</summary>
    private void OnCT_RECONNECT_REQ(PacketReader r)
    {
        uint id = r.ReadUInt32();
        if (_state.FindService(id) is { Conn: { } conn }) conn.Conn.Close();
    }

    /// <summary>Game server reports live counts; update peak + push per-service data to managers.
    /// C++ OnCT_SERVICEMONITOR_REQ (received on the id CTProtocol calls CT_SERVICEMONITOR_REQ = 0x1D).</summary>
    private void OnCT_SERVICEMONITOR_REQ(ServerSession srv, PacketReader r)
    {
        uint tick = r.ReadUInt32();
        uint session = r.ReadUInt32();
        uint user = r.ReadUInt32();
        uint activeUser = r.ReadUInt32();

        if (srv.Service is not { } svc) return;

        long now = Environment.TickCount64;
        svc.RecvTick = now;
        if (svc.MaxUser <= user)
        {
            svc.MaxUser = user;
            svc.PickTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        svc.ActiveUser = activeUser;

        uint ping = (uint)Math.Max(0, now - tick);
        foreach (var m in _state.Managers)
            m.Send(BuildServiceData(svc, session, user, ping, activeUser));
    }

    /// <summary>Send the in-memory chat-ban list. C++ OnCT_CHATBANLIST_REQ.</summary>
    private void OnCT_CHATBANLIST_REQ(ManagerSession mgr)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        var w = new PacketWriter(Msg.CT_CHATBANLIST_ACK);
        w.WriteUInt16((ushort)_state.Bans.Count);
        foreach (var b in _state.Bans.Values)
        {
            w.WriteUInt32(b.Id); w.WriteString(b.BanName); w.WriteInt64(b.Time);
            w.WriteUInt16(b.Min); w.WriteString(b.Reason); w.WriteString(b.OpName);
        }
        mgr.Send(w.ToArray());
    }

    /// <summary>Delete one (or all, id=0) chat-ban entries. C++ OnCT_CHATBANLISTDEL_REQ.</summary>
    private void OnCT_CHATBANLISTDEL_REQ(ManagerSession mgr, PacketReader r)
    {
        if (!CheckAuthority(mgr, ManagerClass.All)) return;
        uint id = r.ReadUInt32();
        if (id == 0) _state.Bans.Clear();
        else _state.Bans.Remove(id);
    }

    // ===== builders (Sender.cpp) =====

    private static byte[] BuildOpLoginAck(byte ret, byte authority, uint id) =>
        new PacketWriter(Msg.CT_OPLOGIN_ACK).WriteByte(ret).WriteByte(authority).WriteUInt32(id).ToArray();

    private byte[] BuildGroupList()
    {
        var w = new PacketWriter(Msg.CT_GROUPLIST_ACK).WriteUInt32((uint)_state.Groups.Count);
        foreach (var g in _state.Groups.Values) { w.WriteByte(g.GroupId); w.WriteString(g.Name); }
        return w.ToArray();
    }

    private byte[] BuildMachineList()
    {
        var w = new PacketWriter(Msg.CT_MACHINELIST_ACK).WriteUInt32((uint)_state.Machines.Count);
        foreach (var m in _state.Machines.Values) { w.WriteByte(m.MachineId); w.WriteString(m.Name); }
        return w.ToArray();
    }

    private byte[] BuildSvrTypeList()
    {
        var w = new PacketWriter(Msg.CT_SVRTYPELIST_ACK).WriteUInt32((uint)_state.SvrTypes.Count);
        foreach (var t in _state.SvrTypes.Values) { w.WriteByte(t.Type); w.WriteString(t.Name); }
        return w.ToArray();
    }

    private byte[] BuildServiceStat()
    {
        var w = new PacketWriter(Msg.CT_SERVICESTAT_ACK).WriteUInt32((uint)_state.Services.Count);
        foreach (var s in _state.Services.Values)
        {
            w.WriteByte(s.Group.GroupId); w.WriteByte(s.SvrType.Type); w.WriteByte(s.ServerId);
            w.WriteString(s.Name); w.WriteByte(s.Machine.MachineId); w.WriteUInt32(s.Status);
        }
        return w.ToArray();
    }

    /// <summary>CT_SERVICEDATA_ACK — per Sender.cpp field order.</summary>
    private static byte[] BuildServiceData(ServiceInstance svc, uint session, uint curUser, uint ping, uint activeUser)
    {
        var w = new PacketWriter(Msg.CT_SERVICEDATA_ACK);
        w.WriteUInt32(svc.Id);
        w.WriteUInt32(session);
        w.WriteUInt32(curUser);
        w.WriteUInt32(svc.MaxUser);
        w.WriteUInt32(ping);
        w.WriteInt64(svc.PickTime);
        w.WriteUInt32(svc.StopCount);
        w.WriteInt64(svc.LatestStop);
        w.WriteUInt32(activeUser);
        return w.ToArray();
    }
}
