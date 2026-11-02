using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The arena, batch S6d (C++ CSHandler.cpp:17340, 21086, 21106; TMapSvr.cpp:3640 (load), 11925 (ArenaLeave), 11969 (ArenaEnd);
/// SSHandler.cpp:240 (the timer); CSSender.cpp:6023, 6131, 7022, 7091; and the guards listed below).
/// <list type="bullet">
/// <item>A party chief signs the party up at an arena's NPC (<c>CS_ARENA_REQ</c>, command 0), paying the fee: the members on the map
/// (between half the arena's size, rounded up, and its size) are sent inside as a team, healed, their buffs, summons and cooldowns
/// cleared; the world is told (<c>MW_ARENAJOIN_ACK</c>), which splits off the members left out. With both sides taken, a party waits
/// in the queue (the info window lists it).</item>
/// <item>With two teams the arena is <c>BS_READY</c>: a minute later the battle starts (<c>BS_BATTLE</c>, 4 minutes), with countdowns
/// (<c>SM_ARENA_COUNTDOWN</c>) in the last 10 seconds of each. A side wiped out loses; at the time limit the side with more standing
/// wins, a tie replays (back to ready) and a second tie goes to the defending winner. The loser is sent out; the winner gets the
/// fee by mail and its streak grows (the best streak's team is the ranking); the next queued party comes in.</item>
/// <item>A chief can give up a waiting arena (command 1). Leaving the map, logging out or leaving the party takes a fighter out —
/// a team down to its last fighter loses.</item>
/// <item>Inside: no fighting before the battle (a peace zone), no PvP points, no party invites, item use, channel change or member
/// recall, no skill 233; fighters and outsiders can't hit each other; a fighter's position is saved as the arena's exit.</item>
/// </list>
/// <para>Fixed C++ bugs: the ranking window read past the end when arena 1 or 2 is missing (now 0 entries); a fighter outside
/// and someone inside are kept apart on the hit itself (the C++ compared the attacker with the reporting client); a member standing
/// far from the arena was sent in by a long teleport, which left the map and so the arena (now placed inside first).</para>
/// </summary>
public sealed partial class MapService
{
    private const byte ArenaSuccess = 0, ArenaFail = 1, ArenaMoney = 2, ArenaChief = 4, ArenaMaxParty = 5, ArenaMinParty = 6,
        ArenaChannel = 7;                                                                        // ARENA_RESULT
    private const byte BsReady = 3, TnpcArena = 24, SmArenaCountdown = 28;
    private const uint ArenaReadySecs = 60, ArenaBattleSecs = 4 * 60, MsgArenaWinTitle = 47, MsgArenaWinMessage = 48;
    private const ushort TournamentCantUseSkill = 233;

    private sealed class Arena
    {
        public required ArenaRow Row;
        public required SpawnPosRow In, Out;
        public readonly SortedDictionary<uint, ClientSession>[] Fighters = { new(), new() };
        public byte Status;
        public long Tick;
        public byte WinCount, DrawCount, MaxWinCount;
        public readonly byte[] RoundWins = new byte[2];
        public ushort WinPartyId;
        public readonly List<(string Name, byte Level, byte Class)> TopPlayers = new();
        public readonly List<List<ClientSession>> Queue = new();
    }

    private readonly SortedDictionary<ushort, Arena> _arenas = new();

    /// <summary>C++ the <c>CTBLArena</c> load (TMapSvr.cpp:3640): an arena whose inside is on a map this server holds.</summary>
    private void InitArenas()
    {
        _arenas.Clear();
        foreach (var row in _templates.Arenas)
            if ((!row.QuestMatch || row.Type is 3 or 5)
                && _templates.SpawnPositions.TryGetValue(row.InPos, out var inPos) && OwnsMap(inPos.MapId)
                && _templates.SpawnPositions.TryGetValue(row.OutPos, out var outPos))
                _arenas[row.Id] = new Arena { Row = row, In = inPos, Out = outPos };
    }

    private Arena? ArenaOf(Character ch) => ch.ArenaId != 0 && _arenas.TryGetValue(ch.ArenaId, out var a) ? a : null;

    /// <summary>C++ <c>CheckPeaceZone</c>'s arena part: no fighting until the battle starts.</summary>
    private bool InArenaPeace(Character ch) => ArenaOf(ch) is { } a && a.Status != BsBattle;

    /// <summary>Whether an attacker and a target are kept apart: one is in an arena and the other is not in the same one.</summary>
    private static bool ArenaApart(Character a, Character b) => (a.ArenaId != 0 || b.ArenaId != 0) && a.ArenaId != b.ArenaId;

    private IEnumerable<ClientSession> ArenaViewers(Arena a)
        => _state.PlayersAroundCell(DefaultChannel, a.In.MapId, MapGrid.KeyOf(a.In.PosX, a.In.PosZ)).ToList();

    /// <summary>C++ <c>OnMove</c> + <c>Teleport</c>: placed at the spot first, so the teleport is a short hop on the same map (a long one
    /// would go through the world and leave the map — and with it the arena; fixed: the C++ did not do it on the way in).</summary>
    private void ArenaMove(ClientSession s, Character ch, SpawnPosRow pos)
    {
        if (s.State == EnterState.InGame && ch.MapId == pos.MapId) RelocateAndExchangeView(s, pos.PosX, pos.PosY, pos.PosZ);
        Teleport(s, ch, pos.Id);
    }

    private static void SendArenaAck(ClientSession s, byte result) => s.Send(new PacketWriter(Msg.CS_ARENA_ACK).WriteByte(result));

    private static void SendArenaTeam(ClientSession s, ushort arenaId, byte team, IReadOnlyList<uint> fighters)
    {
        var w = new PacketWriter(Msg.CS_ARENATEAM_ACK);
        w.WriteUInt16(arenaId); w.WriteByte(team); w.WriteByte((byte)fighters.Count);
        foreach (uint id in fighters) w.WriteUInt32(id);
        s.Send(w);
    }

    private static void SendArenaCountdown(ClientSession s, Arena a, uint seconds)
    {
        var w = new PacketWriter(Msg.CS_SYSTEMMSG_ACK);
        w.WriteByte(SmArenaCountdown); w.WriteUInt16(a.Row.Id); w.WriteUInt32(seconds); w.WriteByte(a.Status);
        w.WriteByte(a.Row.QuestMatch ? Math.Max(a.RoundWins[0], a.RoundWins[1]) : a.WinCount);
        s.Send(w);
    }

    private void SendArenaJoinToWorld(Character ch, uint key, bool join, IReadOnlyList<uint> party)
    {
        var w = new PacketWriter(Msg.MW_ARENAJOIN_ACK);
        w.WriteUInt32(ch.CharId); w.WriteUInt32(key); w.WriteByte(join ? (byte)1 : (byte)0); w.WriteUInt32((uint)party.Count);
        foreach (uint id in party) w.WriteUInt32(id);
        _world.Send(w);
    }

    /// <summary>C++ the fighter's reset on entering (and at the end): aftermath, cooldowns, buffs, summons, full health.</summary>
    private void ArenaReset(ClientSession s, Character ch)
    {
        ch.Persist.Aftermath = 0;
        foreach (var skill in ch.Skills) if (skill.SkillId < 3000) skill.ResetCooldown();                // ResetCoolTime(0)
        ch.ItemCoolTime.Clear();
        s.Send(new PacketWriter(Msg.CS_RESETCOOLTIME_ACK).WriteUInt16(0));
        ReleaseMaintainPlayer(s, ch, notify: true);
        foreach (var m in ch.Recalls.Values.ToList()) SendMW_RECALLMONDEL_ACK(ch.CharId, s.Key, m.Id);   // DeleteAllRecallMon
        foreach (var m in ch.SelfObjs.Values.ToList()) DeleteSelfObj(ch, m.Id);                         // DeleteAllSelfMon
        Revive(s, ch, AftermathNone, null, ch.Level);
    }

    /// <summary>Sends a party into an arena as <paramref name="team"/> (the C++ join, also used for the next queued party).</summary>
    private void ArenaEnter(Arena a, byte team, IReadOnlyList<ClientSession> party, IEnumerable<ClientSession> viewers)
    {
        var ids = party.Select(p => p.CharId).ToList();
        foreach (var v in viewers) SendArenaTeam(v, a.Row.Id, team, ids);
        foreach (var p in party)
        {
            if (p.Char is not { } c) continue;
            c.ArenaId = a.Row.Id;
            c.ArenaTeam = team;
            a.Fighters[team - 1][c.CharId] = p;
            ArenaReset(p, c);
            ArenaMove(p, c, a.In);
        }
    }

    private bool ValidQuestArenaParty(Arena a, IReadOnlyList<ClientSession> party, ClientSession chief)
    {
        if (chief.Char is not { } leader || party.Count != a.Row.Type || !leader.UseMoney(a.Row.Fee, commit: false)) return false;
        return party.All(p => p.IsMain && p.State == EnterState.InGame && !p.TeleportPending
            && p.Channel == DefaultChannel && ReferenceEquals(_state.FindByChar(p.CharId), p)
            && p.Char is { ArenaId: 0, TournamentBackup: null } c && c.MapId == leader.MapId
            && c.GetPartyId() == leader.GetPartyId() && c.GetPartyChiefId() == leader.CharId);
    }

    /// <summary>C++ <c>OnCS_ARENA_REQ</c> (CSHandler.cpp:17340): sign up (command 0) or give up a waiting arena (command 1).</summary>
    private void OnCS_ARENA_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is not { } ch) return;
        if (s.Channel != DefaultChannel) { SendArenaAck(s, ArenaChannel); return; }
        byte command = r.ReadByte();
        ushort npcId = r.ReadUInt16(), arenaId = r.ReadUInt16();
        if (ch.CharId != ch.GetPartyChiefId()) { SendArenaAck(s, ArenaChief); return; }

        if (command != 0)
        {
            if (_arenas.TryGetValue(arenaId, out var waiting) && waiting.Status == BsNormal
                && (!waiting.Row.QuestMatch || (ch.ArenaId == arenaId && waiting.Fighters.Any(side => side.ContainsKey(ch.CharId)))))
                ArenaEnd(waiting, s);
            return;
        }
        if (_state.FindNpc(npcId) is not { ArenaId: not 0 } npc || !_arenas.TryGetValue(npc.ArenaId, out var a)) { SendArenaAck(s, ArenaFail); return; }
        if (a.Row.QuestMatch && !CanTalk(npc, ch)) { SendArenaAck(s, ArenaFail); return; }
        if (ch.ArenaId != 0) { SendArenaAck(s, ArenaFail); return; }
        if (!ch.UseMoney(a.Row.Fee, commit: false)) { SendArenaAck(s, ArenaMoney); return; }

        byte team = a.Fighters[0].Count == 0 ? (byte)1 : a.Fighters[1].Count == 0 ? (byte)2 : (byte)0;
        var mapPlayers = _state.AllInGame().Where(p => p.Char is { } c && c.MapId == ch.MapId && p.Channel == s.Channel).ToList();
        var party = mapPlayers.Where(p => p.Char!.GetPartyId() != 0 && p.Char.GetPartyId() == ch.GetPartyId()).ToList();
        if (party.Count > a.Row.Type) { SendArenaAck(s, ArenaMaxParty); return; }
        if (party.Count < (a.Row.QuestMatch ? a.Row.Type : (byte)(a.Row.Type / 2.0f + 0.99f))) { SendArenaAck(s, ArenaMinParty); return; }
        if (a.Row.QuestMatch && !ValidQuestArenaParty(a, party, s)) { SendArenaAck(s, ArenaFail); return; }

        if (team == 0)                                                                         // both sides taken: the queue
        {
            if (a.Queue.Any(q => q.Any(p => party.Any(m => m.CharId == p.CharId)))) { SendArenaAck(s, ArenaFail); return; }
            a.Queue.Add(party);
            SendArenaInfo(s, a);
            return;
        }
        SendArenaJoinToWorld(ch, s.Key, true, party.Select(p => p.CharId).ToList());
        ch.UseMoney(a.Row.Fee, commit: true);
        SendCS_MONEY_ACK(s, ch);
        if (a.Row.QuestMatch) ScheduleQuestCheckpoint(ch);
        bool start = (a.Fighters[0].Count != 0 && team == 2) || (a.Fighters[1].Count != 0 && team == 1);
        if (start) { a.Status = BsReady; a.Tick = NowMs; Array.Clear(a.RoundWins); }
        ArenaEnter(a, team, party, mapPlayers);
        if (start) foreach (var p in mapPlayers) SendArenaCountdown(p, a, ArenaReadySecs);
    }

    /// <summary>C++ the arena block of the one-second timer (SSHandler.cpp:240).</summary>
    private void ArenaTick()
    {
        foreach (var a in _arenas.Values.ToList())
        {
            uint secs = (uint)Math.Max(0, (NowMs - a.Tick) / 1000);
            uint? notice = null;
            if (a.Status == BsReady)
            {
                if (secs >= ArenaReadySecs) { a.Status = BsBattle; notice = ArenaBattleSecs; }
                else if (secs >= ArenaReadySecs - 10) notice = ArenaReadySecs - secs;
            }
            else if (a.Status == BsBattle)
            {
                if (secs >= ArenaBattleSecs) ArenaEnd(a, null);
                else if (secs >= ArenaBattleSecs - 10) notice = ArenaBattleSecs - secs;
            }
            if (notice is { } n)
                foreach (var side in a.Fighters) foreach (var p in side.Values.ToList()) SendArenaCountdown(p, a, n);
        }
    }

    /// <summary>C++ <c>ArenaLeave</c> (TMapSvr.cpp:11925): a fighter leaves; its team losing when only one more is left.
    /// <paramref name="teleport"/> false when it is logging out (its position is saved as the exit anyway).</summary>
    private void ArenaLeave(ClientSession s, Character ch, bool teleport = true)
    {
        if (ch.ArenaId == 0 || ch.ArenaTeam == 0 || ArenaOf(ch) is not { } a) return;
        if (a.Row.QuestMatch || a.Fighters[ch.ArenaTeam - 1].Count <= 2) { ArenaEnd(a, s); return; }
        a.Fighters[ch.ArenaTeam - 1].Remove(ch.CharId);
        ch.ArenaId = 0;
        ch.ArenaTeam = 0;
        foreach (var v in _state.InView(s).ToList()) SendArenaTeam(v, a.Row.Id, 0, new[] { ch.CharId });
        if (teleport) ArenaMove(s, ch, a.Out);
    }

    /// <summary>C++ the arena queue's clean-up (logout, map change, party change): the party no longer waits.</summary>
    private void ArenaDequeue(uint charId)
    {
        foreach (var a in _arenas.Values) a.Queue.RemoveAll(q => q.Any(p => p.CharId == charId));
    }

    /// <summary>C++ <c>ArenaEnd</c> (TMapSvr.cpp:11969): the result, the loser out, the winner's fee and streak, the next party in.
    /// <paramref name="leaver"/> is the chief giving up or the fighter leaving; null at the time limit or a wipe-out.</summary>
    private void ArenaEnd(Arena a, ClientSession? leaver)
    {
        int standing0 = 0, standing1 = 0, outSide = 0;
        ClientSession? f0 = null, f1 = null;
        for (int side = 0; side < 2; side++)
            foreach (var p in a.Fighters[side].Values.ToList())
            {
                if (p.Char is not { } c) continue;
                if (c.Hp != 0) { if (side == 0) standing0++; else standing1++; }
                if (side == 0 ? f0 is null || c.GetPartyChiefId() == c.CharId : f1 is null || c.GetPartyChiefId() == c.CharId)
                    if (side == 0) f0 = p; else f1 = p;
                ArenaReset(p, c);
                if (leaver is not null && leaver.CharId == c.CharId) outSide = side;
            }
        var viewers = ArenaViewers(a);

        ClientSession? win = null, lose = null;
        if (leaver is not null)
        {
            win = outSide == 1 ? (a.Status == BsNormal ? f1 : f0) : (a.Status == BsNormal ? f0 : f1);
            lose = outSide == 1 ? f1 : f0;
        }
        else if (standing0 > standing1) { win = f0; lose = f1; }
        else if (standing0 < standing1) { win = f1; lose = f0; }
        else if (a.DrawCount != 0)
        {
            if (a.WinCount == 0) { win = f0; lose = f1; }
            else
            {
                win = f0 is { Char: { } c0 } && c0.GetPartyId() == a.WinPartyId ? f0 : f1;
                lose = win == f0 ? f1 : f0;
            }
        }

        if (a.Row.QuestMatch && a.Status == BsBattle && leaver is null
            && win?.Char is { ArenaTeam: > 0 and <= 2 } roundWinner && lose is not null && win != lose)
        {
            int side = roundWinner.ArenaTeam - 1;
            a.RoundWins[side]++;
            if (a.RoundWins[side] < 2)
            {
                a.Status = BsReady; a.Tick = NowMs; a.DrawCount = 0;
                foreach (var player in viewers) SendArenaCountdown(player, a, ArenaReadySecs);
                return; // rounds have no fee mail or quest credit
            }
            foreach (var player in a.Fighters[side].Values.ToArray())
            {
                if (!player.IsMain || player.State != EnterState.InGame || player.Char is not { } winningCharacter) continue;
                using var responses = player.DeferResponses();
                try
                {
                    CheckQuest(player, 0, winningCharacter.PosX, winningCharacter.PosY, winningCharacter.PosZ,
                        a.Row.Type == 3 ? 1u : 2u, QttBattleArena, 0, 1);
                    FlushQuestCheckpoints();
                }
                catch { responses.Discard(); throw; }
            }
        }

        if (lose?.Char is { ArenaTeam: not 0 } loser)
        {
            int side = loser.ArenaTeam - 1;
            var ids = new List<uint>();
            foreach (var p in a.Fighters[side].Values.ToList())
            {
                if (p.Char is not { } c) continue;
                c.ArenaId = 0;
                c.ArenaTeam = 0;
                ids.Add(c.CharId);
                ArenaMove(p, c, a.Out);
            }
            a.Fighters[side].Clear();
            foreach (var v in viewers) SendArenaTeam(v, a.Row.Id, 0, ids);
            SendArenaJoinToWorld(loser, lose.Key, false, ids);
        }
        if (win?.Char is { } winner)
        {
            var (gold, silver, cooper) = SplitMoney(a.Row.Fee);
            _ = MailOperator(winner.CharId, winner.Name, SvrMsg(MsgArenaWinTitle), NetString(SvrMsg(MsgArenaWinMessage)),
                Array.Empty<(ItemTemplate, byte)>(), gold, silver, cooper);
        }

        if (win is null && lose is null) { a.Status = BsReady; a.Tick = NowMs; a.DrawCount++; }
        else if (win is not null && win != lose && win.Char is { } w)
        {
            a.Status = BsNormal;
            a.Tick = 0;
            if (w.GetPartyId() == a.WinPartyId)
            {
                a.WinCount++;
                if (a.WinCount > a.MaxWinCount && w.ArenaTeam != 0)
                {
                    a.TopPlayers.Clear();
                    foreach (var p in a.Fighters[w.ArenaTeam - 1].Values) if (p.Char is { } c) a.TopPlayers.Add((c.Name, c.Level, c.Class));
                    a.MaxWinCount = a.WinCount;
                }
            }
            else a.WinCount = 1;
            a.WinPartyId = w.GetPartyId();
            a.DrawCount = 0;
        }
        else { a.Status = BsNormal; a.Tick = 0; a.WinCount = 0; a.WinPartyId = 0; a.DrawCount = 0; }

        if (a.Status == BsNormal && a.Queue.Count != 0)
        {
            byte team = a.Fighters[0].Count == 0 ? (byte)1 : a.Fighters[1].Count == 0 ? (byte)2 : (byte)0;
            if (team == 0) return;
            List<ClientSession> next;
            ClientSession? chief;
            do
            {
                next = a.Queue[0]; a.Queue.RemoveAt(0);
                chief = next.FirstOrDefault(p => p.Char is { } c && c.GetPartyChiefId() == c.CharId);
                if (!a.Row.QuestMatch || (chief is not null && ValidQuestArenaParty(a, next, chief))) break;
                next = new(); chief = null;
            } while (a.Queue.Count != 0);
            if (chief?.Char is not { } cc || FindPlayer(cc.CharId, chief.Key) is null) return;
            cc.UseMoney(a.Row.Fee, commit: true);
            SendCS_MONEY_ACK(chief, cc);
            if (a.Row.QuestMatch) ScheduleQuestCheckpoint(cc);
            bool start = (a.Fighters[0].Count != 0 && team == 2) || (a.Fighters[1].Count != 0 && team == 1);
            if (start) { a.Status = BsReady; a.Tick = NowMs; Array.Clear(a.RoundWins); }
            // A queued member can be outside the arena's view cells, just as on initial admission.
            var party = a.Row.QuestMatch ? next
                : viewers.Where(p => p.Char is { } c && c.GetPartyId() != 0 && c.GetPartyId() == cc.GetPartyId()).ToList();
            ArenaEnter(a, team, party, viewers);
            SendArenaJoinToWorld(cc, chief.Key, true, next.Select(p => p.CharId).ToList());
        }
        foreach (var v in viewers) SendArenaCountdown(v, a, a.Status == BsReady ? ArenaReadySecs : 0);
    }

    /// <summary>C++ <c>CTPlayer::OnDie</c>'s arena part: a team with nobody standing loses.</summary>
    private void ArenaOnDeath(Character ch)
    {
        if (ch.ArenaTeam == 0 || ArenaOf(ch) is not { } a) return;
        if (a.Fighters[ch.ArenaTeam - 1].Values.All(p => p.Char is not { Hp: > 0 })) ArenaEnd(a, null);
    }

    /// <summary>C++ <c>OnCS_ARENAINFO_REQ</c> + <c>SendCS_ARENAINFO_ACK</c> (CSSender.cpp:7022).</summary>
    private void OnCS_ARENAINFO_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is null) return;
        if (_arenas.TryGetValue(r.ReadByte(), out var a)) SendArenaInfo(s, a);
    }

    private static Character? ArenaLeader(IEnumerable<ClientSession> side)
    {
        Character? pick = null;
        foreach (var p in side) if (p.Char is { } c && (pick is null || c.GetPartyChiefId() == c.CharId)) pick = c;
        return pick;
    }

    private void SendArenaInfo(ClientSession s, Arena a)
    {
        var w = new PacketWriter(Msg.CS_ARENAINFO_ACK);
        bool first = a.Fighters[0].Count != 0, second = a.Fighters[1].Count != 0;
        w.WriteUInt32(first ? 1u : 0u); w.WriteUInt32(second ? 1u : 0u);                    // BOOL
        if (first && ArenaLeader(a.Fighters[0].Values) is { } c0)
        {
            w.WriteString(c0.Name); w.WriteByte(c0.Level); w.WriteByte(c0.Country);
            w.WriteUInt32(c0.GetPartyId() == a.WinPartyId ? 1u : 0u); w.WriteByte(a.WinCount);
        }
        if (second && ArenaLeader(a.Fighters[1].Values) is { } c1) { w.WriteString(c1.Name); w.WriteByte(c1.Level); w.WriteByte(c1.Country); }
        var queued = a.Queue.Select(ArenaLeader).Where(c => c is not null).ToList();
        w.WriteByte((byte)queued.Count);
        foreach (var c in queued) w.WriteString(c!.Name);
        s.Send(w);
    }

    /// <summary>C++ <c>OnCS_ARENARANKING_REQ</c> + <c>SendCS_ARENARANKING_ACK</c> (CSSender.cpp:7091): arenas 1 and 2's best streak.</summary>
    private void OnCS_ARENARANKING_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is null) return;
        r.ReadByte();
        _arenas.TryGetValue(1, out var a1);
        _arenas.TryGetValue(2, out var a2);
        var w = new PacketWriter(Msg.CS_ARENARANKING_ACK);
        w.WriteByte((byte)(a1?.TopPlayers.Count ?? 0)); w.WriteByte(a1?.MaxWinCount ?? 0);
        w.WriteByte((byte)(a2?.TopPlayers.Count ?? 0)); w.WriteByte(a2?.MaxWinCount ?? 0);
        foreach (var a in new[] { a1, a2 })
            if (a is not null) foreach (var (name, level, cls) in a.TopPlayers) { w.WriteString(name); w.WriteByte(level); w.WriteByte(cls); }
        s.Send(w);
    }

    /// <summary>C++ <c>SendQuestListPossible</c>'s arena part (TPlayer.cpp:5145): an arena NPC in the list shows its two teams.</summary>
    private void SendArenaRosters(ClientSession s, Npc npc, HashSet<ushort> shown)
    {
        if (npc.Type != TnpcArena || npc.ArenaId == 0 || !_arenas.TryGetValue(npc.ArenaId, out var a) || !shown.Add(a.Row.Id)) return;
        for (byte side = 0; side < 2; side++)
            if (a.Fighters[side].Count != 0) SendArenaTeam(s, a.Row.Id, (byte)(side + 1), a.Fighters[side].Keys.ToList());
    }
}
