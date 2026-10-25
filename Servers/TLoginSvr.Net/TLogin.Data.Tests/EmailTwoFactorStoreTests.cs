using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TLogin.Data.Tests;

public sealed class TwoFactorSqlFactAttribute : FactAttribute
{
    public TwoFactorSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TLOGIN_2FA_TEST_ADMIN")))
            Skip = "Set TLOGIN_2FA_TEST_ADMIN to run isolated SQL two-factor tests.";
    }
}

public sealed class TwoFactorSqlDatabase : IAsyncLifetime
{
    private readonly string _name = "CodexLogin2FA_" + Guid.NewGuid().ToString("N");
    private string? _admin;
    private bool _created;
    public string ConnectionString { get; private set; } = "";
    public SqlTwoFactorStore Store => new(ConnectionString);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("TLOGIN_2FA_TEST_ADMIN");
        if (string.IsNullOrWhiteSpace(configured)) return;
        _admin = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }.ConnectionString;
        await ExecuteAsync(_admin, $"CREATE DATABASE [{_name}]"); _created = true;
        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = _name }.ConnectionString;
        await ExecuteAsync(ConnectionString, "CREATE TABLE dbo.TACCOUNT_PW(dwUserID int IDENTITY PRIMARY KEY, szUserID varchar(50) NOT NULL, szPasswd varchar(50) NOT NULL)");
        string migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "025_login_email_two_factor.sql"));
        migration = migration.Replace("USE [TGlobal_gsp];", "", StringComparison.Ordinal);
        for (int pass = 0; pass < 2; pass++)
            foreach (string batch in Regex.Split(migration, @"(?im)^\s*GO\s*$"))
                if (!string.IsNullOrWhiteSpace(batch)) await ExecuteAsync(ConnectionString, batch);
        await Store.ValidateSchemaAsync();
    }

    public async Task<uint> AccountAsync()
    {
        await using var c = new SqlConnection(ConnectionString); await c.OpenAsync();
        await using var cmd = new SqlCommand("INSERT dbo.TACCOUNT_PW(szUserID,szPasswd) OUTPUT inserted.dwUserID VALUES(@name,'password')", c);
        cmd.Parameters.AddWithValue("@name", "test-" + Guid.NewGuid().ToString("N"));
        uint user = Convert.ToUInt32(await cmd.ExecuteScalarAsync());
        await ConfigureAsync(user, "owner@example.test"); return user;
    }

    public async Task ConfigureAsync(uint user, string email, bool enabled = true)
    {
        await using var c = new SqlConnection(ConnectionString); await c.OpenAsync();
        await using var cmd = new SqlCommand("EXEC dbo.TLoginTwoFactorConfigure @user,@email,@enabled", c);
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.Int) { Value = unchecked((int)user) });
        cmd.Parameters.Add(new SqlParameter("@email", SqlDbType.NVarChar, 254) { Value = email });
        cmd.Parameters.Add(new SqlParameter("@enabled", SqlDbType.Bit) { Value = enabled });
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task EditAsync(uint user, string assignment)
    {
        await using var c = new SqlConnection(ConnectionString); await c.OpenAsync();
        await using var cmd = new SqlCommand("UPDATE dbo.TLOGIN2FA SET " + assignment + " WHERE dwUserID=@user", c);
        cmd.Parameters.AddWithValue("@user", unchecked((int)user)); await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_created) return;
        // Only a database created by this fixture can be removed, never a configured player database.
        if (!Regex.IsMatch(_name, "^CodexLogin2FA_[a-f0-9]{32}$")) throw new InvalidOperationException("Invalid test database name.");
        SqlConnection.ClearAllPools();
        await ExecuteAsync(_admin!, $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]");
    }

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var c = new SqlConnection(connection); await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class EmailTwoFactorStoreTests(TwoFactorSqlDatabase database) : IClassFixture<TwoFactorSqlDatabase>
{
    private static readonly TwoFactorLimits Limits = new();
    private sealed record Challenge(uint User, Guid Session, Guid Id, byte[] Hash);
    private async Task<Challenge> IssueAsync(TwoFactorLimits? limits = null)
    {
        var challenge = new Challenge(await database.AccountAsync(), Guid.NewGuid(), Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));
        Assert.Equal("owner@example.test", await database.Store.TryIssueAsync(challenge.User, challenge.Session, challenge.Id, challenge.Hash, limits ?? Limits));
        return challenge;
    }
    private Task<bool> Deliver(Challenge c) => database.Store.MarkDeliveredAsync(c.User, c.Session, c.Id);
    private Task<bool> Verify(Challenge c, byte[]? hash = null) => database.Store.VerifyAsync(c.User, c.Session, c.Id, hash ?? c.Hash, Limits);

    [TwoFactorSqlFact]
    public async Task FirstFactorValidationIsReadOnlyAndRejectsWrongPasswordsAndSqlInput()
    {
        uint user = await database.AccountAsync();
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT szUserID FROM dbo.TACCOUNT_PW WHERE dwUserID=@user", connection);
        command.Parameters.AddWithValue("@user", unchecked((int)user));
        string name = (string)(await command.ExecuteScalarAsync())!;
        var auth = new GlobalDatabase(database.ConnectionString);
        Assert.Equal(user, await auth.ValidateCredentialsAsync(name, "password"));
        Assert.Equal(0u, await auth.ValidateCredentialsAsync(name, "wrong"));
        Assert.Equal(0u, await auth.ValidateCredentialsAsync("' OR 1=1 --", "password"));
        // This fixture deliberately has no TLogin procedure or TCURRENTUSER table.
    }

    [TwoFactorSqlFact]
    public async Task DeliveryCommitIsRequiredAndVerificationAndGrantAreSingleUseAcrossProcesses()
    {
        var c = await IssueAsync(); Assert.True(await database.Store.IsRequiredAsync(c.User));
        Assert.False(await Verify(c)); Assert.True(await Deliver(c)); Assert.True(await Verify(c)); Assert.False(await Verify(c));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new SqlTwoFactorStore(database.ConnectionString).ConsumeAsync(c.User, c.Session, c.Id)));
        Assert.Single(results, accepted => accepted);
        Assert.False(await database.Store.ConsumeAsync(c.User, c.Session, c.Id));
    }

    [TwoFactorSqlFact]
    public async Task AccountSessionAndChallengeIdentifiersAreAllRequired()
    {
        var c = await IssueAsync();
        Assert.False(await database.Store.MarkDeliveredAsync(c.User, Guid.NewGuid(), c.Id));
        Assert.True(await Deliver(c));
        Assert.False(await database.Store.VerifyAsync(c.User, c.Session, Guid.NewGuid(), c.Hash, Limits));
        Assert.True(await Verify(c));
        Assert.False(await database.Store.ConsumeAsync(c.User, Guid.NewGuid(), c.Id));
        Assert.False(await database.Store.ConsumeAsync(await database.AccountAsync(), c.Session, c.Id));
        Assert.True(await database.Store.ConsumeAsync(c.User, c.Session, c.Id));
    }

    [TwoFactorSqlFact]
    public async Task FailedAttemptsLockTheAccountAcrossNewConnectionsAndProcessRestarts()
    {
        var c = await IssueAsync(); await Deliver(c);
        for (int attempt = 0; attempt < Limits.MaxAttempts; attempt++) Assert.False(await Verify(c, new byte[32]));
        Assert.False(await Verify(c));
        await database.EditAsync(c.User, "LastSentUtc=DATEADD(hour,-2,SYSUTCDATETIME())");
        var replacement = new SqlTwoFactorStore(database.ConnectionString);
        Assert.Null(await replacement.TryIssueAsync(c.User, Guid.NewGuid(), Guid.NewGuid(), c.Hash, Limits));
        await database.EditAsync(c.User, "LockedUntilUtc=DATEADD(hour,-2,SYSUTCDATETIME()), AttemptWindowUtc=DATEADD(hour,-2,SYSUTCDATETIME())");
        Assert.NotNull(await replacement.TryIssueAsync(c.User, c.Session, c.Id, c.Hash, Limits));
        await Deliver(c); Assert.True(await Verify(c));
    }

    [TwoFactorSqlFact]
    public async Task ResendCooldownIsDurableAndResendingInvalidatesTheOldCode()
    {
        var first = await IssueAsync(); await Deliver(first);
        var second = first with { Session = Guid.NewGuid(), Id = Guid.NewGuid(), Hash = RandomNumberGenerator.GetBytes(32) };
        Assert.Null(await new SqlTwoFactorStore(database.ConnectionString).TryIssueAsync(second.User, second.Session, second.Id, second.Hash, Limits));
        await database.EditAsync(first.User, "LastSentUtc=DATEADD(minute,-2,SYSUTCDATETIME())");
        Assert.NotNull(await database.Store.TryIssueAsync(second.User, second.Session, second.Id, second.Hash, Limits));
        Assert.False(await Verify(first)); await Deliver(second); Assert.True(await Verify(second));
    }

    [TwoFactorSqlFact]
    public async Task HourlySendLimitCannotBeResetByReconnecting()
    {
        var limits = Limits with { MaxSendsPerHour = 2 };
        var c = await IssueAsync(limits);
        await database.EditAsync(c.User, "LastSentUtc=DATEADD(minute,-2,SYSUTCDATETIME())");
        Assert.NotNull(await database.Store.TryIssueAsync(c.User, Guid.NewGuid(), Guid.NewGuid(), c.Hash, limits));
        await database.EditAsync(c.User, "LastSentUtc=DATEADD(minute,-2,SYSUTCDATETIME())");
        Assert.Null(await database.Store.TryIssueAsync(c.User, Guid.NewGuid(), Guid.NewGuid(), c.Hash, limits));
        await database.EditAsync(c.User, "SendWindowUtc=DATEADD(hour,-2,SYSUTCDATETIME())");
        Assert.NotNull(await database.Store.TryIssueAsync(c.User, Guid.NewGuid(), Guid.NewGuid(), c.Hash, limits));
    }

    [TwoFactorSqlFact]
    public async Task ExpiredCodesAndVerifiedGrantsCannotBeUsed()
    {
        var c = await IssueAsync(); await Deliver(c);
        await database.EditAsync(c.User, "ExpiresUtc=DATEADD(minute,-1,SYSUTCDATETIME()), LastSentUtc=DATEADD(minute,-2,SYSUTCDATETIME())");
        Assert.False(await Verify(c));
        Assert.NotNull(await database.Store.TryIssueAsync(c.User, c.Session, c.Id, c.Hash, Limits));
        await Deliver(c); Assert.True(await Verify(c));
        await database.EditAsync(c.User, "VerifiedUntilUtc=DATEADD(minute,-1,SYSUTCDATETIME())");
        Assert.False(await database.Store.ConsumeAsync(c.User, c.Session, c.Id));
    }

    [TwoFactorSqlFact]
    public async Task AdministrativeEmailChangeRevokesCodesAndVerifiedGrants()
    {
        var c = await IssueAsync(); await Deliver(c); Assert.True(await Verify(c));
        await database.ConfigureAsync(c.User, "new-owner@example.test");
        Assert.False(await database.Store.ConsumeAsync(c.User, c.Session, c.Id));
        Assert.Equal("new-owner@example.test", await database.Store.TryIssueAsync(c.User, c.Session, c.Id, c.Hash, Limits));
        await database.ConfigureAsync(c.User, "new-owner@example.test", false);
        Assert.False(await database.Store.IsRequiredAsync(c.User)); Assert.False(await Deliver(c));
    }

    [TwoFactorSqlFact]
    public async Task ConcurrentIssuanceHasOnlyOneWinner()
    {
        uint user = await database.AccountAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => database.Store.TryIssueAsync(
            user, Guid.NewGuid(), Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), Limits)));
        Assert.Single(results, email => email is not null);
    }

    [TwoFactorSqlFact]
    public async Task InvalidationCannotCancelAnotherSessionsNewerChallenge()
    {
        var old = await IssueAsync();
        await database.EditAsync(old.User, "LastSentUtc=DATEADD(minute,-2,SYSUTCDATETIME())");
        var current = old with { Session = Guid.NewGuid(), Id = Guid.NewGuid() };
        Assert.NotNull(await database.Store.TryIssueAsync(current.User, current.Session, current.Id, current.Hash, Limits));
        await database.Store.InvalidateAsync(old.User, old.Session, old.Id);
        await Deliver(current); Assert.True(await Verify(current));
    }
}
