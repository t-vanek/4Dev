# CLAUDE.md

## Current project target

The user has chosen the local **Gameforge CZ installation** in `C:\Program Files (x86)\4Story` as the
gameplay/content reference. `Gameforge/reference.json` pins its files. Use
`python Tools/GameforgeReference/reference.py verify --source 'C:\Program Files (x86)\4Story'` to check it.
The exact official patch is unidentified; neither the PE timestamp nor a matching asset ID establishes
release parity. Preserve the modern engine and .NET infrastructure while restoring verified official behavior.

The explicit content target is **Gameforge 5.0+**, anchored in the publisher's 5.0.1 notes.
Migrations 035–036 deploy the CZ team's EXP thresholds through 140, 12 supported delivery quests,
two original-position gold shops and local routes for Anbari/Undred. See
`Gameforge/progression-140/report.html`, the frozen plans, deployment hashes and SQL-backed packet probes.
The regions still have no monster population: 140 quest guidance points do not establish original spawn
counts/timers/drops. 126 other missions and nine portal NPCs remain deferred. New non-EXP/non-client-fee
level columns retain row-125 compatibility values; do not call those original Gameforge curves.
Current validation: 1,177 Linux map-server tests plus actual deployed-DLL probes. Saved characters were
not rewritten. Immutable post-034 snapshots/backups are under `.runtime/gameforge-high-level/`.

`Gameforge/combined-evidence/report.html` and `joined.json` join the original client, captured public
Gameforge records and a fresh read-only SQL snapshot for 138 regional missions, 64 monster IDs, 93 NPCs
(79 regional) and 1,051 relevant items. All mission names/levels match; 130 published EXP/gold pairs and
14 item reward sets match. Quest 11292 has a giver conflict; 11261 lacks a client giver. Nine monster
levels disagreed with public details before migration 037, and 3538/3557 lack their referenced SQL attribute rows. Public
catalogue/detail level ranges conflict for 3547. That combined report is the pre-037 research snapshot.
It retains eight quest-item source relationships, ten drop helper records and 48 portal edges; spawn
population, numeric loot odds, complete conditions and portal landing heights remain unresolved.
Term 21 is a spawn-instance reference in the native/server protocol; do not join its numeric ID directly
to monster templates. `Tools/GameforgeReference/join_gameforge_evidence.py` verifies captured pages and
pinned client inputs before rebuilding the report. Its SQL snapshot is under `.runtime/gameforge-combined-evidence/`.

Migration 037 is deployed: exact singleton level/HP pairs for 3529 (102/5422) and 3588 (130/11365),
16 mapped original-client fields across 3550/3565/3567/3571/3583, and Elrod 26647/26648 with 22 published
stock rows and verified prices. Original attribute rows and all other combat fields were retained.
`Gameforge/server-completion/report.html` is the current remaining-content report; `remaining.json`
and `current/joined.json` contain refreshed IDs. Seven regional level conflicts and both missing
attribute references remain; 126 missions, all 64 related monster populations, loot rules and nine
regional portals remain unresolved. Global missing NPCs are now 33, including eight BoW merchants
whose pricing mode is not implemented. Backups and the frozen pre-037 baseline are under
`.runtime/gameforge-verified-import/`. Full comparison checked 23,568 old affected rows; 198 other game
tables and 69 global tables were retained. Probes used the actual deployed DLL and checked 22 new
purchases, two monster level/HP pairs, mapped monster fields and 12 existing deliveries without SQL
gameplay writes. Map restarted and loaded 2,755 NPCs. No graphical playthrough was performed.

Migration 039 is deployed: NPCs 22424–22433 now use map 12, as verified in both clients. Dialogue
quests 4578–4587 were blocked by their former SQL map 11. NPC-talk quest execution also now returns
the trigger NPC's ID, so the client no longer ignores an otherwise valid response addressed to NPC 0.
`Gameforge/npc-map-repair/report.html` records exact row/source evidence, before/after packet probes
using the deployed DLL, wrong-map/prerequisite guards and 1,194 passing map-server tests. All 2,755 NPC
dialog/list probes and 48 delivery/talk completions passed. Only ten NPC map fields changed; 202 other
game tables and 69 global tables matched their pre-migration hashes, excluding migration tracking.
Probe characters and prerequisite progress were in memory; no graphical playthrough was performed.

Quest-object pickup is deployed: mission 8003 (Byliny pro Karla) now collects item 9656 from NPC 26193
through routing quest 8005. `CS_ITEMBUY_REQ` resolves eligible routing-quest item terms independently
of shop stock and checks the trigger NPC/map/conditions. Unknown quest IDs no longer bypass shop payment.
`Gameforge/quest-herbs/report.html` records the old deployed-DLL failure and the new pickup → objective
update → turn-in → follow-up probe. All 1,204 map tests, the ten repaired NPC-map dialogue probes and
48 existing delivery/talk completions passed. Quest charts and saved player data were not manually edited.

The read-only pickup audit in `Gameforge/quest-pickup-audit/` compared all 183 routing definitions.
All 182 definitions with real items reproduce ITEMBUY_NOTFOUND on the old deployed DLL and pass pickup,
repeat/prerequisite/NPC/map guards on the current DLL. These include 141 pickup definitions in 58 active
missions, 14 recovery pickups and 27 definitions whose parent missions remain disabled. Quest 877 is a
placeholder referencing item/quest 0. Prerequisites were seeded only in memory; the result does not prove
whole-story reachability. Six cases use global NPC templates, whose map-0 template file is not a static
placement; their dynamic spawning remains unverified. The audit changed no production state.

The inherited RageZone/Araz client and SQL backups contain private-server changes. Do not call them
vanilla Gameforge, infer official monster levels from their values, or import unverified custom content.
Character creation now starts neutral at level 1 and ignores legacy boosted-start options. Existing test
characters are not progression evidence. The official CZ web database supplements the selected client:
`Gameforge/normalization/identity-audit.html` compares complete captured public catalogues with decoded
original-client IDs; the JSON retains every ID, name difference and source hash. The catalogue omits some
same-name variants: direct lookups confirmed 992 additional monster IDs. Migrations 026/027 correct level/HP
for 213 + 1,979 monsters, using exact IDs/names and singleton ID-detail values. Original SQL attributes,
EXP and mappings remain available. Migrations 028–033 restore mapped client fields, verified mission
gold/XP/levels, 56 gold-shop stock sets, explicit item countries and new-character profiles. Starter skills
above the original client's level-one requirement and their starter hotkeys are removed only from new
character profiles. Private 4StoryPW welcome mail and account-mount replacement are removed.

Read the plans in `Gameforge/normalization/` before further normalization. Do not regenerate an applied
migration from an already-modified live baseline. Backups, pre-change chart snapshots, raw web pages,
isolated staging DB checks and the client-file backups are under `.runtime/gameforge-normalization/`.
The live deployment passed full chart comparison, idempotent reruns and 24 transactionally rolled-back
creation probes. Current character/inventory/pet/skill/mail data was retained.

Retained monster damage/defence/EXP/mana, non-EXP progression columns, spawns, drops, rates, unsupported quest graphs
and remaining NPC stock/placement are unverified. Moray's retained EXP remains zero. The website's item
power level/default price is derived and must not be equated blindly to the raw item-attribute grade.
Do not infer an individual level from a published range or map the website's damage to internal attack
columns without validating that mapping. Matching monster levels do not establish matching HP or other stats.

The notes below describe the historical imported package and predate the .NET servers, CMake/bgfx/SDL3
client and current Docker deployment. For current setup use README.md and INSTALLATION.md.

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

The **original RageZone "4Story 5.0" release** of **4Story** (a Korean "Trickster/Tachyon-engine" MMO,
EU build "Araz4Story") — server emulator source, the compiled game client, the database baselines, and a
Windows-service install kit. This is the **pristine upstream**; the sibling folder `..\4retro-4ever\` is a
modified fork of it. When something here looks dated, that fork is the modernized reference — see
`..\4retro-4ever\CLAUDE.md` for the contrast (it rewrote servers as console apps + INI config, added a
.NET login server, DB migrations, and a launcher).

Everything is **C++ targeting Win32/x86** (the servers are **ATL/COM**, the client is **MFC + DirectX 9**),
built with **MSVC** — the projects are `PlatformToolset v141` (Visual Studio 2017), a handful still on
`v140` (VS 2015). There is no cross-platform build and no .NET anywhere in this release.

### Layout

```
Client/    C++ game client source (TClient.sln) + TClientCmd.tif (the compiled-UI artifact shipped with source)
Lib/       Own/ (engine + UI + net libraries, each its own .sln) and 3rdParty/
Servers/   The ATL Windows-service cluster (TServer.sln) + TLogSvr (own sln)
Tools/     Server-side build tools — Happy (moved here from Servers/Tools/Happy), plus TLauncher, TCMLParser
Game/      The COMPILED runtime client + all assets (this is what actually runs; = 4retro's Game/)
Install Services/   .lnk shortcuts that install each server as a Windows service
Reg Services/       .reg files that seed each service's registry config (see "Deployment" below)
TGLOBAL_RAGEZONE.bak / TGAME_RAGEZONE.bak   SQL Server database baselines (restore once)
```

> Note: `Game/` was extracted from the "Araz4Story PvP" client package and renamed to match 4retro's
> layout. `Client/TClientCmd.tif` (Dec 2018 build) differs from `Game/TClientCmd.tif` (Jul 2018 build) —
> the one the client actually loads is the one in `Game/`.

## Building

No CLI/test harness — build with MSBuild or open the `.sln` in Visual Studio. All projects are **x86**.
The servers need the **ATL** workload installed (ATL is what `_AtlModule`/`.rgs`/`.idl` require).

```powershell
# Server cluster — TWorldSvr, TLoginSvr, TMapSvr, TControlSvr, TPatchSvr, TBoWSvr, TBRSvr, TNetLib, TProtocol
msbuild Servers\TServer.sln /p:Configuration=Release /p:Platform=x86

# Log server is a SEPARATE solution (UDP log sink), not part of TServer.sln
msbuild Servers\TLogSvr\4SLogServer.sln /p:Configuration=Release /p:Platform=x86

# Client — TClient, Engine Lib, TChart, TCML, TComp, HwidLib
msbuild Client\TClient.sln /p:Configuration=Release /p:Platform=x86

# Shared libraries can also be built standalone (each has its own .sln under Lib\Own\*)
msbuild "Lib\Own\Engine Lib\Engine Lib.sln" /p:Configuration=Release /p:Platform=x86

# Server-side tool
msbuild Tools\Happy\Happy.sln /p:Configuration=Release /p:Platform=x86
```

## Deployment & configuration (the important difference from 4retro)

These servers are **ATL Windows services**, not console apps. Each `main` is
`CT<Name>SvrModule _AtlModule; _tWinMain → _AtlModule.WinMain()`, so the same `.exe` self-registers as a
service (`/Service`, `/RegServer`, `/UnregServer`) and, when running as a service, reads its settings from
the **Windows registry**, *not* from an INI file:

```
HKLM\SYSTEM\CurrentControlSet\Services\<SERVICE>\Config
  DSN, DBUser, DBPasswd, Port, GroupID, ServerID, LogIP, LogPort, ...
```

Bring-up uses the kit at the repo root:
- **`Reg Services\*.reg`** seed each service's `…\Config` key. Service names are `TWORLD_GSP`,
  `TLOGIN_GSP`, `TMAP_GSP` (and `TMAP2/3_GSP`), `TCTRL_GSP`, `TPATCH_GSP`, `TRELAY_GSP`, `TBOW_GSP`,
  `TBR_GSP`, `TLOG0/1`, `TWORLD2_GSP`.
- **`Install Services\*.lnk`** install/register the services.

DB access is via two **ODBC DSNs**: `TGLOBAL_GSP` (accounts/login + topology) and `TGAME_GSP`
(game data). Default ports from the `.reg`: **World 3816**, **Login 4816**, **Log 7000** (UDP). The
service install kit references **more services than there are source projects** here (e.g. `TRELAY_GSP`,
`TMAP2/3`, `TWORLD2`) — those are extra/duplicate instances of the same binaries for a multi-machine
deployment.

> **The `.reg` files contain real `sa`/DB credentials in plaintext.** Do not print or commit them; scrub
> before sharing. Two databases back the cluster: restore `TGLOBAL_RAGEZONE.bak` → `TGlobal_gsp` (DSN
> `TGLOBAL_GSP`) and `TGAME_RAGEZONE.bak` → `TGame_gsp` (DSN `TGAME_GSP`). There is **no** migration
> tooling in this release (the fork added that).

## Server architecture (the big picture)

A distributed, IOCP-based ATL-service cluster. **TWorldSvr is the hub** holding world state; the other
services connect to it. Inter-server messages are split by plane in `Lib/Own/TProtocol/Include/`:
`CS*`/`CT*` (client↔server), `MW*` (map↔world), `SS*` (server↔server), `DM*`, plus `LogPacket.h`.

| Service       | Role |
|---------------|------|
| `TWorldSvr`   | **Central hub** — guilds (`TGuild`/`TCorps`), characters, parties (`TParty`), rankings, and the **BoW/BR game modes in-process** (`BowSystem.*`, `BRSystem.*`). The `TBoWSvr`/`TBRSvr` projects exist but the logic lives here. |
| `TLoginSvr`   | Account auth / login; the client's entry point (port 4816). |
| `TMapSvr`     | Per-map/zone game logic (mobs, NPCs, movement, quests, combat); deployed as multiple instances (`TMAP_GSP`/`TMAP2`/`TMAP3`), each connecting up to TWorldSvr. |
| `TControlSvr` | Admin/monitoring service. |
| `TPatchSvr`   | Client file patch distribution (see `TPatchSvr/ReadMe.txt`). |
| `TLogSvr`     | Centralized **UDP** log sink (`4SLogServer.sln`, `LogServer.cpp`) — separate solution. |

### Shared server code

There is **no `TServerSystem` base library** here (that is a 4retro addition). Servers share:
- **`Servers/global/globalinc.h`** — common defines.
- **`TNetLib`** (`Servers/TNetLib/`, prebuilt `Servers/Lib/TNetLib.lib`, and a copy under `Lib/Own/TNetLib`) — the networking + DB core:
  - `Packet.*` — wire format (fixed header + body via overloaded `<<`/`>>`, per-session key encryption).
  - `Session.*` — IOCP/overlapped async socket wrapper.
  - `SqlBase/SqlDatabase/SqlQuery/SqlDirect.*` — **ODBC** layer (connection + prepared-statement pooling) over the two DSNs.
- **`TProtocol`** (`Lib/Own/TProtocol/Include/`) — the shared packet definitions listed above.

Each server still carries its own `TServer.cpp`/`TServer.h` plus a `<Name>Session` class; the handler
pattern is the familiar one — worker threads pull completed IOCP I/O, parse the packet header, and
`switch` on packet ID to `On<MSG>` handlers that reply via the session or enqueue DB/batch jobs.

## Client architecture

`Client/TClient.sln` builds the **MFC** game client (rendered with **DirectX 9, June 2010 SDK**) from
these projects:

- **`TClient`** — the game client app (`Client/TClient/`, with `global/` and `History Files/`). There is **no separate launcher project** in this release (the fork added a "4Retro" launcher).
- **`Engine Lib`** — the **Tachyon 3D engine**: D3D9 device/camera/font/light wrappers, audio, skeletal animation, crypto, and the MFC `CTachyonApp`/`CTachyonWnd` base classes.
- **`TCML`** — parser for **TCML**, the XML-like UI markup language.
- **`TComp`** — the UI widget toolkit built on TCML (TButton/TEdit/TList/TFrame/… : `TComponent : CWnd`).
- **`TChart`** — loads compiled game data tables (`.tcd`) and localized strings.
- **`HwidLib`** — hardware-ID fingerprinting for account binding / anti-cheat (also has its own `.sln` under `Lib/Own/HwidLib`).

`Lib/Own/` additionally contains **`TachyonControl`** and the **`TProtocol`** include package. Third-party
code lives under `Lib/3rdParty/`.

## Game data formats & the `Game/` tree

`Game/` is the **compiled, runnable client** (DLLs `d3d9`/`d3dx9_34`/`d3dx9_43`/`GdiPlus`/`dbghelp`,
`Uninstall.exe`, the anti-cheat packages, and all assets). Custom binary formats:

| Format        | Location                       | Holds |
|---------------|--------------------------------|-------|
| `.TMH`        | `Game/Tmh/`, `Game/Data/Mesh/` | Skeletal 3D meshes (Tachyon format) |
| `.TCD`        | `Game/Tcd/`, `Game/*.tcd`      | Compiled data tables (items, NPCs, skills, strings…) |
| `.TAC`        | `Game/Data/Action/`            | Animation/action sequences |
| `.TIF`        | `Game/TClientCmd.tif`          | **Compiled UI** (produced from TCML) |
| `.MPQ`/`.QPD` | `Game/TQuest.mpq`, `TQNode.qpd`| Quest definitions / quest-node graph |
| `.MPC`        | `Game/TClientMP.mpc`           | Client map/patch catalog |
| index/nav     | `Game/Index/`, `Node/`, `Path/`| File catalogs / navigation + pathfinding graphs |

`Game/4storyEU.ini` is the main client config but is **encrypted/obfuscated** (not plain text — don't try
to read or hand-edit it as INI). `Game/GameGuard/` (nProtect) and `Game/HShield/` (AhnLab) are the two
runtime anti-cheat packages. The stray `TClient(...).dmp` is a leftover crash dump.

## Tools

`Tools/Happy/` (`Happy.sln`, moved here from `Servers/Tools/Happy/`) is a server-side build tool — see its
`ReadMe.txt` and `UserPosList.txt`.
