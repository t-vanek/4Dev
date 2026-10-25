using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Net;

namespace TMap.Server.Map;

/// <summary>
/// The map server's game logic — the C# counterpart of the C++ <c>CTMapSvrModule</c> methods spread
/// across <c>CSHandler.cpp</c>/<c>SSHandler.cpp</c>/<c>CSSender.cpp</c>/<c>SSSender.cpp</c>. One
/// <c>sealed partial class</c> split into <c>MapService.&lt;Feature&gt;.cs</c> files. Every method runs on the
/// single batch thread (see <see cref="MapWorker"/>), so <see cref="MapState"/> access is lock-free.
///
/// Dispatch has two planes: <see cref="DispatchClientAsync"/> handles the encrypted client plane
/// (<c>CS_*</c>) and <see cref="DispatchWorldAsync"/> handles the plaintext world plane (<c>MW_*</c>/
/// <c>SM_*</c>). Handlers are named <c>On&lt;MSG&gt;</c>; senders <c>SendMW_*</c> / <c>SendCS_*</c>.
/// </summary>
public sealed partial class MapService
{
    private readonly MapServerOptions _opt;
    private readonly MapState _state;
    private readonly IWorldSink _world;
    private readonly GameDatabase? _gameDb;
    private readonly IMapEntryStore? _entryStore;
    private readonly TemplateStore _templates;
    private readonly ILogger<MapService> _log;

    private bool _worldReady;
    private uint _tickSeconds;

    /// <summary>The map clock in milliseconds (C++ module <c>m_dwTick</c>) — the tick fed to skill-cooldown
    /// math. Advances with <see cref="OnTimerAsync"/>; settable so tests can drive the reuse checks.</summary>
    public uint NowMs { get; set; }

    public MapService(MapServerOptions opt, MapState state, IWorldSink world, GameDatabase? gameDb,
        TemplateStore templates, ILogger<MapService> log, IMapEntryStore? entryStore = null)
    {
        _opt = opt;
        _state = state;
        _world = world;
        _gameDb = gameDb;
        _entryStore = entryStore ?? gameDb;
        PostStore = gameDb;
        PetStore = gameDb;
        CompanionStore = gameDb;
        GuildSkillStore = gameDb;
        ProtectedStore = gameDb;
        AuctionStore = gameDb;
        HeroStore = gameDb;
        GmStore = gameDb;
        CashStore = gameDb;
        SecurityCodeStore = gameDb;
        CloakStore = gameDb;
        _templates = templates;
        _log = log;
    }

    // Exposed for tests.
    public MapState State => _state;
    public bool WorldReady => _worldReady;

    /// <summary>Computed Max HP/MP for the wire (C++ <c>GetMaxHP</c>/<c>GetMaxMP</c>); falls back to the
    /// synthesized value when the stat charts aren't loaded (DB-free). See <see cref="StatEngine"/>.</summary>
    private uint MaxHpFor(Character ch) => StatEngine.MaxHp(ch, _templates);
    private uint MaxMpFor(Character ch) => StatEngine.MaxMp(ch, _templates);

    // ---- connection lifecycle ----

    public ValueTask OnClientConnectedAsync(ClientConnection conn)
    {
        // Only sets connection-scoped state (accept thread). The single reference write is ordered before
        // any packet from this connection by the FIFO batch channel, and the registry (ByChar) is populated
        // later on the batch thread in OnCS_CONNECT_REQ — so no lock is needed.
        var session = new ClientSession(conn);
        conn.State = session;
        TakeControlSession(conn, session);                                                     // MapService.Gm.cs
        return ValueTask.CompletedTask;
    }

    public void OnClientDisconnect(ClientSession session)
    {
        if (session.IsControl) { if (_control == session) _control = null; return; }        // the control server's link (MapService.Gm.cs)
        // Broadcast the leave to neighbours before de-registering.
        var watching = new List<Monster>();
        if (session.State == EnterState.InGame && session.Char is not null)
        {
            BroadcastLeave(session, exitMap: true);
            watching.AddRange(_state.MonstersInView(session));
        }

        // Tell the world the char left this map (best-effort). The C++ closes via MW_CLOSECHAR / MW_TERMINATE.
        if (session.CharId != 0 && _worldReady)
            SendMW_CLOSECHAR_ACK(session.CharId, session.Key);

        if (session.Char is { } entrant) TournamentExitMap(session, entrant);   // C++ ExitMAP first: the real character back (MapService.TournamentMatch.cs)
        SaveCharData(session);   // flush the char record + dirty quests on logout (C++ SetEventCloseSession)
        if (session.Char is { } fighter) { ArenaLeave(session, fighter, teleport: false); ArenaDequeue(fighter.CharId); }   // C++ ExitMAP (MapService.Arena.cs)
        if (session.State == EnterState.InGame && session.Char is { } carrier) DropGodBall(carrier);   // C++ ExitMAP (MapService.Castle.cs)
        if (session.Char is { } looter) ReleaseLootLock(looter);   // C++ SSHandler.cpp:19205 / TMapSvr.cpp:1544
        if (session.Char is { } dueller) { LeaveDuel(dueller, loses: false); ClearDuel(dueller); }   // CTPlayer release
        if (session.Char is { } leaving) { ClearRecalls(session, leaving); ClearCompanionObjs(leaving); }
        if (session.CharId != 0) EraseBill(session.CharId, 0);   // C++ SM_POSTBILLERASE_REQ(id, 0)
        _state.Remove(session);
        ReleaseMeetingRoom(session, disconnect: true);
        _meetingInvitations.Remove(session.CharId);
        if (!_state.ByChar.Values.Any(s => s.UserId == session.UserId)) _securityCodes.Remove(session.UserId);

        // C++ CTMap::LeaveMAP(player) takes the player off its cell first, then runs LeavePlayer over the 3×3:
        // every monster that could see it drops its hate and re-checks its host, so one this player was
        // driving is handed to someone else (or reset) instead of staying hosted by a ghost.
        foreach (var m in watching) MonsterLostSight(m, session.CharId);
        _log.LogDebug("Client char {Char} disconnected.", session.CharId);
    }

    // ---- client plane (CS_*) ----

    public async Task DispatchClientAsync(ClientSession session, byte[] packet)
    {
        var r = new PacketReader(packet);
        try
        {
            if (session.IsControl) { DispatchControl(session, r); return; }                     // the admin tool's link (MapService.Gm.cs)
            if (r.Id != Msg.CS_CONNECT_REQ && session.State is EnterState.Connected or EnterState.Entering) return;
            if (session.SecurityStateFailed) return;
            if (TournamentRefuses(session, r.Id)) return;                                       // MapService.TournamentMatch.cs
            if (SecurityRefuses(session, r.Id)) return;
            switch (r.Id)
            {
                case Msg.CS_MEETINGROOM_REQ: OnCS_MEETINGROOM_REQ(session, r); break;
                case Msg.CS_CUSTOMCLOAK_LIST_REQ: await OnCS_CUSTOMCLOAK_LIST_REQ(session); break;
                case Msg.CS_CUSTOMCLOAK_CHANGE_REQ: await OnCS_CUSTOMCLOAK_CHANGE_REQ(session, r); break;
                case Msg.CS_REGGUILDCLOAK_REQ: OnCS_REGGUILDCLOAK_REQ(session, r); break;
                case Msg.CS_SC_CREATECODE_REQ:
                case Msg.CS_SC_CHANGECODE_REQ:
                case Msg.CS_SC_DISABLECODE_REQ:
                case Msg.CS_SC_ENABLECODE_REQ:
                case Msg.CS_SC_UNLOCK_REQ:
                case Msg.CS_SC_CHECKCODE_REQ:
                case Msg.CS_SC_GETLOCKTIME_REQ: await OnSecurityCode(session, r); break;
                case Msg.CS_CONNECT_REQ: await OnCS_CONNECT_REQ(session, r); break;
                case Msg.CS_POSTSEND_REQ: await OnCS_POSTSEND_REQ(session, r); break;
                case Msg.CS_POSTLIST_REQ: await OnCS_POSTLIST_REQ(session, r); break;
                case Msg.CS_POSTVIEW_REQ: await OnCS_POSTVIEW_REQ(session, r); break;
                case Msg.CS_POSTDEL_REQ: await OnCS_POSTDEL_REQ(session, r); break;
                case Msg.CS_POSTGETITEM_REQ: await OnCS_POSTGETITEM_REQ(session, r); break;
                case Msg.CS_POSTRETURN_REQ: await OnCS_POSTRETURN_REQ(session, r); break;
                case Msg.CS_CONREADY_REQ: OnCS_CONREADY_REQ(session, r); break;
                case Msg.CS_HOTKEYADD_REQ: OnCS_HOTKEYADD_REQ(session, r); break;
                case Msg.CS_INVENADD_REQ: OnCS_INVENADD_REQ(session, r); break;
                case Msg.CS_ITEMUPGRADE_REQ: OnCS_ITEMUPGRADE_REQ(session, r); break;
                case Msg.CS_REFINE_REQ: OnCS_REFINE_REQ(session, r); break;
                case Msg.CS_ITEMCHANGE_REQ: OnCS_ITEMCHANGE_REQ(session, r); break;
                case Msg.CS_REVIVALASK_REQ: OnCS_REVIVALASK_REQ(session, r); break;
                case Msg.CS_TELEPORT_REQ: OnCS_TELEPORT_REQ(session, r); break;
                case Msg.CS_PETMAKE_REQ: OnCS_PETMAKE_REQ(session, r); break;
                case Msg.CS_PETDEL_REQ: await OnCS_PETDEL_REQ(session, r); break;
                case Msg.CS_PETRECALL_REQ: OnCS_PETRECALL_REQ(session, r); break;
                case Msg.CS_PETCANCEL_REQ: OnCS_PETCANCEL_REQ(session, r); break;
                case Msg.CS_PETRIDING_REQ: OnCS_PETRIDING_REQ(session, r); break;
                case Msg.CS_PETEFFECTCHANGE_REQ: OnCS_PETEFFECTCHANGE_REQ(session, r); break;
                case Msg.CS_REQUESTSADDLE_REQ: OnCS_REQUESTSADDLE_REQ(session, r); break;
                case Msg.CS_CREATESADDLE_REQ: await OnCS_CREATESADDLE_REQ(session, r); break;
                case Msg.CS_DELETESADDLE_REQ: await OnCS_DELETESADDLE_REQ(session, r); break;
                case Msg.CS_DELRECALLMON_REQ: OnCS_DELRECALLMON_REQ(session, r); break;
                case Msg.CS_CHGMODERECALLMON_REQ: OnCS_CHGMODERECALLMON_REQ(session, r); break;
                case Msg.CS_CREATECOMPANION_REQ: OnCS_CREATECOMPANION_REQ(session, r); break;
                case Msg.CS_DELETECOMPANION_REQ: await OnCS_DELETECOMPANION_REQ(session, r); break;
                case Msg.CS_COMPANIONRECALL_REQ: OnCS_COMPANIONRECALL_REQ(session, r); break;
                case Msg.CS_COMPANIONCANCEL_REQ: OnCS_COMPANIONCANCEL_REQ(session, r); break;
                case Msg.CS_HIDECOMPANION_REQ: OnCS_HIDECOMPANION_REQ(session, r); break;
                case Msg.CS_COMPANIONUPGRADE_REQ: OnCS_COMPANIONUPGRADE_REQ(session, r); break;
                case Msg.CS_COMPANIONLUP_REQ: OnCS_COMPANIONLUP_REQ(session, r); break;
                case Msg.CS_USEPETITEM_REQ: OnCS_USEPETITEM_REQ(session, r); break;
                case Msg.CS_USECOMPANIONITEM_REQ: OnCS_USECOMPANIONITEM_REQ(session, r); break;
                case Msg.CS_DELETECOMPITEMS_REQ: OnCS_DELETECOMPITEMS_REQ(session, r); break;
                case Msg.CS_CHANGECOMPANIONEFFECT_REQ: OnCS_CHANGECOMPANIONEFFECT_REQ(session, r); break;
                case Msg.CS_USECOMPANIONPOWDER_REQ: OnCS_USECOMPANIONPOWDER_REQ(session, r); break;
                case Msg.CS_USECOMPRESET_REQ: OnCS_USECOMPRESET_REQ(session, r); break;
                case Msg.CS_FINISHCOMPANIONTRANSFER_ACK: OnCS_FINISHCOMPANIONTRANSFER_ACK(session, r); break;
                case Msg.CS_CHGMODESPOLECNIKMON_REQ: OnCS_CHGMODESPOLECNIKMON_REQ(session, r); break;
                case Msg.CS_DELSPOLECNIKMON_REQ: OnCS_DELSPOLECNIKMON_REQ(session, r); break;
                case Msg.CS_SPOLECNIKRECALL_REQ: break;   // its C++ body is commented out
                case Msg.CS_INVENDEL_REQ: OnCS_INVENDEL_REQ(session, r); break;
                case Msg.CS_INVENMOVE_REQ: OnCS_INVENMOVE_REQ(session, r); break;
                case Msg.CS_SETRETURNPOS_REQ: OnCS_SETRETURNPOS_REQ(session, r); break;
                case Msg.CS_PARTYADD_REQ: OnCS_PARTYADD_REQ(session, r); break;
                case Msg.CS_DUELINVITE_REQ: OnCS_DUELINVITE_REQ(session, r); break;
                case Msg.CS_DUELINVITEREPLY_REQ: OnCS_DUELINVITEREPLY_REQ(session, r); break;
                case Msg.CS_DUELEND_REQ: OnCS_DUELEND_REQ(session, r); break;
                case Msg.CS_MONTHRANKLIST_REQ: OnCS_MONTHRANKLIST_REQ(session); break;
                case Msg.CS_FIRSTGRADEGROUP_REQ: OnCS_FIRSTGRADEGROUP_REQ(session); break;
                case Msg.CS_FAMERANKLIST_REQ: OnCS_FAMERANKLIST_REQ(session, r); break;
                case Msg.CS_PVPRECORD_REQ: OnCS_PVPRECORD_REQ(session, r); break;
                case Msg.CS_TITLELIST_REQ: OnCS_TITLELIST_REQ(session); break;
                case Msg.CS_CHANGETITLE_REQ: OnCS_CHANGETITLE_REQ(session, r); break;
                case Msg.CS_CHANGENAME_REQ: await OnCS_CHANGENAME_REQ(session, r); break;
                case Msg.CS_CHANGECOUNTRY_REQ: OnCS_CHANGECOUNTRY_REQ(session, r); break;
                case Msg.CS_PARTYJOIN_REQ: OnCS_PARTYJOIN_REQ(session, r); break;
                case Msg.CS_PARTYDEL_REQ: OnCS_PARTYDEL_REQ(session, r); break;
                case Msg.CS_CHGPARTYCHIEF_REQ: OnCS_CHGPARTYCHIEF_REQ(session, r); break;
                case Msg.CS_CHGPARTYTYPE_REQ: OnCS_CHGPARTYTYPE_REQ(session, r); break;
                case Msg.CS_PARTYMOVE_REQ: OnCS_PARTYMOVE_REQ(session, r); break;
                case Msg.CS_HOTKEYDEL_REQ: OnCS_HOTKEYDEL_REQ(session, r); break;
                case Msg.CS_MOVE_REQ: OnCS_MOVE_REQ(session, r); break;
                case Msg.CS_JUMP_REQ: OnCS_JUMP_REQ(session, r); break;
                case Msg.CS_BLOCK_REQ: OnCS_BLOCK_REQ(session, r); break;
                case Msg.CS_CHGMODE_REQ: OnCS_CHGMODE_REQ(session, r); break;
                case Msg.CS_GUILDLOCALLIST_REQ: OnCS_GUILDLOCALLIST_REQ(session, r); break;
                case Msg.CS_GUILDLOCALRETURN_REQ: OnCS_GUILDLOCALRETURN_REQ(session, r); break;
                case Msg.CS_GUILDCABINETLIST_REQ: OnCS_GUILDCABINETLIST_REQ(session, r); break;
                case Msg.CS_GUILDCABINETPUTIN_REQ: OnCS_GUILDCABINETPUTIN_REQ(session, r); break;
                case Msg.CS_GUILDCABINETTAKEOUT_REQ: OnCS_GUILDCABINETTAKEOUT_REQ(session, r); break;
                case Msg.CS_GUILDCONTRIBUTION_REQ: OnCS_GUILDCONTRIBUTION_REQ(session, r); break;
                case Msg.CS_GUILDARTICLELIST_REQ: OnCS_GUILDARTICLELIST_REQ(session, r); break;
                case Msg.CS_GUILDARTICLEADD_REQ: OnCS_GUILDARTICLEADD_REQ(session, r); break;
                case Msg.CS_GUILDARTICLEDEL_REQ: OnCS_GUILDARTICLEDEL_REQ(session, r); break;
                case Msg.CS_GUILDARTICLEUPDATE_REQ: OnCS_GUILDARTICLEUPDATE_REQ(session, r); break;
                case Msg.CS_GUILDFAME_REQ: OnCS_GUILDFAME_REQ(session, r); break;
                case Msg.CS_GUILDWANTEDADD_REQ: OnCS_GUILDWANTEDADD_REQ(session, r); break;
                case Msg.CS_GUILDWANTEDDEL_REQ: OnCS_GUILDWANTEDDEL_REQ(session, r); break;
                case Msg.CS_GUILDWANTEDLIST_REQ: OnCS_GUILDWANTEDLIST_REQ(session, r); break;
                case Msg.CS_GUILDVOLUNTEERING_REQ: OnCS_GUILDVOLUNTEERING_REQ(session, r); break;
                case Msg.CS_GUILDVOLUNTEERINGDEL_REQ: OnCS_GUILDVOLUNTEERINGDEL_REQ(session, r); break;
                case Msg.CS_GUILDVOLUNTEERLIST_REQ: OnCS_GUILDVOLUNTEERLIST_REQ(session, r); break;
                case Msg.CS_GUILDVOLUNTEERREPLY_REQ: OnCS_GUILDVOLUNTEERREPLY_REQ(session, r); break;
                case Msg.CS_GUILDPOINTLOG_REQ: OnCS_GUILDPOINTLOG_REQ(session, r); break;
                case Msg.CS_GUILDPVPRECORD_REQ: OnCS_GUILDPVPRECORD_REQ(session, r); break;
                case Msg.CS_GUILDTACTICSWANTEDADD_REQ: OnCS_GUILDTACTICSWANTEDADD_REQ(session, r); break;
                case Msg.CS_GUILDTACTICSWANTEDDEL_REQ: OnCS_GUILDTACTICSWANTEDDEL_REQ(session, r); break;
                case Msg.CS_GUILDTACTICSWANTEDLIST_REQ: OnCS_GUILDTACTICSWANTEDLIST_REQ(session); break;
                case Msg.CS_GUILDTACTICSVOLUNTEERING_REQ: OnCS_GUILDTACTICSVOLUNTEERING_REQ(session, r); break;
                case Msg.CS_GUILDTACTICSVOLUNTEERINGDEL_REQ: OnCS_GUILDTACTICSVOLUNTEERINGDEL_REQ(session); break;
                case Msg.CS_GUILDTACTICSVOLUNTEERLIST_REQ: OnCS_GUILDTACTICSVOLUNTEERLIST_REQ(session); break;
                case Msg.CS_GUILDTACTICSREPLY_REQ: OnCS_GUILDTACTICSREPLY_REQ(session, r); break;
                case Msg.CS_GUILDTACTICSKICKOUT_REQ: OnCS_GUILDTACTICSKICKOUT_REQ(session, r); break;
                case Msg.CS_GUILDTACTICSINVITE_REQ: OnCS_GUILDTACTICSINVITE_REQ(session, r); break;
                case Msg.CS_GUILDTACTICSANSWER_REQ: OnCS_GUILDTACTICSANSWER_REQ(session, r); break;
                case Msg.CS_GUILDTACTICSLIST_REQ: OnCS_GUILDTACTICSLIST_REQ(session); break;
                case Msg.CS_GUILDPOINTREWARD_REQ: OnCS_GUILDPOINTREWARD_REQ(session, r); break;
                case Msg.CS_MONSTERBUY_REQ: OnCS_MONSTERBUY_REQ(session, r); break;
                case Msg.CS_GUILDSKILLACTION_REQ: OnCS_GUILDSKILLACTION_REQ(session, r); break;
                case Msg.CS_GUILDESTABLISH_REQ: OnCS_GUILDESTABLISH_REQ(session, r); break;
                case Msg.CS_GUILDDISORGANIZATION_REQ: OnCS_GUILDDISORGANIZATION_REQ(session, r); break;
                case Msg.CS_GUILDINVITE_REQ: OnCS_GUILDINVITE_REQ(session, r); break;
                case Msg.CS_GUILDINVITEANSWER_REQ: OnCS_GUILDINVITEANSWER_REQ(session, r); break;
                case Msg.CS_GUILDLEAVE_REQ: OnCS_GUILDLEAVE_REQ(session, r); break;
                case Msg.CS_GUILDKICKOUT_REQ: OnCS_GUILDKICKOUT_REQ(session, r); break;
                case Msg.CS_GUILDDUTY_REQ: OnCS_GUILDDUTY_REQ(session, r); break;
                case Msg.CS_GUILDPEER_REQ: OnCS_GUILDPEER_REQ(session, r); break;
                case Msg.CS_GUILDMEMBERLIST_REQ: OnCS_GUILDMEMBERLIST_REQ(session, r); break;
                case Msg.CS_GUILDINFO_REQ: OnCS_GUILDINFO_REQ(session, r); break;
                case Msg.CS_PROTECTEDADD_REQ: await OnCS_PROTECTEDADD_REQ(session, r); break;      // MapService.Social.cs
                case Msg.CS_PROTECTEDERASE_REQ: OnCS_PROTECTEDERASE_REQ(session, r); break;
                case Msg.CS_PROTECTEDOPTION_REQ: OnCS_PROTECTEDOPTION_REQ(session, r); break;
                case Msg.CS_FRIENDLIST_REQ: OnCS_FRIENDLIST_REQ(session); break;
                case Msg.CS_FRIENDASK_REQ: OnCS_FRIENDASK_REQ(session, r); break;
                case Msg.CS_FRIENDREPLY_REQ: OnCS_FRIENDREPLY_REQ(session, r); break;
                case Msg.CS_FRIENDERASE_REQ: OnCS_FRIENDERASE_REQ(session, r); break;
                case Msg.CS_FRIENDGROUPMAKE_REQ: OnCS_FRIENDGROUPMAKE_REQ(session, r); break;
                case Msg.CS_FRIENDGROUPDELETE_REQ: OnCS_FRIENDGROUPDELETE_REQ(session, r); break;
                case Msg.CS_FRIENDGROUPCHANGE_REQ: OnCS_FRIENDGROUPCHANGE_REQ(session, r); break;
                case Msg.CS_FRIENDGROUPNAME_REQ: OnCS_FRIENDGROUPNAME_REQ(session, r); break;
                case Msg.CS_TMSSEND_REQ: OnCS_TMSSEND_REQ(session, r); break;
                case Msg.CS_TMSINVITE_REQ: OnCS_TMSINVITE_REQ(session, r); break;
                case Msg.CS_TMSOUT_REQ: OnCS_TMSOUT_REQ(session, r); break;
                case Msg.CS_CORPSASK_REQ: CorpsToWorld(session, r, Msg.MW_CORPSASK_ACK); break;              // MapService.Corps.cs
                case Msg.CS_CORPSREPLY_REQ: { string n = r.ReadString(); byte a = r.ReadByte(); if (session.IsMain && session.Char is not null) SendToWorld(Msg.MW_CORPSREPLY_ACK, session, w => { w.WriteByte(a); w.WriteString(n); }); break; }
                case Msg.CS_CORPSLEAVE_REQ: CorpsToWorld(session, r, Msg.MW_CORPSLEAVE_ACK); break;
                case Msg.CS_CHGCORPSCOMMANDER_REQ: CorpsToWorld(session, r, Msg.MW_CHGCORPSCOMMANDER_ACK); break;
                case Msg.CS_CORPSCMD_REQ: OnCS_CORPSCMD_REQ(session, r); break;
                case Msg.CS_CORPSENEMYLIST_REQ: CorpsToWorld(session, r, Msg.MW_CORPSENEMYLIST_ACK); break;
                case Msg.CS_MOVECORPSENEMY_REQ: CorpsToWorld(session, r, Msg.MW_MOVECORPSENEMY_ACK); break;
                case Msg.CS_MOVECORPSUNIT_REQ: CorpsToWorld(session, r, Msg.MW_MOVECORPSUNIT_ACK); break;
                case Msg.CS_ADDCORPSENEMY_REQ: CorpsToWorld(session, r, Msg.MW_ADDCORPSENEMY_ACK); break;
                case Msg.CS_DELCORPSENEMY_REQ: CorpsToWorld(session, r, Msg.MW_DELCORPSENEMY_ACK); break;
                case Msg.CS_CORPSHP_REQ: CorpsToWorld(session, r, Msg.MW_CORPSHP_ACK); break;
                case Msg.CS_PARTYMEMBERRECALL_REQ: OnCS_PARTYMEMBERRECALL_REQ(session, r); break;
                case Msg.CS_PARTYMEMBERRECALLANS_REQ: OnCS_PARTYMEMBERRECALLANS_REQ(session, r); break;
                case Msg.CS_TOURNAMENTAPPLYINFO_REQ: OnCS_TOURNAMENTAPPLYINFO_REQ(session); break;           // MapService.Tournament.cs
                case Msg.CS_TOURNAMENTAPPLY_REQ: OnCS_TOURNAMENTAPPLY_REQ(session, r); break;
                case Msg.CS_TOURNAMENTPARTYLIST_REQ: OnCS_TOURNAMENTPARTYLIST_REQ(session, r); break;
                case Msg.CS_TOURNAMENTPARTYADD_REQ: OnCS_TOURNAMENTPARTYADD_REQ(session, r); break;
                case Msg.CS_TOURNAMENTPARTYDEL_REQ: OnCS_TOURNAMENTPARTYDEL_REQ(session, r); break;
                case Msg.CS_TOURNAMENTEVENTLIST_REQ: OnCS_TOURNAMENTEVENTLIST_REQ(session); break;
                case Msg.CS_TOURNAMENTEVENTINFO_REQ: OnCS_TOURNAMENTEVENTINFO_REQ(session, r); break;
                case Msg.CS_TOURNAMENTEVENTJOIN_REQ: OnCS_TOURNAMENTEVENTJOIN_REQ(session, r); break;
                case Msg.CS_TOURNAMENTCHEER_REQ: OnCS_TOURNAMENTCHEER_REQ(session, r); break;
                case Msg.CS_TOURNAMENTSCHEDULE_REQ: OnCS_TOURNAMENTSCHEDULE_REQ(session); break;
                case Msg.CS_ARENA_REQ: OnCS_ARENA_REQ(session, r); break;                                   // MapService.Arena.cs
                case Msg.CS_ARENAINFO_REQ: OnCS_ARENAINFO_REQ(session, r); break;
                case Msg.CS_ARENARANKING_REQ: OnCS_ARENARANKING_REQ(session, r); break;
                case Msg.CS_DROPDAMAGE_REQ: OnCS_DROPDAMAGE_REQ(session, r); break;                // MapService.ItemLeftovers.cs
                case Msg.CS_CMMOVE_REQ: OnCS_CMMOVE_REQ(session, r); break;                        // MapService.Gm.cs
                case Msg.CS_CMMOVETOUSER_REQ: OnCS_CMMOVETOUSER_REQ(session, r); break;
                case Msg.CS_CMGIFT_REQ: OnCS_CMGIFT_REQ(session, r); break;
                case Msg.CS_CHATBAN_REQ: OnCS_CHATBAN_REQ(session, r); break;
                case Msg.CS_KICKOUTMAP_REQ: OnCS_KICKOUTMAP_REQ(session); break;
                case Msg.CS_CASHSHOPITEMLIST_REQ: await OnCS_CASHSHOPITEMLIST_REQ(session, r); break;    // MapService.CashShop.cs
                case Msg.CS_CASHITEMBUY_REQ: await OnCS_CASHITEMBUY_REQ(session, r); break;
                case Msg.CS_CASHITEMPRESENT_REQ: await OnCS_CASHITEMPRESENT_REQ(session, r); break;
                case Msg.CS_CASHITEMCABINET_REQ: await OnCS_CASHITEMCABINET_REQ(session); break;
                case Msg.CS_CASHITEMGET_REQ: await OnCS_CASHITEMGET_REQ(session, r); break;
                case Msg.CS_CASHITEMPUTIN_REQ: await OnCS_CASHITEMPUTIN_REQ(session, r); break;
                case Msg.CS_CASHCABINETBUY_REQ: await OnCS_CASHCABINETBUY_REQ(session, r); break;
                case Msg.CS_ACTITEMSUSE_REQ: OnCS_ACTITEMSUSE_REQ(session, r); break;
                case Msg.CS_USEMEDALITEM_REQ: await OnCS_USEMEDALITEM_REQ(session, r); break;
                case Msg.CS_GETREWARDS_REQ: OnCS_GETREWARDS_REQ(session, r); break;
                case Msg.CS_STOPTHECLOCK_REQ: await OnCS_STOPTHECLOCK_REQ(session, r); break;
                case Msg.CS_WASTEREFINE_REQ: OnCS_WASTEREFINE_REQ(session, r); break;
                case Msg.CS_REGISTERBOW_REQ or Msg.CS_CANCELBOWQUEUE_REQ or Msg.CS_REGISTERBR_REQ or Msg.CS_BRTEAMMATEADD_REQ
                    or Msg.CS_BRTEAMMATEDEL_REQ or Msg.CS_BRTEAMMATEADDRESULT_ACK or Msg.CS_VOTEFORBRMAP_REQ or Msg.CS_CMTELEPORTBATTLEMODE_REQ:
                    OnCS_BattleQueue(session, r.Id, r); break;                                  // MapService.BattleQueue.cs
                case Msg.CS_EXCHANGEBOWBP_REQ or Msg.CS_BOWRANKING_REQ or Msg.CS_LEAVEBATTLEFIELD_REQ: break;   // the battle servers'
                case Msg.CS_HEROLIST_REQ: OnCS_HEROLIST_REQ(session); break;                                // MapService.Heroes.cs
                case Msg.CS_HEROSELECT_REQ: await OnCS_HEROSELECT_REQ(session, r); break;
                case Msg.CS_WARLORDSAY_REQ: await OnCS_WARLORDSAY_REQ(session, r); break;
                case Msg.CS_WARCOUNTRYBALANCE_REQ: OnCS_WARCOUNTRYBALANCE_REQ(session); break;              // MapService.CountryBalance.cs
                case Msg.CS_MONITEMLOTTERY_REQ: OnCS_MONITEMLOTTERY_REQ(session, r); break;                 // MapService.LootLottery.cs
                case Msg.CS_RPSSTART_REQ: OnCS_RPSSTART_REQ(session, r); break;                             // MapService.Rps.cs
                case Msg.CS_RPSGAME_REQ: OnCS_RPSGAME_REQ(session, r); break;
                case Msg.CS_SOULLOTTERY_REQ: OnCS_SOULLOTTERY_REQ(session); break;                          // MapService.SoulLottery.cs
                case Msg.CS_SOULLOTTERY_TRIGGER_REQ: OnCS_SOULLOTTERY_TRIGGER_REQ(session); break;
                case Msg.CS_GAMBLECHECK_REQ: OnCS_GAMBLECHECK_REQ(session, r); break;                       // MapService.Gamble.cs
                case Msg.CS_GAMBLEOPEN_REQ: OnCS_GAMBLEOPEN_REQ(session, r); break;
                case Msg.CS_MAGICITEMBUY_REQ: OnCS_MAGICITEMBUY_REQ(session, r); break;
                case Msg.CS_AUCTIONREG_REQ: await OnCS_AUCTIONREG_REQ(session, r); break;                   // MapService.Auction.cs
                case Msg.CS_AUCTIONREGCANCEL_REQ: await OnCS_AUCTIONREGCANCEL_REQ(session, r); break;
                case Msg.CS_AUCTIONBID_REQ: await OnCS_AUCTIONBID_REQ(session, r); break;
                case Msg.CS_AUCTIONBUYDIRECT_REQ: await OnCS_AUCTIONBUYDIRECT_REQ(session, r); break;
                case Msg.CS_AUCTIONFIND_REQ: OnCS_AUCTIONFIND_REQ(session, r); break;
                case Msg.CS_AUCTIONINTEREST_REQ: OnCS_AUCTIONINTEREST_REQ(session, r); break;
                case Msg.CS_AUCTIONINTERESTLIST_REQ: OnCS_AUCTIONMYLIST_REQ(session, r, Msg.CS_AUCTIONINTERESTLIST_ACK, c => c.AuctionInterest, false); break;
                case Msg.CS_AUCTIONBIDLIST_REQ: OnCS_AUCTIONMYLIST_REQ(session, r, Msg.CS_AUCTIONBIDLIST_ACK, c => c.AuctionBid, false); break;
                case Msg.CS_AUCTIONREGLIST_REQ: OnCS_AUCTIONMYLIST_REQ(session, r, Msg.CS_AUCTIONREGLIST_ACK, c => c.AuctionReg, true); break;
                case Msg.CS_SOULMATESEARCH_REQ: OnCS_SOULMATESEARCH_REQ(session, r); break;          // MapService.Soulmate.cs
                case Msg.CS_SOULMATEREGREADY_REQ: OnCS_SOULMATEREGREADY_REQ(session, r); break;
                case Msg.CS_SOULMATEREG_REQ: OnCS_SOULMATEREG_REQ(session, r); break;
                case Msg.CS_SOULMATEEND_REQ: OnCS_SOULMATEEND_REQ(session); break;
                case Msg.CS_CANCELSKILL_REQ: OnCS_CANCELSKILL_REQ(session, r); break;
                case Msg.CS_LOOPSKILL_REQ: OnCS_LOOPSKILL_REQ(session, r); break;
                case Msg.CS_CANCELACTION_REQ: OnCS_CANCELACTION_REQ(session, r); break;
                case Msg.CS_ACTEND_REQ: break;                              // C++ body commented out
                case Msg.CS_GETTARGET_REQ: OnCS_GETTARGET_REQ(session, r); break;
                case Msg.CS_GETTARGETANS_REQ: OnCS_GETTARGETANS_REQ(session, r); break;
                case Msg.CS_MONITEMTAKEALL_REQ: OnCS_MONITEMTAKEALL_REQ(session, r); break;
                case Msg.CS_QUESTENDTIMER_REQ: OnCS_QUESTENDTIMER_REQ(session, r); break;
                case Msg.CS_QUESTPOSEXEC_REQ: OnCS_QUESTPOSEXEC_REQ(session, r); break;
                case Msg.CS_HELMETHIDE_REQ: OnCS_HELMETHIDE_REQ(session, r); break;
                case Msg.CS_COMMENT_REQ: OnCS_COMMENT_REQ(session, r); break;
                // The client-reported aggro bounds — one handler, four triggers.
                case Msg.CS_ENTERLB_REQ: OnAggroBoundReq(session, r, AiTrigger.EnterLb); break;
                case Msg.CS_LEAVELB_REQ: OnAggroBoundReq(session, r, AiTrigger.LeaveLb); break;
                case Msg.CS_ENTERAB_REQ: OnAggroBoundReq(session, r, AiTrigger.EnterAb); break;
                case Msg.CS_LEAVEAB_REQ: OnAggroBoundReq(session, r, AiTrigger.LeaveAb); break;
                case Msg.CS_CHARSTATINFO_REQ: OnCS_CHARSTATINFO_REQ(session, r); break;
                case Msg.CS_MOVEITEM_REQ: OnCS_MOVEITEM_REQ(session, r); break;
                case Msg.CS_ITEMUSE_REQ: OnCS_ITEMUSE_REQ(session, r); break;
                case Msg.CS_DURATIONREP_REQ: OnCS_DURATIONREP_REQ(session, r); break;
                case Msg.CS_MONMOVE_REQ: OnCS_MONMOVE_REQ(session, r); break;
                case Msg.CS_ACTION_REQ: OnCS_ACTION_REQ(session, r); break;
                case Msg.CS_FINISHSKILL_ACK: OnCS_FINISHSKILL_ACK(session, r); break;
                case Msg.CS_DEFEND_REQ: OnCS_DEFEND_REQ(session, r); break;
                case Msg.CS_SKILLUSE_REQ: OnCS_SKILLUSE_REQ(session, r); break;
                case Msg.CS_SKILLEND_REQ: OnCS_SKILLEND_REQ(session, r); break;
                case Msg.CS_SKILLBUY_REQ: OnCS_SKILLBUY_REQ(session, r); break;
                case Msg.CS_NPCITEMLIST_REQ: OnCS_NPCITEMLIST_REQ(session, r); break;
                case Msg.CS_SKILLINIT_REQ: OnCS_SKILLINIT_REQ(session, r); break;
                case Msg.CS_SKILLINITPOSSIBLE_REQ: OnCS_SKILLINITPOSSIBLE_REQ(session, r); break;
                case Msg.CS_SWITCHCHANGE_REQ: OnCS_SWITCHCHANGE_REQ(session, r); break;
                case Msg.CS_MONITEMLIST_REQ: OnCS_MONITEMLIST_REQ(session, r); break;
                case Msg.CS_MONMONEYTAKE_REQ: OnCS_MONMONEYTAKE_REQ(session, r); break;
                case Msg.CS_MONITEMTAKE_REQ: OnCS_MONITEMTAKE_REQ(session, r); break;
                case Msg.CS_CABINETOPEN_REQ: OnCS_CABINETOPEN_REQ(session, r); break;
                case Msg.CS_CABINETLIST_REQ: OnCS_CABINETLIST_REQ(session, r); break;
                case Msg.CS_CABINETITEMLIST_REQ: OnCS_CABINETITEMLIST_REQ(session, r); break;
                case Msg.CS_CABINETPUTIN_REQ: OnCS_CABINETPUTIN_REQ(session, r); break;
                case Msg.CS_CABINETTAKEOUT_REQ: OnCS_CABINETTAKEOUT_REQ(session, r); break;
                case Msg.CS_DEALITEMASK_REQ: OnCS_DEALITEMASK_REQ(session, r); break;
                case Msg.CS_DEALITEMRLY_REQ: OnCS_DEALITEMRLY_REQ(session, r); break;
                case Msg.CS_DEALITEMADD_REQ: OnCS_DEALITEMADD_REQ(session, r); break;
                case Msg.CS_DEALITEM_REQ: OnCS_DEALITEM_REQ(session, r); break;
                case Msg.CS_STOREOPEN_REQ: OnCS_STOREOPEN_REQ(session, r); break;
                case Msg.CS_STORECLOSE_REQ: OnCS_STORECLOSE_REQ(session, r); break;
                case Msg.CS_STOREITEMLIST_REQ: OnCS_STOREITEMLIST_REQ(session, r); break;
                case Msg.CS_STOREITEMBUY_REQ: OnCS_STOREITEMBUY_REQ(session, r); break;
                case Msg.CS_REVIVAL_REQ: OnCS_REVIVAL_REQ(session, r); break;
                case Msg.CS_NPCTALK_REQ: OnCS_NPCTALK_REQ(session, r); break;
                case Msg.CS_ITEMBUY_REQ: OnCS_ITEMBUY_REQ(session, r); break;
                case Msg.CS_ITEMSELL_REQ: OnCS_ITEMSELL_REQ(session, r); break;
                case Msg.CS_QUESTEXEC_REQ: OnCS_QUESTEXEC_REQ(session, r); break;
                case Msg.CS_QUESTDROP_REQ: OnCS_QUESTDROP_REQ(session, r); break;
                case Msg.CS_QUESTLIST_POSSIBLE_REQ: OnCS_QUESTLIST_POSSIBLE_REQ(session, r); break;
                case Msg.CS_CHAT_REQ: OnCS_CHAT_REQ(session, r); break;
                case Msg.CS_REGION_REQ: OnCS_REGION_REQ(session, r); break;
                case Msg.CS_CASTLEAPPLY_REQ: OnCS_CASTLEAPPLY_REQ(session, r); break;
                case Msg.CS_TAKEGODBALL_REQ: OnCS_TAKEGODBALL_REQ(session, r); break;
                case Msg.CS_MOUNTGODBALL_REQ: OnCS_MOUNTGODBALL_REQ(session, r); break;
                case Msg.CS_DEMOUNTGODBALL_REQ: OnCS_DEMOUNTGODBALL_REQ(session, r); break;
                case Msg.CS_PINGMEASUREMENT_REQ: OnCS_PINGMEASUREMENT_REQ(session, r); break;
                case Msg.CS_COUNTDOWN_REQ: OnCS_COUNTDOWN_REQ(session, r); break;
                case Msg.CS_DISCONNECT_REQ: OnCS_DISCONNECT_REQ(session, r); break;
                case Msg.CS_TERMINATE_REQ: OnCS_TERMINATE_REQ(session, r); break;
                case Msg.CS_CHGCHANNEL_REQ: OnCS_CHGCHANNEL_REQ(session, r); break;
                default:
                    _log.LogDebug("Unhandled client message 0x{Id:X4} from char {Char}.", r.Id, session.CharId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Client handler 0x{Id:X4} failed.", r.Id);
        }
    }

    // ---- world plane (MW_* / SM_*) ----

    public async Task DispatchWorldAsync(byte[] packet)
    {
        var r = new PacketReader(packet);
        try
        {
            switch (r.Id)
            {
                case Msg.MW_ENTERSVR_REQ: await OnMW_ENTERSVR_REQ(r); break;
                case Msg.MW_CHARDATA_REQ: OnMW_CHARDATA_REQ(r); break;
                case Msg.MW_CHARINFO_REQ: await OnMW_CHARINFO_REQ(r); break;
                case Msg.MW_HELMETHIDE_REQ: OnMW_HELMETHIDE_REQ(r); break;
                case Msg.MW_LOCALENABLE_REQ: OnMW_LOCALENABLE_REQ(r); break;
                case Msg.MW_CASTLEENABLE_REQ: OnMW_CASTLEENABLE_REQ(r); break;
                case Msg.MW_CASTLEAPPLY_REQ: OnMW_CASTLEAPPLY_REQ(r); break;
                case Msg.MW_GUILDCABINETLIST_REQ: OnMW_GUILDCABINETLIST_REQ(r); break;
                case Msg.MW_GUILDCONTRIBUTION_REQ: OnMW_GUILDCONTRIBUTION_REQ(r); break;
                case Msg.MW_GUILDFAME_REQ: OnMW_GUILDFAME_REQ(r); break;
                case Msg.MW_GUILDARTICLELIST_REQ: RelayToClient(r, Msg.CS_GUILDARTICLELIST_ACK); break;
                case Msg.MW_GUILDWANTEDLIST_REQ: RelayToClient(r, Msg.CS_GUILDWANTEDLIST_ACK); break;
                case Msg.MW_GUILDVOLUNTEERLIST_REQ: RelayToClient(r, Msg.CS_GUILDVOLUNTEERLIST_ACK); break;
                case Msg.MW_GUILDPOINTLOG_REQ: RelayToClient(r, Msg.CS_GUILDPOINTLOG_ACK); break;
                case Msg.MW_GUILDPVPRECORD_REQ: RelayToClient(r, Msg.CS_GUILDPVPRECORD_ACK); break;
                case Msg.MW_GUILDTACTICSWANTEDLIST_REQ: RelayToClient(r, Msg.CS_GUILDTACTICSWANTEDLIST_ACK); break;
                case Msg.MW_GUILDTACTICSVOLUNTEERLIST_REQ: RelayToClient(r, Msg.CS_GUILDTACTICSVOLUNTEERLIST_ACK); break;
                case Msg.MW_GUILDTACTICSLIST_REQ: RelayToClient(r, Msg.CS_GUILDTACTICSLIST_ACK); break;
                case Msg.MW_GUILDTACTICSWANTEDADD_REQ: RelayResult(r, Msg.CS_GUILDTACTICSWANTEDADD_ACK); break;
                case Msg.MW_GUILDTACTICSWANTEDDEL_REQ: RelayResult(r, Msg.CS_GUILDTACTICSWANTEDDEL_ACK); break;
                case Msg.MW_GUILDTACTICSVOLUNTEERING_REQ: RelayResult(r, Msg.CS_GUILDTACTICSVOLUNTEERING_ACK); break;
                case Msg.MW_GUILDTACTICSVOLUNTEERINGDEL_REQ: RelayResult(r, Msg.CS_GUILDTACTICSVOLUNTEERINGDEL_ACK); break;
                case Msg.MW_GUILDTACTICSINVITE_REQ: OnMW_GUILDTACTICSINVITE_REQ(r); break;
                case Msg.MW_GUILDTACTICSANSWER_REQ: OnMW_GUILDTACTICSJOIN(r, Msg.CS_GUILDTACTICSANSWER_ACK); break;
                case Msg.MW_GUILDTACTICSREPLY_REQ: OnMW_GUILDTACTICSJOIN(r, Msg.CS_GUILDTACTICSREPLY_ACK); break;
                case Msg.MW_GUILDTACTICSKICKOUT_REQ: OnMW_GUILDTACTICSKICKOUT_REQ(r); break;
                case Msg.MW_GUILDPOINTREWARD_REQ: OnMW_GUILDPOINTREWARD_REQ(r); break;
                case Msg.MW_MONSTERBUY_REQ: OnMW_MONSTERBUY_REQ(r); break;
                case Msg.MW_GUILDARTICLEADD_REQ: RelayResult(r, Msg.CS_GUILDARTICLEADD_ACK); break;
                case Msg.MW_GUILDARTICLEDEL_REQ: RelayResult(r, Msg.CS_GUILDARTICLEDEL_ACK); break;
                case Msg.MW_GUILDARTICLEUPDATE_REQ: RelayResult(r, Msg.CS_GUILDARTICLEUPDATE_ACK); break;
                case Msg.MW_GUILDWANTEDADD_REQ: RelayResult(r, Msg.CS_GUILDWANTEDADD_ACK); break;
                case Msg.MW_GUILDWANTEDDEL_REQ: RelayResult(r, Msg.CS_GUILDWANTEDDEL_ACK); break;
                case Msg.MW_GUILDVOLUNTEERING_REQ: RelayResult(r, Msg.CS_GUILDVOLUNTEERING_ACK); break;
                case Msg.MW_GUILDVOLUNTEERINGDEL_REQ: RelayResult(r, Msg.CS_GUILDVOLUNTEERINGDEL_ACK); break;
                case Msg.MW_GUILDVOLUNTEERREPLY_REQ: RelayResult(r, Msg.CS_GUILDVOLUNTEERREPLY_ACK); break;
                case Msg.MW_GUILDESTABLISH_REQ: await OnMW_GUILDESTABLISH_REQ(r); break;
                case Msg.MW_GUILDDISORGANIZATION_REQ: OnMW_GUILDDISORGANIZATION_REQ(r); break;
                case Msg.MW_GUILDINVITE_REQ: OnMW_GUILDINVITE_REQ(r); break;
                case Msg.MW_GUILDJOIN_REQ: await OnMW_GUILDJOIN_REQ(r); break;
                case Msg.MW_GUILDDUTY_REQ: await OnMW_GUILDDUTY_REQ(r); break;
                case Msg.MW_GUILDPEER_REQ: OnMW_GUILDPEER_REQ(r); break;
                case Msg.MW_GUILDLEAVE_REQ: await OnMW_GUILDLEAVE_REQ(r); break;
                case Msg.MW_GUILDMEMBERLIST_REQ: OnMW_GUILDMEMBERLIST_REQ(r); break;
                case Msg.MW_GUILDINFO_REQ: await OnMW_GUILDINFO_REQ(r); break;
                case Msg.MW_FRIENDLIST_REQ: RelayToClient(r, Msg.CS_FRIENDLIST_ACK); break;               // MapService.Social.cs
                case Msg.MW_FRIENDCONNECTION_REQ: RelayToClient(r, Msg.CS_FRIENDCONNECTION_ACK); break;
                case Msg.MW_FRIENDREGION_REQ: RelayToClient(r, Msg.CS_FRIENDREGION_ACK); break;
                case Msg.MW_FRIENDADD_REQ: RelayToClient(r, Msg.CS_FRIENDADD_ACK); break;
                case Msg.MW_FRIENDASK_REQ: OnMW_FRIENDASK_REQ(r); break;
                case Msg.MW_FRIENDERASE_REQ: RelayToClient(r, Msg.CS_FRIENDERASE_ACK); break;
                case Msg.MW_FRIENDGROUPMAKE_REQ: RelayToClient(r, Msg.CS_FRIENDGROUPMAKE_ACK); break;
                case Msg.MW_FRIENDGROUPDELETE_REQ: RelayToClient(r, Msg.CS_FRIENDGROUPDELETE_ACK); break;
                case Msg.MW_FRIENDGROUPCHANGE_REQ: RelayToClient(r, Msg.CS_FRIENDGROUPCHANGE_ACK); break;
                case Msg.MW_FRIENDGROUPNAME_REQ: RelayToClient(r, Msg.CS_FRIENDGROUPNAME_ACK); break;
                case Msg.MW_TMSRECV_REQ: RelayToClient(r, Msg.CS_TMSRECV_ACK); break;
                case Msg.MW_TMSINVITE_REQ: RelayToClient(r, Msg.CS_TMSINVITE_ACK); break;
                case Msg.MW_TMSOUT_REQ: RelayToClient(r, Msg.CS_TMSOUT_ACK); break;
                case Msg.MW_TMSINVITEASK_REQ: OnMW_TMSINVITEASK_REQ(r); break;
                case Msg.MW_CHGCORPSCOMMANDER_REQ: RelayToClient(r, Msg.CS_CHGCORPSCOMMANDER_ACK); break;     // MapService.Corps.cs
                case Msg.MW_CORPSASK_REQ: OnMW_CORPSASK_REQ(r); break;
                case Msg.MW_CORPSREPLY_REQ: RelayToClient(r, Msg.CS_CORPSREPLY_ACK); break;
                case Msg.MW_CORPSJOIN_REQ: OnMW_CORPSJOIN_REQ(r); break;
                case Msg.MW_ADDSQUAD_REQ: RelayToClient(r, Msg.CS_ADDSQUAD_ACK); break;
                case Msg.MW_DELSQUAD_REQ: RelayToClient(r, Msg.CS_DELSQUAD_ACK); break;
                case Msg.MW_ADDCORPSUNIT_REQ: RelayToClient(r, Msg.CS_ADDCORPSUNIT_ACK); break;
                case Msg.MW_DELCORPSUNIT_REQ: RelayToClient(r, Msg.CS_DELCORPSUNIT_ACK); break;
                case Msg.MW_CORPSCMD_REQ: RelayToClient(r, Msg.CS_CORPSCMD_ACK); break;
                case Msg.MW_CORPSENEMYLIST_REQ: RelayToClient(r, Msg.CS_CORPSENEMYLIST_ACK); break;
                case Msg.MW_MOVECORPSENEMY_REQ: RelayToClient(r, Msg.CS_MOVECORPSENEMY_ACK); break;
                case Msg.MW_MOVECORPSUNIT_REQ: RelayToClient(r, Msg.CS_MOVECORPSUNIT_ACK); break;
                case Msg.MW_ADDCORPSENEMY_REQ: RelayToClient(r, Msg.CS_ADDCORPSENEMY_ACK); break;
                case Msg.MW_DELCORPSENEMY_REQ: RelayToClient(r, Msg.CS_DELCORPSENEMY_ACK); break;
                case Msg.MW_CORPSHP_REQ: RelayToClient(r, Msg.CS_CORPSHP_ACK); break;
                case Msg.MW_CHGSQUADCHIEF_REQ: RelayToClient(r, Msg.CS_CHGSQUADCHIEF_ACK); break;
                case Msg.MW_REPORTENEMYLIST_REQ: RelayToClient(r, Msg.CS_REPORTENEMYLIST_ACK); break;
                case Msg.MW_PARTYMEMBERRECALLANS_REQ: OnMW_PARTYMEMBERRECALLANS_REQ(r); break;
                case Msg.MW_PARTYMEMBERRECALL_REQ: OnMW_PARTYMEMBERRECALL_REQ(r); break;
                case Msg.CT_USERMOVE_ACK: OnCT_USERMOVE_ACK(r); break;
                case Msg.MW_TOURNAMENT_REQ: OnMW_TOURNAMENT_REQ(r); break;                                  // MapService.Tournament.cs
                case Msg.MW_TOURNAMENTINFO_REQ: OnMW_TOURNAMENTINFO_REQ(r); break;
                case Msg.MW_TOURNAMENTMATCH_REQ: OnMW_TOURNAMENTMATCH_REQ(r); break;
                case Msg.MW_TOURNAMENTENABLE_REQ: OnMW_TOURNAMENTENABLE_REQ(r); break;
                case Msg.MW_TOURNAMENTBATPOINT_REQ: OnMW_TOURNAMENTBATPOINT_REQ(r); break;
                case Msg.MW_TOURNAMENTRESULT_REQ: OnMW_TOURNAMENTRESULT_REQ(r); break;                      // MapService.TournamentMatch.cs
                case Msg.MW_WARCOUNTRYBALANCE_REQ: OnMW_WARCOUNTRYBALANCE_REQ(r); break;                    // MapService.CountryBalance.cs
                case Msg.MW_RECALCCOUNTRYBALANCE_ACK: OnMW_RECALCCOUNTRYBALANCE_ACK(r); break;
                case Msg.MW_EVENTUPDATE_REQ: OnMW_EVENTUPDATE_REQ(r); break;                                // MapService.Events.cs
                case Msg.MW_EVENTMSG_REQ: OnMW_EVENTMSG_REQ(r); break;
                case Msg.MW_EVENTQUARTER_REQ: OnMW_EVENTQUARTER_REQ(r); break;
                case Msg.MW_RPSGAME_REQ: OnMW_RPSGAME_REQ(r); break;                                        // MapService.Rps.cs
                case Msg.MW_RPSGAMECHANGE_REQ: OnMW_RPSGAMECHANGE_REQ(r); break;
                case Msg.MW_EVENTMSGLOTTERY_REQ: OnMW_EVENTMSGLOTTERY_REQ(r); break;                        // MapService.SoulLottery.cs
                case Msg.MW_SOULMATE_REQ: OnMW_SOULMATE_REQ(r); break;                                    // MapService.Soulmate.cs
                case Msg.MW_SOULMATESEARCH_REQ: OnMW_SOULMATESEARCH_REQ(r); break;
                case Msg.MW_SOULMATEREG_REQ: OnMW_SOULMATEREG_REQ(r); break;
                case Msg.MW_SOULMATEEND_REQ: OnMW_SOULMATEEND_REQ(r); break;
                case Msg.MW_GUILDSKILLACTION_ACK: await OnMW_GUILDSKILLACTION_ACK(r); break;              // MapService.GuildSkill.cs
                case Msg.MW_UPDATEGUILDCOOLDOWN_REQ: await OnMW_UPDATEGUILDCOOLDOWN_REQ(r); break;
                case Msg.MW_ADDCOOLDOWN_REQ: OnMW_ADDCOOLDOWN_REQ(r); break;
                case Msg.MW_CASTLEOCCUPY_REQ: OnMW_CASTLEOCCUPY_REQ(r); break;
                case Msg.MW_ENDWAR_REQ: OnMW_ENDWAR_REQ(r); break;
                case Msg.MW_MISSIONENABLE_REQ: OnMW_MISSIONENABLE_REQ(r); break;
                case Msg.MW_SKYGARDENENABLE_REQ: OnMW_SKYGARDENENABLE_REQ(r); break;
                case Msg.MW_CASTLEWARINFO_REQ: OnMW_CASTLEWARINFO_REQ(r); break;
                case Msg.MW_CASTLEGUILDCHG_REQ: OnMW_CASTLEGUILDCHG_REQ(r); break;
                case Msg.MW_CASTLEAPPLICANTCOUNT_REQ: OnMW_CASTLEAPPLICANTCOUNT_REQ(r); break;
                case Msg.MW_HEROSELECT_REQ: OnMW_HEROSELECT_REQ(r); break;
                case Msg.MW_MISSIONOCCUPY_REQ: OnMW_MISSIONOCCUPY_REQ(r); break;
                case Msg.MW_SKYGARDENOCCUPY_REQ: OnMW_SKYGARDENOCCUPY_REQ(r); break;
                case Msg.MW_LOCALOCCUPY_REQ: OnMW_LOCALOCCUPY_REQ(r); break;
                case Msg.MW_GAINPVPPOINT_REQ: OnMW_GAINPVPPOINT_REQ(r); break;
                case Msg.MW_BATTLEMODESTATUS_ACK: OnMW_BATTLEMODESTATUS_ACK(r); break;
                case Msg.MW_MEETINGROOM_REQ: OnMW_MEETINGROOM_REQ(r); break;
                case Msg.MW_CASHSHOPSTOP_REQ: OnMW_CASHSHOPSTOP_REQ(r); break;                    // MapService.CashShop.cs
                case Msg.MW_CMGIFT_REQ: await OnMW_CMGIFT_REQ(r); break;                         // MapService.Gm.cs
                case Msg.MW_CMGIFTRESULT_REQ: OnMW_CMGIFTRESULT_REQ(r); break;
                case Msg.MW_CHATBAN_REQ: OnMW_CHATBAN_REQ(r); break;
                case Msg.MW_USERPOSITION_REQ: OnMW_USERPOSITION_REQ(r); break;
                case Msg.MW_ITEMSTATE_REQ: OnMW_ITEMSTATE_REQ(r); break;
                case Msg.MW_HELPMESSAGE_REQ: OnMW_HELPMESSAGE_REQ(r); break;
                case Msg.MW_CHARMSG_REQ: OnMW_CHARMSG_REQ(r); break;
                case Msg.MW_CASHITEMSALE_REQ: OnMW_CASHITEMSALE_REQ(r); break;
                case Msg.MW_BOWTIMEUPDATE_ACK: OnMW_BattleClock(Msg.CS_BOWTIMEUPDATE_ACK, r, 7); break;   // MapService.BattleQueue.cs
                case Msg.MW_BRTIMEUPDATE_ACK: OnMW_BattleClock(Msg.CS_BRTIMEUPDATE_ACK, r, 6); break;
                case Msg.MW_ADDTOBOWQUEUE_ACK: OnMW_QueueResult(Msg.CS_REGISTERBOW_ACK, r); break;
                case Msg.MW_CANCELBOWQUEUE_ACK: OnMW_QueueResult(Msg.CS_CANCELBOWQUEUE_ACK, r); break;
                case Msg.MW_ADDTOBRQUEUE_ACK: OnMW_QueueResult(Msg.CS_ADDTOBRQUEUE_ACK, r); break;
                case Msg.MW_NOTIFYNONQUEUEDPLAYER_ACK: OnMW_NOTIFYNONQUEUEDPLAYER_ACK(r); break;
                case Msg.MW_BRTEAMMATEADD_ACK: OnMW_BRTEAMMATEADD_ACK(r); break;
                case Msg.MW_UPDATEBRTEAM_ACK: OnMW_UPDATEBRTEAM_ACK(r); break;
                case Msg.MW_PREPAREFORBOW_REQ: OnMW_PrepareForBattle(r, br: false); break;
                case Msg.MW_PREPAREFORBR_REQ: OnMW_PrepareForBattle(r, br: true); break;
                case Msg.MW_WORLDPOSTSEND_REQ: await OnMW_WORLDPOSTSEND_REQ(r); break;
                case Msg.MW_LEVELUP_REQ: OnMW_LEVELUP_REQ(r); break;
                case Msg.MW_CHARSTATINFO_REQ: OnMW_CHARSTATINFO_REQ(r); break;
                case Msg.MW_CHARSTATINFOANS_REQ: OnMW_CHARSTATINFOANS_REQ(r); break;
                case Msg.MW_ROUTE_REQ: OnMW_ROUTE_REQ(r); break;
                case Msg.MW_ENTERCHAR_REQ: await OnMW_ENTERCHAR_REQ(r); break;
                case Msg.MW_MONTHRANKLIST_REQ: OnMW_MONTHRANKLIST_REQ(r); break;
                case Msg.MW_MONTHRANKUPDATE_REQ: OnMW_MONTHRANKUPDATE_REQ(r); break;
                case Msg.MW_MONTHRANKRESET_REQ: OnMW_MONTHRANKRESET_REQ(r); break;
                case Msg.MW_FIRSTGRADEGROUP_REQ: OnMW_FIRSTGRADEGROUP_REQ(r); break;
                case Msg.MW_MONTHRANKRESETCHAR_REQ: OnMW_MONTHRANKRESETCHAR_REQ(r); break;
                case Msg.MW_FAMERANKUPDATE_REQ: OnMW_FAMERANKUPDATE_REQ(r); break;
                case Msg.MW_WARLORDSAY_REQ: OnMW_WARLORDSAY_REQ(r); break;
                case Msg.MW_CHANGECHARBASE_REQ: OnMW_CHANGECHARBASE_REQ(r); break;
                case Msg.MW_CHECKMAIN_REQ: OnMW_CHECKMAIN_REQ(r); break;
                case Msg.MW_CONRESULT_REQ: OnMW_CONRESULT_REQ(r); break;
                case Msg.MW_CHAT_REQ: OnMW_CHAT_REQ(r); break;
                case Msg.MW_PARTYADD_REQ: OnMW_PARTYADD_REQ(r); break;
                case Msg.MW_POSTRECV_REQ: OnMW_POSTRECV_REQ(r); break;
                case Msg.MW_STARTTELEPORT_REQ: OnMW_STARTTELEPORT_REQ(r); break;
                case Msg.MW_TELEPORT_REQ: OnMW_TELEPORT_REQ(r); break;
                case Msg.MW_CONLIST_REQ: OnMW_CONLIST_REQ(r); break;
                case Msg.MW_PARTYJOIN_REQ: OnMW_PARTYJOIN_REQ(r); break;
                case Msg.MW_PARTYDEL_REQ: OnMW_PARTYDEL_REQ(r); break;
                case Msg.MW_PARTYATTR_REQ: OnMW_PARTYATTR_REQ(r); break;
                case Msg.MW_CREATERECALLMON_REQ: OnMW_CREATERECALLMON_REQ(r); break;
                case Msg.MW_RECALLMONDEL_REQ: OnMW_RECALLMONDEL_REQ(r); break;
                case Msg.MW_PETRIDING_REQ: OnMW_PETRIDING_REQ(r); break;
                case Msg.MW_CREATESPOLECNIKMON_REQ: OnMW_CREATESPOLECNIKMON_REQ(r); break;
                case Msg.MW_SPOLECNIKMONDEL_REQ: OnMW_SPOLECNIKMONDEL_REQ(r); break;
                case Msg.MW_RECALLMONDATA_REQ: break;   // cross-server summon handoff — never sent to a single map server
                case Msg.MW_CHGPARTYCHIEF_REQ: OnMW_CHGPARTYCHIEF_REQ(r); break;
                case Msg.MW_CHGPARTYTYPE_REQ: OnMW_CHGPARTYTYPE_REQ(r); break;
                case Msg.MW_PARTYMANSTAT_REQ: OnMW_PARTYMANSTAT_REQ(r); break;
                case Msg.MW_PARTYMOVE_REQ: OnMW_PARTYMOVE_REQ(r); break;
                case Msg.MW_TERMINATE_REQ: OnMW_TERMINATE_REQ(r); break;
                case Msg.MW_CLOSECHAR_REQ: OnMW_CLOSECHAR_REQ(r); break;
                case Msg.SM_TIMER_REQ: break; // world-driven tick; local timer already ticks
                case Msg.SM_DELSESSION_REQ: break;
                case Msg.SM_QUITSERVICE_REQ: break;
                default:
                    _log.LogDebug("Unhandled world message 0x{Id:X4}.", r.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "World handler 0x{Id:X4} failed.", r.Id);
        }
    }

    // ---- world link lifecycle ----

    public ValueTask OnWorldConnectedAsync()
    {
        _worldReady = true;
        SendMW_CONNECT_ACK();
        _log.LogInformation("Announced to world: server {Server}, channels [{Channels}].",
            _opt.ServerId, string.Join(",", _opt.Channels));
        return ValueTask.CompletedTask;
    }

    public void OnWorldDisconnect()
    {
        _worldReady = false;
        _log.LogWarning("World link down; clients will re-handshake on reconnect.");
    }

    public async Task OnTimerAsync()
    {
        _tickSeconds++;
        NowMs = unchecked((uint)(_tickSeconds * 1000L)); // advance the map ms clock (skill cooldowns)
        RunDbResults();                        // the database's answers, applied here (MapService.Fort.cs)
        RunGodBallCmds();                      // the castle wars' god ball commands and power race (MapService.Castle.cs)
        RunMonsterRegen(_tickSeconds * 1000L); // monster spawn/respawn regen (map clock in ms)
        RunMaintainSkills(NowMs);              // expire ended buffs/debuffs (C++ CheckMaintainSkill, before Recover)
        RunSwitchReverts(NowMs);               // auto-revert duration-limited switches (C++ m_vTSWITCHOBJ sweep)
        RunRecover(NowMs);                     // HP/MP regeneration (players + monsters)
        RunAftermath(NowMs);                   // one step of death-penalty recovery when due (CTPlayer::OnTimer)
        RunRecallTimers();                     // summons whose life ran out are sent away (CheckTimeRecallMon)
        RunSelfObjTimers();                    // and placed objects whose time is up die
        RunCompanionTimers(NowMs);             // companion stamina/exp each minute, expired companion items
        RunDuringItems();                      // premium / exp boost countdown and the daily bonus time
        RunCorpseExpiry(_tickSeconds * 1000L); // despawn + re-arm lootable corpses past their lifetime
        RunScheduledAi(_tickSeconds * 1000L);  // Due TAICHART commands (the local SM_AICMD stand-in)
        RunPendingResetHome();                 // abandoned monsters back to their spawn (SM_RESETHOST_ACK)
        RunMonsterAI(_tickSeconds * 1000L);    // legacy sweep — script-less monsters only (roam / chase)
        RunPeriodicSaves(NowMs);               // 30-min per-char DB save (no-op DB-free); off-thread write
        FlushItemDirect();                     // incremental item persistence (TSaveItemDirect); off-thread write
        await RunPostBills();                  // unpaid bills past 3 days go back to their sender
        await RunAuctionEnds();                // auctions whose time is up (MapService.Auction.cs)
        ArenaTick();                           // the arenas' ready and battle clocks (MapService.Arena.cs)
        TournamentTick();                      // the tournament's countdowns and drawn matches (MapService.TournamentMatch.cs)
        HelpMessageTick();                     // the event help message on and off (MapService.Gm.cs)
        MeetingRoomTick();
        RunDuels();                            // duel standby → start → timeout → clear (the SM_DUEL* timer)
        // Still deferred here: war timers — see PORT_STATUS.md.
    }
}
