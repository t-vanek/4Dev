using System.Data;
using Microsoft.Data.SqlClient;

namespace TWorld.Data;

public readonly record struct ReservedPostResult(int Result, uint PostId, string Sender, string Receiver, string Title);

public interface IReservedPostStore
{
    Task<ReservedPostResult> DeliverReservedPostAsync();
}

public sealed partial class GameDatabase : IReservedPostStore
{
    public async Task<ReservedPostResult> DeliverReservedPostAsync()
    {
        await using var c = await OpenAsync(default);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        // Legacy TGetReservedPost has no transaction/claim lock. Serialize all .NET World consumers
        // and bracket the mail, item ID allocation, attachment and bSend update in one transaction.
        await using (var claim = new SqlCommand(@"
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource='4Dev.ReservedPost', @LockMode='Exclusive',
    @LockOwner='Transaction', @LockTimeout=0;
SELECT @lock;", c, tx))
            if (Convert.ToInt32(await claim.ExecuteScalarAsync()) < 0)
                return new(1, 0, "", "", "");

        // Native DBAccess.h CSPGetReservedPost: 38 positional output parameters.
        var types = new[] { SqlDbType.Int, SqlDbType.Int, SqlDbType.Int, SqlDbType.VarChar, SqlDbType.VarChar,
            SqlDbType.VarChar, SqlDbType.VarChar, SqlDbType.BigInt, SqlDbType.TinyInt, SqlDbType.SmallInt,
            SqlDbType.TinyInt, SqlDbType.TinyInt, SqlDbType.TinyInt, SqlDbType.Int, SqlDbType.Int,
            SqlDbType.TinyInt, SqlDbType.SmallDateTime, SqlDbType.TinyInt };
        var args = new List<SqlParameter>();
        for (int i = 0; i < types.Length; i++)
            args.Add(SqlProc.Out($"@p{i}", types[i], i is 3 or 4 ? 50 : i == 5 ? 256 : i == 6 ? 2048 : 0));
        for (int i = 18; i < 38; i++)
            args.Add(SqlProc.Out($"@p{i}", i < 24 || i == 36 ? SqlDbType.TinyInt : i < 30 || i == 37 ? SqlDbType.SmallInt : SqlDbType.Int));
        var ret = SqlProc.Ret();
        string sql = "EXEC @__ret=dbo.TGetReservedPost " + string.Join(",", args.Select(p => p.ParameterName + " OUTPUT"));
        await using (var cmd = new SqlCommand(sql, c, tx))
        {
            cmd.Parameters.Add(ret); cmd.Parameters.AddRange(args.ToArray());
            await cmd.ExecuteNonQueryAsync();
        }
        int result = ret.AsInt();
        if (result == 0)
        {
            // The inherited proc ignores TSaveItemDirect's return value. Never mark a reward delivered
            // unless both rows really exist; throwing rolls the reservation back for a subsequent tick.
            await using var verify = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM TPOSTTABLE WHERE dwPostID=@post AND dwCharID=@receiver)
 OR NOT EXISTS (SELECT 1 FROM TITEMTABLE WHERE dlID=@item AND bStorageType=2 AND dwStorageID=@post
    AND dwOwnerID=@receiver)
THROW 51000, 'Reserved post attachment was not saved', 1;
UPDATE TPOSTTABLE SET bContain=1 WHERE dwPostID=@post AND dwCharID=@receiver;", c, tx);
            verify.Parameters.Add(SqlProc.In("@post", SqlDbType.Int, unchecked((int)args[0].AsUInt())));
            verify.Parameters.Add(SqlProc.In("@receiver", SqlDbType.Int, unchecked((int)args[2].AsUInt())));
            verify.Parameters.Add(SqlProc.In("@item", SqlDbType.BigInt, args[7].Value));
            await verify.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync(); // also retain bSend=2 for invalid recipients, as in the native worker
        return new(result, args[0].AsUInt(), args[3].AsString(), args[4].AsString(), args[5].AsString());
    }
}
