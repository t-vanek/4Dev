using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    private const byte ChcNoChannel = 2; // TCHGCHANNEL_RESULT, NetCode.h

    private bool HostsChannel(byte channel) => _opt.Channels.Length == 0
        ? channel == DefaultChannel : _opt.Channels.Contains(channel);

    /// <summary>CSHandler.cpp OnCS_CHGCHANNEL_REQ. Each channel here has its own grid, so a channel
    /// change takes the ordinary world-coordinated teleport path, even when both channels use this server.
    /// The native acknowledgement-only branch applies to a shared physical map, which this port does not model.</summary>
    private void OnCS_CHGCHANNEL_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Grid is null || s.Char is not { } ch
            || s.TeleportPending) return;

        byte channel = r.ReadByte();
        if (channel == s.Channel) return;

        // Reject before changing any state or sending a loading-screen packet. Channel ownership is the
        // configured MW_CONNECT_ACK list; this deployment has one map server, not a remote-channel topology.
        if (!_worldReady || !HostsChannel(channel) || !OwnsMap(ch.MapId)
            || IsSmallMeetingRoom(ch.MapId) || ch.ArenaId != 0)
        {
            s.Send(new PacketWriter(Msg.CS_CHGCHANNEL_ACK).WriteByte(ChcNoChannel).WriteByte(s.Channel));
            return;
        }

        Teleport(s, ch, channel, ch.MapId, ch.PosX, ch.PosY, ch.PosZ);
    }
}
