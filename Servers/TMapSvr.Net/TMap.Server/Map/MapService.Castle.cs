using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Territory battles, batch E: the castles (C++ <c>LOCAL_CASTLE</c>) — a guild war for the holy relics, once a week.
/// <list type="bullet">
/// <item><b>Sign-up</b> (<c>CS_CASTLEAPPLY_REQ</c>, CSHandler.cpp:9026): the chief of the castle's defending or attacking guild
/// signs a member up for the war while it is open (<c>CanApplyCastle</c>); the world keeps it and answers
/// (<c>MW_CASTLEAPPLY_REQ</c>): the member's castle and camp — one already in the castle is sent out.</item>
/// <item><b>Coming in</b> (InitMap, TMapSvr.cpp:8083): only the signed-up members of the two guilds, while the castle can be
/// entered (<c>CanEnterCastle</c>) — they get <c>CS_ENTERCASTLE_ACK</c> and the god balls and towers; anyone else is sent out.
/// Leaving drops a carried god ball and says <c>CS_LEAVECASTLE_ACK</c>. The castle portals (<c>PCT_ATTACKPOS</c> /
/// <c>PCT_DEFENDPOS</c>) take each camp to its side.</item>
/// <item><b>The war</b> (<c>MW_CASTLEENABLE_REQ</c>, SSHandler.cpp:12048): the gates close, the three gatekeepers come out and
/// the castle's monsters turn suspended; the power race starts (<c>CTMap::StartWar</c>): each side 1800 power plus its share of
/// 800 from the forts its country holds, and two god balls each. Without both guilds it ends at once (<c>WIN_NOWAR</c>); else
/// every second each god ball mounted on a tower moves a point of power to its side and everyone in the castle is shown the
/// balance (<c>CS_BALANCEOFPOWER_ACK</c>); a side at 0 loses (<c>WIN_POWER</c>); the time running out ends it too
/// (<c>WIN_TIME</c>). A player's death there counts a kill point for the other side.</item>
/// <item><b>God balls</b> (<c>CS_TAKEGODBALL_REQ</c> / <c>CS_MOUNTGODBALL_REQ</c> / <c>CS_DEMOUNTGODBALL_REQ</c>): a guild's member
/// picks up one of its balls and carries it to a free tower (a fresh ball then comes out in 15 s); a ball on a tower of the other
/// side can be knocked off by a carrier (it goes back to its spot in 15 s). A carrier's death or transformation drops it (back
/// in 20 s); one carried too long (5 min) goes back. These run on the C++ AI thread's delays (<c>SM_GODBALLCMD</c>), each ball
/// command superseding the last (its key).</item>
/// <item><b>The end</b> (<c>EndWar</c>, TMapSvr.cpp:10653): the stronger side wins, everyone in the castle sees the result
/// (<c>CS_ENDWAR_ACK</c>); the castle's monsters come back; the winner's members in the castle get the reward item by mail (the
/// chief the chief's item; others in the castle a consolation of 5 × item 18044 and 500 silver) and stat exp; it is saved
/// (<c>TSaveCastleOccupy</c>), and the world tells every map (<c>MW_CASTLEOCCUPY_REQ</c>): the owner, the forts' week wiped,
/// the news (<c>SM_CASTLE_END</c>) and the points (<c>PVPE_ENTRY</c> / <c>WIN</c> / <c>DEFEND</c>, MapService.Fort.cs).</item>
/// </list>
/// <para>The castle guards' shop is in MapService.GuildTactics.cs. Operator entry and guild-destination eviction follow the
/// native checks. SQL occupation succeeds before reward dispatch; the ball commands run on the 1-second timer.
/// TODO(TERRITORY_BASELINE): persist settlement/reward delivery and active-war recovery in SQL before claiming restart safety;
/// MailOperatorItem now has SQL queue/receipt recovery per accepted letter; occupation, all recipients, points and World
/// result delivery still do not share an atomic settlement. See Gameforge/territory-wars.json.</para>
/// </summary>
public sealed partial class MapService
{
    // TWIN_TYPE, GODBALL_CMD (TMapType.h:189), CASTLEAPPLY_RESULT, BS_NOBATTLE, PCT_ATTACKPOS / DEFENDPOS
    private const byte WinTime = 1, WinPower = 2, WinNowar = 3;
    private const byte GbEndWar = 0, GbStartWar = 1, GbMountBall = 2, GbDemountBall = 3, GbDropBall = 4, GbTakeBall = 5;
    private static readonly uint[] GbDelayMs = { 0, 1000, 15000, 15000, 20000, 300000 };
    private const byte CbsSuccess = 0, CbsNotFound = 2, CbsNotReady = 3, CbsCantApply = 4;
    private const byte BsNoBattle = 7, CampNone = 0, SmCastleEnd = 16;
    private const byte PctAttackPos = 3, PctDefendPos = 4;
    private const uint DefaultWarPower = 1800, LocalBonusPoint = 800;
    private const int CastleOccupyStatExp = 2000, CastleInternal = 1;
    private const ushort ConsolationItem = 18044;

    /// <summary>The god ball commands waiting for their delay (C++ <c>m_vGBBUF</c>).</summary>
    private readonly List<GodBallCmd> _gbCmds = new();

    private Territory? CastleOfMap(ushort mapId) => _castles.FirstOrDefault(c => c.Valid && c.Zone.MapId == mapId);

    /// <summary>Builds each castle's war state with its towers and ball spots (C++ <c>CTBLGodTower</c> / <c>CTBLGodBall</c>);
    /// called from <see cref="InitTerritories"/>.</summary>
    private void InitCastleWars()
    {
        foreach (var c in _castles)
        {
            var war = new CastleWar();
            foreach (var t in _templates.GodTowers.Where(t => t.MapId == c.Zone.MapId))
                war.Towers[t.Id] = new GodTower(t.Id, t.PosX, t.PosY, t.PosZ);
            foreach (var b in _templates.GodBallSpots.Where(b => b.MapId == c.Zone.MapId))
                war.Spots[b.Id] = new GodBallSpot(b.Id, b.Camp, b.PosX, b.PosY, b.PosZ);
            c.War = war;
        }
    }

    // ================================ the war ================================

    /// <summary>The war-phase part of <c>OnMW_CASTLEENABLE_REQ</c> for a valid castle that changed status (MapService.Territory.cs).</summary>
    private void CastlePhase(Territory c, byte status, uint second)
    {
        var z = c.Zone;
        switch (status)
        {
            case BsNormal:
                SetZoneSwitches(z, SwcClose);
                SendEveryoneOut(z.MapId);                                   // C++ LeaveCastleMap
                ClearMapMonsters(z.MapId);
                break;
            case BsBattle:
                c.Records.Clear();
                c.CanBattle = true;
                SetZoneSwitches(z, SwcClose);
                foreach (var id in new[] { z.LGateKeeperSpawnId, z.RGateKeeperSpawnId, z.CGateKeeperSpawnId }) AddWarSpawn(id, suspend: true);
                foreach (var id in ZoneSpawnIds(c))
                    foreach (var sp in _spawns.Where(p => p.Def.Spawn.Id == id && p.Channel == DefaultChannel)) sp.Suspended = true;
                StartWar(c, NowMs + second * 1000L);
                if (c.DefGuildId == 0 || c.AtkGuildId == 0) EndCastleWar(c, WinNowar);
                else QueueGodBallCmd(0, GbStartWar, c.Id, 0, "");
                break;
            case BsPeace:
                EndCastleWar(c, WinTime);
                break;
        }
    }

    /// <summary>C++ <c>CTMap::ClearMonster</c>: every live monster on the map dies.</summary>
    private void ClearMapMonsters(ushort mapId)
    {
        foreach (var sp in _spawns.Where(p => p.Channel == DefaultChannel && p.Def.Spawn.MapId == mapId).ToList())
            foreach (var slot in sp.Slots)
                if (slot.Live is { Hp: > 0, Dead: false } m) { m.Hp = 0; OnMonsterDeath(m); }
    }

    /// <summary>C++ <c>CTMap::GetLocalBonus</c>: the defending side's share of the 800 bonus points, by the forts each country held
    /// (an even split when none).</summary>
    private static uint LocalBonus(byte defCountry, ushort dPoint, ushort cPoint)
    {
        uint def = defCountry == TcontryD ? dPoint : cPoint, atk = defCountry == TcontryD ? cPoint : dPoint;
        if (def + atk == 0) def = atk = LocalBonusPoint;
        return def * LocalBonusPoint / (def + atk);
    }

    /// <summary>C++ <c>CTMap::StartWar</c> (TMap.cpp:1786): the powers, the end, and two god balls a side.</summary>
    private void StartWar(Territory c, long endMs)
    {
        var war = c.War!;
        uint bonus = LocalBonus(c.DefCountry, c.DPoint, c.CPoint);
        war.DefPower = DefaultWarPower + bonus;
        war.AtkPower = DefaultWarPower + LocalBonusPoint - bonus;
        war.DefKillPoint = war.AtkKillPoint = 0;
        war.EndMs = Math.Max(1, endMs);                                         // 0 means "no war"
        war.DefBallMax = war.AtkBallMax = 2;
        for (int i = 0; i < 2; i++) InitGodBall(c, CampDefend, c.DefGuildId);
        for (int i = 0; i < 2; i++) InitGodBall(c, CampAttack, c.AtkGuildId);
    }

    /// <summary>C++ <c>CTMapSvrModule::EndWar</c> (TMapSvr.cpp:10653): the winner — the only side, or the stronger — told to everyone
    /// in the castle, the result saved, the war state cleared, the castle's monsters back, the rewards.</summary>
    private void EndCastleWar(Territory c, byte type)
    {
        c.CanBattle = false;
        var war = c.War;
        if (war is null || war.EndMs == 0) return;
        uint win = c.AtkGuildId != 0 && c.DefGuildId == 0 ? c.AtkGuildId
            : c.AtkGuildId == 0 && c.DefGuildId != 0 ? c.DefGuildId
            : war.AtkPower > war.DefPower ? c.AtkGuildId : c.DefGuildId;
        uint bonus = LocalBonus(c.DefCountry, c.DPoint, c.CPoint);

        var w = new PacketWriter(Msg.CS_ENDWAR_ACK, capacity: 64);
        w.WriteByte(type); w.WriteUInt32(win); w.WriteUInt32(war.DefPower); w.WriteUInt32(war.AtkPower);
        w.WriteString(c.DefName); w.WriteUInt32(war.DefPower); w.WriteUInt16((ushort)bonus); w.WriteUInt16(war.DefKillPoint);
        w.WriteString(c.AtkName); w.WriteUInt32(war.AtkPower); w.WriteUInt16((ushort)(LocalBonusPoint - bonus)); w.WriteUInt16(war.AtkKillPoint);
        var msg = w.ToArray();
        foreach (var s in PlayersOnMap(c.Zone.MapId)) if (s.IsMain) s.Send(msg);

        CastleOccupy(c, win == c.Guild ? OccupyDefend : OccupyAccept, win, win == c.DefGuildId ? c.AtkGuildId : c.DefGuildId, () =>
        {
            CastleReward(c, win);
            foreach (var s in _state.AllInGame().ToList())
                if (s.Char is { } ch && ch.LocalId == c.Id && ch.GuildId == win) IncreaseStatExp(ch, CastleOccupyStatExp / 2);
        });
        EndMapWar(c);
        foreach (var id in ZoneSpawnIds(c)) ReleaseWarSpawn(id);
        c.Status = BsPeace;
    }

    /// <summary>C++ <c>CTMap::EndWar</c> (TMap.cpp:1813): no more war; the balls gone, their carriers relieved.</summary>
    private void EndMapWar(Territory c)
    {
        var war = c.War!;
        war.DefBallMax = war.AtkBallMax = 0;
        war.DefPower = war.AtkPower = 0;
        war.DefKillPoint = war.AtkKillPoint = 0;
        war.EndMs = 0;
        foreach (var spot in war.Spots.Values) spot.Move = true;              // ResetTempGodBall
        foreach (var t in war.Towers.Values) t.Ball = null;
        foreach (var b in war.Balls.Values.ToList()) LeaveMapBall(c, b, erase: false);
        war.Balls.Clear();
        foreach (var s in PlayersOnMap(c.Zone.MapId).ToList())
            if (s.IsMain && s.Char is { GodBall: not 0 } ch) RemoveGodBall(c, ch, null);
    }

    /// <summary>C++ <c>CastleReward</c> (TMapSvr.cpp:9954): to every member of the winning guild in the castle, the castle's item by
    /// mail (the chief the chief's); to anyone else of it there, 5 × item 18044 and 500 silver.</summary>
    private void CastleReward(Territory c, uint guild)
    {
        if (guild == 0) return;
        var normal = c.Zone.NormalItem != 0 ? _templates.Item(c.Zone.NormalItem) : null;
        var chief = c.Zone.ChiefItem != 0 ? _templates.Item(c.Zone.ChiefItem) : null;
        foreach (var s in PlayersOnMap(c.Zone.MapId).ToList())
        {
            if (!s.IsMain || s.Char is not { } ch || GuildOf(ch) != guild) continue;
            if (ch.GuildDuty == GuildDutyChief && ch.GuildId == guild && chief is not null)
                _ = MailOperatorItem(ch.CharId, ch.Name, "Castle reward", "", chief, 1);
            else if (normal is not null && ch.GuildId == guild)
                _ = MailOperatorItem(ch.CharId, ch.Name, "Castle reward", "", normal, 1);
            else if (_templates.Item(ConsolationItem) is { } consolation)
                _ = MailOperatorItem(ch.CharId, ch.Name, "Castle reward", "", consolation, 5, silver: 500);
        }
    }

    /// <summary>C++ <c>SendDM_CASTLEOCCUPY_REQ</c> → <c>TSaveCastleOccupy</c> → <c>OnDM_CASTLEOCCUPY_ACK</c>: saved, and the world
    /// told. DB-free, the proc's choice is made here (a castle taken by no guild is refused, as the proc does).</summary>
    private void CastleOccupy(Territory c, byte type, uint guild, uint loseGuild, Action? confirmed = null)
    {
        void Done(int ret, byte country)
        {
            if (ret != 0) return;
            var w = new PacketWriter(Msg.MW_CASTLEOCCUPY_ACK);
            w.WriteByte(type); w.WriteUInt16(c.Id); w.WriteUInt32(guild); w.WriteByte(country); w.WriteUInt32(loseGuild);
            _world.Send(w);
            confirmed?.Invoke();
            _log.LogInformation("Castle {Id} {How}: guild {Guild}, country {Country}.", c.Id, type == OccupyAccept ? "taken" : "held", guild, country);
        }
        if (TerritoryOccupationStore is not { } db)
        {
            if (type == OccupyDefend) Done(0, c.Country);
            else if (guild != 0)
                Done(0, _state.AllInGame().Select(s => s.Char).FirstOrDefault(ch => ch is { GuildDuty: GuildDutyChief } && ch.GuildId == guild)?.Country ?? c.Country);
            else Done(1, 0);
            return;
        }
        QueueTerritoryWrite(c, async () =>
        {
            var result = await SaveTerritoryResultAsync(() => db.SaveCastleOccupyAsync(c.Id, type, guild));
            Done(result.Ret, result.Country);
        });
    }

    /// <summary>C++ <c>OnMW_CASTLEOCCUPY_REQ</c> (SSHandler.cpp:11465): the new owner (a new guild loses the hero), the war's
    /// sides and points reset, the next war a week on, the forts' week wiped; the news; the points.</summary>
    private void OnMW_CASTLEOCCUPY_REQ(PacketReader r)
    {
        byte type = r.ReadByte();
        ushort id = r.ReadUInt16();
        uint guild = r.ReadUInt32();
        byte country = r.ReadByte();
        string guildName = r.ReadString();
        if (!_territories.TryGetValue(id, out var c) || c.Type != LocalType.Castle) return;
        if (c.Guild != guild) { c.Hero = ""; c.HeroTime = 0; }
        uint prev = c.Guild;
        c.Guild = guild; c.Country = country; c.GuildName = guildName; c.LastOccType = type;
        c.DPoint = c.CPoint = 0; c.DefGuildId = c.AtkGuildId = 0; c.DefCount = c.AtkCount = 0; c.DefCountry = TcontryN;
        c.AtkName = c.DefName = "";
        c.NextDefend += WeekOne;
        c.Points.Clear(); c.Top3[TcontryD].Clear(); c.Top3[TcontryC].Clear();
        foreach (var l in c.Locals) { Array.Clear(l.OccupyGuild); Array.Clear(l.OccupyType); }
        NotifyLocalInfo(SmCastleEnd, 0, 0, c.Zone.Name, guildName);
        if (type == OccupyAccept)
            foreach (var s in _state.AllInGame().ToList())
                if (s.IsMain && s.Char is { } ch && CheckMapGuild(ch)) ReturnFromGuildDestination(s, ch);
        if (!c.Valid) return;
        PvPEntry(c);
        if (prev != guild) PvPWin(c, prev); else PvPDefend(c);
    }

    /// <summary>C++ <c>OnMW_ENDWAR_REQ</c> (SSHandler.cpp:14406): the castle has no war this time.</summary>
    private void OnMW_ENDWAR_REQ(PacketReader r)
    {
        if (_territories.TryGetValue(r.ReadUInt16(), out var c) && c.Type == LocalType.Castle && c.Valid) c.Status = BsNoBattle;
    }

    // ================================ the power race ================================

    /// <summary>C++ <c>SendSM_GODBALLCMD_REQ</c> / <c>DoGBCMD</c>: a ball command to run after its delay (a ball's own supersedes
    /// its last: <paramref name="key"/>).</summary>
    private void QueueGodBallCmd(ushort ball, byte cmd, ushort castle, uint key, string name)
        => _gbCmds.Add(new GodBallCmd(ball, cmd, castle, key, name, NowMs + GbDelayMs[cmd]));

    private void DoGodBallCmd(Territory c, byte cmd, GodBall ball, string name = "")
        => QueueGodBallCmd(ball.Id, cmd, c.Id, ++ball.Key, name);

    /// <summary>The due ball commands (C++ the AI thread's <c>m_vGBBUF</c> sweep → <c>OnSM_GODBALLCMD_ACK</c>).</summary>
    private void RunGodBallCmds()
    {
        if (_gbCmds.Count == 0) return;
        var due = _gbCmds.Where(g => g.DueMs <= NowMs).ToList();
        _gbCmds.RemoveAll(g => g.DueMs <= NowMs);
        foreach (var g in due) OnGodBallCmd(g);
    }

    /// <summary>C++ <c>OnSM_GODBALLCMD_ACK</c> (SSHandler.cpp:606).</summary>
    private void OnGodBallCmd(GodBallCmd g)
    {
        if (!_territories.TryGetValue(g.Castle, out var c) || c.War is not { EndMs: not 0 } war) return;
        var ball = war.FindBall(g.Ball);
        if (g.Ball != 0 && ball is null) return;
        switch (g.Cmd)
        {
            case GbStartWar:
                if (CheckPower(c)) QueueGodBallCmd(g.Ball, g.Cmd, g.Castle, g.Key, g.Name);
                else EndCastleWar(c, WinPower);
                break;
            case GbMountBall:
                if (ball!.Key == g.Key) InitGodBall(c, ball.Camp, ball.Guild);
                break;
            case GbDemountBall or GbDropBall:
                if (ball!.Key == g.Key) MoveGodBall(c, ball);
                break;
            case GbTakeBall:
                if (ball!.Key == g.Key && FindByName(g.Name)?.Char is { } carrier)
                {
                    RemoveGodBall(c, carrier, ball);
                    MoveGodBall(c, ball);
                }
                break;
        }
    }

    /// <summary>C++ <c>CTMap::CheckPower</c> (TMap.cpp:1660): each mounted ball moves a point of power to its side; the balance,
    /// the time left, the kills and the carriers to everyone in the castle. False when a side ran out (or the time).</summary>
    private bool CheckPower(Territory c)
    {
        var war = c.War!;
        if (war.DefBallMax == 0 || war.AtkBallMax == 0) return false;
        foreach (var t in war.Towers.Values)
        {
            if (t.Ball is { } b)
            {
                if (b.Camp == CampAttack) { war.AtkPower++; war.DefPower--; }
                else if (b.Camp == CampDefend) { war.DefPower++; war.AtkPower--; }
            }
            if (war.AtkPower == 0 || war.DefPower == 0) return false;
        }
        long left = war.EndMs - NowMs;
        if (left <= 0 || left >= 86_400_000L) return false;
        float defShare = war.DefPower * 100.0f / (war.DefPower + war.AtkPower);
        var carriers = CarriersByCamp(war);
        var w = new PacketWriter(Msg.CS_BALANCEOFPOWER_ACK, capacity: 64);
        w.WriteFloat(defShare); w.WriteUInt32((uint)left); w.WriteUInt16(war.AtkKillPoint); w.WriteUInt16(war.DefKillPoint);
        foreach (var (name, _) in carriers) w.WriteString(name);
        foreach (var (_, keep) in carriers) w.WriteUInt16(keep);
        var msg = w.ToArray();
        foreach (var s in PlayersOnMap(c.Zone.MapId)) if (s.IsMain) s.Send(msg);
        return true;
    }

    /// <summary>C++ <c>CTMap::GetGodBallOwner</c>: the first two carriers of each side (attack, then defence) and how long they have
    /// held their ball (seconds).</summary>
    private List<(string Name, ushort Keep)> CarriersByCamp(CastleWar war)
    {
        var atk = new List<(string, ushort)>(); var def = new List<(string, ushort)>();
        long now = UnixNow();
        foreach (var b in war.Balls.Values)
        {
            if (b.Owner.Length == 0) continue;
            var list = b.Camp == CampAttack ? atk : b.Camp == CampDefend ? def : null;
            if (list is null) continue;
            if (list.Count == 2) list[1] = (b.Owner, (ushort)(now - b.OwnerSince));                   // sic: a third overwrites the second
            else list.Add((b.Owner, (ushort)(now - b.OwnerSince)));
        }
        while (atk.Count < 2) atk.Add(("", 0));
        while (def.Count < 2) def.Add(("", 0));
        return new List<(string, ushort)> { atk[0], atk[1], def[0], def[1] };
    }

    // ================================ god balls ================================

    /// <summary>C++ <c>CTMap::GenGodBall</c>: a free ball id of the camp (defence 1..towers, attack towers+1..2×towers) unless one is
    /// given, and a free spot of the camp.</summary>
    private static GodBallSpot? GenGodBall(CastleWar war, byte camp, ref ushort id)
    {
        if (id == 0)
        {
            int ts = war.Towers.Count;
            int from = camp == CampDefend ? 1 : ts + 1, to = camp == CampDefend ? ts : ts * 2;
            for (int i = from; i <= to; i++)
                if (war.FindBall((ushort)i) is null) { id = (ushort)i; break; }
            if (id == 0) return null;
        }
        foreach (var spot in war.Spots.Values)
            if (spot.Camp == camp && spot.Move) { spot.Move = false; return spot; }
        return null;
    }

    /// <summary>C++ <c>CTMap::InitGodBall</c>: a new ball of the camp on one of its spots.</summary>
    private void InitGodBall(Territory c, byte camp, uint guild)
    {
        ushort id = 0;
        if (GenGodBall(c.War!, camp, ref id) is not { } spot || id == 0) return;
        EnterMapBall(c, new GodBall { Id = id, SpotId = spot.Id, Camp = camp, Guild = guild, Castle = c.Id, X = spot.X, Y = spot.Y, Z = spot.Z });
    }

    /// <summary>C++ <c>CTMap::EnterMAP(LPTGODBALL)</c>: the ball lies on the ground, shown to everyone in the castle.</summary>
    private void EnterMapBall(Territory c, GodBall ball)
    {
        ball.Ground = true;
        var msg = BuildCS_ADDGODBALL_ACK(ball);
        foreach (var s in PlayersOnMap(c.Zone.MapId)) s.Send(msg);
        c.War!.Balls[ball.Id] = ball;
    }

    /// <summary>C++ <c>CTMap::LeaveMAP(LPTGODBALL)</c>: off the ground (taken away from everyone's sight), and off the map.</summary>
    private void LeaveMapBall(Territory c, GodBall ball, bool erase)
    {
        if (ball.Ground)
        {
            var w = new PacketWriter(Msg.CS_DELGODBALL_ACK, capacity: 2);
            w.WriteUInt16(ball.Id);
            var msg = w.ToArray();
            foreach (var s in PlayersOnMap(c.Zone.MapId)) s.Send(msg);
            ball.Ground = false;
        }
        if (erase) c.War!.Balls.Remove(ball.Id);
    }

    /// <summary>C++ <c>CTMap::MoveGodBall</c>: the ball goes back to a free spot of its camp, if the camp is short of balls.</summary>
    private void MoveGodBall(Territory c, GodBall ball)
    {
        var war = c.War!;
        LeaveMapBall(c, ball, erase: true);
        int count = war.Balls.Values.Count(b => b.Camp == ball.Camp);
        if (count >= (ball.Camp == CampDefend ? war.DefBallMax : war.AtkBallMax)) return;
        ushort id = ball.Id;
        if (GenGodBall(war, ball.Camp, ref id) is not { } spot) return;
        ball.Id = id; ball.SpotId = spot.Id; ball.X = spot.X; ball.Y = spot.Y; ball.Z = spot.Z;
        ball.Owner = ""; ball.OwnerSince = 0;
        EnterMapBall(c, ball);
    }

    private static byte[] BuildCS_ADDGODBALL_ACK(GodBall b)
    {
        var w = new PacketWriter(Msg.CS_ADDGODBALL_ACK, capacity: 18);
        w.WriteUInt16(b.Id); w.WriteByte(b.Camp); w.WriteByte((byte)(b.Ground ? 1 : 0)); w.WriteFloat(b.X); w.WriteFloat(b.Y); w.WriteFloat(b.Z);
        return w.ToArray();
    }

    private static byte[] BuildCS_ADDGODTOWER_ACK(GodTower t)
    {
        var w = new PacketWriter(Msg.CS_ADDGODTOWER_ACK, capacity: 17);
        w.WriteUInt16(t.Id); w.WriteFloat(t.X); w.WriteFloat(t.Y); w.WriteFloat(t.Z);
        w.WriteUInt16(t.Ball?.Id ?? 0); w.WriteByte(t.Ball?.Camp ?? 0);
        return w.ToArray();
    }

    private void SendToCastle(Territory c, byte[] msg)
    {
        foreach (var s in PlayersOnMap(c.Zone.MapId)) s.Send(msg);
    }

    /// <summary>C++ <c>OnCS_TAKEGODBALL_REQ</c> (CSHandler.cpp:13731): a member picks up a ball of its guild lying free; it goes
    /// back on its own after 5 minutes. The carrier loses the resurrection grace.</summary>
    private void OnCS_TAKEGODBALL_REQ(ClientSession s, PacketReader r)
    {
        ushort id = r.ReadUInt16();
        if (!s.IsMain || s.Char is not { } ch || CastleOfMap(ch.MapId) is not { War: { } war } c) return;
        if (!war.Balls.TryGetValue(id, out var ball) || ball.Guild != GuildOf(ch) || ch.GodBall != 0 || ball.Owner.Length != 0) return;
        if (ch.MaintainSkills.Any(m => m.Template?.IsTrans() == true)) return;
        if (war.Spots.TryGetValue(ball.SpotId, out var spot)) spot.Move = true;      // MoveTempGodBall
        ch.GodBall = id;
        ball.Owner = ch.Name;
        ball.OwnerSince = UnixNow();
        LeaveMapBall(c, ball, erase: false);
        DoGodBallCmd(c, GbTakeBall, ball, ch.Name);
        var w = new PacketWriter(Msg.CS_TAKEGODBALL_ACK, capacity: 6);
        w.WriteUInt32(ch.CharId); w.WriteUInt16(id);
        SendToCastle(c, w.ToArray());
        EraseMaintainById(s, ch, TrevivalSkill);
    }

    /// <summary>C++ <c>OnCS_MOUNTGODBALL_REQ</c> (CSHandler.cpp:13785): the carried ball goes on a free tower; a fresh one of its camp
    /// comes out 15 s later.</summary>
    private void OnCS_MOUNTGODBALL_REQ(ClientSession s, PacketReader r)
    {
        ushort towerId = r.ReadUInt16();
        if (!s.IsMain || s.Char is not { } ch || CastleOfMap(ch.MapId) is not { War: { } war } c) return;
        if (!war.Towers.TryGetValue(towerId, out var tower) || tower.Ball is not null || ch.GodBall == 0) return;
        if (!war.Balls.TryGetValue(ch.GodBall, out var ball)) return;
        DoGodBallCmd(c, GbMountBall, ball);
        war.Balls.Remove(ball.Id);
        tower.Ball = ball;
        RemoveGodBall(c, ch, ball);
        var w = new PacketWriter(Msg.CS_MOUNTGODBALL_ACK, capacity: 9);
        w.WriteUInt16(towerId); w.WriteUInt16(ball.Id); w.WriteByte(ball.Camp); w.WriteUInt32(ch.CharId);
        SendToCastle(c, w.ToArray());
    }

    /// <summary>C++ <c>OnCS_DEMOUNTGODBALL_REQ</c> (CSHandler.cpp:13833): a carrier knocks the other side's ball off a tower; it lies
    /// there, and goes back to its spot 15 s later.</summary>
    private void OnCS_DEMOUNTGODBALL_REQ(ClientSession s, PacketReader r)
    {
        ushort towerId = r.ReadUInt16();
        if (!s.IsMain || s.Char is not { } ch || CastleOfMap(ch.MapId) is not { War: { } war } c) return;
        if (!war.Towers.TryGetValue(towerId, out var tower) || tower.Ball is not { } mounted || ch.GodBall == 0) return;
        if (!war.Balls.ContainsKey(ch.GodBall) || mounted.Guild == GuildOf(ch)) return;
        DelTowerGuards(c, towerId, s.Channel);                                     // the guards bought for this tower go
        war.Balls[mounted.Id] = mounted;
        DoGodBallCmd(c, GbDemountBall, mounted);
        tower.Ball = null;
        var w = new PacketWriter(Msg.CS_DEMOUNTGODBALL_ACK, capacity: 6);
        w.WriteUInt16(towerId); w.WriteUInt32(ch.CharId);
        SendToCastle(c, w.ToArray());
    }

    /// <summary>C++ <c>CTPlayer::DropGodBall</c> (TPlayer.cpp:2175): a carrier's ball falls where it stands, and goes back 20 s
    /// later. On death, a transformation and leaving the map.</summary>
    private void DropGodBall(Character ch)
    {
        if (ch.GodBall == 0 || CastleOfMap(ch.MapId) is not { War: { } war } c || !war.Balls.TryGetValue(ch.GodBall, out var ball)) return;
        ball.X = ch.PosX; ball.Y = ch.PosY; ball.Z = ch.PosZ;
        RemoveGodBall(c, ch, ball);
        EnterMapBall(c, ball);
        DoGodBallCmd(c, GbDropBall, ball);
    }

    /// <summary>C++ <c>CTPlayer::RemoveGodBall</c>: the player no longer carries a ball (everyone in the castle is told).</summary>
    private void RemoveGodBall(Territory c, Character ch, GodBall? ball)
    {
        ch.GodBall = 0;
        if (ball is not null) { ball.Owner = ""; ball.OwnerSince = 0; }
        var w = new PacketWriter(Msg.CS_REMOVEGODBALL_ACK, capacity: 4);
        w.WriteUInt32(ch.CharId);
        SendToCastle(c, w.ToArray());
    }

    /// <summary>C++ <c>CTPlayer::MapKillPoint</c> (TPlayer.cpp:2149): a death in a castle at war is a kill point for the other side.</summary>
    private void CastleKillPoint(Character victim)
    {
        if (CastleOfMap(victim.MapId) is not { Status: BsBattle, War: { } war } c) return;
        if (c.DefGuildId == GuildOf(victim)) war.AtkKillPoint++; else war.DefKillPoint++;
    }

    /// <summary>A player's death: the ball falls and the other side scores (C++ <c>CTPlayer::OnDie</c>).</summary>
    private void CastleOnDeath(Character ch)
    {
        DropGodBall(ch);
        CastleKillPoint(ch);
    }

    // ================================ coming in, signing up ================================

    /// <summary>C++ <c>CanEnterCastle</c>: while signing up is open, or during its war.</summary>
    private bool CanEnterCastle(Territory c) => CanApplyCastle(c) || c.Status == BsBattle;

    /// <summary>The castle part of C++ <c>InitMap</c> (TMapSvr.cpp:8083): a signed-up member of either guild comes in (with the
    /// balls and towers in sight); anyone else is sent out.</summary>
    private void CastleEnterMap(ClientSession s, Character ch)
    {
        if (CastleOfMap(ch.MapId) is not { } c) return;
        uint guild = GuildOf(ch);
        if (IsOperator(ch.CharId) || (CanEnterCastle(c) && (guild == c.DefGuildId || guild == c.AtkGuildId) && ch.Castle == c.Id && ch.Camp != CampNone))
        {
            var w = new PacketWriter(Msg.CS_ENTERCASTLE_ACK, capacity: 32);
            w.WriteUInt16(ch.Castle); w.WriteByte(ch.Camp); w.WriteString(c.AtkName); w.WriteString(c.DefName);
            s.Send(w);
            if (c.War is { } war)
            {
                foreach (var b in war.Balls.Values) s.Send(BuildCS_ADDGODBALL_ACK(b));
                foreach (var t in war.Towers.Values) s.Send(BuildCS_ADDGODTOWER_ACK(t));
            }
            return;
        }
        if (s.IsMain) Teleport(s, ch, ch.Persist.LastSpawnId != 0 ? ch.Persist.LastSpawnId : ch.Persist.SpawnId);
    }

    /// <summary>C++ <c>ExitMAP</c>'s castle part: a carried ball falls, and the castle says goodbye.</summary>
    private void CastleExitMap(ClientSession s, Character ch)
    {
        DropGodBall(ch);
        if (IsInCastle(ch)) s.Send(new PacketWriter(Msg.CS_LEAVECASTLE_ACK, capacity: 0));
    }

    /// <summary>C++ <c>OnCS_CASTLEAPPLY_REQ</c> (CSHandler.cpp:9026): a guild chief signs a member (or, with no castle, withdraws
    /// one) for the war of the castle its guild defends or attacks.</summary>
    private void OnCS_CASTLEAPPLY_REQ(ClientSession s, PacketReader r)
    {
        ushort castle = r.ReadUInt16();
        uint target = r.ReadUInt32();
        if (!s.IsMain || s.Char is not { } ch || ch.GuildDuty != GuildDutyChief) return;
        if (castle == 0) { SendMW_CASTLEAPPLY_ACK(s, castle, target, CampNone); return; }
        if (!_territories.TryGetValue(castle, out var c) || c.Type != LocalType.Castle) { SendCS_CASTLEAPPLY_ACK(s, CbsNotFound); return; }
        uint guild = GuildOf(ch);
        if (c.DefGuildId != guild && c.AtkGuildId != guild) { SendCS_CASTLEAPPLY_ACK(s, CbsCantApply); return; }
        if (!CanApplyCastle(c)) { SendCS_CASTLEAPPLY_ACK(s, CbsNotReady); return; }
        SendMW_CASTLEAPPLY_ACK(s, castle, target, c.DefGuildId == guild ? CampDefend : CampAttack);
    }

    private void SendMW_CASTLEAPPLY_ACK(ClientSession s, ushort castle, uint target, byte camp)
    {
        var w = new PacketWriter(Msg.MW_CASTLEAPPLY_ACK);
        w.WriteUInt32(s.CharId); w.WriteUInt32(s.Key); w.WriteUInt16(castle); w.WriteUInt32(target); w.WriteByte(camp);
        _world.Send(w);
    }

    private static void SendCS_CASTLEAPPLY_ACK(ClientSession s, byte result, ushort castle = 0, uint target = 0, byte camp = 0)
    {
        var w = new PacketWriter(Msg.CS_CASTLEAPPLY_ACK, capacity: 8);
        w.WriteByte(result); w.WriteUInt16(castle); w.WriteUInt32(target); w.WriteByte(camp);
        s.Send(w);
    }

    /// <summary>C++ <c>OnMW_CASTLEAPPLY_REQ</c> (SSHandler.cpp:12177): the world's answer to the chief; the signed-up member (when
    /// it is the chief itself) takes its castle and camp, and is sent out of a castle it stands in.</summary>
    private void OnMW_CASTLEAPPLY_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte result = r.ReadByte();
        ushort castle = r.ReadUInt16();
        uint target = r.ReadUInt32();
        byte camp = r.ReadByte();
        if (FindPlayer(charId, key) is not { IsMain: true, Char: { } ch } s) return;
        if (result != CbsSuccess) { SendCS_CASTLEAPPLY_ACK(s, result); return; }
        SendCS_CASTLEAPPLY_ACK(s, result, castle, target, camp);
        if (charId != target) return;
        ch.Castle = castle;
        ch.Camp = camp;
        if (IsInCastle(ch)) Teleport(s, ch, ch.Persist.LastSpawnId);
    }

    /// <summary>The castle portals (C++ <c>PCT_ATTACKPOS</c> / <c>PCT_DEFENDPOS</c>): one's own castle, in the camp, while it can be
    /// entered.</summary>
    private bool CheckCastlePortal(Character ch, PortalRow portal, byte condition)
        => _territories.TryGetValue(portal.LocalId, out var c) && c.Type == LocalType.Castle
           && ch.Camp == (condition == PctAttackPos ? CampAttack : CampDefend) && c.Id == ch.Castle && CanEnterCastle(c);
}

/// <summary>A castle's war (C++ the <c>CTMap</c> war fields of the castle's map, default channel).</summary>
public sealed class CastleWar
{
    /// <summary>C++ <c>m_dwEndTick</c> on the map clock (ms); 0 = no war.</summary>
    public long EndMs { get; set; }
    public uint DefPower { get; set; }
    public uint AtkPower { get; set; }
    public ushort DefKillPoint { get; set; }
    public ushort AtkKillPoint { get; set; }
    public byte DefBallMax { get; set; }
    public byte AtkBallMax { get; set; }
    /// <summary>C++ <c>m_mapTGODBALL</c> — the balls lying or carried (not those on a tower).</summary>
    public SortedDictionary<ushort, GodBall> Balls { get; } = new();
    /// <summary>C++ <c>m_mapTTEMPGODBALL</c> — the balls' starting spots.</summary>
    public SortedDictionary<ushort, GodBallSpot> Spots { get; } = new();
    public SortedDictionary<ushort, GodTower> Towers { get; } = new();

    /// <summary>C++ <c>CTMap::FindGodBall</c>: lying, carried, or on a tower.</summary>
    public GodBall? FindBall(ushort id)
        => Balls.TryGetValue(id, out var b) ? b : Towers.Values.FirstOrDefault(t => t.Ball?.Id == id)?.Ball;
}

/// <summary>A god ball (C++ <c>TGODBALL</c>).</summary>
public sealed class GodBall
{
    public ushort Id { get; set; }
    public ushort SpotId { get; set; }
    public byte Camp { get; init; }
    public uint Guild { get; init; }
    public uint Key { get; set; }
    public ushort Castle { get; init; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public string Owner { get; set; } = "";
    public long OwnerSince { get; set; }
    public bool Ground { get; set; }
}

/// <summary>A god ball's starting spot; <see cref="Move"/> = free (C++ <c>m_bMove</c>).</summary>
public sealed class GodBallSpot(ushort id, byte camp, float x, float y, float z)
{
    public ushort Id { get; } = id;
    public byte Camp { get; } = camp;
    public float X { get; } = x;
    public float Y { get; } = y;
    public float Z { get; } = z;
    public bool Move { get; set; } = true;
}

/// <summary>A god tower (C++ <c>TGODTOWER</c>) and the ball mounted on it.</summary>
public sealed class GodTower(ushort id, float x, float y, float z)
{
    public ushort Id { get; } = id;
    public float X { get; } = x;
    public float Y { get; } = y;
    public float Z { get; } = z;
    public GodBall? Ball { get; set; }
}

/// <summary>A ball command waiting for its delay (C++ <c>TGBBUF</c>).</summary>
public sealed record GodBallCmd(ushort Ball, byte Cmd, ushort Castle, uint Key, string Name, long DueMs);
