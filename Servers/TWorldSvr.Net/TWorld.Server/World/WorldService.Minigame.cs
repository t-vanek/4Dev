using TWorld.Protocol;
using TWorld.Server.Net;

namespace TWorld.Server.World;

/// <summary>
/// Final MW slice — minigames and status queries, ported from <c>SSHandler.cpp</c>.
/// <list type="bullet">
/// <item><c>RPSGAME</c>: a rock-paper-scissors play. The world arbitrates whether the win may stand under the
/// per-(type,winCount) "win-keep" rule (only so many wins allowed inside a rolling period), prunes aged-out
/// wins, and replies with the verdict.</item>
/// <item><c>MEETINGROOM</c>: a private meeting-room invite/accept handshake between two characters, ending in a
/// <c>CT_USERMOVE</c> teleport of the inviter to the accepter's room.</item>
/// <item><c>BATTLEMODESTATUS</c>: a snapshot of the in-process BoW/BR machines, relayed to the asking char's main.</item>
/// </list>
/// The RPS chart (and its DB record persistence) is a deferred config gap, so the catalog is empty unless
/// seeded — an unconfigured (type,winCount) simply yields a "win denied" verdict, faithful to a missing row.
/// </summary>
public sealed partial class WorldService
{
    private readonly Dictionary<(uint Host, uint Guest), (uint HostKey, uint GuestKey, long Expires)> _meetingInvitations = new();
    private bool DispatchMinigame(ServerSession session, PacketReader r)
    {
        switch (r.Id)
        {
            case Msg.MW_RPSGAME_ACK: OnMW_RPSGAME_ACK(session, r); return true;
            case Msg.MW_MEETINGROOM_ACK: OnMW_MEETINGROOM_ACK(session, r); return true;
            case Msg.MW_BATTLEMODESTATUS_REQ: OnMW_BATTLEMODESTATUS_REQ(r); return true;
        }
        return false;
    }

    /// <summary>BoW/BR status snapshot for the asking char's main. C++ OnMW_BATTLEMODESTATUS_REQ.</summary>
    private void OnMW_BATTLEMODESTATUS_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        var ch = _state.FindChar(charId, key);
        if (ch is null) return;
        _state.FindMapSvr(ch.MainId)?.Send(BuildBattleModeStatus(charId, key));
    }

    private byte[] BuildBattleModeStatus(uint charId, uint key)
    {
        var w = new PacketWriter(Msg.MW_BATTLEMODESTATUS_ACK);
        w.WriteUInt32(charId); w.WriteUInt32(key);
        if (_state.Bow is { } bow) { w.WriteByte((byte)bow.Status); w.WriteUInt32(bow.Start); w.WriteByte(bow.Winner); }
        else { w.WriteByte(0); w.WriteUInt32(0); w.WriteByte((byte)Contry.None); }
        if (_state.Br is { } br) { w.WriteByte((byte)br.Status); w.WriteUInt32(br.Start); w.WriteByte(br.Type); }
        else { w.WriteByte(0); w.WriteUInt32(0); w.WriteByte(0); }
        return w.ToArray();
    }

    /// <summary>A rock-paper-scissors play. C++ OnMW_RPSGAME_ACK.</summary>
    private void OnMW_RPSGAME_ACK(ServerSession session, PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        byte type = r.ReadByte();
        byte winCount = r.ReadByte();
        byte playerRps = r.ReadByte();

        bool result = true;
        ushort mapKey = (ushort)(type | (winCount << 8));   // MAKEWORD(type, winCount)

        if (!_state.RpsGames.TryGetValue(mapKey, out var rps))
        {
            result = false;
        }
        else if (rps.WinKeep != 0)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ushort concurrent = 0;
            for (int i = 0; i < rps.WinDates.Count;)
            {
                long age = now - rps.WinDates[i];
                if (age < (long)rps.WinPeriod * Proto.DayOne) { concurrent++; i++; }
                else if (age > (long)Proto.DayOne * 30)
                {
                    long old = rps.WinDates[i];
                    rps.WinDates.RemoveAt(i);
                    if (_gameDb is not null) _ = _gameDb.SaveRpsRecordAsync(false, 0, type, winCount, old);   // prune in DB
                }
                else i++;
            }

            if (concurrent >= rps.WinKeep)
                result = false;
            else
            {
                rps.WinDates.Add(now);
                if (_gameDb is not null) _ = _gameDb.SaveRpsRecordAsync(true, charId, type, winCount, now);   // record the win
            }
        }

        var w = new PacketWriter(Msg.MW_RPSGAME_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key); w.WriteBool(result); w.WriteByte(playerRps);
        session.Send(w.ToArray());
    }

    /// <summary>Private meeting-room invite (type 0) and accept (type != 0). On accept the inviter is teleported
    /// to the accepter's room via CT_USERMOVE. C++ OnMW_MEETINGROOM_ACK.</summary>
    private void OnMW_MEETINGROOM_ACK(ServerSession session, PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        byte type = r.ReadByte();
        byte value = r.ReadByte();
        string name = r.ReadString();

        var ch = _state.FindChar(charId, key);
        _state.CharactersByName.TryGetValue(name, out var target);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var expired in _meetingInvitations.Where(x => x.Value.Expires < now).Select(x => x.Key).ToArray())
            _meetingInvitations.Remove(expired);
        if (type > 1 || value > (byte)MeetingResult.InRoom || (ch is not null && ch.MainId != session.ServerId)) return;

        if (ch is null && target is null) return;

        if (ch is not null && target is null)
        {
            session.Send(BuildMeetingRoom(charId, key, type, (byte)MeetingResult.NoTarget, name));
            return;
        }

        var targetMap = _state.FindMapSvr(target!.MainId);
        if (targetMap is null)
        {
            session.Send(BuildMeetingRoom(charId, key, type, (byte)MeetingResult.NoTarget, name));
            return;
        }

        if (ch is null)   // target exists but the inviter went away: tell the target's map
        {
            targetMap.Send(BuildMeetingRoom(target.CharId, target.Key, type, (byte)MeetingResult.NoTarget, name));
            return;
        }

        if (type == 0)   // invite
        {
            if (!IsMeetingRoom(ch.MapId, small: true))
            {
                session.Send(BuildMeetingRoom(charId, key, type, (byte)MeetingResult.NotChief, name));
                return;
            }
            if ((byte)(target.PosX / Proto.UnitSize) != 3 || (byte)(target.PosZ / Proto.UnitSize) != 0)
            {
                session.Send(BuildMeetingRoom(charId, key, type, (byte)MeetingResult.NoTarget, name));
                return;
            }
            if (target.MapId != 0)
            {
                session.Send(BuildMeetingRoom(charId, key, type, (byte)MeetingResult.Busy, name));
                return;
            }
            _meetingInvitations[(ch.CharId, target.CharId)] = (ch.Key, target.Key, now + 120);
            targetMap.Send(BuildMeetingRoom(target.CharId, target.Key, type, (byte)MeetingResult.Success, ch.Name));
        }
        else   // accept / decline
        {
            if (!_meetingInvitations.Remove((target.CharId, ch.CharId), out var invite)
                || invite.HostKey != target.Key || invite.GuestKey != ch.Key || invite.Expires < now)
            {
                session.Send(BuildMeetingRoom(charId, key, type, (byte)MeetingResult.NoTarget, name));
                return;
            }
            if (value != (byte)MeetingResult.Success)
            {
                targetMap.Send(BuildMeetingRoom(target.CharId, target.Key, type, value, ch.Name));
                return;
            }
            if (!IsMeetingRoom(target.MapId, small: true))
            {
                session.Send(BuildMeetingRoom(charId, key, type, (byte)MeetingResult.NoTarget, name));
                return;
            }
            // Teleport the inviter into the accepter's room (CT_USERMOVE; wPartyID defaults to 0).
            var w = new PacketWriter(Msg.CT_USERMOVE_ACK);
            w.WriteUInt32(ch.CharId); w.WriteUInt32(ch.Key); w.WriteByte(target.Channel); w.WriteUInt16(target.MapId);
            w.WriteFloat(target.PosX); w.WriteFloat(target.PosY); w.WriteFloat(target.PosZ); w.WriteUInt16(0);
            session.Send(w.ToArray());
        }
    }

    private static byte[] BuildMeetingRoom(uint charId, uint key, byte type, byte result, string name)
    {
        var w = new PacketWriter(Msg.MW_MEETINGROOM_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key); w.WriteByte(type); w.WriteByte(result); w.WriteString(name);
        return w.ToArray();
    }

    /// <summary>A meeting-room map is the small-room range [MEETING_MAPID+1 .. MEETING_MAPID+SROOM_COUNT].
    /// CTWorldSvrModule::IsMeetingRoom.</summary>
    private static bool IsMeetingRoom(ushort mapId, bool small)
    {
        bool inRange = mapId >= Proto.MeetingMapId && mapId <= Proto.MeetingMapId + Proto.MeetingSroomCount;
        if (small) inRange = inRange && mapId != Proto.MeetingMapId;
        return inRange;
    }
}
