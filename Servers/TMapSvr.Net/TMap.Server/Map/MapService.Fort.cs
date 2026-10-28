using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Territory battles, batch D: the forts (C++ <c>LOCAL_OCCUPATION</c>) — a guild war for a fort, every day.
/// <list type="bullet">
/// <item><b>The war</b> (<c>MW_LOCALENABLE_REQ</c>, SSHandler.cpp:11194): in battle the fort's two gates close and its two
/// gatekeepers come out; its own monsters turn <i>suspended</i> (a dead one stays dead). At peace a fort nobody took stays with
/// its owner (<c>OCCUPY_DEFEND</c>), the gates open and its monsters come back; the guilds that hold a fort get its reward item by
/// mail if they are near one of its line's forts (<c>LocalReward</c>). At normal the boss and the gatekeepers go.</item>
/// <item><b>Gates and boss</b> (<c>CTMonster::OnBattleZoneEvent</c>): a gatekeeper's death opens its gate, and the first gate
/// open brings the fort's boss out (<c>SM_BATTLE_OPENGATE</c> to whoever is in the fort). The boss's death takes the fort for the
/// guild that did it the most damage (<c>m_mapGuildDamage</c>) — or, with no guild, for the killer's country — and pays the
/// killer (or the party nearby) the zone's god-monster points.</item>
/// <item><b>The capture</b> (<c>TSaveLocalOccupy</c> → <c>OnDM_LOCALOCCUPY_ACK</c>): the proc decides the owner; the fort's
/// monsters change sides (<c>CS_CHANGECOLOR_ACK</c>); its players of the owner guild gain stat exp (half
/// <c>LOCALOCCUPY_STATEXP</c> and 10 a kill there); the world is told (<c>MW_LOCALOCCUPY_ACK</c>) and tells every map
/// (<c>MW_LOCALOCCUPY_REQ</c>): the news (<c>SM_BATTLE_BOSSDIE</c>), <c>CS_LOCALOCCUPY_ACK</c> to everyone, and the new owner
/// (<c>ResetLocal</c>) — its week, next war and hero, the castle's scoreboard, the participants' points
/// (<c>PVPE_ENTRY</c>, with their records to the world, <c>MW_LOCALRECORD_ACK</c>) and the guilds' (<c>PVPE_WIN</c> /
/// <c>PVPE_DEFEND</c>, via <c>MW_GAINPVPPOINT_ACK</c>).</item>
/// <item><b>Kills inside a fort at war</b> use the local kill chart (<c>PVPS_LOCAL</c>), are written down per guild
/// (<c>LocalRecord</c>), and do not suffer the death-penalty cut.</item>
/// <item><b>Its owner's say</b>: an NPC or a portal of a fort serves the fort's country (<c>m_pLocal-&gt;m_bCountry</c>), a
/// <c>PCT_GUILD</c> portal its guild; NPC discounts and success bonuses for the owner guild and for the fort / castle heroes
/// (<c>GetDiscountRate</c>, <c>CalcProb</c>).</item>
/// </list>
/// <para><b>Fixed C++ bug:</b> <c>ResetLocal</c> marks the fort taken (<c>m_bOccupied</c>) after every result, and only the next
/// peace cleared it — so every other war of a fort that was held ended without its <c>OCCUPY_DEFEND</c> result. It is now cleared
/// when a war starts. <b>Deviations:</b> the proc's answer comes back on the next timer tick (the C++ posts it to its batch
/// thread); the stat-exp change is not sent
/// to the world's guild info (guilds are not ported).</para>
/// </summary>
public sealed partial class MapService
{
    // PVP_STATUS / PVP_EVENT (NetCode.h:2363/2371), TSYSMSG_TYPE, TOWNER_TYPE, DISCOUNT_CONDITION, GUILD_DUTY
    private const byte PvpsLocal = 2, PvpeGodMonKill = 4, PvpeEntry = 5, PvpeCount = 8;
    private const byte SmBattleOpenGate = 6, SmBattleBossDie = 7;
    private const byte TownerChar = 0, TownerGuild = 1;
    private const byte DccGuild = 1, DccLocalHero = 2, DccCastleHero = 3;
    private const byte GuildDutyChief = 2;
    private const int LocalOccupyStatExp = 1000, UnitSize = 1024, LoccupyInternal = 1;

    /// <summary>What the database answered, to be applied on the map's own thread (C++ <c>SayToBATCH</c>).</summary>
    private readonly ConcurrentQueue<Action> _dbResults = new();

    private void RunDbResults()
    {
        while (_dbResults.TryDequeue(out var apply)) apply();
    }

    /// <summary>C++ <c>CTObjBase::GetGuild</c>: the tactics guild one fights for, else one's own.</summary>
    private static uint GuildOf(Character ch) => ch.TacticsId != 0 ? ch.TacticsId : ch.GuildId;

    /// <summary>C++ <c>CTObjBase::OnDamage</c>'s guild part (TMonster.cpp:403): the damage a guild member of Defugel or Craxion
    /// (a Broa one only as a tactics member) does, by guild.</summary>
    private static void AddGuildDamage(Monster mon, Character owner, uint dmg)
    {
        if (dmg == 0 || WarCountryOf(owner) >= TcontryBroa || (owner.Country >= TcontryBroa && owner.TacticsId == 0)) return;
        uint guild = GuildOf(owner);
        if (guild != 0) mon.GuildDamage[guild] = mon.GuildDamage.GetValueOrDefault(guild) + dmg;
    }

    // ================================ the war ================================

    /// <summary>The fort part of <c>OnMW_LOCALENABLE_REQ</c> for a valid fort that changed status (MapService.Territory.cs).</summary>
    private void FortPhase(Territory t, byte status)
    {
        var z = t.Zone;
        switch (status)
        {
            case BsNormal:
                t.CanBattle = true;
                foreach (var id in new[] { z.BossSpawnId, z.LGateKeeperSpawnId, z.RGateKeeperSpawnId }) DelMonSpawn(id, DefaultChannel);
                break;
            case BsBattle:
                t.CanBattle = true;
                t.Occupied = false;                      // the C++ bug fixed (see the class remarks)
                t.Records.Clear();
                t.GateOpened = false;
                ChangeSwitchModule(DefaultChannel, z.MapId, z.LSwitchId, SwcClose);
                ChangeSwitchModule(DefaultChannel, z.MapId, z.RSwitchId, SwcClose);
                AddWarSpawn(z.LGateKeeperSpawnId, suspend: true);
                AddWarSpawn(z.RGateKeeperSpawnId, suspend: true);
                foreach (var id in ZoneSpawnIds(t))
                    foreach (var sp in _spawns.Where(p => p.Def.Spawn.Id == id && p.Channel == DefaultChannel)) sp.Suspended = true;
                break;
            case BsPeace:
                if (!t.Occupied) LocalOccupy(t, OccupyDefend, 0, 0);
                t.Occupied = false;
                t.CanBattle = false;
                t.GateOpened = true;
                ChangeSwitchModule(DefaultChannel, z.MapId, z.LSwitchId, SwcOpen);
                ChangeSwitchModule(DefaultChannel, z.MapId, z.RSwitchId, SwcOpen);
                foreach (var id in ZoneSpawnIds(t)) ReleaseWarSpawn(id);
                break;
        }
    }

    /// <summary>The fort part of C++ <c>CTMonster::OnBattleZoneEvent</c> (TMonster.cpp:2041): a gate opened by its keeper brings
    /// the boss out (the first time) and the news; the boss's death takes the fort.</summary>
    private void FortZoneEvent(Territory t, Monster mon, ushort spawnId, bool gateOpened, uint attackerId)
    {
        var z = t.Zone;
        if (gateOpened)
        {
            if (!t.GateOpened) AddWarSpawn(z.BossSpawnId, suspend: true);
            t.GateOpened = true;
            NotifyLocalInfo(SmBattleOpenGate, t.Id, 0, z.Name);
        }
        if (spawnId != z.BossSpawnId) return;
        uint guild = 0, max = 0;
        foreach (var (g, dmg) in mon.GuildDamage.OrderBy(p => p.Key))         // C++ std::map order, the first highest
            if (max < dmg) { max = dmg; guild = g; }
        t.Occupied = true;
        LocalOccupy(t, OccupyAccept, guild, attackerId);
        PvPGodMonKill(attackerId);
    }

    // ================================ the capture ================================

    /// <summary>C++ <c>SendDM_LOCALOCCUPY_REQ</c> → <c>TSaveLocalOccupy</c> → <c>OnDM_LOCALOCCUPY_ACK</c>. DB-free, the proc's
    /// choice is made here.</summary>
    private void LocalOccupy(Territory t, byte type, uint guildId, uint charId)
    {
        if (_gameDb is not { } db)
        {
            var (country, guild) = LocalOccupyOwner(t, type, guildId, charId);
            OnLocalOccupied(t, 0, type, country, guild);
            return;
        }
        _ = Task.Run(async () =>
        {
            (int ret, byte country, uint guild) result;
            try { result = await db.SaveLocalOccupyAsync(t.Id, type, guildId, charId); }
            catch (Exception ex) { _log.LogWarning(ex, "TSaveLocalOccupy failed for fort {Id}.", t.Id); result = (LoccupyInternal, 0, 0); }
            _dbResults.Enqueue(() => OnLocalOccupied(t, result.ret, type, result.country, result.guild));
        });
    }

    /// <summary>What <c>TSaveLocalOccupy</c> decides, for the DB-free map: held, the fort stays its owner's; taken, the guild's
    /// country (its chief's, here the killer's) — a Broa one gives no guild and its aid country (or the other one) instead.</summary>
    private (byte Country, uint Guild) LocalOccupyOwner(Territory t, byte type, uint guildId, uint charId)
    {
        if (type == OccupyDefend) return (t.Country, t.Guild);
        var killer = _state.FindByChar(charId)?.Char;
        byte country = killer?.Country ?? t.Country;
        if (country > TcontryC)
        {
            guildId = 0;
            country = killer is { AidCountry: < TcontryBroa } k ? k.AidCountry : t.Country == TcontryD ? TcontryC : t.Country == TcontryC ? TcontryD : country;
        }
        return (country, guildId);
    }

    /// <summary>C++ <c>OnDM_LOCALOCCUPY_ACK</c> (SSHandler.cpp:2873): the fort's monsters change sides; its players of the owner
    /// guild gain stat exp; the world is told.</summary>
    private void OnLocalOccupied(Territory t, int ret, byte type, byte country, uint guild)
    {
        if (ret != 0 || !t.Valid) return;
        foreach (var id in ZoneSpawnIds(t))
            foreach (var sp in _spawns.Where(p => p.Def.Spawn.Id == id && p.Channel == DefaultChannel))
                foreach (var slot in sp.Slots)
                {
                    if (slot.Live is not { } m) continue;
                    LeaveAggro(m, m.HostId, m.TargetId, m.TargetType, NowMs);
                    m.Country = country;
                    foreach (var p in _state.PlayersAround(m)) SendMonsterColor(p, m);
                }

        foreach (var s in _state.AllInGame().ToList())
        {
            if (!s.IsMain || s.Char is not { } ch || ch.LocalId != t.Id || ch.GuildId != guild) continue;
            byte kills = (byte)t.Kills.GetValueOrDefault(ch.CharId);
            IncreaseStatExp(ch, (ushort)(kills * 10));
            IncreaseStatExp(ch, LocalOccupyStatExp / 2);
        }

        var ack = new PacketWriter(Msg.MW_LOCALOCCUPY_ACK);
        ack.WriteByte(type); ack.WriteUInt16(t.Id); ack.WriteByte(country); ack.WriteUInt32(guild); ack.WriteByte(t.Country);
        _world.Send(ack);
        _log.LogInformation("Fort {Id} {How}: country {Country}, guild {Guild}.", t.Id, type == OccupyAccept ? "taken" : "held", country, guild);
    }

    /// <summary>C++ <c>CTPlayer::IncreaseStatExp</c> (TPlayer.cpp:7808): past the level's need (level × 1200) the stat level goes
    /// up with a point, and the exp starts over.</summary>
    private static void IncreaseStatExp(Character ch, ushort inc)
    {
        var p = ch.Persist;
        if (p.StatExp + inc > p.StatLevel * 1200u) { p.StatLevel++; p.StatPoint++; p.StatExp = 0; }
        else p.StatExp += inc;
    }

    /// <summary>C++ <c>OnMW_LOCALOCCUPY_REQ</c> (SSHandler.cpp:11399): the news of a capture, the new owner, and every player told.</summary>
    private void OnMW_LOCALOCCUPY_REQ(PacketReader r)
    {
        byte type = r.ReadByte();
        ushort id = r.ReadUInt16();
        byte country = r.ReadByte();
        uint guild = r.ReadUInt32();
        string guildName = r.ReadString();
        if (_territories.TryGetValue(id, out var t) && t.Type == LocalType.Occupation)
        {
            if (type == OccupyAccept) NotifyLocalInfo(SmBattleBossDie, country, 0, t.Zone.Name, guildName);
            ResetLocal(t, country, guild, guildName, type);
        }
        var w = new PacketWriter(Msg.CS_LOCALOCCUPY_ACK, capacity: 8);
        w.WriteByte(type); w.WriteUInt16(id); w.WriteByte(country); w.WriteUInt32(guild);
        var msg = w.ToArray();
        foreach (var s in _state.AllInGame()) s.Send(msg);
    }

    /// <summary>C++ <c>ResetLocal</c> (TMapSvr.cpp:7868): the new owner (a new guild loses the hero), the next war a day on (two
    /// when the castle's war comes next), today's line of the week, the kills forgotten; then the castle's scoreboard and the
    /// points: the participants', and the winning or holding guild's.</summary>
    private void ResetLocal(Territory t, byte country, uint guild, string guildName, byte type)
    {
        if (t.Guild != guild) { t.Hero = ""; t.HeroTime = 0; }
        uint prev = t.Guild;
        t.Country = country; t.Guild = guild; t.GuildName = guildName;
        t.NextDefend += 86400;
        t.LastOccType = type;
        int day = (int)LocalNow().DayOfWeek + 1;                              // CTime::GetDayOfWeek: 1 = Sunday
        t.OccupyGuild[day - 1] = guild;
        t.OccupyType[day - 1] = type;
        var castle = _territories.TryGetValue(t.Zone.Castle, out var c) && c.Type == LocalType.Castle ? c : null;
        if (castle is not null && castle.Day == day + 1) t.NextDefend += 86400;
        t.Kills.Clear();
        if (!t.Valid) return;
        t.Occupied = true;
        if (castle is not null) SendCastleWarInfo(castle);
        PvPEntry(t);
        if (prev != guild) PvPWin(t, prev); else PvPDefend(t);
    }

    // ================================ the points ================================

    private (uint Inc, uint Dec)? LocalPoint(Territory t, byte evt)
        => _templates.LocalPvPoints.TryGetValue((t.Id, PvpsLocal, evt), out var p) ? p : null;

    /// <summary>C++ <c>LocalRecord</c> / <c>SetEntryRecord</c> (TMapSvr.cpp:10586): a player's points, kills and deaths in a fort's
    /// war, by guild.</summary>
    private static void LocalRecord(Territory t, Character ch, byte evt, uint point, bool gain)
    {
        uint guild = GuildOf(ch);
        if (!t.Records.TryGetValue(guild, out var byChar)) t.Records[guild] = byChar = new();
        if (!byChar.TryGetValue(ch.CharId, out var rec)) byChar[ch.CharId] = rec = new EntryRecord(ch.CharId);
        if (gain) rec.Gain[evt] += point;
        if (evt is >= PvpeKillH and <= PvpeKillL)
        {
            if (gain) rec.Kills++; else rec.Deaths++;
        }
    }

    /// <summary>C++ <c>PvPEvent(PVPE_GODMONKILL)</c>: the killer of a boss (or each partner of its party nearby, a share) gets the
    /// god-monster points of the territory it stands in.</summary>
    private void PvPGodMonKill(uint attackerId)
    {
        if (_state.FindByChar(attackerId) is not { Char: { } ch } s || LocalOf(ch) is not { } local) return;
        if (LocalPoint(local, PvpeGodMonKill) is not { Inc: not 0 } row) return;
        var party = ch.GetPartyId() != 0
            ? _state.InView(s).Where(x => x.Char is { } c && c.GetPartyId() == ch.GetPartyId()).ToList()
            : new List<ClientSession>();
        if (party.Count == 0)
        {
            LocalRecord(local, ch, PvpeGodMonKill, row.Inc, gain: true);
            if (s.IsMain) GainPvPoint(s, ch, row.Inc, PvpeGodMonKill, PvpUseable);
            else SendMW_GAINPVPPOINT_ACK(TownerChar, attackerId, row.Inc, PvpeGodMonKill, PvpUseable, true);
            return;
        }
        uint share = row.Inc / (uint)party.Count;
        for (int i = party.Count - 1; i >= 0; i--)                                  // vParty.back() first
        {
            var x = party[i];
            LocalRecord(local, x.Char!, PvpeGodMonKill, row.Inc, gain: true);         // sic: the whole, not the share
            if (x.IsMain) GainPvPoint(x, x.Char!, share, PvpeGodMonKill, PvpUseable);
            else SendMW_GAINPVPPOINT_ACK(TownerChar, x.CharId, share, PvpeGodMonKill, PvpUseable, true);
        }
    }

    /// <summary>C++ <c>PvPEvent(PVPE_ENTRY)</c>: everyone written down in the war gets the taking-part points; the records go to the
    /// world (with the guild's win / hold points), and are forgotten.</summary>
    private void PvPEntry(Territory t)
    {
        if (LocalPoint(t, PvpeEntry) is not { Inc: not 0 } row || t.Records.Count == 0) return;
        uint guildPoint = LocalPoint(t, t.LastOccType == OccupyDefend ? PvpeDefend : PvpeWin)?.Inc ?? 0;
        var w = new PacketWriter(Msg.MW_LOCALRECORD_ACK, capacity: 256);
        w.WriteUInt32(t.Guild); w.WriteUInt32(guildPoint); w.WriteUInt16((ushort)t.Records.Count);
        foreach (var (guild, byChar) in t.Records)
        {
            w.WriteUInt32(guild); w.WriteUInt16((ushort)byChar.Count);
            foreach (var rec in byChar.Values)
            {
                rec.Gain[PvpeEntry] = row.Inc;
                w.WriteUInt32(rec.CharId); w.WriteUInt16(rec.Kills); w.WriteUInt16(rec.Deaths);
                for (int i = 0; i < PvpeCount; i++) w.WriteUInt32(rec.Gain[i]);
                if (_state.FindByChar(rec.CharId) is { IsMain: true, Char: { } ch } s) GainPvPoint(s, ch, row.Inc, PvpeEntry, PvpUseable);
                else SendMW_GAINPVPPOINT_ACK(TownerChar, rec.CharId, row.Inc, PvpeEntry, PvpUseable, true);
            }
        }
        _world.Send(w);
        t.Records.Clear();
    }

    /// <summary>C++ <c>PvPEvent(PVPE_WIN)</c>: the guild that lost the fort loses points, the one that took it gains.</summary>
    private void PvPWin(Territory t, uint prevGuild)
    {
        if (LocalPoint(t, PvpeWin) is not { } row) return;
        if (row.Dec != 0 && prevGuild != 0) SendMW_GAINPVPPOINT_ACK(TownerGuild, prevGuild, row.Dec, PvpeWin, PvpTotal, false);
        if (row.Inc != 0 && t.Guild != 0) SendMW_GAINPVPPOINT_ACK(TownerGuild, t.Guild, row.Inc, PvpeWin, PvpTotal | PvpUseable, true);
    }

    /// <summary>C++ <c>PvPEvent(PVPE_DEFEND)</c>: the guild that held the fort gains.</summary>
    private void PvPDefend(Territory t)
    {
        if (LocalPoint(t, PvpeDefend) is { Inc: not 0 } row && t.Guild != 0)
            SendMW_GAINPVPPOINT_ACK(TownerGuild, t.Guild, row.Inc, PvpeDefend, PvpTotal | PvpUseable, true);
    }

    /// <summary>C++ <c>SendMW_GAINPVPPOINT_ACK</c> (SSSender.cpp:4754): points for a guild, or for a character played elsewhere.</summary>
    private void SendMW_GAINPVPPOINT_ACK(byte ownerType, uint ownerId, uint point, byte evt, byte type, bool gain)
    {
        var w = new PacketWriter(Msg.MW_GAINPVPPOINT_ACK);
        w.WriteByte(ownerType); w.WriteUInt32(ownerId); w.WriteUInt32(point); w.WriteByte(evt); w.WriteByte(type);
        w.WriteByte((byte)(gain ? 1 : 0)); w.WriteString(""); w.WriteByte(0); w.WriteByte(0);
        _world.Send(w);
    }

    /// <summary>C++ <c>OnMW_GAINPVPPOINT_REQ</c> (SSHandler.cpp:15598): the world forwards a character's points to its main. (The
    /// kill record that may come with them is not kept here.)</summary>
    private void OnMW_GAINPVPPOINT_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), point = r.ReadUInt32();
        byte evt = r.ReadByte(), type = r.ReadByte(), gain = r.ReadByte();
        if (_state.FindByChar(charId) is not { Char: { } ch } s) return;
        if (gain != 0) GainPvPoint(s, ch, point, evt, type); else UsePvPoint(s, ch, point, evt, type);
    }

    /// <summary>C++ <c>LocalReward</c> (TMapSvr.cpp:9879), when the forts' war ends: the players around each fort's boss spot are
    /// gathered by line (north / south); then each held fort mails its reward item to the players of its guild on its line — the
    /// guild chief the chief's item.</summary>
    private void LocalReward()
    {
        var byLine = new Dictionary<byte, Dictionary<uint, ClientSession>>();
        foreach (var t in _occupations)
        {
            if (SpawnById(t.Zone.BossSpawnId)?.Spawn is not { } boss) continue;
            if (!byLine.TryGetValue(t.Zone.Line, out var line)) byLine[t.Zone.Line] = line = new();
            int ux = (int)(boss.PosX / UnitSize), uz = (int)(boss.PosZ / UnitSize);
            foreach (var s in PlayersOnMap(t.Zone.MapId))
                if (s.Char is { } ch && (int)(ch.PosX / UnitSize) == ux && (int)(ch.PosZ / UnitSize) == uz) line.TryAdd(ch.CharId, s);
        }
        foreach (var t in _occupations)
        {
            if (t.Guild == 0 || t.Zone.NormalItem == 0 || _templates.Item(t.Zone.NormalItem) is not { } normal) continue;
            var chief = t.Zone.ChiefItem != 0 ? _templates.Item(t.Zone.ChiefItem) : null;
            if (!byLine.TryGetValue(t.Zone.Line, out var line)) continue;
            foreach (var s in line.Values)
            {
                if (!s.IsMain || s.Char is not { } ch || GuildOf(ch) != t.Guild) continue;
                bool isChief = ch.GuildDuty == GuildDutyChief && chief is not null;
                _ = MailOperatorItem(ch.CharId, ch.Name, "Local reward", "", isChief ? chief! : normal, 1);
            }
        }
    }

    // ================================ the owner's say ================================

    /// <summary>The country an NPC or portal serves: a fort's own (C++ <c>m_pLocal ? m_pLocal-&gt;m_bCountry : m_bCountry</c>).</summary>
    private byte LocalCountryOr(ushort localId, byte own)
        => localId != 0 && _territories.TryGetValue(localId, out var t) && t.Type == LocalType.Occupation && t.Valid ? t.Country : own;

    /// <summary>C++ <c>CTNpc::CanTalk</c> with the NPC's fort.</summary>
    private bool CanTalk(Npc npc, Character ch) => npc.CanTalk(ch.Country, ch.AidCountry, 0, LocalCountryOr(npc.LocalId, npc.Country));

    /// <summary>C++ <c>GetDiscountRate</c> (TMapSvr.cpp:9694): an NPC's discount for its fort's guild, or for a fort's (or a
    /// castle's) hero while the hero's term lasts.</summary>
    private byte DiscountRate(Character ch, Npc? npc)
    {
        if (npc is null) return 0;
        return npc.DiscountCondition switch
        {
            DccGuild => NpcFortGuild(npc) is var g && g != 0 && g == ch.GuildId ? npc.DiscountRate : (byte)0,
            DccLocalHero => _occupations.Any(t => t.Hero == ch.Name && t.HeroTime < t.NextDefend) ? npc.DiscountRate : (byte)0,
            DccCastleHero => _castles.Any(t => t.Hero == ch.Name && t.HeroTime < t.NextDefend) ? npc.DiscountRate : (byte)0,
            _ => 0,
        };
    }

    /// <summary>The <c>m_bAddProb</c> part of C++ <c>CalcProb</c> (TMapSvr.cpp:9744): an NPC's bonus for its fort's guild, or for
    /// any fort's (or castle's) hero.</summary>
    private int NpcAddProb(Character ch, Npc npc) => npc.DiscountCondition switch
    {
        DccGuild => NpcFortGuild(npc) is var g && g != 0 && g == ch.GuildId ? npc.AddProb : 0,
        DccLocalHero => _occupations.Any(t => t.Hero == ch.Name) ? npc.AddProb : 0,
        DccCastleHero => _castles.Any(t => t.Hero == ch.Name) ? npc.AddProb : 0,
        _ => 0,
    };

    private uint NpcFortGuild(Npc npc)
        => npc.LocalId != 0 && _territories.TryGetValue(npc.LocalId, out var t) && t.Type == LocalType.Occupation && t.Valid ? t.Guild : 0;
}

/// <summary>One player's line in a fort's war (C++ <c>TENTRYRECORD</c>).</summary>
public sealed class EntryRecord(uint charId)
{
    public uint CharId { get; } = charId;
    public ushort Kills { get; set; }
    public ushort Deaths { get; set; }
    public uint[] Gain { get; } = new uint[8];
}
