using Microsoft.Extensions.Logging;
using TControl.Data;
using TControl.Protocol;
using TControl.Server.Net;
using TControl.Server.Ops;

namespace TControl.Server.Control;

/// <summary>
/// The control server's packet handling — the ported C++ <c>CTControlSvrModule</c> <c>On*</c> dispatch.
/// Split across partial files by concern: <c>.Manager.cs</c> (operator login / topology / service control),
/// <c>.Relay.cs</c> (GM-command relays to game servers + ACK routing back to managers), <c>.Event.cs</c>
/// (the scheduled-event engine), and <c>.Patch.cs</c> (patch/preversion + file upload).
///
/// Everything runs on the single batch task, so state is mutated without locks. Managers connect inbound;
/// game servers are connected outbound by the connector, which calls <see cref="OnServerConnected"/> /
/// <see cref="OnServerDisconnected"/> (both marshalled onto the batch task by <c>ControlWorker</c>).
/// </summary>
public sealed partial class ControlService
{
    private readonly ControlState _state;
    private readonly ControlDatabase? _db;
    private readonly ILogger<ControlService> _log;
    private readonly IServiceController _svcCtl;
    private readonly IPlatformMonitor _platform;
    private readonly IFileDeploy _fileDeploy;
    private readonly IOperatorAuthenticator _authenticator;
    private readonly ControlServerOptions _options;
    private readonly CancellationTokenSource _operationCancellation;
    private readonly Action<ProcessStopResult>? _stopCompletion;
    private ManagerSession? _uploadOwner;

    public ControlService(
        ControlState state, ControlDatabase? db, ILogger<ControlService> log,
        IServiceController svcCtl, IPlatformMonitor platform, IFileDeploy fileDeploy,
        IOperatorAuthenticator? authenticator = null, ControlServerOptions? options = null,
        CancellationToken stoppingToken = default, Action<ProcessStopResult>? stopCompletion = null)
    {
        _state = state;
        _db = db;
        _log = log;
        _svcCtl = svcCtl;
        _platform = platform;
        _fileDeploy = fileDeploy;
        _authenticator = authenticator ?? new DatabaseOperatorAuthenticator(db);
        _options = options ?? new ControlServerOptions();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _stopCompletion = stopCompletion;
    }

    public ControlState State => _state;

    // ===== connection lifecycle (all invoked on the batch task) =====

    public void OnManagerConnected(PacketConnection conn)
    {
        var mgr = new ManagerSession(conn);
        conn.State = mgr;
        _state.Sessions.Add(mgr);
    }

    public void OnManagerDisconnected(PacketConnection conn)
    {
        if (conn.State is not ManagerSession mgr) return;
        if (_uploadOwner == mgr) CancelUpload(mgr);
        _state.Sessions.Remove(mgr);
        _state.Managers.Remove(mgr);
    }

    public void OnServerConnected(PacketConnection conn, ServiceInstance svc)
    {
        var srv = new ServerSession(conn) { Service = svc };
        conn.State = srv;
        svc.Conn = srv;
        svc.RecvTick = Environment.TickCount64;
        // Transition to RUNNING and notify managers (mirrors QueryStatus detecting a RUNNING service).
        SetServiceStatus(svc, Proto.SvcRunning, smsType: 0);
        // C++ SendCT_CTRLSVR_REQ then SendEventToNewConnect.
        srv.Send(new PacketWriter(Msg.CT_CTRLSVR_REQ).ToArray());
        SendEventToNewConnect(svc);
        _log.LogInformation("Registered with game server {Name} ({Id:X6}).", svc.Name, svc.Id);
    }

    public void OnServerDisconnected(PacketConnection conn, ServiceInstance svc)
    {
        if (svc.Conn?.Conn != conn) return;
        svc.Conn = null;
        svc.RecvTick = 0;
        if (_svcCtl.GetStatus(svc) is not { } status || status == Proto.SvcStopped)
            SetServiceStatus(svc, Proto.SvcStopped, smsType: 0);
    }

    // ===== dispatch =====

    public async Task DispatchAsync(PacketConnection conn, byte[] packet)
    {
        DrainStopResults();
        var r = new PacketReader(packet);
        try
        {
            if (conn.State is ManagerSession mgr)
                await DispatchManagerAsync(mgr, r, packet);
            else if (conn.State is ServerSession srv)
                DispatchServer(srv, r, packet);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Handler for msg 0x{Id:X4} failed.", r.Id);
        }
    }

    /// <summary>Packets from an inbound manager (GM tool).</summary>
    private async Task DispatchManagerAsync(ManagerSession mgr, PacketReader r, byte[] packet)
    {
        if (r.Id is Msg.CT_OPLOGIN_REQ or Msg.CT_STLOGIN_REQ)
        {
            if (mgr.IsManager) { mgr.Send(new PacketWriter(Msg.CT_AUTHORITY_ACK)); return; }
        }
        else if (!AuthorizeMessage(mgr, r.Id)) return;

        switch (r.Id)
        {
            // --- login / topology / service control (.Manager.cs) ---
            case Msg.CT_OPLOGIN_REQ: await OnCT_OPLOGIN_REQ(mgr, r); break;
            case Msg.CT_STLOGIN_REQ: await OnCT_STLOGIN_REQ(mgr, r); break;
            case Msg.CT_SERVICESTAT_REQ: OnCT_SERVICESTAT_REQ(mgr); break;
            case Msg.CT_SERVICECONTROL_REQ: OnCT_SERVICECONTROL_REQ(mgr, r); break;
            case Msg.CT_SERVICEAUTOSTART_REQ: OnCT_SERVICEAUTOSTART_REQ(r); break;
            case Msg.CT_SERVICEDATACLEAR_REQ: OnCT_SERVICEDATACLEAR_REQ(); break;
            case Msg.CT_RECONNECT_REQ: OnCT_RECONNECT_REQ(r); break;
            case Msg.CT_CHATBANLIST_REQ: OnCT_CHATBANLIST_REQ(mgr); break;
            case Msg.CT_CHATBANLISTDEL_REQ: OnCT_CHATBANLISTDEL_REQ(mgr, r); break;

            // --- patch / upload (.Patch.cs) ---
            case Msg.CT_SERVICEUPLOADSTART_REQ: OnCT_SERVICEUPLOADSTART_REQ(mgr, r); break;
            case Msg.CT_SERVICEUPLOAD_REQ: OnCT_SERVICEUPLOAD_REQ(mgr, r); break;
            case Msg.CT_SERVICEUPLOADEND_REQ: OnCT_SERVICEUPLOADEND_REQ(mgr, r); break;
            case Msg.CT_UPDATEPATCH_REQ: await OnCT_UPDATEPATCH_REQ(mgr, r); break;
            case Msg.CT_PREVERSIONTABLE_REQ: await OnCT_PREVERSIONTABLE_REQ(mgr); break;
            case Msg.CT_PREVERSIONUPDATE_REQ: await OnCT_PREVERSIONUPDATE_REQ(mgr, r); break;

            // --- GM-command relays (.Relay.cs) ---
            case Msg.CT_ANNOUNCEMENT_REQ: OnCT_ANNOUNCEMENT_REQ(mgr, r); break;
            case Msg.CT_USERKICKOUT_REQ: OnCT_USERKICKOUT_REQ(mgr, r); break;
            case Msg.CT_USERMOVE_REQ: OnCT_USERMOVE_REQ(mgr, r); break;
            case Msg.CT_USERPOSITION_REQ: OnCT_USERPOSITION_REQ(mgr, r); break;
            case Msg.CT_MONSPAWNFIND_REQ: OnCT_MONSPAWNFIND_REQ(mgr, r); break;
            case Msg.CT_MONACTION_REQ: OnCT_MONACTION_REQ(mgr, r); break;
            case Msg.CT_USERPROTECTED_REQ: await OnCT_USERPROTECTED_REQ(mgr, r); break;
            case Msg.CT_CHARMSG_REQ: OnCT_CHARMSG_REQ(mgr, r); break;
            case Msg.CT_CHATBAN_REQ: OnCT_CHATBAN_REQ(mgr, r); break;
            case Msg.CT_ITEMFIND_REQ: OnCT_ITEMFIND_REQ(mgr, r); break;
            case Msg.CT_ITEMSTATE_REQ: OnCT_ITEMSTATE_REQ(mgr, r); break;
            case Msg.CT_CASTLEINFO_REQ: OnCT_CASTLEINFO_REQ(mgr, r); break;
            case Msg.CT_CASTLEGUILDCHG_REQ: OnCT_CASTLEGUILDCHG_REQ(mgr, r); break;
            case Msg.CT_CASTLEENABLE_REQ: OnCT_CASTLEENABLE_REQ(mgr, r); break;
            case Msg.CT_HELPMESSAGE_REQ: OnCT_HELPMESSAGE_REQ(r, packet); break;
            case Msg.CT_RPSGAMEDATA_REQ: OnCT_RPSGAMEDATA_REQ(r, packet); break;
            case Msg.CT_RPSGAMECHANGE_REQ: OnCT_RPSGAMECHANGE_REQ(r, packet); break;
            case Msg.CT_TOURNAMENTEVENT_REQ: OnCT_TOURNAMENTEVENT_REQ(r, packet); break;
            case Msg.CT_EVENTQUARTERLIST_REQ: OnCT_EVENTQUARTERLIST_REQ(mgr, r, packet); break;
            case Msg.CT_EVENTQUARTERUPDATE_REQ: OnCT_EVENTQUARTERUPDATE_REQ(mgr, r, packet); break;
            case Msg.CT_CMGIFT_REQ: OnCT_CMGIFT_REQ(mgr, r); break;
            case Msg.CT_CMGIFTLIST_REQ: OnCT_CMGIFTLIST_REQ(mgr, r); break;
            case Msg.CT_CMGIFTCHARTUPDATE_REQ: OnCT_CMGIFTCHARTUPDATE_REQ(mgr, r, packet); break;

            // --- event admin (.Event.cs) ---
            case Msg.CT_EVENTCHANGE_REQ: await OnCT_EVENTCHANGE_REQ(mgr, r); break;
            case Msg.CT_EVENTLIST_REQ: OnCT_EVENTLIST_REQ(mgr); break;
            case Msg.CT_CASHITEMLIST_REQ: OnCT_CASHITEMLIST_REQ(mgr); break;

            default:
                _log.LogDebug("Unhandled manager msg 0x{Id:X4}.", r.Id);
                break;
        }
    }

    /// <summary>Packets from an outbound game server.</summary>
    private void DispatchServer(ServerSession srv, PacketReader r, byte[] packet)
    {
        switch (r.Id)
        {
            case Msg.CT_SERVICEMONITOR_REQ: OnCT_SERVICEMONITOR_REQ(srv, r); break;
            case Msg.CT_CHATBAN_ACK: OnCT_CHATBAN_ACK(r); break;
            case Msg.CT_MONSPAWNFIND_ACK: OnCT_MONSPAWNFIND_ACK(r); break;
            case Msg.CT_ITEMFIND_ACK: OnCT_ITEMFIND_ACK(r); break;
            case Msg.CT_ITEMSTATE_ACK: OnCT_ITEMSTATE_ACK(r, packet); break;
            case Msg.CT_CASTLEINFO_ACK: OnCT_CASTLEINFO_ACK(r, packet); break;
            case Msg.CT_CASTLEGUILDCHG_ACK: OnCT_CASTLEGUILDCHG_ACK(r, packet); break;
            case Msg.CT_EVENTQUARTERLIST_ACK: OnCT_EVENTQUARTERLIST_ACK(r, packet); break;
            case Msg.CT_EVENTQUARTERUPDATE_ACK: OnCT_EVENTQUARTERUPDATE_ACK(r, packet); break;
            case Msg.CT_TOURNAMENTEVENT_ACK: OnCT_TOURNAMENTEVENT_ACK(r, packet); break;
            case Msg.CT_RPSGAMEDATA_ACK: OnCT_RPSGAMEDATA_ACK(packet); break;
            case Msg.CT_CMGIFT_ACK: OnCT_CMGIFT_ACK(r); break;
            case Msg.CT_CMGIFTLIST_ACK: OnCT_CMGIFTLIST_ACK(r); break;
            case Msg.CT_CASHITEMSALE_ACK: /* no-op cleanup (C++ OnCT_CASHITEMSALE_ACK) */ break;
            default:
                _log.LogDebug("Unhandled server msg 0x{Id:X4}.", r.Id);
                break;
        }
    }

    // ===== authority =====

    /// <summary>C++ <c>CTManager::CheckAuthority</c>: pass when authority &lt;= class; otherwise (for
    /// non-service operators) send CT_AUTHORITY_ACK and deny.</summary>
    private bool CheckAuthority(ManagerSession mgr, ManagerClass cls)
    {
        if (mgr.IsManager && mgr.Authority >= (byte)ManagerClass.All && mgr.Authority <= (byte)cls) return true;
        mgr.Send(new PacketWriter(Msg.CT_AUTHORITY_ACK));
        return false;
    }

    // Every manager message is covered, including legacy handlers without their own authority check.
    private bool AuthorizeMessage(ManagerSession mgr, ushort id)
    {
        ManagerClass? required = id switch
        {
            Msg.CT_SERVICESTAT_REQ => ManagerClass.GmLevel3,
            Msg.CT_SERVICECONTROL_REQ or Msg.CT_SERVICEAUTOSTART_REQ or Msg.CT_RECONNECT_REQ => ManagerClass.Service,
            Msg.CT_SERVICEUPLOADSTART_REQ or Msg.CT_SERVICEUPLOAD_REQ or Msg.CT_SERVICEUPLOADEND_REQ
                or Msg.CT_UPDATEPATCH_REQ or Msg.CT_PREVERSIONTABLE_REQ or Msg.CT_PREVERSIONUPDATE_REQ => ManagerClass.Control,
            Msg.CT_ANNOUNCEMENT_REQ or Msg.CT_MONSPAWNFIND_REQ or Msg.CT_MONACTION_REQ
                or Msg.CT_USERPROTECTED_REQ => ManagerClass.GmLevel1,
            Msg.CT_USERKICKOUT_REQ or Msg.CT_USERMOVE_REQ or Msg.CT_USERPOSITION_REQ or Msg.CT_CHARMSG_REQ => ManagerClass.GmLevel2,
            Msg.CT_CHATBAN_REQ or Msg.CT_EVENTCHANGE_REQ => ManagerClass.GmLevel3,
            Msg.CT_EVENTLIST_REQ or Msg.CT_CASHITEMLIST_REQ => ManagerClass.User,
            Msg.CT_SERVICEDATACLEAR_REQ or Msg.CT_CHATBANLIST_REQ or Msg.CT_CHATBANLISTDEL_REQ
                or Msg.CT_ITEMFIND_REQ or Msg.CT_ITEMSTATE_REQ or Msg.CT_CASTLEINFO_REQ or Msg.CT_CASTLEGUILDCHG_REQ
                or Msg.CT_CASTLEENABLE_REQ or Msg.CT_HELPMESSAGE_REQ or Msg.CT_RPSGAMEDATA_REQ or Msg.CT_RPSGAMECHANGE_REQ
                or Msg.CT_TOURNAMENTEVENT_REQ or Msg.CT_EVENTQUARTERLIST_REQ or Msg.CT_EVENTQUARTERUPDATE_REQ
                or Msg.CT_CMGIFT_REQ or Msg.CT_CMGIFTLIST_REQ or Msg.CT_CMGIFTCHARTUPDATE_REQ => ManagerClass.All,
            _ => null,
        };
        if (required is { } permission && (!mgr.ReadOnly || id == Msg.CT_SERVICESTAT_REQ))
            return CheckAuthority(mgr, permission);
        mgr.Send(new PacketWriter(Msg.CT_AUTHORITY_ACK));
        return false;
    }

    // ===== monitor tick (1s) — C++ OnCT_TIMER_REQ + QueryStatus =====

    public async Task OnTickAsync()
    {
        DrainStopResults();
        long now = Environment.TickCount64;
        foreach (var svc in _state.Services.Values)
        {
            if (!_pendingStops.ContainsKey(svc.Id) && _svcCtl.GetStatus(svc) is { } status)
            {
                SetServiceStatus(svc, status, 0);
                if (status == Proto.SvcStopped && _state.AutoStart && svc.ManagerControl && now >= svc.RestartAfterTick
                    && !_pendingStops.Values.Any(p => p.Service.IsWorld && p.Service.Group.GroupId == svc.Group.GroupId))
                {
                    svc.RestartAfterTick = now + _options.RestartDelaySeconds * 1000L;
                    if (_svcCtl.Start(svc)) SetServiceStatus(svc, Proto.SvcRunning, 0);
                }
            }
            if (svc.Conn is { } conn)
            {
                // 60s recv timeout -> notify managers + SMS (C++ OnCT_TIMER_REQ).
                if (svc.RecvTick != 0 && now - svc.RecvTick > 60000)
                {
                    svc.RecvTick = 0;
                    foreach (var m in _state.Managers)
                        if (m.Authority <= (byte)ManagerClass.Service)
                            m.Send(BuildServiceData(svc, session: 0, curUser: 0, ping: 60000, activeUser: 0));
                    _log.LogWarning("Service {Name} monitor timeout (60s).", svc.Name);
                    _ = SvrStatusSmsAsync(svc.SvrType.Type, svc.Id, 3);
                }
                // Poll live counts (control -> game server).
                conn.Send(new PacketWriter(Msg.CT_SERVICEMONITOR_ACK).WriteUInt32((uint)now).ToArray());
            }
            else
            {
                foreach (var m in _state.Managers)
                    if (m.Authority <= (byte)ManagerClass.Service)
                        m.Send(BuildServiceData(svc, 0, 0, 0, 0));
            }
        }

        if (_platform.Sample(_options.LocalMachineId) is { } sample)
            foreach (var manager in _state.Managers.Where(m => m.Authority <= (byte)ManagerClass.Service))
                manager.Send(new PacketWriter(Msg.CT_PLATFORM_ACK).WriteByte(_options.LocalMachineId)
                    .WriteUInt32(sample.Cpu).WriteUInt32(sample.Mem).WriteFloat(sample.Net));

        await CheckEventAsync();
    }

    public void CloseSessions()
    {
        if (_uploadOwner is { } owner) CancelUpload(owner);
        foreach (var manager in _state.Sessions) manager.Conn.Close();
        foreach (var service in _state.Connected) service.Conn!.Conn.Close();
    }

    // ===== shared helpers =====

    /// <summary>Set a service's status and broadcast the change to managers (C++ CT_SERVICECHANGE flow).</summary>
    private void SetServiceStatus(ServiceInstance svc, uint status, byte smsType)
    {
        if (svc.Status == status) return;
        if (status == Proto.SvcStopped)
        {
            svc.StopCount++;
            svc.LatestStop = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            svc.RestartAfterTick = Environment.TickCount64 + _options.RestartDelaySeconds * 1000L;
        }
        svc.Status = status;
        foreach (var m in _state.Managers)
            m.Send(new PacketWriter(Msg.CT_SERVICECHANGE_ACK).WriteUInt32(svc.Id).WriteUInt32(status).ToArray());
        if (smsType != 0) _ = SvrStatusSmsAsync(svc.SvrType.Type, svc.Id, smsType);
    }

    private void BroadcastToManagers(byte[] packet)
    {
        foreach (var m in _state.Managers) m.Send(packet);
    }

    /// <summary>Re-frame an already-parsed packet body under a (possibly different) message id, copying the
    /// bytes after the header verbatim. Mirrors the C++ <c>CPacket::Copy</c> + <c>SetID</c> forwarding.</summary>
    private static byte[] Reframe(ushort id, byte[] packet) => ReframeSkip(id, packet, 0);

    /// <summary>Re-frame, skipping <paramref name="skip"/> leading body bytes. Mirrors the C++
    /// <c>CPacket::CopyData(pkt, skip)</c> + <c>SetID</c> — e.g. <c>CopyData(pkt, sizeof(BYTE))</c> drops the
    /// leading group byte the control server used only for routing.</summary>
    private static byte[] ReframeSkip(ushort id, byte[] packet, int skip)
    {
        int start = Math.Min(PacketHeader.Size + skip, packet.Length);
        var body = packet.AsSpan(start);
        var w = new PacketWriter(id, capacity: PacketHeader.Size + body.Length);
        w.WriteRaw(body);
        return w.ToArray();
    }

    private async Task SvrStatusSmsAsync(byte svrType, uint svrId, byte status)
    {
        if (_db is null) return;
        try { await _db.SvrStatusSmsAsync(svrType, svrId, status, CancellationToken.None); }
        catch (Exception ex) { _log.LogWarning(ex, "OPTool_SMSEmergency failed."); }
    }
}
