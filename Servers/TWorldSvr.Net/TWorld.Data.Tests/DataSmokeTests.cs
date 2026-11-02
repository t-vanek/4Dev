using TWorld.Data;
using Xunit;
using Xunit.Abstractions;

namespace TWorld.Data.Tests;

/// <summary>
/// Integration smoke tests against a live SQL Server with the restored baselines. They run only when
/// <c>TWORLD_TEST_GLOBAL</c> / <c>TWORLD_TEST_GAME</c> connection strings are set; otherwise they no-op
/// so the suite stays green without a database (e.g. point them at <c>Server=localhost,11433;...</c>).
/// </summary>
public class DataSmokeTests
{
    private readonly ITestOutputHelper _out;
    public DataSmokeTests(ITestOutputHelper output) => _out = output;

    private static string? Global => Environment.GetEnvironmentVariable("TWORLD_TEST_GLOBAL");
    private static string? Game => Environment.GetEnvironmentVariable("TWORLD_TEST_GAME");

    [Fact]
    public async Task GlobalDb_Ping_And_GetNation()
    {
        if (string.IsNullOrWhiteSpace(Global)) { _out.WriteLine("TWORLD_TEST_GLOBAL not set — skipping."); return; }
        var db = new GlobalDatabase(Global!);
        await db.PingAsync();
        byte nation = await db.GetNationAsync();
        _out.WriteLine($"Nation = {nation}");
        Assert.True(nation >= 0);
    }

    [Fact]
    public async Task GameDb_Ping()
    {
        if (string.IsNullOrWhiteSpace(Game)) { _out.WriteLine("TWORLD_TEST_GAME not set — skipping."); return; }
        await new GameDatabase(Game!).PingAsync();
    }

    [Fact]
    public async Task OperatorsMatchSql_ReadOnly()
    {
        if (string.IsNullOrWhiteSpace(Game)) { _out.WriteLine("TWORLD_TEST_GAME not set; no SQL assertions."); return; }
        var actual = await new GameDatabase(Game!).LoadOperatorsAsync();
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(Game);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("SELECT dwOperatorID FROM TOPERATORTABLE", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var expected = new List<uint>();
        while (await reader.ReadAsync()) expected.Add(unchecked((uint)reader.GetInt32(0)));
        Assert.Equal(expected.Order(), actual.Order());
        _out.WriteLine($"Verified {actual.Count} operator IDs against live SQL, read-only.");
    }
}
