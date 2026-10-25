using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Protocol;
using TMap.Server.Net;

if (args.FirstOrDefault() == "login") await LoginBurst();
else if (args.FirstOrDefault() == "map-send") await MapSend();
else throw new ArgumentException("Use login (T4DEV_TEST_ADMIN required) or map-send.");

static async Task Exec(string cs, string sql)
{
    await using var c = new SqlConnection(cs); await c.OpenAsync();
    await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
    await cmd.ExecuteNonQueryAsync();
}

static async Task<long> LockWaits(string cs)
{
    await using var c = new SqlConnection(cs); await c.OpenAsync();
    await using var cmd = new SqlCommand("SELECT COALESCE(SUM(wait_time_ms),0) FROM sys.dm_os_wait_stats WHERE wait_type LIKE 'LCK_M_%'", c);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}

static async Task LoginBurst()
{
    string admin = Environment.GetEnvironmentVariable("T4DEV_TEST_ADMIN")
        ?? throw new InvalidOperationException("Set T4DEV_TEST_ADMIN to an SQL connection with CREATE DATABASE permission.");
    string name = "Codex4DevBurst_" + Guid.NewGuid().ToString("N");
    string cs = new SqlConnectionStringBuilder(admin) { InitialCatalog = name, MaxPoolSize = 100 }.ConnectionString;
    bool created = false;
    try
    {
        // Copy only the tables, defaults, indexes and procedures used by login. DBCC CLONEDATABASE is a
        // diagnostic artifact and may carry unusable generated-column metadata from this old baseline.
        await Exec(admin, $"CREATE DATABASE [{name}];");
        created = true;
        await CopyLoginSchema(admin, cs, name);
        await Exec(cs, @"WITH n AS (SELECT TOP (400) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
            INSERT TACCOUNT_PW(szUserID,szPasswd,bCheck) SELECT CONCAT('codex-bench-',i),'bench123!',0 FROM n;
            INSERT TUSERINFOTABLE(dwUserID,bAgreement) SELECT dwUserID,1 FROM TACCOUNT_PW;
            UPDATE STATISTICS TACCOUNT_PW WITH FULLSCAN;");
        foreach (int limit in new[] { 100, 32 }) await Run("before-indexes", limit);
        var root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "docker-compose.yml"))) root = root.Parent;
        string migration = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "Database/migrations/024_login_burst_indexes.sql"));
        migration = migration.Replace("[TGlobal_gsp]", $"[{name}]");
        foreach (string batch in Regex.Split(migration, @"(?im)^\s*GO\s*$"))
            if (!string.IsNullOrWhiteSpace(batch)) await Exec(cs, batch);
        foreach (int limit in new[] { 100, 32 }) await Run("after-indexes", limit);
    }
    finally
    {
        SqlConnection.ClearAllPools();
        if (created) await Exec(admin, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
    }

    async Task Run(string stage, int concurrency)
    {
        await Exec(cs, "TRUNCATE TABLE TCURRENTUSER; TRUNCATE TABLE TLOG; TRUNCATE TABLE USERIPLOG;");
        var db = new TLogin.Data.GlobalDatabase(cs);
        // Warm the pool and procedure plan equally before every measured burst.
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => db.CheckIpAsync("127.0.0.1")));
        var probe = await db.LoginAsync("codex-bench-1", "bench123!", "127.0.0.1", 0);
        if (probe.Ret != 0) throw new InvalidOperationException("Synthetic login warmup failed.");
        await Exec(cs, "TRUNCATE TABLE TCURRENTUSER; TRUNCATE TABLE TLOG; TRUNCATE TABLE USERIPLOG;");
        using var gate = new SemaphoreSlim(concurrency);
        var times = new double[400]; var errors = new int[400];
        long waits = await LockWaits(admin);
        var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 400).Select(async i =>
        {
            long start = Stopwatch.GetTimestamp();
            await gate.WaitAsync();
            try
            {
                int ipCheck = await db.CheckIpAsync("127.0.0.1");
                var row = await db.LoginAsync($"codex-bench-{i + 1}", "bench123!", "127.0.0.1", (byte)ipCheck);
                if (row.Ret != 0) errors[i] = row.Ret;
            }
            catch (SqlException ex) { errors[i] = ex.Number; }
            finally { times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; gate.Release(); }
        }));
        Array.Sort(times);
        Console.WriteLine(JsonSerializer.Serialize(new { stage, concurrency, logins = 400,
            succeeded = errors.Count(x => x == 0), errors = errors.Where(x => x != 0).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count()),
            totalMs = Math.Round(elapsed.Elapsed.TotalMilliseconds), p50Ms = Math.Round(times[199]), p95Ms = Math.Round(times[379]),
            lockWaitMs = await LockWaits(admin) - waits }));
        if (errors.All(x => x != 0) || (stage == "after-indexes" && errors.Any(x => x != 0)))
            throw new InvalidOperationException("Login benchmark failed; the measurements above are not a successful authentication run.");
    }
}

static async Task CopyLoginSchema(string admin, string target, string name)
{
    string[] tables = { "TACCOUNT_PW", "TCURRENTUSER", "TUSERPROTECTED", "IPBLACKLIST_game", "TUSERINFOTABLE", "USERIPLOG", "TLOG", "TIPAUTHORITY" };
    string source = new SqlConnectionStringBuilder(admin) { InitialCatalog = "TGlobal_gsp" }.ConnectionString;
    await using (var info = new SqlConnection(source))
    {
        await info.OpenAsync();
        await using var cmd = new SqlCommand("SELECT compatibility_level,collation_name FROM sys.databases WHERE name=DB_NAME()", info);
        await using var reader = await cmd.ExecuteReaderAsync(); await reader.ReadAsync();
        await Exec(admin, $"ALTER DATABASE [{name}] COLLATE {reader.GetString(1)}; ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL={reader.GetByte(0)};");
    }
    foreach (string table in tables)
        await Exec(admin, $"SELECT TOP(0) * INTO [{name}].dbo.[{table}] FROM TGlobal_gsp.dbo.[{table}];");
    var commands = new List<string>();
    await using (var c = new SqlConnection(source))
    {
        await c.OpenAsync();
        string names = string.Join(",", tables.Select(t => $"'{t}'"));
        await using (var cmd = new SqlCommand($@"SELECT 'ALTER TABLE dbo.'+QUOTENAME(t.name)+' ADD DEFAULT '+d.definition+' FOR '+QUOTENAME(c.name)
            FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id
            JOIN sys.default_constraints d ON d.object_id=c.default_object_id WHERE t.name IN ({names});", c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) commands.Add(r.GetString(0));
        var indexes = new Dictionary<(string Table, string Name), (string Kind, bool Unique, string? Filter, List<string> Keys, List<string> Included)>();
        await using (var cmd = new SqlCommand($@"SELECT t.name,i.name,i.type_desc,i.is_unique,i.filter_definition,
            col.name,ic.key_ordinal,ic.is_included_column,ic.is_descending_key
            FROM sys.tables t JOIN sys.indexes i ON i.object_id=t.object_id
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns col ON col.object_id=ic.object_id AND col.column_id=ic.column_id
            WHERE t.name IN ({names}) AND i.type IN (1,2)
            ORDER BY t.name,i.index_id,ic.key_ordinal,ic.index_column_id;", c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!indexes.TryGetValue(key, out var index))
                    indexes[key] = index = (r.GetString(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4), new(), new());
                string column = $"[{r.GetString(5)}]";
                if (r.GetBoolean(7)) index.Included.Add(column);
                else if (r.GetByte(6) > 0) index.Keys.Add(column + (r.GetBoolean(8) ? " DESC" : ""));
            }
        foreach (var (key, index) in indexes)
        {
            // Reproduce the baseline even when migration 024 has already been applied to the source database.
            if (key.Name is "IX_TACCOUNT_PW_LoginName" or "IX_IPBLACKLIST_game_IP" or "IX_TUSERPROTECTED_Login") continue;
            commands.Add($"CREATE {(index.Unique ? "UNIQUE " : "")}{index.Kind} INDEX [{key.Name}] ON dbo.[{key.Table}] ({string.Join(",", index.Keys)})"
                + (index.Included.Count == 0 ? "" : $" INCLUDE({string.Join(",", index.Included)})") + (index.Filter is null ? "" : $" WHERE {index.Filter}"));
        }
        foreach (string proc in new[] { "TLogin", "TCheckIP" })
        {
            await using var cmd = new SqlCommand($"SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.{proc}'))", c);
            commands.Add((string)(await cmd.ExecuteScalarAsync())!);
        }
    }
    foreach (string sql in commands) await Exec(target, sql);
}

static async Task MapSend()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
    await using var connection = new ClientConnection(await listener.AcceptSocketAsync(), NullLogger.Instance);
    const int count = 4000;
    byte[] packet = new PacketWriter(Msg.CS_CHARSTATINFO_ACK).WriteUInt32(42).ToArray();
    for (int i = 0; i < count; i++) connection.Send(packet);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var running = connection.RunAsync((_, _) => ValueTask.CompletedTask, timeout.Token);
    await peer.GetStream().ReadExactlyAsync(new byte[count * packet.Length], timeout.Token);
    await connection.DisposeAsync(); await running;

    var writer = new PacketWriter(Msg.CS_CHARSTATINFO_ACK, capacity: 65535);
    writer.WriteString("warmup");
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++) writer.WriteString("town square");
    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Console.WriteLine(JsonSerializer.Serialize(new { packets = count, socketSends = connection.SendCalls,
        maxPacketsPerBatch = 128, stringWrites = 1000, temporaryStringBytes = allocated }));
}
