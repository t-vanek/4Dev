using System.Data;
using Microsoft.Data.SqlClient;

namespace TLogin.Data;

/// <summary>Account-wide OTP state. All deadlines use the database clock; conditional writes prevent
/// replay and races across connections or Login processes. SQL errors never mean "2FA disabled".</summary>
public sealed class SqlTwoFactorStore(string connectionString) : ITwoFactorStore
{
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task ValidateSchemaAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("SELECT TOP(0) dwUserID, szEmail, bEnabled, SessionID, ChallengeID, CodeHash, ExpiresUtc, Delivered, VerifiedUntilUtc, FailedAttempts, AttemptWindowUtc, LockedUntilUtc, LastSentUtc, SendWindowUtc, SentCount FROM dbo.TLOGIN2FA", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsRequiredAsync(uint userId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, "SELECT 1 FROM dbo.TLOGIN2FA WHERE dwUserID=@user AND bEnabled=1", userId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<string?> TryIssueAsync(uint userId, Guid sessionId, Guid challengeId, byte[] codeHash,
        TwoFactorLimits limits, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            DECLARE @now datetime2 = SYSUTCDATETIME();
            UPDATE dbo.TLOGIN2FA WITH (UPDLOCK)
            SET SessionID=@session, ChallengeID=@challenge, CodeHash=@hash, Delivered=0,
                ExpiresUtc=DATEADD(second,@ttl,@now), VerifiedUntilUtc=NULL,
                FailedAttempts=CASE WHEN AttemptWindowUtc IS NULL OR AttemptWindowUtc<=DATEADD(second,-@lockout,@now) THEN 0 ELSE FailedAttempts END,
                AttemptWindowUtc=CASE WHEN AttemptWindowUtc IS NULL OR AttemptWindowUtc<=DATEADD(second,-@lockout,@now) THEN @now ELSE AttemptWindowUtc END,
                LockedUntilUtc=NULL, LastSentUtc=@now,
                SentCount=CASE WHEN SendWindowUtc IS NULL OR SendWindowUtc<=DATEADD(hour,-1,@now) THEN 1 ELSE SentCount+1 END,
                SendWindowUtc=CASE WHEN SendWindowUtc IS NULL OR SendWindowUtc<=DATEADD(hour,-1,@now) THEN @now ELSE SendWindowUtc END
            OUTPUT inserted.szEmail
            WHERE dwUserID=@user AND bEnabled=1
                AND (LockedUntilUtc IS NULL OR LockedUntilUtc<=@now)
                AND (LastSentUtc IS NULL OR LastSentUtc<=DATEADD(second,-@resend,@now))
                AND (SendWindowUtc IS NULL OR SendWindowUtc<=DATEADD(hour,-1,@now) OR SentCount<@maxSends);
            """, userId, sessionId, challengeId, codeHash);
        Add(command, "@ttl", limits.CodeLifetimeSeconds);
        Add(command, "@lockout", limits.LockoutSeconds);
        Add(command, "@resend", limits.ResendDelaySeconds);
        Add(command, "@maxSends", limits.MaxSendsPerHour);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<bool> MarkDeliveredAsync(uint userId, Guid sessionId, Guid challengeId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            UPDATE dbo.TLOGIN2FA SET Delivered=1 WHERE dwUserID=@user AND bEnabled=1
                AND SessionID=@session AND ChallengeID=@challenge AND CodeHash IS NOT NULL
                AND ExpiresUtc>SYSUTCDATETIME();
            """, userId, sessionId, challengeId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> VerifyAsync(uint userId, Guid sessionId, Guid challengeId, byte[] codeHash,
        TwoFactorLimits limits, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            SET XACT_ABORT ON;
            DECLARE @now datetime2=SYSUTCDATETIME(), @stored binary(32), @attempts int, @accepted bit=0;
            BEGIN TRANSACTION;
            SELECT @stored=CodeHash, @attempts=FailedAttempts FROM dbo.TLOGIN2FA WITH (UPDLOCK, HOLDLOCK)
            WHERE dwUserID=@user AND bEnabled=1 AND SessionID=@session AND ChallengeID=@challenge
                AND Delivered=1 AND CodeHash IS NOT NULL AND ExpiresUtc>@now
                AND (LockedUntilUtc IS NULL OR LockedUntilUtc<=@now);
            IF @stored IS NOT NULL
            BEGIN
                IF @stored=@hash
                BEGIN
                    UPDATE dbo.TLOGIN2FA SET CodeHash=NULL, Delivered=0, ExpiresUtc=NULL,
                        VerifiedUntilUtc=DATEADD(second,@grant,@now), FailedAttempts=0,
                        AttemptWindowUtc=NULL, LockedUntilUtc=NULL WHERE dwUserID=@user;
                    SET @accepted=1;
                END
                ELSE
                BEGIN
                    SET @attempts=@attempts+1;
                    UPDATE dbo.TLOGIN2FA SET FailedAttempts=@attempts,
                        LockedUntilUtc=CASE WHEN @attempts>=@maxAttempts THEN DATEADD(second,@lockout,@now) ELSE NULL END,
                        CodeHash=CASE WHEN @attempts>=@maxAttempts THEN NULL ELSE CodeHash END,
                        VerifiedUntilUtc=NULL WHERE dwUserID=@user;
                END;
            END;
            COMMIT;
            SELECT @accepted;
            """, userId, sessionId, challengeId, codeHash);
        Add(command, "@grant", limits.GrantLifetimeSeconds);
        Add(command, "@maxAttempts", limits.MaxAttempts);
        Add(command, "@lockout", limits.LockoutSeconds);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public async Task<bool> ConsumeAsync(uint userId, Guid sessionId, Guid challengeId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            UPDATE dbo.TLOGIN2FA SET SessionID=NULL, ChallengeID=NULL, CodeHash=NULL,
                ExpiresUtc=NULL, VerifiedUntilUtc=NULL, Delivered=0
            WHERE dwUserID=@user AND bEnabled=1 AND SessionID=@session AND ChallengeID=@challenge
                AND VerifiedUntilUtc>SYSUTCDATETIME();
            """, userId, sessionId, challengeId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task InvalidateAsync(uint userId, Guid sessionId, Guid challengeId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, """
            UPDATE dbo.TLOGIN2FA SET SessionID=NULL, ChallengeID=NULL, CodeHash=NULL,
                ExpiresUtc=NULL, VerifiedUntilUtc=NULL, Delivered=0
            WHERE dwUserID=@user AND SessionID=@session AND ChallengeID=@challenge;
            """, userId, sessionId, challengeId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static SqlCommand Command(SqlConnection connection, string sql, uint userId,
        Guid? sessionId = null, Guid? challengeId = null, byte[]? codeHash = null)
    {
        var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@user", SqlDbType.Int) { Value = unchecked((int)userId) });
        if (sessionId is not null) command.Parameters.Add(new SqlParameter("@session", SqlDbType.UniqueIdentifier) { Value = sessionId.Value });
        if (challengeId is not null) command.Parameters.Add(new SqlParameter("@challenge", SqlDbType.UniqueIdentifier) { Value = challengeId.Value });
        if (codeHash is not null)
        {
            if (codeHash.Length != 32) throw new ArgumentException("OTP hashes must be SHA-256.", nameof(codeHash));
            command.Parameters.Add(new SqlParameter("@hash", SqlDbType.Binary, 32) { Value = codeHash });
        }
        return command;
    }

    private static void Add(SqlCommand command, string name, int value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Int) { Value = value });
}
