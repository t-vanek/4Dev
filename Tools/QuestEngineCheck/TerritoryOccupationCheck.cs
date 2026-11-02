using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TMap.Data;

// Called only after FullSaveCheck has verified the disposable SQL host/catalog/property marker.
internal static class TerritoryOccupationCheck
{
    public static async Task RunAsync(string connection, string migration, List<string> passed)
    {
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        async Task Exec(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
        async Task<object?> Scalar(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); return await cmd.ExecuteScalarAsync(); }
        async Task<long> Number(string sql) => Convert.ToInt64(await Scalar(sql));
        async Task Seed(string table, Dictionary<string, object> values)
        {
            await using var c = new SqlConnection(connection); await c.OpenAsync();
            await using var columns = new SqlCommand(@"SELECT c.name,TYPE_NAME(c.system_type_id),c.is_nullable,c.default_object_id,c.is_identity
FROM sys.columns c WHERE object_id=OBJECT_ID(@table) AND c.is_computed=0 AND c.system_type_id<>189 ORDER BY column_id", c);
            columns.Parameters.AddWithValue("@table", table);
            var fields = new List<(string Name, object Value, bool Identity)>();
            await using (var r = await columns.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    string name = r.GetString(0), type = r.GetString(1);
                    if (values.TryGetValue(name, out var value)) { fields.Add((name, value, r.GetBoolean(4))); continue; }
                    if (r.GetBoolean(4) || r.GetInt32(3) != 0) continue;
                    object fallback = r.GetBoolean(2) ? DBNull.Value : type switch
                    {
                        "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" => "",
                        "datetime" or "smalldatetime" or "datetime2" or "date" => new DateTime(2000, 1, 1),
                        "binary" or "varbinary" or "image" => new byte[] { 0 },
                        "uniqueidentifier" => Guid.NewGuid(),
                        _ => 0
                    };
                    fields.Add((name, fallback, false));
                }
            Require(fields.Count > 0, "Missing native territory fixture table " + table);
            bool identity = fields.Any(f => f.Identity);
            string quoted = "[" + table.Replace("]", "]]") + "]";
            string names = string.Join(',', fields.Select(f => "[" + f.Name.Replace("]", "]]") + "]"));
            string args = string.Join(',', fields.Select((_, i) => "@p" + i));
            await using var insert = new SqlCommand((identity ? "SET IDENTITY_INSERT " + quoted + " ON;" : "") +
                "INSERT " + quoted + "(" + names + ") VALUES(" + args + ");" + (identity ? "SET IDENTITY_INSERT " + quoted + " OFF;" : ""), c);
            for (int i = 0; i < fields.Count; i++) insert.Parameters.AddWithValue("@p" + i, fields[i].Value);
            await insert.ExecuteNonQueryAsync();
        }
        Dictionary<string, object> Values(params (string Name, object Value)[] entries)
            => entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.OrdinalIgnoreCase);
        await Exec("UPDATE TCHARTABLE SET bCountry=1 WHERE dwCharID=1"); // synthetic winning chief; independent of earlier full-save fixture defaults
        await Seed("TBATTLETIMECHART", Values(("bType", 0), ("dwBattleStart", 72000)));
        await Seed("TGUILDTABLE", Values(("dwID", 10), ("dwChief", 1), ("szName", "WarFixture")));
        await Seed("TGUILDMEMBERTABLE", Values(("dwCharID", 1), ("dwGuildID", 10), ("bDuty", 2)));
        await Seed("TLOCALTABLE", Values(("wLocalID", 6000), ("bCountry", 0), ("dwGuild", 0)));
        await Seed("TCASTLETABLE", Values(("wCastle", 7000), ("bCountry", 0), ("dwGuildID", 0)));
        await Seed("TBATTLEZONECHART", Values(("wID", 6000), ("wCastle", 7000)));
        await Seed("TMISSIONTABLE", Values(("wMissionID", 8000), ("bCountry", 3)));
        await Seed("TMISSIONTABLE", Values(("wMissionID", 8001), ("bCountry", 3)));
        await Seed("TSKYGARDENTABLE", Values(("wID", 200), ("bCountry", 0)));
        await Seed("TSKYGARDENTABLE", Values(("wID", 201), ("bCountry", 0)));
        var db = new GameDatabase(connection);
        Require(await db.SaveSkyGardenOccupyAsync(1, 200, 1) == 0 &&
            await Number("SELECT COUNT(*) FROM TSKYGARDENTABLE WHERE bCountry=1") == 2,
            "Inherited sky-garden unscoped update was not reproduced");
        passed.Add("native TSaveSkyGardenOccupy ignores its target ID and changes two synthetic gardens (reproduced)");
        await Exec("UPDATE TSKYGARDENTABLE SET bCountry=0");

        string originalSky = Convert.ToString(await Scalar("SELECT OBJECT_DEFINITION(OBJECT_ID('TSaveSkyGardenOccupy'))"))!;
        await Exec(Regex.Replace(originalSky, @"\bCREATE\s+PROCEDURE\b", "ALTER PROCEDURE", RegexOptions.IgnoreCase) + "\n-- existing fixture repair to preserve");
        bool refused = false;
        try { await SqlQueueFixture.ApplyMigration(connection, migration); } catch (SqlException) { refused = true; }
        Require(refused && await Number("SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('TSaveMissionOccupy')) LIKE '%4DEV_044_TARGET_GUARD%' THEN 1 ELSE 0 END") == 0,
            "Migration overwrote unknown repair or failed to roll both changes back");
        await Exec(Regex.Replace(originalSky, @"\bCREATE\s+PROCEDURE\b", "ALTER PROCEDURE", RegexOptions.IgnoreCase));
        passed.Add("migration 044 rejects an unexpected existing SQL repair and atomically rolls back the earlier procedure alteration");
        await SqlQueueFixture.ApplyMigration(connection, migration); // helper applies twice
        Require(await db.SaveSkyGardenOccupyAsync(1, 200, 1) == 0 &&
            await Number("SELECT bCountry FROM TSKYGARDENTABLE WHERE wID=200") == 1 &&
            await Number("SELECT bCountry FROM TSKYGARDENTABLE WHERE wID=201") == 0, "Sky garden target isolation failed");
        passed.Add("migration 044 applies twice; fresh SQL calls change only the requested sky garden");
        Require(await db.SaveSkyGardenOccupyAsync(1, 999, 1) != 0 &&
            await db.SaveMissionOccupyAsync(999, 1, 1, 1) != 0, "Missing target was falsely acknowledged as occupied");
        passed.Add("missing mission/garden return native failure through the real C# adapters without a success result");
        Require(await db.SaveMissionOccupyAsync(8000, 1, 1, 1) == 0 &&
            await Number("SELECT bCountry FROM TMISSIONTABLE WHERE wMissionID=8000") == 1 &&
            await Number("SELECT bCountry FROM TMISSIONTABLE WHERE wMissionID=8001") == 3, "Mission country/target binding failed");
        passed.Add("mission occupation target and winning country survive a fresh-connection native SQL roundtrip");
        var world = new TWorld.Data.GameDatabase(connection);
        await world.SaveCastleApplicantAsync(7000, 1, 1);
        await world.SaveCastleApplicantAsync(7001, 1, 2);
        Require(await Number("SELECT COUNT(*) FROM TCASTLEAPPLICANTTABLE WHERE dwCharID=1 AND wCastleID=7001 AND bCamp=2") == 1 &&
            await Number("SELECT COUNT(*) FROM TCASTLEAPPLICANTTABLE WHERE dwCharID=1") == 1, "Castle applicant upsert lost its target/camp or duplicated");
        passed.Add("World's native applicant adapter inserts then updates one persisted castle/camp without duplicate rows");
        await Exec("ALTER TABLE TCASTLEAPPLICANTTABLE ADD CONSTRAINT FixtureApplicantFailure CHECK(bCamp<>1)");
        try
        {
            bool rejected = false;
            try { await world.SaveCastleApplicantAsync(7000, 1, 1); } catch (SqlException) { rejected = true; }
            Require(rejected && await Number("SELECT COUNT(*) FROM TCASTLEAPPLICANTTABLE WHERE dwCharID=1 AND wCastleID=7001 AND bCamp=2") == 1,
                "Failed applicant update was acknowledged or replaced the confirmed slot");
        }
        finally { await Exec("ALTER TABLE TCASTLEAPPLICANTTABLE DROP CONSTRAINT FixtureApplicantFailure"); }
        passed.Add("injected native applicant SQL rejection propagates while preserving the previously confirmed castle/camp");
        await world.SaveCastleApplicantAsync(0, 1, 0); await world.SaveCastleApplicantAsync(0, 1, 0);
        Require(await Number("SELECT COUNT(*) FROM TCASTLEAPPLICANTTABLE WHERE dwCharID=1") == 0, "Applicant withdrawal did not persist");
        passed.Add("native applicant withdrawal survives fresh-connection reload and is idempotent when repeated");
        var fort = await db.SaveLocalOccupyAsync(6000, 1, 10, 1);
        Require(fort == (0, (byte)1, 10u) && await Number("SELECT dwGuild FROM TLOCALTABLE WHERE wLocalID=6000") == 10 &&
            await Number("SELECT COUNT(*) FROM TLOCALOCCUPYTABLE WHERE wLocalID=6000 AND dwGuildID=10 AND bType=1") == 1,
            "Fort native ownership/output/history binding failed");
        passed.Add("fort native save returns the chief's country/guild and persists occupation history");
        await world.SaveCastleApplicantAsync(7000, 1, 1);
        var castle = await db.SaveCastleOccupyAsync(7000, 1, 10);
        Require(castle == (0, (byte)1) && await Number("SELECT dwGuildID FROM TCASTLETABLE WHERE wCastle=7000") == 10 &&
            await Number("SELECT COUNT(*) FROM TLOCALOCCUPYTABLE WHERE wLocalID=6000") == 0 &&
            await Number("SELECT COUNT(*) FROM TCASTLEAPPLICANTTABLE WHERE wCastleID=7000") == 0, "Castle save/output/week/applicant reset failed");
        Require((await db.SaveCastleOccupyAsync(7000, 1, 0)).Ret != 0 &&
            await Number("SELECT dwGuildID FROM TCASTLETABLE WHERE wCastle=7000") == 10, "Guildless castle capture was acknowledged");
        passed.Add("castle native owner/country and fort-week reset roundtrip; a guildless capture rejects without replacing its owner");
        await Exec("ALTER TABLE TMISSIONTABLE ADD CONSTRAINT FixtureMissionFailure CHECK(wMissionID<>8000 OR bCountry<>0)");
        try
        {
            bool failed = false;
            try { await db.SaveMissionOccupyAsync(8000, 1, 1, 0); } catch (SqlException) { failed = true; }
            Require(failed && await Number("SELECT bCountry FROM TMISSIONTABLE WHERE wMissionID=8000") == 1, "Rejected mission write altered ownership");
        }
        finally { await Exec("ALTER TABLE TMISSIONTABLE DROP CONSTRAINT FixtureMissionFailure"); }
        passed.Add("injected native mission SQL failure propagates and retains its previously confirmed owner");
    }
}
