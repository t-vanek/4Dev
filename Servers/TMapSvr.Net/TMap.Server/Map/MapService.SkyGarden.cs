using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Territory battles, batch C: the sky garden (Avalon Garden of Heaven) — the owning country defends it against the other one.
/// <list type="bullet">
/// <item><b>Coming in</b> (C++ <c>InitMap</c>): on its map a player is a defender when its country (or aid country) owns the garden,
/// else an attacker; it gets the garden's state (<c>CS_ENTERSKYGARDEN_ACK</c>: camp, the countries, who holds the left, centre and
/// right points). Leaving the map ends it (<c>CS_LEAVESKYGARDEN_ACK</c>).</item>
/// <item><b>The war</b> (<c>MW_SKYGARDENENABLE_REQ</c>): in battle the gates close; the three gatekeepers, the three point guardians
/// (fighting for the owner) and each side's camp spawns come out, suspended; every point starts with the defenders. A killed
/// gatekeeper opens its gate; a killed guardian turns its point over (<c>CS_SKYGARDEN_OCCUPY_*_ACK</c> to everyone on the map);
/// killing a country's boss wins it for the other country at once. At the end of the war the side holding two points wins.
/// At normal everyone is sent out of the map — out of a peace the gates open again and the garden empties.</item>
/// <item><b>The win</b> (<c>TSaveSkyGardenOccupy</c> → <c>MW_SKYGARDENOCCUPY_ACK</c> → every map's <c>MW_SKYGARDENOCCUPY_REQ</c>): the
/// new owner, its spawns gone, the gates open, the news (<c>SM_SKYGARDEN_END</c>), and on its map the winners get the reward item
/// and 360 useable PvP points, the others 360 too.</item>
/// <item><b>Portals</b>: the sky-garden portal conditions (attacker / defender side, the point one's camp holds).</item>
/// </list>
/// <para>Guardian/boss point dispatch uses the shared territory path; absent sky-garden rows in TPVPOINTCHART remain a data
/// evidence gap. Capture acknowledgements wait for SQL success; settlement/reward replay after a crash remains incomplete.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte SmSkyGardenEnd = 34, PvpeWin = 6, PvpeDefend = 7, TcontryBroa = 2;

    // ================================ coming in ================================

    /// <summary>C++ <c>InitMap</c> (TMapSvr.cpp:8094): on the garden's map, one's camp and the garden's state.</summary>
    private void SkyGardenEnterMap(ClientSession s, Character ch)
    {
        if (_skyGardens.FirstOrDefault(g => g.Valid && g.Zone.MapId == ch.MapId) is not { } g) return;
        ch.Camp = g.Country == ch.Country || g.Country == ch.AidCountry ? CampDefend : CampAttack;
        ch.Castle = g.Id;
        var w = new PacketWriter(Msg.CS_ENTERSKYGARDEN_ACK, capacity: 12);
        w.WriteUInt16(g.Id); w.WriteByte(ch.Camp); w.WriteByte(g.Country);
        w.WriteByte(g.LeftOwner); w.WriteByte(g.MiddleOwner); w.WriteByte(g.RightOwner);
        w.WriteByte(g.Country == TcontryC ? TcontryD : TcontryC);
        s.Send(w);
    }

    /// <summary>C++ <c>ExitMAP</c>: leaving the garden's map.</summary>
    private void SkyGardenExitMap(ClientSession s, Character ch)
    {
        if (IsInSkyGarden(ch)) s.Send(new PacketWriter(Msg.CS_LEAVESKYGARDEN_ACK, capacity: 0));
    }

    // ================================ the war ================================

    /// <summary>The war-phase part of <c>OnMW_SKYGARDENENABLE_REQ</c> for a garden that changed status (MapService.Territory.cs).</summary>
    private void SkyGardenPhase(Territory g, byte status, bool reopen)
    {
        var z = g.Zone;
        switch (status)
        {
            case BsNormal:
                if (reopen)
                {
                    SetZoneSwitches(z, SwcOpen);
                    foreach (var id in ZoneSpawnIds(g)) DelMonSpawn(id, DefaultChannel);   // C++ ClearMonster
                }
                else SetZoneSwitches(z, SwcClose);
                SendEveryoneOut(z.MapId);
                break;
            case BsBattle:
                g.Occupied = false;     // C++ bug fixed: m_bOccupied was never cleared, so only the first war since start had a result
                SetZoneSwitches(z, SwcClose);
                foreach (var id in new[] { z.CGateKeeperSpawnId, z.LGateKeeperSpawnId, z.RGateKeeperSpawnId }) AddWarSpawn(id, suspend: true);
                foreach (var id in new[] { z.MiddleSpawnId, z.LeftSpawnId, z.RightSpawnId }) AddWarSpawn(id, suspend: true, country: g.Country);
                foreach (var def in _templates.MonsterSpawns.Where(d => d.Spawn.LocalId == g.Id))
                {
                    var sp = def.Spawn;
                    if ((g.Country == sp.Country && sp.Event == CampDefend) || (g.Country != sp.Country && sp.Event == CampAttack))
                        AddWarSpawn(sp.Id, suspend: true);
                }
                break;
            case BsPeace:
                if (!g.Occupied)
                {
                    int held = new[] { g.LeftOwner, g.MiddleOwner, g.RightOwner }.Count(o => o == CampDefend);
                    byte country = held >= 2 ? (g.Country == TcontryD ? TcontryD : TcontryC) : (g.Country != TcontryD ? TcontryD : TcontryC);
                    SkyGardenOccupy(g, OccupyAccept, country);
                }
                break;
        }
    }

    /// <summary>The sky-garden part of C++ <c>CTMonster::OnBattleZoneEvent</c> (TMonster.cpp:2139): a guardian turns its point over;
    /// a country's boss falling wins the garden for the other country.</summary>
    private void SkyGardenZoneEvent(Territory g, Monster mon, ushort spawnId, uint attackerId)
    {
        var z = g.Zone;
        void Point(ushort guardian, Func<byte> flip, ushort ack)
        {
            if (spawnId != guardian) return;
            PvPGodMonKill(attackerId);                                          // MapService.Fort.cs
            mon.Country = mon.Country == TcontryD ? TcontryC : TcontryD;
            var w = new PacketWriter(ack, capacity: 4);
            w.WriteByte(flip());
            var msg = w.ToArray();
            foreach (var p in PlayersOnMap(z.MapId)) p.Send(msg);
        }
        Point(z.MiddleSpawnId, () => g.MiddleOwner = Flip(g.MiddleOwner), Msg.CS_SKYGARDEN_OCCUPY_CENTER_ACK);
        Point(z.LeftSpawnId, () => g.LeftOwner = Flip(g.LeftOwner), Msg.CS_SKYGARDEN_OCCUPY_LEFT_ACK);
        Point(z.RightSpawnId, () => g.RightOwner = Flip(g.RightOwner), Msg.CS_SKYGARDEN_OCCUPY_RIGHT_ACK);

        byte? winner = spawnId == z.DerionBossAttack || spawnId == z.DerionBossDefend ? TcontryD
            : spawnId == z.ValorianBossAttack || spawnId == z.ValorianBossDefend ? TcontryC : null;
        if (winner is { } country && spawnId != 0)
        {
            g.Occupied = true;
            g.Status = BsPeace;
            PvPGodMonKill(attackerId);
            SkyGardenOccupy(g, OccupyAccept, country);
        }
    }

    private static byte Flip(byte camp) => camp == CampDefend ? CampAttack : CampDefend;

    /// <summary>C++ <c>SendDM_SKYGARDENOCCUPY_REQ</c> → <c>TSaveSkyGardenOccupy</c> → <c>MW_SKYGARDENOCCUPY_ACK</c>.</summary>
    private void SkyGardenOccupy(Territory g, byte type, byte country)
    {
        if (TerritoryOccupationStore is { } db)
        {
            QueueTerritoryWrite(g, async () =>
            {
                int result = await SaveTerritoryResultAsync(() => db.SaveSkyGardenOccupyAsync(country, g.Id, type));
                if (result == 0) SendSkyGardenOccupied(g, type, country);
            });
            return;
        }
        SendSkyGardenOccupied(g, type, country);
    }

    private void SendSkyGardenOccupied(Territory g, byte type, byte country)
    {
        var w = new PacketWriter(Msg.MW_SKYGARDENOCCUPY_ACK);
        w.WriteByte(type); w.WriteUInt16(g.Id); w.WriteByte(country);
        _world.Send(w);
        _log.LogInformation("Sky garden {Id} won by country {Country}.", g.Id, country);
    }

    /// <summary>C++ <c>OnMW_SKYGARDENOCCUPY_REQ</c> (SSHandler.cpp:11549): the new owner and the garden reset, its spawns gone, the
    /// gates open, the news, and the reward.</summary>
    private void OnMW_SKYGARDENOCCUPY_REQ(PacketReader r)
    {
        byte type = r.ReadByte();
        ushort id = r.ReadUInt16();
        byte country = r.ReadByte();
        if (!_territories.TryGetValue(id, out var g) || g.Type != LocalType.SkyGarden) return;
        g.Guild = 0; g.GuildName = ""; g.Country = country;
        g.DPoint = g.CPoint = 0; g.DefGuildId = g.AtkGuildId = 0; g.DefCount = g.AtkCount = 0; g.DefCountry = TcontryN;
        g.AtkName = g.DefName = ""; g.Points.Clear(); g.Top3[0].Clear(); g.Top3[1].Clear();
        g.NextDefend += 86400;
        g.Status = BsPeace; g.CanBattle = false; g.Occupied = true;
        g.LeftOwner = g.MiddleOwner = g.RightOwner = CampDefend;
        foreach (var sid in ZoneSpawnIds(g)) DelMonSpawn(sid, DefaultChannel);
        SetZoneSwitches(g.Zone, SwcOpen);
        NotifyLocalInfo(SmSkyGardenEnd, country, g.Zone.MapId, g.Zone.Name);
        SkyGardenReward(g, type, country);
    }

    /// <summary>C++ <c>SkygardenReward</c> (TMapSvr.cpp:10100): on its map, the winners get the reward item and 360 useable PvP
    /// points (<c>PVPE_WIN</c>), the others 360 (<c>PVPE_DEFEND</c>).</summary>
    private void SkyGardenReward(Territory g, byte type, byte country)
    {
        if (type != OccupyAccept) return;
        var reward = g.Zone.NormalItem != 0 ? _templates.Item(g.Zone.NormalItem) : null;
        foreach (var s in PlayersOnMap(g.Zone.MapId).ToList())
        {
            if (!s.IsMain || s.Char is not { } ch) continue;
            if (WarCountryOf(ch) == country)
            {
                if (reward is not null) _ = MailOperatorItem(ch.CharId, ch.Name, "Sky garden", "", reward, 1);
                GainPvPoint(s, ch, 360, PvpeWin, PvpUseable);
            }
            else GainPvPoint(s, ch, 360, PvpeDefend, PvpUseable);
            ScheduleQuestCheckpoint(ch);
        }
    }

    // ================================ portals ================================

    /// <summary>The sky-garden part of C++ <c>CTPlayer::CheckPortalCondition</c> (TPlayer.cpp:3331): the portal's garden owned by
    /// one's side (a Broa player counts as its aid country) or not, and / or the point the portal leads to held by one's camp.</summary>
    private bool CheckSkyGardenPortal(Character ch, PortalRow portal, byte condition)
    {
        if (!_territories.TryGetValue(portal.LocalId, out var g) || g.Type != LocalType.SkyGarden) return false;
        byte country = ch.Country == TcontryBroa ? ch.AidCountry : ch.Country;
        bool attacker = g.Country != country, defender = g.Country == country;
        return condition switch
        {
            PctSkyAttackPos => attacker,
            PctSkyDefendPos => defender,
            PctSkyLeft => g.LeftOwner == ch.Camp,
            PctSkyMiddle => g.MiddleOwner == ch.Camp,
            PctSkyRight => g.RightOwner == ch.Camp,
            18 => attacker && g.LeftOwner == ch.Camp,
            19 => defender && g.LeftOwner == ch.Camp,
            20 => attacker && g.MiddleOwner == ch.Camp,
            21 => defender && g.MiddleOwner == ch.Camp,
            22 => attacker && g.RightOwner == ch.Camp,
            23 => defender && g.RightOwner == ch.Camp,
            _ => true,
        };
    }

    private const byte PctSkyAttackPos = 13, PctSkyDefendPos = 14, PctSkyLeft = 15, PctSkyMiddle = 16, PctSkyRight = 17;   // PORTALCONDITION_TYPE (18-23: ATTACK/DEFENDPOS_LEFT/CENTER/RIGHT)

    // ================================ helpers ================================

    private IEnumerable<ClientSession> PlayersOnMap(ushort mapId)
        => _state.AllInGame().Where(s => s.Channel == DefaultChannel && s.Char?.MapId == mapId);

    /// <summary>C++ <c>ChangeSwitch</c> on a zone's left, right and centre gate switches.</summary>
    private void SetZoneSwitches(BattleZone z, byte swc)
    {
        foreach (var id in new[] { z.LSwitchId, z.RSwitchId, z.CSwitchId })
            if (id != 0) ChangeSwitchModule(DefaultChannel, z.MapId, id, swc);
    }

    /// <summary>C++ <c>LeaveCastleMap</c> (TMapSvr.cpp): everyone on the map back to its last spawn point (else its own).</summary>
    private void SendEveryoneOut(ushort mapId)
    {
        foreach (var s in PlayersOnMap(mapId).ToList())
            if (s.IsMain && s.Char is { } ch)
                Teleport(s, ch, ch.Persist.LastSpawnId != 0 ? ch.Persist.LastSpawnId : ch.Persist.SpawnId);
    }
}
