using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TMap.Data;

public sealed record QuestPostIntent(Guid OperationId, uint QuestId, uint CharId, byte CountMax,
    uint Target, ushort MapId, byte Channel, float X, float Y, float Z, string ChartHash,
    List<uint> Ancestors, List<ItemSaveData> Items, int Version = 1)
{
    public void Validate()
    {
        if (Version != 1 || OperationId == Guid.Empty || CharId == 0 || QuestId == 0 ||
            ChartHash is not { Length: 64 } || Ancestors is null || Ancestors.Count > 64 ||
            Items is not { Count: > 0 } || !float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Z))
            throw new InvalidDataException("Invalid quest post operation.");
    }
}

public sealed record QuestPostOperation(QuestPostIntent Intent, int Result, byte Count, bool Completed);

public interface IQuestPostOperationStore : IQuestPostStore
{
    Task<QuestPostResult> ReserveQuestPostAsync(QuestPostIntent intent);
    Task<IReadOnlyList<QuestPostOperation>> LoadQuestPostOperationsAsync(uint charId);
    Task AcknowledgeQuestPostOperationsAsync(uint charId, IReadOnlyList<Guid> operations);
}

public sealed partial class GameDatabase : IQuestPostOperationStore
{
    public Task<QuestPostResult> ReserveQuestPostAsync(QuestPostIntent intent)
    {
        intent.Validate();
        return ReserveQuestPostCoreAsync(intent.QuestId, intent.CharId, intent.Items, intent.CountMax, intent);
    }

    public async Task<IReadOnlyList<QuestPostOperation>> LoadQuestPostOperationsAsync(uint charId)
    {
        await using var c = await OpenAsync(default);
        await using var cmd = new SqlCommand(@"SELECT TOP(128) OperationId,Payload,Result,IssuedCount
FROM dbo.TQuestPostOperation WHERE CharId=@char AND Completed=0 ORDER BY Sequence", c);
        cmd.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<QuestPostOperation>();
        while (await r.ReadAsync())
        {
            var intent = JsonSerializer.Deserialize<QuestPostIntent>(r.GetString(1))
                ?? throw new InvalidDataException("Empty quest post operation.");
            intent.Validate();
            if (intent.OperationId != r.GetGuid(0) || intent.CharId != charId || r.GetInt32(2) != 0)
                throw new InvalidDataException("Quest post operation identity mismatch.");
            rows.Add(new(intent, 0, r.GetByte(3), false));
        }
        return rows;
    }

    public async Task AcknowledgeQuestPostOperationsAsync(uint charId, IReadOnlyList<Guid> operations)
    {
        if (operations.Count == 0) return;
        await using var c = await OpenAsync(default);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        foreach (var id in operations)
        {
            await using var cmd = new SqlCommand(@"UPDATE dbo.TQuestPostOperation SET Completed=1
WHERE OperationId=@id AND CharId=@char AND Result=0;
IF @@ROWCOUNT<>1 THROW 51042,'Missing quest post continuation receipt',1;", c, tx);
            cmd.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
            cmd.Parameters.Add(SqlProc.In("@id", SqlDbType.UniqueIdentifier, id));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
