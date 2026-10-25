using System.Data;
using Microsoft.Data.SqlClient;

namespace TMap.Data;

public readonly record struct CustomCloakRow(ushort Id, long CreatedAt);

public interface ICloakStore
{
    Task<List<CustomCloakRow>> LoadCustomCloaksAsync(uint userId);
    /// <summary>Atomically checks ownership, changes the equipped item and spends 4000 medals. Null means refusal.</summary>
    Task<uint?> ChangeCustomCloakAsync(uint userId, uint charId, long itemId, ushort cloakId);
    Task<uint?> RedeemMedalItemAsync(uint charId, long itemId, byte count, uint medals);
}

public sealed partial class GameDatabase : ICloakStore
{
    public async Task<uint?> RedeemMedalItemAsync(uint charId, long itemId, byte count, uint medals)
    {
        if (count == 0 || medals == 0) return null;
        await using var c = await OpenAsync(default);
        await using var cmd = new SqlCommand(@"SET XACT_ABORT ON; BEGIN TRAN;
            DECLARE @old bigint, @balance bigint;
            SELECT @old=dwMedals FROM TMEDALS WITH (UPDLOCK, HOLDLOCK) WHERE dwCharID=@c;
            SET @balance=ISNULL(@old,0)+@amount;
            IF @balance>2147483647 BEGIN ROLLBACK; RETURN; END;
            UPDATE TITEMTABLE SET bCount=bCount-@count WHERE dlID=@item AND dwOwnerID=@c AND bOwnerType=0
                AND bStorageType=0 AND bCount>=@count;
            IF @@ROWCOUNT<>1 BEGIN ROLLBACK; RETURN; END;
            DELETE TITEMTABLE WHERE dlID=@item AND bCount=0;
            UPDATE TMEDALS SET dwMedals=@balance WHERE dwCharID=@c;
            IF @@ROWCOUNT=0 INSERT TMEDALS(dwCharID,dwMedals) VALUES(@c,@balance);
            COMMIT; SELECT @balance;", c);
        cmd.Parameters.AddRange(new[] { SqlProc.In("@c", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@item", SqlDbType.BigInt, itemId), SqlProc.In("@count", SqlDbType.Int, (int)count),
            SqlProc.In("@amount", SqlDbType.BigInt, (long)medals) });
        object? value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToUInt32(value);
    }

    public async Task<List<CustomCloakRow>> LoadCustomCloaksAsync(uint userId)
    {
        await using var c = await OpenAsync(default);
        await using var cmd = new SqlCommand("SELECT wID, dDateCreate FROM TCUSTOMCLOAKTABLE WHERE dwUserID=@u ORDER BY wID", c);
        cmd.Parameters.Add(SqlProc.In("@u", SqlDbType.Int, unchecked((int)userId)));
        await using var r = await cmd.ExecuteReaderAsync();
        var result = new List<CustomCloakRow>();
        while (await r.ReadAsync()) result.Add(new CustomCloakRow(r.GetUShortSafe(0), ToTime64(r.GetValue(1))));
        return result;
    }

    public async Task<uint?> ChangeCustomCloakAsync(uint userId, uint charId, long itemId, ushort cloakId)
    {
        await using var c = await OpenAsync(default);
        // All checks and both writes share the transaction; a rejected texture never consumes medals (a C++ bug).
        await using var cmd = new SqlCommand(@"SET XACT_ABORT ON; BEGIN TRAN;
            IF (@tex<>0 AND NOT EXISTS(SELECT 1 FROM TCUSTOMCLOAKTABLE WHERE dwUserID=@u AND wID=@ownedTex))
                OR NOT EXISTS(SELECT 1 FROM TCHARTABLE WHERE dwCharID=@c AND dwUserID=@u AND bDelete=0)
                BEGIN ROLLBACK; RETURN; END;
            UPDATE TMEDALS WITH (UPDLOCK) SET dwMedals=dwMedals-4000 WHERE dwCharID=@c AND dwMedals>=4000;
            IF @@ROWCOUNT<>1 BEGIN ROLLBACK; RETURN; END;
            UPDATE TITEMTABLE SET dwTime6=@tex WHERE dlID=@item AND dwOwnerID=@c AND bOwnerType=0
                AND bStorageType=0 AND dwStorageID=254 AND bItemID=4 AND bCount>0;
            IF @@ROWCOUNT<>1 BEGIN ROLLBACK; RETURN; END;
            SELECT dwMedals FROM TMEDALS WHERE dwCharID=@c; COMMIT;", c);
        cmd.Parameters.AddRange(new[] {
            SqlProc.In("@u", SqlDbType.Int, unchecked((int)userId)), SqlProc.In("@c", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@item", SqlDbType.BigInt, itemId), SqlProc.In("@tex", SqlDbType.Int, (int)cloakId),
            SqlProc.In("@ownedTex", SqlDbType.SmallInt, unchecked((short)cloakId)),
        });
        object? value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToUInt32(value);
    }
}
