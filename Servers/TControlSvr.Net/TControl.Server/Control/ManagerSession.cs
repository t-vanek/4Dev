using TControl.Protocol;
using TControl.Server.Net;

namespace TControl.Server.Control;

/// <summary>
/// Per-connection state for an inbound GM admin-tool peer — the ported C++ <c>CTManager</c>. A session is
/// "valid" (in <see cref="ControlState.Managers"/>) only after a successful <c>CT_OPLOGIN</c>/<c>CT_STLOGIN</c>.
/// </summary>
public sealed class ManagerSession
{
    public ManagerSession(PacketConnection conn) => Conn = conn;

    public PacketConnection Conn { get; }

    /// <summary>Authenticated (logged-in) flag — C++ <c>m_bManager</c>.</summary>
    public bool IsManager { get; set; }
    /// <summary>Operator authority level (lower = more privileged) — C++ <c>m_bAuthority</c>.</summary>
    public byte Authority { get; set; } = byte.MaxValue;
    public bool ReadOnly { get; set; }
    /// <summary>Session sequence id used to route ACKs back — C++ <c>m_dwID</c>.</summary>
    public uint Id { get; set; }
    public string StrId { get; set; } = "";
    /// <summary>A file upload is in progress on this session — C++ <c>m_bUpload</c>.</summary>
    public bool Upload { get; set; }

    public void Send(byte[] packet) => Conn.Send(packet);
    public void Send(PacketWriter w) => Conn.Send(w);
}
