# Isolated quest/mail database checks

Run `test-gor-content.ps1 -RunLifecycle` for migration 055: 15 import scenarios and 44 actual SQL
template executions cover exact recipe inputs/outputs, all repaired mission rewards, native
inventory/quest reload and repeated Corneus exchanges. Existing class restrictions and caps stay
in the frozen plan. With `-WakeUp`, the same fixture tests migration 056 using 13 import scenarios
and two execution cases: canonical map-2010-to-map-0 delivery with persisted acceptance/completion,
plus the retained legacy switch responding to completion 7125. Trial prerequisites are synthetic;
actual portal playthrough is not asserted. Fixtures copy static charts and empty native storage
shapes only; generated `4DevQuestTest_content_*` databases are removed in `finally`.
`--gor-content-readonly <plan.json>` separately checks deployed SQL through the production template
loader and performs zero SQL writes. These data-only changes do not require another Map image.

Run `test-gor-reconstruction.ps1 -RunLifecycle` for migration 054. Sixteen cases check exact
definitions, the six approved population anchors, opt-in 3/5 arenas, reruns, preserved unrelated
rows and atomic rejection guards. Five lifecycle scenarios load the actual migrated SQL templates
and use native quest/inventory procedures: partial hunts survive fresh Map instances, five repeated
producer cycles grant five durable seals, turn-in consumes them, and all four type-28 objectives
survive reload and prevent duplicate rewards. The storage test injects the arena result counter;
`ReconstructedArenaTests` independently exercises real two-round result production for all four
missions, split rounds, mode isolation, partial teams, forfeit, abandonment and queued payment.
Only generated `4DevQuestTest_gor_*` databases receive synthetic player rows, then are removed.
`--gor-reconstruction-readonly` loads live templates and initializes the six populations offline;
it performs zero SQL writes. These are separate from actual client verification.

The full save/process fixture additionally checks exact RT_POINT persistence/replay through native
PvP procedures. Its schema export now includes `TGetPvPRecord` and `TGetMonthPvPoint`; use the complete
current object list while retaining the original pre-repair procedure bodies (including `TSaveChar`
and the pre-044 territory procedures) for the deliberate defect-reproduction tests.

Run `test-gor-pact.ps1 -RunLifecycle` for migration 053 and the SQL-loaded pact lifecycle. Ten
scenarios check canonical definitions, reruns, coupled legacy quarantine and seven atomic rejection
guards. Four more use native `TSaveQuest`/`TSaveQuestTerm`, new database readers and fresh MapService
instances for acceptance, rewards, completion reload and replay prevention. These cover the quest
store; the tool explicitly does not claim a gold/EXP native-table round trip or full MapWorker/client
restart. Only synthetic player state is written to a generated `4DevQuestTest_pakt_*` fixture, removed
in `finally`. Credentials pass through the child process environment without being printed or saved.
`QuestSoulRewardTests`, `GorPactQuestTests` and `BattleArenaQuestGuardTests` add eleven actor cases;
the full Map.Server suite passes 1,853. Evidence: `Gameforge/gor-remaining-quests.json`.

`Tools/GameforgeReference/audit_gor_publisher_quests.py` performs the separate read-only publisher
check. Supply fresh `--cache` and `--output` directories; it captures every Tebekut catalogue page
at levels 0–140, the existing pinned Gor scope and seal producer 3210. The current evidence contains
134 Tebekut entries and 140 detail pages, including NPCs, objectives, rewards and source conflicts.
Six parser regressions cover dialogue scope, explicit talk targets, empty reward fields, currency/EXP,
conflicting arena points and error-page rejection. Evidence: `Gameforge/gor-publisher-quests.json`.
An absent website reward field never authorizes removing a pinned reward or enabling an unsupported
quest. Last-speaker NPCs are labelled as inferences; no SQL writes occur in this tool.

Run `test-gor-event-activation.ps1` to test migration 052 in a fresh `4DevQuestTest_gorevents_*`
database. It seeds the frozen disabled identities, checks all six restored mission/completion pairs,
reruns and preservation, and rejects six conflicting baselines/dependencies. Nine scenarios pass;
cleanup runs in `finally` and no player rows are copied. Six `GorEventActivationTests` cases exercise
NPC offers, acceptance, ingredients, rewards, replay and repeated acceptance through actual actor
packets. The remaining seven requested missions and event-item acquisition are not certified by
these checks. Evidence: `Gameforge/gor-quest-activation.json`.

`test-gor-quest-givers.ps1` tests migration 051 in a generated `4DevQuestTest_gor_*` database with
static charts only. Seven scenarios verify the three giver changes, reruns, exact preservation and
atomic rejection of an unexpected giver, fetch count, reward or NPC map. Cleanup runs in `finally`.
The three `GorPaperQuestTests` cases drive actual actor offer/accept/turn-in packets and reject replay
rewards. `Tools/GameforgeReference/audit_gor_quests.py` generates the broader read-only Gor report;
its scope classifier has six regression cases. Missing source contracts are reported, never invented.

`test-quest-completion-npcs.ps1` exercises migration 050 against a fresh generated
`4DevQuestTest_turnin_*` database containing static chart copies only. Eight scenarios cover all
30 repairs, reruns, exact preservation of the other fields/charts and five atomic conflict rejections.
The frozen plan is `Gameforge/quest-turn-in-npcs/audit-before.json`; the fixture is removed in `finally`.
The reference classifier has 12 Python regression cases in `Tools/GameforgeReference/test_quest_turn_in_npcs.py`.

For the Diannina přeměna completion-NPC repair, run `test-dianna-turn-in.ps1`. It creates a fresh
generated `4DevQuestTest_dianna_*` database in the selected SQL container, copies only the relevant
static chart rows, and exercises migration 049's repair, rerun and three conflict guards. No player
rows are copied or modified. Cleanup drops only the generated database in `finally`. The four actor
regressions in `DiannaTurnInTests` verify the corresponding NPC offer, completion and objective guards.

Run `prepare.sql` through sqlcmd with `-v QuestTestDb=4DevQuestTest_local` against the development SQL
instance. It creates a fresh database, copies empty storage/quest table shapes and the fifteen native
stored procedures, and uses minimal synthetic character/text fixtures. It refuses an existing database.
The source `TGame_gsp` is read only; this is not a full database backup or a production migration.

Set `QUEST_TEST_DB` to a SQL connection string pointing at that new `4DevQuestTest_*` database, then run:

```powershell
dotnet run --project Tools/QuestEngineCheck -c Release
```

The executable refuses ordinary database names. It inserts synthetic rows and temporary CHECK constraints
to test twenty-nine scenarios: invalid mail input, atomic stack reservation, item attributes, concurrent/offline
delivery, attachment rollback/retry, quest progress reload, atomic progress rollback, completion reload,
concurrent capped issuance/reload, configurable limits, issuance-counter rollback, uncapped counter wrap
runtime timer snapshots/reload through a fresh MapService instance, header/term read consistency,
reader lock release, failed term-query rollback/restoration and cancellation recovery, plus twelve
character-state checks described below.
It prints JSON only after all checks pass; any failed check exits with an exception. Use a fresh test
database for each run. The created database is disposable after inspecting the result.

These checks exercise Map/World data assemblies and Map timer snapshot/load logic against SQL Server. A fresh
service instance is not a restart of the full running game server. These checks do not certify
Gameforge content, a complete real-client playthrough, a process crash between independent gameplay saves,
or exactly-once issuance of uncapped original events. Capped issuance is protected by its durable quest
counter; a process crash between committing a reservation and running child quests remains a separate
recovery case. See `Gameforge/quest-engine.json`.

## Save recovery checks

Recovery storage is now SQL only (`TCharacterSaveQueue` and `TCharacterSaveOwner`, migration 043).
Scope arguments below name SQL ownership domains; they are not filesystem paths. Earlier file-journal
evidence in Gameforge reports is historical and superseded by `Gameforge/sql-save-queue.json`.

After the normal check initialized the synthetic character, run this on the same isolated database:

```powershell
dotnet run --project Tools/QuestEngineCheck -c Release -- --save-check <fresh-sql-queue-scope> <absolute-path-to-043_character_save_queue.sql>
```

The parent starts separate writer/recovery processes. The writer exits immediately after committing
quest progress, with the complete snapshot and a later item mutation still unacknowledged. Recovery
first encounters an injected SQL constraint, retains the records, then succeeds in a new process.
Seven scenarios cover process boundaries, order, item attributes, idempotent hotkeys, page rollback,
acknowledgement and strict rejection of missing quest procedures. Successful output is JSON.

The SQL callback deliberately exercises only native quest/item/hotkey stages. It does not claim a full
MapWorker restart or complete SaveChar/global-account/pet/companion recovery. Those remain acceptance
requirements in `Gameforge/quest-save-recovery.json`. Use a fresh test database and SQL queue scope
for each complete run. Production player databases are rejected in both parent and child modes.

The quest read-consistency check holds an exclusive lock on a synthetic term row, waits until the real
LoadQuestsAsync reader reaches it, then attempts a header update with a short SQL lock timeout. The
update must stay blocked until the reader finishes both queries. This verifies actual transaction/lock
behavior, rather than relying on nondeterministic concurrent loops. A missing term table must throw
without returning partial headers and must release the read transaction before later writes.

## Character-state checks

The default run also checks native companion/pet insert/update and fresh-connection reload, companion
stats/equipment/selection/medals, account mount/saddle ownership, required-query failures and retry,
premium/EXP loading with the optional PC-bang procedure absent, recall/buff fields, allegiance and
malformed hotkey schemas. Failures are injected by temporarily renaming fixture tables/procedures;
restoration happens in finally blocks. All writes stay in the fresh 4DevQuestTest_* database.

The fixture copies empty pet/companion/mount/recall/aid tables and a local table shaped like the premium
view. It does not copy a cross-database view or player data. The inherited TGetMountSaddle module retained
an old _copy CREATE name after sp_rename; only that copied header is corrected for the fixture, with its
body preserved. The synthetic character/account columns support native pet ownership resolution.
These are component-level SQL checks, not a full SaveChar/global-account roundtrip or MapWorker restart.

## Complete snapshot callback in a disposable SQL instance

`FullSaveCheck` requires a SEPARATE SQL Server instance because native character saves and the C#
premium writer address TGLOBAL_GSP directly. Never run this fixture on the normal game SQL instance.

1. Export metadata only from the reference instance with `export-full-schema.sql`, using sqlcmd
   `-b -m 1 -y 0`. Save its output as the fixture DDL. No player row is read/exported.
2. Start a fresh disposable SQL container named with prefix `4dev-full-save-sql-`, with its own generated
   password and no published host port. Run that DDL there using sqlcmd `-b`; its first batch refuses
   either existing native database. The final batch creates a synthetic character and fixture marker.
3. Set `FULL_SAVE_DB` to that isolated instance's TGame_gsp connection string. The test checks BOTH the
   server prefix and database marker before writing. Supply a fresh SQL queue scope:

```powershell
dotnet run --project Tools/QuestEngineCheck -c Release -- --full-check <fresh-sql-queue-scope> <absolute-path-to-041_character_save_integrity.sql>
```

4. Stop/remove the disposable container and remove its credential file after saving the JSON result.
   Keep credentials in ignored environment files; do not print them or put them in shell arguments.

The exporter copies 42 table shapes (native columns/defaults/computed definitions/PKs) and 37 native
procedure definitions. It does not clone foreign keys, triggers, non-primary indexes, production rows
or all game content. The synthetic game/global databases retain their native names ONLY inside this
separate instance. It is a focused persistence fixture, not a production backup.

Ten checks reproduce the ignored stat fields/private gift branch, apply migration 041 twice, verify
fresh-connection stat persistence and nonzero native save rejection, then run the actual MapService
journal callback. A writer is killed during a paused quest write after earlier stages commit. Another
process replays the whole snapshot. A late companion-item constraint exercises retry after inventory,
hotkeys and other stages already committed. The final state and empty acknowledged journal are checked
again in a new process. This verifies real callback/SQL process recovery, not a full MapWorker restart
or a client playthrough. The script applies migration 041 to the fixture, never the running server.

The same full check now runs seven additional quest lifecycle scenarios in `QuestLifecycleCheck`.
An isolated synthetic character uses the real client/World/timer dispatchers to accept a mission,
receive its starter item, advance a hunt, turn in a selected item/money/skill reward, start a child
mission, abandon/reaccept and expire a timer. Each mutating child exits immediately without logout
or SaveAll; the next process recovers the event journal and reloads SQL before continuing. A paused
quest trigger prevents premature acknowledgement during these exits. Repeated turn-in after reload
must not pay again, and an expired timer must stay failed. This verifies the interpreter's event
checkpoints and native persistence for that synthetic graph, not official content or a real client
playthrough. Parent and child modes both pass the disposable-instance safety checks.

`QuestMailRecoveryCheck` adds eleven mail continuation scenarios, after applying migration 042 twice
inside the fixture. A synthetic graph has parent and sibling mail, a child mission/item/skill, nested
mail and a repeatable money craft. Separate processes exit after reservation, before child publication,
after native snapshot/nested reservation and after acknowledgement. Injected SQL queue insertion failures
also exercise the real disconnect path; failed initial publication must not leak a starter item via
an independent direct-item record. Each successful recovery enters through CS_CONREADY, and a second
entry must leave all counts/items/money unchanged. Same-ID concurrent uncapped requests deduplicate;
an outbox constraint rolls attachments/counter back. A missing outbox fails as a required query.
The tool also verifies that unsupported world-effect children remain pending and cannot leak partial
character state. It does not certify those world effects, multi-Map ownership, real-client play or a
full MapWorker restart. Migrations 041/042/043 are applied only to the disposable instance.

Five `SqlSaveQueueCheck` scenarios verify actual SQL queue persistence, exclusive ownership/release,
identity/corruption rejection, same-ID enqueue retry, loss of the SQL lease and required-schema failure.
Migration 043 applies twice. An ordinary quest enqueue failure also checks that its starter item and
progress cannot escape as independent writes. These account for 33 SQL scenarios. The fixture can
run in an SDK container with a read-only root filesystem, writable temporary build directories and
an output-only results mount; the Map recovery code needs no state directory or file volume.

Eleven `TerritoryOccupationCheck` scenarios extend the full fixture to 44 checks. The inherited Sky
Garden procedure is first reproduced changing two synthetic targets. Migration 044 then applies
twice, scopes the target and rejects missing mission/garden IDs. An unknown existing SQL repair
must reject the migration and roll both alterations back. Native fort ownership/history and castle
owner/country/week/applicant reset roundtrip through the real adapters; guildless castle capture
rejects. World applicant insert/update/delete roundtrips preserve castle/camp and avoid duplicate
rows; injected applicant/mission SQL failures preserve previously confirmed data. All rows are
synthetic in the separate instance. These checks do not verify active-war restart or delivery of
the complete result/reward sequence after a crash. Migration 044 is never applied to live SQL by
this tool. See `Gameforge/territory-wars.json`.

Fourteen `OperatorPostRecoveryCheck` scenarios extend the full fixture to 58 checks. Migration 045
adds durable server reward receipts. Native header/money/multiple-item writes, DB item allocation,
containment and receipt are checked together, including failures on a later item, silent native
insertion failure, receipt failure, ID collision, missing recipient, concurrent replay and replay
after collection/deletion. Plain notices and money-only packages retain their type and coins.
Separate child processes use the real Sky Garden reward handler and SQL queue, then exit before
delivery or after its SQL commit but before queue acknowledgement. Fresh MapService recovery must
deliver the accepted letter once; a rejected attachment also remains queued for later recovery.
This verifies per-letter SQL/process recovery, not complete war settlement, World replay, active-war
restart or a full MapWorker/client playthrough. Only the disposable instance receives migration 045.

`ExchangeRecoveryCheck` adds six SQL scenarios to the native full fixture (64 total). Two synthetic
characters perform the real ASK/RLY/ADD/confirm sequence. The writer is killed while the second native
character save is paused, after the first committed. A fresh process must recover both currencies,
inventories and item attributes from one SQL queue record; another replay must change nothing. Failed
publication cannot send success or leak through logout/direct item saves. A later second-player SQL
failure must retain the pair and deny admission until a new process finishes it. This is single-Map
queue recovery, not an atomic cross-player native SQL transaction or distributed ownership guarantee.

To run the current Map suite, all 64 native checks and the 14 framework checks in a fresh SQL instance:

```powershell
./Tools/QuestEngineCheck/test-full-save.ps1 -SchemaPath <trusted-synthetic-full-fixture.sql>
```

The schema must contain only empty native table/procedure definitions, the synthetic full-save seed
and `4DevDisposableFixture=full-save-v1`. The script creates its own network/container, never mounts
the running game database, refuses result paths outside the repository and removes only its created
container/network and disposable credential files. Test reports are diagnostic output; player state
and accepted exchange recovery reside in SQL only. `-KeepFixture` retains the isolated instance for
diagnosis. Runtime package selection matches the local 10.0.13 cache for offline repeatability.

`WelcomeMailCheck` adds nine SQL/process scenarios (73 native checks in total). Migration 048 applies
twice with the feature disabled, starting with sqlcmd's QUOTED_IDENTIFIER OFF, and preserves existing
characters. The migration explicitly supplies the required index SET options. Synthetic character inserts verify
birth-only enrollment; concurrent preparation freezes one payload/operation ID. Configuration changes,
attachment rollback, collection/deletion replay, disabled-at-entry and invalid/deleted-character cases
preserve the intended eligibility. A child uses the real CONREADY entry path with the existing quest
admission gate satisfied, freezes the letter, then exits immediately without logout. Separate recovery
processes create one native letter with both attachments and retain its permanent receipt. No live
characters or production settings are touched. These checks do not verify an actual client or a full
MapWorker restart; see `Gameforge/welcome-mail.json`.

After the 73 native checks, run the Wolverine integration on the same disposable fixture:

```powershell
dotnet run --project Tools/QuestEngineCheck -c Release -- --full-wolverine-check <scope> <schema-output.sql> <absolute-path-to-046_wolverine_mail.sql>
```

The same server-prefix/catalog/property guard applies before any child/host can run. The test host
explicitly provisions isolated Wolverine schemas, then checks all mail store contracts, native claim
rollback/multi-item delete guards, atomic reward/outbox failure, stable legacy queue forwarding and
repeatable migration 046. Producer and consumer processes exit forcibly before delivery and after
native commit; a new host with auto-DDL off must recover inbox/outbox without another reward. Actor
notifications use a test inbox drainer; separate World socket regressions verify the native recipient
route. These 14 framework scenarios are additional to the 73 native scenarios (87 combined), not a full MapWorker/
WorldWorker/client playthrough. Production never enables the fixture's schema auto-provisioning or
shortened orphan-recovery intervals. See `Gameforge/wolverine-mail.json`.

Inventory slot repair regression (migration 047): start a fresh disposable SQL container whose name
begins `4dev-inventory-sql-`, wait until SQL is ready, then from the repository root run:

```powershell
python Tools/QuestEngineCheck/check-inventory-slot-repair.py --container 4dev-inventory-sql-check --output .runtime/inventory-slot-check
```

The helper refuses other container names and starts with `CREATE DATABASE TGame_gsp`; an existing
catalog fails before fixture writes. It creates synthetic minimal tables and checks visible overflow,
orphan and duplicate-slot recovery, identity/count/attribute preservation, unrelated storage, repeat
application, no-space rollback and refusal with pending saves. Never point it at a game instance.
