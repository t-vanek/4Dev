using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Teleport — NPC portals (<c>OnCS_TELEPORT_REQ</c>, CSHandler.cpp:5904) and the move itself
/// (<c>CTMapSvrModule::Teleport</c>, TMapSvr.cpp:7165-7270), with the world round trip that relocates a player.
///
/// <para>A hop inside the same channel and map and under one cell each way is local: the client is told
/// <c>TPR_SUCCESS</c> and moves itself. Anything further goes through the world, and the client keeps its
/// socket — on a single map server the chain is:</para>
/// <list type="number">
/// <item><c>MW_BEGINTELEPORT_ACK</c> to the world, <c>CS_BEGINTELEPORT_ACK</c> to the client (loading screen);</item>
/// <item>world → <c>MW_STARTTELEPORT_REQ</c>: the player leaves the map (<c>ExitMAP</c>), takes the destination,
/// and the owning server is looked up (<c>DM_TELEPORT</c> → <c>MW_TELEPORT_ACK</c>);</item>
/// <item>world → <c>MW_TELEPORT_REQ</c> (→ <c>CS_TELEPORT_ACK</c>: the client loads the map) and
/// <c>MW_CONLIST_REQ</c> (→ <c>MW_CONLIST_ACK</c>);</item>
/// <item>the ordinary enter tail: <c>MW_CHECKMAIN</c> → <c>MW_CONRESULT</c> → <c>CS_CONNECT_ACK</c> →
/// <c>CS_CONREADY_REQ</c>, which places the player again because it is no longer in game.</item>
/// </list>
///
/// <para>Faithful, flagged: a request with <c>wNpcID = 0</c> skips the NPC, price and item checks and goes
/// straight to the portal's spawn point — the C++ behaves the same.</para>
///
/// <para>Meeting-room reservations and release are in MapService.MeetingRooms.cs.
/// <b>Not ported:</b> <c>TT_LEAVEMAP</c> quest triggers and the second-server
/// variant of the chain (<c>ROUTELIST</c>/<c>ADDCONNECT</c>/<c>RELEASEMAIN</c>).</para>
/// </summary>
public sealed partial class MapService
{
    private const byte TprSuccess = 0, TprNotTeleportNpc = 1, TprNoPortal = 2, TprNoDestination = 3,
        TprNeedMoney = 4, TprNoItem = 5, TprInvalid = 6, TprUsed = 7;                      // TTELEPORT_RESULT
    private const byte PctHaveItem = 2, PctTournamentPlayer = 9, PctTournamentLounge = 10, PctTournamentGuest = 11, PctMeeting = 12;   // PORTALCONDITION_TYPE
    private const byte InvalidServerId = 0xFF;

    // Buffs a teleport always clears (TMapSvr.cpp:7206).
    private static readonly ushort[] TeleportClearedSkills = { 526, 527, 331 };

    private void OnCS_TELEPORT_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        if (s.State != EnterState.InGame || !s.IsMain || ch.Hp == 0 || s.Store.IsOpen || s.Deal.InProgress)
        {
            SendCS_TELEPORT_ACK(s, TprInvalid, ch.MapId, 0, 0, 0);
            return;
        }

        ushort npcId = r.ReadUInt16();
        ushort portalId = r.ReadUInt16();

        Npc? npc = null;
        PortalRow? npcPortal = null;
        uint price = 0;
        bool bet = false;                                                                        // C++ bTournamentBat
        bool meeting = false;
        var consume = new List<ushort>();

        if (npcId != 0)
        {
            npc = _state.FindNpc(npcId);
            if (npc is null) { SendCS_TELEPORT_ACK(s, TprNotTeleportNpc, ch.MapId, 0, 0, 0); return; }
            if (!CanTalk(npc, ch)) return;                          // disguise unported ⇒ 0

            if (npc.PortalId == 0 || !_templates.Portals.TryGetValue(npc.PortalId, out npcPortal))
            {
                SendCS_TELEPORT_ACK(s, TprNoPortal, ch.MapId, 0, 0, 0);
                return;
            }
            if (!npcPortal.Destinations.TryGetValue(portalId, out var dest))
            {
                SendCS_TELEPORT_ACK(s, TprNoDestination, ch.MapId, 0, 0, 0);
                return;
            }

            price = dest.Price;
            price -= price * DiscountRate(ch, npc) / 100;                                    // GetDiscountRate
            if (!ch.UseMoney(price, commit: false)) { SendCS_TELEPORT_ACK(s, TprNeedMoney, ch.MapId, 0, 0, 0); return; }

            if (npc.RequiredItemId != 0 && !ch.Invens.Any(i => i.Items.Any(it => it.TemplateId == npc.RequiredItemId)))
            {
                SendCS_TELEPORT_ACK(s, TprNoItem, ch.MapId, 0, 0, 0);
                return;
            }

            foreach (var cond in dest.Conditions)
            {
                if (cond.Type == PctMeeting)
                {
                    if (cond.Id != 0 || MeetingDestinationBusy(s.Channel, npcPortal.PortalId, portalId, s.CharId))
                    { SendCS_TELEPORT_ACK(s, TprUsed, ch.MapId, 0, 0, 0); return; }
                    meeting = true;
                }
                if (cond.Type == PctTournamentLounge) bet = true;
                if (cond.Type == PctHaveItem)
                {
                    ushort need = (ushort)cond.Id;
                    if (!ch.Invens.Any(i => i.Items.Any(it => it.TemplateId == need)))
                    {
                        SendCS_TELEPORT_ACK(s, TprNoItem, ch.MapId, 0, 0, 0);
                        return;
                    }
                    consume.Add(need);
                }
            }
        }

        ushort spawnId = portalId != 0 && _templates.Portals.TryGetValue(portalId, out var target) ? target.SpawnId : (ushort)0;
        ushort fromMap = ch.MapId;

        // An unpriced client request must not bypass the room reservation through wNpcID=0.
        if (spawnId != 0 && _templates.SpawnPositions.TryGetValue(spawnId, out var destination)
            && IsSmallMeetingRoom(destination.MapId) && (!meeting || OwnedMeetingRoom(s) is not null))
        { SendCS_TELEPORT_ACK(s, TprUsed, ch.MapId, 0, 0, 0); return; }

        if (spawnId != 0 && Teleport(s, ch, spawnId))
        {
            if (price != 0)
            {
                ch.UseMoney(price, commit: true);
                SendCS_MONEY_ACK(s, ch);
                if (bet)                                                                         // the lounge gate's price is a bet ticket
                {
                    var w = new PacketWriter(Msg.MW_TOURNAMENTENTERGATE_ACK);
                    w.WriteUInt32(ch.CharId); w.WriteUInt32(s.Key); w.WriteUInt32(price); w.WriteByte(1);
                    _world.Send(w);
                }
            }
            // Remember the portal a player left map 0 through (used by the return scroll).
            if (npcPortal is not null && fromMap == 0)
            {
                ch.Persist.LastSpawnId = npcPortal.SpawnId;
                ch.Persist.LastDestination = (uint)(npcPortal.PortalId | (portalId << 16));   // MAKELONG(portal, dest)
            }
            foreach (var itemId in consume) UseItemByTemplate(s, ch, itemId, 1);
            if (meeting && npcPortal is not null && _templates.SpawnPositions.TryGetValue(spawnId, out var roomSpawn))
                ReserveMeetingRoom(s, npcPortal.PortalId, portalId, roomSpawn.MapId);
        }
        else
        {
            SendCS_TELEPORT_ACK(s, TprNoDestination, ch.MapId, 0, 0, 0);
        }
    }

    /// <summary>C++ <c>Teleport(pPlayer, wSpawnID)</c> — to a named spawn point, on the player's own channel.</summary>
    private bool Teleport(ClientSession s, Character ch, ushort spawnId)
        => _templates.SpawnPositions.TryGetValue(spawnId, out var pos)
           && Teleport(s, ch, s.Channel, pos.MapId, pos.PosX, pos.PosY, pos.PosZ);

    /// <summary>C++ <c>Teleport(pPlayer, bChannel, wMapID, x, y, z)</c> (TMapSvr.cpp:7189).</summary>
    private bool Teleport(ClientSession s, Character ch, byte channel, ushort mapId, float x, float y, float z)
    {
        _log.LogInformation("Teleport char {Char}: HP {Hp}/{MaxHp}, from {FromMap} ({FromX},{FromY},{FromZ}) to {ToMap} ({ToX},{ToY},{ToZ}).",
            ch.CharId, ch.Hp, MaxHpFor(ch), ch.MapId, ch.PosX, ch.PosY, ch.PosZ, mapId, x, y, z);
        PetRiding(s, ch, 0);
        for (int i = ch.MaintainSkills.Count - 1; i >= 0; i--)
            if (Array.IndexOf(TeleportClearedSkills, ch.MaintainSkills[i].SkillId) >= 0) EraseMaintainPlayer(s, ch, i);

        bool shortHop = channel == s.Channel && mapId == ch.MapId
                        && Math.Abs((int)x - (int)ch.PosX) < MapGrid.CellSize
                        && Math.Abs((int)z - (int)ch.PosZ) < MapGrid.CellSize;
        if (shortHop)
        {
            SendCS_TELEPORT_ACK(s, TprSuccess, mapId, x, y, z);
            return true;
        }

        LeaveDuel(ch, loses: true);                                                     // teleporting away forfeits
        var w = new PacketWriter(Msg.MW_BEGINTELEPORT_ACK);
        w.WriteUInt32(ch.CharId); w.WriteUInt32(s.Key); w.WriteByte(0 /* bSameChannel */); w.WriteByte(channel);
        w.WriteUInt16(mapId); w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        _world.Send(w);

        var begin = new PacketWriter(Msg.CS_BEGINTELEPORT_ACK);
        begin.WriteByte(channel); begin.WriteUInt16(mapId);
        s.Send(begin);
        return true;
    }

    /// <summary>C++ <c>OnMW_STARTTELEPORT_REQ</c> (SSHandler.cpp:13025) + <c>OnDM_TELEPORT_REQ/ACK</c>: leave the
    /// map, take the destination, then tell the world which server owns it.</summary>
    private void OnMW_STARTTELEPORT_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte channel = r.ReadByte();
        ushort mapId = r.ReadUInt16();
        float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat();
        if (FindPlayer(charId, key) is not { Char: { } ch } s) return;

        ExitMap(s, ch);
        s.Channel = channel;
        ch.MapId = mapId;
        ch.PosX = x; ch.PosY = y; ch.PosZ = z;
        _log.LogInformation("Teleport char {Char} left map: HP {Hp}/{MaxHp}, destination {Map}.", charId, ch.Hp, MaxHpFor(ch), mapId);

        if (!s.IsMain) return;
        // TGetServerID: the server owning that map unit. This deployment runs one map server, which owns them all.
        byte serverId = OwnsMap(mapId) ? _opt.ServerId : InvalidServerId;
        if (serverId == InvalidServerId)
        {
            SendCS_TELEPORT_ACK(s, TprNoDestination, ch.MapId, 0, 0, 0);
            return;
        }
        var w = new PacketWriter(Msg.MW_TELEPORT_ACK);
        w.WriteUInt32(charId); w.WriteUInt32(key); w.WriteByte(serverId);
        _world.Send(w);
    }

    /// <summary>C++ <c>OnMW_TELEPORT_REQ</c> (SSHandler.cpp:1383): the world's verdict, passed to the client —
    /// on success this is what makes it load the destination.</summary>
    private void OnMW_TELEPORT_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        r.ReadByte();                                                   // bChannel
        ushort mapId = r.ReadUInt16();
        float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat();
        byte result = r.ReadByte();
        if (FindPlayer(charId, key) is not { } s) return;
        if (s.Char is { } ch)
            _log.LogInformation("Teleport char {Char} verdict {Result}: HP {Hp}/{MaxHp}, destination {Map}.", charId, result, ch.Hp, MaxHpFor(ch), mapId);
        SendCS_TELEPORT_ACK(s, result, mapId, x, y, z);
    }

    /// <summary>C++ <c>OnMW_CONLIST_REQ</c> (SSHandler.cpp:6210): the servers the destination cell needs. One
    /// server here — itself (the world adds the sender anyway).</summary>
    private void OnMW_CONLIST_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        r.ReadByte(); ushort mapId = r.ReadUInt16();                    // bChannel, wMapID; position follows

        var w = new PacketWriter(Msg.MW_CONLIST_ACK);
        w.WriteUInt32(charId); w.WriteUInt32(key);
        if (OwnsMap(mapId)) { w.WriteByte(1); w.WriteByte(_opt.ServerId); }
        else w.WriteByte(0);
        _world.Send(w);
    }

    /// <summary>Whether this server hosts <paramref name="mapId"/>. It hosts every map it has data for; with no
    /// charts loaded (DB-free) it hosts them all.</summary>
    private bool OwnsMap(ushort mapId)
        => _templates.SpawnPositions.Count == 0 || _templates.SpawnPositions.Values.Any(p => p.MapId == mapId);

    /// <summary>C++ <c>ExitMAP(pPlayer, TRUE, wGoMapID)</c> (TMapSvr.cpp:7333), the parts that exist here: any
    /// trade is ended, the player leaves its cells (<c>CTCell::LeavePlayer</c> — others see it leave the map, it
    /// forgets them and their monsters, and every monster that saw it drops it), and it is no longer in game
    /// until the new <c>CS_CONREADY_REQ</c>.</summary>
    private void ExitMap(ClientSession s, Character ch)
    {
        if (s.Deal.Status != (byte)DealStatus.Ready)
        {
            SendCS_DEALITEMEND_ACK(s, DealResult.NoTarget, ch.Name);
            if (FindByName(s.Deal.TargetName) is { } partner && partner.Deal.TargetName == ch.Name)
            {
                SendCS_DEALITEMEND_ACK(partner, DealResult.NoTarget, ch.Name);   // the world's DEALITEMERROR relay
                partner.Deal.Clear();
            }
            s.Deal.Clear();
        }

        if (s.State != EnterState.InGame) return;

        ReleaseMeetingRoom(s);

        EraseZoneEffects(s, ch);               // C++ ExitMAP → EraseMissionSkill (MapService.Mission.cs)
        ArenaLeave(s, ch); ArenaDequeue(ch.CharId);   // and ArenaLeave + the queue (MapService.Arena.cs)
        TournamentExitMap(s, ch);              // a fighter leaving its match, the real character back (MapService.TournamentMatch.cs)
        SkyGardenExitMap(s, ch);               // and CS_LEAVESKYGARDEN_ACK (MapService.SkyGarden.cs)
        CastleExitMap(s, ch);                  // a carried god ball falls, CS_LEAVECASTLE_ACK (MapService.Castle.cs)
        RecallsExitMap(s, ch);
        CompanionExitMap(ch);
        var watching = _state.MonstersInView(s).ToList();
        foreach (var other in _state.Neighbors(s).ToList())
        {
            other.Send(BuildCS_LEAVE_ACK(s.CharId, exitMap: true));
            s.Send(BuildCS_LEAVE_ACK(other.CharId, exitMap: false));
        }
        foreach (var m in watching)
        {
            ReleaseLootLock(ch, m);                                     // TCell.cpp:387
            SendCS_DELMON_ACK(s, m.Id, exitMap: false);
        }

        _state.LeaveWorld(s);
        s.State = EnterState.Granted;
        foreach (var m in watching) MonsterLostSight(m, s.CharId);
    }

    /// <summary>C++ <c>SendCS_TELEPORT_ACK</c> (CSSender.cpp:3864) — <c>bResult · dwID · bType · dwRange · wMapID ·
    /// x · y · z</c>. Every caller passes <c>dwRange</c> 0.</summary>
    private static void SendCS_TELEPORT_ACK(ClientSession s, byte result, ushort mapId, float x, float y, float z)
    {
        var w = new PacketWriter(Msg.CS_TELEPORT_ACK);
        w.WriteByte(result); w.WriteUInt32(s.CharId); w.WriteByte(OtPc); w.WriteUInt32(0);
        w.WriteUInt16(mapId); w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        s.Send(w);
    }
}
