using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The client-enter handshake — the C# port of the C++ connect/session flow spread across
/// <c>CSHandler.cpp</c> (client side) and <c>SSHandler.cpp</c> (map↔world side). The sequence:
/// <code>
/// client CS_CONNECT_REQ                      → map SendMW_ADDCHAR_ACK
/// world  MW_ENTERSVR_REQ   → map SendMW_ENTERSVR_ACK  (loads char from DB here, DB-manager role in-process)
/// world  MW_CHARDATA_REQ   → map SendMW_CHARDATA_ACK
/// world  MW_CHARINFO_REQ   → map SendCS_CHARINFO_ACK  (the client gets its char here)
/// world  MW_ROUTE_REQ      → map SendMW_ROUTE_ACK
/// world  MW_ENTERCHAR_REQ  → map SendMW_ENTERCHAR_ACK
/// world  MW_CHECKMAIN_REQ  → map SendMW_CHECKMAIN_ACK
/// world  MW_CONRESULT_REQ  → map SendCS_CONNECT_ACK   (the "enter granted" signal)
/// client CS_CONREADY_REQ                     → place player into cell, broadcast CS_ENTER_ACK
/// </code>
/// The map does not control the order — the world drives it — so each MW handler responds independently.
/// </summary>
public sealed partial class MapService
{
    // Default spawn used when a character cannot be loaded from the DB (DB-free operation). Matches the
    // fresh-newbie spawn observed against the deployed stack (map 0, ~(3663, 0, 557), dir 762).
    private const float SpawnX = 3663f, SpawnY = 0f, SpawnZ = 557f;
    private const ushort SpawnDir = 762;

    // ---- startup: announce this map server to the world ----

    private void SendMW_CONNECT_ACK()
    {
        var w = new PacketWriter(Msg.MW_CONNECT_ACK);
        w.WriteUInt16(Proto.MakeServerId(_opt.ServerId, SvrType.Map)); // MAKEWORD(serverId, SVRGRP_MAPSVR)
        w.WriteByte((byte)_opt.Channels.Length);
        foreach (var ch in _opt.Channels) w.WriteByte(ch);
        _world.Send(w);
    }

    // ---- client's first packet ----

    private async Task OnCS_CONNECT_REQ(ClientSession s, PacketReader r)
    {
        ushort version = r.ReadUInt16();
        byte channel = r.ReadByte();
        uint userId = r.ReadUInt32();
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        uint ip = r.ReadUInt32();
        ushort port = r.ReadUInt16();
        long checksum = r.ReadInt64();

        if (version != Proto.ClientVersion)
        {
            SendCS_CONNECT_ACK(s, ConnectResult.InvalidVer, null);
            _log.LogWarning("CONNECT rejected (bad version 0x{V:X4}) for char {Char}.", version, charId);
            return;
        }

        long expected = Proto.ComputeConnectChecksum(version, userId, key, charId);
        if (checksum != expected)
        {
            // The C++ silently drops (no ACK) on checksum mismatch.
            _log.LogWarning("CONNECT rejected (bad checksum) for char {Char}.", charId);
            s.Conn.Close();
            return;
        }

        if (s.State != EnterState.Connected || s.CharId != 0)
        {
            _log.LogWarning("CONNECT for char {New} on a session already bound to {Old}.", charId, s.CharId);
            return;
        }

        var entry = new MapEntry(userId, charId, key, _opt.GroupId, channel,
            EntryIp(ip), port);
        if (!await ValidateEntryAsync(entry, enter: false))
        {
            SendCS_CONNECT_ACK(s, ConnectResult.Internal, null);
            s.Conn.Close();
            return;
        }

        // Reject a duplicate of a char already live on this map (the C++ CloseSession/suspend dance).
        if (_state.FindByChar(charId) is { } existing && !ReferenceEquals(existing, s))
        {
            SendCS_CONNECT_ACK(s, ConnectResult.AlreadyExist, null);
            s.Conn.Close();
            return;
        }

        s.Version = version;
        s.Channel = channel;
        s.UserId = userId;
        s.CharId = charId;
        s.Key = key;
        s.ClientIp = ip;
        s.ClientPort = port;
        s.State = EnterState.Entering;
        _state.Register(s);

        // Resolve the character up front (DB or synthesized) so the handshake ACKs carry real data.
        try { await EnsureCharAsync(s); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Character load failed for {Char}; entry denied.", charId);
            SendCS_CONNECT_ACK(s, ConnectResult.Internal, null);
            s.Conn.Close();
            _state.Remove(s);
            return;
        }

        if (!await LoadSecurityCode(s)) return;
        if (!NormalizeMeetingEntry(s)) return;

        SendMW_ADDCHAR_ACK(s);
        _log.LogInformation("Char {Char} (user {User}, ch {Ch}) entering; announced to world.", charId, userId, channel);
    }

    private void SendMW_ADDCHAR_ACK(ClientSession s)
    {
        var w = new PacketWriter(Msg.MW_ADDCHAR_ACK);
        w.WriteUInt32(s.CharId);
        w.WriteUInt32(s.Key);
        w.WriteUInt32(s.ClientIp);
        w.WriteUInt16(s.ClientPort);
        w.WriteUInt32(s.UserId);
        _world.Send(w);
    }

    // ---- world-driven handshake steps ----

    private async Task OnMW_ENTERSVR_REQ(PacketReader r)
    {
        byte dbLoad = r.ReadByte();
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        var s = _state.FindByChar(charId);
        if (s is null || s.Key != key) return;

        // Also runs on promotion of an existing secondary connection to the main map.
        {
            var entry = new MapEntry(s.UserId, s.CharId, s.Key, _opt.GroupId, s.Channel,
                EntryIp(s.ClientIp), s.ClientPort);
            if (!await ValidateEntryAsync(entry, enter: true))
            {
                SendCS_CONNECT_ACK(s, ConnectResult.Internal, null);
                s.Conn.Close();
                _state.Remove(s);
                return;
            }
            s.EntryValidated = true;
        }

        await EnsureCharAsync(s);
        var ch = s.Char!;

        // TEnterServer succeeded for this exact session before any successful world response.
        var w = new PacketWriter(Msg.MW_ENTERSVR_ACK);
        w.WriteUInt32(charId);
        w.WriteUInt32(key);
        w.WriteString(ch.Name);
        w.WriteByte(ch.Level);
        w.WriteByte(ch.Sex);       // realSex
        w.WriteByte(ch.Class);
        w.WriteByte(ch.Race);
        w.WriteByte(ch.Sex);
        w.WriteByte(ch.Face);
        w.WriteByte(ch.Hair);
        w.WriteByte(ch.HelmetHide);
        w.WriteByte(ch.Country);
        w.WriteByte(ch.AidCountry);
        w.WriteUInt32(ch.RegionId);
        w.WriteByte(s.Channel);
        w.WriteUInt16(ch.MapId);
        w.WriteFloat(ch.PosX);
        w.WriteFloat(ch.PosY);
        w.WriteFloat(ch.PosZ);
        w.WriteByte(0);            // bLogout
        w.WriteByte(1);            // bSave
        w.WriteByte((byte)ConnectResult.Success);
        w.WriteUInt16(0);         // wTitleID
        w.WriteUInt32(0);         // dwRankPoint
        w.WriteUInt32(s.ClientIp); // dwUserIP
        _world.Send(w);
    }

    private void OnMW_CHARDATA_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        var s = _state.FindByChar(charId);
        if (s?.Char is not { } ch) return;

        var w = new PacketWriter(Msg.MW_CHARDATA_ACK);
        w.WriteUInt32(charId);
        w.WriteUInt32(key);
        w.WriteByte(ch.StartAct);
        w.WriteByte(ch.Level);
        uint maxHp = MaxHpFor(ch), maxMp = MaxMpFor(ch);
        w.WriteUInt32(maxHp);
        w.WriteUInt32(Math.Min(ch.Hp, maxHp));
        w.WriteUInt32(maxMp);
        w.WriteUInt32(Math.Min(ch.Mp, maxMp));
        w.WriteByte(ch.Country);
        w.WriteByte(ch.Mode);
        w.WriteByte(0);            // recall-mon count (Phase-1: none)
        w.WriteString(ch.Comment); // m_strComment
        _world.Send(w);
    }

    private async Task OnMW_CHARINFO_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        var s = _state.FindByChar(charId);
        if (s?.Char is not { } ch) return;

        // Guild / party / title context the world resolved for this char.
        ch.GuildId = r.ReadUInt32();
        r.ReadByte();                     // bGuildCountry
        ch.GuildName = r.ReadString();
        ch.Fame = r.ReadUInt32();
        ch.FameColor = r.ReadUInt32();
        ch.TacticsId = r.ReadUInt32();
        ch.TacticsName = r.ReadString();
        ch.GuildDuty = r.ReadByte();
        ch.GuildPeer = r.ReadByte();
        ch.Castle = r.ReadUInt16();       // wCastle (SSHandler.cpp:2085)
        ch.Camp = r.ReadByte();           // bCamp
        ch.PartyId = r.ReadUInt16();
        ch.PartyType = r.ReadByte();      // bPartyType (loot/exp mode; PT_SOLO opts out of party sharing)
        ch.PartyChiefId = r.ReadUInt32();
        if (r.Remaining >= 2) ch.TitleId = r.ReadUInt16();   // wTitleID (then dwRankPoint / BOWRelease, unused)

        // C++ OnMW_CHARINFO_REQ, before CHARINFO_ACK: the ladder check, then the titles; gold is re-checked on every change.
        CheckMonthRank(ch, ch.Country, ch.MonthPvPoint, ch.PvpTotalPoint);
        LoginTitles(s, ch);
        ch.MoneyChecked = c => GetTitle(s, c, GoldTitle, c.Gold, start: true);

        // The saddle goes first (C++ OnDM_LOADCHAR_ACK sends it before the world's CHARINFO step).
        var saddle = ch.Saddle ?? default;
        SendCS_SENDSADDLE_REQ(s, saddle.ItemId, saddle.EndTime, saddle.Type, openUi: false);

        // C++ OnMW_CHARINFO_REQ: the guild skills first, among the skills the character info carries (MapService.GuildSkill.cs).
        await RefreshGuildSkills(s, notify: false);
        await LoadProtected(ch);                                // C++ OnDM_LOADCHAR_REQ (MapService.Social.cs)
        await LoadAuctionLists(ch);                             // … and its auction lists (MapService.Auction.cs)
        if (s.Char != ch) return;

        // This is where the client actually receives its character (see agent analysis, item 5).
        SendCS_CHARINFO_ACK(s);
        // C++ sends the running-quest list here (OnMW_CHARINFO_REQ, right after CHARINFO_ACK) so the client
        // rebuilds its quest log; the completed-quest list (CS_QUESTLIST_COMPLETE_ACK) is never dispatched by
        // the C++, so it's not ported.
        SendCS_QUESTLIST_ACK(s, ch);
        SendQuestTimers(s, ch);   // C++ SendQuestTimer(m_dwTick) — restores active-timer countdowns on relog
        // The stat sheet (SSHandler.cpp:2124). The client's skill cooldowns scale by its attack delay rates, which only this
        // packet fills — without it every cooldown came out 0.
        SendCS_CHARSTATINFO_ACK(s, ch);
        SendCS_PETLIST_ACK(s, ch);   // C++ sends it after CS_CHARSTATINFO_ACK (SSHandler.cpp:2171)
        SendCS_COMPANIONLIST_ACK(s, ch);
        SendCS_PROTECTEDLIST_ACK(s, ch);   // C++ after the companion list (SSHandler.cpp:2174)
        RestoreSavedRecalls(s, ch);  // C++ OnDM_LOADCHAR_ACK: the summons saved at logout, before…
        SummonTamedAtLogin(s, ch);   // C++ InitCharInfo ends with the sorcerer's tamed monster
        SendHelpOnEnter(s, ch);      // C++ OnMW_CHARINFO_REQ ends with the help message (MapService.Gm.cs)
    }

    private void OnMW_ROUTE_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        // channel, mapId, pos follow — validated in the C++; single-map deployment needs no cross-routing.
        var s = _state.FindByChar(charId);
        if (s is null || s.Key != key || !s.EntryValidated) return;

        var w = new PacketWriter(Msg.MW_ROUTE_ACK);
        w.WriteUInt32(charId);
        w.WriteUInt32(key);
        w.WriteByte(0); // no neighbouring map servers to route to (single-server Phase-1)
        _world.Send(w);
    }

    private async Task OnMW_ENTERCHAR_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        var s = _state.FindByChar(charId);
        if (s is null || s.Key != key || s.Char is not { } ch) return;
        // A secondary map joins an already-authenticated world character through ENTERCHAR rather
        // than ENTERSVR. Recheck its credentials, but do not replace the main map's DB route.
        if (!s.EntryValidated)
        {
            var entry = new MapEntry(s.UserId, s.CharId, s.Key, _opt.GroupId, s.Channel,
                EntryIp(s.ClientIp), s.ClientPort);
            if (!await ValidateEntryAsync(entry, enter: false))
            { s.Conn.Close(); _state.Remove(s); return; }
            s.EntryValidated = true;
        }

        // The world tells the map the char's live state. We read the leading fields we model and overlay
        // them onto the loaded character (the deep block — recall-mons, soulmate, comment — is skipped;
        // the reader tolerates a partial read).
        ch.StartAct = r.ReadByte();
        ch.Name = r.ReadString();
        ch.MapId = r.ReadUInt16();
        ch.PosX = r.ReadFloat();
        ch.PosY = r.ReadFloat();
        ch.PosZ = r.ReadFloat();
        ch.GuildId = r.ReadUInt32();
        ch.Fame = r.ReadUInt32();
        ch.FameColor = r.ReadUInt32();
        ch.GuildName = r.ReadString();
        ch.GuildDuty = r.ReadByte();
        ch.GuildPeer = r.ReadByte();
        ch.Castle = r.ReadUInt16();       // wCastle (SSHandler.cpp:1477)
        ch.Camp = r.ReadByte();           // bCamp
        ch.TacticsId = r.ReadUInt32();
        ch.TacticsName = r.ReadString();
        ch.PartyId = r.ReadUInt16();
        ch.PartyType = r.ReadByte();      // bPartyType (loot/exp mode; PT_SOLO opts out of party sharing)
        ch.PartyChiefId = r.ReadUInt32();
        ch.CommanderId = r.ReadUInt16();
        ch.Level = r.ReadByte();
        ch.HelmetHide = r.ReadByte();
        ch.Country = r.ReadByte();
        ch.AidCountry = r.ReadByte();
        ch.Mode = r.ReadByte();
        ch.Riding = r.ReadUInt32();       // m_dwRiding (SSHandler.cpp:1490)
        ch.ChatBanTime = r.ReadInt64();   // m_nChatBanTime (SSHandler.cpp:1491; MapService.Gm.cs)
        // remaining fields (soulmate, class, recall-mons, comment) are not read

        var w = new PacketWriter(Msg.MW_ENTERCHAR_ACK);
        w.WriteUInt32(charId);
        w.WriteUInt32(key);
        _world.Send(w);
    }

    private void OnMW_CHECKMAIN_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        // channel, mapId, pos follow; single-server → this map is always the main cell.
        var s = _state.FindByChar(charId);
        if (s is null) return;

        s.IsMain = true;
        var w = new PacketWriter(Msg.MW_CHECKMAIN_ACK);
        w.WriteUInt32(charId);
        w.WriteUInt32(key);
        _world.Send(w);
    }

    private void OnMW_CONRESULT_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        var result = (ConnectResult)r.ReadByte();
        var serverIds = new List<byte>();
        int count = r.ReadByte();
        for (int i = 0; i < count; i++) serverIds.Add(r.ReadByte());

        var s = _state.FindByChar(charId);
        if (s is null || s.Key != key || !s.EntryValidated) return;

        SendCS_CONNECT_ACK(s, result, serverIds);

        if (result == ConnectResult.Success)
        {
            s.IsMain = true;
            s.State = EnterState.Granted;
            _log.LogInformation("Char {Char} enter granted.", charId);
        }
        else
        {
            _log.LogWarning("Char {Char} enter denied ({Result}).", charId, result);
            s.Conn.Close();
        }
    }

    private void OnMW_CLOSECHAR_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        var s = _state.FindByChar(charId);
        s?.Conn.Close();
    }

    private void OnMW_TERMINATE_REQ(PacketReader r)
    {
        // World asked the map to drop a char (kick / world shutdown).
        uint charId = r.ReadUInt32();
        var s = _state.FindByChar(charId);
        s?.Conn.Close();
    }

    private void SendMW_CLOSECHAR_ACK(uint charId, uint key)
    {
        var w = new PacketWriter(Msg.MW_CLOSECHAR_ACK);
        w.WriteUInt32(charId);
        w.WriteUInt32(key);
        _world.Send(w);
    }

    // ---- client "ready" → live in the world ----

    private void OnCS_CONREADY_REQ(ClientSession s, PacketReader r)
    {
        if (s.State is not (EnterState.Granted or EnterState.InGame)) return;
        if (s.Char is null) return;

        bool firstEntry = s.State != EnterState.InGame;
        s.State = EnterState.InGame;

        if (firstEntry)
        {
            // Place into the world grid, then exchange CS_ENTER_ACK with the 3×3-cell neighbours
            // (CTMap::EnterMAP adds the player to its centre cell, then CTCell::EnterPlayer runs the
            // bidirectional exchange across the neighbour block). The player must be in the grid first so
            // subsequently-entering players can see it.
            _state.EnterWorld(s);
            SendRecallsInView(s);  // summons first, so a rider's mount exists when its rider appears (TCell.cpp:70-95)
            foreach (var other in _state.Neighbors(s))
            {
                other.Send(BuildCS_ENTER_ACK(s, newMember: true)); // existing players see the newcomer (flagged new)
                s.Send(BuildCS_ENTER_ACK(other));                  // the newcomer sees existing players (not new)
            }
            SendMonstersInView(s); // and the newcomer learns of the monsters already in its view (CTCell::EnterPlayer)
            SendSwitchesAndGatesInView(s); // + the switches/gates already in view (CTCell::EnterPlayer switch/gate loops)
            RecallsEnterMap(s, s.Char);    // summons that followed through a teleport come back (InitMap)
            CompanionEnterMap(s, s.Char);  // and the summoned companion is called out (InitMap)
            DuringItemsAtLogin(s, s.Char); // the premium / exp boost buffs, the premium shown around (InitMap)
            SkyGardenEnterMap(s, s.Char);  // on the sky garden's map: one's camp and the garden's state (InitMap)
            CastleEnterMap(s, s.Char);     // on a castle's map: signed up, or sent out (InitMap, MapService.Castle.cs)
            TournamentEnterMap(s, s.Char); // the lounge and the tournament maps (InitMap, MapService.TournamentMatch.cs)
            _log.LogInformation("Char {Char} live on map {Map} ch {Ch}.", s.CharId, s.Char.MapId, s.Channel);
        }
    }

    // ---- senders to the client ----

    private static void SendCS_CONNECT_ACK(ClientSession s, ConnectResult result, IReadOnlyList<byte>? serverIds)
    {
        var w = new PacketWriter(Msg.CS_CONNECT_ACK);
        w.WriteByte((byte)result);
        w.WriteByte((byte)(serverIds?.Count ?? 0));
        if (serverIds is not null)
            foreach (var id in serverIds) w.WriteByte(id);
        s.Send(w);
    }

    private void SendCS_CHARINFO_ACK(ClientSession s)
    {
        var ch = s.Char!;
        var w = new PacketWriter(Msg.CS_CHARINFO_ACK, capacity: 512);
        w.WriteUInt32(ch.CharId);
        w.WriteBool(CodeFor(s) is not null);
        w.WriteBool(s.SecurityUnlocked);
        w.WriteBool(CodeFor(s) is { Enabled: false });
        w.WriteUInt16(ch.TitleId);
        w.WriteString(ch.Name);
        w.WriteByte(ch.StartAct);
        w.WriteByte(ch.Class);
        w.WriteByte(ch.Race);
        w.WriteByte(ch.Country);
        w.WriteByte(ch.AidCountry);
        w.WriteByte(ch.Sex);
        w.WriteByte(ch.Hair);
        w.WriteByte(ch.Face);
        w.WriteByte(ch.Body);
        w.WriteByte(ch.Pants);
        w.WriteByte(ch.Hand);
        w.WriteByte(ch.Foot);
        w.WriteByte(ch.HelmetHide);
        w.WriteByte(ch.Level);
        w.WriteUInt16(ch.PartyId);
        w.WriteUInt32(ch.GuildId);
        w.WriteUInt32(ch.Fame);
        w.WriteUInt32(ch.FameColor);
        w.WriteByte(ch.GuildDuty);
        w.WriteByte(ch.GuildPeer);
        w.WriteString(ch.GuildName);
        w.WriteUInt32(ch.TacticsId);
        w.WriteString(ch.TacticsName);
        w.WriteUInt32(ch.Gold);
        w.WriteUInt32(ch.Silver);
        w.WriteUInt32(ch.Cooper);
        w.WriteUInt32(_templates.LevelExpOf(ch.Level - 1) ?? 0);
        w.WriteUInt32(_templates.LevelExpOf(ch.Level) ?? 0);
        w.WriteUInt32(ch.Exp);
        uint maxHp = MaxHpFor(ch), maxMp = MaxMpFor(ch); // C++ GetMaxHP/GetMaxMP; current HP/MP clamped down
        w.WriteUInt32(maxHp);
        w.WriteUInt32(Math.Min(ch.Hp, maxHp));
        w.WriteUInt32(maxMp);
        w.WriteUInt32(Math.Min(ch.Mp, maxMp));
        w.WriteUInt32(ch.PartyChiefId);
        w.WriteUInt16((ushort)ch.CommanderId); // wCommanderID (header contract is WORD)
        w.WriteUInt32(ch.RegionId);
        w.WriteUInt16(ch.MapId);
        w.WriteFloat(ch.PosX);
        w.WriteFloat(ch.PosY);
        w.WriteFloat(ch.PosZ);
        w.WriteUInt16(ch.Dir);
        w.WriteUInt16((ushort)ch.SkillPoint);   // wSkillPoint (my)
        w.WriteByte(ch.LuckyNumber); // bLuckyNumber (MapService.Events.cs)
        w.WriteUInt32(0);            // aid left time
        foreach (var k in SkillKindPoints(ch)) w.WriteUInt16(k);   // skill kind points 1-4 (GetSkillKindPoint)
        w.WriteUInt32(0);            // dwRankPoint
        w.WriteByte(0);              // non-BOW build flag (FALSE)

        // Inventory containers + items (each item via CTItem::WrapPacketClient). The C++ holds these in
        // map<BYTE invenId> / map<BYTE slot>, so it emits containers by ascending id and items by ascending
        // slot — sort to match the on-wire byte sequence (the client self-keys, so this is parity, not correctness).
        w.WriteByte((byte)ch.Invens.Count);
        foreach (var inv in ch.Invens.OrderBy(i => i.InvenId))
        {
            w.WriteByte(inv.InvenId);
            w.WriteUInt16(inv.TemplateId);
            w.WriteInt64(inv.EndTime);
            w.WriteByte((byte)inv.Items.Count);
            foreach (var it in inv.Items.OrderBy(x => x.ItemSlot)) it.WrapPacketClient(w, ch.CharId);
        }

        // Learned skills (C++ map<WORD skillId> → ascending by id).
        w.WriteByte((byte)ch.Skills.Count);
        foreach (var sk in ch.Skills.OrderBy(s => s.SkillId))
        {
            w.WriteUInt16(sk.SkillId);
            w.WriteByte(sk.Level);
            w.WriteUInt32(sk.ReuseRemainTick);
        }

        // Maintained (buff) skills.
        w.WriteByte((byte)ch.MaintainSkills.Count);
        foreach (var m in ch.MaintainSkills) WriteMaintainSkill(w, m, NowMs);

        // Hotkey pages (MAX_HOTKEY_POS slots each; C++ map<BYTE invenKey> → ascending by key).
        w.WriteByte((byte)ch.HotkeyPages.Count);
        foreach (var pg in ch.HotkeyPages.OrderBy(p => p.InvenKey))
        {
            w.WriteByte(pg.InvenKey);
            for (int i = 0; i < HotkeyPage.SlotCount; i++)
            {
                w.WriteByte(pg.Slots[i].Type);
                w.WriteUInt16(pg.Slots[i].Id);
            }
        }

        w.WriteByte(0);              // item-cooltime count (Phase-2: none)
        w.WriteUInt32(ch.PvpTotalPoint);
        w.WriteUInt32(ch.PvpUseablePoint);
        w.WriteUInt32(ch.MonthPvPoint);
        w.WriteString(DateTime.Now.ToString("tt hh : mm")); // strTajm (server clock)
        w.WriteUInt32(ch.Medals);    // medals
        SendBalanceChat(s);          // the kingdom balance line, said just before (MapService.CountryBalance.cs)
        s.Send(w);
    }

    // ---- character resolution ----

    private async Task EnsureCharAsync(ClientSession s)
    {
        if (s.Char is not null) return;

        var ch = new Character
        {
            CharId = s.CharId,
            Country = 4,       // neutral until the world/DB says otherwise
            Level = 1,
            MaxHp = 100, Hp = 100, MaxMp = 100, Mp = 100,
            MapId = 0,
            PosX = SpawnX, PosY = SpawnY, PosZ = SpawnZ, Dir = SpawnDir,
            Name = $"Char{s.CharId}",
        };

        if (_gameDb is not null)
        {
            try
            {
                var row = await _gameDb.LoadCharAsync(s.CharId);
                if (row is CharLoadRow c)
                {
                    ch.Name = string.IsNullOrEmpty(c.Name) ? ch.Name : c.Name;
                    ch.StartAct = c.StartAct;
                    ch.Class = c.Class; ch.Race = c.Race; ch.Country = c.Country; ch.Sex = c.Sex;
                    ch.Hair = c.Hair; ch.Face = c.Face; ch.Body = c.Body; ch.Pants = c.Pants;
                    ch.Hand = c.Hand; ch.Foot = c.Foot; ch.Level = c.Level == 0 ? (byte)1 : c.Level;
                    ch.RegionId = c.Region; ch.HelmetHide = c.HelmetHide;
                    // Where it was saved (C++ CTBLChar): the map and the spot — a never-placed row (all 0) keeps the default.
                    if (c.MapId != 0 || c.PosX != 0 || c.PosZ != 0)
                    { ch.MapId = c.MapId; ch.PosX = c.PosX; ch.PosY = c.PosY; ch.PosZ = c.PosZ; ch.Dir = c.Dir; }
                    ch.Hp = c.Hp; ch.Mp = c.Mp; // persisted CURRENT hp/mp; max is computed + clamped at serialize
                    ch.Gold = c.Gold; ch.Silver = c.Silver; ch.Cooper = c.Cooper;
                    ch.Exp = c.Exp; ch.SkillPoint = c.SkillPoint;
                    // Round-trip-only columns (unused by the map, saved back unchanged by TSaveChar).
                    ch.Persist.GuildLeave = c.GuildLeave; ch.Persist.GuildLeaveTime = c.GuildLeaveTime;
                    ch.Persist.SpawnId = c.SpawnId; ch.Persist.LastSpawnId = c.LastSpawnId;
                    ch.Persist.LastDestination = c.LastDestination; ch.Persist.TemptedMon = c.TemptedMon;
                    ch.Persist.Aftermath = c.Aftermath; ch.Persist.StatLevel = c.StatLevel;
                    ch.Persist.StatPoint = c.StatPoint; ch.Persist.StatExp = c.StatExp;
                    RestoreAftermath(ch);        // re-armed through SetAftermath, as the C++ load does
                    await LoadPendingBills(ch.CharId);   // C++ DM_POSTBILL_REQ from DM_ENTERMAPSVR_ACK
                    ch.DbLoaded = true;          // a real row ⇒ eligible to be saved back
                    ch.LastSaveMs = NowMs;       // first periodic save one interval after load (C++ m_dwSaveTick)
                }
                else
                {
                    throw new InvalidDataException($"Character {s.CharId} is unavailable.");
                }

                await TryLoadInventoryAsync(ch);
                try { (ch.OriCountry, ch.AidCountry, ch.AidDate) = await _gameDb.LoadAidAsync(ch.CharId); }   // CTBLChar / CTBLAidTable
                catch (Exception ex) { _log.LogWarning(ex, "Char {Char} aid country load failed.", ch.CharId); }
                try { LoadDuringItems(ch, await _gameDb.LoadDuringItemsAsync(ch.CharId, s.UserId, LocalDay(UnixNow()))); }
                catch (Exception ex) { _log.LogWarning(ex, "Char {Char} premium / exp item load failed.", ch.CharId); }
                await LoadPetsAsync(s, ch);
                await LoadCompanionsAsync(ch);

                // C++ clamps the persisted current HP/MP DOWN to the computed max at load (no refill), so the
                // in-memory value is never over-max (matters once regen/damage/save read it). Charts-gated.
                if (_templates.HasStats)
                {
                    ch.Hp = Math.Min(ch.Hp, StatEngine.MaxHp(ch, _templates));
                    ch.Mp = Math.Min(ch.Mp, StatEngine.MaxMp(ch, _templates));
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Char {Char} DB load failed; denying entry.", s.CharId);
                throw;
            }
        }

        s.Char = ch;
    }

    private async Task<bool> ValidateEntryAsync(MapEntry entry, bool enter)
    {
        if (_entryStore is null) return false;
        try { return enter ? await _entryStore.EnterAsync(entry) : await _entryStore.ValidateAsync(entry); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Entry validation unavailable for character {Char}.", entry.CharId);
            return false;
        }
    }

    private static string EntryIp(uint ip) => new System.Net.IPAddress(
        new byte[] { (byte)ip, (byte)(ip >> 8), (byte)(ip >> 16), (byte)(ip >> 24) }).ToString();

    /// <summary>
    /// Loads the character's inventory containers + items, learned/maintained skills and hotkey pages
    /// (CTBLInven/CTBLItem/CTBLSkill/CTBLSkillMaintain/CTBLHotKey), building the in-memory model the
    /// CS_CHARINFO_ACK / CS_ENTER_ACK sub-loops serialize. Best-effort — any failure leaves the char with
    /// whatever loaded so far. Only <c>STORAGE_INVEN</c> (0) items land in the inventory containers; the
    /// backpack (0xFF) and equipped (0xFE) containers always exist.
    /// </summary>
    private async Task TryLoadInventoryAsync(Character ch)
    {
        if (_gameDb is null) return;

        // Containers: seed backpack + equipped, add the loaded bags, then bucket items by storage id.
        var byId = new Dictionary<byte, Inven>
        {
            [Proto.InvenDefault] = new Inven { InvenId = Proto.InvenDefault },
            [Proto.InvenEquip] = new Inven { InvenId = Proto.InvenEquip },
        };
        foreach (var b in await _gameDb.LoadInvensAsync(ch.CharId))
            byId[b.InvenId] = new Inven { InvenId = b.InvenId, TemplateId = b.TemplateId, EndTime = b.EndTime, Eld = b.Eld };

        // Cabinet open-state headers BEFORE items (C++ OnDM_LOADCHAR_ACK applies bCabinetID/bUse first, so the
        // per-item GetCabinet(m_bItemID) lands in an existing record).
        foreach (var cab in await _gameDb.LoadCabinetsAsync(ch.CharId))
            ch.GetOrCreateCabinet(cab.CabinetId).Use = cab.Use;

        foreach (var row in await _gameDb.LoadItemsAsync(ch.CharId))
        {
            // STORAGE_INVEN (bags) and STORAGE_CABINET (warehouse) both ride this load; STORAGE_POST (mail) is
            // already excluded by the query (bStorageType <> 2). Anything else is dropped.
            if (row.StorageType is not (Proto.StorageInven or Proto.StorageCabinet)) continue;

            if (ItemFromRow(row) is not { } item) continue;

            if (row.StorageType == Proto.StorageCabinet)
            {
                // C++: dwStorageID → m_dwStItemID; bItemID (the row's ItemSlot) → cabinet id.
                item.StItemId = row.StorageId;
                ch.GetOrCreateCabinet(row.ItemSlot).Items.Add(item);
            }
            else
            {
                byte id = (byte)row.StorageId;
                if (!byId.TryGetValue(id, out var inv)) { inv = new Inven { InvenId = id }; byId[id] = inv; }
                inv.Items.Add(item);
            }
        }

        ch.Invens.Clear();
        ch.Invens.AddRange(byId.Values);

        var pvp = await _gameDb.LoadPvpAsync(ch.CharId);                                // CSPGetPvPRecord + CSPGetMonthPvPoint
        (ch.PvpUseablePoint, ch.PvpTotalPoint, ch.PvpRankOrder, ch.PvpRankPercent) = (pvp.Useable, pvp.Total, pvp.RankOrder, pvp.RankPercent);
        for (int i = 0; i < 12; i++) ch.PvpRecord[i] = pvp.Record[i];
        ch.TotalLose = pvp.Record.Where((_, i) => i % 2 == 0).Aggregate(0u, (a, b) => a + b);
        ch.TotalWin = pvp.Record.Where((_, i) => i % 2 == 1).Aggregate(0u, (a, b) => a + b);
        (ch.MonthPvPoint, ch.MonthWin, ch.MonthLose, ch.MonthRankOrder, ch.MonthRankPercent)
            = (pvp.MonthPoint, pvp.MonthWin, pvp.MonthLose, pvp.MonthRankOrder, pvp.MonthRankPercent);
        foreach (var (id, selected) in await _gameDb.LoadTitlesAsync(ch.CharId)) ch.Titles[id] = selected;   // CTBLTitle
        try { LoadSavedRecalls(ch, await _gameDb.LoadRecallsAsync(ch.CharId)); }                          // CTBLRecallMon / Maintain
        catch (Exception ex) { _log.LogWarning(ex, "Char {Char} summon load failed; continuing without them.", ch.CharId); }

        foreach (var sk in await _gameDb.LoadSkillsAsync(ch.CharId))
            if (!IsGuildSkill(sk.SkillId))                                     // those come from the guild's tables
            ch.Skills.Add(new Skill
            {
                SkillId = sk.SkillId, Level = sk.Level, ReuseRemainTick = sk.RemainTick,
                Template = _templates.Skill(sk.SkillId), // C++ CTSkill::m_pTSKILL link
            });

        foreach (var m in await _gameDb.LoadMaintainAsync(ch.CharId))
        {
            var buff = new MaintainSkill
            {
                SkillId = m.SkillId, Level = m.Level, RemainTick = m.RemainTick, AttackType = m.AttackType,
                AttackId = m.AttackId, HostType = m.HostType, HostId = m.HostId, AttackCountry = m.AttackCountry,
                Template = _templates.Skill(m.SkillId),  // C++ CTSkill::m_pTSKILL link — needed for stat effect + expiry
            };
            // C++ enter reconstruction (SSHandler.cpp:4955): the saved REMAINING becomes the buff's full duration
            // measured from the login tick; a 0-remain (permanent) buff keeps StartTick 0 (never expires). The 11
            // non-persisted combat fields keep their MaintainSkill ctor defaults (CanSelect 1, powers 0).
            if (m.RemainTick != 0) buff.SetLoopEndTick(NowMs, m.RemainTick);
            ch.MaintainSkills.Add(buff);
        }

        try
        {
            foreach (var hk in await _gameDb.LoadHotkeysAsync(ch.CharId))
            {
                var page = new HotkeyPage { InvenKey = hk.InvenKey };
                for (int i = 0; i < HotkeyPage.SlotCount && i < hk.Slots.Length; i++)
                    page.Slots[i] = new HotkeySlot(hk.Slots[i].Type, hk.Slots[i].Id);
                ch.HotkeyPages.Add(page);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Char {Char} hotkey load failed; continuing without hotkeys.", ch.CharId);
        }

        // Quest progress (C++ char-enter quest load): rebuild m_mapQUEST from the saved rows — the read-back
        // counterpart of the Phase-25 quest save, so accepted quests survive relog. Best-effort.
        try
        {
            var (qRows, qTerms) = await _gameDb.LoadQuestsAsync(ch.CharId);
            LoadQuestProgress(ch, qRows, qTerms);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Char {Char} quest-progress load failed; continuing without quests.", ch.CharId);
        }
    }

    /// <summary>
    /// C++ <c>CTMapSvrModule::SetItemAttr</c> — links the item's <c>m_pTITEMATTR</c> by the key
    /// (<c>wAttrID + m_itemgrade[level].m_bGrade + gem</c>), truncated to 16 bits (the C++ <c>std::map&lt;WORD&gt;</c>
    /// key), with the lowest-id row as fallback (<c>m_mapTItemAttr.begin()</c>). No-op if the attr chart
    /// isn't loaded.
    /// </summary>
    /// <summary>C++ <c>CreateItem</c> from a persisted <c>TITEMTABLE</c> row — null when the template is unknown
    /// (C++ drops it; only once the chart is loaded, so DB-free operation keeps everything).</summary>
    private Item? ItemFromRow(FullItemRow row)
    {
        var template = _templates.Item(row.TemplateId);
        if (template is null && _templates.HasItems) return null;

        var item = new Item
        {
            DlId = row.DlId,
            ItemSlot = row.ItemSlot, TemplateId = row.TemplateId, Level = row.Level, Count = row.Count,
            GLevel = row.GLevel, DuraMax = row.DuraMax, DuraCur = row.DuraCur, RefineCur = row.RefineCur,
            EndTime = row.EndTime, GradeEffect = row.GradeEffect, Gem = row.Gem, MoggItemId = row.MoggItemId,
            Template = template,
        };
        for (int i = 0; i < 6; i++) item.Ext[i] = row.Ext[i];
        // C++ CreateItem filter: skip id/value-0 slots, drop unknown-magic-id (chart-gated), dedup by id.
        Item.AddPersistedMagic(item, row.Magic, row.Value, _templates);
        LinkItemAttr(item);
        return item;
    }

    /// <summary>C++ <c>SetItemAttr</c> (TMapSvr.cpp:6952, added by this repo): a companion rune with no species yet
    /// takes the one <c>TCOMPANIONRUNECHART</c> gives its item id (stored in <c>dwTime5</c>, IEV_COMPANION).</summary>
    private void StampCompanionRune(Item item)
    {
        if (item.Template is { Type: 22 } && item.Ext[Item.IevCompanion] == 0
            && _templates.CompanionRunes.TryGetValue(item.TemplateId, out var species))
            item.Ext[Item.IevCompanion] = species;
    }

    private void LinkItemAttr(Item item)
    {
        StampCompanionRune(item);
        LinkItemAttrAt(item, item.Level);
    }

    /// <summary>C++ <c>SetItemAttr(pItem, bLevel)</c>: the attributes of the item at a given level (a territory's item cap).</summary>
    private void LinkItemAttrAt(Item item, byte level)
    {
        if (item.Template is null || !_templates.HasItemAttrs) return;
        ushort key = (ushort)(item.Template.AttrId + _templates.GradeForLevel(level) + item.Gem);
        item.Attr = _templates.Attr(key) ?? _templates.DefaultAttr;
    }

    /// <summary>Writes one maintained/buff skill block, shared by CS_CHARINFO_ACK / CS_ENTER_ACK / CS_ADDMON_ACK.
    /// The remaining tick is computed live off <paramref name="now"/> (C++ <c>GetRemainTick(dwTick)</c>) so a
    /// running buff's countdown is current on the wire; 0 for a permanent or elapsed buff.</summary>
    internal static void WriteMaintainSkill(PacketWriter w, MaintainSkill m, uint now)
    {
        w.WriteUInt16(m.SkillId);
        w.WriteByte(m.Level);
        w.WriteUInt32(m.GetRemainTick(now));
        w.WriteUInt32(m.AttackId);
        w.WriteByte(m.AttackType);
        w.WriteUInt32(m.HostId);
        w.WriteByte(m.HostType);
        w.WriteByte(m.Hit);
        w.WriteUInt16(m.AttackLevel);
        w.WriteByte(m.AttackerLevel);
        w.WriteUInt32(m.PysMinPower);
        w.WriteUInt32(m.PysMaxPower);
        w.WriteUInt32(m.MgMinPower);
        w.WriteUInt32(m.MgMaxPower);
        w.WriteByte(m.CanSelect);
        w.WriteByte(m.AttackCountry);
        w.WriteFloat(m.PosX);
        w.WriteFloat(m.PosY);
        w.WriteFloat(m.PosZ);
    }
}
