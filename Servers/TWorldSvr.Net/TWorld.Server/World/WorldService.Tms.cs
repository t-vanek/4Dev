using TWorld.Protocol;
using TWorld.Server.Net;

namespace TWorld.Server.World;

/// <summary>
/// Phase 5i — TMS, the multi-person private-chat ("whisper conversation") subsystem, ported from
/// <c>SSHandler.cpp</c> (OnMW_TMSSEND/TMSINVITEASK/TMSINVITE/TMSOUT) + <c>SSSender.cpp</c>. A conversation
/// (<see cref="Tms"/>) holds a member set; invites require the same war-country; a fresh 1:1 invite to
/// someone who already opened an empty conversation toward you merges into it. The "no receiver" system
/// message uses the loaded server-message table. Reopening a conversation correlates the Map answer
/// with a pending server-owned message; duplicate target IDs retain native set semantics.
/// </summary>
public sealed partial class WorldService
{
    private sealed record TmsPrompt(Character Sender, Character Target, string Message);
    private readonly Dictionary<uint, List<TmsPrompt>> _tmsPrompts = new();

    private Character? TmsActor(ServerSession session, uint id, uint key)
        => _state.FindChar(id, key) is { Logout: false } ch && ch.MainId == session.ServerId && session.ServerType == 4 ? ch : null;

    private bool TmsOnline(Character ch)
        => !ch.Logout && _state.Characters.TryGetValue(ch.CharId, out var live) && ReferenceEquals(live, ch)
           && _state.FindMapSvr(ch.MainId) is { ServerType: 4 };

    private bool DispatchTms(ServerSession session, PacketReader r)
    {
        switch (r.Id)
        {
            case Msg.MW_TMSSEND_ACK: OnMW_TMSSEND_ACK(session, r); return true;
            case Msg.MW_TMSINVITEASK_ACK: OnMW_TMSINVITEASK_ACK(session, r); return true;
            case Msg.MW_TMSINVITE_ACK: OnMW_TMSINVITE_ACK(session, r); return true;
            case Msg.MW_TMSOUT_ACK: OnMW_TMSOUT_ACK(session, r); return true;
        }
        return false;
    }

    /// <summary>Send a message into a conversation. A still-one-member conversation first prompts the saved
    /// peer to join (TMSINVITEASK); otherwise the message is delivered to all members. C++ OnMW_TMSSEND_ACK.</summary>
    private void OnMW_TMSSEND_ACK(ServerSession session, PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        uint tmsId = r.ReadUInt32();
        string message = r.ReadString();

        var ch = TmsActor(session, charId, key);
        if (ch is null || !ch.TmsIds.Contains(tmsId)) return;
        if (!_state.TmsMap.TryGetValue(tmsId, out var tms) || !tms.Members.TryGetValue(charId, out var member) || !ReferenceEquals(member, ch)) return;

        if (tms.Members.Count == 1)
        {
            var target = FindByName(tms.LastMember);
            if (target is null || !TmsOnline(target) || GetWarCountry(ch) != GetWarCountry(target)) message = NoReceiverMessage();
            else
            {
                if (!_tmsPrompts.TryGetValue(tmsId, out var prompts)) _tmsPrompts[tmsId] = prompts = new();
                prompts.Add(new(ch, target, message));
                var w = new PacketWriter(Msg.MW_TMSINVITEASK_REQ);
                w.WriteUInt32(target.CharId); w.WriteUInt32(target.Key);
                w.WriteUInt32(ch.CharId); w.WriteUInt32(ch.Key); w.WriteUInt32(tmsId); w.WriteString(message);
                SendToChar(target, w.ToArray());
                return;
            }
        }

        foreach (var m in tms.Members.Values.OrderBy(c => c.CharId))
            SendToChar(m, BuildTmsRecv(m, tmsId, ch.Name, message));
    }

    /// <summary>The prompted peer accepted (or declined) the invite. On accept, add them and notify all members
    /// (TMSINVITE); then relay the original message to everyone. C++ OnMW_TMSINVITEASK_ACK.</summary>
    private void OnMW_TMSINVITEASK_ACK(ServerSession session, PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        uint targetId = r.ReadUInt32();
        uint targetKey = r.ReadUInt32();
        byte result = r.ReadByte();
        uint tmsId = r.ReadUInt32();
        string message = r.ReadString();

        var ch = _state.FindChar(charId, key);
        if (ch is null || !ch.TmsIds.Contains(tmsId)) return;
        if (!_state.TmsMap.TryGetValue(tmsId, out var tms)) return;

        var target = _state.FindChar(targetId, targetKey);
        // This answer comes from the prompted target's main, not from the original sender's Map.
        if (target is null || TmsActor(session, targetId, targetKey) != target
            || !_tmsPrompts.TryGetValue(tmsId, out var prompts)) return;
        int pending = prompts.FindIndex(p => ReferenceEquals(p.Sender, ch) && ReferenceEquals(p.Target, target) && p.Message == message);
        if (pending < 0) return;
        message = prompts[pending].Message;
        prompts.RemoveAt(pending);
        if (prompts.Count == 0) _tmsPrompts.Remove(tmsId);
        if (!TmsOnline(ch) || !tms.Members.TryGetValue(ch.CharId, out var member) || !ReferenceEquals(member, ch)) return;
        if (GetWarCountry(ch) != GetWarCountry(target)) result = 0;
        if (tms.Members.Count == byte.MaxValue && !tms.Members.ContainsKey(targetId)) result = 0;
        if (target is not null && result != 0)
        {
            tms.Members[target.CharId] = target;
            target.TmsIds.Add(tms.Id);
            // fixed C++ bug: it sent (target id, member key, member id), so only the one brought back heard — every member is
            // now shown the new roster, the sender as inviter
            foreach (var m in tms.Members.Values.OrderBy(c => c.CharId))
                SendToChar(m, BuildTmsInvite(m.CharId, m.Key, ch.CharId, tms));
        }
        else
        {
            message = NoReceiverMessage();
        }

        foreach (var m in tms.Members.Values.OrderBy(c => c.CharId))
            SendToChar(m, BuildTmsRecv(m, tmsId, ch.Name, message));
    }

    /// <summary>Invite one or more chars into a (new or existing) conversation — same war-country only.
    /// A 1:1 invite to someone who already opened an empty conversation toward me merges into it.
    /// C++ OnMW_TMSINVITE_ACK.</summary>
    private void OnMW_TMSINVITE_ACK(ServerSession session, PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        uint tmsId = r.ReadUInt32();
        byte count = r.ReadByte();

        var ch = TmsActor(session, charId, key);
        if (ch is null || count == 0) { for (byte i = 0; i < count; i++) r.ReadUInt32(); return; }

        Tms? tms = null;
        if (tmsId != 0)
        {
            if (!ch.TmsIds.Contains(tmsId) || !_state.TmsMap.TryGetValue(tmsId, out tms)
                || !tms.Members.TryGetValue(charId, out var current) || !ReferenceEquals(current, ch))
            { for (byte i = 0; i < count; i++) r.ReadUInt32(); return; }
        }

        var targets = new SortedDictionary<uint, Character>();
        for (byte i = 0; i < count; i++)
        {
            uint targetId = r.ReadUInt32();
            if (!_state.Characters.TryGetValue(targetId, out var t)) continue;
            if (!TmsOnline(t) || GetWarCountry(ch) != GetWarCountry(t)) continue;
            targets[t.CharId] = t;
        }
        if (targets.Count == 0) return;

        // 1:1 to someone who already opened an empty conversation toward me -> join theirs.
        if (tms is null && targets.Count == 1)
        {
            var target = targets.Values.First();
            foreach (var existingId in target.TmsIds)
            {
                if (!_state.TmsMap.TryGetValue(existingId, out var existing)) continue;
                if (existing.Members.Count == 1 && string.Equals(existing.LastMember, ch.Name, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Members[ch.CharId] = ch;
                    ch.TmsIds.Add(existing.Id);
                    SendToChar(ch, BuildTmsInvite(ch.CharId, ch.Key, ch.CharId, existing));
                    return;
                }
            }
        }

        // The roster count is one byte on the wire. Refuse an unrepresentable roster atomically.
        int currentCount = tms?.Members.Count ?? 1;
        if (currentCount + targets.Keys.Count(id => tms is null ? id != ch.CharId : !tms.Members.ContainsKey(id)) > byte.MaxValue) return;

        if (tms is null)
        {
            tms = new Tms { Id = ++_state.TmsIndex };
            tms.Members[ch.CharId] = ch;
            ch.TmsIds.Add(tms.Id);
            _state.TmsMap[tms.Id] = tms;
        }

        foreach (var t in targets.Values)
        {
            tms.Members[t.CharId] = t;
            t.TmsIds.Add(tms.Id);
        }

        foreach (var m in tms.Members.Values.OrderBy(c => c.CharId))
            SendToChar(m, BuildTmsInvite(m.CharId, m.Key, ch.CharId, tms));
    }

    /// <summary>Leave a conversation: notify all members, remove the leaver, and dissolve an empty conversation.
    /// C++ OnMW_TMSOUT_ACK.</summary>
    private void OnMW_TMSOUT_ACK(ServerSession session, PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        uint tmsId = r.ReadUInt32();

        var ch = TmsActor(session, charId, key);
        if (ch is null || !ch.TmsIds.Contains(tmsId)) return;
        if (!_state.TmsMap.TryGetValue(tmsId, out var tms) || !tms.Members.TryGetValue(charId, out var member) || !ReferenceEquals(member, ch)) return;

        foreach (var m in tms.Members.Values.OrderBy(c => c.CharId))
        {
            var w = new PacketWriter(Msg.MW_TMSOUT_REQ);
            w.WriteUInt32(m.CharId); w.WriteUInt32(m.Key); w.WriteUInt32(tmsId); w.WriteString(ch.Name);
            SendToChar(m, w.ToArray());
        }

        tms.Members.Remove(charId);
        _tmsPrompts.Remove(tmsId);
        tms.LastMember = ch.Name;
        ch.TmsIds.Remove(tmsId);
        if (tms.Members.Count == 0) _state.TmsMap.Remove(tmsId);
    }

    /// <summary>Cancel outstanding prompts even if this peer has not joined any conversation yet.</summary>
    private void CancelTmsPrompts(Character ch)
    {
        // A prompted peer can disconnect without belonging to the conversation yet.
        foreach (var (id, prompts) in _tmsPrompts.ToArray())
        {
            foreach (var pending in prompts.Where(p => ReferenceEquals(p.Target, ch)))
                if (TmsOnline(pending.Sender) && _state.TmsMap.TryGetValue(id, out var tms)
                    && tms.Members.ContainsKey(pending.Sender.CharId))
                    SendToChar(pending.Sender, BuildTmsRecv(pending.Sender, id, pending.Sender.Name, NoReceiverMessage()));
            prompts.RemoveAll(p => ReferenceEquals(p.Target, ch) || ReferenceEquals(p.Sender, ch));
            if (prompts.Count == 0) _tmsPrompts.Remove(id);
        }
    }

    /// <summary>C++ LeaveTMS: preserve the earlier C# logout notifications and LastMember tracking.</summary>
    private void TmsLeaveAll(Character ch)
    {
        foreach (var tmsId in ch.TmsIds.ToList())
        {
            _tmsPrompts.Remove(tmsId);
            if (_state.TmsMap.TryGetValue(tmsId, out var tms))
            {
                tms.LastMember = ch.Name;
                tms.Members.Remove(ch.CharId);
                foreach (var m in tms.Members.Values.OrderBy(c => c.CharId))
                {
                    var w = new PacketWriter(Msg.MW_TMSOUT_REQ);
                    w.WriteUInt32(m.CharId); w.WriteUInt32(m.Key); w.WriteUInt32(tmsId); w.WriteString(ch.Name);
                    SendToChar(m, w.ToArray());
                }
                if (tms.Members.Count == 0) _state.TmsMap.Remove(tmsId);
            }
        }
        ch.TmsIds.Clear();
    }

    // ===== senders / helpers =====

    private static byte[] BuildTmsRecv(Character to, uint tmsId, string sender, string message)
    {
        var w = new PacketWriter(Msg.MW_TMSRECV_REQ);
        w.WriteUInt32(to.CharId); w.WriteUInt32(to.Key); w.WriteUInt32(tmsId); w.WriteString(sender); w.WriteString(message);
        return w.ToArray();
    }

    /// <summary>MW_TMSINVITE_REQ — recipient char/key, the inviter, the tms id, then the member roster.</summary>
    private static byte[] BuildTmsInvite(uint charId, uint key, uint inviter, Tms tms)
    {
        var w = new PacketWriter(Msg.MW_TMSINVITE_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key); w.WriteUInt32(inviter); w.WriteUInt32(tms.Id);
        w.WriteByte((byte)tms.Members.Count);
        foreach (var m in tms.Members.Values.OrderBy(c => c.CharId))
        {
            w.WriteUInt32(m.CharId); w.WriteString(m.Name); w.WriteByte(m.Class); w.WriteByte(m.Level);
        }
        return w.ToArray();
    }

    private Character? FindByName(string name)
        => _state.CharactersByName.TryGetValue(name, out var c) ? c : null;

    /// <summary>C++ BuildNetString(header, body) = 4-hex header length + 4-hex body length + header + body.</summary>
    private static string BuildNetString(string header, string body)
        => $"{header.Length:X4}{body.Length:X4}{header}{body}";

    /// <summary>The "no receiver" system message (CTBLSvrMsg / TMS_NORECEIVER), wrapped as a net string.
    /// Empty body if the message table wasn't loaded.</summary>
    private string NoReceiverMessage() => BuildNetString("", _state.GetSvrMsg((uint)SvrMsg.TmsNoReceiver));
}
