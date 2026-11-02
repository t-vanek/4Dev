using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>Someone on the protected list (C++ <c>TPROTECTED</c>): its name and what is refused from it.</summary>
public sealed class ProtectedEntry
{
    public string Name = "";
    public byte Option;
}

/// <summary>
/// Social, batch S1 — the protected (block) list, and the map's side of friends and the messenger (C++ CSHandler.cpp:6961–7060,
/// 8441–8640, 10490–10570, 15185; SSHandler.cpp:10083–10391, 11962–12046, 12726–12820):
/// <list type="bullet">
/// <item><b>The protected list</b> is the map's own: read on entering and sent after the character info
/// (<c>CS_PROTECTEDLIST_ACK</c>); a name is added (at most 200, refusing chat at first) or taken off through the database, and the
/// world is told so the friendship shows offline (and is broken when added). Each entry refuses what its option says — chat
/// (every <c>CS_CHAT_ACK</c> from it), invites (party, guild, messenger), trades and duels (<see cref="CheckProtected"/>).</item>
/// <item><b>Friends</b> live in the world: the map passes the requests on (<c>MW_FRIEND*_ACK</c>) and the answers back
/// (<c>MW_FRIEND*_REQ</c> → <c>CS_FRIEND*_ACK</c>). A friend request to or from someone blocked is refused.</item>
/// <item><b>The messenger</b> (<c>TMS</c>, group chat) lives in the world too; the map only answers whether an invitee blocks the
/// one asking.</item>
/// </list>
/// <para><b>Fixed C++ bug:</b> a friend group renamed with too long a name was refused but still passed on. An option change is
/// saved at once (the C++ saves the changed ones at logout). <b>Not ported:</b> the security-code lock on erasing a friend or a
/// group (as for the guild).</para>
/// </summary>
public sealed partial class MapService
{
    private const byte ProtectedSuccess = 0, ProtectedNotFound = 1, ProtectedFail = 3;     // PROTECTED_RESULT
    private const byte ProtectedChat = 1, ProtectedInvite = 2, ProtectedDeal = 4, ProtectedDuel = 8;
    private const byte FriendRefuse = 1, FriendNotFound = 3;                                // FRIEND_RESULT
    private const byte FriendConnection = 0, FriendDisconnection = 1;
    private const int MaxProtected = 200;

    /// <summary>The protected-list store — the game database in production, a fake in tests.</summary>
    public IProtectedStore? ProtectedStore { get; set; }

    /// <summary>C++ <c>CTPlayer::CheckProtected</c> (TPlayer.cpp:5472): false when <paramref name="target"/> is on the list for
    /// <paramref name="option"/>.</summary>
    private static bool CheckProtected(Character ch, uint target, byte option)
        => !(ch.Protected.TryGetValue(target, out var p) && (p.Option & option) != 0);

    private static bool HasProtectedName(Character ch, string name) => ch.Protected.Values.Any(p => p.Name == name);

    /// <summary>C++ <c>OnDM_LOADCHAR_REQ</c>'s <c>CTBLProtectedTable</c>: read once, when the character first enters.</summary>
    private async Task LoadProtected(Character ch)
    {
        if (ch.ProtectedLoaded || ProtectedStore is not { } db) return;
        var rows = await Safe(() => db.LoadProtectedAsync(ch.CharId), new List<ProtectedRow>());
        ch.Protected.Clear();
        foreach (var p in rows) ch.Protected[p.CharId] = new ProtectedEntry { Name = p.Name, Option = p.Option };
        ch.ProtectedLoaded = true;
    }

    /// <summary>C++ <c>SendCS_PROTECTEDLIST_ACK</c> (CSSender.cpp:3557).</summary>
    private static void SendCS_PROTECTEDLIST_ACK(ClientSession s, Character ch)
    {
        var w = new PacketWriter(Msg.CS_PROTECTEDLIST_ACK);
        var list = ch.Protected.OrderBy(p => p.Key).ToList();
        w.WriteByte((byte)list.Count);
        foreach (var (id, p) in list) { w.WriteUInt32(id); w.WriteString(p.Name); w.WriteByte(p.Option); }
        s.Send(w);
    }

    private static void SendCS_PROTECTEDADD_ACK(ClientSession s, byte result, uint charId, string name, byte option)
    {
        var w = new PacketWriter(Msg.CS_PROTECTEDADD_ACK);
        w.WriteByte(result); w.WriteUInt32(charId); w.WriteString(name); w.WriteByte(option);
        s.Send(w);
    }

    private static void SendCS_PROTECTEDERASE_ACK(ClientSession s, byte result, string name)
    {
        var w = new PacketWriter(Msg.CS_PROTECTEDERASE_ACK);
        w.WriteByte(result); w.WriteString(name);
        s.Send(w);
    }

    private static void SendCS_FRIENDADD_ACK(ClientSession s, byte result, string name)
    {
        var w = new PacketWriter(Msg.CS_FRIENDADD_ACK);
        w.WriteByte(result); w.WriteUInt32(0); w.WriteString(name); w.WriteByte(0); w.WriteByte(0); w.WriteByte(0); w.WriteUInt32(0);
        s.Send(w);
    }

    // ================================ the protected list ================================

    /// <summary>C++ <c>OnCS_PROTECTEDADD_REQ</c> (CSHandler.cpp:10490) and <c>OnDM_PROTECTEDADD_ACK</c> (SSHandler.cpp:12766).</summary>
    private async Task OnCS_PROTECTEDADD_REQ(ClientSession s, PacketReader r)
    {
        string name = r.ReadString();
        if (!s.IsMain || s.Char is not { } ch) return;
        if (ch.Name == name || name.Length == 0 || Cp949Length(name) > MaxName || ch.Protected.Count > MaxProtected)
        {
            SendCS_PROTECTEDADD_ACK(s, ProtectedFail, 0, name, 0);
            return;
        }
        byte option = ProtectedChat;
        var (result, id) = ProtectedStore is { } db ? await Safe(() => db.ProtectedInsertAsync(ch.CharId, name, option), (1, 0u)) : (1, 0u);
        if (s.Char != ch || !s.IsMain) return;
        if (result == 0)
        {
            ch.Protected[id] = new ProtectedEntry { Name = name, Option = option };
            SendToWorld(Msg.MW_PROTECTEDCHECK_ACK, s, w => { w.WriteByte(FriendDisconnection); w.WriteString(name); });
        }
        SendCS_PROTECTEDADD_ACK(s, (byte)result, id, name, option);
    }

    /// <summary>C++ <c>OnCS_PROTECTEDERASE_REQ</c> (CSHandler.cpp:10522).</summary>
    private void OnCS_PROTECTEDERASE_REQ(ClientSession s, PacketReader r)
    {
        string name = r.ReadString();
        if (!s.IsMain || s.Char is not { } ch) return;
        if (ch.Name == name || name.Length == 0 || Cp949Length(name) > MaxName) { SendCS_PROTECTEDERASE_ACK(s, ProtectedFail, name); return; }
        var found = ch.Protected.FirstOrDefault(p => p.Value.Name == name);
        if (found.Value is null) { SendCS_PROTECTEDERASE_ACK(s, ProtectedNotFound, name); return; }
        ch.Protected.Remove(found.Key);
        SendCS_PROTECTEDERASE_ACK(s, ProtectedSuccess, name);
        SendToWorld(Msg.MW_PROTECTEDCHECK_ACK, s, w => { w.WriteByte(FriendConnection); w.WriteString(name); });
        if (ProtectedStore is { } db) _ = EnqueueDbWrite(() => db.ProtectedDeleteAsync(ch.CharId, found.Key));
    }

    /// <summary>C++ <c>OnCS_PROTECTEDOPTION_REQ</c> (CSHandler.cpp:15185): what is refused from someone on the list.</summary>
    private void OnCS_PROTECTEDOPTION_REQ(ClientSession s, PacketReader r)
    {
        uint target = r.ReadUInt32();
        byte option = r.ReadByte();
        if (!s.IsMain || s.Char is not { } ch || !ch.Protected.TryGetValue(target, out var p)) return;
        p.Option = option;
        if (ProtectedStore is { } db) _ = EnqueueDbWrite(() => db.SaveProtectedOptionAsync(ch.CharId, target, option));
    }

    // ================================ friends ================================

    /// <summary>C++ <c>OnCS_FRIENDLIST_REQ</c> (CSHandler.cpp:6961).</summary>
    private void OnCS_FRIENDLIST_REQ(ClientSession s)
    {
        if (s.IsMain && s.Char is not null) SendToWorld(Msg.MW_FRIENDLIST_ACK, s);
    }

    /// <summary>C++ <c>OnCS_FRIENDASK_REQ</c> (CSHandler.cpp:6977): someone blocked is refused here.</summary>
    private void OnCS_FRIENDASK_REQ(ClientSession s, PacketReader r)
    {
        string target = r.ReadString();
        if (!s.IsMain || s.Char is not { } ch || ch.Name == target) return;
        if (HasProtectedName(ch, target)) { SendCS_FRIENDADD_ACK(s, FriendRefuse, target); return; }
        SendToWorld(Msg.MW_FRIENDASK_ACK, s, w => w.WriteString(target));
    }

    /// <summary>C++ <c>OnCS_FRIENDREPLY_REQ</c> (CSHandler.cpp:7012).</summary>
    private void OnCS_FRIENDREPLY_REQ(ClientSession s, PacketReader r)
    {
        string inviter = r.ReadString();
        byte reply = r.ReadByte();
        if (s.IsMain && s.Char is not null) SendToWorld(Msg.MW_FRIENDREPLY_ACK, s, w => { w.WriteString(inviter); w.WriteByte(reply); });
    }

    /// <summary>C++ <c>OnCS_FRIENDERASE_REQ</c> (CSHandler.cpp:7036).</summary>
    private void OnCS_FRIENDERASE_REQ(ClientSession s, PacketReader r)
    {
        uint target = r.ReadUInt32();
        if (s.IsMain && s.Char is not null) SendToWorld(Msg.MW_FRIENDERASE_ACK, s, w => w.WriteUInt32(target));
    }

    /// <summary>C++ <c>OnCS_FRIENDGROUPMAKE_REQ</c> (CSHandler.cpp:8441).</summary>
    private void OnCS_FRIENDGROUPMAKE_REQ(ClientSession s, PacketReader r)
    {
        byte group = r.ReadByte();
        string name = r.ReadString();
        if (!s.IsMain || s.Char is null) return;
        if (Cp949Length(name) > MaxName) { SendFriendGroupAck(s, Msg.CS_FRIENDGROUPMAKE_ACK, FriendNotFound, group, name); return; }
        SendToWorld(Msg.MW_FRIENDGROUPMAKE_ACK, s, w => { w.WriteByte(group); w.WriteString(name); });
    }

    /// <summary>C++ <c>OnCS_FRIENDGROUPDELETE_REQ</c> (CSHandler.cpp:8471).</summary>
    private void OnCS_FRIENDGROUPDELETE_REQ(ClientSession s, PacketReader r)
    {
        byte group = r.ReadByte();
        if (s.IsMain && s.Char is not null) SendToWorld(Msg.MW_FRIENDGROUPDELETE_ACK, s, w => w.WriteByte(group));
    }

    /// <summary>C++ <c>OnCS_FRIENDGROUPCHANGE_REQ</c> (CSHandler.cpp:8513): a friend moved into a group (0 = none).</summary>
    private void OnCS_FRIENDGROUPCHANGE_REQ(ClientSession s, PacketReader r)
    {
        uint friend = r.ReadUInt32();
        byte group = r.ReadByte();
        if (s.IsMain && s.Char is not null) SendToWorld(Msg.MW_FRIENDGROUPCHANGE_ACK, s, w => { w.WriteUInt32(friend); w.WriteByte(group); });
    }

    /// <summary>C++ <c>OnCS_FRIENDGROUPNAME_REQ</c> (CSHandler.cpp:8533).</summary>
    private void OnCS_FRIENDGROUPNAME_REQ(ClientSession s, PacketReader r)
    {
        byte group = r.ReadByte();
        string name = r.ReadString();
        if (!s.IsMain || s.Char is null) return;
        if (Cp949Length(name) > MaxName) { SendFriendGroupAck(s, Msg.CS_FRIENDGROUPNAME_ACK, FriendNotFound, group, name); return; }
        SendToWorld(Msg.MW_FRIENDGROUPNAME_ACK, s, w => { w.WriteByte(group); w.WriteString(name); });
    }

    private static void SendFriendGroupAck(ClientSession s, ushort id, byte result, byte group, string name)
    {
        var w = new PacketWriter(id);
        w.WriteByte(result); w.WriteByte(group); w.WriteString(name);
        s.Send(w);
    }

    /// <summary>C++ <c>OnMW_FRIENDASK_REQ</c> (SSHandler.cpp:10256): the one asked is asked — unless it blocks the one asking, who
    /// the world then refuses.</summary>
    private void OnMW_FRIENDASK_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        string inviter = r.ReadString();
        r.ReadUInt32();                                   // dwInviter
        if (FindPlayer(charId, key) is not { Char: { } ch } s) return;
        if (HasProtectedName(ch, inviter))
        {
            var w = new PacketWriter(Msg.MW_FRIENDPROTECTEDASK_ACK);
            w.WriteUInt32(charId); w.WriteUInt32(key); w.WriteString(inviter);
            _world.Send(w);
            return;
        }
        s.Send(new PacketWriter(Msg.CS_FRIENDASK_ACK).WriteString(inviter));
    }

    // ================================ the messenger ================================

    /// <summary>C++ <c>OnCS_TMSSEND_REQ</c> (CSHandler.cpp:8562).</summary>
    private void OnCS_TMSSEND_REQ(ClientSession s, PacketReader r)
    {
        uint tms = r.ReadUInt32();
        string message = r.ReadString();
        if (s.State == EnterState.InGame && s.IsMain && s.Char is not null) SendToWorld(Msg.MW_TMSSEND_ACK, s, w => { w.WriteUInt32(tms); w.WriteString(message); });
    }

    /// <summary>C++ <c>OnCS_TMSINVITE_REQ</c> (CSHandler.cpp:8583): a new conversation (0) or more people into one.</summary>
    private void OnCS_TMSINVITE_REQ(ClientSession s, PacketReader r)
    {
        uint tms = r.ReadUInt32();
        byte count = r.ReadByte();
        var targets = new uint[count];
        for (int i = 0; i < count; i++) targets[i] = r.ReadUInt32();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is null) return;
        SendToWorld(Msg.MW_TMSINVITE_ACK, s, w =>
        {
            w.WriteUInt32(tms); w.WriteByte(count);
            foreach (uint t in targets) w.WriteUInt32(t);
        });
    }

    /// <summary>C++ <c>OnCS_TMSOUT_REQ</c> (CSHandler.cpp:8617).</summary>
    private void OnCS_TMSOUT_REQ(ClientSession s, PacketReader r)
    {
        uint tms = r.ReadUInt32();
        if (s.State == EnterState.InGame && s.IsMain && s.Char is not null) SendToWorld(Msg.MW_TMSOUT_ACK, s, w => w.WriteUInt32(tms));
    }

    /// <summary>C++ <c>OnMW_TMSINVITEASK_REQ</c> (SSHandler.cpp:11983): may the one who left a conversation be brought back in —
    /// not if it blocks the sender's invites (or is gone).</summary>
    private void OnMW_TMSINVITEASK_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        uint targetId = r.ReadUInt32(), targetKey = r.ReadUInt32();
        uint tms = r.ReadUInt32();
        string message = r.ReadString();
        bool ok = FindPlayer(charId, key) is { State: EnterState.InGame, IsMain: true, Char: { } ch } && CheckProtected(ch, targetId, ProtectedInvite);
        var w = new PacketWriter(Msg.MW_TMSINVITEASK_ACK);
        w.WriteUInt32(targetId); w.WriteUInt32(targetKey); w.WriteUInt32(charId); w.WriteUInt32(key);
        w.WriteByte(ok ? (byte)1 : (byte)0); w.WriteUInt32(tms); w.WriteString(message);
        _world.Send(w);
    }
}
