# Server benchmarks

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project Tools/ServerBenchmarks -c Release -- map-send
```

This queues 4,000 encrypted packets, receives their complete byte stream over a real loopback socket,
and reports socket calls. It also measures temporary allocations for 1,000 string writes after reserving
the packet capacity. No game server or database is needed.

```powershell
$env:T4DEV_TEST_ADMIN = '<SQL Server connection string with CREATE DATABASE permission>'
dotnet run --project Tools/ServerBenchmarks -c Release -- login
```

The login benchmark copies the login tables' schema, defaults, indexes and procedures from `TGlobal_gsp` into a temporary `Codex4DevBurst_<GUID>` database,
seeds 400 synthetic accounts, and compares authentication bursts at 100 and 32 concurrent DB operations
before and after migration 024. It removes that database in `finally`. Player data and real account credentials
are not copied. Source collation/compatibility are preserved; migration 024's indexes are omitted from the
baseline even if already installed on the source. The SQL pool and procedure are warmed before each burst.
Each result reports successes, SQL error numbers, elapsed time, p50/p95 including admission
waiting, and the server-wide lock-wait delta. Run against an otherwise idle development SQL Server for useful
lock-wait comparisons; repeat runs when comparing latency.

`T4DEV_TEST_ADMIN` also enables the isolated SQL tests in `TMap.Data.Tests.SecurityAndCloakStoreTests`.
