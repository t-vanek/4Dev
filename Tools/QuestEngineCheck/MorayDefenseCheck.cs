using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;
using TMap.Protocol;

internal static class MorayDefenseCheck
{
    // Read-only SQL. Exercise the shared native accuracy rule against an actual SQL-spawned Moray.
    // Candidate DL/MDL values are an explicit proposal, not claimed original server data.
    public static async Task RunAsync(string proposalPath, bool before)
    {
        var connection = Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var templates = await new GameDatabase(connection).LoadTemplatesAsync();
        using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(proposalPath));
        var expected = proof.RootElement.GetProperty(before ? "attribute_before" : "attribute_after");
        var state = new MapState();
        var service = new MapService(new MapServerOptions { Channels = new byte[] { 1 } }, state, new Sink(), null, templates, NullLogger<MapService>.Instance);
        service.SpawnRng = new Random(1); service.InitMonsterSpawns(); service.RunMonsterRegen(60000);
        var mon = state.FindMonster(Monster.MakeId(32404, 1, 0)) ?? throw new InvalidOperationException("Training Moray did not spawn from SQL");
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        Require(mon.ChartId == 801 && mon.Level == 10 && mon.MaxHp == 711 && mon.MaxMp == 485, "Moray identity/vitals drifted");
        Require(mon.GetDefendLevel() == expected.GetProperty("wDL").GetUInt16() && mon.GetMagicDefLevel() == expected.GetProperty("wMDL").GetUInt16(), "Defense level differs from the selected proposal state");
        var owned = AttackerPower.Of(mon, templates.Skill(700));
        Require(owned.PysMin == 72 && owned.PysMax == 75 && owned.MgMin == 74 && owned.MgMax == 77 && owned.AttackLevel == 23, "Earlier captured attack repair regressed");
        Require(mon.GetDefendPower() == 330 && mon.GetMagicDefPower() == 330, "Unverified damage reduction was changed");
        var samples = new List<object>();
        foreach (byte formula in new byte[] { 30, 31 })
        foreach (byte level in new byte[] { 7, 9, 10 })
        {
            var rng = new Random(12345); int hits = 0; const int attempts = 20000;
            uint defense = formula == 30 ? mon.GetDefendLevel() : mon.GetMagicDefLevel();
            for (int i = 0; i < attempts; i++)
                if (MapService.HitTypeVsMonster(rng, templates.Formula(formula), level, mon.Level, defense, 0, 36) != 0) hits++;
            double rate = hits * 100.0 / attempts;
            Require(before ? rate >= 18 && rate <= 22 : rate >= 95 && rate <= 99, "Accuracy regression outside expected fixture range");
            samples.Add(new { Formula = formula, AttackerLevel = level, AttackLevel = 36, DefenseLevel = defense, Attempts = attempts, Hits = hits, HitPercent = rate });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ReadOnlySql = true, PlayerRowsModified = 0, BeforeProposal = before, Samples = samples,
            OriginalDefenseValueVerified = false, RealClientVerified = false }));
    }
    private sealed class Sink : IWorldSink { public void Send(byte[] p) { } public void Send(PacketWriter p) { } }
}
