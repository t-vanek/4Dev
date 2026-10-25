using TMap.Data;
using TMap.Protocol;
using TMap.Server.Net;

namespace TMap.Server.Map;

/// <summary>
/// The GM tools, batch S7c (C++ CSHandler.cpp:27–240 (control), 5256, 14940, 16337, 16518, 17557–17610; SSHandler.cpp:340–404, 2132,
/// 2222, 12694, 15524, 15570, 18539, 18918–19090; CSSender.cpp:8–72, 5820, 5958, 6148).
/// <list type="bullet">
/// <item><b>An operator in game</b> (a character in <c>TOPERATORTABLE</c>): moves anywhere (<c>CS_CMMOVE_REQ</c>), next to a player
/// (<c>CS_CMMOVETOUSER_REQ</c>, through the world), gives a gift of the world's gift chart (<c>CS_CMGIFT_REQ</c>) and bans a player's
/// chat for some minutes (<c>CS_CHATBAN_REQ</c>; anyone else is told "not an operator").</item>
/// <item><b>The world</b> (for the admin tool or a GM elsewhere): a gift for a player here (<c>MW_CMGIFT_REQ</c>: an item or a
/// magic item by letter, money by letter, merit, exp — the letter in any case — logged with <c>TCMGiftLog</c>), its result for a GM
/// here (<c>MW_CMGIFTRESULT_REQ</c>), a chat ban (<c>MW_CHATBAN_REQ</c>), a player's position for a GM (<c>MW_USERPOSITION_REQ</c>),
/// a line to a player (<c>MW_CHARMSG_REQ</c>), items switched on or off for drops (<c>MW_ITEMSTATE_REQ</c>) and the help messages
/// (<c>MW_HELPMESSAGE_REQ</c>).</item>
/// <item><b>A chat ban</b> holds back chat and comments with the time left (<c>SM_CHAT_BAN</c>); the world sends it on entering.</item>
/// <item><b>The help messages</b> (<c>THELPMESSAGETABLE</c>): on entering, the tutorial one in the tutorial, else the event one while
/// it runs, else the normal one; the event one starts and ends on the clock.</item>
/// <item><b>The admin tool's own link</b> (the authenticated Control server, mutual TLS, one at a time): announcements,
/// kicks, the monster spawn finder, monster actions, the castles' state and the server monitor.</item>
/// <item>Not a GM tool but with them: <c>CS_KICKOUTMAP_REQ</c> sends a player over a map's level cap back to its spawn point.</item>
/// </list>
/// <para><b>C++ bugs fixed:</b> a gift for a player not on the map never answered the GM; a chat ban's answer looked its GM up without
/// a null check (a GM gone crashed it); the move and gift commands did not check the GM was in game.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte ChatBanNotOperator = 1;                                                  // CHATBAN_RESULT
    private const byte CmGiftSuccess = 0, CmGiftTarget = 1, CmGiftId = 2, CmGiftErrPost = 4, CmGiftFail = 5;   // CMGIFT_RESULT
    private const byte CgtNone = 0, CgtItem = 1, CgtMagicItem = 2, CgtMoney = 3, CgtMerit = 4, CgtExp = 5;     // CMGIFT_TYPE
    private const byte SmChatBan = 10;
    private const byte HelpNone = 0, HelpTutorial = 1, HelpNormal = 2, HelpEvent = 3;           // HELPMESSAGE_TYPE

    public IGmStore? GmStore { get; set; }

    /// <summary>C++ <c>THELPMESSAGE</c>.</summary>
    private sealed class HelpMessage
    {
        public byte Id;
        public long Start, End;
        public string Message = "";
        public bool Notice;
    }

    private readonly SortedDictionary<byte, HelpMessage> _helpMessages = new();
    private bool _helpLoaded;

    /// <summary>C++ <c>m_bInitState</c> / <c>m_wWeight</c> set by <c>MW_ITEMSTATE_REQ</c>: an item switched on or off for drops.</summary>
    private readonly Dictionary<ushort, byte> _itemState = new();

    private byte ItemInitState(ItemTemplate t) => _itemState.TryGetValue(t.ItemId, out var v) ? v : t.InitState;
    private ushort ItemWeight(ItemTemplate t) => _itemState.TryGetValue(t.ItemId, out var v) ? v : t.Weight;

    // ================================ an operator in game ================================

    /// <summary>C++ <c>OnCS_CMMOVE_REQ</c>.</summary>
    private void OnCS_CMMOVE_REQ(ClientSession s, PacketReader r)
    {
        byte channel = r.ReadByte();
        ushort mapId = r.ReadUInt16();
        float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || !IsOperator(ch.CharId)) return;
        Teleport(s, ch, channel, mapId, x, y, z);
    }

    /// <summary>C++ <c>OnCS_CMMOVETOUSER_REQ</c> → <c>SendCT_USERPOSITION_ACK</c>: the world asks the player's map where it is, and
    /// moves the GM there (<c>MW_USERPOSITION_REQ</c> → <c>CT_USERMOVE_ACK</c>).</summary>
    private void OnCS_CMMOVETOUSER_REQ(ClientSession s, PacketReader r)
    {
        string target = r.ReadString();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || !IsOperator(ch.CharId)) return;
        var w = new PacketWriter(Msg.CT_USERPOSITION_ACK);
        w.WriteString(target); w.WriteString(ch.Name);
        _world.Send(w);
    }

    /// <summary>C++ <c>OnCS_CMGIFT_REQ</c> → <c>SendMW_CMGIFT_ACK</c>: the world checks the gift and sends it to the player's map.</summary>
    private void OnCS_CMGIFT_REQ(ClientSession s, PacketReader r)
    {
        string target = r.ReadString();
        ushort giftId = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || !IsOperator(ch.CharId)) return;
        var w = new PacketWriter(Msg.MW_CMGIFT_ACK);
        w.WriteString(target); w.WriteUInt16(giftId); w.WriteUInt32(ch.CharId);
        _world.Send(w);
    }

    /// <summary>C++ <c>OnCS_CHATBAN_REQ</c> → <c>SendMW_CHATBAN_ACK</c>.</summary>
    private void OnCS_CHATBAN_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        if (!IsOperator(ch.CharId)) { s.Send(new PacketWriter(Msg.CS_CHATBAN_ACK).WriteByte(ChatBanNotOperator)); return; }
        string name = r.ReadString();
        ushort minutes = r.ReadUInt16();
        var w = new PacketWriter(Msg.MW_CHATBAN_ACK);
        w.WriteString(name); w.WriteUInt16(minutes); w.WriteUInt32(ch.CharId); w.WriteUInt32(s.Key);
        _world.Send(w);
    }

    /// <summary>C++ <c>OnCS_KICKOUTMAP_REQ</c> + <c>CheckMapLevel</c>: a player who came through a portal with a level cap and is now
    /// over it goes back to its spawn point.</summary>
    private void OnCS_KICKOUTMAP_REQ(ClientSession s)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        if (ch.MapId == 0 || ch.Persist.LastDestination == 0) return;
        if (!_templates.Portals.TryGetValue((ushort)(ch.Persist.LastDestination & 0xFFFF), out var portal)
            || !portal.Destinations.TryGetValue((ushort)(ch.Persist.LastDestination >> 16), out var dest)) return;
        if (dest.Conditions.Any(c => c.Type == PctUpLevel && c.Id < ch.Level)) Teleport(s, ch, ch.Persist.LastSpawnId);
    }

    // ================================ the world's GM messages ================================

    /// <summary>C++ <c>OnMW_CMGIFT_REQ</c>: the gift for a player here.</summary>
    private async Task OnMW_CMGIFT_REQ(PacketReader r)
    {
        uint targetId = r.ReadUInt32();
        byte type = r.ReadByte();
        uint value = r.ReadUInt32();
        byte count = r.ReadByte();
        string title = r.ReadString(), msg = r.ReadString();
        uint gmId = r.ReadUInt32();
        byte tool = r.ReadByte();
        ushort giftId = r.ReadUInt16(), errId = r.ReadUInt16();
        byte ret = r.ReadByte();

        if (_state.FindByChar(targetId) is not { Char: { } target } ts) ret = CmGiftTarget;   // (C++ returned without an answer)
        else
        {
            Item? item = null;
            uint gold = 0, silver = 0, cooper = 0;
            byte postType = PostNormal;
            switch (type)
            {
                case CgtNone: break;
                case CgtItem:
                    if (_templates.Item((ushort)value) is not { } t) { ret = CmGiftId; break; }
                    item = new Item { TemplateId = t.ItemId, Template = t, Count = count, DuraMax = t.DuraMax, DuraCur = t.DuraMax };
                    SetEndTime(item);
                    postType = PostPackage;
                    break;
                case CgtMagicItem:
                    if (!_templates.QuestItems.TryGetValue(value, out var q) || ItemFromRow(q.Item) is not { } magic) { ret = CmGiftId; break; }
                    magic.Count = count;
                    if (q.UseTime != 0) SetEndTime(magic, q.UseTime * 86400L);
                    item = magic;
                    postType = PostPackage;
                    break;
                case CgtMoney:
                    (gold, silver, cooper) = SplitMoney(value);
                    break;
                case CgtMerit: GainPvPoint(ts, target, value, PvpeWin, PvpUseable); break;
                case CgtExp: GainExp(ts, target, value); break;
                default: ret = CmGiftFail; break;
            }
            if (ret is CmGiftSuccess or CmGiftErrPost)
            {
                await MailGift(target.CharId, target.Name, title, msg, postType, gold, silver, cooper, item);
                if (GmStore is { } db)
                {
                    ushort logged = ret == CmGiftSuccess ? giftId : errId, error = ret == CmGiftSuccess ? (ushort)0 : giftId;
                    uint gm = tool != 0 ? 0 : gmId;
                    _ = EnqueueDbWrite(() => db.GiftLogAsync(ts.UserId, target.CharId, logged, gm, error));
                }
            }
        }
        if (tool == 0 && _state.FindByChar(gmId) is { } gmSession) { gmSession.Send(new PacketWriter(Msg.CS_CMGIFT_ACK).WriteByte(ret)); return; }
        var w = new PacketWriter(Msg.MW_CMGIFTRESULT_ACK);
        w.WriteByte(ret); w.WriteByte(tool); w.WriteUInt32(gmId);
        _world.Send(w);
    }

    /// <summary>C++ <c>SendDM_POSTRECV_REQ</c> from the operator: the gift's letter, with its money or item.</summary>
    private async Task MailGift(uint charId, string name, string title, string body, byte type, uint gold, uint silver, uint cooper, Item? item)
    {
        if (PostStore is not { } db) return;
        string sender = SvrMsg(MsgNameOperator), head = NetString(title), text = NetString(body);
        long now = UnixNow();
        (int saved, uint postId, _) = await Safe(() => db.SavePostAsync(0, charId, name, sender, head, text, 0, type, gold, silver, cooper, now),
            (PostInternal, 0u, 0u));
        if (saved != 0 || postId == 0) return;
        if (item is not null)
        {
            if (item.DlId == 0) item.DlId = GenItemId();
            var row = BuildItemSave(0, item) with { StorageType = Proto.StoragePost, StorageId = postId, ItemSlot = 0 };
            await Safe(async () => { await db.SavePostItemAsync(charId, row); return 0; }, 0);
        }
        NotifyPostRecv(postId, sender, name, head, type, now);
    }

    /// <summary>C++ <c>OnMW_CMGIFTRESULT_REQ</c>: a gift's result for a GM here.</summary>
    private void OnMW_CMGIFTRESULT_REQ(PacketReader r)
    {
        byte ret = r.ReadByte();
        uint gmId = r.ReadUInt32();
        _state.FindByChar(gmId)?.Send(new PacketWriter(Msg.CS_CMGIFT_ACK).WriteByte(ret));
    }

    /// <summary>C++ <c>OnMW_CHATBAN_REQ</c>: a player's chat ban (until a time), and the answer for the GM who asked.</summary>
    private void OnMW_CHATBAN_REQ(PacketReader r)
    {
        string name = r.ReadString();
        long until = r.ReadInt64();
        byte result = r.ReadByte();
        uint gmId = r.ReadUInt32();
        r.ReadUInt32();                                                                          // dwKEY
        if (FindByName(name) is { IsMain: true, State: EnterState.InGame, Char: { } target }) target.ChatBanTime = until;
        if (gmId != 0) _state.FindByChar(gmId)?.Send(new PacketWriter(Msg.CS_CHATBAN_ACK).WriteByte(result));   // (C++: no null check)
    }

    /// <summary>C++ <c>OnMW_USERPOSITION_REQ</c>: where a player is, for the GM to be moved there.</summary>
    private void OnMW_USERPOSITION_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        string gm = r.ReadString();
        if (FindPlayer(charId, key) is not { IsMain: true, State: EnterState.InGame, Char: { } ch } s) return;
        SendCT_USERMOVE_ACK(gm, s.Channel, ch.MapId, ch.PosX, ch.PosY, ch.PosZ);
    }

    /// <summary>C++ <c>OnMW_CHARMSG_REQ</c>: an operator's line to a player.</summary>
    private void OnMW_CHARMSG_REQ(PacketReader r)
    {
        string name = r.ReadString(), msg = r.ReadString();
        FindByName(name)?.Send(BuildCS_CHAT_ACK(ChatGroup.Operator, 0, SvrMsg(MsgNameOperator), NetString(msg)));
    }

    /// <summary>C++ <c>OnMW_ITEMSTATE_REQ</c>: items switched on or off (their drop state and weight).</summary>
    private void OnMW_ITEMSTATE_REQ(PacketReader r)
    {
        r.ReadUInt32();                                                                          // dwID
        ushort count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            ushort itemId = r.ReadUInt16();
            byte state = r.ReadByte();
            if (_templates.Item(itemId) is not null) _itemState[itemId] = state;
        }
    }

    // ================================ chat bans ================================

    /// <summary>C++ <c>OnCS_CHAT_REQ</c> / <c>OnCS_COMMENT_REQ</c>'s ban check: true (and the time left told) while the chat is banned.</summary>
    private bool ChatBanned(ClientSession s, Character ch)
    {
        long now = UnixNow();
        if (now >= ch.ChatBanTime) return false;
        s.Send(new PacketWriter(Msg.CS_SYSTEMMSG_ACK).WriteByte(SmChatBan).WriteUInt32((uint)(ch.ChatBanTime - now)));
        return true;
    }

    // ================================ help messages ================================

    private void LoadHelpMessages()
    {
        if (_helpLoaded) return;
        _helpLoaded = true;
        foreach (var (id, start, end, msg) in _templates.HelpMessages)
            _helpMessages[id] = new HelpMessage { Id = id, Start = start, End = end, Message = msg };
    }

    /// <summary>C++ <c>GetHelpMessage</c>: the tutorial's in the tutorial; else the event's while it shows, else the normal one.</summary>
    private byte GetHelpMessage(Character ch, out string message)
    {
        LoadHelpMessages();
        message = "";
        if (IsTutorial(ch))
        {
            if (_helpMessages.TryGetValue(HelpTutorial, out var t)) { message = t.Message; return HelpTutorial; }
            return HelpNone;
        }
        if (_helpMessages.TryGetValue(HelpEvent, out var e) && e.Notice) { message = e.Message; return HelpEvent; }
        if (_helpMessages.TryGetValue(HelpNormal, out var n)) { message = n.Message; return HelpNormal; }
        return HelpNone;
    }

    private static void SendHelpMessage(ClientSession s, byte id, string message)
        => s.Send(new PacketWriter(Msg.CS_HELPMESSAGE_ACK).WriteByte(id).WriteString(message));

    /// <summary>C++ <c>OnMW_CHARINFO_REQ</c>'s end: the help message on entering.</summary>
    private void SendHelpOnEnter(ClientSession s, Character ch)
    {
        byte id = GetHelpMessage(ch, out var message);
        if (id != HelpNone) SendHelpMessage(s, id, message);
    }

    /// <summary>C++ <c>OnSM_TIMER_REQ</c>'s help part: the event message shows from its start and goes at its end (the normal one back).</summary>
    private void HelpMessageTick()
    {
        LoadHelpMessages();
        if (!_helpMessages.TryGetValue(HelpEvent, out var e)) return;
        long now = UnixNow();
        byte id = 0;
        string message = "";
        if (e.End != 0 && !e.Notice && e.Start <= now) { e.Notice = true; id = e.Id; message = e.Message; }
        else if (e.End < now)
        {
            id = e.Id;
            _helpMessages.Remove(HelpEvent);
            if (_helpMessages.TryGetValue(HelpNormal, out var n)) { id = n.Id; message = n.Message; }
        }
        if (id == 0) return;
        foreach (var s in _state.AllInGame())
            if (s.IsMain && s.Char is { } ch && !IsTutorial(ch)) SendHelpMessage(s, id, message);
    }

    /// <summary>C++ <c>OnMW_HELPMESSAGE_REQ</c>: a help message set by the admin tool, shown at once when it is current.</summary>
    private void OnMW_HELPMESSAGE_REQ(PacketReader r)
    {
        LoadHelpMessages();
        byte id = r.ReadByte();
        long start = r.ReadInt64(), end = r.ReadInt64();
        string message = r.ReadString();
        long now = UnixNow();
        if (!_helpMessages.TryGetValue(id, out var h)) _helpMessages[id] = h = new HelpMessage { Id = id };
        h.Start = start; h.End = end; h.Message = message;
        h.Notice = end != 0 && start <= now;
        if (end != 0 && start > now) return;
        foreach (var s in _state.AllInGame())
        {
            if (!s.IsMain || s.Char is not { } ch) continue;
            byte shown = GetHelpMessage(ch, out var text);
            if (shown == id || (shown != HelpTutorial && shown < id)) SendHelpMessage(s, shown, text);
        }
    }

    // ================================ the admin tool's link ================================

    /// <summary>The authenticated control server's session: at most one.</summary>
    private ClientSession? _control;

    /// <summary>Only the dedicated listener can supply an authenticated Control connection.</summary>
    private bool TakeControlSession(ClientConnection conn, ClientSession session)
    {
        if (!conn.IsAuthenticatedControl) return false;
        session.IsControl = true;
        if (Interlocked.CompareExchange(ref _control, session, null) is not null) { conn.Close(); return false; }
        conn.UseCrypt = false;
        _log.LogInformation("Authenticated Control server connected from {Address}.", conn.RemoteEndPoint);
        return true;
    }

    /// <summary>The control server's messages (C++ the <c>SESSION_SERVER</c> branch of <c>OnReceive</c>).</summary>
    public void DispatchControl(ClientSession s, PacketReader r)
    {
        if (!s.IsControl) return;
        switch (r.Id)
        {
            case Msg.CT_SERVICEMONITOR_ACK:
            {
                uint tick = r.ReadUInt32();
                var all = _state.AllInGame().ToList();
                var w = new PacketWriter(Msg.CT_SERVICEMONITOR_REQ);
                w.WriteUInt32(tick); w.WriteUInt32((uint)_state.ByChar.Count); w.WriteUInt32((uint)all.Count(p => p.IsMain));
                w.WriteUInt32((uint)all.Where(p => p.IsMain).Select(p => p.UserId).Distinct().Count());
                s.Send(w);
                break;
            }
            case Msg.CT_ANNOUNCEMENT_ACK:
            {
                string text = r.ReadString();
                if (text.Length > 1024) text = text[..1024];
                var msg = BuildCS_CHAT_ACK(ChatGroup.World, 0, SvrMsg(MsgNameOperator), NetString(text));
                foreach (var p in _state.AllInGame()) if (p.IsMain) p.Send(msg);
                break;
            }
            case Msg.CT_USERKICKOUT_ACK:
                if (FindByName(r.ReadString()) is { } kicked) kicked.Conn.Close();
                break;
            case Msg.CT_USERMOVE_ACK: OnCT_USERMOVE_ACK(r); break;
            case Msg.CT_MONSPAWNFIND_ACK: OnCT_MONSPAWNFIND_ACK(s, r); break;
            case Msg.CT_MONACTION_ACK: OnCT_MONACTION_ACK(r); break;
            case Msg.CT_CASTLEINFO_REQ: SendCT_CASTLEINFO_ACK(s, r.ReadUInt32()); break;
            case Msg.CT_SERVICEDATACLEAR_ACK: case Msg.CT_CTRLSVR_REQ: break;                     // nothing kept here / nothing to do
        }
    }

    /// <summary>C++ <c>OnCT_MONSPAWNFIND_ACK</c> + <c>SendCT_MONSPAWNFIND_ACK</c>: a spawn point's live monsters (or, with none, where
    /// its monsters would stand).</summary>
    private void OnCT_MONSPAWNFIND_ACK(ClientSession s, PacketReader r)
    {
        uint manager = r.ReadUInt32();
        byte channel = r.ReadByte();
        ushort mapId = r.ReadUInt16(), spawnId = r.ReadUInt16();
        if (SpawnById(spawnId) is not { } def) return;
        var live = _spawns.Where(p => p.Def.Spawn.Id == spawnId && p.Channel == channel)
            .SelectMany(p => p.Slots).Select(sl => sl.Live).Where(m => m is not null && m.MapId == mapId).Cast<Monster>().ToList();
        var w = new PacketWriter(Msg.CT_MONSPAWNFIND_ACK);
        w.WriteUInt32(manager); w.WriteUInt16(mapId); w.WriteUInt16(spawnId);
        if (live.Count > 0)
        {
            w.WriteByte((byte)live.Count);
            foreach (var m in live) { w.WriteUInt32(m.Id); w.WriteUInt32(m.HostId); w.WriteByte(m.Status); w.WriteFloat(m.PosX); w.WriteFloat(m.PosY); w.WriteFloat(m.PosZ); }
        }
        else if (def.Spawn.Count != 0)
        {
            w.WriteByte(def.Spawn.Count);
            for (int i = 0; i < def.Spawn.Count; i++) { w.WriteUInt32(0); w.WriteUInt32(0); w.WriteByte(0); w.WriteFloat(def.Spawn.PosX); w.WriteFloat(def.Spawn.PosY); w.WriteFloat(def.Spawn.PosZ); }
        }
        else return;
        s.Send(w);
    }

    /// <summary>C++ <c>OnCT_MONACTION_ACK</c>: a monster made to act (or die); with none, its spawn point filled for good.</summary>
    private void OnCT_MONACTION_ACK(PacketReader r)
    {
        byte channel = r.ReadByte();
        ushort mapId = r.ReadUInt16();
        uint monId = r.ReadUInt32();
        byte action = r.ReadByte();
        uint triggerId = r.ReadUInt32(), hostId = r.ReadUInt32(), rhId = r.ReadUInt32();
        byte rhType = r.ReadByte();
        ushort spawnId = r.ReadUInt16();
        if (_state.FindMonster(monId) is not { } mon || mon.Channel != channel || mon.MapId != mapId)
        {
            if (spawnId != 0) AddTimelimitedMon(spawnId, channel, 0, NowMs, TcontryN);
            return;
        }
        if (action == AtDead) { mon.Hp = 0; OnMonsterDeath(mon); }
        else OnAiEvent(mon, (AiTrigger)action, triggerId, hostId, rhId, rhType);
    }

    /// <summary>C++ <c>SendCT_CASTLEINFO_ACK</c>: every castle's state.</summary>
    private void SendCT_CASTLEINFO_ACK(ClientSession s, uint manager)
    {
        var w = new PacketWriter(Msg.CT_CASTLEINFO_ACK);
        w.WriteUInt32(manager); w.WriteUInt16((ushort)_castles.Count);
        foreach (var c in _castles)
        {
            w.WriteUInt16(c.Id); w.WriteString(c.Zone.Name); w.WriteByte(c.Status); w.WriteUInt32(c.DefGuildId); w.WriteString(c.DefName);
            w.WriteByte(c.Country); w.WriteUInt32(c.AtkGuildId); w.WriteString(c.AtkName); w.WriteInt64(c.NextDefend);
        }
        s.Send(w);
    }
}
