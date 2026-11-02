using Microsoft.Extensions.Logging;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Chat — ported from <c>OnCS_CHAT_REQ</c> (CSHandler.cpp:5206) / <c>SendCS_CHAT_ACK</c>. Local scopes
/// (CHAT_NEAR, and CHAT_WHISPER when the target is on this map) are delivered directly; the remaining
/// scopes (party/guild/tactics/map/force and unresolved whispers) are forwarded to the world via
/// <c>MW_CHAT_ACK</c>. World deliveries use the native receiver layout and faction/block-list rules.
/// World selects party/guild/corps/tactics members and remote whisper recipients from authoritative state.
/// </summary>
public sealed partial class MapService
{
    private const int MaxChatLen = 1024;

    private void OnCS_CHAT_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;

        string sender = r.ReadString();
        var group = (ChatGroup)r.ReadByte();
        uint target = r.ReadUInt32();
        string name = r.ReadString();
        string talk = r.ReadString();

        if (group is ChatGroup.World or ChatGroup.Operator || (byte)group > (byte)ChatGroup.Show) return;
        if (talk.Length > MaxChatLen) talk = talk[..MaxChatLen];
        if (ChatBanned(s, ch)) return;                                                          // MapService.Gm.cs
        if (group == ChatGroup.Show && (target != talk.Length || !talk.Contains(ch.Name, StringComparison.Ordinal))) return;

        // Anti-spoof: the sender name must match the char (the C++ chat-bans a mismatch). Phase-1 just drops.
        if (!string.Equals(sender, ch.Name, StringComparison.Ordinal))
        {
            _log.LogWarning("Chat sender spoof from char {Char} ('{Sender}' != '{Name}').", s.CharId, sender, ch.Name);
            return;
        }

        switch (group)
        {
            case ChatGroup.Near:
                foreach (var other in _state.Neighbors(s))
                    if (other is { IsMain: true, State: EnterState.InGame, Char: { } receiver }
                        && (ch.MapId is >= MeetingLobby and <= MeetingLast || CanTalk(ch, group, receiver.Country, receiver.AidCountry)))
                        SendChat(other, BuildCS_CHAT_ACK(group, s.CharId, ch.Name, talk), s.CharId);
                s.Send(BuildCS_CHAT_ACK(group, s.CharId, ch.Name, talk)); // echo to self
                break;

            case ChatGroup.Whisper:
                bool CanWhisperTo(ClientSession? candidate) => candidate is { IsMain: true, State: EnterState.InGame, Char: { } c }
                    && CanTalk(ch, group, c.Country, c.AidCountry);
                var to = _state.FindByChar(target);
                if (!CanWhisperTo(to)) to = FindByName(name);
                if (CanWhisperTo(to))
                {
                    var recipient = to!.Char!;
                    SendChat(to, BuildCS_CHAT_ACK(IsOperator(ch.CharId) ? ChatGroup.Operator : group, s.CharId, ch.Name, talk), s.CharId);
                    s.Send(BuildCS_CHAT_ACK(group, s.CharId, recipient.Name, talk));
                }
                else
                {
                    SendMW_CHAT_ACK(s, (byte)group, target, name, talk); // maybe on another map
                }
                break;

            default:
                SendMW_CHAT_ACK(s, (byte)group, target, name, talk);
                break;
        }
    }

    /// <summary>C++ <c>SendCS_CHAT_ACK</c>: nothing reaches a player who blocks the sender's chat (MapService.Social.cs).</summary>
    private static void SendChat(ClientSession to, byte[] msg, uint senderId)
    {
        if (to.Char is not { } ch || CheckProtected(ch, senderId, ProtectedChat)) to.Send(msg);
    }

    private static byte[] BuildCS_CHAT_ACK(ChatGroup group, uint senderId, string name, string talk)
    {
        var w = new PacketWriter(Msg.CS_CHAT_ACK);
        w.WriteByte((byte)group);
        w.WriteUInt32(senderId);
        w.WriteString(name);
        w.WriteString(talk);
        return w.ToArray();
    }

    private void SendMW_CHAT_ACK(ClientSession s, byte group, uint target, string name, string talk)
    {
        if (!_worldReady) return;
        var w = new PacketWriter(Msg.MW_CHAT_ACK);
        w.WriteByte(s.Channel);
        w.WriteUInt32(s.CharId);
        w.WriteUInt32(s.Key);
        w.WriteString(s.Char!.Name);
        w.WriteByte(IsOperator(s.CharId) ? (byte)ChatGroup.Operator : group);   // bType
        w.WriteByte(group);   // bGroup
        target = (ChatGroup)group switch { ChatGroup.Party => s.Char.GetPartyId(), ChatGroup.Guild => s.Char.GuildId,
            ChatGroup.Tactics => GuildOf(s.Char), _ => target };
        w.WriteUInt32(target);
        w.WriteString(name);
        w.WriteString(talk);
        _world.Send(w);
    }

    private void OnMW_CHAT_REQ(PacketReader r)
    {
        // SSHandler.cpp:9618. Channel/target are routing context; the native broadcast receiver does
        // not apply another local map/channel filter. Targeted deliveries require the current main key.
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        r.ReadByte(); // channel
        uint senderId = r.ReadUInt32();
        string sender = r.ReadString();
        byte country = r.ReadByte(), aidCountry = r.ReadByte();
        var type = (ChatGroup)r.ReadByte();
        var group = (ChatGroup)r.ReadByte();
        r.ReadUInt32(); // target
        string talk = r.ReadString();

        bool CanReceive(ClientSession recipient)
            => recipient is { IsMain: true, State: EnterState.InGame, Char: { } ch }
               && CanTalk(ch, group, country, aidCountry);

        if (group is ChatGroup.Show or ChatGroup.Map or ChatGroup.World)
        {
            var message = BuildCS_CHAT_ACK(type, senderId, sender, talk);
            foreach (var recipient in _state.AllInGame())
                if (CanReceive(recipient)) SendChat(recipient, message, senderId);
        }
        else if (group is ChatGroup.Tactics or ChatGroup.Force or ChatGroup.Guild or ChatGroup.Party or ChatGroup.Whisper
                 && _state.FindByChar(charId) is { } recipient && recipient.Key == key && CanReceive(recipient))
            SendChat(recipient, BuildCS_CHAT_ACK(charId == senderId ? group : type, senderId, sender, talk), senderId);
    }

    private ClientSession? FindByName(string name)
    {
        foreach (var s in _state.AllInGame())
            if (s.IsMain && string.Equals(s.Char!.Name, name, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    private static bool CanTalk(Character ch, ChatGroup group, byte country, byte aid)
    {
        if (ch.Country == TcontryPeace || country is TcontryPeace or TcontryN) return true;
        if (group is ChatGroup.Party or ChatGroup.Guild or ChatGroup.Force or ChatGroup.Tactics) return true;
        if (group == ChatGroup.Near && (ch.Country == TcontryB || country == TcontryB)
            && (((byte)((int)ch.PosX / 1024)) | ((byte)((int)ch.PosZ / 1024) << 8)) == 3) return true;
        return WarCountryOf(ch) == GetAttackCountry(country, aid);
    }
}
