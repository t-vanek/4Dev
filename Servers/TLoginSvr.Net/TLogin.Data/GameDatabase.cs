using System.Data;
using Microsoft.Data.SqlClient;
using TLogin.Protocol;

namespace TLogin.Data;

/// <summary>
/// Access to a single world group's game database (DSN <c>TGAME_GSP</c> → <c>TGame_gsp</c>):
/// characters, equipped items, guild info, BoW/BR placement and the map-server lookup. One instance per
/// group connection string. Transcribed from <c>Servers/TLoginSvr/DBAccess.h</c>. Equipped-item storage
/// constants come from THIS repo's protocol headers (INVEN_EQUIP is 0xFE here, not 0).
/// </summary>
public sealed class GameDatabase
{
    private readonly string _cs;
    public GameDatabase(string connectionString) => _cs = connectionString;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    private async Task<uint> FindPlayerAsync(string proc, uint userId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        var charId = SqlProc.Out("@dwCharID", SqlDbType.Int);
        try
        {
            await SqlProc.ExecAsync(c, proc, null, new[]
            {
                SqlProc.In("@dwUserID", SqlDbType.Int, unchecked((int)userId)),
                charId,
            }, ct);
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            // Optional game-mode proc (e.g. TFindBOWPlayer) is absent in this baseline. The C++ server
            // treats a failed query->Call() as "no such player" and leaves the char id at 0; do the same.
            return 0;
        }
        return charId.AsUInt();
    }

    /// <summary>TFindBOWPlayer — the account's Battle-of-Wonders character, or 0. CSPFindBOWPlayer.</summary>
    public Task<uint> FindBowPlayerAsync(uint userId, CancellationToken ct = default) => FindPlayerAsync("TFindBOWPlayer", userId, ct);

    /// <summary>TFindBRPlayer — the account's Battle-Royal character, or 0. CSPFindBRPlayer.</summary>
    public Task<uint> FindBrPlayerAsync(uint userId, CancellationToken ct = default) => FindPlayerAsync("TFindBRPlayer", userId, ct);

    /// <summary>TFindServerID — map a character+channel to its map-server id. CSPFindServerID.</summary>
    public async Task<FindServerRow> FindServerIdAsync(uint charId, byte channel, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var serverId = SqlProc.Out("@bServerID", SqlDbType.TinyInt);
        await SqlProc.ExecAsync(c, "TFindServerID", ret, new[]
        {
            SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@bChannel", SqlDbType.TinyInt, channel),
            serverId,
        }, ct);
        return new FindServerRow(ret.AsInt(), serverId.AsByte());
    }

    /// <summary>TCreateChar — creates a character. CSPCreateChar (param order is authoritative).</summary>
    public async Task<CreateCharRow> CreateCharAsync(CreateCharArgs a, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var createCnt = SqlProc.Out("@bCreateCnt", SqlDbType.TinyInt);
        var charId = SqlProc.Out("@dwCharID", SqlDbType.Int);
        await SqlProc.ExecAsync(c, "TCreateChar", ret, new[]
        {
            createCnt,
            SqlProc.In("@szNAME", SqlDbType.VarChar, a.Name, Proto.MaxName),
            charId,
            SqlProc.In("@dwUserID", SqlDbType.Int, unchecked((int)a.UserId)),
            SqlProc.In("@bGroup", SqlDbType.TinyInt, a.Group),
            SqlProc.In("@bSlot", SqlDbType.TinyInt, a.Slot),
            SqlProc.In("@bClass", SqlDbType.TinyInt, a.Class),
            SqlProc.In("@bRace", SqlDbType.TinyInt, a.Race),
            SqlProc.In("@bCountry", SqlDbType.TinyInt, a.Country),
            SqlProc.In("@bSex", SqlDbType.TinyInt, a.Sex),
            SqlProc.In("@bHair", SqlDbType.TinyInt, a.Hair),
            SqlProc.In("@bFace", SqlDbType.TinyInt, a.Face),
            SqlProc.In("@bBody", SqlDbType.TinyInt, a.Body),
            SqlProc.In("@bPants", SqlDbType.TinyInt, a.Pants),
            SqlProc.In("@bHand", SqlDbType.TinyInt, a.Hand),
            SqlProc.In("@bFoot", SqlDbType.TinyInt, a.Foot),
            SqlProc.In("@bLevelOption", SqlDbType.TinyInt, a.LevelOption),
        }, ct);
        return new CreateCharRow(ret.AsInt(), charId.AsUInt(), createCnt.AsByte());
    }

    /// <summary>TDeleteChar — deletes a character. CSPDeleteChar.</summary>
    public async Task<DeleteCharRow> DeleteCharAsync(byte group, uint userId, uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var createCnt = SqlProc.Out("@bCreateCnt", SqlDbType.TinyInt);
        await SqlProc.ExecAsync(c, "TDeleteChar", ret, new[]
        {
            createCnt,
            SqlProc.In("@bGroup", SqlDbType.TinyInt, group),
            SqlProc.In("@dwUserID", SqlDbType.Int, unchecked((int)userId)),
            SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)),
        }, ct);
        return new DeleteCharRow(ret.AsInt(), createCnt.AsByte());
    }

    /// <summary>TGetGuildInfo — guild name/fame for a character (null if none). CSPGetGuildInfo.</summary>
    public async Task<GuildRow?> GetGuildInfoAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var name = SqlProc.Out("@szName", SqlDbType.VarChar, Proto.MaxName);
        var fame = SqlProc.Out("@dwFame", SqlDbType.Int);
        var fameColor = SqlProc.Out("@dwFameColor", SqlDbType.Int);
        await SqlProc.ExecAsync(c, "TGetGuildInfo", null, new[]
        {
            SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)),
            name, fame, fameColor,
        }, ct);
        var n = name.AsString();
        return string.IsNullOrEmpty(n) ? null : new GuildRow(n, fame.AsUInt(), fameColor.AsUInt());
    }

    private const string CharSql = @"
SELECT TOP 6 szNAME, dwCharID, bStartAct, bClass, bRace, bCountry, bSex, bHair, bFace, bBody, bPants,
       bHand, bFoot, bSlot, bLevel, dwRegion, bHelmetHide
FROM TCHARTABLE WHERE dwUserID = @dwUserID AND bDelete = 0 ORDER BY dLogoutDate DESC";

    /// <summary>CTBLChar — up to six non-deleted characters for the account, newest logout first.</summary>
    public async Task<List<CharRow>> CharListAsync(uint userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(CharSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwUserID", SqlDbType.Int, unchecked((int)userId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CharRow>();
        while (await r.ReadAsync(ct))
            list.Add(new CharRow(
                r.GetStringSafe(0), r.GetUIntSafe(1), r.GetByteSafe(2), r.GetByteSafe(3), r.GetByteSafe(4),
                r.GetByteSafe(5), r.GetByteSafe(6), r.GetByteSafe(7), r.GetByteSafe(8), r.GetByteSafe(9),
                r.GetByteSafe(10), r.GetByteSafe(11), r.GetByteSafe(12), r.GetByteSafe(13), r.GetByteSafe(14),
                r.GetUIntSafe(15), r.GetByteSafe(16)));
        return list;
    }

    /// <summary>Two DB round trips for all six characters instead of one plus two per character.
    /// Uses the same guild membership and item columns as TGetGuildInfo and EquippedItemsAsync.</summary>
    public async Task<List<CharRow>> CharListWithAppearanceAsync(uint userId, CancellationToken ct = default)
    {
        var chars = await CharListAsync(userId, ct);
        if (chars.Count == 0) return chars;
        var byId = chars.ToDictionary(x => x.CharId);
        string ids = string.Join(",", chars.Select((_, i) => $"@c{i}"));
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand($@"
            SELECT dwOwnerID, bItemID, wItemID, bLevel, bGradeEffect, dwTime3, dwTime4, dwTime6, wMoggItemID
            FROM TITEMTABLE WHERE dwOwnerID IN ({ids}) AND bOwnerType=0 AND bStorageType=0 AND dwStorageID=254;
            SELECT m.dwCharID, g.szName, g.dwFame, g.dwFameColor FROM TGUILDMEMBERTABLE m
            JOIN TGUILDTABLE g ON g.dwID=m.dwGuildID WHERE m.dwCharID IN ({ids});", c);
        for (int i = 0; i < chars.Count; i++) cmd.Parameters.Add(SqlProc.In($"@c{i}", SqlDbType.Int, unchecked((int)chars[i].CharId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (byId.TryGetValue(r.GetUIntSafe(0), out var ch))
                ch.Items.Add(new ItemRow(r.GetByteSafe(1), r.GetUShortSafe(2), r.GetByteSafe(3), r.GetByteSafe(4),
                    (ushort)r.GetUIntSafe(5), (byte)(r.GetUIntSafe(6) == ch.CharId ? 1 : 0), r.GetUShortSafe(7), r.GetUShortSafe(8)));
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            if (byId.TryGetValue(r.GetUIntSafe(0), out var ch))
            { ch.GuildName = r.GetStringSafe(1); ch.Fame = r.GetUIntSafe(2); ch.FameColor = r.GetUIntSafe(3); }
        return chars;
    }

    private const string ItemSql = @"
SELECT bItemID, wItemID, bLevel, bGradeEffect, dwTime3, dwTime4, dwTime6, wMoggItemID
FROM TITEMTABLE
WHERE dwOwnerID = @dwOwnerID AND bOwnerType = @bOwnerType AND bStorageType = @bStorageType AND dwStorageID = @dwStorageID";

    /// <summary>CTBLItem — items in a storage slot. dwTime3/4/6 alias to color/regGuild/customTex per DBAccess.h.</summary>
    public async Task<List<ItemRow>> ItemListAsync(uint ownerId, byte ownerType, byte storageType, uint storageId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(ItemSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwOwnerID", SqlDbType.Int, unchecked((int)ownerId)));
        cmd.Parameters.Add(SqlProc.In("@bOwnerType", SqlDbType.TinyInt, ownerType));
        cmd.Parameters.Add(SqlProc.In("@bStorageType", SqlDbType.TinyInt, storageType));
        cmd.Parameters.Add(SqlProc.In("@dwStorageID", SqlDbType.Int, unchecked((int)storageId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ItemRow>();
        while (await r.ReadAsync(ct))
            list.Add(new ItemRow(
                r.GetByteSafe(0),                    // bItemID
                r.GetUShortSafe(1),                  // wItemID
                r.GetByteSafe(2),                    // bLevel
                r.GetByteSafe(3),                    // bGradeEffect
                (ushort)r.GetUIntSafe(4),            // dwTime3 -> wColor
                (byte)(r.GetUIntSafe(5) != 0 && r.GetUIntSafe(5) == ownerId ? 1 : 0), // owner id -> registration flag
                r.GetUShortSafe(6),                  // dwTime6 -> wCustomTex
                r.GetUShortSafe(7)));                // wMoggItemID
        return list;
    }

    /// <summary>Equipped-inventory items for a character (storage INVEN / slot INVEN_EQUIP / owner CHAR).</summary>
    public Task<List<ItemRow>> EquippedItemsAsync(uint charId, CancellationToken ct = default)
        => ItemListAsync(charId, Proto.OwnerChar, Proto.StorageInven, Proto.InvenEquip, ct);
}
