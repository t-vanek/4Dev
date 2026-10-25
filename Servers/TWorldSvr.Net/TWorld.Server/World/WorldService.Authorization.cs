using TWorld.Protocol;
using TWorld.Server.Security;

namespace TWorld.Server.World;

public sealed partial class WorldService
{
    private bool AuthorizePeer(ServerSession s, ushort id)
    {
        if (s.Conn.Identity.Role == PeerRole.Control)
        {
            if (id == Msg.CT_CTRLSVR_REQ)
                return _state.ControlServer is null || ReferenceEquals(_state.ControlServer, s);
            if (!ReferenceEquals(_state.ControlServer, s)) return false;
            return id is Msg.CT_SERVICEMONITOR_ACK or Msg.CT_USERMOVE_ACK or Msg.CT_USERPOSITION_ACK
                or Msg.CT_CHATBAN_REQ or Msg.CT_CHARMSG_ACK or Msg.CT_SERVICEDATACLEAR_ACK
                or Msg.CT_CASTLEGUILDCHG_REQ or Msg.CT_EVENTMSG_REQ or Msg.CT_CASHSHOPSTOP_REQ
                or Msg.CT_HELPMESSAGE_REQ or Msg.CT_RPSGAMEDATA_REQ or Msg.CT_RPSGAMECHANGE_REQ
                or Msg.CT_CMGIFTLIST_REQ or Msg.CT_CASHITEMSALE_REQ or Msg.CT_CMGIFT_REQ
                or Msg.CT_CMGIFTCHARTUPDATE_REQ or Msg.CT_ITEMFIND_REQ or Msg.CT_ITEMSTATE_REQ
                or Msg.CT_EVENTQUARTERLIST_REQ or Msg.CT_EVENTQUARTERUPDATE_REQ or Msg.CT_TOURNAMENTEVENT_REQ
                or Msg.SM_BATTLESTATUS_REQ or Msg.CT_EVENTUPDATE_REQ;
        }
        if (s.Conn.Identity.Role != PeerRole.Map) return false;
        if (id == Msg.MW_CONNECT_ACK) return s.WId == 0;
        if (s.WId == 0) return false;
        // These two CT messages are also emitted by in-game GM/corps operations on trusted maps.
        if (id is Msg.CT_USERPOSITION_ACK or Msg.CT_USERMOVE_ACK) return true;
        return id >= Msg.MW_BASE && id < Msg.CT_CONTROL;
    }
}
