# TLoginSvr.Net

New characters follow the neutral level-one progression. The client creates directly without the
private-server veteran dialog. The server consumes legacy level/faction selections but passes neutral
country `4` and level option `0` to `TCreateChar`, then acknowledges level `1` on success. Legacy
`CS_VETERAN_REQ` receives an ordinary-start-only response. Existing characters are unaffected.
This removes boosted creation; the remaining legacy database gifts, quest and gameplay data are not
claimed to match Gameforge until independently verified against the selected reference.

A C# (.NET 10) reimplementation of the C++/ATL `Servers/TLoginSvr` login server, **protocol- and
DB-compatible with this repo's (Araz-4ever) client and SQL baselines**. It compiles to a console `.exe`
and runs in Docker — no Windows-only dependencies, no ATL/registry, no ODBC DSNs.

It replaces the IOCP/ATL service with async sockets + the .NET Generic Host, and the ODBC layer with
`Microsoft.Data.SqlClient`. The two existing databases and their stored procedures are used unchanged.

## Email two-factor authentication

Email OTPs use the existing client dialog and encrypted packet layouts. The server checks the password
without creating a game session, reserves a challenge, delivers the code through SMTP, then sends
`CS_SECURITYCONFIRM_REQ`. It never sends a pending `CS_LOGIN_ACK(LR_SECURITY)`, because this client would
close the socket on that response. After a correct code, `CS_SECURITYRESULT_ACK(CODE_CORRECT)` makes the
client repeat `CS_LOGIN_REQ`; a one-use grant bound to that connection, account and password permits the
normal `TLogin` flow. Agreement results and duplicate-login recovery retain their normal checks.

Returning from the map to character selection uses a one-use 256-bit continuation token instead of
another email code. The updated client keeps it in memory and sends it in the reserved `Zombie3` login
field only during its automatic return. The server binds it to the account, password and client IP,
checks credentials and restrictions again, then rotates it on a successful return. Normal logins still
require the email code. Tokens expire after 24 hours, are replaced by the next successful verification,
and are lost when Login restarts; an expired/invalid token or an older client requires a new code.
`CS_LOGIN_ACK` carries the optional token after the legacy ban fields. Deploy both client and Login.

Apply **`Database/migrations/025_login_email_two_factor.sql` before starting the new Login binary**.
The migration adds `TLOGIN2FA` and administrative `TLoginTwoFactorConfigure`; it does not change `TLogin`
or enroll existing accounts. A missing schema fails startup before session cleanup/listening. Missing
SMTP, database errors or delivery failures never bypass 2FA. Unenrolled accounts keep their normal login;
custom legacy procedures returning `LR_SECURITY` remain denied rather than being silently promoted.

Configure `Login:TwoFactor:Smtp` in deployment configuration:

| Key | Example / default |
|---|---|
| `Host` | Your SMTP server; empty disables delivery |
| `Port` | `587` |
| `Encryption` | `StartTls` (mandatory STARTTLS) or `SslOnConnect` (usually port 465) |
| `From` | A verified sender address |
| `UserName` | SMTP username; empty only for a TLS relay that does not require authentication |
| `Password` | Supply through `Login__TwoFactor__Smtp__Password`, never commit credentials |
| `TimeoutSeconds` | `20`, including admission/connection/delivery; shorter than the code lifetime |

MailKit 4.18.2 sends over TLS 1.2/1.3 using normal certificate/name validation. There is no plaintext or
accept-any-certificate configuration. At most four deliveries run concurrently, outside the authentication
DB admission gate. The only recipient is the enrolled database address, never a client-supplied address.

After independently verifying the account owner's address, a database administrator can enroll it:

```sql
USE [TGlobal_gsp];
EXEC dbo.TLoginTwoFactorConfigure
    @dwUserID = 123,
    @szEmail = N'owner@example.com',
    @bEnabled = 1;
```

This is administrative enrollment/recovery, not a public self-service endpoint. Use the same procedure
to replace a verified address or disable the policy with `@bEnabled = 0`; it invalidates outstanding codes
and unconsumed grants. Do not change recipient columns directly. No MAC, IP address or empty device
identifier is saved as a trusted device. Each new login connection requires its own verification.

Defaults under `Login:TwoFactor`: a cryptographically generated six-digit code lives **300 seconds**;
resends require **60 seconds**, with at most **10 per hour/account**; **5 wrong attempts** lock the account
for **900 seconds**. The verified relogin grant lives **60 seconds**. Codes are stored only as salted
SHA-256 digests, consumed once and replaced on resend. Deadlines, attempts, locks and send limits use
SQL Server's clock and survive Login restarts and parallel connections. OTP values and SMTP error text
are not logged. Email OTP preserves the legacy flow; it is not phishing-resistant MFA.

Tests exercise the complete encrypted client flow and a real local SMTP server with STARTTLS and implicit
TLS, including certificate rejection and missing STARTTLS. Set `TLOGIN_2FA_TEST_ADMIN` to an SQL admin
connection to run isolated SQL concurrency, expiry, lockout, resend, grant and enrollment-revocation tests.
They create their own `CodexLogin2FA_<guid>` database, apply migration 025 twice and remove only that test
database. They never use player rows; these tests do not replace restored-baseline or production SMTP validation.

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

- **Second-factor bypass removed:** a database `Security` result stays challenge-required; this client
  build supplies no verifiable trusted-device identity. Failed or pending logins expose no map key,
  character or routing data and cannot use authenticated handlers. A new login attempt clears any
  previous account authentication. Duplicate-login recovery verifies the password before removing its
  existing login row, and the retried login still enforces `Security`.
- **Email second factor is implemented** through the challenge/relogin flow above. It requires migration
  025, SMTP configuration and administrative enrollment. OpenIddict was not added.
- Exec-checksum/HWID anti-cheat is still disabled (`m_hExecFile` is not loaded); `GetCheckFilePoint` is `0`.
- On Ctrl+C / SIGTERM the server runs `TClearLoginCurrentUser`, mirroring the C++ `OnExit`.
- Implemented messages: LOGIN, AGREEMENT, GROUPLIST, CHANNELLIST, CHARLIST, CREATECHAR, DELCHAR, START,
  TESTVERSION, VETERAN, SECURITYCONFIRM, TERMINATE, HOTSEND (no-op). The dev-only `TESTLOGIN` is omitted.
