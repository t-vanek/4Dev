using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>Rented private maps 1101–1105. Reservations are transient and timers run on the serialized map tick,
/// replacing the C++ SM_MEETINGROOM timer round-trip. Portals remain immutable chart data.</summary>
public sealed partial class MapService
{
    private const ushort MeetingLobby = 1100, MeetingLast = 1105, MeetingExit = 15500;
    private const uint MeetingDurationMs = 3_600_000;
    private readonly Dictionary<(byte Channel, ushort Source, ushort Destination), MeetingRoom> _meetingRooms = new();
    private readonly Dictionary<uint, (string Host, long Expires)> _meetingInvitations = new();
    private sealed record MeetingRoom(uint Owner, byte Channel, ushort MapId, uint Started);

    private static bool IsSmallMeetingRoom(ushort mapId) => mapId > MeetingLobby && mapId <= MeetingLast;
    private MeetingRoom? OwnedMeetingRoom(ClientSession s) => _meetingRooms.Values.FirstOrDefault(x => x.Owner == s.CharId);
    private bool IsMeetingChief(ClientSession s) => s.Char is { } ch && s.State == EnterState.InGame
        && IsSmallMeetingRoom(ch.MapId) && OwnedMeetingRoom(s) is { } room && room.Channel == s.Channel && room.MapId == ch.MapId;

    private static void SendMeetingRoom(ClientSession s, byte type, byte result, string name)
        => s.Send(new PacketWriter(Msg.CS_MEETINGROOM_ACK).WriteByte(type).WriteByte(result).WriteString(name));

    private void SendWorldMeetingRoom(ClientSession s, byte type, byte result, string name)
        => _world.Send(new PacketWriter(Msg.MW_MEETINGROOM_ACK).WriteUInt32(s.CharId).WriteUInt32(s.Key)
            .WriteByte(type).WriteByte(result).WriteString(name));

    private void OnCS_MEETINGROOM_REQ(ClientSession s, PacketReader r)
    {
        byte type = r.ReadByte(), value = r.ReadByte();
        string name = r.ReadString();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is null || type > 1 || name.Length is 0 or > 50) return;
        if (type == 0 && !IsMeetingChief(s)) { SendMeetingRoom(s, type, 4, name); return; } // MTR_NOTCHIEF
        if (type == 1)
        {
            if (!_meetingInvitations.TryGetValue(s.CharId, out var invite) || invite.Expires < UnixNow()
                || !string.Equals(invite.Host, name, StringComparison.OrdinalIgnoreCase)) return;
            _meetingInvitations.Remove(s.CharId);
            if (value == 0 && (IsActionBlock(s.Char) || IsTutorial(s.Char) || s.Char.DuelId != 0 || s.Store.IsOpen || s.Deal.InProgress))
                value = 2;
        }
        SendWorldMeetingRoom(s, type, value, name);
    }

    private void OnMW_MEETINGROOM_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte type = r.ReadByte(), result = r.ReadByte();
        string name = r.ReadString();
        if (FindPlayer(charId, key) is not { IsMain: true, State: EnterState.InGame, Char: { } ch } s || type > 1) return;
        if (result == 0 && type == 0)
        {
            if (IsActionBlock(ch) || IsTutorial(ch) || ch.DuelId != 0 || s.Store.IsOpen || s.Deal.InProgress)
            { SendWorldMeetingRoom(s, 1, 2, name); return; } // MTR_BUSY
            _meetingInvitations[s.CharId] = (name, UnixNow() + 120);
        }
        SendMeetingRoom(s, type, result, name);
    }

    private bool MeetingDestinationBusy(byte channel, ushort source, ushort destination, uint owner)
    {
        if (_meetingRooms.TryGetValue((channel, source, destination), out var room) && room.Owner != owner) return true;
        return _templates.Portals.TryGetValue(destination, out var portal)
            && _templates.SpawnPositions.TryGetValue(portal.SpawnId, out var spawn)
            && _meetingRooms.Values.Any(x => x.Channel == channel && x.MapId == spawn.MapId && x.Owner != owner);
    }

    private void ReserveMeetingRoom(ClientSession s, ushort source, ushort destination, ushort map)
        => _meetingRooms[(s.Channel, source, destination)] = new MeetingRoom(s.CharId, s.Channel, map, NowMs);

    private void ReleaseMeetingRoom(ClientSession s, bool disconnect = false)
    {
        var room = OwnedMeetingRoom(s);
        if (room is null || (!disconnect && s.Char?.MapId != room.MapId)) return;
        CloseMeetingRoom(room, s.CharId);
    }

    private void CloseMeetingRoom(MeetingRoom room, uint leaving = 0)
    {
        foreach (var key in _meetingRooms.Where(x => x.Value == room).Select(x => x.Key).ToArray()) _meetingRooms.Remove(key);
        foreach (var guest in _state.AllInGame().Where(s => s.Channel == room.Channel && s.Char!.MapId == room.MapId
            && s.CharId != leaving).ToArray())
            Teleport(guest, guest.Char!, MeetingExit);
    }

    private void MeetingRoomTick()
    {
        foreach (var room in _meetingRooms.Values.ToArray())
        {
            var owner = _state.FindByChar(room.Owner);
            bool abandoned = owner is null || (owner.Char?.MapId != room.MapId && unchecked(NowMs - room.Started) >= 30_000);
            if (abandoned || unchecked(NowMs - room.Started) >= MeetingDurationMs) CloseMeetingRoom(room);
        }
        long now = UnixNow();
        foreach (uint charId in _meetingInvitations.Where(x => x.Value.Expires < now).Select(x => x.Key).ToArray())
            _meetingInvitations.Remove(charId);
    }

    private bool NormalizeMeetingEntry(ClientSession s)
    {
        if (s.Char is not { } ch || !IsSmallMeetingRoom(ch.MapId) || OwnedMeetingRoom(s) is not null) return true;
        if (!_templates.SpawnPositions.TryGetValue(MeetingExit, out var exit)) { s.Conn.Close(); return false; }
        ch.MapId = exit.MapId; ch.PosX = exit.PosX; ch.PosY = exit.PosY; ch.PosZ = exit.PosZ;
        return true;
    }

    // Rentals are transient: a logout/restart must not put a character inside somebody else's next rental.
    private CharSaveData WithMeetingExit(CharSaveData data)
        => IsSmallMeetingRoom(data.MapId) && _templates.SpawnPositions.TryGetValue(MeetingExit, out var exit)
            ? data with { MapId = exit.MapId, PosX = exit.PosX, PosY = exit.PosY, PosZ = exit.PosZ } : data;
}
