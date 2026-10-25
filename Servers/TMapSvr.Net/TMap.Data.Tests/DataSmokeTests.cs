using TMap.Data;
using Xunit;
using Xunit.Abstractions;

namespace TMap.Data.Tests;

/// <summary>
/// The map runs DB-free by design (the server catches connection failures and synthesizes defaults), so
/// there is nothing to assert without a live database. These tests exercise the real query path only when
/// <c>TMAP_TEST_GAME</c> points at a game DB; otherwise they no-op, matching the sibling ports.
/// <para>
/// A skipped run is <b>logged</b>, not silent: without the env var these assert nothing, and a green suite
/// would otherwise read as "the DB path is verified" when it was never entered. To run them against the
/// compose stack (see the repo-root <c>docker-compose.yml</c>):
/// </para>
/// <code>
/// $env:TMAP_TEST_GAME = "Server=localhost,11433;Database=TGame_gsp;User ID=sa;Password=$env:SA_PASSWORD;TrustServerCertificate=True;Encrypt=False"
/// dotnet test TMap.Data.Tests
/// </code>
/// </summary>
public class DataSmokeTests
{
    private readonly ITestOutputHelper _out;
    public DataSmokeTests(ITestOutputHelper output) => _out = output;

    private static string? GameCs => Environment.GetEnvironmentVariable("TMAP_TEST_GAME");

    /// <summary>True when a live game DB is configured; logs the skip reason when it is not.</summary>
    private bool Configured([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (!string.IsNullOrWhiteSpace(GameCs)) return true;
        _out.WriteLine($"TMAP_TEST_GAME not set — skipping {caller} (asserted nothing).");
        return false;
    }

    [Fact]
    public async Task Ping_Succeeds_WhenConfigured()
    {
        if (!Configured()) return;
        var db = new GameDatabase(GameCs!);
        await db.PingAsync();
    }

    [Fact]
    public async Task LoadChar_ReturnsNull_ForUnknownChar_WhenConfigured()
    {
        if (!Configured()) return;
        var db = new GameDatabase(GameCs!);
        var row = await db.LoadCharAsync(0xFFFFFFF0);
        Assert.Null(row);
    }

    [Fact]
    public async Task LoadTemplates_PopulatesItemAndMagicCharts_WhenConfigured()
    {
        if (!Configured()) return;
        var db = new GameDatabase(GameCs!);
        var store = await db.LoadTemplatesAsync();
        Assert.True(store.HasItems, "TITEMCHART should yield at least one item template.");
        // Every loaded item template has a 4-element revision table (fRevision/fMRevision/fAtRate/fMAtRate).
        Assert.All(store.Items.Values, t => Assert.Equal(4, t.Revision.Length));
        _out.WriteLine($"Loaded {store.Items.Count} item templates.");
    }

    /// <summary>
    /// The AI-script load against the real baseline. Pinned to the shape actually in
    /// <c>TGame_gsp</c> (47 <c>TAICHART</c> rows / 18 commands / 6 conditions → 2 scripts), because that
    /// shape is what falsified the guess that "aggressive = binds SetHost under AT_ENTER": both
    /// live scripts bind it, so the real discriminator is the <c>AT_ENTERLB</c> engagement chain. If a
    /// migration ever changes the chart, this is the test that should make you re-read the scripts.
    /// </summary>
    /// <summary>The mail procedures through the real C# bindings: can-send → save → info → list → view →
    /// delete, between two existing characters. A plain letter with no money or item, removed in <c>finally</c>
    /// — the database is left as it was.</summary>
    [Fact]
    public async Task PostProcedures_RoundTrip_WhenConfigured()
    {
        if (!Configured()) return;
        var db = new GameDatabase(GameCs!);

        string a, b;
        await using (var c = new Microsoft.Data.SqlClient.SqlConnection(GameCs))
        {
            await c.OpenAsync();
            await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"SELECT TOP 1 x.szName, y.szName
FROM TCHARTABLE x JOIN TCHARTABLE y ON y.dwCharID <> x.dwCharID AND (x.bCountry >= 2 OR y.bCountry >= 2 OR x.bCountry = y.bCountry)
WHERE x.bDelete = 0 AND y.bDelete = 0", c);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return;                          // fewer than two characters
            a = r.GetString(0); b = r.GetString(1);
        }

        var (canSend, recvId) = await db.PostCanSendAsync(a, b, 0);
        Assert.True(canSend is 0 or 6, $"TPostCanSend returned {canSend}");
        if (canSend != 0) return;                                      // mailbox full: nothing to write
        Assert.NotEqual(0u, recvId);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60;  // smalldatetime keeps minutes
        var (saved, postId, recv) = await db.SavePostAsync(0, recvId, b, a, "port test", "body", 0, 0, 0, 0, 0, now);
        try
        {
            Assert.Equal(0, saved);
            Assert.NotEqual(0u, postId);
            Assert.Equal(recvId, recv);

            var (total, _, begin) = await db.PostInfoAsync(recvId, 1);
            Assert.True(total >= 1);
            Assert.Contains(await db.PostListAsync(recvId, begin), p => p.PostId == postId && p.Title == "port test");

            var view = await db.PostViewAsync(recvId, postId);
            Assert.Equal(0, view.Result);
            Assert.Equal("body", view.Message);
            Assert.Equal(a, view.Sender);
            Assert.Equal(0, view.ItemCount);

            Assert.Equal(0, await db.PostDeleteAsync(recvId, postId));
            Assert.DoesNotContain(await db.PostListAsync(recvId, uint.MaxValue >> 1), p => p.PostId == postId);
        }
        finally
        {
            await using var c = new Microsoft.Data.SqlClient.SqlConnection(GameCs);
            await c.OpenAsync();
            await using var del = new Microsoft.Data.SqlClient.SqlCommand("DELETE FROM TPOSTTABLE WHERE dwPostID = @p", c);
            del.Parameters.AddWithValue("@p", (int)postId);
            await del.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The teleport charts and the global-skill flag, against the live shape: 417 spawn points, 529
    /// portals, destinations only where the target portal exists (the C++ join), 5 global skills.</summary>
    [Fact]
    public async Task LoadTemplates_PopulatesTeleportChartsAndGlobalSkills_WhenConfigured()
    {
        if (!Configured()) return;
        var store = await new GameDatabase(GameCs!).LoadTemplatesAsync();

        Assert.Equal(417, store.SpawnPositions.Count);
        Assert.Equal(529, store.Portals.Count);
        Assert.NotEmpty(store.Portals[1001].Destinations);
        Assert.All(store.Portals.Values.SelectMany(p => p.Destinations.Keys), d => Assert.True(store.Portals.ContainsKey(d)));
        Assert.All(store.Portals.Values.SelectMany(p => p.Destinations.Values), d => Assert.Equal(3, d.Conditions.Length));
        Assert.Equal(5, store.Skills.Values.Count(k => k.Global));
    }

    /// <summary>The crafting charts, against the live shape: the gem and upgrade probabilities, the per-kind
    /// option index, the gamble groups (520 prizes) and the (empty) accessory table.</summary>
    [Fact]
    public async Task LoadTemplates_PopulatesCraftingCharts_WhenConfigured()
    {
        if (!Configured()) return;
        var store = await new GameDatabase(GameCs!).LoadTemplatesAsync();

        Assert.Equal(new byte[] { 33, 20, 14, 8, 4, 0 }, store.GemProbs);
        Assert.Equal(45, store.ItemGradeProb[9]);
        Assert.Equal(0, store.ItemGradeProb[24]);                       // no upgrade past the cap
        Assert.NotEmpty(store.MagicsByKind);
        Assert.Equal(520, store.CashGamble.Values.Sum(g => g.Count));
        Assert.All(store.CashGamble, g => Assert.Equal(store.CashGambleTotal[g.Key], (uint)g.Value.Sum(p => p.Prob)));
        Assert.Empty(store.AccessoryMagic);
        Assert.NotEmpty(store.RefineCostByLevel);
    }

    [Fact]
    public async Task LoadTemplates_PopulatesAiScripts_WhenConfigured()
    {
        if (!Configured()) return;
        var store = await new GameDatabase(GameCs!).LoadTemplatesAsync();

        Assert.Equal(2, store.AiScripts.Count);
        Assert.True(store.AiScripts.ContainsKey(1) && store.AiScripts.ContainsKey(2));
        Assert.Equal(47, store.AiScripts.Values.Sum(s => s.BindingCount));   // every TAICHART row bound

        // Script 1 engages on sight, script 2 only fights back — the live split (3192 vs 344 monsters).
        Assert.True(store.AiScripts[1].IsAggressive);
        Assert.False(store.AiScripts[2].IsAggressive);

        // Both bind SetHost under AT_ENTER, so the discarded heuristic cannot tell them apart.
        Assert.All(store.AiScripts.Values, s => Assert.True(s.Binds(AiTrigger.Enter, AiCommandKind.SetHost)));

        // The 6 TAICONCHART rows landed on their commands.
        Assert.Equal(6, store.AiScripts.Values
            .SelectMany(s => s.ByTrigger.SelectMany(b => b.Values).SelectMany(l => l))
            .Select(b => b.Command).Distinct().Sum(c => c.Conditions.Count));

        // Every monster template resolves a script (all live rows carry a non-zero bAIType).
        Assert.NotEmpty(store.MonsterTemplates);
        Assert.All(store.MonsterTemplates.Values, t => Assert.NotNull(store.AiScriptFor(t.AiType)));

        _out.WriteLine($"Loaded {store.AiScripts.Count} AI scripts over {store.MonsterTemplates.Count} monster templates.");
    }

    /// <summary>The companion procedures against the live database (TSaveCompanion / TDeleteCompanion /
    /// TSaveLastCompanion / TGetLastCompanion / TSaveMedals / TGetMedals), on a character id no player has. Every row
    /// it writes is removed in <c>finally</c>. Also checks the new charts load.</summary>
    [Fact]
    public async Task CompanionProcedures_RoundTrip_WhenConfigured()
    {
        if (!Configured()) return;
        var db = new GameDatabase(GameCs!);
        const uint charId = 2_000_000_001;
        long end = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60 + 3600;   // smalldatetime keeps minutes
        try
        {
            var a = new CompanionRow(0, 31125, 3, "PortTestA", 77, 40000, 2, 5, new byte[] { 1, 2, 3, 4, 5, 6 }, 13,
                new ushort[] { 18084, 0 }, new long[] { end, 0 }, 0);
            var b = a with { Slot = 1, Name = "PortTestB", Life = 11000, ItemIds = new ushort[] { 0, 0 }, EndTimes = new long[2] };
            await db.SaveCompanionAsync(charId, a);
            await db.SaveCompanionAsync(charId, b);
            await db.SaveLastCompanionAsync(charId, 1);
            await db.SaveMedalsAsync(charId, 1234);

            var load = await db.LoadCompanionsAsync(charId);
            Assert.Equal(2, load.Companions.Count);
            var la = load.Companions.Single(c => c.Slot == 0);
            Assert.Equal("PortTestA", la.Name);
            Assert.Equal(32767u, la.Life);                                  // clamped to the SMALLINT column
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, la.Stats);
            Assert.Equal((ushort)18084, la.ItemIds[0]);
            Assert.Equal(end, la.EndTimes[0]);
            Assert.Equal(0, la.EndTimes[1]);                                // 1900-01-01 reads back as 0
            Assert.Equal((byte)1, load.SummonedSlot);
            Assert.Equal(1234u, load.Medals);

            await db.DeleteCompanionAsync(charId, 1);
            Assert.Single((await db.LoadCompanionsAsync(charId)).Companions);

            var none = await db.LoadCompanionsAsync(3_000_000_003);           // nobody: no slot, no medals
            Assert.Equal((byte)0xFF, none.SummonedSlot);
            Assert.Equal(0u, none.Medals);
        }
        finally
        {
            await using var c = new Microsoft.Data.SqlClient.SqlConnection(GameCs);
            await c.OpenAsync();
            await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"DELETE FROM TCOMPANIONTABLE WHERE dwCharID=@c;
DELETE FROM TCOMPANIONITEMTABLE WHERE dwCharID=@c; DELETE FROM TLASTCOMPANIONTABLE WHERE dwCharID=@c; DELETE FROM TMEDALS WHERE dwCharID=@c;", c);
            cmd.Parameters.AddWithValue("@c", (int)charId);
            await cmd.ExecuteNonQueryAsync();
        }

        var store = await db.LoadTemplatesAsync();
        Assert.True(store.Mounts.Count >= 40); // the baseline has 40; later content migrations import additional mounts
        Assert.Equal(44, store.CompanionRunes.Count);
        Assert.Equal(new CompanionBonus(11, 1f, 2f), store.CompanionBonuses[11]);
        Assert.Equal(10, store.CompanionBonuses.Count);                    // 12 rows, 11 and 12 duplicated
    }
}
