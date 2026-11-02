using System.Data;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace TMap.Data;

public readonly record struct QuestPostResult(int Result, byte Count)
{
    public const int AlreadyIssued = 3;
}

public interface IQuestPostStore
{
    /// <summary>Reserve attachments and completed progress atomically. 0 issued, 1 missing character,
    /// 2 missing mail text, 3 chart CountMax reached. Count is the durable completed count.</summary>
    Task<QuestPostResult> ReserveQuestPostAsync(uint questId, uint charId, IReadOnlyList<ItemSaveData> items, byte countMax = 0);
}

public sealed partial class GameDatabase : IQuestPostStore
{
    public Task<QuestPostResult> ReserveQuestPostAsync(uint questId, uint charId, IReadOnlyList<ItemSaveData> items, byte countMax = 0)
        => ReserveQuestPostCoreAsync(questId, charId, items, countMax, null);

    private async Task<QuestPostResult> ReserveQuestPostCoreAsync(uint questId, uint charId, IReadOnlyList<ItemSaveData> items, byte countMax, QuestPostIntent? intent)
    {
        if (items.Count == 0) return new(0, 0);
        await using var c = await OpenAsync(default);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        string? payload = intent is null ? null : JsonSerializer.Serialize(intent);
        if (intent is not null)
        {
            // The identity is frozen in the SQL save queue BEFORE reservation. Replaying an ambiguous commit,
            // including an uncapped reward, must find this operation instead of reserving it again.
            await using var existing = new SqlCommand(@"SELECT Payload,Result,IssuedCount FROM dbo.TQuestPostOperation WITH (UPDLOCK,HOLDLOCK) WHERE OperationId=@id", c, tx);
            existing.Parameters.Add(SqlProc.In("@id", SqlDbType.UniqueIdentifier, intent.OperationId));
            await using var r = await existing.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                if (r.GetString(0) != payload) throw new InvalidDataException("Quest post identity reused with different data.");
                return new(r.GetInt32(1), r.GetByte(2));
            }
        }
        async Task<QuestPostResult> Commit(int result, byte count)
        {
            if (intent is not null)
            {
                await using var op = new SqlCommand(@"INSERT dbo.TQuestPostOperation(OperationId,CharId,Payload,Result,IssuedCount,Completed)
VALUES(@id,@char,@payload,@result,@count,@done)", c, tx);
                op.Parameters.Add(SqlProc.In("@id", SqlDbType.UniqueIdentifier, intent.OperationId));
                op.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
                op.Parameters.Add(SqlProc.In("@payload", SqlDbType.NVarChar, payload!, -1));
                op.Parameters.Add(SqlProc.In("@result", SqlDbType.Int, result));
                op.Parameters.Add(SqlProc.In("@count", SqlDbType.TinyInt, count));
                op.Parameters.Add(SqlProc.In("@done", SqlDbType.Bit, result != 0));
                await op.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return new(result, count);
        }
        int completed;
        await using (var counter = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM TCHARTABLE WHERE dwCharID=@char) BEGIN SELECT -1; RETURN; END;
SELECT COALESCE((SELECT CONVERT(int, CASE WHEN bTriggerCount>bCompleteCount THEN bTriggerCount ELSE bCompleteCount END)
    FROM TQUESTTABLE WITH (UPDLOCK,HOLDLOCK) WHERE dwCharID=@char AND dwQuestID=@quest),0);", c, tx))
        {
            counter.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
            counter.Parameters.Add(SqlProc.In("@quest", SqlDbType.Int, unchecked((int)questId)));
            completed = Convert.ToInt32(await counter.ExecuteScalarAsync());
        }
        if (completed < 0) return await Commit(1, 0);
        if (countMax != 0 && completed >= countMax) return await Commit(QuestPostResult.AlreadyIssued, (byte)completed);

        // Same chart text / length-prefixed strings as TQuestSendPost. Unlike that legacy proc, retain
        // grade effect, gem and appearance and commit the whole reward, rather than each stack separately.
        foreach (var item in items)
        {
            await using var cmd = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM TCHARTABLE WHERE dwCharID=@char) BEGIN SELECT 1; RETURN; END;
IF NOT EXISTS (SELECT 1 FROM TQTITLECHART WHERE dwQuestID=@quest
    AND szTitle IS NOT NULL AND szMessage IS NOT NULL AND szNPCName IS NOT NULL)
BEGIN SELECT 2; RETURN; END;
INSERT INTO TRESERVEDPOST (dwRecverID,szSender,szTitle,szMessage,bSend,wItemID,bLevel,bCount,bGLevel,
    dwDuraMax,dwDuraCur,bRefineCur,dEndTime,bGradeEffect,
    bMagic1,bMagic2,bMagic3,bMagic4,bMagic5,bMagic6,wValue1,wValue2,wValue3,wValue4,wValue5,wValue6,
    dwTime1,dwTime2,dwTime3,dwTime4,dwTime5,dwTime6,bGem,wMoggItemID)
SELECT @char,CAST(szNPCName AS varchar(255)),
    RIGHT(master.dbo.fn_sqlvarbasetostr(CONVERT(binary(4),DATALENGTH(CAST(szTitle AS varchar(255))))),8)+CAST(szTitle AS varchar(255)),
    RIGHT(master.dbo.fn_sqlvarbasetostr(CONVERT(binary(4),DATALENGTH(CAST(szMessage AS varchar(1024))))),8)+CAST(szMessage AS varchar(1024)),
    0,@item,@level,@count,@grade,@max,@cur,@refine,@end,@effect,
    @m0,@m1,@m2,@m3,@m4,@m5,@v0,@v1,@v2,@v3,@v4,@v5,@e0,@e1,@e2,@e3,@e4,@e5,@gem,@mogg
FROM TQTITLECHART WHERE dwQuestID=@quest;
SELECT 0;", c, tx);
            cmd.Parameters.AddRange(new[]
            {
                SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)),
                SqlProc.In("@quest", SqlDbType.Int, unchecked((int)questId)),
                SqlProc.In("@item", SqlDbType.SmallInt, unchecked((short)item.TemplateId)),
                SqlProc.In("@level", SqlDbType.TinyInt, item.Level), SqlProc.In("@count", SqlDbType.TinyInt, item.Count),
                SqlProc.In("@grade", SqlDbType.TinyInt, item.GLevel),
                SqlProc.In("@max", SqlDbType.Int, unchecked((int)item.DuraMax)),
                SqlProc.In("@cur", SqlDbType.Int, unchecked((int)item.DuraCur)),
                SqlProc.In("@refine", SqlDbType.TinyInt, item.RefineCur),
                SqlProc.In("@end", SqlDbType.SmallDateTime, FromTime64(item.EndTime)),
                SqlProc.In("@effect", SqlDbType.TinyInt, item.GradeEffect),
                SqlProc.In("@gem", SqlDbType.TinyInt, item.Gem),
                SqlProc.In("@mogg", SqlDbType.SmallInt, unchecked((short)item.MoggItemId)),
            });
            for (int i = 0; i < 6; i++)
            {
                cmd.Parameters.Add(SqlProc.In($"@m{i}", SqlDbType.TinyInt, i < item.Magic.Length ? item.Magic[i] : (byte)0));
                cmd.Parameters.Add(SqlProc.In($"@v{i}", SqlDbType.SmallInt, unchecked((short)(i < item.Value.Length ? item.Value[i] : 0))));
                cmd.Parameters.Add(SqlProc.In($"@e{i}", SqlDbType.Int, unchecked((int)(i < item.Ext.Length ? item.Ext[i] : 0))));
            }
            int result = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (result != 0) return new(result, (byte)completed); // disposal rolls every earlier attachment back
        }
        // CountMax is already part of the chart/CanRunQuest contract. Keep a completed SendPost entry
        // in the ordinary quest table so replay protection also survives a new process/connection.
        byte nextCount = (byte)(completed == byte.MaxValue ? 1 : completed + 1);
        await using (var counter = new SqlCommand(@"
UPDATE TQUESTTABLE SET dwTick=0,bTriggerCount=@count,bCompleteCount=@count WHERE dwCharID=@char AND dwQuestID=@quest;
IF @@ROWCOUNT=0 INSERT TQUESTTABLE(dwCharID,dwQuestID,dwTick,bTriggerCount,bCompleteCount) VALUES(@char,@quest,0,@count,@count);", c, tx))
        {
            counter.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
            counter.Parameters.Add(SqlProc.In("@quest", SqlDbType.Int, unchecked((int)questId)));
            counter.Parameters.Add(SqlProc.In("@count", SqlDbType.TinyInt, nextCount));
            await counter.ExecuteNonQueryAsync();
        }
        return await Commit(0, nextCount);
    }
}
