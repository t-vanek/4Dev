# 4Dev

A self-hosted **4Story** reconstruction targeting the user's original **Gameforge CZ** installation,
with a modernized game client and servers rewritten in .NET.

The explicit content target is **Gameforge 5.0 and newer**, with the publisher's **5.0.1** notes as the
documented baseline. See the [target profile](Gameforge/target.json) and [version audit](Gameforge/version-target/report.html).
Later features retain their own version evidence; older 3.x/4.x notes are historical inputs. This is a
target, not a claim that the current client/server already implement the whole 5.x feature set. The working wire version is separate from the content
release, and the selected original executable's exact patch remains unknown.

Companion level 20 is enforced. Migrations **035–036** restore the publisher team's cumulative EXP curve
through level 140 (27 corrected thresholds and 15 new chart rows), prevent EXP overflow and a maximum-level
soul-bar underflow, send loaded EXP bounds on login, and activate 12 delivery quests, two shops with 26 stock entries and two world-unit
routes for Anbari/Undred. All **1,177 map-server tests** pass in Linux .NET 10. The actual deployed libraries
also pass SQL-backed packet probes for all 15 level boundaries, all 12 deliveries and all 26 purchases.
See the [progression/region report](Gameforge/progression-140/report.html) and
[deployment evidence](Gameforge/progression-140/deployment.json). The regions still lack monster populations,
126 other missions and nine portal NPCs. New non-EXP/non-client-fee level fields retain level-125 compatibility
values; skill-point, HP/MP and PvP curves are not certified Gameforge data. Saved characters are retained.

The [current content gap inventory](Gameforge/content-gaps/report.html) lists every missing regional
mission, NPC/portal and quest-item source, plus all published monster drop memberships and a global
selected-client ID appendix. It uses a fresh live SQL snapshot and 77 refreshed primary Gameforge pages;
missing templates, absent gameplay links and unknown numeric probabilities are reported separately.

The [direct client evidence report](Gameforge/client-reconstruction/report.html) extracts 126 deferred
missions, 156 direct helper records, eight quest-item/monster navigation links, nine portal definitions
and 48 directional records. A strict reader consumes both original regional TMU files completely and
recovers terrain heights using the indexed map dimensions. Terrain heights exclude model/platform
collision and must not be treated as certified server landing or spawn heights. No complete population,
respawn timer, numeric loot-weight or quest-condition export was recovered; original files are unchanged.

The selected reference is the local installation in `C:\Program Files (x86)\4Story`, pinned by the file
hashes in [`Gameforge/reference.json`](Gameforge/reference.json). Its exact patch number is unknown;
the executable's timestamp is not a release identifier. Verify it before importing data:

```powershell
python Tools/GameforgeReference/reference.py verify --source 'C:\Program Files (x86)\4Story'
```

The inherited RageZone/Araz PvP client and SQL backups are implementation material, **not an official
gameplay baseline**. New characters now use the neutral level-one creation path, without the 19/29/93
selection. Existing test characters keep their saved levels and equipment.

Published gameplay data comes from the [official Gameforge CZ database](https://cz.4story.gameforge.com/monster),
captured in [`Gameforge/monsters-web.json`](Gameforge/monsters-web.json). It contains 1,451 published monster
records; 1,450 IDs exist in the inherited SQL charts. Of 279 levels outside the published ranges, migration
`026_gameforge_monster_levels_hp.sql` restores 213 exact level/HP pairs whose IDs and names match the selected
client. [`Gameforge/monster-restoration.json`](Gameforge/monster-restoration.json) records each source and
the 66 deferred records. For example, Moray (243) is restored from level 80 / 20,000 HP to level 11 / 311 HP.

Each correction uses a separate attribute row; the original rows and their mappings are preserved in SQL.
The [ID audit](Gameforge/normalization/identity-audit.html) reads the original client directly and joins it
to the complete captured public catalogues. All 3,520 listed item, 1,451 monster and 1,820 quest IDs exist
in that client; 1,297 of 1,299 NPC IDs exist. Item/quest list levels agree throughout. The audit retains 100
name differences and the two missing NPC IDs. Direct ID lookups additionally confirm 992 monster variants
omitted from the main catalogue; a catalogue's grouped level range is not an individual monster level.

Migrations **027–033** extend the verified restoration:

- **1,979 additional monsters:** ID-specific level/HP, including 494 level changes. Together with 026,
  2,192 monsters have tracked corrections. Every original attribute row and monster EXP value is retained.
- **9,471 client fields:** shared item properties/names, three mount mappings and monetary chart fields.
  All 9,002 decoded item combat-attribute rows already match the selected client. Direct published item
  stats agree for all 3,443 name-matched items; 2,051 explicit country restrictions are restored separately.
- **545 matching missions:** 1,083 gold/XP values plus 143 level/main fields, gated on matching type, parent,
  objectives and the published NPC giver. **56 normal gold shops** receive exact published stock, with
  item existence and final SQL price checks.
- **New-character profiles:** SQL also ignores boosted-start options, removes branded 4StoryPW mail and
  automatic account-mount replacement, and excludes 163 starter-skill assignments above level one.
  Only their 111 starter hotkey entries are cleared; existing character skills/hotkeys are retained.

The runtime `TItem`, `TMount` and quest files receive only corresponding numeric patches; current text and
unrelated bytes are preserved. Deployment was checked against the isolated database, rerun idempotently,
then verified on the live database across 65,947 chart rows and 24 rollback character-creation probes.
Backups, raw web captures and deployment checks are under `.runtime/gameforge-normalization/`.

The [client content import](Gameforge/client-import/report.html) adds missing original IDs to `Game/`:
**39 items, 149 monsters, 2,553 quest records, 48 skills and 21 NPC IDs** (36 placement records), plus
maps, regions, portals, strings and reward previews. The deployed import includes 5,102 chart records,
439 indexed resources, 62 absent asset/navigation files, eight absent image pages and two isolated image
page aliases. Existing records and translations are retained. The rebuilt client reads both six- and
nine-slot quest reward previews and the additional original string IDs.

Validation reads 56,546 chart records, checks 11,424 resource dependency nodes without unresolved links,
and verifies all 117 deployed file hashes and their backups. Repeating the import produces no changes.
Deployment metadata, added IDs and source hashes are in the [manifest](Gameforge/client-import/manifest.json).
Client launch/playthrough remains unverified: Windows refused automated launch because its existing
manifest requires administrator rights. That client-only import initially left 39 item, 134 monster,
2,484 quest, 12 NPC and 48 skill IDs without server rows. The broader server audit below includes
additional gaps that predated that client import.

The [server reconstruction](Gameforge/server-import/report.html), migration **034**, adds **134 crafting
recipes, 128 NPCs, 24 NPC-to-portal associations, eight mount deeds and seven noncombat companion
registrations**, plus nine local map routes. Crafting now consumes ingredients, grants the output and
reports success/failure through the real quest packets. NPC country checks also apply to direct quest
requests. Unicode labels retain the previous values, defaults and name indexes.

These additions distinguish exact source fields from reconstruction policy: recipe class restrictions
derive from the original output item's class mask, and unrepresented mount fields require agreement
between equivalent existing templates. They are not certified official server exports. All 1,152 map
server tests pass. A probe using the deployed Linux assemblies and live SQL charts performs 268 successful
crafts, rejects 134 attempts without ingredients, uses all eight new mount deeds and resolves the new
NPCs/portal associations, with ephemeral characters and no gameplay writes to SQL. All 45,132 old rows
of affected charts and 263 other game/global tables were retained. SQL and old runtime backups are under
`.runtime/gameforge-server-import/`.

The [implementation research](Gameforge/server-import/research.html) cross-checks publisher patch notes,
public quest trees and the native `TQNode.qpd` reader. It recovers both NPC endpoints for **323 missing
quest records** and **690 public quest-tree edges**. These are implementation inputs, not proof of complete
eligibility/reset rules. The full original-ID audit still defers **37 items, 133 monsters, 37 NPCs, 2,530
quest records and 48 skills** with specific reasons in the [plan](Gameforge/server-import/plan.json).

**Complete Gameforge parity is still unverified.** Monster damage/defence/mana/EXP, progression curves,
spawn mechanics, drop probabilities/rates, unsupported quest graphs and remaining NPC shops/placement lack
a verified complete server reference. For example Moray's retained EXP is still zero. Public item power
level/default-price derivation also needs a validated mapping; raw attribute grade is not always its
displayed power level. Ambiguous or conflicting records are recorded in the normalization plans rather
than filled with invented values. Client quest, skill and companion formats differ. The import converts
the supported layouts explicitly; unsupported companion and other feature tables remain deferred.

👉 **New here? Follow the [Installation guide](INSTALLATION.md).**

## What's inside

| Folder | What it is |
|---|---|
| `Client/` | Game client source (C++, rendered with bgfx, windowed with SDL3) |
| `Game/` | The runnable game: client exe, assets and `config.ini` |
| `Servers/` | Game servers. The `*.Net` folders are the ones in use (Login, World, Map, Patch, Control) and the original C++ servers are kept for reference |
| `Database/` | Database setup and migrations, applied automatically by Docker; inherited gameplay data is provisional |
| `Lib/` | Engine, UI and network libraries, plus bundled third-party code |
| `Tools/` | Launcher, UI editor, asset import tools and the [Happy Rust operator console](Tools/Happy.Rust/README.md) |

## Quick start

```powershell
git lfs pull                       # fetch the big game files
.\scripts\configure-local-server.ps1 -BackupDirectory 'C:\path\to\Database'
docker compose up -d --build       # start the database + all servers
.\Client\build.ps1 -Deploy         # build the game client
cd Game; .\TClient.exe 127.0.0.1 4816
```

Log in with **`test`** / **`test123`**. The configuration script prepares credentials and TLS identities;
for an existing database volume, preserve its original password in `.env`. See [INSTALLATION.md](INSTALLATION.md).

---

Thanks Zemi, thanks Araz <3
