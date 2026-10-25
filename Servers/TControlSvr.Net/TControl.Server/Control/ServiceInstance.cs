using TControl.Protocol;

namespace TControl.Server.Control;

/// <summary>
/// Runtime state for one configured game-server service — the ported C++ <c>TSVRTEMP</c>. Assembled from
/// the DB topology (or the configured dial list) at startup. <see cref="Conn"/> is the live outbound
/// connection when connected; <see cref="Status"/> tracks the service status reported to managers.
/// </summary>
public sealed class ServiceInstance
{
    public uint Id { get; init; }              // MAKESVRID(group, type, serverId)
    public byte ServerId { get; init; }
    public ushort Port { get; init; }
    public string Name { get; init; } = "";
    public TGroup Group { get; init; } = new();
    public TSvrType SvrType { get; init; } = new();
    public TMachine Machine { get; init; } = new();

    public uint Status { get; set; } = Proto.SvcStopped;
    public uint MaxUser { get; set; }
    public uint ActiveUser { get; set; }
    public uint StopCount { get; set; }
    public long LatestStop { get; set; }
    public long PickTime { get; set; }
    public bool ManagerControl { get; set; }
    public LocalProcessOptions? Process { get; init; }
    public Security.PeerTlsClientOptions? PeerTls { get; init; }
    public long RestartAfterTick { get; set; }

    /// <summary>The live outbound connection (a <see cref="ServerSession"/>) or null when not connected.</summary>
    public ServerSession? Conn { get; set; }

    /// <summary>Last tick (ms) a monitor reply was received (C++ <c>m_dwRecvTick</c>); 0 when never/timed-out.</summary>
    public long RecvTick { get; set; }

    /// <summary>The address the connector dials — the machine's private address if present, else its public one.</summary>
    public string DialHost =>
        Machine.PriAddr.Count > 0 ? Machine.PriAddr[0] :
        Machine.IpAddr.Count > 0 ? Machine.IpAddr[0] : "127.0.0.1";

    public bool IsWorld => SvrType.Type == Proto.SVRGRP_WORLDSVR;
    public bool IsMap => SvrType.Type == Proto.SVRGRP_MAPSVR;
    public bool IsRelay => SvrType.Type == Proto.SVRGRP_RLYSVR;
}
