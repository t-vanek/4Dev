using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The tournament, batch S6e-2 — the matches (C++ TMap.cpp:1930–2110 (the match state), SSHandler.cpp:17892 (the steps), 18263 (results),
/// 18360 (the final's reward), 284 (the timer); TMapSvr.cpp:8116 (InitMap), 7367 (ExitMAP), 11445 (SetTournamentChar);
/// TPlayer.cpp:6053–6200 (the tournament character), 2149 (kill points)).
/// <list type="bullet">
/// <item>Each match is played on a tournament map (501–532) between two slots, blue (the lower) and red. A fighter who enters its
/// map becomes the <b>tournament character</b>: its own level, skills, buffs and bags are kept aside (and saved first), and it plays at
/// the level cap with every skill of its class at the top level and the bags and items of the tournament charts; it gets them back when
/// it leaves. Nothing of it is saved meanwhile.</item>
/// <item>The world drives the steps: at a match step the clock starts (a side that is missing loses at once); at its end the side with
/// more kills wins, then the one that hid less, and a full tie is drawn by dice (<c>CS_TOURNAMENTSELECTWINNER_ACK</c>) and settled
/// 10 seconds later. A side that leaves its map mid-match loses. Before the semi-final and the final the winners are sent to the
/// lounge and the new matches set up (<c>CS_TOURNAMENTENTER_ACK</c>).</item>
/// <item>The world's results (<c>MW_TOURNAMENTRESULT_REQ</c>) are shown on the tournament maps; the final's winners get the reward
/// (<c>TTournamentReward</c>) and, for the first tournament, a place in the fame list.</item>
/// <item>The lounge (map 500) and the tournament maps are peace zones except during a match; spectators stand on the stands
/// (height 7 and up); there are no PvP points, deaths cost no aftermath, and many things are off for a tournament character.</item>
/// </list>
/// </summary>
public sealed partial class MapService
{
    private const byte TnmtSlotNone = 8, SmTournamentCountdown = 19, EsCostumeHat = 15, EsCostumeRace = 18;
    private const ushort TnmtCheerItemSkillValue = 1016;

    private sealed class TournamentMatch
    {
        public required ushort MapId;
        public readonly SortedDictionary<uint, TournamentPlayer> Players = new();
        public byte Blue = TnmtSlotNone, Red = TnmtSlotNone;
        public ushort DefKill, AtkKill;
        public uint BlueHide, RedHide;
        public long EndTick;
    }

    /// <summary>What a tournament character set aside (C++ <c>m_*_bak</c>).</summary>
    public sealed class TournamentBackup
    {
        public byte Level;
        public List<Inven> Invens = new();
        public List<Skill> Skills = new();
        public List<MaintainSkill> Maintains = new();
        public List<HotkeyPage> Hotkeys = new();
    }

    private readonly Dictionary<ushort, TournamentMatch> _tnmtMatches = new();
    private readonly List<TournamentMatch> _tnmtResultMaps = new();
    private long _tnmtResultTick;
    private byte _tnmtResultStep;

    private TournamentMatch MatchOf(ushort mapId)
    {
        if (!_tnmtMatches.TryGetValue(mapId, out var m)) _tnmtMatches[mapId] = m = new TournamentMatch { MapId = mapId };
        return m;
    }

    private static int MatchIndex(byte step) => step == TnmtFinal ? 2 : step == TnmtSFinal ? 1 : 0;

    private IEnumerable<ClientSession> TournamentMapPlayers(ushort mapId)
        => _state.AllInGame().Where(p => p.IsMain && p.Channel == DefaultChannel && p.Char?.MapId == mapId).ToList();

    /// <summary>C++ <c>CTMap::AddTNMTPlayer</c>: the lower slot is blue, the other red.</summary>
    private static void AddTnmtPlayer(TournamentMatch m, TournamentPlayer p)
    {
        m.Players[p.CharId] = p;
        if (m.Players.Count == 1) m.Blue = p.Slot;
        foreach (var o in m.Players.Values)
        {
            if (o.Slot == p.Slot) continue;
            if (o.Slot < p.Slot) { m.Blue = o.Slot; m.Red = p.Slot; }
            else { m.Blue = p.Slot; m.Red = o.Slot; }
            break;
        }
    }

    private static void ResetTournament(TournamentMatch m)
    {
        m.DefKill = m.AtkKill = 0;
        m.BlueHide = m.RedHide = 0;
        m.EndTick = 0;
        m.Blue = m.Red = TnmtSlotNone;
        m.Players.Clear();
    }

    /// <summary>C++ <c>CTMap::CanDoTournament</c>: both sides are on the map and the match is not decided yet.</summary>
    private static bool CanDoMatch(TournamentMatch m, byte step = TnmtReady)
    {
        if (m.Blue == TnmtSlotNone || m.Red == TnmtSlotNone || m.Players.Count == 0) return false;
        int blue = 0, red = 0, id = MatchIndex(step);
        foreach (var p in m.Players.Values)
        {
            if (p.MapId == m.MapId) { if (p.Slot == m.Blue) blue++; else red++; }
            if (step != TnmtReady && p.Result[id] != TnmtWinNone) return false;
        }
        return blue != 0 && red != 0;
    }

    /// <summary>C++ <c>CTMap::EndTournament</c> (TMap.cpp:1958): the winning side — the one left, then the kills, then the hiding —
    /// and its and the other side's chiefs; false when there is nothing to decide. The match is reset.</summary>
    private static bool EndTournament(TournamentMatch m, out bool won, out uint winner, out uint loser, byte step = TnmtReady)
    {
        winner = loser = 0;
        won = true;
        if (m.Players.Count == 0 || (m.Blue == TnmtSlotNone && m.Red == TnmtSlotNone)) return false;
        byte win;
        if (m.Blue == TnmtSlotNone) win = m.Red;
        else if (m.Red == TnmtSlotNone) win = m.Blue;
        else
        {
            int blue = 0, red = 0;
            foreach (var p in m.Players.Values) if (p.MapId == m.MapId) { if (p.Slot == m.Blue) blue++; else red++; }
            if (blue == 0 && red == 0) { won = false; win = m.Blue; }
            else if (blue == 0) win = m.Red;
            else if (red == 0) win = m.Blue;
            else if (m.AtkKill < m.DefKill) win = m.Blue;
            else if (m.AtkKill > m.DefKill) win = m.Red;
            else if (m.RedHide < m.BlueHide) win = m.Red;
            else win = m.Blue;
        }
        int id = MatchIndex(step);
        foreach (var p in m.Players.Values)
        {
            if (step != TnmtReady && p.Result[id] != TnmtWinNone) return false;
            if (p.Slot == win) winner = p.ChiefId; else loser = p.ChiefId;
            if (winner != 0 && loser != 0) break;
        }
        ResetTournament(m);
        return true;
    }

    private void SelectWinner(TournamentMatch m)
    {
        while (m.DefKill == m.AtkKill) { m.DefKill = (ushort)(Rand() % 6 + 1); m.AtkKill = (ushort)(Rand() % 6 + 1); }
    }

    private static void SetHideTick(TournamentMatch m, uint charId, uint tick)
    {
        if (!m.Players.TryGetValue(charId, out var p)) return;
        if (p.Slot == m.Blue) m.BlueHide += tick; else m.RedHide += tick;
    }

    /// <summary>C++ <c>IsActiveTournamentMap</c> (TMapSvr.cpp:11266): a map of this step's matches in the running group.</summary>
    private bool IsActiveTournamentMap(ushort mapId)
    {
        byte step = TournamentStep;
        if (step == TnmtReady) return false;
        foreach (var e in _tournaments.Values)
        {
            if (e.Group != _tnmtGroup) continue;
            int baseMap = MapIdTournament + (e.EntryId - 1) * 4;
            if (step < TnmtSFEnter) { if (mapId >= baseMap && mapId < baseMap + 4) return true; }
            else if (step < TnmtFEnter) { if (mapId == baseMap || mapId == baseMap + 2) return true; }
            else if (mapId == baseMap) return true;
        }
        return false;
    }

    private IEnumerable<TournamentMatch> ActiveMatches()
    {
        for (int id = MapIdTournament; id < MapIdTournament + TournamentMapCount; id++)
            if (IsActiveTournamentMap((ushort)id) && OwnsMap((ushort)id)) yield return MatchOf((ushort)id);
    }

    private void SendTournamentResultToWorld(byte step, bool won, uint winner, uint loser, uint blueHide = 0, uint redHide = 0)
    {
        var w = new PacketWriter(Msg.MW_TOURNAMENTRESULT_ACK);
        w.WriteByte(step); w.WriteByte(won ? (byte)1 : (byte)0); w.WriteUInt32(winner); w.WriteUInt32(loser); w.WriteUInt32(blueHide); w.WriteUInt32(redHide);
        _world.Send(w);
    }

    /// <summary>C++ <c>SendCS_TOURNAMENTENTER_ACK</c>: the match's kills and fighters (the good buffs come off first).</summary>
    private void SendTournamentEnter(ClientSession s, Character ch, TournamentMatch m)
    {
        StripMaintains(s, ch, positive: true);                                                // DeletePositiveMaintainSkill
        var w = new PacketWriter(Msg.CS_TOURNAMENTENTER_ACK);
        w.WriteUInt16(m.DefKill); w.WriteUInt16(m.AtkKill); w.WriteByte((byte)m.Players.Count);
        foreach (var p in m.Players.Values)
        {
            w.WriteByte(p.Slot); w.WriteUInt32(p.CharId); w.WriteByte(p.Country); w.WriteString(p.Name); w.WriteByte(p.Level);
            w.WriteByte(p.Class); w.WriteUInt32(p.ChiefId);
        }
        s.Send(w);
    }

    /// <summary>The hide buffs a fighter has on (C++ <c>IsHideBuff</c>).</summary>
    private IEnumerable<MaintainSkill> HideBuffs(Character ch)
        => ch.MaintainSkills.Where(b => (b.Template ?? _templates.Skill(b.SkillId))?.IsHideSkill == true);

    /// <summary>C++ <c>ClearTournamentPlayer</c>: no bracket, no match.</summary>
    private void ClearTournamentPlayers()
    {
        foreach (var m in _tnmtMatches.Values) ResetTournament(m);
        _tnmtPlayers.Clear();
    }

    // ================================ the world's steps ================================

    /// <summary>C++ <c>OnMW_TOURNAMENTENABLE_REQ</c>'s matches (SSHandler.cpp:17905): a match step starts the clocks, its end decides,
    /// and the semi-final and final entries set the new matches up.</summary>
    private void TournamentMatchesOnStep(byte group, byte step, uint period)
    {
        if (step is TnmtQFinal or TnmtSFinal or TnmtFinal)
        {
            _tnmtGroup = group; _tnmtStep = step; _tnmtEndTime = UnixNow() + period;
            foreach (var m in ActiveMatches().ToList())
            {
                m.EndTick = NowMs + period * 1000L;
                foreach (var p in m.Players.Values.Where(p => p.MapId == m.MapId))
                    if (FindByName(p.Name)?.Char is { } c)
                        foreach (var hide in HideBuffs(c)) if (hide.MaintainTick == 0) hide.ChargeTick = NowMs;
                if (!CanDoMatch(m, step))
                {
                    if (EndTournament(m, out bool won, out uint winner, out uint loser, step)) SendTournamentResultToWorld(_tnmtStep, won, winner, loser);
                    ResetTournament(m);
                }
            }
        }
        else if (step is TnmtQFEnd or TnmtSFEnd or TnmtEnd)
        {
            _tnmtResultTick = NowMs;
            _tnmtResultStep = _tnmtStep;
            _tnmtResultMaps.Clear();
            foreach (var m in ActiveMatches().ToList())
            {
                if (m.EndTick == 0) continue;
                foreach (var p in m.Players.Values.Where(p => p.MapId == m.MapId))
                    if (FindByName(p.Name)?.Char is { } c)
                        foreach (var hide in HideBuffs(c)) SetHideTick(m, c.CharId, unchecked(NowMs - hide.ChargeTick));
                if (m.AtkKill == m.DefKill && m.BlueHide == m.RedHide)
                {
                    _tnmtResultMaps.Add(m);
                    SelectWinner(m);
                    var dice = new PacketWriter(Msg.CS_TOURNAMENTSELECTWINNER_ACK).WriteByte((byte)m.DefKill).WriteByte((byte)m.AtkKill).ToArray();
                    foreach (var s in TournamentMapPlayers(m.MapId)) s.Send(dice);
                }
                else
                {
                    uint blueHide = 0, redHide = 0;
                    if (m.AtkKill == m.DefKill) { blueHide = m.BlueHide; redHide = m.RedHide; }
                    EndTournament(m, out bool won, out uint winner, out uint loser);
                    SendTournamentResultToWorld(_tnmtStep, won, winner, loser, blueHide, redHide);
                }
            }
        }
        else if (step is TnmtSFEnter or TnmtFEnter)
        {
            _tnmtGroup = group; _tnmtStep = step; _tnmtEndTime = UnixNow() + period;
            foreach (var p in _tnmtPlayers.Values.ToList())
            {
                ushort map = TournamentMapOf(p.CharId, group);
                if (map != 0) AddTnmtPlayer(MatchOf(map), p);
                if (p.MapId != 0 && FindByName(p.Name) is { Char: { } c } s)
                {
                    ushort lounge = _templates.SpawnPositions.ContainsKey(TournamentLoungeSpawn) ? TournamentLoungeSpawn
                        : c.Persist.LastSpawnId != 0 ? c.Persist.LastSpawnId : c.Persist.SpawnId;
                    Teleport(s, c, lounge);
                    ExitTournamentChar(s, c);
                }
            }
            foreach (var m in ActiveMatches().ToList())
                foreach (var s in TournamentMapPlayers(m.MapId)) SendTournamentEnter(s, s.Char!, m);
        }
    }

    /// <summary>C++ the timer's tournament part (SSHandler.cpp:286): the countdowns, and a drawn match settled 10 seconds on.</summary>
    private void TournamentTick()
    {
        byte notice = 0;
        uint secs = 0;
        long now = UnixNow();
        if (_tnmtStep is TnmtEnter or TnmtSFEnter or TnmtFEnter && _tnmtEndTime > now)
        {
            secs = (uint)(_tnmtEndTime - now);
            if (secs >= 60 ? secs % 60 == 0 : secs >= 10 ? secs % 10 == 0 : true) notice = 1;
        }
        if (_tnmtStep is TnmtQFinal or TnmtSFinal or TnmtFinal && _tnmtEndTime > now)
        {
            secs = (uint)(_tnmtEndTime - now);
            if (secs % 10 == 0) notice = 2;
        }
        if (_tnmtResultMaps.Count != 0 && _tnmtResultStep >= TnmtQFinal && NowMs - _tnmtResultTick > 10_000)
        {
            foreach (var m in _tnmtResultMaps)
            {
                EndTournament(m, out bool won, out uint winner, out uint loser);
                SendTournamentResultToWorld(_tnmtResultStep, won, winner, loser);
            }
            _tnmtResultStep = 0;
            _tnmtResultMaps.Clear();
        }
        if (notice == 0) return;
        var w = new PacketWriter(Msg.CS_SYSTEMMSG_ACK);
        w.WriteByte(SmTournamentCountdown); w.WriteByte(_tnmtStep); w.WriteUInt32(secs);
        var msg = w.ToArray();
        foreach (var s in _state.AllInGame())
            if (s.IsMain && s.Char is { } c && (IsTournamentMap(c.MapId) || (notice == 1 && c.MapId == TournamentLoungeMap))) s.Send(msg);
    }

    /// <summary>C++ <c>OnMW_TOURNAMENTRESULT_REQ</c> (SSHandler.cpp:18263): the results on the tournament maps; the final's winners
    /// get the reward.</summary>
    private void OnMW_TOURNAMENTRESULT_REQ(PacketReader r)
    {
        ushort tournamentId = r.ReadUInt16();
        byte step = r.ReadByte();
        bool won = r.ReadByte() != 0;
        uint winnerId = r.ReadUInt32(), loserId = r.ReadUInt32(), blueHide = r.ReadUInt32(), redHide = r.ReadUInt32();
        byte count = r.ReadByte();
        if (step is not (TnmtQFinal or TnmtSFinal or TnmtFinal)) return;
        int id = MatchIndex(step);
        var winners = new List<TournamentPlayer>();
        var win = FindTournamentPlayer(winnerId);
        if (win is not null) { win.Result[id] = won ? TnmtWinWin : TnmtWinLose; winners.Add(win); }
        var los = FindTournamentPlayer(loserId);
        if (los is not null) los.Result[id] = TnmtWinLose;
        for (int i = 0; i < count; i++)
        {
            if (FindTournamentPlayer(r.ReadUInt32()) is not { } member) continue;
            if (win is not null && win.EntryId == member.EntryId && win.Slot == member.Slot) { member.Result[id] = won ? TnmtWinWin : TnmtWinLose; winners.Add(member); }
            if (los is not null && los.EntryId == member.EntryId && los.Slot == member.Slot) member.Result[id] = TnmtWinLose;
        }
        var w = new PacketWriter(Msg.CS_TOURNAMENTRESULT_ACK);
        w.WriteByte(step); w.WriteByte(won ? (byte)1 : (byte)0); w.WriteUInt32(winnerId); w.WriteUInt32(loserId); w.WriteUInt32(blueHide); w.WriteUInt32(redHide);
        var msg = w.ToArray();
        foreach (var s in _state.AllInGame()) if (s.IsMain && s.Char is { } c && IsTournamentMap(c.MapId)) s.Send(msg);

        if (step != TnmtFinal || !won || win is null || HeroStore is not { } db) return;
        byte month = LastRankMonth;
        foreach (var p in winners.DistinctBy(p => p.CharId))
            if (FindByName(p.Name) is { IsMain: true, Char: { } c })
            {
                bool shield = c.FindInven(Proto.InvenEquip)?.Items.Any(i => i.Template?.Kind == IkShieldKind) == true;
                _ = TournamentReward(db, month, tournamentId, p.EntryId, p.ChiefId, c.CharId, shield);
            }
    }

    /// <summary>C++ <c>OnDM_TOURNAMENTREWARD_REQ/ACK</c>: <c>TTournamentReward</c>; for the first tournament the winner's fame-list
    /// place goes to the world (<c>MW_FAMERANKUPDATE_ACK</c>).</summary>
    private async Task TournamentReward(IHeroStore db, byte month, ushort tournamentId, byte entryId, uint chiefId, uint winnerId, bool shield)
    {
        var reward = await Safe(() => db.TournamentRewardAsync(month, tournamentId, entryId, chiefId, winnerId, shield), null);
        if (tournamentId != 1 || reward is not { } rw || rw.Type >= FrtCount || month >= MonthCount || rw.Order >= FameRankCount) return;
        var w = new PacketWriter(Msg.MW_FAMERANKUPDATE_ACK);
        w.WriteByte(rw.Type); w.WriteByte(month); w.WriteByte(rw.Order);
        new MonthRanker
        {
            CharId = winnerId, Name = rw.Name, MonthRank = rw.MonthRank, TotalRank = rw.TotalRank, MonthPoint = rw.MonthPoint,
            TotalPoint = rw.TotalPoint, MonthWin = rw.MonthWin, MonthLose = rw.MonthLose, TotalWin = rw.TotalWin, TotalLose = rw.TotalLose,
            Country = rw.Country, Level = rw.Level, Class = rw.Class, Race = rw.Race, Sex = rw.Sex, Face = rw.Face, Hair = rw.Hair, Guild = rw.Guild,
        }.Write(w);
        _world.Send(w);
    }

    // ================================ coming in, going out, dying ================================

    /// <summary>C++ <c>InitMap</c>'s lounge and tournament maps (TMapSvr.cpp:8116): out of the tournament, home; a fighter not on its
    /// match's map, or a spectator below the stands, home; a fighter on its map is reset and becomes the tournament character.</summary>
    private void TournamentEnterMap(ClientSession s, Character ch)
    {
        ushort home = ch.Persist.LastSpawnId != 0 ? ch.Persist.LastSpawnId : ch.Persist.SpawnId;
        if (ch.MapId == TournamentLoungeMap)
        {
            if (!CanDoTournament(TnmtEnter)) Teleport(s, ch, home);
            return;
        }
        if (!IsTournamentMap(ch.MapId)) return;
        if (!CanDoTournament(TnmtEnter)) { Teleport(s, ch, home); return; }
        if (FindTournamentPlayer(ch.CharId) is { } tp)
        {
            ushort map = TournamentMapOf(ch.CharId, _tnmtGroup);
            if ((map == 0 && ch.PosY < 7) || (map != 0 && map != ch.MapId)) { Teleport(s, ch, home); return; }
            if (map != 0)
            {
                ch.Persist.Aftermath = 0;
                foreach (var skill in ch.Skills) skill.ResetCooldown();
                ch.ItemCoolTime.Clear();
                s.Send(new PacketWriter(Msg.CS_RESETCOOLTIME_ACK).WriteUInt16(0));
                ReleaseMaintainPlayer(s, ch, notify: true);
                tp.MapId = ch.MapId;
                SendCS_CHARSTATINFO_ACK(s, ch);
            }
        }
        else if (ch.PosY < 7) { Teleport(s, ch, home); return; }
        foreach (var pet in ch.Recalls.Values.Where(m => m.PetId != 0).ToList())                // the pet goes (not a ridden one)
            if (ch.Riding == 0) SendMW_RECALLMONDEL_ACK(ch.CharId, s.Key, pet.Id);
        SendTournamentEnter(s, ch, MatchOf(ch.MapId));
        if (FindTournamentPlayer(ch.CharId, active: true) is not null) SetTournamentChar(s, ch);
    }

    /// <summary>C++ <c>ExitMAP</c>'s tournament map (TMapSvr.cpp:7367): a fighter leaving its match's map mid-match loses it if its
    /// side is gone; the tournament character is put back.</summary>
    private void TournamentExitMap(ClientSession s, Character ch)
    {
        if (!IsTournamentMap(ch.MapId)) return;
        if (FindTournamentPlayer(ch.CharId, active: true) is { } tp)
        {
            tp.MapId = 0;
            var m = MatchOf(ch.MapId);
            if (_tnmtStep is TnmtQFinal or TnmtSFinal or TnmtFinal && m.EndTick != 0 && TournamentMapOf(tp.CharId, _tnmtGroup) == ch.MapId
                && !CanDoMatch(m))
            {
                EndTournament(m, out bool won, out uint winner, out uint loser);
                SendTournamentResultToWorld(_tnmtStep, won, winner, loser);
            }
        }
        ExitTournamentChar(s, ch);
    }

    /// <summary>C++ <c>MapKillPoint</c> (TPlayer.cpp:2149): a fighter down scores for the other side.</summary>
    private void TournamentOnDeath(Character ch)
    {
        if (!IsTournamentMap(ch.MapId) || !_tnmtMatches.TryGetValue(ch.MapId, out var m) || !m.Players.TryGetValue(ch.CharId, out var p)) return;
        if (m.Blue == p.Slot) m.AtkKill++; else m.DefKill++;
    }

    /// <summary>C++ <c>EraseMaintainSkill</c>'s hide time (TObjBase.cpp:2666): a hide buff ending during a match counts for its side.</summary>
    private void TournamentHideEnded(Character ch, MaintainSkill buff)
    {
        if (!IsTournamentMap(ch.MapId) || _tnmtStep is not (TnmtQFinal or TnmtSFinal or TnmtFinal)) return;
        if ((buff.Template ?? _templates.Skill(buff.SkillId))?.IsHideSkill != true || !_tnmtMatches.TryGetValue(ch.MapId, out var m)) return;
        SetHideTick(m, ch.CharId, buff.MaintainTick != 0 ? buff.MaintainTick : unchecked(NowMs - buff.ChargeTick));
    }

    /// <summary>C++ <c>CheckPeaceZone</c>'s tournament part: the lounge always, a tournament map except during a match.</summary>
    private bool InTournamentPeace(Character ch)
        => ch.MapId == TournamentLoungeMap || (IsTournamentMap(ch.MapId) && _tnmtStep is not (TnmtQFinal or TnmtSFinal or TnmtFinal));

    /// <summary>The C++ <c>IsTournamentPlayer()</c> guards: a tournament character can't trade, sell, mail, store or keep things.</summary>
    private static bool IsTournamentChar(Character? ch) => ch?.TournamentBackup is not null;

    /// <summary>The C++ <c>IsTournamentPlayer()</c> guards (CSHandler.cpp): what a tournament character can't do.</summary>
    private static readonly HashSet<ushort> TournamentCharBlocked = new()
    {
        Msg.CS_CABINETPUTIN_REQ, Msg.CS_CABINETTAKEOUT_REQ, Msg.CS_CABINETLIST_REQ, Msg.CS_CABINETITEMLIST_REQ, Msg.CS_CABINETOPEN_REQ,
        Msg.CS_GUILDCABINETPUTIN_REQ, Msg.CS_ITEMBUY_REQ, Msg.CS_MAGICITEMBUY_REQ, Msg.CS_POSTSEND_REQ, Msg.CS_POSTVIEW_REQ,
        Msg.CS_POSTDEL_REQ, Msg.CS_POSTGETITEM_REQ, Msg.CS_POSTRETURN_REQ, Msg.CS_DEALITEMASK_REQ, Msg.CS_DEALITEMRLY_REQ,
        Msg.CS_DEALITEMADD_REQ, Msg.CS_DEALITEM_REQ, Msg.CS_STOREOPEN_REQ, Msg.CS_STORECLOSE_REQ, Msg.CS_STOREITEMLIST_REQ,
        Msg.CS_STOREITEMBUY_REQ,
    };

    /// <summary>The C++ <c>IsTournamentMap()</c> guards: what is off on a tournament map.</summary>
    private static readonly HashSet<ushort> TournamentMapBlocked = new()
        { Msg.CS_GUILDTACTICSINVITE_REQ, Msg.CS_PETEFFECTCHANGE_REQ, Msg.CS_CHANGECOMPANIONEFFECT_REQ };

    private const byte PetCantUse = 6;                                                          // PET_CANTUSE

    /// <summary>True when the request is refused for the tournament (answered where the C++ answers).</summary>
    private bool TournamentRefuses(ClientSession s, ushort id)
    {
        if (s.Char is not { } ch) return false;
        if (IsTournamentChar(ch))
        {
            if (TournamentCharBlocked.Contains(id)) return true;
            if (id == Msg.CS_ITEMSELL_REQ) { SendCS_ITEMSELL_ACK(s, ch, ItemSellResult.NotFound); return true; }
        }
        if (!IsTournamentMap(ch.MapId)) return false;
        if (TournamentMapBlocked.Contains(id)) return true;
        if (id == Msg.CS_PETRECALL_REQ) { SendCS_PETRECALL_ACK(s, PetCantUse); return true; }
        return false;
    }

    /// <summary>C++ <c>OnCS_ITEMUSE_REQ</c>'s tournament part (CSHandler.cpp:9160): in the lounge or a tournament map only a fighter
    /// uses items, and not potions, the two named items or the skill-1016 items.</summary>
    private bool TournamentItemBanned(Character ch, Item item)
    {
        if (!IsTournamentMap(ch.MapId) && ch.MapId != TournamentLoungeMap) return false;
        if (FindTournamentPlayer(ch.CharId, active: true) is null) return true;
        var t = item.Template;
        return t is { Kind: 42 or 43 } || item.TemplateId is 6818 or 7688
               || (t is { Type: ItUse, Kind: IkSkill } && t.UseValue == TnmtCheerItemSkillValue);
    }

    /// <summary>C++ <c>OnCS_SKILLUSE_REQ</c>'s tournament part (CSHandler.cpp:2633, 2671): on a tournament map only a fighter casts, and
    /// not skill 233; an entry of class mask 16 can't use 528, 529 or 507 there.</summary>
    private SkillUseResult? TournamentSkillBanned(Character ch, ushort skillId)
    {
        if (!IsTournamentMap(ch.MapId)) return null;
        if (FindTournamentPlayer(ch.CharId, active: true) is not { } tp || skillId == TournamentCantUseSkill) return SkillUseResult.NotFound;
        if (_tournaments.TryGetValue(tp.EntryId, out var e) && e.Class == 16 && skillId is 528 or 529 or 507) return SkillUseResult.WrongRegion;
        return null;
    }

    // ================================ the tournament character ================================

    /// <summary>C++ <c>SetTournamentChar</c> (TMapSvr.cpp:11445): the character set aside, the tournament one given.</summary>
    private void SetTournamentChar(ClientSession s, Character ch)
    {
        if (!IsTournamentMap(ch.MapId) || FindTournamentPlayer(ch.CharId, active: true) is null || ch.TournamentBackup is not null) return;
        SaveCharData(s);                                                                       // TournamentCharClear: saved first
        var bak = new TournamentBackup
        {
            Level = ch.Level,
            Invens = ch.Invens.ToList(),
            Skills = ch.Skills.ToList(),
            Maintains = ch.MaintainSkills.ToList(),
            Hotkeys = ch.HotkeyPages.Select(CopyHotkeys).ToList(),
        };
        ch.TournamentBackup = bak;
        foreach (var bag in ch.Invens)                                                         // ClearChar(FALSE)
        {
            foreach (var item in bag.Items) SendCS_DELITEM_ACK(s, bag.InvenId, item);
            if (bag.InvenId != Proto.InvenDefault && bag.InvenId != Proto.InvenEquip) SendCS_INVENDEL_ACK(s, 0, bag.InvenId);
        }
        ch.Invens.Clear();
        ch.Skills.Clear();
        var end = ch.MaintainSkills.Select(m => BuildSkillEndAck(ch.CharId, OtPc, m.SkillId)).ToList();
        ch.MaintainSkills.Clear();
        foreach (var e in end) s.Send(e);

        ch.Level = _templates.LimitedLevel != 0 ? _templates.LimitedLevel : ch.Level;
        foreach (var t in _templates.Skills.Values.Where(t => t.CanLearn != 0 && t.IsClassMatch(ch.Class)).OrderBy(t => t.Id))
            ch.Skills.Add(new Skill { SkillId = t.Id, Level = t.MaxLevel, Template = t });
        foreach (var (invenId, itemId, eld) in _templates.TnmtInvens)
        {
            ch.Invens.Add(Inven.FromTemplate(invenId, itemId, _templates, eld: eld));
            if (invenId != Proto.InvenDefault && invenId != Proto.InvenEquip) SendCS_INVENADD_ACK(s, 0, invenId, itemId, 0);
        }
        var oldEquip = bak.Invens.FirstOrDefault(b => b.InvenId == Proto.InvenEquip);
        if (_templates.TnmtItems.TryGetValue(ch.Class, out var items))
            foreach (var (invenId, row) in items)
            {
                if (ch.FindInven(invenId) is not { } bag || ItemFromRow(row) is not { Template: { } t } item) continue;
                if (t.Kind is >= Ik1Hand and <= IkBack && oldEquip?.Items.FirstOrDefault(o => o.Template is { } ot && ot.SlotId == t.SlotId
                        && (t.Kind > IkShieldKind || ot.Kind == t.Kind)) is { } worn)
                {
                    item.MoggItemId = worn.MoggItemId != 0 ? worn.MoggItemId : worn.TemplateId;   // looks like what it wore
                    item.GradeEffect = worn.GradeEffect;
                }
                bag.Items.Add(item);
                SendCS_ADDITEM_ACK(s, invenId, item);
            }
        if (ch.FindInven(Proto.InvenEquip) is { } equip && oldEquip is not null)              // the costumes stay on
            foreach (var costume in oldEquip.Items.Where(i => i.ItemSlot is >= EsCostumeHat and <= EsCostumeRace))
            {
                var copy = costume.Clone();
                equip.Items.Add(copy);
                SendCS_ADDITEM_ACK(s, Proto.InvenEquip, copy);
            }
        SendTournamentCharState(s, ch);
    }

    /// <summary>C++ <c>ExitTournamentCharClear</c> (TPlayer.cpp:6110) + <c>ResetChar</c>: the character given back.</summary>
    private void ExitTournamentChar(ClientSession s, Character ch)
    {
        if (ch.TournamentBackup is not { } bak) return;
        ch.TournamentBackup = null;
        foreach (var bag in ch.Invens)
        {
            foreach (var item in bag.Items) SendCS_DELITEM_ACK(s, bag.InvenId, item);
            if (bag.InvenId != Proto.InvenDefault && bag.InvenId != Proto.InvenEquip) SendCS_INVENDEL_ACK(s, 0, bag.InvenId);
        }
        foreach (var m in ch.MaintainSkills) s.Send(BuildSkillEndAck(ch.CharId, OtPc, m.SkillId));
        ch.Invens.Clear(); ch.Invens.AddRange(bak.Invens);
        ch.Skills.Clear(); ch.Skills.AddRange(bak.Skills);
        ch.MaintainSkills.Clear(); ch.MaintainSkills.AddRange(bak.Maintains);
        ch.HotkeyPages.Clear(); ch.HotkeyPages.AddRange(bak.Hotkeys);
        ch.Level = bak.Level;

        foreach (var bag in ch.Invens)                                                         // ResetChar
        {
            if (bag.InvenId != Proto.InvenDefault && bag.InvenId != Proto.InvenEquip) SendCS_INVENADD_ACK(s, 0, bag.InvenId, bag.TemplateId, bag.EndTime);
            foreach (var item in bag.Items) SendCS_ADDITEM_ACK(s, bag.InvenId, item);
        }
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
        var buffs = new PacketWriter(Msg.CS_EXITTOURNAMETMAINTAIN_ACK);
        buffs.WriteByte((byte)ch.MaintainSkills.Count);
        foreach (var m in ch.MaintainSkills)
        {
            buffs.WriteUInt16(m.SkillId); buffs.WriteByte(m.Level); buffs.WriteUInt32(m.RemainTick); buffs.WriteUInt32(m.AttackId);
            buffs.WriteByte(m.AttackType); buffs.WriteUInt32(m.HostId); buffs.WriteByte(m.HostType); buffs.WriteByte(m.Hit);
            buffs.WriteUInt16(m.AttackLevel); buffs.WriteByte(m.AttackerLevel); buffs.WriteUInt32(m.PysMinPower); buffs.WriteUInt32(m.PysMaxPower);
            buffs.WriteUInt32(m.MgMinPower); buffs.WriteUInt32(m.MgMaxPower); buffs.WriteByte(m.CanSelect); buffs.WriteByte(m.AttackCountry);
            buffs.WriteFloat(m.PosX); buffs.WriteFloat(m.PosY); buffs.WriteFloat(m.PosZ);
        }
        s.Send(buffs);
        foreach (var page in ch.HotkeyPages)
            SendCS_HOTKEYCHANGE_ACK(s, page.InvenKey, page.Slots.Select((k, i) => ((byte)i, k.Type, k.Id)).ToArray());
        SendTournamentCharState(s, ch);
    }

    /// <summary>The end of both swaps: full health, the skills, the level, the looks and the stats.</summary>
    private void SendTournamentCharState(ClientSession s, Character ch)
    {
        ch.Hp = MaxHpFor(ch);
        ch.Mp = MaxMpFor(ch);
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
        SendCS_SKILLLIST_ACK(s, ch);
        var level = new PacketWriter(Msg.CS_LEVEL_ACK);
        level.WriteUInt32(ch.CharId); level.WriteByte(ch.Level); level.WriteByte(0);
        s.Send(level);
        SendSelfHpMp(s, ch.CharId, ch.Hp, ch.Hp, ch.Mp, ch.Mp);
        s.Send(BuildCS_EQUIP_ACK(ch));
        SendCS_CHARSTATINFO_ACK(s, ch);
    }

    private static HotkeyPage CopyHotkeys(HotkeyPage page)
    {
        var copy = new HotkeyPage { InvenKey = page.InvenKey, Save = page.Save };
        Array.Copy(page.Slots, copy.Slots, HotkeyPage.SlotCount);
        return copy;
    }
}
