using TWorld.Protocol;

namespace TWorld.Server.World;

/// <summary>
/// All live world state. Mirrors the C++ module's in-memory maps. <b>Accessed only from the single
/// batch task</b> (the C++ used one big batch lock), so plain dictionaries are safe here — there is no
/// concurrent access. Phase 1 holds servers + characters; guild/party/corps/ranking maps arrive later.
/// </summary>
public sealed class WorldState
{
    public Dictionary<ushort, ServerSession> Servers { get; } = new();   // wID -> peer
    public Dictionary<uint, Character> Characters { get; } = new();      // charId -> character
    public HashSet<uint> Operators { get; } = new();
    public Dictionary<string, Character> CharactersByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<uint> ActiveUsers { get; } = new();

    public ServerSession? ControlServer { get; set; }

    public byte Nation { get; set; }

    // --- Phase 2: guilds (DB-backed) + parties/corps (in-memory) ---
    public Dictionary<byte, GuildLevel> GuildLevels { get; } = new();
    public Dictionary<uint, Guild> Guilds { get; } = new();         // guildId -> guild
    public Dictionary<uint, uint> CharGuild { get; } = new();       // charId -> guildId (membership index)
    public Dictionary<ushort, Party> Parties { get; } = new();      // partyId -> party
    public Dictionary<ushort, Corps> CorpsMap { get; } = new();     // corpsId -> corps
    public PartyIdPool PartyIds { get; } = new();

    public Guild? FindGuild(uint guildId) => Guilds.TryGetValue(guildId, out var g) ? g : null;
    public Guild? FindGuildByChar(uint charId)
        => CharGuild.TryGetValue(charId, out var gid) ? FindGuild(gid) : null;
    public Party? FindParty(ushort partyId) => Parties.TryGetValue(partyId, out var p) ? p : null;
    public Corps? FindCorps(ushort corpsId) => CorpsMap.TryGetValue(corpsId, out var c) ? c : null;

    // --- Phase 2b: recruitment boards + tactics membership index ---
    public Dictionary<uint, GuildWanted> GuildWanted { get; } = new();           // guildId -> wanted ad
    public Dictionary<uint, GuildTacticsWanted> TacticsWanted { get; } = new();  // wantedId -> tactics ad
    public uint TacticsWantedSeq { get; set; }
    public Dictionary<uint, uint> CharTactics { get; } = new();                  // charId -> guildId (tactics membership)

    /// <summary>The guild a char belongs to as a tactics (mercenary) member, if any.</summary>
    public Guild? FindTacticsGuild(uint charId)
        => CharTactics.TryGetValue(charId, out var gid) ? FindGuild(gid) : null;

    /// <summary>Current guild for display: tactics guild takes priority over the regular guild (GetCurGuild).</summary>
    public Guild? GetCurGuild(uint charId) => FindTacticsGuild(charId) ?? FindGuildByChar(charId);

    public GuildLevel? GuildLevelOf(byte level) => GuildLevels.TryGetValue(level, out var l) ? l : null;

    // --- Phase 3: PvP rankings + monthly rollover ---

    /// <summary>m_arMonthRank[country][rank]: index 0 is the country warlord, 1..N the monthly ladder.</summary>
    public MonthRanker[][] MonthRank { get; } = NewGrid(Proto.CountryCount, Proto.MonthRankCount);
    /// <summary>m_arFirstGradeGroup[country][rank]: last month's frozen first-grade ladder.</summary>
    public MonthRanker[][] FirstGradeGroup { get; } = NewGrid(Proto.CountryCount, Proto.FirstGradeGroupCount);
    /// <summary>m_arLastFameRank: the cross-country fame podium snapshot.</summary>
    public MonthRanker[] LastFameRank { get; } = NewRow(Proto.FameRankCount);

    /// <summary>Calendar month (1-12) the loaded rank tables belong to (m_bRankMonth).</summary>
    public byte RankMonth { get; set; }
    /// <summary>Set while a monthly rollover save is in flight (m_bFameRankSave).</summary>
    public bool FameRankSave { get; set; }

    // --- Phase 4: Battle of the Warlords / Battle Royale (null until the config row loads at startup). ---
    public BowState? Bow { get; set; }
    public BrState? Br { get; set; }

    // --- Phase 4c: scheduled battle-field windows (local / castle / mission / sky-garden). ---
    public BattleSchedule? Battles { get; set; }

    // --- Phase 5d: castle-war scoreboard (castleId -> aggregated occupation/def/atk). ---
    public Dictionary<ushort, CastleWarInfo> CastleWarInfo { get; } = new();

    /// <summary>Day number (unix/86400) of the most recent war-end record recalc (m_dwRecentRecordDate).</summary>
    public uint RecentRecordDate { get; set; }

    // --- Phase 5i: TMS multi-person private chat (m_mapTMS + m_dwTMSIndex). ---
    public Dictionary<uint, Tms> TmsMap { get; } = new();
    public uint TmsIndex { get; set; }

    // --- Phase 5f: nation balance — online char ids bucketed by [country][level-gap] (m_mapWarCountry). ---
    public HashSet<uint>[][] WarCountry { get; } =
        Enumerable.Range(0, Proto.CountryCount)
            .Select(_ => Enumerable.Range(0, Proto.WarCountryMaxGap).Select(_ => new HashSet<uint>()).ToArray())
            .ToArray();

    // --- Phase 4d: tournament config + announce state (null until config loads at startup). ---
    public TournamentState? Tournament { get; set; }

    /// <summary>GM event-tournament config (m_mapTournament): tournament-event id → its entries by entryId.
    /// Fed by the control server's CT_TOURNAMENTEVENT TET_ENTRYADD.</summary>
    public Dictionary<ushort, Dictionary<byte, TournamentEntry>> EventTournaments { get; } = new();

    /// <summary>GM event-tournament schedules (m_mapTournamentSchedule + m_mapTournamentTime): id → schedule.
    /// The earliest-starting becomes the running tournament.</summary>
    public Dictionary<ushort, EventTournamentSchedule> EventSchedules { get; } = new();

    /// <summary>Allocator for event-tournament schedule ids (m_wTournamentID); GM event ids start above the
    /// reserved base id 1.</summary>
    public ushort EventTournamentIdSeq { get; set; } = 1;

    // --- Final MW slice: minigame / cash-mall / summon state. ---
    /// <summary>RPS chart keyed by MAKEWORD(type, winCount) (m_mapRPSGame). Seeded from TRPSGAMECHART at startup.</summary>
    public Dictionary<ushort, RpsGame> RpsGames { get; } = new();

    /// <summary>Localized system-message text keyed by id (m_mapTSvrMsg, from TSVRMSGCHART).</summary>
    public Dictionary<uint, string> ServerMessages { get; } = new();

    /// <summary>System-message text for an id, or "" if absent. CTWorldSvrModule::GetSvrMsg.</summary>
    public string GetSvrMsg(uint id) => ServerMessages.TryGetValue(id, out var s) ? s : "";

    /// <summary>Cash-mall gift catalog keyed by gift id (m_mapCMGift). Fed by the control server via
    /// CT_CMGIFTCHARTUPDATE; loaded from TCMGIFTCHART at startup.</summary>
    public Dictionary<ushort, CmGift> CmGifts { get; } = new();

    /// <summary>Fallback gift-id allocator used only when no DB is configured (tests); live, the DB assigns
    /// ids via CSPCMGiftAdd.</summary>
    public ushort CmGiftSeq { get; set; }

    /// <summary>Active cash-item sale events keyed by index (m_mapTCashItemSale), pushed by CT_CASHITEMSALE.</summary>
    public Dictionary<uint, CashItemSaleEvent> CashItemSales { get; } = new();

    /// <summary>Monotonic recall/companion-monster id allocator (m_dwGenRecallID, seeded from CSPGetRecallID).</summary>
    public uint GenRecallId { get; set; }

    /// <summary>The next recall-monster id (++m_dwGenRecallID). CTWorldSvrModule::GenRecallID.</summary>
    public uint NextRecallId() => ++GenRecallId;

    // --- Event subsystem (m_mapEVENT / m_mapEVQT / m_vExpired). Fed by the control server (CT_EVENT*). ---
    /// <summary>Active server events keyed by index (m_mapEVENT).</summary>
    public Dictionary<uint, EventInfo> EventInfos { get; } = new();

    /// <summary>The map servers counted in the kingdom balance (<see cref="WorldServerOptions.CountryBalanceServers"/>); null = all.</summary>
    public HashSet<byte>? CountryBalanceServers { get; set; }

    /// <summary>The minute the kingdom balance was last sent (C++ <c>m_bReservedPostMinute</c>'s per-minute block).</summary>
    public int BalanceMinute { get; set; } = -1;
    /// <summary>Scheduled lucky-event quarters keyed by id (m_mapEVQT); each carries its next-fire time.</summary>
    public Dictionary<ushort, EventQuarter> EventQuarters { get; } = new();
    /// <summary>Timed-expiry queue, kept sorted ascending by fire time (m_vExpired).</summary>
    public List<ExpiredBuf> Expired { get; } = new();

    /// <summary>Active chat bans (name → ban-until unix seconds), set by the control server's CT_CHATBAN.
    /// Mirrors AddChatBan; consulted when re-applying a ban on a banned char's re-entry (hook-up deferred).</summary>
    public Dictionary<string, long> ChatBans { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static MonthRanker[] NewRow(int n)
    {
        var a = new MonthRanker[n];
        for (int i = 0; i < n; i++) a[i] = new MonthRanker();
        return a;
    }

    private static MonthRanker[][] NewGrid(int rows, int cols)
    {
        var g = new MonthRanker[rows][];
        for (int i = 0; i < rows; i++) g[i] = NewRow(cols);
        return g;
    }

    /// <summary>Find a character by id, validating the session key (mirrors C++ <c>FindTChar</c>).</summary>
    public Character? FindChar(uint charId, uint key)
        => Characters.TryGetValue(charId, out var c) && c.Key == key ? c : null;

    /// <summary>Find a connected map server by its LOBYTE server id (mirrors C++ <c>FindMapSvr</c>).</summary>
    public ServerSession? FindMapSvr(byte serverId)
    {
        foreach (var s in Servers.Values)
            if (s.ServerId == serverId) return s;
        return null;
    }
}
