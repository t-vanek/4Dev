# TControlSvr.Net

A C#/.NET 10 port of the C++/ATL **TControlSvr** — the 4Story cluster's **operations / control hub**.
It authenticates GM admin tools, monitors the game-server cluster, and relays GM commands to the world /
map / login / relay servers. Byte-compatible with this repo's protocol (`Lib/Own/TProtocol/…/CTProtocol.h`)
and SQL baselines, so it interoperates with the already-ported `TWorldSvr.Net`.

See **[PORT_STATUS.md](PORT_STATUS.md)** for the handler-by-handler port map and the faithful-degradation notes.

## Authenticated server links

World and Map connectors now require a `PeerTls` section on each corresponding `Control:Servers` entry:
`CertificatePath` (Control's client-auth PFX), `CertificatePassword` (prefer environment configuration,
e.g. `Control__Servers__0__PeerTls__CertificatePassword`), `ServerName` (the server certificate SAN), and
`ServerSha256` (64 hex digits of its leaf SHA-256 fingerprint). Authorize Control's certificate as
`Role: "Control", ServerId: 0` at the target. Missing TLS configuration disables that connector and keeps
Control's manager/process supervision running. Authentication failures never downgrade to plaintext.

For Maps, configure the target's separate `Map:ControlPort` and use that as the Control service's `Port`;
the public game-client port no longer accepts Control by source IP. For DB-loaded topology, add a
matching `Control:Servers` override for every World/Map service needing TLS configuration. Existing
Login/relay links and the Happy manager wire protocol are unchanged. Details and certificate requirements
are in [World](../TWorldSvr.Net/README.md) and [Map](../TMapSvr.Net/README.md). Upgrade these binaries and
their configuration together. Temporary test certificates are not deployment certificates.

## What it is

TControlSvr plays two roles at once:

- **Server** — accepts GM admin-tool connections (the C++ `CTManager`), authenticates operators via
  `TOPLogin`, and serves topology + service-monitoring + admin commands.
- **Client** — connects **out** to each game server (the C++ `CTServer`), registers with `CT_CTRLSVR_REQ`,
  polls live counts (`CT_SERVICEMONITOR`), and relays GM commands (chat-ban, char-message, GM teleport,
  castle override, events, cash-sale, RPS, cash-mall gift, item find/state, help, tournament-event,
  monster, announcement, kick-out), routing each server's reply back to the originating manager.

It also runs the **scheduled-event engine** (`CheckEvent`: daily/term event start-end alarms, lottery /
gift-time distribution, cash-sale windows) and persists via the DB (`TEventUpdate`, `TUserProtectedAdd`,
the patch procs, `OPTool_SMSEmergency`).

All traffic (manager↔control and control↔server) is the **plaintext 16-byte TNetLib framing** — no RC4/XOR
(unlike the login server). Wire framing/codec is shared verbatim with `TWorldSvr.Net`.

## Projects

| Project | Purpose |
|---|---|
| `TControl.Protocol` | Plaintext codec (`PacketHeader/Reader/Writer/Framer`) + `Msg` (all CT ids from `CTProtocol.h`) + `Proto` constants + wire models (`EventInfo`, `CmGift`, `BanInfo`, `PatchFile`, topology). No deps. |
| `TControl.Data` | `Microsoft.Data.SqlClient` access: topology load (`TMACHINE/TNETWORK/TGROUP/TSVRTYPE/TSERVER/TIPADDR`, `TEVENTCHART`, `TCASHSHOPITEMCHART`, `TPREVERSION`) + procs (`TOPLogin`, `TLoadService`, `TEventUpdate`, `TUserProtectedAdd`, `OPTool_SMSEmergency`, the patch procs). |
| `TControl.Server` | Generic host + Serilog, manager accept loop, outbound connector, single batch task + 1 s timer, `ControlService` handlers, and the `Ops` abstractions. |
| `*.Tests` | xUnit: protocol codec/golden-id/`EventInfo` round-trip; a loopback end-to-end (connect → register → login → chat-ban relay → ACK); env-gated DB smoke. |

## Build & test

```powershell
dotnet build TControlSvr.slnx -c Release
dotnet test  TControlSvr.slnx
```

Protocol + end-to-end tests run with **no database**. The DB smoke test runs only when
`TCONTROL_TEST_GLOBAL` is set (a `TGlobal_gsp` connection string).

## Run

```powershell
dotnet run --project TControl.Server -c Release
```

With no connection string it boots with the configured topology and listens on **:3615**, but **rejects
every operator login**. A configured database that cannot be opened faults startup before the listener
opens. Production operator authentication always uses `TOPLogin`; tests inject an explicit authenticator.

## Configuration

Bound from the `Control` section of `appsettings.json`, overridable by environment variables
(`Control__Port`, `Control__Db__GlobalConnectionString`, …). The C++ read these from an INI
(`Configurations/TControlSvr.ini`); keep the DB connection string out of the file and pass it via env.

| Key | Default | Meaning |
|---|---|---|
| `Control:Port` | `3615` | TCP listen port for GM managers (`DEFAULT_CTL_PORT`; the C++ INI default was 3616). |
| `Control:AutoStart` | `false` | Start configured local processes and restart unexpected exits. An operator stop suppresses restart until the next explicit start. |
| `Control:RestartDelaySeconds` | `60` | Delay between restart attempts (1–3600 seconds). |
| `Control:ProcessStateDirectory` | `"process-state"` | Private persistent ownership/restart records. Relative to the Control content root; keep the same directory across upgrades. |
| `Control:LocalMachineId` | `0` | Machine id used for local metrics, configured processes and uploads. |
| `Control:UploadRoot` | `""` | Local deployment directory; an empty value disables uploads. Relative paths resolve against the Control content root. |
| `Control:MaxUploadBytes` | `536870912` | Maximum size of one upload, including its final chunk. |
| `Control:ConnectIntervalSeconds` | `10` | How often the connector re-dials game servers that aren't connected. |
| `Control:Servers` | `[]` | Explicit dial list, optionally with `Process` below. An entry overrides a DB topology row with the same group/type/server id. Without `Process`, the peer is monitored but its OS process is not controlled. |
| `Control:Db:GlobalConnectionString` | `""` | `TGlobal_gsp` connection string. Empty disables operator access. |

Example (PowerShell):

```powershell
$env:Control__Db__GlobalConnectionString = 'Server=localhost,11433;Database=TGlobal_gsp;User Id=sa;Password=…;TrustServerCertificate=true'
dotnet run --project TControl.Server -c Release
```

## Local .NET processes

Publish the game servers first. Configure executable paths locally; network requests can select only a
known service id, never an executable, argument, environment variable or arbitrary PID. For example:

```json
{
  "Control": {
    "AutoStart": true,
    "LocalMachineId": 1,
    "ProcessStateDirectory": "C:/4Dev/runtime/control-processes",
    "UploadRoot": "C:/4Dev/deploy",
    "Servers": [
      {
        "Host": "127.0.0.1",
        "Port": 3816,
        "Group": 1,
        "Type": 3,
        "ServerId": 1,
        "Name": "World",
        "Process": {
          "FileName": "TWorld.Server.exe",
          "WorkingDirectory": "C:/4Dev/servers/world",
          "Arguments": [],
          "Environment": { "World__Port": "3816" },
          "StopTimeoutSeconds": 45,
          "ForceKillOnTimeout": false
        }
      }
    ]
  }
}
```

On Linux use the published `TWorld.Server` executable. To run a framework-dependent DLL, set `FileName`
to the absolute `dotnet` executable path and put the DLL path in `Arguments`. Paths are resolved against
the configured working directory, which defaults to the Control content root. Managed processes require
a loopback dial address. Their own DB settings are read from their working directory or environment;
keep credentials outside tracked configuration.

Control starts executables without a shell and records their PID, creation time, executable and private
pipe credentials in `ProcessStateDirectory`. After Control maintenance it recovers only registered
processes with matching identities. An externally started process is not adopted by PID or port.
Shutdown uses a same-user named pipe with a random authentication token and a PID/creation-time
handshake, then calls the game's normal `StopApplication` so map persistence can finish. A timeout
reports failure and retains supervision; forceful process-tree termination requires
`ForceKillOnTimeout=true` and a successful authenticated shutdown acknowledgement.

Process waits run outside the single packet-processing task; results are committed back on that task.
Other managers, monitor replies and event ticks keep running during a slow stop. A pending world stop
temporarily inhibits group restarts; only a successful world stop commits group-wide suppression.
Failed stops preserve the previous restart policy. Starting an already-running service explicitly
re-enables its restart policy. Restart intent survives a supervisor restart, including a supervisor
interruption after the game has already acknowledged shutdown.

Exiting or restarting Control leaves games running. A new Control instance uses the same private state
directory to resume management. Disconnecting a lifecycle-pipe client does not stop the game. The
lifetime helper is included in each server project so that every server remains independently
publishable. Rebuild/publish Control and the managed game servers together when upgrading from the
earlier stdin-based lifecycle implementation.

Only one supervisor may own a state directory at a time. The directory contains runtime secrets: keep
it private to the server OS account and outside uploads/version control (the default `process-state/`
name is Git-ignored). Linux requires mode `0700`; new state files use `0600`. Windows relies on the
directory's account ACL and same-user/elevation checks on the pipe. An incomplete launch record is
reported as not controllable to prevent duplicate launches; resolve its identity locally before retrying.

Uploads are staged beside their destination and replace it only after a successful END packet. Cancel,
disconnect, oversized data and malformed chunks discard the staged file and preserve the old target.
Only the owning authenticated session can finish/cancel an upload. Names must be relative, use `/` for
subdirectories, and contain no traversal, drive/stream paths, symlinks or junctions. Existing Unix file
permissions are preserved; a newly uploaded executable needs its execution permission set locally.

## Docker

```powershell
docker build -t tcontrol .
docker run --rm -p 3615:3615 -e Control__Db__GlobalConnectionString='…' tcontrol
```

The topology (which servers to dial, at which private IP:port) comes from the DB (`TSERVER` + `TMACHINE` +
`TIPADDR`); those addresses must be reachable from the container.

## Behaviour notes / known simplifications

- **Local operations are implemented**: `LocalProcessController`, `LocalPlatformMonitor` and
  `LocalFileDeploy` are registered by the executable. Windows SCM and remote SMB are outside the chosen
  local-process deployment model. Remote peers can still be monitored over the existing TCP protocol.
- Process state is independent of TCP connection health. Unexpected process exits update stop counters
  and restart deadlines; a dropped monitor connection alone does not start a duplicate process.
- `CT_PLATFORM_ACK` is emitted every second for `LocalMachineId`: host CPU %, physical memory used % and
  aggregate network utilization % (not the C++ committed-memory counter). Remote metrics are not
  fabricated. Windows uses system counters; Linux uses `/proc` and interface byte counters.
- Every manager command has a central authorization policy. Unauthenticated/invalid-authority sessions
  are denied; `CT_STLOGIN` can read service status and receive updates but cannot issue mutations.
- The current GM client is [Happy in Rust](../../Tools/Happy.Rust/README.md); `Tools/Happy` keeps the
  legacy MFC source. The Rust UI and socket worker follow this server's authorization policy, including
  read-only status sessions. Both login modes receive topology. Groups without a connected relay use
  map/world fallback for announcements/character messages, including deployments without relay rows.
  Automated loopback tests run the Rust client against the production dispatcher and atomic uploads;
  real game-server command execution and restored-database operator validation remain separate checks.
- **Wire reconciliation with `TWorldSvr.Net`** (RESOLVED): `CTProtocol.h` defines
  `CT_SERVICEMONITOR_REQ = 0x931E` / `_ACK = 0x931F`; `TWorldSvr.Net/TWorld.Protocol/NetCode.cs` previously
  had them **swapped** (never live-exercised, since no control server existed). The swap has been fixed in
  the world port, and the 1 s monitor poll now round-trips end-to-end (verified live: poll `0x931F` →
  counts reply `0x931E`).

Map session-key/ownership checks and authenticated World/Map admin links are implemented and covered by
regressions. Login implements the [email second-factor flow](../TLoginSvr.Net/README.md#email-two-factor-authentication)
and denies access until verification completes. Restored-database/deployment validation remains a separate check. See the Happy README for client validation;
Docker lifecycle control is outside the local-process model.
