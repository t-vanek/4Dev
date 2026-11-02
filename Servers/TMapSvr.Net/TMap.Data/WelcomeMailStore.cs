using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TMap.Data;

public sealed record WelcomeMailItem(ushort ItemId, int Count);
public sealed record WelcomeMailSettings(bool Enabled, string Title, string Message, List<WelcomeMailItem> Items);
public sealed record WelcomeMailIdentity(uint CharId, Guid OperationId, string Receiver, long Time);
public interface IWelcomeMailStore
{
    Task<WelcomeMailSettings> VerifyWelcomeMailStorageAsync();
    Task<OperatorPostIntent?> PrepareWelcomeMailAsync(uint charId, Func<WelcomeMailSettings, WelcomeMailIdentity, OperatorPostIntent> build);
    Task<IReadOnlyList<OperatorPostIntent>> PendingWelcomeMailAsync();
    Task MarkWelcomeMailDispatchedAsync(Guid operationId);
}

public sealed partial class GameDatabase : IWelcomeMailStore
{
    private static async Task<WelcomeMailSettings> ReadWelcomeSettings(SqlConnection c, SqlTransaction? tx)
    {
        bool enabled; string title, message;
        await using (var cmd = new SqlCommand("SELECT Enabled,Title,Message FROM dbo.TWelcomeMailSettings WITH(HOLDLOCK) WHERE SettingsId=1", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync()) throw new InvalidDataException("Welcome mail settings are missing.");
            enabled = r.GetBoolean(0); title = r.GetString(1); message = r.GetString(2);
        }
        var items = new List<WelcomeMailItem>();
        await using (var cmd = new SqlCommand("SELECT ItemId,ItemCount FROM dbo.TWelcomeMailItem WITH(HOLDLOCK) ORDER BY ItemId", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) items.Add(new(checked((ushort)r.GetInt32(0)), r.GetInt32(1)));
        return new(enabled, title, message, items);
    }

    public async Task<WelcomeMailSettings> VerifyWelcomeMailStorageAsync()
    {
        await using var c = await OpenAsync(default);
        await using (var cmd = new SqlCommand(@"
SELECT TOP(0) CharId,OperationId,FirstEnteredAt,Payload,DispatchedAt FROM dbo.TCharacterWelcomeMail;
IF NOT EXISTS(SELECT 1 FROM sys.triggers WHERE object_id=OBJECT_ID(N'dbo.TR_TCharacterWelcomeMail_Register') AND is_disabled=0)
    THROW 51048,'New-character welcome registration trigger is unavailable',1;", c)) await cmd.ExecuteNonQueryAsync();
        return await ReadWelcomeSettings(c, null);
    }

    public async Task<OperatorPostIntent?> PrepareWelcomeMailAsync(uint charId,
        Func<WelcomeMailSettings, WelcomeMailIdentity, OperatorPostIntent> build)
    {
        await using var c = await OpenAsync(default);
        // HOLDLOCK on the identity/settings reads provides range stability without leaving a
        // pooled connection at SERIALIZABLE for unrelated SQL transport READPAST operations.
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        Guid id; string? payload; bool entered, dispatched;
        await using (var cmd = new SqlCommand(@"SELECT OperationId,FirstEnteredAt,Payload,DispatchedAt
FROM dbo.TCharacterWelcomeMail WITH(UPDLOCK,HOLDLOCK) WHERE CharId=@char", c, tx))
        {
            cmd.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null; // existing characters are never enrolled by login
            id = r.GetGuid(0); entered = !r.IsDBNull(1); payload = r.IsDBNull(2) ? null : r.GetString(2); dispatched = !r.IsDBNull(3);
        }
        if (dispatched || entered && payload is null) return null;
        if (payload is not null) return DecodeWelcome(payload, charId, id);
        string? receiver;
        await using (var cmd = new SqlCommand("SELECT szName FROM dbo.TCHARTABLE WITH(HOLDLOCK) WHERE dwCharID=@char AND bDelete=0", c, tx))
        {
            cmd.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
            receiver = await cmd.ExecuteScalarAsync() as string;
        }
        if (receiver is null) return null;
        var settings = await ReadWelcomeSettings(c, tx);
        var intent = settings.Enabled ? build(settings, new(charId, id, receiver, DateTimeOffset.UtcNow.ToUnixTimeSeconds())) : null;
        if (intent is not null)
        {
            intent.Validate();
            if (intent.CharId != charId || intent.OperationId != id) throw new InvalidDataException("Welcome mail identity mismatch.");
            payload = JsonSerializer.Serialize(intent);
        }
        await using (var cmd = new SqlCommand(@"UPDATE dbo.TCharacterWelcomeMail
SET FirstEnteredAt=SYSUTCDATETIME(),Payload=@payload WHERE CharId=@char AND FirstEnteredAt IS NULL;
IF @@ROWCOUNT<>1 THROW 51048,'Welcome mail entry changed during preparation',1;", c, tx))
        {
            cmd.Parameters.Add(SqlProc.In("@char", SqlDbType.Int, unchecked((int)charId)));
            cmd.Parameters.Add(SqlProc.In("@payload", SqlDbType.NVarChar, (object?)payload ?? DBNull.Value, -1));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync(); return intent;
    }

    private static OperatorPostIntent DecodeWelcome(string payload, uint charId, Guid id)
    {
        var intent = JsonSerializer.Deserialize<OperatorPostIntent>(payload) ?? throw new InvalidDataException("Empty welcome mail payload.");
        intent.Validate();
        if (intent.CharId != charId || intent.OperationId != id) throw new InvalidDataException("Stored welcome mail identity mismatch.");
        return intent;
    }

    public async Task<IReadOnlyList<OperatorPostIntent>> PendingWelcomeMailAsync()
    {
        await using var c = await OpenAsync(default);
        await using var cmd = new SqlCommand(@"SELECT TOP(100) CharId,OperationId,Payload FROM dbo.TCharacterWelcomeMail
WHERE Payload IS NOT NULL AND DispatchedAt IS NULL ORDER BY CharId", c);
        await using var r = await cmd.ExecuteReaderAsync(); var result = new List<OperatorPostIntent>();
        while (await r.ReadAsync()) result.Add(DecodeWelcome(r.GetString(2), r.GetUIntSafe(0), r.GetGuid(1)));
        return result;
    }

    public async Task MarkWelcomeMailDispatchedAsync(Guid operationId)
    {
        await using var c = await OpenAsync(default);
        await using var cmd = new SqlCommand(@"UPDATE dbo.TCharacterWelcomeMail SET DispatchedAt=COALESCE(DispatchedAt,SYSUTCDATETIME())
WHERE OperationId=@id AND Payload IS NOT NULL;
IF @@ROWCOUNT<>1 THROW 51048,'Prepared welcome mail record missing',1;", c);
        cmd.Parameters.Add(SqlProc.In("@id", SqlDbType.UniqueIdentifier, operationId)); await cmd.ExecuteNonQueryAsync();
    }
}
