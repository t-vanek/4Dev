using System.Data;
using Microsoft.Data.SqlClient;

namespace TMap.Data;

/// <summary>One row of a mailbox page (C++ <c>CTBLPostTable</c>, DBAccess.h:2806).</summary>
public readonly record struct PostListRow(uint PostId, byte Read, byte Type, string Sender, string Title, long TimeRecv, byte Contain);

/// <summary>A mail opened for reading (C++ <c>CSPPostView</c> outputs, DBAccess.h:7298). <see cref="Result"/> is the
/// proc's return — 0 when found.</summary>
public sealed record PostViewData(int Result, byte ItemCount, uint SendId, byte Type, byte Read, uint Gold, uint Silver,
    uint Cooper, long TimeRecv, string Sender, string Title, string Message, byte Contain);

/// <summary>The outcome of settling a bill (C++ <c>CSPPostBillsUpdate</c> outputs, DBAccess.h:7331).</summary>
public readonly record struct PostBillResult(int Result, uint NewId, string Sender, string RecvName, string Title);

/// <summary>
/// The mail database (TPOSTTABLE + the post-storage items in TITEMTABLE). One method per stored procedure the
/// C++ calls, so the map logic can be tested against an in-memory fake.
/// </summary>
public interface IPostStore
{
    /// <summary><c>TPostCanSend</c> → (return, recipient id). 1 no such receiver, 6 mailbox full.</summary>
    Task<(int Result, uint RecvId)> PostCanSendAsync(string sender, string target, byte type);

    /// <summary><c>TSavePost</c> → (return, new post id, recipient id).</summary>
    Task<(int Result, uint PostId, uint RecvId)> SavePostAsync(uint sendId, uint recvId, string target, string sender,
        string title, string message, byte read, byte type, uint gold, uint silver, uint cooper, long timeRecv);

    /// <summary>Stores one attached item under the post (<c>TSaveItemDirect</c>, storage <c>STORAGE_POST</c>).</summary>
    Task SavePostItemAsync(uint recvId, ItemSaveData item);

    /// <summary><c>TGetPostInfo</c> → (total, unread, first id of the page).</summary>
    Task<(ushort Total, ushort NotRead, uint BeginId)> PostInfoAsync(uint charId, ushort page);

    /// <summary><c>CTBLPostTable</c> — every mail with id ≤ <paramref name="beginId"/>, newest first.</summary>
    Task<List<PostListRow>> PostListAsync(uint charId, uint beginId);

    Task<PostViewData> PostViewAsync(uint charId, uint postId);
    Task<List<FullItemRow>> PostItemsAsync(uint charId, uint postId);

    /// <summary><c>TPostDelete</c> → return (1 money left, 2 an item left, 0 deleted).</summary>
    Task<int> PostDeleteAsync(uint charId, uint postId);

    /// <summary><c>TPostGetItem</c> — zero the money and delete the post's items.</summary>
    Task PostGetItemAsync(uint charId, uint postId);

    Task<PostBillResult> PostBillsUpdateAsync(uint postId, string charName, byte type);

    /// <summary><c>CTBLPostBill</c> — the unpaid bills a sender has out: (post id, sent at).</summary>
    Task<List<(uint PostId, long TimeRecv)>> PendingBillsAsync(uint sendId);
}

public sealed partial class GameDatabase : IPostStore
{
    private const byte StoragePost = 2;
    private const byte PostBills = 2;

    public async Task<(int Result, uint RecvId)> PostCanSendAsync(string sender, string target, byte type)
    {
        await using var c = await OpenAsync(default);
        var ret = SqlProc.Ret();
        var recv = SqlProc.Out("@p0", SqlDbType.Int);
        await SqlProc.ExecAsync(c, "TPostCanSend", ret, new[]
        {
            recv,
            SqlProc.In("@p1", SqlDbType.VarChar, sender, 50),
            SqlProc.In("@p2", SqlDbType.VarChar, target, 50),
            SqlProc.In("@p3", SqlDbType.TinyInt, type),
        }, default);
        return (ret.AsInt(), recv.AsUInt());
    }

    public async Task<(int Result, uint PostId, uint RecvId)> SavePostAsync(uint sendId, uint recvId, string target,
        string sender, string title, string message, byte read, byte type, uint gold, uint silver, uint cooper, long timeRecv)
    {
        await using var c = await OpenAsync(default);
        var ret = SqlProc.Ret();
        var make = SqlProc.Out("@p0", SqlDbType.Int);
        var recv = SqlProc.Out("@p1", SqlDbType.Int);
        await SqlProc.ExecAsync(c, "TSavePost", ret, new[]
        {
            make, recv,
            SqlProc.In("@p2", SqlDbType.Int, unchecked((int)sendId)),
            SqlProc.In("@p3", SqlDbType.Int, unchecked((int)recvId)),
            SqlProc.In("@p4", SqlDbType.VarChar, target, 50),
            SqlProc.In("@p5", SqlDbType.VarChar, sender, 50),
            SqlProc.In("@p6", SqlDbType.VarChar, title, 256),
            SqlProc.In("@p7", SqlDbType.VarChar, message, 2048),
            SqlProc.In("@p8", SqlDbType.TinyInt, read),
            SqlProc.In("@p9", SqlDbType.TinyInt, type),
            SqlProc.In("@p10", SqlDbType.Int, unchecked((int)gold)),
            SqlProc.In("@p11", SqlDbType.Int, unchecked((int)silver)),
            SqlProc.In("@p12", SqlDbType.Int, unchecked((int)cooper)),
            SqlProc.In("@p13", SqlDbType.SmallDateTime, FromTime64(timeRecv)),
        }, default);
        return (ret.AsInt(), make.AsUInt(), recv.AsUInt());
    }

    public Task SavePostItemAsync(uint recvId, ItemSaveData item) => SaveItemDirectAsync(recvId, item);

    public async Task<(ushort Total, ushort NotRead, uint BeginId)> PostInfoAsync(uint charId, ushort page)
    {
        await using var c = await OpenAsync(default);
        var total = SqlProc.Out("@p0", SqlDbType.SmallInt);
        var notRead = SqlProc.Out("@p1", SqlDbType.SmallInt);
        var begin = SqlProc.Out("@p2", SqlDbType.Int);
        await SqlProc.ExecAsync(c, "TGetPostInfo", null, new[]
        {
            total, notRead, begin,
            SqlProc.In("@p3", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@p4", SqlDbType.SmallInt, unchecked((short)page)),
        }, default);
        return ((ushort)total.AsInt(), (ushort)notRead.AsInt(), begin.AsUInt());
    }

    public async Task<List<PostListRow>> PostListAsync(uint charId, uint beginId)
    {
        await using var c = await OpenAsync(default);
        await using var cmd = new SqlCommand(@"SELECT dwPostID, bRead, timeRecv, szSender, bType, szTitle, bContain
FROM TPOSTTABLE WHERE dwCharID = @c AND dwPostID <= @b ORDER BY dwPostID DESC", c);
        cmd.Parameters.Add(SqlProc.In("@c", SqlDbType.Int, unchecked((int)charId)));
        cmd.Parameters.Add(SqlProc.In("@b", SqlDbType.Int, unchecked((int)beginId)));
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<PostListRow>();
        while (await r.ReadAsync())
            list.Add(new PostListRow(r.GetUIntSafe(0), r.GetByteSafe(1), r.GetByteSafe(4), r.GetStringSafe(3),
                r.GetStringSafe(5), ToTime64(r, 2), r.GetByteSafe(6)));
        return list;
    }

    public async Task<PostViewData> PostViewAsync(uint charId, uint postId)
    {
        await using var c = await OpenAsync(default);
        var ret = SqlProc.Ret();
        var outs = new[]
        {
            SqlProc.Out("@p0", SqlDbType.TinyInt), SqlProc.Out("@p1", SqlDbType.Int), SqlProc.Out("@p2", SqlDbType.TinyInt),
            SqlProc.Out("@p3", SqlDbType.TinyInt), SqlProc.Out("@p4", SqlDbType.Int), SqlProc.Out("@p5", SqlDbType.Int),
            SqlProc.Out("@p6", SqlDbType.Int), SqlProc.Out("@p7", SqlDbType.SmallDateTime),
            SqlProc.Out("@p8", SqlDbType.VarChar, 50), SqlProc.Out("@p9", SqlDbType.VarChar, 256),
            SqlProc.Out("@p10", SqlDbType.VarChar, 2048), SqlProc.Out("@p11", SqlDbType.TinyInt),
        };
        await SqlProc.ExecAsync(c, "TPostView", ret, outs.Concat(new[]
        {
            SqlProc.In("@p12", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@p13", SqlDbType.Int, unchecked((int)postId)),
        }).ToArray(), default);
        return new PostViewData(ret.AsInt(), outs[0].AsByte(), outs[1].AsUInt(), outs[2].AsByte(), outs[3].AsByte(),
            outs[4].AsUInt(), outs[5].AsUInt(), outs[6].AsUInt(), ToTime64(outs[7].Value), outs[8].AsString(),
            outs[9].AsString(), outs[10].AsString(), outs[11].AsByte());
    }

    public Task<List<FullItemRow>> PostItemsAsync(uint charId, uint postId) => LoadPostItemsAsync(charId, postId);

    public async Task<int> PostDeleteAsync(uint charId, uint postId)
    {
        await using var c = await OpenAsync(default);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        // Native TPostDelete rejects attachments only when @@ROWCOUNT=1, allowing two or more
        // unclaimed attachments to lose their header. Preserve its return codes for ANY contents.
        await using (var guard = new SqlCommand(@"SELECT CASE
WHEN EXISTS(SELECT 1 FROM TPOSTTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwCharID=@char AND dwPostID=@post AND (dwGold>0 OR dwSilver>0 OR dwCooper>0)) THEN 1
WHEN EXISTS(SELECT 1 FROM TITEMTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwOwnerID=@char AND bOwnerType=0 AND bStorageType=2 AND dwStorageID=@post) THEN 2 ELSE 0 END", c, tx))
        {
            guard.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
            guard.Parameters.Add(SqlProc.In("@post", SqlDbType.Int, unchecked((int)postId)));
            int blocked = Convert.ToInt32(await guard.ExecuteScalarAsync());
            if (blocked != 0) return blocked;
        }
        var ret = SqlProc.Ret();
        await SqlProc.ExecAsync(c, "TPostDelete", ret, new[]
        {
            SqlProc.In("@p0", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@p1", SqlDbType.Int, unchecked((int)postId)),
        }, default, tx);
        await tx.CommitAsync();
        return ret.AsInt();
    }

    public async Task PostGetItemAsync(uint charId, uint postId)
    {
        await using var c = await OpenAsync(default);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await SqlProc.ExecAsync(c, "TPostGetItem", null, new[]
        {
            SqlProc.In("@p0", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@p1", SqlDbType.Int, unchecked((int)postId)),
        }, default, tx);
        await tx.CommitAsync();
    }

    public async Task<PostBillResult> PostBillsUpdateAsync(uint postId, string charName, byte type)
    {
        await using var c = await OpenAsync(default);
        var ret = SqlProc.Ret();
        var newId = SqlProc.Out("@p0", SqlDbType.Int);
        var sender = SqlProc.Out("@p1", SqlDbType.VarChar, 50);
        var recver = SqlProc.Out("@p2", SqlDbType.VarChar, 50);
        var title = SqlProc.Out("@p3", SqlDbType.VarChar, 256);
        await SqlProc.ExecAsync(c, "TPostBillsUpdate", ret, new[]
        {
            newId, sender, recver, title,
            SqlProc.In("@p4", SqlDbType.Int, unchecked((int)postId)),
            SqlProc.In("@p5", SqlDbType.VarChar, charName, 50),
            SqlProc.In("@p6", SqlDbType.TinyInt, type),
        }, default);
        return new PostBillResult(ret.AsInt(), newId.AsUInt(), sender.AsString(), recver.AsString(), title.AsString());
    }

    public async Task<List<(uint PostId, long TimeRecv)>> PendingBillsAsync(uint sendId)
    {
        await using var c = await OpenAsync(default);
        await using var cmd = new SqlCommand(
            "SELECT dwPostID, timeRecv FROM TPOSTTABLE WHERE dwSendID = @s AND bType = @t AND bRead = 0", c);
        cmd.Parameters.Add(SqlProc.In("@s", SqlDbType.Int, unchecked((int)sendId)));
        cmd.Parameters.Add(SqlProc.In("@t", SqlDbType.TinyInt, PostBills));
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<(uint, long)>();
        while (await r.ReadAsync()) list.Add((r.GetUIntSafe(0), ToTime64(r, 1)));
        return list;
    }

    private static long ToTime64(object? v)
    {
        if (v is null or DBNull) return 0;
        var dt = Convert.ToDateTime(v);
        if (dt.Year < 2000) return 0;
        long secs = (long)(DateTime.SpecifyKind(dt, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
        return secs < 0 ? 0 : secs;
    }
}
