using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TWorld.Data;
using TWorld.Protocol;
using TWorld.Server.Net;
using TWorld.Server.World;

namespace TWorld.Server;

/// <summary>One unit of work for the batch task: a framed packet, a disconnect (Conn set, Packet null),
/// or a 1-second timer tick (Tick true) — all serialized on the single batch task.</summary>
internal readonly record struct BatchItem(PacketConnection? Conn, byte[]? Packet, bool Tick = false);

/// <summary>
/// Hosted service that boots the world server: validates config / loads nation, then runs three
/// cooperating loops — the TCP acceptor (<see cref="PacketServer"/>), the single <b>batch task</b> that
/// serializes all game logic (mirroring the C++ batch thread + lock), and a 1-second timer loop
/// (scaffolded; subsystem ticks land in later phases).
/// </summary>
public sealed class WorldWorker : BackgroundService
{
    private readonly WorldServerOptions _opt;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WorldWorker> _log;

    public WorldWorker(IOptions<WorldServerOptions> opt, ILoggerFactory loggerFactory)
    {
        _opt = opt.Value;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<WorldWorker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var peerTls = new Security.TlsPeerAuthenticator(_opt.PeerTls);
        var state = new WorldState();
        if (_opt.CountryBalanceServers is { Length: > 0 } balance) state.CountryBalanceServers = new HashSet<byte>(balance);

        // Phase 1 DB use is limited to the server nation + a connectivity check; the world coordinates
        // in-memory and does not require the DB to accept peers.
        byte nation = _opt.Nation ?? 0;
        GlobalDatabase? globalDb = null;
        if (!string.IsNullOrWhiteSpace(_opt.Db.GlobalConnectionString))
        {
            try
            {
                globalDb = new GlobalDatabase(_opt.Db.GlobalConnectionString);
                await globalDb.PingAsync(stoppingToken);
                if (_opt.Nation is null) nation = await globalDb.GetNationAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Global DB unavailable at startup; continuing with nation={Nation}.", nation);
            }
        }
        GuildDatabase? guildDb = null;
        SocialDatabase? socialDb = null;
        RankDatabase? rankDb = null;
        BowDatabase? bowDb = null;
        BrDatabase? brDb = null;
        GameDatabase? gameDb = null;
        if (!string.IsNullOrWhiteSpace(_opt.Db.GameConnectionString))
        {
            gameDb = new GameDatabase(_opt.Db.GameConnectionString);
            try
            {
                await gameDb.PingAsync(stoppingToken);
                state.GenRecallId = await gameDb.GetRecallIdAsync(stoppingToken);
                await LoadServerMessagesAsync(gameDb, state, stoppingToken);
                await LoadRpsAsync(gameDb, state, stoppingToken);
                await LoadCmGiftsAsync(gameDb, state, stoppingToken);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Game DB unavailable at startup."); }

            guildDb = new GuildDatabase(_opt.Db.GameConnectionString);
            socialDb = new SocialDatabase(_opt.Db.GameConnectionString);
            rankDb = new RankDatabase(_opt.Db.GameConnectionString);
            bowDb = new BowDatabase(_opt.Db.GameConnectionString);
            brDb = new BrDatabase(_opt.Db.GameConnectionString);
            try { await LoadBattleTimesAsync(new BattleDatabase(_opt.Db.GameConnectionString), bowDb, state, stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "Battle-time load failed; scheduled battles disabled."); }
            try { await LoadGuildsAsync(guildDb, state, stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "Guild load failed; continuing with no guilds."); }
            try { await LoadRanksAsync(rankDb, state, stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "Rank load failed; continuing with empty ranks."); }
            try { await LoadBowAsync(bowDb, state, stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "BoW config load failed; BoW disabled."); }
            try { await LoadBrAsync(bowDb, brDb, state, stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "BR config load failed; BR disabled."); }
            try { await LoadTournamentAsync(new TournamentDatabase(_opt.Db.GameConnectionString), state, stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "Tournament config load failed; tournament disabled."); }
        }
        // m_bRankMonth tracks the CURRENT calendar month; the rollover fires only when the month advances
        // past it (the first-grade-group LOAD, by contrast, reads last month — see LoadRanksAsync).
        if (state.RankMonth == 0) state.RankMonth = (byte)DateTime.UtcNow.Month;
        state.Nation = nation;

        var service = new WorldService(state, guildDb, _loggerFactory.CreateLogger<WorldService>(), socialDb, rankDb, bowDb, brDb, gameDb, globalDb);
        // DM_ACTIVECHARUPDATE: seed nation-balance buckets from the active-char table (best-effort; no-op when
        // the table is empty/absent so the live buckets are never wiped).
        if (gameDb is not null)
        {
            try { await service.RefreshActiveCharBucketsAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Active-char nation refresh failed."); }
        }
        _log.LogInformation("World group={Grp} server={Sid} nation={Nation} guilds={G} guildLevels={L} rankMonth={RM} bow={Bow} br={Br} battles={Bt} tnmt={Tn} recallId={Rid}",
            _opt.GroupId, _opt.ServerId, nation, state.Guilds.Count, state.GuildLevels.Count, state.RankMonth, state.Bow is not null, state.Br is not null, state.Battles is not null, state.Tournament is not null, state.GenRecallId);

        // Single batch task: all packet handling + state mutation happens here, in order.
        var batch = Channel.CreateUnbounded<BatchItem>(new UnboundedChannelOptions { SingleReader = true });
        var batchTask = RunBatchAsync(batch.Reader, service, stoppingToken);
        var timerTask = RunTimerAsync(batch.Writer, stoppingToken);

        var server = new PacketServer(_loggerFactory.CreateLogger<PacketServer>(), peerTls);
        await server.ListenAsync(
            _opt.Port,
            onPacket: (conn, data) => { batch.Writer.TryWrite(new BatchItem(conn, data)); return ValueTask.CompletedTask; },
            onConnected: service.OnConnectedAsync,
            onDisconnected: conn => { batch.Writer.TryWrite(new BatchItem(conn, null)); return ValueTask.CompletedTask; },
            token: stoppingToken);

        batch.Writer.TryComplete();
        try { await Task.WhenAll(batchTask, timerTask); } catch (OperationCanceledException) { }
    }

    private async Task RunBatchAsync(ChannelReader<BatchItem> reader, WorldService service, CancellationToken token)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(token))
            {
                if (item.Tick) await service.OnTimerAsync();
                else if (item.Conn is null) { /* nothing */ }
                else if (item.Packet is null) service.OnDisconnect(item.Conn);
                else await service.DispatchAsync(item.Conn, item.Packet);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadGuildsAsync(GuildDatabase db, WorldState state, CancellationToken ct)
    {
        foreach (var lv in await db.LoadGuildChartAsync(ct))
        {
            var gl = new GuildLevel
            {
                Level = lv.Level, Exp = lv.Exp, MaxCnt = lv.MaxCnt, MinCnt = lv.MinCnt,
                CabinetCnt = lv.CabinetCnt, TacticsCnt = lv.TacticsCnt, BattleSetCnt = lv.BattleSetCnt,
                GuardCnt = lv.GuardCnt, RoyalGuardCnt = lv.RoyalGuardCnt, TurretCnt = lv.TurretCnt,
            };
            gl.Peer[0] = lv.Peer1; gl.Peer[1] = lv.Peer2; gl.Peer[2] = lv.Peer3; gl.Peer[3] = lv.Peer4; gl.Peer[4] = lv.Peer5;
            state.GuildLevels[lv.Level] = gl;
        }

        foreach (var g in await db.LoadGuildsAsync(ct))
        {
            var guild = new Guild
            {
                Id = g.Id, Name = g.Name, Chief = g.Chief, Level = g.Level, Fame = g.Fame, FameColor = g.FameColor,
                MaxCabinet = g.MaxCabinet, GI = g.GI, Exp = g.Exp, GPoint = g.GPoint, Status = g.Status,
                Gold = g.Gold, Silver = g.Silver, Cooper = g.Cooper, Disorg = g.Disorg, Time = g.Time,
                TimeEstablish = g.TimeEstablish, PvPTotalPoint = g.PvPTotalPoint, PvPUseablePoint = g.PvPUseablePoint,
                PvPMonthPoint = g.PvPMonthPoint, LevelChart = state.GuildLevelOf(g.Level),
            };
            state.Guilds[guild.Id] = guild;
        }

        foreach (var m in await db.LoadGuildMembersAsync(ct))
        {
            if (!state.Guilds.TryGetValue(m.GuildId, out var guild)) continue;
            guild.Members[m.CharId] = new GuildMember
            {
                CharId = m.CharId, Name = m.Name, Country = m.Country, WarCountry = m.WarCountry,
                Level = m.Level, Class = m.Class, Duty = m.Duty, Peer = m.Peer, ConnectedDate = m.ConnectedDate,
            };
            state.CharGuild[m.CharId] = m.GuildId;
            if (m.Duty == (byte)GuildDuty.Chief)                                 // C++ LoadGuild: the chief gives the guild its country
                (guild.Chief, guild.ChiefName, guild.Country) = (m.CharId, m.Name, m.Country);
        }

        // ---- Phase 2b sub-loads (each best-effort: a missing table/view is non-fatal) ----
        await TryLoad("articles", async () =>
        {
            foreach (var a in await db.LoadArticlesAsync(ct))
                if (state.Guilds.TryGetValue(a.GuildId, out var g))
                {
                    g.Articles[a.Id] = new GuildArticle { Id = a.Id, Duty = a.Duty, Writer = a.Writer, Title = a.Title, Article = a.Article, Date = WorldService.ArticleDate(a.Time) };
                    if (a.Id > g.ArticleSeq) g.ArticleSeq = a.Id;
                    g.ArticleTitle = a.Title;                                    // C++ CTGuild::AddArticle (load): the last loaded
                }
        });
        await TryLoad("tactics", async () =>
        {
            foreach (var t in await db.LoadTacticsAsync(ct))
                if (state.Guilds.TryGetValue(t.GuildId, out var g))
                {
                    g.Tactics[t.CharId] = new TacticsMember { CharId = t.CharId, Name = t.Name, Level = t.Level, Class = t.Class, RewardPoint = t.RewardPoint, RewardMoney = t.RewardMoney, GainPoint = t.GainPoint, Day = t.Day, EndTime = t.EndTime };
                    state.CharTactics[t.CharId] = t.GuildId;
                    state.Expired.Add(new ExpiredBuf((byte)ExpiredType.GuildTactics, t.EndTime, t.GuildId, t.CharId));   // C++ bInitExpired
                }
            state.Expired.Sort((x, y) => x.TimeExpired.CompareTo(y.TimeExpired));   // the queue is kept by fire time
        });
        await TryLoad("relations", async () =>
        {
            foreach (var rel in await db.LoadRelationsAsync(ct))
            {
                var a = state.FindGuild(rel.GuildOne);
                var b = state.FindGuild(rel.GuildTwo);
                if (a is null || b is null) continue;
                if (rel.Type == (byte)GuildRelation.Alliance) { a.Allies.Add(rel.GuildTwo); b.Allies.Add(rel.GuildOne); }
                else if (rel.Type == (byte)GuildRelation.Enemy) { a.Enemies.Add(rel.GuildTwo); b.Enemies.Add(rel.GuildOne); }
            }
        });
        await TryLoad("point-rewards", async () =>
        {
            foreach (var pr in await db.LoadPointRewardsAsync(ct))
                if (state.Guilds.TryGetValue(pr.GuildId, out var g) && g.PointRewards.Count < 50)
                    g.PointRewards.Add(new GuildPointReward { Name = pr.Name, Point = pr.Point, Date = pr.Date });
        });
        await TryLoad("pvp-records", async () =>
        {
            foreach (var rec in await db.LoadPvpRecordsAsync(ct))
                if (state.Guilds.TryGetValue(rec.GuildId, out var g) && g.FindMember(rec.CharId) is { } mem)
                {
                    var pr = new GuildPvpRecord { CharId = rec.CharId, Date = rec.Date, KillCount = rec.KillCount, DieCount = rec.DieCount };
                    Array.Copy(rec.Point, pr.Point, Math.Min(rec.Point.Length, pr.Point.Length));
                    mem.Records.Add(pr);
                    if (state.RecentRecordDate < rec.Date) state.RecentRecordDate = rec.Date;
                }
            foreach (var g in state.Guilds.Values) g.CalcWeekRecord(state.RecentRecordDate);   // C++ LoadData
        });
        await TryLoad("stats", async () =>
        {
            foreach (var s in await db.LoadStatsAsync(ct))
                if (state.Guilds.TryGetValue(s.GuildId, out var g)) { g.StatLevel = s.Level; g.StatPoint = s.SkillPoint; g.StatExp = s.Exp; }
        });
        await TryLoad("cabinet", async () =>
        {
            foreach (var it in await db.LoadCabinetAsync(ct))
                if (state.Guilds.TryGetValue(it.OwnerId, out var g))
                {
                    var item = new GuildItem { ItemDbId = it.ItemDbId, StorageId = it.StorageId, ItemId = it.ItemId, Level = it.Level, Count = it.Count, GLevel = it.GLevel, DuraMax = it.DuraMax, DuraCur = it.DuraCur, RefineCur = it.RefineCur, EndTime = it.EndTime, GradeEffect = it.GradeEffect,
                        Gem = it.Gem, MoggItemId = it.MoggItemId, ItemSlot = it.ItemSlot };
                    Array.Copy(it.Magic, item.Magic, 6); Array.Copy(it.Value, item.Value, 6); Array.Copy(it.ExtValue, item.ExtValue, 6);
                    g.Cabinet.Add(item);
                }
        });
        await TryLoad("wanted", async () =>
        {
            foreach (var w in await db.LoadWantedAsync(ct))
            {
                var guild = state.FindGuild(w.GuildId);                           // C++ AddGuildWanted: the guild's name and country
                state.GuildWanted[w.GuildId] = new GuildWanted { GuildId = w.GuildId, MinLevel = w.MinLevel, MaxLevel = w.MaxLevel, EndTime = w.EndTime, Title = w.Title, Text = w.Text, Name = guild?.Name ?? "", Country = guild?.Country ?? 0 };
            }
        });
        await TryLoad("tactics-wanted", async () =>
        {
            foreach (var w in await db.LoadTacticsWantedAsync(ct))
            {
                state.TacticsWanted[w.Id] = new GuildTacticsWanted { Id = w.Id, GuildId = w.GuildId, MinLevel = w.MinLevel, MaxLevel = w.MaxLevel, EndTime = w.EndTime, Title = w.Title, Text = w.Text, Day = w.Day, Gold = w.Gold, Silver = w.Silver, Cooper = w.Cooper, Point = w.PvPoint, Name = state.FindGuild(w.GuildId)?.Name ?? "", Country = state.FindGuild(w.GuildId)?.Country ?? 0 };
                if (w.Id > state.TacticsWantedSeq) state.TacticsWantedSeq = w.Id;
            }
        });
        await TryLoad("volunteers", async () =>
        {
            foreach (var v in await db.LoadVolunteersAsync(ct))
            {
                // C++ LoadData: bType GUILDAPP_MEMBER (1) applies to a guild's ad (by guild id), else to a tactics ad (by its id)
                if (v.Type == (byte)GuildRelation.Alliance)
                {
                    if (state.GuildWanted.TryGetValue(v.Id, out var gw))
                        gw.Apps[v.CharId] = new GuildWantedApp { CharId = v.CharId, WantedId = v.Id, Class = v.Class, Level = v.Level, Name = v.Name };
                }
                else if (state.TacticsWanted.TryGetValue(v.Id, out var tw))
                    tw.Apps[v.CharId] = new GuildTacticsWantedApp { CharId = v.CharId, WantedId = v.Id, WantedGuildId = tw.GuildId, Class = v.Class, Level = v.Level, Name = v.Name,
                        Day = tw.Day, Point = tw.Point, Gold = tw.Gold, Silver = tw.Silver, Cooper = tw.Cooper };
            }
        });

        async Task TryLoad(string what, Func<Task> load)
        {
            try { await load(); }
            catch (Exception ex) { _log.LogWarning(ex, "Guild sub-load '{What}' failed (table/view absent?).", what); }
        }
    }

    // 1s tick — enqueues a tick onto the batch task so OnTimer logic (Phase 3: the monthly PvP-rank
    // rollover; later: BoW/BR/castle phase machines) runs single-threaded with the rest of the game logic.
    private async Task RunTimerAsync(ChannelWriter<BatchItem> batch, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                batch.TryWrite(new BatchItem(null, null, Tick: true));
        }
        catch (OperationCanceledException) { }
    }

    private const byte BtBow = 5; // BT_BOW (TWorldType.h BATTLE_TYPE)
    private const byte BtBr = 6;  // BT_BR
    private const byte BtMission = 3; // BT_MISSION

    private async Task LoadBattleTimesAsync(BattleDatabase db, BowDatabase bowDb, WorldState state, CancellationToken ct)
    {
        var rows = await db.LoadBattleTimesAsync(ct);
        if (rows.Count == 0) { _log.LogInformation("No battle-time rows; scheduled battles disabled."); return; }

        var sched = new BattleSchedule();
        foreach (var row in rows)
        {
            if (row.Type >= (byte)BattleType.Count) continue;
            var b = sched.Times[row.Type];
            b.Type = row.Type; b.Status = BattleStatus.Normal;
            b.BattleDur = row.BattleDur; b.BattleStart = row.BattleStart; b.AlarmStart = row.AlarmStart;
            b.AlarmEnd = row.AlarmEnd; b.PeaceDur = row.PeaceDur; b.Day = row.Day; b.Week = row.Week;
        }

        // The C++ hard-codes the local-field peace window to 180s and seeds the mission schedule.
        sched[BattleType.Local].PeaceDur = 180;
        foreach (var t in await bowDb.LoadCustomTimesAsync(ct))
            if (t.Type == BtMission) sched.CustomTimes.Add(t.Time);
        sched.CustomTimes.Sort();

        state.Battles = sched;
        _log.LogInformation("Battle-time schedule loaded: {N} windows, missionTimes={M}.", rows.Count, sched.CustomTimes.Count);
    }

    private async Task LoadBrAsync(BowDatabase bowDb, BrDatabase db, WorldState state, CancellationToken ct)
    {
        var settings = await db.LoadSettingsAsync(ct);
        if (settings is not { } s) { _log.LogInformation("No BR settings row; BR disabled."); return; }

        var br = new BrState(s.AlarmDur, s.BuyTimeDur, s.BattleDur, s.MinPlayerCount);
        foreach (var t in await bowDb.LoadCustomTimesAsync(ct))
            if (t.Type == BtBr) br.Times.Add(t.Time);

        // Init(): sort the schedule, alternate Team/Solo per slot, pick the next start + its type.
        br.Times.Sort();
        for (int i = 0; i < br.Times.Count; i++)
            br.BattleTypes.Add(i % 2 == 0 ? (byte)BrType.Team : (byte)BrType.Solo);
        if (br.Times.Count > 0)
        {
            uint nowTod = (uint)DateTime.UtcNow.TimeOfDay.TotalSeconds;
            uint start = br.Times.FirstOrDefault(t => t > nowTod, br.Times[0]);
            br.Start = start;
            int idx = br.Times.IndexOf(start);
            br.Type = idx >= 0 && idx < br.BattleTypes.Count ? br.BattleTypes[idx] : (byte)BrType.Team;
        }
        state.Br = br;
        _log.LogInformation("BR initialized: times={N} firstStart={Start} type={Type}.", br.Times.Count, br.Start, br.Type);
    }


    /// <summary>Load the system-message text table (CTBLSvrMsg → m_mapTSvrMsg).</summary>
    private async Task LoadServerMessagesAsync(GameDatabase db, WorldState state, CancellationToken ct)
    {
        foreach (var m in await db.LoadServerMessagesAsync(ct)) state.ServerMessages[m.Id] = m.Message;
        _log.LogInformation("Server messages loaded: {Count}.", state.ServerMessages.Count);
    }

    /// <summary>Load the RPS chart + standing win records (CTBLRPSGame / CTBLRPSGameRecord → m_mapRPSGame).</summary>
    private async Task LoadRpsAsync(GameDatabase db, WorldState state, CancellationToken ct)
    {
        foreach (var c in await db.LoadRpsChartAsync(ct))
        {
            var rps = new RpsGame { Type = c.Type, WinCount = c.WinCount, WinKeep = c.WinKeep, WinPeriod = c.WinPeriod };
            rps.Prob[0] = c.ProbWin; rps.Prob[1] = c.ProbDraw; rps.Prob[2] = c.ProbLose;
            state.RpsGames[(ushort)(c.Type | (c.WinCount << 8))] = rps;
        }
        foreach (var rec in await db.LoadRpsRecordsAsync(ct))
            if (state.RpsGames.TryGetValue((ushort)(rec.Type | (rec.WinCount << 8)), out var rps))
                rps.WinDates.Add(rec.WinDate);
        _log.LogInformation("RPS chart loaded: {Count} entries.", state.RpsGames.Count);
    }

    /// <summary>Load the cash-mall gift catalog (CTBLCMGiftChart → m_mapCMGift) and seed the fallback id seq.</summary>
    private async Task LoadCmGiftsAsync(GameDatabase db, WorldState state, CancellationToken ct)
    {
        foreach (var g in await db.LoadCmGiftChartAsync(ct))
        {
            state.CmGifts[g.GiftId] = new CmGift
            {
                GiftId = g.GiftId, GiftType = g.GiftType, Value = g.Value, Count = g.Count, TakeType = g.TakeType,
                MaxTakeCount = g.MaxTakeCount, ToolOnly = g.ToolOnly, ErrGiftId = g.ErrGiftId, Title = g.Title, Msg = g.Msg,
            };
            if (g.GiftId > state.CmGiftSeq) state.CmGiftSeq = g.GiftId;
        }
        _log.LogInformation("Cash-mall gift catalog loaded: {Count} gifts.", state.CmGifts.Count);
    }

    private async Task LoadBowAsync(BowDatabase db, WorldState state, CancellationToken ct)
    {
        var settings = await db.LoadSettingsAsync(ct);
        if (settings is not { } s) { _log.LogInformation("No BoW settings row; BoW disabled."); return; }

        var bow = new BowState(s.MapId, s.MinPlayersCount, s.MaxNationDifference)
        {
            AlarmDur = s.AlarmDur, BuyTimeDur = s.BuyTimeDur, BattleDur = s.BattleDur,
        };
        foreach (var t in await db.LoadCustomTimesAsync(ct))
            if (t.Type == BtBow) bow.Times.Add(t.Time);

        // Init(): sort the schedule and pick the next start time after the current second-of-day.
        bow.Times.Sort();
        if (bow.Times.Count > 0)
        {
            uint nowTod = (uint)DateTime.UtcNow.TimeOfDay.TotalSeconds;
            bow.Start = bow.Times.FirstOrDefault(t => t > nowTod, bow.Times[0]);
        }
        state.Bow = bow;
        _log.LogInformation("BoW initialized: map={Map} times={N} firstStart={Start}.", s.MapId, bow.Times.Count, bow.Start);
    }

    private async Task LoadTournamentAsync(TournamentDatabase db, WorldState state, CancellationToken ct)
    {
        var entries = await db.LoadEntriesAsync(ct);
        if (entries.Count == 0) { _log.LogInformation("No tournament entries; tournament disabled."); return; }

        var t = new TournamentState();
        foreach (var e in entries)
            t.Entries[e.EntryId] = new TournamentEntry
            {
                Group = e.Group, EntryId = e.EntryId, Name = e.Name, Type = e.Type, Class = e.Class,
                Fee = e.Fee, FeeBack = e.FeeBack, PermitItemId = e.PermitItemId, PermitCount = e.PermitCount,
                MinLevel = 0, MaxLevel = 0xFF,
            };
        foreach (var rw in await db.LoadRewardsAsync(ct))
            if (t.Entries.TryGetValue(rw.EntryId, out var e))
                e.Rewards.Add(new TournamentReward { ChartType = rw.ChartType, ItemId = rw.ItemId, Count = rw.Count, Class = rw.Class, CheckShield = rw.CheckShield });

        foreach (var st in await db.LoadScheduleAsync(ct))
            t.Steps.Add(new TournamentStep { Group = st.Group, StepId = st.Step, Period = st.Period });

        // m_bFirstGroupCount = min(entries * 4/3 + 1.999, FIRSTGRADEGROUPCOUNT) — verbatim from TWorldSvr.cpp.
        t.FirstGroupCount = (byte)Math.Min(t.Entries.Count * 4.0 / 3.0 + 1.999, 17);
        state.Tournament = t;
        _log.LogInformation("Tournament config loaded: {N} entries, {S} schedule steps, firstGroupCount={F}.", t.Entries.Count, t.Steps.Count, t.FirstGroupCount);
    }

    private async Task LoadRanksAsync(RankDatabase db, WorldState state, CancellationToken ct)
    {
        // The loaded ladder belongs to last month (matches the C++ first-grade-group load month).
        int lm = DateTime.UtcNow.Month - 1;
        byte lastMonth = (byte)(lm == 0 ? 12 : lm);

        for (byte country = 0; country < Proto.CountryCount; country++)
        {
            var warlord = await db.LoadWarlordAsync(country, ct);
            if (warlord is not null)
            {
                ApplyRow(state.MonthRank[country][0], warlord);
                var month = await db.LoadMonthCharAsync(warlord.CharId, ct);
                if (month is { } mc)
                {
                    state.MonthRank[country][0].MonthPoint = mc.MonthPoint;
                    state.MonthRank[country][0].MonthWin = mc.Win;
                    state.MonthRank[country][0].MonthLose = mc.Lose;
                    state.MonthRank[country][0].Say = mc.Say;
                }
            }

            byte order = 1;
            foreach (var row in await db.LoadMonthLadderAsync(country, ct))
            {
                if (order >= Proto.MonthRankCount) break;
                ApplyRow(state.MonthRank[country][order], row);
                order++;
            }
        }

        foreach (var row in await db.LoadFirstGradeGroupAsync(lastMonth, ct))
            if (row.Country < Proto.CountryCount && row.Rank < Proto.FirstGradeGroupCount)
                ApplyRow(state.FirstGradeGroup[row.Country][row.Rank], row);

        static void ApplyRow(MonthRanker r, MonthRankRow s)
        {
            r.CharId = s.CharId; r.Name = s.Name; r.TotalPoint = s.TotalPoint; r.MonthPoint = s.MonthPoint;
            r.MonthWin = s.MonthWin; r.MonthLose = s.MonthLose; r.TotalWin = s.TotalWin; r.TotalLose = s.TotalLose;
            r.Country = s.Country; r.Level = s.Level; r.Class = s.Class; r.Race = s.Race; r.Sex = s.Sex;
            r.Hair = s.Hair; r.Face = s.Face; r.Say = s.Say; r.Guild = s.Guild;
        }
    }
}
