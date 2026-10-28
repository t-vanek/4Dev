using Microsoft.Extensions.Logging;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Small client-plane handlers: ping and disconnect/terminate. Channel changes are in MapService.Channels.cs,
/// mode changes in MapService.SmallRequests.cs, and regions in MapService.Territory.cs.
/// </summary>
public sealed partial class MapService
{
    private void OnCS_PINGMEASUREMENT_REQ(ClientSession s, PacketReader r)
    {
        uint tick = r.ReadUInt32();
        var w = new PacketWriter(Msg.CS_PINGMEASUREMENT_ACK);
        w.WriteUInt32(tick); // echo the client tick back (round-trip time measurement)
        s.Send(w);
    }

    private void OnCS_COUNTDOWN_REQ(ClientSession s, PacketReader r)
    {
        // "Character selection" / "Exit" after the 8s outro: the client waits for this echo before it runs
        // dwCommand (GM_EXIT_GAME -> relog to char select, GM_EXIT -> quit). C++ also sets m_bGraceExit,
        // which only skips the combat-logout death penalty (not ported).
        if (!s.IsMain || s.Char is null) return;
        uint command = r.ReadUInt32();
        var w = new PacketWriter(Msg.CS_COUNTDOWN_ACK);
        w.WriteUInt32(command);
        s.Send(w);
    }

    private void OnCS_DISCONNECT_REQ(ClientSession s, PacketReader r)
    {
        // C++: CloseSession(pPlayer). The socket teardown drives OnClientDisconnectAsync (world close + view leave).
        s.Conn.Close();
    }

    private void OnCS_TERMINATE_REQ(ClientSession s, PacketReader r)
    {
        // The C++ treats CS_TERMINATE_REQ as a backdoor-attack probe: it logs and does nothing else.
        _log.LogWarning("CS_TERMINATE_REQ from {Endpoint} (char {Char}); ignored.", s.Conn.RemoteEndPoint, s.CharId);
    }
}
