using TWorld.Protocol;

namespace TWorld.Server.World;

public sealed partial class WorldService
{
    // CHAT_GROUP, SSHandler.cpp:5248. Membership and sender identity are authoritative here.
    private void RoutePlayerChat(ServerSession source, PacketReader r)
    {
        r.ReadByte();
        uint senderId = r.ReadUInt32(), key = r.ReadUInt32();
        string suppliedName = r.ReadString(); r.ReadByte(); // display type is derived from the operator chart
        byte group = r.ReadByte(); uint target = r.ReadUInt32();
        string name = r.ReadString(), talk = r.ReadString();
        var sender = _state.FindChar(senderId, key);
        if (sender is null || sender.Logout || source.ServerType != 4 || source.ServerId != sender.MainId
            || !string.Equals(suppliedName, sender.Name, StringComparison.Ordinal)
            || sender.ChatBanTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || (_state.ChatBans.TryGetValue(sender.Name, out long bannedUntil) && bannedUntil > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            || group is 1 or 3 or 7 or > 9) return;
        if (talk.Length > 1024) talk = talk[..1024];
        byte type = _state.Operators.Contains(senderId) ? (byte)7 : group;

        byte[] Message(uint id, uint recipientKey, string displayName, uint displayedSender, byte displayType, string text)
            => new PacketWriter(Msg.MW_CHAT_REQ).WriteUInt32(id).WriteUInt32(recipientKey).WriteByte(sender.Channel)
                .WriteUInt32(displayedSender).WriteString(displayName).WriteByte(sender.Country).WriteByte(sender.AidCountry)
                .WriteByte(displayType).WriteByte(group).WriteUInt32(target).WriteString(text).ToArray();
        bool Send(Character recipient, string? displayName = null, uint? displayedSender = null, byte? displayType = null, string? text = null)
        {
            if (recipient.Logout || !_state.Characters.TryGetValue(recipient.CharId, out var live) || !ReferenceEquals(live, recipient)
                || _state.FindMapSvr(recipient.MainId) is not { ServerType: 4 } server) return false;
            server.Send(Message(recipient.CharId, recipient.Key, displayName ?? sender.Name, displayedSender ?? senderId, displayType ?? type, text ?? talk));
            return true;
        }
        switch (group)
        {
            case 2: case 9: // CHAT_MAP / CHAT_SHOW: native fan-out to every map, faction filtering in Map
                if (group == 9 && (target != talk.Length || !talk.Contains(sender.Name, StringComparison.Ordinal))) return;
                var broadcast = Message(0, 0, sender.Name, senderId, type, talk);
                foreach (var map in _state.Servers.Values.Where(s => s.ServerType == 4)) map.Send(broadcast);
                break;
            case 4: // party
                if (sender.Party is not { } party) return;
                target = party.Id;
                foreach (var member in party.Members) Send(member);
                break;
            case 5: case 8: // guild / guild + mercenaries
                var guild = group == 8 ? _state.FindTacticsGuild(senderId) ?? sender.Guild : sender.Guild;
                if (guild is null) return;
                target = guild.Id;
                var recipients = guild.Members.Values.Select(m => m.OnlineChar);
                if (group == 8) recipients = recipients.Concat(guild.Tactics.Values.Select(m => m.OnlineChar));
                foreach (var member in recipients.Where(c => c is not null).DistinctBy(c => c!.CharId)) Send(member!);
                break;
            case 6: // corps, derived from the sender's party
                if (sender.Party is not { } squad || !_state.CorpsMap.TryGetValue(squad.CorpsId, out var corps)) return;
                foreach (var member in corps.Parties.Values.SelectMany(p => p.Members).DistinctBy(c => c.CharId)) Send(member);
                break;
            case 0:
                string operatorName = _state.GetSvrMsg((uint)SvrMsg.NameOperator);
                if (operatorName.Length != 0 && name == operatorName)
                {
                    int delivered = 0;
                    foreach (uint id in _state.Operators)
                        if (_state.Characters.TryGetValue(id, out var op) && op.Country == sender.Country && Send(op)) delivered++;
                    Send(sender, operatorName, delivered == 0 ? 0u : senderId, delivered == 0 ? (byte)7 : type,
                        delivered == 0 ? BuildNetString("", _state.GetSvrMsg((uint)SvrMsg.CharLogout)) : talk);
                    return;
                }
                _state.Characters.TryGetValue(target, out var to);
                if (to is null || to.Logout || _state.FindMapSvr(to.MainId) is null)
                    _state.CharactersByName.TryGetValue(name, out to);
                if (to is null || (GetWarCountry(sender) != GetWarCountry(to) && sender.Country != 4 && to.Country != 4)) return;
                if (Send(to)) Send(sender, to.Name);
                break;
        }
    }
}
