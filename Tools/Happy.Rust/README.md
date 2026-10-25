# Happy (Rust)

Native desktop operator console for the current **TControlSvr.Net** implementation. Rust + egui/eframe;
no MFC, Windows SCM, direct database access, web server or bundled browser. The original C++ tool in
[`../Happy`](../Happy) remains as a reference.

## Build and run

Install a stable Rust toolchain (Rust 1.88 or newer, with the platform's native linker). On Windows use
the MSVC toolchain and Visual Studio C++ build tools. From the repository root:

```powershell
cargo run --locked --release --manifest-path Tools/Happy.Rust/Cargo.toml
```

Or build once and run `Tools/Happy.Rust/target/release/happy.exe` directly:

```powershell
cargo build --locked --release --manifest-path Tools/Happy.Rust/Cargo.toml
```

The default endpoint is **127.0.0.1:3615**, editable in the connection panel. `HAPPY_HOST` and `HAPPY_PORT`
can override those defaults. Enter a **TOPLogin operator account**, not a game login. Control rejects all
operator logins without its configured Global database; Happy does not bypass authentication. Passwords
are never saved to a file or activity log and the password box is cleared when connecting.

Manager traffic retains the server's existing plaintext transport. Keep it on localhost/a private
network, or use a private tunnel for remote access. World/Map peer mTLS belongs to Control's outbound
configuration; Happy connects only to the manager endpoint and does not use the peer listener.

## Using the console

- **Overview & services** loads group/machine/type topology, service status, live counts, a 120-sample
  user history, CPU/memory/network samples, stop counters and refresh age. Filter by name/group/id,
  select a service to start/stop its configured process or reconnect monitoring, and optionally export CSV.
- **Operator commands** provides named forms for the current manager API: player messages, kicks,
  teleports and bans; monster lookup/actions; item states; castles; scheduled/lucky events; help messages;
  RPS; gifts/catalog changes; tournament schedules/entries/players; patch and preversion metadata.
  Expand records/lists and add rows for compound operations. Every mutation has a review dialog.
- **Deploy file** streams a local file (path entry or drag/drop) in acknowledged 48 KiB chunks. Enter
  Control's `LocalMachineId` and a path relative to its `UploadRoot`. Final chunks, empty files, cancel,
  server errors and disconnect are handled explicitly. The client caps files at 512 MiB; Control may
  impose a lower configured limit. Cancellation discards staging before final commit. A lost final ACK
  leaves the commit outcome uncertain: inspect the destination before retrying.
- **Responses & activity** shows decoded structured replies and a bounded in-memory activity log. Copy
  reply JSON or export/import a command form for repeatable operations. JSON imports use the form's
  field names; unknown fields, missing fields, numeric overflow and unsupported text are rejected.

Permissions are applied both in the UI and in the socket worker, using the server's central policy:
authority **1 is highest**, **7 lowest**. A status session (`CT_STLOGIN`) can request only service status;
all mutations remain disabled even when the account has authority 1. Manager-id fields are inserted from
the authenticated session, including lucky-event and item-state requests. The server remains authoritative.

The current protocol uses **Latin-1**, signed 32-bit byte lengths for strings, little-endian scalars and
a 16-byte header, with a maximum packet size of 65535 bytes. Unsupported Unicode is rejected instead of
silently replacing characters. Dates in command forms and replies are Unix seconds; coordinates are
float32. Field descriptions identify routing conventions and operation enums.

## Differences from the MFC tool

Happy targets the APIs that the .NET dispatcher actually supports. In particular:

- Process start/stop is for **configured local executables**, with Control's ownership, graceful
  shutdown and restart rules. Remote/Docker peers can be monitored; Happy cannot start a container by
  pretending it is a Windows service. Reconnect affects the monitor TCP connection only.
- File deployment uses Control's atomic local `UploadRoot`, replacing the old SMB/FTP assumptions.
  Patch/preversion commands update database metadata. Publishing patch archives to the launcher's
  download host is a separate deployment step; Happy does not claim to have published files.
- Account protection includes the current `reason` and `permanent` fields. Gift catalog requests and
  replies omit the obsolete MFC `level` byte. Tournament reply decoding follows the .NET schedule and
  nested tournament-entry blocks, including the schedule-add id-only reply.
- Removed menus for unhandled legacy operations (disconnect/install-version/local-guild commands) are
  not exposed. Every manager request handled by the current dispatcher has a form or upload workflow.
- A sent command is recorded as **sent**, not as confirmed success. Some legacy commands have no ACK.
  Process-control ACK values are historically ambiguous; refresh service state to verify start/stop.

This change also updates Control to send topology after read-only login and to route announcements
and character messages in groups without a relay topology row. Connected relays retain priority within
their own group, avoiding duplicate delivery. Rebuild Control to obtain those two changes.

## Verification

```powershell
cd Tools/Happy.Rust
cargo fmt --check
cargo clippy --locked --all-targets -- -D warnings
./test.ps1
```

`test.ps1` runs the Rust codec, framing, permission, UI rendering and loopback network tests, builds
the isolated .NET fixture, then explicitly runs the three normally ignored interoperability tests.
It requires .NET 10. The fixture binds an ephemeral loopback port and injects test authentication only
in its own executable; it never reads production settings or opens SQL/game-server connections.
Its dispatcher, event serialization, manager authentication policy and atomic upload implementation
are the production Control classes. Tests verify byte-exact event round trips, topology, restricted
sessions and multi-chunk/empty-file deployment. Network tests cover cancellation followed by another
upload, failed login and invalid framing. Sandbox runners must allow localhost TCP.

Control's regression suite is separate:

```powershell
dotnet test ../../Servers/TControlSvr.Net/TControlSvr.slnx -c Release
```

Windows build, headless rendering and loopback interoperability are validated locally. Tests do not
claim a restored-database run, real player/world command execution or Linux desktop validation.

## Source layout

| File | Responsibility |
|---|---|
| `src/wire.rs` | Strict bounded codec, Latin-1 and incremental TCP framing |
| `src/catalog.rs` | Typed request/reply schemas and operator permissions |
| `src/net.rs` | Background socket owner, login and acknowledged upload state machine |
| `src/model.rs` | Service/host samples, topology and decoded replies |
| `src/ui.rs` | Native desktop interface and operation review |
| `tests/interop/` | Test-only .NET loopback host |

eframe API reference: [eframe 0.33.3](https://docs.rs/eframe/0.33.3/eframe/).
