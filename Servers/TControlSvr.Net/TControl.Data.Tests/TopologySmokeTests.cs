using TControl.Data;
using Xunit;

namespace TControl.Data.Tests;

/// <summary>
/// DB smoke tests, gated on the <c>TCONTROL_TEST_GLOBAL</c> connection string (a TGlobal_gsp). When unset,
/// the test is reported as skipped rather than counted as database validation.
/// Example: <c>Server=localhost,11433;Database=TGlobal_gsp;User ID=sa;Password=...;TrustServerCertificate=True;Encrypt=False</c>.
/// </summary>
public class TopologySmokeTests
{
    private static string? Cs => Environment.GetEnvironmentVariable("TCONTROL_TEST_GLOBAL");

    [DatabaseFact]
    public async Task Loads_topology_from_the_global_database()
    {
        var db = new ControlDatabase(Cs!);
        await db.PingAsync(default);

        var groups = await db.LoadGroupsAsync(default);
        var svrTypes = await db.LoadSvrTypesAsync(default);
        var servers = await db.LoadServersAsync(default);

        // The baseline should define at least the Lapiris group and a server topology.
        Assert.NotEmpty(groups);
        Assert.NotNull(svrTypes);
        Assert.NotNull(servers);
    }
}

internal sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TCONTROL_TEST_GLOBAL")))
            Skip = "TCONTROL_TEST_GLOBAL is not configured.";
    }
}
