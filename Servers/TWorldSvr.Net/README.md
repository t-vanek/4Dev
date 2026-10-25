# TWorldSvr.Net — Phases 1–4 (BoW + BR + battle-time + tournament config)

## Peer authentication

The World listener requires mutual TLS before dispatching any game-server packet. Configure
`World:PeerTls:CertificatePath` (a PFX with private key and server-auth EKU), its password through
`World__PeerTls__CertificatePassword`, and `World:PeerTls:Peers`. Each allowlist entry has `Sha256`
(64 hex digits of the **leaf certificate's SHA-256 fingerprint**), `Role` (`Map` or `Control`),
and `ServerId`. Map certificates bind to the exact `MAKEWORD(serverId, 4)` identity
(`1025` / `0x0401` for map 1); Control uses `ServerId: 0`. Peers need separate private keys and
client-auth EKU. Empty configuration refuses startup.

Example (replace the fingerprint placeholders):

```json
"PeerTls": {
  "CertificatePath": "certificates/world.pfx",
  "Peers": [
    { "Sha256": "<map-1-certificate-sha256>", "Role": "Map", "ServerId": 1025 },
    { "Sha256": "<control-certificate-sha256>", "Role": "Control", "ServerId": 0 }
  ]
}
```

Trust comes from explicit certificate pins; certificates need not be in the OS trust store. Dates and
EKU are checked; clients also check the server SAN/name. TLS 1.2/1.3 protects the entire connection.
Revoke a certificate by removing its pin and restarting the listener; this private pin-based deployment
does not use online CRL/OCSP. Protect PFX files/passwords under the server account. Windows imports
temporary user key containers for Schannel; Linux uses ephemeral keys.

Maps must register their pinned identity before gameplay traffic. Control must register before issuing
allowed admin commands. A Map cannot become Control or send Control-only commands; local timer/DB
messages are refused from Maps. Two existing in-game GM/corps CT messages remain allowed for registered
Maps. Control's event and castle commands are explicitly allowed. Violations close the connection.

Configure matching clients in [Map](../TMapSvr.Net/README.md) and [Control](../TControlSvr.Net/README.md).
Upgrade all three binaries/configurations together: legacy plaintext links are refused. Loopback tests
cover successful traffic, role/identity violations, unknown certificates, wrong server names/pins and
plaintext connections. Restored SQL databases and Linux execution still require deployment validation.

## Earlier port history

A C# (.NET 10) reimplementation of the C++/ATL `Servers/TWorldSvr` — the cluster's central **world
hub**. **Phase 1**: infrastructure + the core map-server-connect and character enter/leave/chat flow.
**Phase 2**: the core guild lifecycle (DB-backed) + in-memory parties. **Phase 2b**: the full guild
long-tail (cabinet, articles, fame, contribution, wanted/volunteer boards, PvP records/rewards, stats),
the **tactics (sub-guild / mercenary)** system, and **corps** (squad-of-parties) command-relay + party
move/recall. **Phase 3**: **friends** (ask/reply/erase, groups, online/region notify), **soulmate**
(search/register/end with the silence window), and **PvP rankings** (the monthly ladder + warlord +
fame podium and the month-boundary rollover). **Phase 4 (in progress)**: **Battle of the Warlords (BoW)** —
the matchmaking queue + team-balanced match build + the alarm→peace→battle→bodpeace phase machine driven
off the 1-second tick — and **Battle Royale (BR)**: premade teams (invite/accept/leave/ready), map + mode
voting, the class-bucketed team build, and the same phase machine — and the **scheduled battle-time
machine** (the local-field / castle-siege / mission windows that broadcast their enable packets on the
1-second tick), and the **tournament config + announce** layer (the bracket entries/rewards loaded from
config and pushed to maps via `TOURNAMENTINFO`) plus **tournament registration & parties** (apply with
the 1st-grade seeding gate, apply-info/join/match lists, party add/del/list) and the **scheduler +
rank-seeded bracket build**, **match results**, and **betting** — the full player-facing tournament. The
only remaining tournament piece is the event sub-tournament admin commands on the (unimplemented) `CT_*`
control plane.

Mirrors the `TLoginSvr.Net` layout (net10, multi-project, Docker, same SQL Server).

## What TWorld is

TWorld is **accept-only** and speaks **server↔server, plaintext** (no RC4/XOR — unlike the login
server, which encrypts client traffic). Its peers are:

- **TMapSvr** over the `MW_*` plane (`MW_BASE 0x9001`) — the main plane.
- **TControlSvr** over `CT_*` (`0x9301`). (There is no `TRelaySvr` in this release, so the `RW_*` relay plane
  is intentionally not ported.)

It listens on **port 3815** (`DEF_WORLDPORT`). The wire framing is the same 16-byte TNetLib header as
TLogin, but server traffic is plaintext so the `dwNumber`/`llChkSUM` header fields stay zero.

## Architecture (C++ → C#)

- **Accept loop** → `Net/PacketServer` (async, inbound server peers).
- **Per-socket recv/send** → `Net/PacketConnection` (plaintext; recv loop frames packets and enqueues them).
- **Batch thread (the C++ single game-logic lock)** → **one batch task** consuming a `Channel`; every
  handler and all `WorldState` mutation runs there, single-threaded, so no locks are needed.
- **Timer thread (`OnTimer` @1s)** → a `PeriodicTimer(1s)` loop (scaffolded; subsystem ticks come later).
- **DB plane (`DM_*` async queue)** → deferred; Phase 1 only reads the server nation + pings the DBs at boot.

## Projects

| Project | Role |
|---|---|
| `TWorld.Protocol` | Plaintext wire codec (`PacketHeader/Reader/Writer/Framer`, **no crypto**) + `NetCode` planes/IDs. |
| `TWorld.Data` | `Microsoft.Data.SqlClient` access; Phase 1 = nation + connectivity checks. |
| `TWorld.Server` | Host worker, `PacketServer`/`PacketConnection`, `WorldState`, `WorldService` handlers, batch task + 1s timer. |
| `*.Tests` | Protocol codec tests, env-gated DB smoke tests, and the **simulated map-server end-to-end test**. |

## Phase-1 handlers (`World/WorldService.cs`)

`MW_CONNECT_ACK` (map registers), `MW_ADDCHAR_ACK` (validate `dwKEY`, create character, → `MW_ENTERSVR_REQ`),
`MW_CHARDATA_ACK` (→ `MW_ENTERCHAR_REQ` with full state; guild/party fields zeroed), `MW_ENTERCHAR_ACK`
(mark ready), `MW_CLOSECHAR_ACK` (cleanup), `MW_CHECKCONNECT_ACK` (keepalive), `MW_CHAT_ACK` (relay),
`CT_CTRLSVR_REQ` (control-server registration). Every other message id falls through to a
logged no-op, so the ~270 deferred handlers can't crash the server.

## Phase-2 handlers

**Guild (DB-backed, `World/Guild.cs` + `TWorld.Data/GuildDatabase.cs`)**: startup load of the
guild-level chart + guilds + members; on character enter the member is linked and `MW_ENTERCHAR_REQ`
now carries the real guild fields (id/fame/name/duty/peer/castle). Lifecycle: `ESTABLISH`, `DISORGANIZATION`,
`INVITE`/`INVITEANSWER`(→`JOIN`), `LEAVE`, `KICKOUT`, `DUTY`, `PEER`, `INFO`, `MEMBERLIST` — with
persistence via `TGuildEstablish`/`Disorg`/`MemberAdd`/`Leave`/`Duty`/`Peer`/`Kickout` (best-effort;
in-memory state always updates). The world learns member names from the guild roster.

**Party / corps (in-memory, `World/Party.cs`)**: `PARTYADD`, `PARTYJOIN`, `PARTYDEL`, `CHGPARTYCHIEF`,
`CHGPARTYTYPE`, `PARTYMANSTAT`; party ids allocated from a 0x100–0xFFFF pool; parties dissolve on the
last member leaving (and on logout). `MW_ENTERCHAR_REQ` carries party id/type/chief. Corps command-relay
is deferred (Phase 2b).

> Phase-2 simplification: DB-backed guild ops `await` on the batch task (the C++ used a separate DB
> thread). Fine at this scale; can move to a dedicated DB task if throughput needs it.

## Phase-2b handlers

The remaining **45** guild/tactics/corps handlers from the C++ dispatch are implemented (full inventory
in the git history). Grouped:

- **Guild long-tail** (`World/WorldService.Guild2b.cs`): cabinet list/putin/takeout, contribution,
  article list/add/del/update, fame, wanted add/del/list, volunteer/del/list/reply, point log, PvP
  record, skill-action relay, cooldown ack. State + best-effort DB persistence (`CSPGuild*` procs).
- **Tactics / sub-guild**: invite/answer, kickout, list, tactics-wanted add/del/list, tactics-volunteer
  ing/del/list, reply — with the `Tactics` roster, `CharTactics` index, and `GetCurGuild` (tactics
  takes priority over the regular guild), persisted via `TGuildTacticsAdd/Del`.
- **Corps** (`World/WorldService.Corps.cs`): ask/reply (form), leave, cmd (broadcast a squad chief's
  order to all corps members), change-commander, enemy-list/hp; plus the `CorpsJoin`/`PartyAttr`/
  `AddSquad`/`DelSquad` broadcast helpers. **Party move/recall** too.
- **Startup loads** (`WorldWorker.LoadGuildsAsync`): articles, cabinet items, tactics members,
  relations (ally/enemy), PvP records/rewards, stats, wanted/tactics-wanted ads and volunteers — each
  best-effort so a missing table/view is non-fatal.

> Fidelity: all Phase-2b senders were **byte-verified against `SSSender.cpp`/`TServer.cpp`**. Fixes
> applied during that pass include — cabinet/article list counts are `BYTE` (not DWORD); the cabinet
> item body follows `CTServer::WrapItem`; `GUILDPOINTLOG` count is `WORD` with `date,name,point` order;
> `GUILDPVPRECORD` iterates all members (week + recent record, 5 `PVPE_KILL_H..PVPE_WIN` points each);
> `GUILDWANTEDLIST`/`GUILDTACTICSWANTEDLIST`/`GUILDTACTICSVOLUNTEERLIST` entry layouts; `TACTICSANSWER`/
> `TACTICSREPLY`/`TACTICSINVITE` fields; and `ADDSQUAD`'s `chiefId, partyId(WORD), size(BYTE)` + full
> member loop. Verified already-correct: `GUILDINFO`, `GUILDMEMBERLIST`, `GUILDCONTRIBUTION`, `GUILDFAME`,
> `GUILDVOLUNTEERLIST`, `GUILDTACTICSLIST`, `PARTYATTR`, `CORPSJOIN`, `CORPSCMD`, `DELSQUAD`,
> `CHGCORPSCOMMANDER`.
>
> Remaining documented gaps (data not modelled in this phase, so emitted as 0 — the empty/no-data paths
> are exact): cabinet item `bItemID`/`bGem`/`wMoggItemID` + `IEV_*` ext-values (the Phase-2b cabinet query
> doesn't load them); `GUILDPVPRECORD` week-aggregate (recent record is real); and the `ADDCORPSUNIT`/
> `DELCORPSUNIT` per-unit packets on intra-corps membership changes (corps still forms/leaves/commands via
> the squad-level broadcasts). Guild DB persistence is best-effort (in-memory state always updates).

## Phase-3 handlers

**Friends** (`World/WorldService.Friend.cs`, `World/Social.cs`): `FRIENDASK`/`FRIENDREPLY` (mutual add),
`FRIENDERASE` (downgrade/remove on both sides), `FRIENDLIST` (the combined soulmate + group + friend
snapshot), `FRIENDGROUPMAKE`/`DELETE`/`CHANGE`/`NAME`, and `FRIENDPROTECTEDASK`. Friend state lives on
`Character` (`Friends`, `FriendGroups`); the list is loaded once on enter (`LoadFriendsAsync`, mirroring
`OnDM_FRIENDLIST_ACK` including the cross-char online/`FRIENDCONNECTION` sync), and `LeaveFriend` fires the
offline notify on logout. One-way "target" entries are excluded from the visible count, as in the C++.

**Soulmate** (`World/WorldService.Soulmate.cs`): `SOULMATESEARCH` (the level/sex/availability candidate
filter), `SOULMATEREG`, `SOULMATEEND` (silence window). `LoadSoulmatesAsync` mirrors `OnDM_SOULMATELIST_ACK`
(silence handling, partner online-link, `CheckSoulmateEnd` level-gap break, the post-load `MW_SOULMATE_REQ`),
with `RegSoulmate`/`SoulmateEnd`/`SoulmateDel` and `LeaveSoulmate` on logout.

**Rankings + monthly rollover** (`World/WorldService.Rank.cs`, `World/MonthRanker.cs`): startup load of the
per-country monthly ladder + warlord + last-month first-grade group (`WorldWorker.LoadRanksAsync`); the
ladder is pushed to each map on connect (`MW_MONTHRANKLIST_REQ`). `MONTHRANKUPDATE` performs the full
ladder-insert + warlord re-pick + ranged broadcast (the intricate sender, transcribed verbatim);
`FAMERANKUPDATE` and `WARLORDSAY` relay to all maps; `MONTHRANKRESETCHAR` relays to a char's other maps.
The **month-boundary rollover** runs on the 1-second timer (routed through the batch task so it stays
single-threaded): it freezes the fame podium + first-grade group, best-effort-persists via
`TInitMonthRank`/`TSaveMonthRank`, broadcasts `MONTHRANKRESET` + `FIRSTGRADEGROUP`, resets the live ladder,
and advances the rank month.

> Fidelity: `MonthRanker.WrapIn`/`WrapOut` and the friend/soulmate senders are transcribed field-for-field
> from `SSSender.cpp`/`TWorldType.h`. The C++ DB-thread round-trips (`OnDM_FRIEND*`/`OnDM_SOULMATE*`/
> `OnDM_MONTHRANKSAVE*`) are folded into single best-effort calls on the batch task — the net wire/state
> effect is preserved. Documented simplifications: the friend "protected"/blacklist check
> (`CSPProtectedSearch`) is treated as not-protected (the online-sync path always proceeds), and the
> rollover's `TInitMonthPvPoint` new-warlord recomputation is left to the next live `MONTHRANKUPDATE`
> rather than re-derived from the DB. The ranking tables are fed by the PvP/BoW/BR plane (Phase 4), so on
> the clean baseline they load empty and every rank DB call is best-effort.

## Phase-4 handlers (BoW)

**Battle of the Warlords** (`World/BowSystem.cs` = state, `World/WorldService.Bow.cs` = behaviour),
ported from `BowSystem.cpp` + the `TWorldSvr.cpp` module helpers + the `SSHandler.cpp` handlers:

- **Queue**: `ADDTOBOWQUEUE`/`CANCELBOWQUEUE` — solo and guild registration during the alarm window, plus
  the in-battle late-join path (returns the `BOWREG_FAIL+1` "joined late" code and teleports the straggler
  onto the smaller side). De-queue on logout.
- **Match build** (`BowCreateMatch`): guilds placed by their first registrant's country, the guild-swap
  rebalance for badly-skewed splits, solo fill of the smaller side, then the final ±`MaxNationDifference`
  trim — transcribed from `CreateMatch`.
- **Phase machine** (`BowSetStatus` + `BowOnTimerAsync`): the schedule window drives
  `ALARM → PEACE (build match) → BATTLE (`BOW_START`) → BODPEACE (`BOW_END`, 12-second countdown, then
  teleport-out)`; `BowUpdatePoints` applies a country's score and flips to BODPEACE on a wipe. The tick is
  routed through the batch task (the same path as the Phase-3 monthly rollover), so all BoW logic stays
  single-threaded.
- **Roster relays**: `ADDBOWPLAYERS`/`PREPAREFORBOW`/`ENDBOWWAR`/`RELEASESINGLEBOWPLAYER` to the dedicated
  BoW map (server id 30, registered on its `CONNECT`), `BOWTIMEUPDATE` broadcast to all maps, and
  `NOTIFYNONQUEUEDPLAYER` to everyone left out. Live-roster DB (`TAddBOWPlayer`/`TClearBOWPlayers`/
  `TDeleteSingleBOWPlayer`) is best-effort; config + schedule load from `TBOWSETTINGSCHART`/`TCUSTOMTIMECHART`.

> Fidelity: senders match `SSSender.cpp` field-for-field. The C++ used `GetTickCount()` for the BODPEACE
> countdown — mirrored with a monotonic millisecond clock. The match-build edge cases (guild-swap rebalance)
> are ported as-is. The dedicated BoW *map* server is the C++ cluster (not ported), so the queue/match/phase
> logic is verified via the simulated-map harness (a test impersonates server id 30 and drives the tick seam).

**Battle Royale** (`World/BrSystem.cs` = state, `World/WorldService.Br.cs` = behaviour), ported from
`BRSystem.cpp` + the `TWorldSvr.cpp` module helpers + `SSHandler.cpp`:

- **Premade teams**: `BRTEAMMATEADD`(invite)/`BRTEAMMATEADDRESULT`(accept)/`BRTEAMMATEDEL`(kick/leave) +
  the ready flags (`ADDTOBRQUEUE` with the `onlyReady` flag → `FlagPlayerReady`/`FlagTeamReady`), with
  chief promotion / team dissolve on leave (`ErasePlayerFromPremade`) and `UPDATEBRTEAM` pushes.
- **Solo queue + voting**: `ADDTOBRQUEUE` (join), map/mode votes (`VOTEFORBRMAP`). BR shares the BoW
  cancel handler (the client sends one cancel for both queues) and the `LEAVEBATTLEFIELD` release.
- **Match build** (`BrCreateMatch`): mode by vote (team) or random (solo); map by vote / random / small-
  turnout default; premade size rebalancing; then class-role bucketing (3v3 eyes/attack/support, 2v2
  attack/support) into fair teams with the leftover recombine — the live path of the C++ `CreateMatch`.
- **Phase machine** (`BrSetStatus` + `BrOnTimerAsync`): the same alarm→peace→battle→bodpeace cycle as BoW,
  plus the in-battle early end when only one team has a live member. Tick routed through the batch task.
- **Roster relays**: `ADDBRTEAMS`/`PREPAREFORBR`/`ENDBRWAR`/`RELEASESINGLEBRPLAYER` to the BR map (server
  id 50), `BRTIMEUPDATE` broadcast; config from `TBRSETTINGSCHART` + `TCUSTOMTIMECHART`; live-roster DB
  (`TAddBRPlayer`/`TClearBRPlayers`/`TDeleteSingleBRPlayer`) best-effort.

> Fidelity: senders byte-verified against `SSSender.cpp` (field order + widths). The list-bearing senders
> (`UPDATEBRTEAM`/`ADDBRTEAMS`/`ENDBRWAR`, and BoW's `ADDBOWPLAYERS`/`ENDBOWWAR`) iterate in ascending
> charId / team-key order to match the C++ `std::map` iteration exactly. The C++ `CreateMatch` carried a
> large dead/commented block — only the live path is ported. `random_shuffle` is mirrored with a
> Fisher-Yates shuffle. The in-battle early-end
> uses the per-player BR channel, which the unported BR *map* would set, so on this server it never fires
> early (the timed BODPEACE ends the match) — documented, not a guess.

**Scheduled battle-time machine** (`World/BattleTime.cs` = state, `World/WorldService.Battle.cs` =
behaviour), ported from the `OnTimer` battle-time block in `TWorldSvr.cpp` + `OnSM_BATTLESTATUS_REQ`:

- Each tick picks the **active window** — castle (when today matches its day-of-week), else the sky-garden
  window, else the local field — with a **mission override** when the mission window is open. (Unsigned
  arithmetic wraps exactly as the C++ `DWORD` math, so a `BattleStart` of 0 simply isn't selected.)
- Runs the per-window `NORMAL → (alarm pings) → BATTLE → (alarm pings) → PEACE → NORMAL` cycle with the
  C++ alarm cadences (600/120/10s before start, 60/10s before end) and broadcasts `LOCALENABLE` /
  `CASTLEENABLE` / `MISSIONENABLE` to every map on each alarm/transition. The mission window advances its
  start to the next custom time on PEACE→NORMAL. Driven through the batch task; config from
  `TBATTLETIMECHART` + the mission slots in `TCUSTOMTIMECHART` (local peace hard-set to 180s, as in C++).

> Fidelity: the active-window selection, the cycle, and the enable-packet layouts match the C++. The
> sky-garden enable is compiled out in the shipped build (`#ifdef SKYGARDEN`), so it is selected but emits
> nothing — preserved. The non-mission PEACE side effects (guild week-record recalc, castle-war-info clear)
> are deferred subsystems and left as a no-op beyond the broadcast — documented.

**Tournament config + announce** (`World/Tournament.cs` = state, `World/WorldService.Tournament.cs` =
behaviour), ported from `TournamentInfo` + the `OnMW_TOURNAMENT_ACK` dispatcher:

- Loads the bracket config — entries (`TTOURNAMENTCHART`) + rewards (`TTOURNAMENTREWARDCHART`) — and the
  derived `firstGroupCount = min(entries·4/3 + 1.999, 17)` (verbatim from the C++).
- Broadcasts `MW_TOURNAMENTINFO_REQ` (entries + per-entry rewards) to each map on connect; the read-only
  sub-commands of `MW_TOURNAMENT_ACK` (`SCHEDULE`/`APPLYINFO`/`JOINLIST`/`MATCHLIST`/`EVENTLIST`) return
  the same info. Entries are emitted in ascending `entryId` order to match the C++ `std::map`.

Registration gameplay (`TournamentApply`/`ApplyInfo`/`JoinList`/`PartyAdd`/`PartyDel`/`PartyList`/
`MatchList`, dispatched from `MW_TOURNAMENT_ACK` by its `wProtocol` sub-command):

- **Apply** with the C++ gating: country check, the 1st-grade-group seeding gate (during the 1st-grade
  step only `m_arFirstGradeGroup` members register — read from the Phase-3 rank data), the duplicate
  HWID/IP guard, slot-full check, then registration into the entry's 1st/normal roster + the player index.
- **Apply-info / join-list / match-list** rebuild the C++ per-entry views (class-masked reward filtering
  via `dwClass & (1 << class)`, free-slot/normal counts, the seeded match bracket with per-round results).
- **Party add/del/list** for the party division (chief-led, ≤6, level-gated). Composite replies are
  `MW_TOURNAMENT_REQ` packets carrying the echoed sub-protocol, with entries/players in ascending
  `entryId`/`charId` order to match the C++ `std::map`.

Scheduler + bracket build (`World/WorldService.TournamentSched.cs`), ported from `SetTournamentTime` +
the OnTimer schedule walker + `OnSM_TOURNAMENT_REQ` + `TournamentSelectPlayer`/`TNMTMatch`/`TournamentUpdate`:

- **Scheduler date-math** (`SetTournamentTime`): from the `BT_TOURNAMENT` window (Nth weekday of the
  month at the window's battle-start second) it chains each step's absolute start/end back-to-back, for
  this month then next; the step list drives the timer.
- **Schedule walker** (on the 1-second tick): fires each step once as its start passes — advancing the
  group/step, recomputing the prize base on a group change, and broadcasting `TOURNAMENTENABLE` (with the
  next step's start) to every map — and re-runs `SetTournamentTime` + `TournamentUpdate` at the END step.
- **Bracket build** (`TournamentSelectPlayer` + `TNMTMatch`): keeps the 1st-grade seeds, fills the rest of
  the 8 slots by a country-balanced, level-weighted random draw, then orders the slots `{0,6,4,2,3,5,7,1}`
  preferring the lowest month-rank and a cross-country opponent on odd slots; broadcasts `TOURNAMENTMATCH`.

Match results + betting (`World/WorldService.TournamentResult.cs`), ported from `OnMW_TOURNAMENTRESULT_ACK`,
`OnMW_TOURNAMENTENTERGATE_ACK`, and the `TournamentEvent*` (betting) functions:

- **Results** — a reported QF/SF/Final outcome marks the per-round `TnmtWin` result on the winner/loser
  and their party members, fans `TOURNAMENTRESULT` out to every map, and at the final pays each backer of
  the champion (`TOURNAMENTBATPOINT`) using the C++ integer odds (`base · bet · floor(pool/targetPool)`).
- **Betting** — `ENTERGATE` grants tickets from the money brought to the arena (only past the ENTER step);
  `EVENTJOIN` places/replaces a bet on a bracket player; `EVENTLIST`/`EVENTINFO` are the odds views
  (`FindBatter`/`JoinBatting`/`ResetBatting`/`GetBattingAmount`).

> Scope: config/announce + registration & parties + scheduler + rank-seeded bracket + results + betting —
> the full player-facing tournament. **Remaining**: the **event sub-tournament admin** (the `TET_*`
> add/remove-tournament commands on the `CT_*` control plane) — these are driven by the control server,
> which this port registers but doesn't otherwise implement, so there's no live driver for them yet. The
> reward fee-back/result/status DB posts are best-effort gaps; multi-schedule overlap + crash-recovery
> step-resume are collapsed to the single base tournament; per-char rank is 0 (affects only seeding order).

## Build & test

```bash
dotnet build Servers/TWorldSvr.Net/TWorldSvr.slnx -c Release
dotnet test  Servers/TWorldSvr.Net/TWorldSvr.slnx -c Release
```

- **Protocol tests** + the **end-to-end test** run with no DB. The e2e test boots the real server and
  drives a simulated map server through `CONNECT → ADDCHAR → ENTERSVR_REQ → CHARDATA → ENTERCHAR_REQ →
  ENTERCHAR_ACK → chat relay`.
- **DB smoke tests** run only when `TWORLD_TEST_GLOBAL` / `TWORLD_TEST_GAME` are set (else they no-op).

## Run

### Docker (part of the root stack)

```bash
cd ../..          # repo root
docker compose up --build worldsvr
```

Brings up SQL Server (if not already), then the world server on port **3815**. **It will boot, load the
nation, and listen — but stay idle until a map server connects**, because TMap/TControl are the
C++ cluster and aren't part of this stack.

### Console executable

```powershell
pwsh ./publish-console.ps1   # -> publish/win-x64/TWorld.Server.exe, publish/linux-x64/TWorld.Server
```

## Caveat / verification

Unlike the login server (which the game client hits directly), TWorld's only live peers are other
servers. Phase 1 is verified by the **simulated map-server harness** (no C++ cluster needed). Real
integration — pointing the C++ `TMapSvr` at this server on `3815` — is a documented follow-up and would
exercise the byte-level fidelity of `MW_ENTERCHAR_REQ` (which carries the full character + guild/party
state; Phase 1 emits the faithful field order with zeros for the deferred subsystems).

## Roadmap

- **P2 (done)** Guild (DB load + lifecycle + persistence) + in-memory parties.
- **P2b (done)** Guild long-tail (cabinet/articles/fame/contribution/wanted/volunteer/pvp/stats),
  tactics (sub-guild), corps command-relay, party move/recall.
- **P3 (done)** Friends/Soulmate, PvP rankings + monthly rollover.
- **P4 (done — pending CT event-admin)** Battle of the Warlords + Battle Royale + the local/castle/mission
  battle-time machine + the **full tournament** (config/announce, registration & parties, scheduler
  date-math, rank-seeded bracket build, match results, and betting). The only tournament remainder is the
  event sub-tournament admin commands, which ride the not-yet-implemented `CT_*` control plane.
- **P5 (done — full handler surface)** The remaining `CT_*` admin (item find/state, cash-mall gift catalog +
  take-check, the event/lottery/quarter subsystem, the **complete** GM tournament-event admin incl. the
  per-event schedule machinery + the PLAYERADD char-info lookup), `DM_ACTIVECHARUPDATE` (active-char nation
  rebuild), and `SM_GUILDDISORGANIZATION` + the guild auto-extinction timer. Every `TWorldSvr` handler is now
  ported, and the DM-thread DB persistence the C++ does (tournament apply/clear/status/result/payback,
  cash-item sale, tournament-event schedule/entry/reward, help message — 17 procs, all verified present in
  `TGame_gsp`) is wired. The `RW_*` relay plane is intentionally **not** ported — there is no `TRelaySvr` in
  these sources. See `PORT_STATUS.md`.
