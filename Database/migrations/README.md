# Database migrations

SQL migrations applied automatically by the `mssql-importer` service (after the `.bak` restore) and
tracked in `TGlobal_gsp.dbo._SchemaMigrations`. Each file is applied once, in filename order.

## Conventions

- Name files `NNN_short_description.sql` (zero-padded, e.g. `001_...`, `002_...`) so they sort correctly.
- **Start each file by selecting its database**: `USE [TGame_gsp];` or `USE [TGlobal_gsp];` (the runner
  connects to `master`).
- **Make every migration idempotent and re-runnable** (guard with `IF ... EXISTS` / `IF COL_LENGTH(...)`),
  so a partially-applied or hand-run migration can't wedge the importer.
- Separate batches with `GO` where needed (e.g. before/after `ALTER PROCEDURE`).
- To reference the `.env` SA password without committing the secret, use the sqlcmd variable
  `'$(SA_PASSWORD)'` — the runner passes it via `-v SA_PASSWORD=...`. (The value must not contain a
  single quote, since it's substituted into a string literal.)
- A failing migration aborts the importer and is **not** recorded, so it will be retried next run.

## Running

`055_gor_content_repairs.sql` aligns 32 existing recipes and nine aligned mission reward lists
with the pinned client, adds recipe 10634 at Beshil, and restores Corneus missions 5917/5919 with
their canonical completion actions. Existing recipe NPCs, class restrictions, caps and aligned
mission progress remain intact. Source type-0 ingredient slots remain inactive. Conflicting identities,
missing dependencies, legacy Corneus progress and pending character snapshots reject atomically.
Fifteen import scenarios and 44 SQL-loaded execution/inventory/quest reload cases pass, including
repeatable Corneus exchanges. No runtime code changes or invented drop rates are involved.

`056_gor_wakeup_quest.sql` restores mission 7307 (Parakelius 25223 on map 2010 to Biseira 26003
on map 0), completion 9720 and prerequisite 7243. The inherited switch action occupying ID 7307
is preserved under internal ID 40007307, with its trigger 7125, condition and switch 363 unchanged.
It rejects saved legacy progress, occupied identities and external references. Thirteen import
scenarios and two SQL-loaded execution cases verify persistence, destination-map gating and the
old switch action. This does not certify the whole trial/portal chain or alter its balance.
Both migrations are data-only; restart Map to reload. Evidence:
[`gor-content-repairs.json`](../../Gameforge/gor-content-repairs.json).

`054_gor_reconstructed_missions.sql` applies the user's approved exception for the five remaining
missions (1082/1084/1203/1208/3209). It restores pinned NPCs, conditions, objectives and rewards,
adds their canonical completion actions and repeatable seal producer 3210, and quarantines six
incompatible legacy helpers. It adds one stationary population for each required monster kind
at the pinned navigation target, with 60-second delay and 100% spawn chance. These population
parameters and the two-round PvP-only match are reconstruction choices, not publisher certification.
Arena rows 1/2 explicitly opt in through `bQuestMatch`; sizes become 3/5, existing fees stay intact.
Deploy the new Map image alongside this migration (type-28 results and default RT_POINT support).
Sixteen isolated import scenarios and five SQL-loaded lifecycle scenarios cover native quest and
inventory reload, repeatable seals, consumed ingredients and replay refusal. Legacy progress or
identity conflicts reject atomically. New Year mission 291 remains disabled. See
[`gor-remaining-quests.json`](../../Gameforge/gor-remaining-quests.json).

`053_gor_tripartite_pact.sql` restores level-90 mission 547 at Bílý sokol (26200), completing at
Randaygaron (26436) through action 548. Its exact rewards are 1,782 money units, 855 EXP and 167
Soul Power; deploy the RT_SOUL-capable Map image with the data. Existing repeat cap 1 is retained.
Inherited mission 3200 has a stale prerequisite on 547 but is a different completion action in the
pinned client; it is quarantined before that dependency could expose obsolete content. The import
rejects any saved target progress, unexpected active dependencies, missing NPCs or changed semantic
baselines, including unused reward metadata. Ten isolated migration scenarios and four native
quest-store lifecycle scenarios pass. Migration 054 later resolves the other five regular missions
under an explicit reconstruction exception; the New Year event stays disabled. See
[`gor-remaining-quests.json`](../../Gameforge/gor-remaining-quests.json).

`052_gor_event_missions.sql` restores six of the thirteen requested disabled Gor missions: 233–237
at Elyssa and 2398 at Pivař, together with their six completion actions. NPC labels, item objectives
and rewards come from the pinned client; existing repeat caps are retained. The import checks an
exact semantic baseline, NPC/item dependencies, running legacy quests and unexpected child actions.
Nine isolated SQL scenarios and six new actor cases pass. It changes static SQL charts only, with no
invented event drops or calendar rules. Seven missions still need missing reward/objective/chain
contracts; see [`gor-quest-activation.json`](../../Gameforge/gor-quest-activation.json). Restart Map to reload.

`051_gor_paper_quest_givers.sql` completes the scoped Gor repair by assigning missions 8883/8885/8887
to Houba (22919), matching both original start and completion markers. Their completion actions were
already repaired by 050. Mission identities, objectives, rewards, conditions and NPC identity are
guarded; only the three acceptance trigger IDs change. Repeat caps and player progress are retained.
Seven isolated SQL checks and three actor acceptance/turn-in cases pass. See [`gor-quests.json`](../../Gameforge/gor-quests.json)
for the full Gor/Tebekut audit and its incomplete quest-chain/reward coverage. Restart Map to reload.

`050_quest_completion_npcs.sql` corrects 30 further completion NPC assignments from pinned original
quest/navigation records. It validates mission/completion identities, exact objectives, completion
conditions and NPC map/country before one atomic update; reruns retain already corrected values.
Only completion trigger IDs change. All 1,497 existing completion actions were audited; 699 NPC
actions still need compatible reference/graph evidence. See [`quest-turn-in-npcs.json`](../../Gameforge/quest-turn-in-npcs.json)
and its complete before/after reports. Restart Map after applying the migration.

`049_dianna_quest_turn_in.sql` corrects only quest 8007's completion NPC from 20004 to Karl (20012).
The pinned `TQNode.qpd` explicitly assigns mission 8006's completion marker to Karl; existing SQL
already has both objectives satisfied on the reported character. The migration guards quest identity,
completion term and NPC/map, preserves progress and rewards, and accepts reruns. Restart Map to reload
the quest trigger index. Evidence and remaining reward/client gaps: [`dianna-quest-turn-in.json`](../../Gameforge/dianna-quest-turn-in.json).

`048_character_welcome_mail.sql` adds the explicitly requested custom welcome gift. It starts disabled
and never enrolls existing characters. A trigger registers characters born while SQL settings are
enabled; the Map actor freezes their letter at the first authenticated, playable main-session entry.
Deploy the matching Map image before running [`enable-welcome-mail.sql`](../enable-welcome-mail.sql).
That activation selects 5 small life potions (8001) and 5 small mana potions (8101), after checking the
native item chart. Settings and item counts live in `TWelcomeMailSettings` / `TWelcomeMailItem`.

`TCharacterWelcomeMail` stores the immutable first-entry intent and dispatch acknowledgement in SQL.
Wolverine SQL transport and the permanent native operator-mail receipt recover accepted delivery
without another gift after replay, collection or deletion. There is no filesystem game-state journal.
Setting `Enabled=0` stops enrollment and preparation; previously prepared letters still recover.
An eligible character entering while disabled consumes eligibility without postponing a gift to a
later login. Configuration changes affect unprepared letters only. Migration reruns preserve settings
and receipts; there is no existing-character backfill. See [`welcome-mail.json`](../../Gameforge/welcome-mail.json).

`026_gameforge_monster_levels_hp.sql` restores the 213 exact level/HP pairs documented in
[`Gameforge/monster-restoration.json`](../../Gameforge/monster-restoration.json), using the official CZ
web database and names from the pinned local client. It retains every original attribute row and stores
old/new mappings and source hashes in `TGAMEFORGEMONSTERRESTORE`. Each affected monster receives a separate
attribute row. EXP and unpublished combat columns are preserved, not certified as official. Ambiguous
ranges, name mismatches and missing source attributes are deferred. A different SQL baseline or a changed
reserved attribute ID causes an error rather than overwriting unknown data.

Before applying 026 to an unmodified baseline, `scripts/test-gameforge-monster-restore.ps1` applies it twice
inside a rollback transaction, checks original attribute hashes/EXP, and verifies the Moray example. The
test intentionally refuses an already-restored database. Restart Map after applying this migration.

The capture and audit tools are `Tools/GameforgeReference/reference.py` and `monsters.py`. Web responses are
cached under `.runtime/`; the tracked manifests preserve their URLs/hashes and the exact client reference.
The public web database's patch is not assumed to be the selected executable's patch.

### Verified Gameforge extensions (027–033)

These migrations use the **post-026 baseline**, the pinned original client and SHA-256-recorded official
web captures. They were applied to `TGame_GameforgeStage`, checked against complete chart rows, then
deployed atomically to the live database and registered by the importer. Each rerun retains the same values.

| Migration | Verified scope |
|---|---|
| 027 | 1,979 more ID-specific monster level/HP pairs; 494 level changes; original rows/EXP retained |
| 028 | 9,471 decoded shared client fields, including 6,883 item names; NPC candidates remain deferred |
| 029 | 545 missions with matching type, parent, objectives and public giver; 1,083 gold/XP fields and 143 level/main fields |
| 030 | 56 normal gold-shop item sets, with item existence and final-price checks |
| 031 | 2,051 explicit Valorian/Derion/Gor item-country mappings; ambiguous generic country labels retained |
| 032 | Neutral level-one creation, ignored legacy start option, no private welcome mail or account-mount replacement |
| 033 | Remove 163 higher-level starter-skill assignments and clear only their 111 starter hotkeys |

The individual before/after values and deferred records are in `Gameforge/normalization/`. The readable
[`identity-audit.html`](../../Gameforge/normalization/identity-audit.html) compares original IDs with the public
catalogues and includes direct lookups of same-name monster variants. Catalogue ranges must not be assigned
arbitrarily to an individual ID. The website's derived item power level is not simply its raw attribute grade.

`Tools/GameforgeReference/client_facts.py`, `catalogue.py`, `direct_variants.py` and `identity_audit.py` capture
and compare evidence. The `prepare_*.py` tools generate plans from saved baseline charts; `normalization.py`
generates guarded field updates. **Do not regenerate an applied migration from the modified live database.**
`verify_normalization.py` compares every column/row in the affected charts with the baseline plus only the
planned changes. `test_creation.py --database <explicit-db> --output <json>` tests all six classes/four legacy
options inside one rollback transaction, including preserved account pets and absence of custom mail and
higher-level skills. It leaves only identity-number gaps. Raw pages, saved chart snapshots, verified SQL
backups and numeric client-file backups are under `.runtime/gameforge-normalization/`.

Deploy the corresponding prepared numeric client patches with `prepare_client_sync.py` /
`deploy_client_sync.py`, preserving current translations and unrelated bytes, and restart Map to reload
templates. Original client formats differ; a whole-file copy is not a general conversion.

This is not a complete official server-database import. Unpublished monster EXP/combat columns, progression
curves, loot probabilities, spawn mechanics and unsupported quest/NPC dependencies remain unverified.

### Supported client-content reconstruction (034)

`034_gameforge_supported_content.sql` inserts 790 supported rows: eight mount deeds, seven noncombat
species registrations, 128 NPCs, 24 explicit NPC-to-existing-portal links, nine local map routes and 134
craft recipes with their ingredients, rewards and derived class conditions. It also widens NPC/monster
labels to Unicode, preserving defaults and the inspected name indexes. Existing rows are retained and
conflicting import keys abort the transaction. It requires the corresponding Map `QT_CRAFT` handler.

The [deployment report](../../Gameforge/server-import/report.html) distinguishes original client fields
and published giver/output data from local compatibility policy. In particular, the recipe class gates
are derived from the output-item class mask; they are not an official server-condition export. Ordinary
mission conditions, combat stats, drops and missing skill effects are not fabricated. The full original-ID
audit is broader than the previous client-import delta.

`prepare_server_content.py` generates from the saved **post-033** snapshot, pinned client and cached public
evidence. Do not regenerate an applied 034 from a modified live database. `verify_server_content.py`
compares every pre-existing row of the affected tables and allows only planned insertions. Explicit
`retained_hashes.py --exclude ...` checks protect all other tables. `Tools/GameforgeServerProbe` loads the
actual SQL charts and exercises packet handlers using ephemeral characters with no SQL gameplay writes.
`analyze_gameforge_sources.py` records publisher sources, native navigation NPC endpoints and public
quest-tree edges; its analysis does not automatically activate unresolved mission chains.

### Gameforge EXP through 140 and supported regional deliveries (035–036)

`035_gameforge_progression_140.sql` uses the 26 November 2018 EXP table published by the CZ Gameforge
team. It corrects 27 old cumulative thresholds and adds rows 126–140, with zero as the closed ceiling
at maximum level. SQL row L contains the threshold to reach L+1. Client fees are original; other new
columns retain level-125 compatibility values and are explicitly not certified Gameforge curves.
Deploy the accompanying Map EXP overflow/cap and soul-bar corrections together with the chart.

`036_gameforge_region_deliveries.sql` adds 12 closed NPC-delivery missions with completion records,
terms, rewards and reconstructed eligibility conditions, Lamian and Elina with 26 exact published
stock entries/prices, and two local terrain-unit routes. It does not fabricate monster spawns or drops.
68 existing regional NPC placements were verified. 126 other missions and nine portal NPCs remain deferred.

The [report](../../Gameforge/progression-140/report.html) links publisher evidence, frozen plans,
deployment and tests. `prepare_progression.py` / `prepare_high_level_regions.py` generate only from
the immutable post-034 snapshot. `verify_high_level.py` checks all 34,603 prior affected-chart rows plus
143 additions, with only the 27 planned EXP edits permitted. 194 other game tables and 69 global tables
(excluding migration metadata) are retained. `Tools/GameforgeHighLevelProbe` exercises actual SQL
templates and packet handlers without writing character progress. Applied migrations must not be
regenerated from the modified live database.

The [combined evidence report](../../Gameforge/combined-evidence/report.html) joins original client
records, verified public Gameforge captures and a later read-only server snapshot. Its
`reconstruction-plan.json` records missing dependencies and source conflicts; it is not an applied
migration. `join_gameforge_evidence.py` checks typed IDs, raw-source SHA-256, quest objectives/rewards,
NPC dependencies, monster attribute references and portal edges. Do not turn guidance points into
spawn counts, qualitative loot labels into percentages, term-21 spawn references into monster IDs,
or terrain heights into certified portal landing heights.

### Further verified fields and shops (037)

`037_gameforge_verified_regional_fields.sql` restores the exact published level/HP pairs for monster
IDs 3529 and 3588 using separate attribute clones, aligns 16 mapped original-client fields on five
other monster templates, and registers Elrod 26647/26648 with 22 source-verified gold-shop stock rows.
The imported level/HP values do not certify the retained damage, defence, mana, EXP or AI parameters.
It preserves old attribute rows and aborts on conflicting keys or field baselines. An outer transaction
covers all three parts. The live deployment also registered the migration atomically.

`Gameforge/server-completion/plan.json` is frozen against `.runtime/gameforge-verified-import/before/`;
do not regenerate it from modified live charts. Stage/live full-row comparisons, idempotence and a
deliberate conflicting-baseline rollback probe passed. SQL-loaded packet probes exercised all 22
new purchases plus existing delivery/EXP regressions using the deployed server DLL, with no SQL
gameplay writes. The Map service reloaded the charts. The
[remaining-content report](../../Gameforge/server-completion/report.html) lists all unresolved IDs and
dependencies; this migration does not invent quest conditions, monster populations or drop odds.

### Training Registration / Gripan (038)

`038_gripan_training_registration.sql` repairs six corrupted fields on Gripan (NPC 20001), whose invalid
country `69` blocked both quest markers and NPC conversations. Country, class selector, required item
and position come from the pinned original CZ client and match the current game client. The frozen
source and before-state are in `Gameforge/quest-training/gripan-repair.json`. The migration accepts only
that exact baseline or the already-repaired values. Quest 7981, its completion 7983, rewards and player
progress remain unchanged. Restart Map after applying it so the NPC registry reloads.

### Dialogue NPC maps (039)

`039_npc_dialogue_maps.sql` moves NPCs 22424–22433 from SQL map 11 to map 12, matching both the
pinned original CZ client and the current client. The mismatch prevented dialogue quests 4578–4587
from executing on the client's map. `Gameforge/npc-map-repair/plan.json` retains the source hashes
and frozen before-state. The migration checks NPC identities, exact position bits and quest links,
accepts the repaired map for reruns, and changes only `TNPCCHART.wMapID`. Restart Map to reload it.

SQL-backed packet probes use the deployed Map DLL and isolated prerequisite progress to exercise each
dialogue. The deployment report records both the original failure and the repaired responses, plus
wrong-map/prerequisite guards. The server also resolves the dialogue response's NPC ID from its NPC
trigger, so the client can match it to the selected NPC. Saved player quests and inventory are unchanged.

### Skill target-active prerequisite (040)

`040_gameforge_skill_target_active.sql` restores skill 201's `wTargetActiveID` from 0 to 200, matching
the pinned original CZ client's `TSkill.tcd`. The comparison checked previous-active, target-active and
durability-selector fields for all 765 shared skill IDs; this was the only difference. The source hash,
layout and frozen before-state are in `Gameforge/normalization/skill-active-wear.json`. The migration
requires effect 200 and accepts only the expected before-value or the already-correct value; unexpected
or missing rows abort. Five connection-local temporary-table scenarios passed, including reruns and
conflicting/missing-row rejection. It was applied with the rebuilt Map service on 2026-10-28. A read-only
probe using the deployed library confirms prerequisite 200 and all loaded fields on 765 skills,
27 magic options and 142 ammunition-consuming weapons. See the
[deployment record](../../Gameforge/deployments/2026-10-28-baseline.json); the actual skill chain still
requires verification in the client.

`025_login_email_two_factor.sql` is required by the Login email-OTP implementation. It creates durable
per-account challenge/rate-limit state and the administrator-only `TLoginTwoFactorConfigure` procedure.
It does not enroll users automatically or modify the baseline `TLogin` procedure. Configure SMTP and
independently verify the recipient before enabling an account; see
[`TLoginSvr.Net`](../../Servers/TLoginSvr.Net/README.md#email-two-factor-authentication).

Migrations run on every `docker compose up`. To apply new ones against an already-running stack:

```bash
docker compose run --rm mssql-importer
```

To re-apply a specific migration during development, delete its row first:

```sql
DELETE FROM TGlobal_gsp.dbo._SchemaMigrations WHERE Id = N'001_example.sql';
```

`057_gor_training.sql` activates the 14 captured initial Gor training missions and their
completion/pickup actions. The once-per-character cap, guaranteed missing quest items and fixed
60-second Moray population are explicitly approved substitutes. Existing legacy routing and player
state are preserved. The frozen plan, approval, SQL tests and deployment evidence are in
`Gameforge/gor-training.json` and `Gameforge/gor-training/`.

The working native client also needs the three routing alias definitions. Run
`python Tools/GameforgeReference/repair_training_client.py --apply` from the repository root,
then fully restart the game. Without these client records the sword objects ignore the
advertised quest IDs and cannot open their collection dialogs. The tool preserves existing
records, saves a backup and can be rerun without adding duplicates.

`058_gor_portal_destinations.sql` aligns the second guard post and Marka's hideout with
captured portal acknowledgements and removes the duplicate portal binding on NPC 26176.
It reuses the existing second-post spawn, adds one captured hideout spawn and preserves player
state. Run the isolated migration and full request/World/verdict checks with
`FOURDEV_TRAINING_SQL_TEST=1 python Tools/GameforgeReference/test_gor_training_sql.py --portals`.
Evidence and deployment: `Gameforge/gor-training/portal-destinations.json`.

`059_gor_moray_combat.sql` corrects the uniquely owned level-10 Moray attribute row:
physical power 72–75, magic power 74–77, physical attack level 23 and MP 485 match
the original server capture. The internal base/weapon split uses the existing level-10
base and is explicitly recorded as an inference. Other monster attributes, quest rules
and combat formulas are preserved. Run the captured-packet and isolated SQL/native hit
checks with `FOURDEV_MORAY_SQL_TEST=1 python -m unittest discover -s Tools/GameforgeReference -p test_moray_combat.py -v`.
Evidence and remaining unknowns: `Gameforge/gor-training/moray-combat.json`.

`060_gor_moray_defense.sql` applies the explicitly approved functional substitute for
Moray 801: physical and magic defense levels 124 -> 11. It depends on 059 and leaves
damage-reduction power, other attributes and player progress intact. The value 11 is
the inherited standard level-10 reference, not a verified Gameforge value. The reviewed
SQL body is the same as `Gameforge/gor-training/moray-defense-proposal.sql`.
Validate both repairs in an isolated SQL fixture with `FOURDEV_MORAY_SQL_TEST=1
FOURDEV_MORAY_DEFENSE_TEST=1 python -m unittest discover -s Tools/GameforgeReference -p test_moray_combat.py -v`.
Approval, checks and deployment: `Gameforge/gor-training/moray-defense-proposal.json`.

`061_gor_marka_spawn.sql` corrects only the three coordinates of existing Marka spawn
5024. Two captured full-HP appearances with that same spawn ID match the pinned
quest 11027 hunt marker; inherited SQL placed the boss about 90 units away and
10 units higher. Existing count, radius, probability, delay, attributes and saved
progress are retained. Run the isolated migration, rejection and SQL-loaded native
appearance/kill-credit/respawn checks with `FOURDEV_MARKA_SQL_TEST=1 python -m
unittest discover -s Tools/GameforgeReference -p test_marka_spawn.py -v`.
Evidence and remaining limits: `Gameforge/gor-training/marka-spawn.json`.
