using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace TMap.Data.Tests;

/// <summary>Creates and removes its own database; never inserts test accounts/items into a player's database.
/// Set T4DEV_TEST_ADMIN to an SQL connection with CREATE DATABASE permission to run these checks.</summary>
public sealed class SecurityAndCloakStoreTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string? _admin = Environment.GetEnvironmentVariable("T4DEV_TEST_ADMIN");
    private readonly string _name = "Codex4DevFeature_" + Guid.NewGuid().ToString("N");
    private string? _cs;
    public SecurityAndCloakStoreTests(ITestOutputHelper output) => _output = output;

    private async Task Exec(string cs, string sql)
    {
        await using var c = new SqlConnection(cs); await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_admin)) { _output.WriteLine("DB checks not run: T4DEV_TEST_ADMIN is unset."); return; }
        await Exec(_admin, $"CREATE DATABASE [{_name}]");
        _cs = new SqlConnectionStringBuilder(_admin) { InitialCatalog = _name }.ConnectionString;
        await Exec(_cs, @"CREATE TABLE TMEDALS(dwCharID int PRIMARY KEY, dwMedals int NOT NULL);
            CREATE TABLE TCHARTABLE(dwCharID int PRIMARY KEY, dwUserID int NOT NULL, bDelete tinyint NOT NULL);
            CREATE TABLE TITEMTABLE(dlID bigint PRIMARY KEY, dwOwnerID int NOT NULL, bOwnerType tinyint NOT NULL,
                bStorageType tinyint NOT NULL, dwStorageID int NOT NULL, bItemID tinyint NOT NULL, bCount tinyint NOT NULL,
                dwTime6 int NOT NULL);");
        // Exercise the real migration twice, including its GO boundaries and rerun guards.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "docker-compose.yml"))) root = root.Parent;
        string migration = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "Database/migrations/023_security_codes_and_cloaks.sql"));
        migration = migration.Replace("[TGlobal_gsp]", $"[{_name}]").Replace("[TGame_gsp]", $"[{_name}]");
        for (int run = 0; run < 2; run++)
            foreach (string batch in Regex.Split(migration, @"(?im)^\s*GO\s*$"))
                if (!string.IsNullOrWhiteSpace(batch)) await Exec(_cs, batch);
    }

    public async Task DisposeAsync()
    {
        if (_cs is null) return;
        SqlConnection.ClearPool(new SqlConnection(_cs));
        await Exec(_admin!, $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]");
    }

    [Fact]
    public async Task SecurityCode_HashEnabledAttemptsAndLock_RoundTripAcrossInstances()
    {
        if (_cs is null) return;
        var db = new GameDatabase(_cs, _cs);
        Assert.Null(await db.LoadSecurityCodeAsync(9));
        var expected = new SecurityCodeRow(SecurityCodeHash.Create("1234!abc"), true, 10, 1_900_000_000);
        await db.SaveSecurityCodeAsync(9, expected);
        Assert.Equal(expected, await new GameDatabase(_cs, _cs).LoadSecurityCodeAsync(9));
        await using var c = new SqlConnection(_cs); await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT strSecurityCode FROM TSECURECODE WHERE dwUserID=9", c);
        Assert.Equal("", await cmd.ExecuteScalarAsync());
        var disabled = expected with { Enabled = false, Tries = 0, LockedUntil = 0 };
        await db.SaveSecurityCodeAsync(9, disabled);
        Assert.Equal(disabled, await db.LoadSecurityCodeAsync(9));
    }

    [Fact]
    public async Task Cloak_PaymentAndItemUpdate_AreAtomic_OwnershipAndFundsAreEnforced()
    {
        if (_cs is null) return;
        await Exec(_cs, @"INSERT TCHARTABLE VALUES(1,9,0); INSERT TMEDALS VALUES(1,9000);
            INSERT TITEMTABLE VALUES(123,1,0,0,254,4,1,0);
            INSERT TCUSTOMCLOAKTABLE(dwUserID,wID) VALUES(9,7),(10,8);");
        var db = new GameDatabase(_cs, _cs);
        Assert.Null(await db.ChangeCustomCloakAsync(9, 1, 123, 8)); // another account's texture
        Assert.Null(await db.ChangeCustomCloakAsync(10, 1, 123, 8)); // another account's character
        Assert.Null(await db.ChangeCustomCloakAsync(9, 1, 999, 7)); // item missing: roll the charge back
        Assert.Equal(5000u, await db.ChangeCustomCloakAsync(9, 1, 123, 7));
        Assert.Equal(1000u, await db.ChangeCustomCloakAsync(9, 1, 123, 0)); // remove a custom texture
        Assert.Null(await db.ChangeCustomCloakAsync(9, 1, 123, 7)); // insufficient funds
        await using var c = new SqlConnection(_cs); await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT dwTime6 FROM TITEMTABLE WHERE dlID=123", c);
        Assert.Equal(0, await cmd.ExecuteScalarAsync());
        await Exec(_cs, "UPDATE TMEDALS SET dwMedals=5000 WHERE dwCharID=1; INSERT TCUSTOMCLOAKTABLE(dwUserID,wID) VALUES(9,-25536);");
        Assert.Equal(1000u, await db.ChangeCustomCloakAsync(9, 1, 123, 40000)); // WORD id across SQL's signed SMALLINT boundary
        Assert.Equal(40000, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MedalPackage_ConsumptionAndCredit_AreAtomic_AndCannotBeReplayed()
    {
        if (_cs is null) return;
        await Exec(_cs, "INSERT TITEMTABLE VALUES(456,2,0,0,255,0,2,0);");
        var db = new GameDatabase(_cs, _cs);
        Assert.Null(await db.RedeemMedalItemAsync(99, 456, 1, 100)); // wrong owner
        Assert.Null(await db.RedeemMedalItemAsync(2, 456, 3, 300)); // too many
        Assert.Equal(100u, await db.RedeemMedalItemAsync(2, 456, 1, 100));
        Assert.Equal(200u, await db.RedeemMedalItemAsync(2, 456, 1, 100));
        Assert.Null(await db.RedeemMedalItemAsync(2, 456, 1, 100)); // already consumed
    }
}
