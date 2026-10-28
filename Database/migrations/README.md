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
