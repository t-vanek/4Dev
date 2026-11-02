using System.Data;
using Microsoft.Data.SqlClient;

namespace TWorld.Data;

/// <summary>A server-message row (TSVRMSGCHART): id → localized system-message text.</summary>
public readonly record struct ServerMessageRow(uint Id, string Message);

/// <summary>A rock-paper-scissors chart row (TRPSGAMECHART).</summary>
public readonly record struct RpsChartRow(byte Type, byte WinCount, byte ProbWin, byte ProbDraw, byte ProbLose, ushort WinKeep, ushort WinPeriod);

/// <summary>A standing RPS win record (TRPSGAMERECORDTABLE): when a win at (type,winCount) was scored.</summary>
public readonly record struct RpsRecordRow(byte Type, byte WinCount, long WinDate);

/// <summary>An item-chart search hit (TITEMCHART): id, init-state, localized name (CTBLItemFind).</summary>
public readonly record struct ItemFindRow(ushort ItemId, byte InitState, string Name);

/// <summary>A cash-mall gift-chart row (TCMGIFTCHART → m_mapCMGift; CTBLCMGiftChart).</summary>
public readonly record struct CmGiftRow(ushort GiftId, byte GiftType, uint Value, byte Count, byte TakeType,
    byte MaxTakeCount, byte ToolOnly, ushort ErrGiftId, string Title, string Msg);

/// <summary>An active (recently-online) char row for the nation-balance refresh (CTBLActiveCharTable). The
/// aid-country is null when the char has no aid-country row (LEFT OUTER JOIN to TAIDTABLE).</summary>
public readonly record struct ActiveCharRow(uint CharId, byte Country, byte? AidCountry, byte Level);

/// <summary>A character lookup-by-name row (TCHARTABLE; CTBLGetCharInfo).</summary>
public readonly record struct CharInfoRow(uint CharId, string Name, byte Country, byte Level, byte Class);

/// <summary>A lucky-event chart row (TEVENTQUARTERCHART; CTBLEventQuarterList), without item names.</summary>
public readonly record struct LuckyEventRow(ushort Id, byte Day, byte Hour, byte Min,
    ushort ItemId1, ushort ItemId2, ushort ItemId3, ushort ItemId4, ushort ItemId5, byte Count,
    string Present, string Announce, string Title, string Message);

/// <summary>Result of a lucky-event edit (CSPEventQuarterUpdate): proc result, the DB-assigned id on add, and
/// the resolved item names.</summary>
public readonly record struct EventQuarterUpdateResult(int Ret, ushort OutId, string[] Names);

/// <summary>
/// Access to the game database (TGame_gsp). Phase 1 only validates connectivity at startup; the guild,
/// party, ranking, BoW/BR and tournament procs are added in later phases.
/// </summary>
public sealed partial class GameDatabase
{
    private readonly string _cs;
    public GameDatabase(string connectionString) => _cs = connectionString;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>Opens and closes a connection to validate configuration at boot.</summary>
    public async Task PingAsync(CancellationToken ct = default)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("SELECT 1", c);
        await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>TGetRecallID — the persisted recall/companion-monster id high-water mark (CSPGetRecallID).
    /// The world resumes id allocation from here so fresh summon ids never collide with saved ones. Returns
    /// 0 if the proc is absent.</summary>
    public async Task<uint> GetRecallIdAsync(CancellationToken ct = default)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        var genId = SqlProc.Out("@dwRecallID", SqlDbType.Int);
        try
        {
            await SqlProc.ExecAsync(c, "TGetRecallID", null, new[] { genId }, ct);
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            return 0; // proc absent in this baseline
        }
        return genId.AsUInt();
    }

    public async Task<List<uint>> LoadOperatorsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT dwOperatorID FROM TOPERATORTABLE", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<uint>();
        while (await r.ReadAsync(ct)) ids.Add(r.GetUIntSafe(0));
        return ids;
    }

    /// <summary>TSVRMSGCHART — localized system-message text keyed by id (CTBLSvrMsg → m_mapTSvrMsg).</summary>
    public async Task<List<ServerMessageRow>> LoadServerMessagesAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT dwID, szMessage FROM TSVRMSGCHART", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ServerMessageRow>();
        while (await r.ReadAsync(ct)) list.Add(new ServerMessageRow(r.GetUIntSafe(0), r.GetStringSafe(1)));
        return list;
    }

    /// <summary>TRPSGAMECHART — the rock-paper-scissors config rows (CTBLRPSGame).</summary>
    public async Task<List<RpsChartRow>> LoadRpsChartAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT bType, bWinCount, bProb_Win, bProb_Draw, bProb_Lose, wWinKeep, wWinPeriod FROM TRPSGAMECHART", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RpsChartRow>();
        while (await r.ReadAsync(ct))
            list.Add(new RpsChartRow(r.GetByteSafe(0), r.GetByteSafe(1), r.GetByteSafe(2), r.GetByteSafe(3), r.GetByteSafe(4),
                (ushort)r.GetUIntSafe(5), (ushort)r.GetUIntSafe(6)));
        return list;
    }

    /// <summary>TRPSGAMERECORDTABLE — standing win timestamps per (type,winCount) (CTBLRPSGameRecord).</summary>
    public async Task<List<RpsRecordRow>> LoadRpsRecordsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT bType, bWinCount, dWinDate FROM TRPSGAMERECORDTABLE", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RpsRecordRow>();
        while (await r.ReadAsync(ct))
        {
            long when = r.IsDBNull(2) ? 0 : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)).ToUnixTimeSeconds();
            list.Add(new RpsRecordRow(r.GetByteSafe(0), r.GetByteSafe(1), when));
        }
        return list;
    }

    /// <summary>TSaveCastleApplicant — persist a char's castle-war application so it survives a restart
    /// (CSPSaveCastleApplicant). camp 0 clears the application.</summary>
    public async Task SaveCastleApplicantAsync(ushort castle, uint charId, byte camp, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TSaveCastleApplicant", null, new[]
        {
            SqlProc.In("@wCastle", SqlDbType.SmallInt, unchecked((short)castle)),
            SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@bCamp", SqlDbType.TinyInt, camp),
        }, ct);
    }

    /// <summary>TRPSGameRecord — insert (record=true) or delete (record=false) a standing RPS win record
    /// (CSPRPSGameRecord). <paramref name="winDate"/> is unix seconds.</summary>
    public async Task SaveRpsRecordAsync(bool record, uint charId, byte type, byte winCount, long winDate, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TRPSGameRecord", null, new[]
        {
            SqlProc.In("@bRecord", SqlDbType.TinyInt, record ? (byte)1 : (byte)0),
            SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@bType", SqlDbType.TinyInt, type),
            SqlProc.In("@bWinCount", SqlDbType.TinyInt, winCount),
            SqlProc.In("@dWinDate", SqlDbType.SmallDateTime, DateTimeOffset.FromUnixTimeSeconds(winDate).UtcDateTime),
        }, ct);
    }

    // --- Cash-mall gift catalog (TCMGIFT). The control server feeds add/update/del ops; ids are DB-assigned
    //     on add (CSPCMGiftAdd OUTPUT). Ported from DBAccess.h CSPCMGift{Add,Set,Del,CanTake}. ---

    /// <summary>TCMGiftAdd — insert a gift; the DB assigns and returns the new gift id (CSPCMGiftAdd).</summary>
    public async Task<ushort> CmGiftAddAsync(byte giftType, uint value, byte count, byte takeType, byte maxTakeCount,
        byte toolOnly, ushort errGiftId, string title, string msg, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var newId = SqlProc.Out("@wGiftID", SqlDbType.SmallInt);
        await SqlProc.ExecAsync(c, "TCMGiftAdd", null, new[]
        {
            newId,
            SqlProc.In("@bGiftType", SqlDbType.TinyInt, giftType),
            SqlProc.In("@dwValue", SqlDbType.Int, unchecked((int)value)),
            SqlProc.In("@bCount", SqlDbType.TinyInt, count),
            SqlProc.In("@bTakeType", SqlDbType.TinyInt, takeType),
            SqlProc.In("@bMaxTakeCount", SqlDbType.TinyInt, maxTakeCount),
            SqlProc.In("@bToolOnly", SqlDbType.TinyInt, toolOnly),
            SqlProc.In("@wErrGiftID", SqlDbType.SmallInt, unchecked((short)errGiftId)),
            SqlProc.In("@szTitle", SqlDbType.NVarChar, title, 256),
            SqlProc.In("@szMsg", SqlDbType.NVarChar, msg, 1024),
        }, ct);
        return (ushort)newId.AsUInt();
    }

    /// <summary>TCMGiftSet — update an existing gift in place (CSPCMGiftSet).</summary>
    public async Task CmGiftSetAsync(ushort giftId, byte giftType, uint value, byte count, byte takeType, byte maxTakeCount,
        byte toolOnly, ushort errGiftId, string title, string msg, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TCMGiftSet", null, new[]
        {
            SqlProc.In("@wGiftID", SqlDbType.SmallInt, unchecked((short)giftId)),
            SqlProc.In("@bGiftType", SqlDbType.TinyInt, giftType),
            SqlProc.In("@dwValue", SqlDbType.Int, unchecked((int)value)),
            SqlProc.In("@bCount", SqlDbType.TinyInt, count),
            SqlProc.In("@bTakeType", SqlDbType.TinyInt, takeType),
            SqlProc.In("@bMaxTakeCount", SqlDbType.TinyInt, maxTakeCount),
            SqlProc.In("@bToolOnly", SqlDbType.TinyInt, toolOnly),
            SqlProc.In("@wErrGiftID", SqlDbType.SmallInt, unchecked((short)errGiftId)),
            SqlProc.In("@szTitle", SqlDbType.NVarChar, title, 256),
            SqlProc.In("@szMsg", SqlDbType.NVarChar, msg, 1024),
        }, ct);
    }

    /// <summary>TCMGiftDel — delete a gift by id (CSPCMGiftDel).</summary>
    public async Task CmGiftDelAsync(ushort giftId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TCMGiftDel", null, new[]
        {
            SqlProc.In("@wGiftID", SqlDbType.SmallInt, unchecked((short)giftId)),
        }, ct);
    }

    /// <summary>TCMGiftCanTake — gift take-check; returns the CMGIFT_* result code the proc returns
    /// (CSPCMGiftCanTake, the leading <c>? =</c> return value).</summary>
    public async Task<byte> CmGiftCanTakeAsync(string name, ushort giftId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        await SqlProc.ExecAsync(c, "TCMGiftCanTake", ret, new[]
        {
            SqlProc.In("@szName", SqlDbType.NVarChar, name, 64),
            SqlProc.In("@wGiftID", SqlDbType.SmallInt, unchecked((short)giftId)),
        }, ct);
        return ret.AsByte();
    }

    // --- GM item admin (TITEMCHART). Ported from DBAccess.h CTBLItemFind + CSPItemStateChange. ---

    /// <summary>CTBLItemFind — item-chart rows matching a name (LIKE) or exact id. Returns (itemId, initState,
    /// name) tuples.</summary>
    public async Task<List<ItemFindRow>> ItemFindAsync(ushort itemId, string name, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT wItemID, bInitState, szName FROM TITEMCHART WHERE szName LIKE @szName OR wItemID = @wItemID", c);
        cmd.Parameters.Add(SqlProc.In("@szName", SqlDbType.NVarChar, name, 64));
        cmd.Parameters.Add(SqlProc.In("@wItemID", SqlDbType.SmallInt, unchecked((short)itemId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ItemFindRow>();
        while (await r.ReadAsync(ct))
            list.Add(new ItemFindRow((ushort)r.GetUIntSafe(0), r.GetByteSafe(1), r.GetStringSafe(2)));
        return list;
    }

    /// <summary>TCMGIFTCHART — the persisted cash-mall gift catalog loaded at boot (CTBLCMGiftChart).</summary>
    public async Task<List<CmGiftRow>> LoadCmGiftChartAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT wGiftID, bGiftType, dwValue, bCount, bTakeType, bMaxTakeCount, bToolOnly, wErrGiftID, szTitle, szMsg FROM TCMGIFTCHART", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CmGiftRow>();
        while (await r.ReadAsync(ct))
            list.Add(new CmGiftRow((ushort)r.GetUIntSafe(0), r.GetByteSafe(1), r.GetUIntSafe(2), r.GetByteSafe(3),
                r.GetByteSafe(4), r.GetByteSafe(5), r.GetByteSafe(6), (ushort)r.GetUIntSafe(7), r.GetStringSafe(8), r.GetStringSafe(9)));
        return list;
    }

    /// <summary>CTBLGetCharInfo — look a character up by exact name (for the GM tournament-event PLAYERADD).
    /// Returns 0, 1, or &gt;1 rows; the caller treats anything but exactly one as a failure.</summary>
    public async Task<List<CharInfoRow>> GetCharInfoByNameAsync(string name, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT dwCharID, szName, bCountry, bLevel, bClass FROM TCHARTABLE WHERE szName = @szName", c);
        cmd.Parameters.Add(SqlProc.In("@szName", SqlDbType.NVarChar, name, 64));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CharInfoRow>();
        while (await r.ReadAsync(ct))
            list.Add(new CharInfoRow(r.GetUIntSafe(0), r.GetStringSafe(1), r.GetByteSafe(2), r.GetByteSafe(3), r.GetByteSafe(4)));
        return list;
    }

    // --- Event subsystem (TEVENTQUARTERCHART + TGetItemName). ---

    /// <summary>CTBLEventQuarterList — the lucky-event rows scheduled on a given day-of-week (1..7).</summary>
    public async Task<List<LuckyEventRow>> EventQuarterListAsync(byte day, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT wID, bDay, bHour, bMinute, wItemID1, wItemID2, wItemID3, wItemID4, wItemID5, bCount, " +
            "szPresent, szAnnounce, szTitle, szMessage FROM TEVENTQUARTERCHART WHERE bDay = @bDay ORDER BY bHour, bMinute", c);
        cmd.Parameters.Add(SqlProc.In("@bDay", SqlDbType.TinyInt, day));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<LuckyEventRow>();
        while (await r.ReadAsync(ct))
            list.Add(new LuckyEventRow((ushort)r.GetUIntSafe(0), r.GetByteSafe(1), r.GetByteSafe(2), r.GetByteSafe(3),
                (ushort)r.GetUIntSafe(4), (ushort)r.GetUIntSafe(5), (ushort)r.GetUIntSafe(6), (ushort)r.GetUIntSafe(7), (ushort)r.GetUIntSafe(8),
                r.GetByteSafe(9), r.GetStringSafe(10), r.GetStringSafe(11), r.GetStringSafe(12), r.GetStringSafe(13)));
        return list;
    }

    /// <summary>TGetItemName — resolve up to 5 item ids to their localized names (CSPGetItemName).</summary>
    public async Task<string[]> GetItemNamesAsync(ushort[] ids, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var outs = new SqlParameter[5];
        var args = new List<SqlParameter>();
        for (int i = 0; i < 5; i++) args.Add(SqlProc.In($"@wItemID{i + 1}", SqlDbType.SmallInt, unchecked((short)(i < ids.Length ? ids[i] : 0))));
        for (int i = 0; i < 5; i++) { outs[i] = SqlProc.Out($"@szName{i + 1}", SqlDbType.NVarChar, 64); args.Add(outs[i]); }
        await SqlProc.ExecAsync(c, "TGetItemName", null, args, ct);
        return outs.Select(p => p.AsString()).ToArray();
    }

    /// <summary>TEventQuarterUpdate — add/update/del a lucky-event chart row (CSPEventQuarterUpdate). Returns the
    /// proc result, the DB-assigned id (on add), and the resolved item names.</summary>
    public async Task<EventQuarterUpdateResult> EventQuarterUpdateAsync(byte type, ushort id, byte day, byte hour,
        byte minute, ushort[] itemIds, byte count, string present, string announce, string title, string message,
        CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var outId = SqlProc.Out("@wOutID", SqlDbType.SmallInt);
        var names = new SqlParameter[5];
        for (int i = 0; i < 5; i++) names[i] = SqlProc.Out($"@szName{i + 1}", SqlDbType.NVarChar, 64);
        var args = new List<SqlParameter>
        {
            SqlProc.In("@bType", SqlDbType.TinyInt, type),
            SqlProc.In("@wID", SqlDbType.SmallInt, unchecked((short)id)),
            SqlProc.In("@bDay", SqlDbType.TinyInt, day),
            SqlProc.In("@bHour", SqlDbType.TinyInt, hour),
            SqlProc.In("@bMinute", SqlDbType.TinyInt, minute),
            SqlProc.In("@wItemID1", SqlDbType.SmallInt, unchecked((short)(itemIds.Length > 0 ? itemIds[0] : 0))),
            SqlProc.In("@wItemID2", SqlDbType.SmallInt, unchecked((short)(itemIds.Length > 1 ? itemIds[1] : 0))),
            SqlProc.In("@wItemID3", SqlDbType.SmallInt, unchecked((short)(itemIds.Length > 2 ? itemIds[2] : 0))),
            SqlProc.In("@wItemID4", SqlDbType.SmallInt, unchecked((short)(itemIds.Length > 3 ? itemIds[3] : 0))),
            SqlProc.In("@wItemID5", SqlDbType.SmallInt, unchecked((short)(itemIds.Length > 4 ? itemIds[4] : 0))),
            SqlProc.In("@bCount", SqlDbType.TinyInt, count),
            SqlProc.In("@szPresent", SqlDbType.NVarChar, present, 64),
            SqlProc.In("@szAnnounce", SqlDbType.NVarChar, announce, 256),
            SqlProc.In("@szTitle", SqlDbType.NVarChar, title, 64),
            SqlProc.In("@szMessage", SqlDbType.NVarChar, message, 501),
            outId, names[0], names[1], names[2], names[3], names[4],
        };
        await SqlProc.ExecAsync(c, "TEventQuarterUpdate", ret, args, ct);
        return new EventQuarterUpdateResult(ret.AsInt(), (ushort)outId.AsUInt(), names.Select(p => p.AsString()).ToArray());
    }

    /// <summary>CTBLActiveCharDel — purge active-char rows older than the cutoff (DM_ACTIVECHARUPDATE cleanup).</summary>
    public async Task ActiveCharDeleteOldAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE TACTIVECHARTABLE WHERE dateEnter < @d", c);
        cmd.Parameters.Add(SqlProc.In("@d", SqlDbType.SmallDateTime, cutoffUtc));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>CTBLActiveCharTable — recently-online chars joined to their country/level + aid-country, for the
    /// nation-balance bucket rebuild (DM_ACTIVECHARUPDATE).</summary>
    public async Task<List<ActiveCharRow>> LoadActiveCharsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT AC.dwCharID, CT.bCountry, AT.bCountry, CT.bLevel FROM TACTIVECHARTABLE AS AC " +
            "INNER JOIN TCHARTABLE AS CT ON AC.dwCharID = CT.dwCharID " +
            "LEFT OUTER JOIN TAIDTABLE AS AT ON AC.dwCharID = AT.dwCharID", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ActiveCharRow>();
        while (await r.ReadAsync(ct))
            list.Add(new ActiveCharRow(r.GetUIntSafe(0), r.GetByteSafe(1), r.IsDBNull(2) ? null : r.GetByteSafe(2), r.GetByteSafe(3)));
        return list;
    }

    // --- Persistence side-writes the C++ does via its DM thread (1:1 fidelity). All best-effort. ---

    private static DateTime MoneyFromUnix(long s) => s <= 0 ? new DateTime(1900, 1, 1) : DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    /// <summary>TCashItemSale — persist one item's sale value (CSPCashItemSale). Called per item once all maps
    /// confirm the sale.</summary>
    public async Task CashItemSaleAsync(ushort id, byte value, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TCashItemSale", null, new[]
        {
            SqlProc.In("@wID", SqlDbType.SmallInt, unchecked((short)id)),
            SqlProc.In("@bValue", SqlDbType.TinyInt, value),
        }, ct);
    }

    /// <summary>TTournamentApply — register (add=1) / unregister (add=0) a tournament participant
    /// (CSPTournamentApply).</summary>
    public async Task TournamentApplyAsync(byte add, uint charId, byte entry, uint chiefId, string hwid, uint ip, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTournamentApply", null, new[]
        {
            SqlProc.In("@bAdd", SqlDbType.TinyInt, add),
            SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@bEntry", SqlDbType.TinyInt, entry),
            SqlProc.In("@dwChiefID", SqlDbType.Int, unchecked((int)chiefId)),
            SqlProc.In("@szHWID", SqlDbType.NVarChar, hwid, 64),
            SqlProc.In("@dwIPAddr", SqlDbType.Int, unchecked((int)ip)),
        }, ct);
    }

    /// <summary>TTournamentClear — wipe the persisted tournament registration (CSPTournamentClear).</summary>
    public async Task TournamentClearAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTournamentClear", null, new[] { SqlProc.In("@bClear", SqlDbType.TinyInt, (byte)1) }, ct);
    }

    /// <summary>TTournamentStatus — persist the running tournament's id/group/step (CSPTournamentStatus).</summary>
    public async Task TournamentStatusAsync(ushort id, byte group, byte step, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTournamentStatus", null, new[]
        {
            SqlProc.In("@wID", SqlDbType.SmallInt, unchecked((short)id)),
            SqlProc.In("@bGroup", SqlDbType.TinyInt, group),
            SqlProc.In("@bStep", SqlDbType.TinyInt, step),
        }, ct);
    }

    /// <summary>TTournamentResult — persist a match result (CSPTournamentResult).</summary>
    public async Task TournamentResultAsync(byte step, byte ret, uint win, uint lose, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTournamentResult", null, new[]
        {
            SqlProc.In("@bStep", SqlDbType.TinyInt, step),
            SqlProc.In("@bRet", SqlDbType.TinyInt, ret),
            SqlProc.In("@dwWin", SqlDbType.Int, unchecked((int)win)),
            SqlProc.In("@dwLose", SqlDbType.Int, unchecked((int)lose)),
        }, ct);
    }

    /// <summary>TTournamentPayback — mail an entry-fee refund to an unseeded entrant (CSPTournamentPayback).
    /// <paramref name="packedMoney"/> is split into gold/silver/cooper via the CalcMoney radix (1000), as the
    /// C++ OnDM_TOURNAMENTPAYBACK does.</summary>
    public async Task TournamentPaybackAsync(uint charId, uint packedMoney, CancellationToken ct = default)
    {
        uint cooper = packedMoney % 1000, silver = packedMoney / 1000 % 1000, gold = packedMoney / 1000 / 1000;
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTournamentPayback", null, new[]
        {
            SqlProc.Out("@dwPostID", SqlDbType.Int),
            SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@dwGold", SqlDbType.Int, unchecked((int)gold)),
            SqlProc.In("@dwSilver", SqlDbType.Int, unchecked((int)silver)),
            SqlProc.In("@dwCooper", SqlDbType.Int, unchecked((int)cooper)),
        }, ct);
    }

    /// <summary>TTnmtEventTime — persist an event-tournament schedule window (CSPTnmtEventTime).</summary>
    public async Task TnmtEventTimeAsync(ushort tourId, byte week, byte day, uint start, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTnmtEventTime", null, new[]
        {
            SqlProc.In("@wTourID", SqlDbType.SmallInt, unchecked((short)tourId)),
            SqlProc.In("@bWeek", SqlDbType.TinyInt, week),
            SqlProc.In("@bDay", SqlDbType.TinyInt, day),
            SqlProc.In("@dwStart", SqlDbType.Int, unchecked((int)start)),
        }, ct);
    }

    /// <summary>TTnmtEventSchedule — persist one event-tournament schedule step (CSPTnmtEventSchedule).</summary>
    public async Task TnmtEventScheduleAsync(ushort tourId, byte step, uint period, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTnmtEventSchedule", null, new[]
        {
            SqlProc.In("@wTourID", SqlDbType.SmallInt, unchecked((short)tourId)),
            SqlProc.In("@bStep", SqlDbType.TinyInt, step),
            SqlProc.In("@dwPeriod", SqlDbType.Int, unchecked((int)period)),
        }, ct);
    }

    /// <summary>TTnmtEventDel — delete an event-tournament's persisted schedule (CSPTnmtEventDel).</summary>
    public async Task TnmtEventDelAsync(ushort tourId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTnmtEventDel", null, new[] { SqlProc.In("@wTourID", SqlDbType.SmallInt, unchecked((short)tourId)) }, ct);
    }

    /// <summary>TTnmtEventEntry — persist one event-tournament entry (CSPTnmtEventEntry). entryId 0 + empty name
    /// is the C++ "clear existing entries" sentinel.</summary>
    public async Task TnmtEventEntryAsync(ushort tourId, byte entryId, string name, byte type, uint cls, uint fee,
        uint feeBack, ushort permitItemId, byte permitCount, byte minLevel, byte maxLevel, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTnmtEventEntry", null, new[]
        {
            SqlProc.In("@wTourID", SqlDbType.SmallInt, unchecked((short)tourId)),
            SqlProc.In("@bEntryID", SqlDbType.TinyInt, entryId),
            SqlProc.In("@szName", SqlDbType.NVarChar, name, 64),
            SqlProc.In("@bType", SqlDbType.TinyInt, type),
            SqlProc.In("@dwClass", SqlDbType.Int, unchecked((int)cls)),
            SqlProc.In("@dwFee", SqlDbType.Int, unchecked((int)fee)),
            SqlProc.In("@dwFeeBack", SqlDbType.Int, unchecked((int)feeBack)),
            SqlProc.In("@wPermitItemID", SqlDbType.SmallInt, unchecked((short)permitItemId)),
            SqlProc.In("@bPermitCount", SqlDbType.TinyInt, permitCount),
            SqlProc.In("@bMinLevel", SqlDbType.TinyInt, minLevel),
            SqlProc.In("@bMaxLevel", SqlDbType.TinyInt, maxLevel),
        }, ct);
    }

    /// <summary>TTnmtEventReward — persist one event-tournament reward row (CSPTnmtEventReward).</summary>
    public async Task TnmtEventRewardAsync(ushort tourId, byte entryId, byte chartType, ushort itemId, byte count,
        uint cls, byte checkShield, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TTnmtEventReward", null, new[]
        {
            SqlProc.In("@wTourID", SqlDbType.SmallInt, unchecked((short)tourId)),
            SqlProc.In("@bEntryID", SqlDbType.TinyInt, entryId),
            SqlProc.In("@bChartType", SqlDbType.TinyInt, chartType),
            SqlProc.In("@wItemID", SqlDbType.SmallInt, unchecked((short)itemId)),
            SqlProc.In("@bCount", SqlDbType.TinyInt, count),
            SqlProc.In("@dwClass", SqlDbType.Int, unchecked((int)cls)),
            SqlProc.In("@bCheckShield", SqlDbType.TinyInt, checkShield),
        }, ct);
    }

    /// <summary>THelpMessage — persist a scheduled help message (CSPHelpMessage). start/end are unix seconds.</summary>
    public async Task HelpMessageAsync(byte id, long start, long end, string message, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "THelpMessage", null, new[]
        {
            SqlProc.In("@bID", SqlDbType.TinyInt, id),
            SqlProc.In("@timeStart", SqlDbType.DateTime, MoneyFromUnix(start)),
            SqlProc.In("@timeEnd", SqlDbType.DateTime, MoneyFromUnix(end)),
            SqlProc.In("@szMessage", SqlDbType.NVarChar, message, 2048),
        }, ct);
    }

    /// <summary>TItemStateChange — set an item's init-state; returns the proc's int result (0 = success).</summary>
    public async Task<int> ItemStateChangeAsync(ushort itemId, byte initState, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        await SqlProc.ExecAsync(c, "TItemStateChange", ret, new[]
        {
            SqlProc.In("@wItemID", SqlDbType.SmallInt, unchecked((short)itemId)),
            SqlProc.In("@bInitState", SqlDbType.TinyInt, initState),
        }, ct);
        return ret.AsInt();
    }
}
