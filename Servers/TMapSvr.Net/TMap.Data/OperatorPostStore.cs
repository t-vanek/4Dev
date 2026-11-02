using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TMap.Data;

// Newly created server rewards only. Existing inventory items use their own transfer contracts.
public sealed record OperatorPostIntent(Guid OperationId, uint CharId, string Receiver, string Title,
    string Message, byte Type, uint Gold, uint Silver, uint Cooper, long Time, List<ItemSaveData> Items)
{
    public void Validate()
    {
        if (OperationId == Guid.Empty || CharId == 0 || Receiver is null || Title is null || Message is null ||
            Receiver.Length > 50 || Title.Length > 256 || Message.Length > 2048 || Time <= 0 || Items is null || Items.Count > 256)
            throw new InvalidDataException("Incomplete server reward mail.");
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item.DlId != 0 || item.StorageType != 2 || item.StorageId != 0 || item.ItemSlot != i ||
                item.TemplateId == 0 || item.Count == 0 || item.Magic?.Length != 6 || item.Value?.Length != 6 || item.Ext?.Length != 6)
                throw new InvalidDataException("Server reward mail must contain complete new-item snapshots in distinct slots.");
        }
    }
}

public readonly record struct OperatorPostResult(int Result, uint PostId, uint CharId, string Receiver, bool Created);
public interface IOperatorPostStore
{
    Task VerifyOperatorPostSchemaAsync();
    Task<OperatorPostResult> DeliverOperatorPostAsync(OperatorPostIntent intent);
}

public sealed partial class GameDatabase : IOperatorPostStore
{
    public async Task VerifyOperatorPostSchemaAsync()
    {
        await using var c = await OpenAsync(default);
        await using var cmd = new SqlCommand("SELECT TOP(0) OperationId,CharId,Payload,Result,PostId,ReceiverName FROM dbo.TOperatorPostOperation", c);
        await cmd.ExecuteNonQueryAsync(); // required schema: no empty fallback on deployment mistakes
    }

    public async Task<OperatorPostResult> DeliverOperatorPostAsync(OperatorPostIntent intent)
        => await DeliverOperatorPostAsync(intent, null);

    public async Task<OperatorPostResult> DeliverOperatorPostAsync(OperatorPostIntent intent,
        Func<SqlConnection, SqlTransaction, OperatorPostResult, Task>? onCommittedMail)
    {
        intent.Validate();
        string payload = JsonSerializer.Serialize(intent);
        await using var c = await OpenAsync(default);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        // Same operation can be replayed after commit/connection loss or by another queue owner.
        // A global transaction lock also serializes native DB item-ID allocation between .NET mail writers.
        await using (var claim = new SqlCommand(@"
DECLARE @r int;
EXEC @r=sys.sp_getapplock @Resource='4Dev.OperatorPost',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @r<0 THROW 51045,'Server reward mail lock unavailable',1;", c, tx)) await claim.ExecuteNonQueryAsync();
        await using (var existing = new SqlCommand(@"SELECT CharId,Payload,Result,PostId,ReceiverName
FROM dbo.TOperatorPostOperation WITH(UPDLOCK,HOLDLOCK) WHERE OperationId=@id", c, tx))
        {
            existing.Parameters.Add(SqlProc.In("@id", SqlDbType.UniqueIdentifier, intent.OperationId));
            await using var r = await existing.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                if (r.GetUIntSafe(0) != intent.CharId || r.GetString(1) != payload)
                    throw new InvalidDataException("Server reward mail identity reused with different content.");
                return new(r.GetInt32(2), r.GetUIntSafe(3), intent.CharId, r.GetString(4), false);
            }
        }

        string? receiver;
        await using (var find = new SqlCommand("SELECT szName FROM dbo.TCHARTABLE WITH(HOLDLOCK) WHERE dwCharID=@char", c, tx))
        {
            find.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)intent.CharId)));
            receiver = await find.ExecuteScalarAsync() as string;
        }
        uint postId = 0;
        int result = receiver is null ? 1 : 0; // a deleted recipient is a durable rejection, not an orphan mail or blocked queue
        if (result == 0)
        {
            var ret = SqlProc.Ret(); var post = SqlProc.Out("@make", SqlDbType.Int); var recv = SqlProc.Out("@recv", SqlDbType.Int);
            await SqlProc.ExecAsync(c, "TSavePost", ret, new[]
            {
                post, recv, SqlProc.In("@sendId", SqlDbType.Int, 0), SqlProc.In("@char", SqlDbType.Int, unchecked((int)intent.CharId)),
                SqlProc.In("@target", SqlDbType.VarChar, receiver!, 50), SqlProc.In("@sender", SqlDbType.VarChar, "Operator", 50),
                SqlProc.In("@title", SqlDbType.VarChar, intent.Title, 256), SqlProc.In("@body", SqlDbType.VarChar, intent.Message, 2048),
                SqlProc.In("@read", SqlDbType.TinyInt, (byte)0), SqlProc.In("@type", SqlDbType.TinyInt, intent.Type),
                SqlProc.In("@gold", SqlDbType.Int, unchecked((int)intent.Gold)), SqlProc.In("@silver", SqlDbType.Int, unchecked((int)intent.Silver)),
                SqlProc.In("@cooper", SqlDbType.Int, unchecked((int)intent.Cooper)), SqlProc.In("@time", SqlDbType.SmallDateTime, FromTime64(intent.Time))
            }, default, tx);
            postId = post.AsUInt();
            if (ret.AsInt() != 0 || postId == 0 || recv.AsUInt() != intent.CharId)
                throw new IOException("Native server reward mail header was rejected.");
            foreach (var row in intent.Items)
            {
                var id = SqlProc.Out("@id", SqlDbType.BigInt);
                await SqlProc.ExecAsync(c, "TGenerateDBItemID", null, [id], default, tx);
                long itemId = Convert.ToInt64(id.Value);
                await using (var check = new SqlCommand(@"IF @id<=0 OR EXISTS(SELECT 1 FROM dbo.TITEMTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dlID=@id)
THROW 51045,'Server reward item ID is invalid or already owned',1;", c, tx))
                {
                    check.Parameters.Add(SqlProc.In("@id", SqlDbType.BigInt, itemId)); await check.ExecuteNonQueryAsync();
                }
                var item = row with { DlId = itemId, StorageId = postId };
                var itemRet = SqlProc.Ret();
                await SqlProc.ExecAsync(c, "TSaveItemDirect", itemRet, ItemArgs(unchecked((int)intent.CharId), item), default, tx);
                if (itemRet.AsInt() != 0) throw new IOException("Native server reward attachment was rejected.");
                await using var verify = new SqlCommand(@"IF NOT EXISTS(SELECT 1 FROM dbo.TITEMTABLE WHERE dlID=@id AND dwOwnerID=@char
AND bStorageType=2 AND dwStorageID=@post AND bItemID=@slot AND wItemID=@item AND bCount=@count)
THROW 51045,'Server reward attachment was not stored',1;", c, tx);
                verify.Parameters.AddRange([
                    SqlProc.In("@id", SqlDbType.BigInt, itemId), SqlProc.In("@char", SqlDbType.Int, unchecked((int)intent.CharId)),
                    SqlProc.In("@post", SqlDbType.Int, unchecked((int)postId)), SqlProc.In("@slot", SqlDbType.TinyInt, item.ItemSlot),
                    SqlProc.In("@item", SqlDbType.SmallInt, unchecked((short)item.TemplateId)), SqlProc.In("@count", SqlDbType.TinyInt, item.Count)]);
                await verify.ExecuteNonQueryAsync();
            }
            await using var contain = new SqlCommand(@"UPDATE dbo.TPOSTTABLE SET bContain=@contain WHERE dwPostID=@post AND dwCharID=@char;
IF @@ROWCOUNT<>1 THROW 51045,'Server reward mail header was not stored',1;", c, tx);
            contain.Parameters.AddRange([SqlProc.In("@contain", SqlDbType.TinyInt, intent.Items.Count == 0 ? (byte)0 : (byte)1),
                SqlProc.In("@post", SqlDbType.Int, unchecked((int)postId)), SqlProc.In("@char", SqlDbType.Int, unchecked((int)intent.CharId))]);
            await contain.ExecuteNonQueryAsync();
        }
        await using (var receipt = new SqlCommand(@"INSERT dbo.TOperatorPostOperation(OperationId,CharId,Payload,Result,PostId,ReceiverName)
VALUES(@id,@char,@payload,@result,@post,@name)", c, tx))
        {
            receipt.Parameters.AddRange([SqlProc.In("@id", SqlDbType.UniqueIdentifier, intent.OperationId),
                SqlProc.In("@char", SqlDbType.Int, unchecked((int)intent.CharId)), SqlProc.In("@payload", SqlDbType.NVarChar, payload, -1),
                SqlProc.In("@result", SqlDbType.Int, result), SqlProc.In("@post", SqlDbType.Int, unchecked((int)postId)),
                SqlProc.In("@name", SqlDbType.VarChar, receiver ?? "", 50)]);
            await receipt.ExecuteNonQueryAsync();
        }
        if (result == 0 && onCommittedMail is not null)
            await onCommittedMail(c, tx, new(result, postId, intent.CharId, receiver!, true));
        await tx.CommitAsync();
        return new(result, postId, intent.CharId, receiver ?? "", result == 0);
    }
}
