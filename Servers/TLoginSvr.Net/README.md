# TLoginSvr.Net

A C# (.NET 10) reimplementation of the C++/ATL `Servers/TLoginSvr` login server, **protocol- and
DB-compatible with this repo's (Araz-4ever) client and SQL baselines**. It compiles to a console `.exe`
and runs in Docker — no Windows-only dependencies, no ATL/registry, no ODBC DSNs.

It replaces the IOCP/ATL service with async sockets + the .NET Generic Host, and the ODBC layer with
`Microsoft.Data.SqlClient`. The two existing databases and their stored procedures are used unchanged.

## Why "compatible with *this* repo"

The wire protocol here differs from the sibling `..\4retro-4ever` fork. All wire values are taken from
**this** repo's source, not the fork:

| | Value (source) |
|---|---|
| Client version `TVERSION` | `0x2918` (`Lib/Own/TProtocol/include/ProtocolBase.h`) |
| Message base `CS_LOGIN` | `0x1987` (same) |
| XOR key table / RC4 secret | `Servers/TNetLib/Session.cpp` |
| Packet field orders | `Servers/TLoginSvr/{CSHandler,CSSender}.cpp` |
| Stored-proc parameter order | `Servers/TLoginSvr/DBAccess.h` |

### Wire format (recap)

- 16-byte little-endian header: `wSize | wID | dwNumber | llChkSUM`.
- Strings are a 4-byte length prefix + raw bytes (no NUL).
- Per-connection cipher is **asymmetric**: outbound (server→client) is XOR header+body + checksum only;
  inbound (client→server) is RC4 over the whole buffer (outermost), then XOR header, then verify the
  sequence number and XOR/verify the body. `wSize` always stays plaintext so packets can be framed.

## Projects

| Project | Role |
|---|---|
| `TLogin.Protocol` | Wire codec (`PacketReader/Writer/Framer/Header`) + crypto (`Rc4`, `PacketCrypto`, `ProtocolKeys`, `SessionCipher`) + `NetCode` IDs/enums. No dependencies. |
| `TLogin.Data` | `Microsoft.Data.SqlClient` access to the global + per-group game databases. Procs called positionally to match the C++ binding. |
| `TLogin.Server` | Generic-Host worker, async TCP `PacketServer`/`PacketConnection`, and the `LoginService` handlers. |
| `*.Tests` | xUnit: protocol codec/crypto round-trips + golden vectors, DB smoke tests, an end-to-end encrypted-login test. |

## Build & test

```bash
dotnet build TLoginSvr.slnx -c Release          # needs the .NET 10 SDK
dotnet test  TLoginSvr.slnx -c Release
```

Protocol and end-to-end (version-reject) tests run with no database. The DB smoke tests run only when
`TLOGIN_TEST_GLOBAL` (and optionally `TLOGIN_TEST_GAME`) connection strings are set, e.g.
`Server=localhost,11433;Database=TGlobal_gsp;User ID=sa;Password=...;TrustServerCertificate=True;Encrypt=False`.

## Run

### 1) Docker (full stack)

The compose file lives at the **repo root** (`../../docker-compose.yml`). It brings up SQL Server 2022,
restores `TGlobal_gsp` + `TGame_gsp` from the repo-root `.bak` files (once), applies any pending SQL
migrations (`Database/migrations/`), then starts this login server on port 4816.

```bash
cd ../..          # repo root (where docker-compose.yml + the .bak files live)
# edit .env (SA_PASSWORD) first
docker compose up --build
```

SQL Server is published on host port **11433**; the login server on **4816**. The map IPs the login
server returns come from `TRoute` in the database, so the external TWorldSvr/TMapSvr cluster must be
reachable from clients at those addresses.

Database schema changes are applied as tracked migrations — see `Database/migrations/README.md` at the root.

### 2) Windows / Linux console executable

```powershell
pwsh ./publish-console.ps1
# -> publish/win-x64/TLogin.Server.exe   and   publish/linux-x64/TLogin.Server
```

Edit the `appsettings.json` next to the binary (or set environment overrides) to point at your SQL
Server, then run it.

## Configuration

`appsettings.json` (section `Login`), overridable by environment variables (`__` = nesting):

| Key | Env | Default | Notes |
|---|---|---|---|
| `Port` | `Login__Port` | 4816 | TCP listen port |
| `MaxConcurrentLogins` | `Login__MaxConcurrentLogins` | 100 | authentication DB sequences admitted concurrently (1–1024); others wait without holding SQL connections |
| `Db:GlobalConnectionString` | `Login__Db__GlobalConnectionString` | — | `TGlobal_gsp` (accounts/login/routing) |
| `Db:GameConnectionStringsByDsn:<DSN>` | `Login__Db__GameConnectionStringsByDsn__<DSN>` | — | one per `TGROUP.szDSN`; the game DB(s) |
| `ControlServerIp` | `Login__ControlServerIp` | "" | if set, that peer skips the cipher (plaintext, like the C++ control server) |
| `Nation` | `Login__Nation` | from `TGetNation` | optional locale override |
| `ValidateClientChecksum` | `Login__ValidateClientChecksum` | true | enforce the client version checksum that `OnCS_LOGIN_REQ` validates |

## Behaviour notes / known simplifications

Migration `024_login_burst_indexes.sql` adds covering account-name and protection indexes and a blacklist-IP index.
Authentication retains the existing stored procedures and READ COMMITTED isolation. Character selection loads all six
characters' equipment and guild appearance together, removing the two per-character DB round trips. The guild-cloak
owner id is normalized to the client registration flag in both the individual and bulk item readers.

For a repeatable 400-account burst test using a disposable database, see
[`Tools/ServerBenchmarks`](../../Tools/ServerBenchmarks/README.md). The benchmark does not read or modify player rows.

- **Anti-cheat (exec checksum / HWID / 2FA / email) is wired but disabled**, exactly as in the C++ build
  (`m_hExecFile` is never loaded; the SMTP path is commented out). HWID/2FA validators auto-pass; the
  `GetCheckFilePoint` value sent in group/channel/char lists is `0`.
- On Ctrl+C / SIGTERM the server runs `TClearLoginCurrentUser`, mirroring the C++ `OnExit`.
- Implemented messages: LOGIN, AGREEMENT, GROUPLIST, CHANNELLIST, CHARLIST, CREATECHAR, DELCHAR, START,
  TESTVERSION, VETERAN, SECURITYCONFIRM, TERMINATE, HOTSEND (no-op). The dev-only `TESTLOGIN` is omitted.
