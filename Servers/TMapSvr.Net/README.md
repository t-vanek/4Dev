# TMapSvr.Net

## Current implementation

The full port ledger is in [`PORT_STATUS.md`](PORT_STATUS.md); the phase descriptions below record the earlier work.
Cash shop and GM tools are implemented and covered by their existing tests. The follow-up includes:

- Account security codes with durable salted hashes, session-local unlocks, ten-attempt/24-hour account locks,
  and protected inventory, trade, mail, auction, guild and cash operations.
- Custom-cloak lists and changes, atomic ownership/payment/item updates, guild-cloak registration, and atomic medal-package redemption.
- Meeting-room rental, NPC availability, owner invitations, single-use acceptance, expiry and owner-leave cleanup.
  Rentals are transient; saved characters return outside after a logout/restart.
- Pooled outbound copies, bounded queues, immediate batching of up to 128 packets / 64 KiB, and allocation-free
  string encoding into the existing packet buffer. Client packet framing, encryption and sequence numbers are unchanged.

Migration `023_security_codes_and_cloaks.sql` supplies security columns and cloak/medal lookup indexes. Docker's migration
job applies it before the servers start. `Map:Db:GlobalConnectionString` supplies the security-code database; if absent,
the game connection's server/credentials are used with catalog `TGlobal_gsp`.
Configured security-database failures refuse entry even when game-data loading falls back to DB-free operation.

Tests include fake-channel gameplay, real encrypted loopback sockets, and isolated SQL transactions (set
`T4DEV_TEST_ADMIN` to enable those). Reproducible performance checks are in
[`Tools/ServerBenchmarks`](../../Tools/ServerBenchmarks/README.md).

## Original phase history

A C#/.NET 10 port of the C++/ATL **`TMapSvr`** (the per-map / zone game server), byte-compatible with
this repo's client and SQL baselines. It follows the same architecture as the sibling `TLoginSvr.Net`
and `TWorldSvr.Net` ports.

`TMapSvr` is the biggest server in the cluster (~54k lines, 305 client handlers, 325 server handlers, 250
DB queries), so — like `TWorldSvr.Net` before it — it is being ported in phases:
- **Phase 1** — the vertical slice that runs: a client connects, completes the full map↔world handshake,
  spawns, and moves / jumps / chats with other players in view.
- **Phase 2** — character data: the player's **inventory, equipped gear, skills and hotkeys** are loaded
  from the DB and serialized into `CS_CHARINFO_ACK` / `CS_ENTER_ACK` (byte-exact `CTItem::WrapPacketClient`).
- **Phase 3** — the **`CTCell` spatial grid**: visibility is the real 64-unit 3×3 cell block with proper
  enter/leave-on-move, per-`(channel, map)` grids — replacing the Phase-1 whole-map broadcast.
- **Phase 4** — the **item & magic template charts** (`TITEMCHART`/`TITEMMAGICCHART`): loaded once at
  startup and linked to each item, driving the template `RefineMax` and the computed `GetMagicValue` on the
  wire (closing the two Phase-2 item simplifications).
- **Phase 5** — the **`CTObjBase` stat/vitals core**: the six primary stats (fully derived from the
  class/race/formula charts) and the computed `GetMaxHP`/`GetMaxMP`, so the MaxHP/MaxMP in
  `CHARINFO`/`ENTER` are real (was synthesized 100).
- **Phase 6** — the **rest of the stat sheet**: AP/DP (from the `TITEMATTR`/`TITEMGRADE` charts + equipped
  gear), attack-speed/crit/charge, and the 31-field `CS_CHARSTATINFO` packet — every field value-exact.
- **Phase 7** — **item manipulation** (`CS_MOVEITEM`): move/swap/split/merge/drop within the inventory, and
  equip/unequip with `CanEquip` validation → live stat recompute + the `CS_EQUIP_ACK` appearance broadcast.
- **Phase 8** — **item use** (`CS_ITEMUSE`): HP/MP potions heal (`wUseValue` / full-restore), clamp to max,
  consume, and broadcast the bar via `CS_HPMP_ACK`; `IU_FULL`/`IU_NEEDLEVEL`/`IU_WRAPPING` guards.
- **Phase 9** — **two-handed auto-eviction** (completing `CS_MOVEITEM` equip): equipping a 2H weapon evicts
  the off-hand occupant into the bags via the C++ `CanPush`/`PushTItem` allocator (`MI_INVENFULL` when full,
  never auto-dropped), and unequip-into-occupied is normalized to the equip/swap path — byte-exact.
- **Phase 10** — the **money currency core** (gold/silver/copper tiers combined on base 1000; `UseMoney`/
  `EarnMoney`; loaded from `TCHARTABLE`; `CS_MONEY_ACK`) and **durability repair** (`CS_DURATIONREP`:
  RPT_NORMAL/EQUIP/ALL, the cost quote, two-phase money check/commit, restore-to-full, portable-smith item).
- **Phase 11** — **field monsters** on the grid: a `Monster` occupies a 64-unit cell and becomes visible to
  players in its 3×3 view via `CS_ADDMON_ACK`/`CS_DELMON_ACK` (spawn-announce, player-enter, player-move
  enter/leave diff, despawn). Spawn mechanism + visibility only — the DB spawn pipeline, respawn/RNG, AI and
  combat are deferred.
- **Phase 12** — the **monster spawn pipeline**: the four spawn charts (`TMONSTERCHART`/`TMONATTRCHART`/
  `TMONSPAWNCHART`/`TMAPMONCHART`) load at startup, `SE_DEFAULT` spawns build their slots at bring-up, and the
  1-second regen tick (`CTAICmdRegen`: prob roll → weighted type pick → radius-scatter position → HP/MP full)
  populates the world through the Phase-11 visibility layer.
- **Phase 13** — the **combat spine**: a player's physical melee hit (`CS_DEFEND`) → `CalcDamage` (the
  value-exact `AP−DP` roll) → the monster loses HP (`CS_DEFEND_ACK` + `CS_HPMP_ACK`) → on 0 HP it dies
  (`CS_DIE_ACK`/`CS_DELMON_ACK`) and its spawn slot re-arms, closing the spawn↔death loop. Skills/magic/crit,
  `CS_SKILLUSE`, loot/exp, regen, PvP and monster AI are deferred.
- **Phase 14** — the **attack-announce half** (`CS_SKILLUSE`, the packet the client sends *first*): the
  **skill chart** (`TSKILLCHART` → `SkillTemplate`) loads at startup and links to each learned skill; a cast
  validates (skill-known → MP `<` → HP `<=` → reuse cooldown), deducts the **caster's own** MP/HP
  (`GetRequiredMP/HP`, value-exact), arms the reuse cooldown (`GetReuseDelay`/`Use`), and broadcasts
  `CS_SKILLUSE_ACK` (the attack-power payload the follow-up `CS_DEFEND` reads back) + `CS_HPMP_ACK` (the
  cost) to the near players — closing the two-packet attack. OT_PC casters only; the physical-melee power
  path; transform-cost, weapon-durability decrement, aggro, and the tournament/arena/premium/peace-zone/buff
  guards are deferred.
- **Phase 15** — **skill-data damage scaling** (`TSKILLDATA`/`m_vData`): the skill-effect chart loads and drives
  `GetValue`/`Calculate`/`CalcValue` (the `SVI_*` operators) so an attacking skill's `MTYPE_DAMAGE` row scales
  the `CS_DEFEND` AP−DP roll (a basic attack stays the raw roll), plus `GetAttackType`/`IsLongAttack` — which
  now select the melee/long AP set + physical/magic attack-level in both `CS_DEFEND` and `CS_SKILLUSE` (folding
  in Phase 14's physical-melee deferral). The maintained-buff layer lands in Phase 31; the cure/remain layers
  and `DistributeSkill` stay deferred (magic-branch damage + crit/miss land in Phase 28).
- **Phase 16** — **HP/MP regen** (`Recover`): players regen HP (NORMAL-only) + MP (always) by the flat
  `GetHPR`/`GetMPR` amount every 3s; monsters regen 25%/tick (non-battle); attacking / being hit enters battle
  mode + resets the recover anchor (suppressing HP regen); a change broadcasts `CS_HPMP_ACK` to the 3×3 view.
- **Phase 17** — **loot/exp on death**: the kill's owner (≥10% damage) gains attacker-level-scaled exp
  (`GetExp`/`GetLevelRate`, value-exact), which can level them up (full heal + skill points + `CS_LEVEL_ACK`/
  `CS_EXP_ACK`); money drops onto a lootable corpse taken via `CS_MONMONEYTAKE`. Anti-farm: 0 exp at ≥10 levels
  above, no drops at ≥25. The item drop table + party split + quests are deferred.
- **Phase 18** — **monster idle roam** (`CTAICmdRoam`): on a per-monster roam timer, an idle monster a player
  can see picks a destination on its spawn-radius circle and broadcasts `CS_MONACTION_ACK` (WALK + dest); a
  monster no one sees stays dormant. The client-authoritative position echo (`CS_MONMOVE`) is deferred.
- **Phase 19** — **monster aggro + chase** (`CTAICmdChgMode`→BATTLE + `CTAICmdFollow`): being hit targets the
  attacker and enters battle; the monster then chases (`CS_MONACTION_ACK` `TA_FOLLOW` toward the target) and
  drops aggro (→ NORMAL, HP regen resumes) when the target is gone or flees past the leash. (The target choice
  is upgraded to the real highest-cumulative-aggro table in **Phase 44**.)
- **Phase 20** — **monster-attacks-player** (`CTAICmdAttack`): a battle monster whose target is in melee range
  hits it on the attack cadence — `CalcDamage` with the monster's AP band (`wAP + wMin/MaxWAP`) vs the player's
  DP → the player loses HP + enters battle → `CS_MONATTACK_ACK` + `CS_DEFEND_ACK` + `CS_HPMP_ACK`; at 0 HP the
  player dies (`CS_DIE_ACK`) and the monster disengages. The monster damage is applied server-side (the C++
  routes it through the host client's `CS_DEFEND_REQ`).
- **Phase 21** — **player revival** (`CS_REVIVAL`): a dead player revives at a chosen point — reposition (grid
  re-bucket + `CS_ENTER/LEAVE` diff), restore HP/MP by type (`NPC` 30% / `GHOST` 40%, HP ≥ 1), back to
  `MT_NORMAL` (HP regen resumes) → `CS_REVIVAL_ACK` + `CS_HPMP_ACK`. Closes the death loop Phase 20 opened.
  The death-penalty aftermath, companion respawn, revival-protection buff, and the priest-resurrection
  (`CS_REVIVALASK`) flow are deferred.
- **Phase 22** — **item drop-loot** (`TMONITEMCHART`): a killed monster with a drop table rolls chart-type
  items (per-attempt `bItemProb` × `bDropCount`, weighted pick + the four "normal" gates) onto its corpse
  inventory alongside the money; `CS_MONITEMLIST` now serializes the items (`WrapPacketClient`) and
  `CS_MONITEMTAKE` moves one into the taker's bags (`CanPush`/`PushTItem` + `CS_GETITEM_ACK`). No drop table
  ⇒ no loot at all (money included, matching the C++ `AddItem` gate). Magic/rare option rolls, ranged
  (`MonChoiceItem`) picks, and party loot modes are deferred.
- **Phase 23** — **NPC shops** (`CS_NPCTALK`/`CS_ITEMBUY`/`CS_ITEMSELL`): NPCs load from `TNPCCHART` +
  `TNPCITEMCHART` into a static server-side registry (the client already knows their positions, so nothing is
  broadcast); talk validates country gating (`CTNpc::CanTalk`) and replies `CS_NPCTALK_ACK`; buy resolves the
  item from the NPC's stock, charges gold (`GetItemPrice` = level-money·`fPrice`), and pushes it into the bags
  (`CanPush`/`PushTItem`); sell earns ¼ of the item's price (`GetPrice`/4) and removes/decrements it. The quest
  engine (`CheckQuest` ⇒ talk returns questId 0; a nonzero quest-id buy is free, faithful to the no-quest-chart
  C++ path), NPC discount, PvP-point/BoW pricing, and the trade/store/secure-code guards are deferred.
- **Phase 24** — the **quest engine** (`CQuest` hierarchy + `CheckQuest` + `m_mapQUEST`): the classic loop —
  a quest is **offered** (NPC-talk objective echo / the possible-quest list `CS_QUESTLIST_POSSIBLE`),
  **accepted / run** (`CS_QUESTEXEC` → the type's `ExecQuest` → `CS_QUESTADD_ACK`, fetch items handed over),
  its **objectives advance** as the universal `CheckQuest` hook fires on get-item (buy) / kill / talk
  (`CS_QUESTUPDATE_ACK`), and it is **turned in** (a `QT_COMPLETE` quest → `CheckComplete` → reward grant
  (item/gold/exp) → `CS_QUESTCOMPLETE_ACK`), recursing into child quests. Eligibility (`CanRunQuest` —
  level / parent / prereq-quest / class / item / position conditions, count-max) and drop (`CS_QUESTDROP`)
  are covered. The four foundational subtypes (NpcTalk / Mission+Guild / Complete / GiveItem) + DefTalk are
  implemented; the other 15 subtypes, the exotic term/condition types, skill/magic-item rewards, quest
  persistence (`DM_*`), and the DB quest-template load (schema unverified ⇒ quests are test-injected) are
  deferred.
- **Phase 25** — **persistence** (the `DM_*` save path): the character record + quest progress are now written
  back to SQL Server. `TSaveChar` (one `UPDATE TCHARTABLE` by `dwCharID` — money/exp/level/vitals/position/
  skill-points, the full 28-column value set round-tripped so nothing is zeroed) + `TSaveQuest`/`TSaveQuestTerm`
  (per-quest upsert driven by the `QuestProgress.Save` dirty flag). Triggers mirror the C++: a **30-min**
  periodic save (`CHAR_SAVE_TICK`), **on disconnect/logout**, and a **shutdown flush**. The snapshot is built on
  the batch thread; the SQL runs off-thread (fire-and-forget, per-char throttle) so the map never blocks. Only
  real DB-loaded main chars are saved; everything no-ops DB-free. Inventory/item persistence lands in Phase 26.
- **Phase 26** — **inventory persistence**: items + containers are now saved alongside the char record,
  **resolving the money↔inventory relog desync**. The char-inventory rewrite (`TSaveItemDataStart` →
  per-container `TSaveInven` → per-item `TSaveItem` (the 35-value set) → `TSaveItemDataEnd`) stages the char's
  storage then atomically promotes staging → live `TINVENTABLE`/`TITEMTABLE` on each save. The per-item `dlID` PK
  is loaded + preserved for existing items and minted from the per-server id counter (`TInitGenItemID` seed →
  `GenItemId`) for in-session items. Gated on the seed being ready, so an unseeded counter never rewrites with
  unsafe ids (the DB keeps its last item state instead). **Live-DB round-trip verified** against the running
  SQL Server — which corrected the bracket (the `TSaveCharData*` bracket's item promotion is commented out in
  this baseline) and a no-expiry sentinel bug (`0 → 1900-01-01`, not `NULL` — the `dEndTime` columns are
  `NOT NULL`). **Deferred**: skill/maintain/hotkey/companion/pet saves (cabinet lands in Phase 37).
- **Phase 27** — **incremental item persistence** (the fast-path): the Phase-26 full rewrite only runs on the
  30-min timer / disconnect / shutdown, so a hard crash could lose up to 30 min of item changes. Now every
  ported item mutation (move/split/merge/drop, equip, use, buy, sell, loot, quest, repair) — all of which funnel
  through the ADD/UPDATE/DEL item senders — is queued and flushed **per tick** via `TSaveItemDirect` (a single-row
  upsert straight to live `TITEMTABLE`) + a delete-by-`dlID` for removals. Queued by `dlID` (repeated changes
  collapse; a same-tick pick-up-then-drop nets to a delete), gated on the same item-id seed as the full save, and
  written off-thread. The periodic full save stays the authoritative reconciler. **Live-DB round-trip verified**
  (insert / idempotent upsert / delete-by-`dlID`). *Deviation:* the C++ uses `TSaveItemDirect` only for
  server-side grants; this port additionally uses it as a crash-safety layer for client changes. **Deferred**:
  skill/hotkey/companion/pet saves (these persist state no ported handler mutates yet; cabinet items land in Phase 37).
- **Phase 28** — **combat quality**: hits are resolved through `GetAtkHitType` — a level-scaled accuracy roll
  (`FTYPE_PAR`/`MAR` vs the monster's defend-level and the attacker's attack level, ≥ 20% floor) then a crit roll
  on the attacker's crit rate — so a swing can miss, land, or crit (both PC→monster and monster→PC). A magic skill
  takes the **magic branch** (magic AP vs the monster's `wMDP`); crits use the `FTYPE_PCD`/`MCD` formula
  (+20–40% of the base band). A miss deals 0 with an empty damage map but still aggros. Monster combat stats
  (`wMDP`/`wDL`/`wMDL`/`bCriticalPP`/`wAL`) now load from `TMONATTRCHART`. **Deferred**: the hit-test/premium/
  guild/boss-special skill early-outs, and PvP (the shield-block roll is done in Phase 41).
- **Phase 29** — **rest of the quest engine**: the **DB quest-template load** (the 4 charts `TQUESTCHART` +
  `TQCONDITIONCHART` + `TQREWARDCHART` + `TQUESTTERMCHART` assembled by quest id) — so quests exist on a **live**
  server (Phase 24 ran on test-injected templates only). Live-DB verified: 6261 templates / 5712 conditions /
  4322 rewards / 10687 terms. Plus 5 more subtypes: **DeleteItem**, **DropQuest**, **ChapterMsg**, **Routing**,
  same-map **Teleport**. **Deferred** (each blocked on an unported subsystem):
  **Craft** (self-completion), **SendPost** (mail);
  cross-map teleport. (**DefendSkill** is done in Phase 31, **Switch** in Phase 32,
  **SpawnMon**/**DieMon**/**DropItem** in Phase 33, **Regen** in Phase 34, and **GiveSkill** in Phase 36.)
- **Phase 30** — **quest persistence load-on-enter + the quest log**: Phase 25 saved quest progress but nothing
  reloaded it, so accepted quests vanished on relog. Now the enter char-data load rebuilds `m_mapQUEST` from
  `TQUESTTABLE`/`TQUESTTERMTABLE` (trigger/complete counts, running-term counters, the remaining timer), and the
  `MW_CHARINFO` step sends `CS_QUESTLIST_ACK` (the in-progress quests + per-term need/current/status) so the
  client shows its quest log on login. Live-DB verified.
- **Phase 31** — **maintained-skill (buff/debuff) engine**: `CalcAbilityValue` now layers active buffs onto
  every stat getter (`base → items → buffs`, keyed on `MTYPE_*`), so a `+STR`/`+MaxHP`/`+AP` buff really moves
  the sheet (no per-tick HoT — HP buffs surface through regen). Buffs apply via a buff-type `CS_DEFEND`
  (self/ally, or a debuff on the target monster alongside the damage) and the `ForceMaintain` direct grant;
  they stack-resolve (`UpdateBuffSkill`: priority → value → self-cast), expire on the per-tick
  `CheckMaintainSkill` sweep (broadcasting the new `CS_SKILLEND_ACK`), and drop on death. DB-loaded buffs are
  template-linked + duration-reconstructed on enter. Unblocks the **quest DefendSkill** subtype. The
  cure/effection/remain layers, loop skills, and DB save-back are deferred.
- **Phase 32** — **map switch/gate subsystem**: chart-driven per-(channel, map) switches (`TSWITCHCHART`) +
  switch-driven gates (`TGATECHART`), placed in the grid so they add/del on enter/move like monsters. A player
  activates a switch (`CS_SWITCHCHANGE_REQ`) subject to its lock flags + duration cooldown; it toggles, fires the
  `TT_RUNSWITCH` quest trigger, broadcasts `CS_SWITCHCHANGE_ACK`, and drives its linked gates (a `GT_MULTISWITCH`
  gate opens only when all its switches match) — each firing `TT_RUNGATE` + `CS_GATECHANGE_ACK`. The `dwDuration`
  auto-revert flips the switch back on the tick, and `QCT_SWITCH` conditions gate quests on switch state. Unblocks
  the **quest Switch** subtype. A gate is a synced visual door (no server-side movement block); state is per-map
  runtime (not persisted). The `GT_SELECTSWITCH` clone pick + battle-zone gatekeeper gating are deferred.
- **Phase 33** — **quest-driven monster spawn**: a time-limited-spawn engine (`AddTimelimitedMon`/`DelMonSpawn`)
  on top of the Phase-12 spawn model, unblocking three quest subtypes. **SpawnMon** adds (`QTT_SPAWNID`) /
  removes (`QTT_SPAWNID_DEL`) a chart spawn at runtime; **DieMon** force-kills every live monster of a spawn —
  silently for an `SE_QUESTDEL` spawn, else a credited kill (exp/loot to the quester via the normal death path);
  **DropItem** attaches an owner-locked item to a killed monster's corpse (honored by the item-list/take owner
  filters). `RT_TIMELIMIT ≡ RT_ETERNAL` (the C++ auto-expire is commented out), so a quest spawn persists +
  regens until despawned.
- **Phase 34** — **quest Regen** (dynamic monster replacement): killing a monster mints a one-shot `SE_DYNAMIC`
  spawn of another kind at the death spot (`RegenDynamicMonster` from a recycled reserved id →
  `AddTimelimitedMon`), linked to the killed monster's slot so its respawn force-removes the temp
  (`m_wRegenDelSpawn`); the dynamic spawn never re-arms. Completes the monster-spawn subtype family.
- **Phase 35** — **cure/dispel** (completes the buff engine): a cure skill (`SDT_CURE` rows) cast on self/an
  ally via `CS_DEFEND` strips positive buffs (`SCT_POSREMOVE`) or debuffs (`SCT_NEGREMOVE`, strict
  `SPT_NEGATIVE`) and instant-heals HP/MP (`SCT_HP`/`SCT_MP` with a 0-15% over-heal roll, clamped +
  `CS_HPMP_ACK`). The stat-layer `CalcCure` term, `SCT_CANCEL`/`SCT_DIE`, and the recall/aftermath/revival
  execs are deferred.
- **Phase 36** — **skill-learn** path + quest **GiveSkill**: `UpdateSkill` (add-only skill grant + the byte-exact
  `CS_SKILLBUY_ACK`) drives `CQuestGiveSkill` — learn the `QTT_SKILLID` term's skill (level = term count,
  floored at 1), gated by the skill's class mask (`m_dwClassID & BITSHIFTID(m_bClass)`); recurses children only
  when the skill was newly granted (an already-known skill is a no-op, no level-up). The NPC-purchase handler
  `OnCS_SKILLBUY_REQ` is deferred (blocked on the skill-point currency, price/level tables, NPC teach-lists, and
  parent-skill prerequisites — all unmodelled).
- **Phase 37** — **player cabinet** (item warehouse): per-character in-memory storage (C++ `m_mapCabinet` — no
  world round-trip), up to 3 cabinets × 16 items. Open (per-id gold cost, `bUse` flag), list, item-list, put-in
  (`ITEMTRADE_CABINET`-gated, merge-then-one-new-slot, `dwStItemID` = max+1), take-out (whole-stack only, per-id
  fee charged only on success). Loaded on enter (`TCABINETTABLE` headers + `STORAGE_CABINET` item rows) and
  persisted incrementally as cabinet-stamped item rows. The remote NPC-call-scroll access path is deferred.
- **Phase 38** — **party gameplay** (shared exp + party loot): membership is world-authoritative (the map caches
  `PartyId`/`PartyType`/`ChiefId`, pushed via MW), so the map does the *gameplay* keyed off those. A party-member
  kill makes the party the monster's keeper (`OWNER_PARTY`); its exp splits among near members (size-bonus ×
  level-weighted share × level-gap scale), and its corpse is lootable by any member (PT_FREE) with a
  `CS_PARTYITEMTAKE` broadcast. PT_SOLO opts out. Management (invite/join/leave — MW relays to the world's
  `CTParty`), the exotic loot modes (HUNTER/LOTTERY/CHIEF/ORDER), and the party money-split are deferred.
- **Phase 39** — player-to-player **deal (trade)**: a same-map, in-memory two-party state machine (ASK →
  accept → one-shot offer → two-phase confirm → atomic swap). Offered items are staged as copies and only leave
  the bag at execution; a full guard (both offers re-validated + both bags fit via `CanPush`) precedes any
  mutation, so a full bag / moved item aborts cleanly (BUSY/CANTRECV) — no partial trade. Tradability via
  `Item.CanDeal()` (`ITEMTRADE_DEAL` / wrapped). The deal-lock guards on other handlers, the block-list/nation
  gates, and the cross-map teardown are deferred (execution-time `ValidDeal` guarantees integrity).
- **Phase 40** — personal **store** (player vendor): a same-map, in-memory listing — the seller opens over their
  own bag items at prices (items stay in the bag, only referenced), nearby players browse + buy (guard money +
  bag space, then money buyer→seller + a copy to the buyer, decrement the offer + seller stack, auto-close when
  sold out). Open/close broadcast to the 3×3 neighbors; the store flag rides `CS_ENTER_ACK` for late-joiners.
  The credits (PvP-point) price path, the faction/zone gate, and the `TSTORE_SKILL` buff are deferred.
- **Phase 41** — the **shield-block roll** (`GetShieldDP`/`GetShieldMDP`): the defender-side block roll inside
  `CalcDamage`. An equipped shield rolls `ABILITY_SDR` (base `bBlockProb` + `MTYPE_SDR` enchants, folded through
  the buff layer); on `rate > rand()%100` it adds its `ABILITY_SDP` (base `m_wDP` + `MTYPE_SPDPOW`) to defence —
  additive reduction, never a fixed %/full negation (the 5/7 floor holds) — and flags the reported hit
  `HT_BLOCK`. Ported as a pure `StatEngine` function and wired live into the monster→player PC-defender path (a
  kill still reports `HT_LASTHIT`). The block-triggered shield-durability decrement + `TBLOCK_SKILL` reaction,
  the disguise-buff item gates, and PvP are deferred.
- **Phase 42** — the **`CalcDamage` completion**: the exec-aware per-data-row damage dispatch. A skill's
  `SDT_ABILITY` rows resolve by attr (physical/magic → AP band + `GetDefendPower`/`GetMagicDefPower`) then exec —
  `MTYPE_DAMAGE` (30, HP), `MTYPE_MDAMAGE` (88, **MP damage**), `MTYPE_HP`/`MTYPE_MP` (direct **heal/drain**) —
  accumulating the net HP/MP change and a **per-exec damage map** that fills `CS_DEFEND_ACK` (multi-entry; the
  C++ find-by-attr/insert-by-exec quirk reproduced; the map value is the full roll and `OnDamage` floors/clamps
  HP/MP, so a kill reports full damage). Adds **lifedrain** — `MTYPE_HI`/`MI` send `MW_GETBLOOD_ACK` to the world
  (a `Calculate`-scaled fraction of the damage dealt). The basic-attack fallback is byte-preserved. Deferred: the
  buff-amplification / `CalcCure` layers of `CalcAbilityValue`, `DistributeSkill` (pets), the custom Araz ≥3000
  skills, and PvP.
- **Phase 43** — the direct HP/MP **transfer &amp; swap** execs, finishing the combat-core vitals family. On the
  self/ally `CS_DEFEND` path: `SCT_HPTRANS`/`SCT_MPTRANS` add HP/MP from the attacker's transferred amount
  (`wTransHP`/`wTransMP`, no over-heal roll); `SDT_STATUS_HPMPCHANGE` swaps HP↔MP (each side self-clamped to its
  own max, fails when MP is 0); `SDT_STATUS_HPTOMP` sacrifices half the current HP into MP. Both broadcast
  `CS_HPMP_ACK`. This closes the in-process, DB-free combat surface; the rest (`CalcCure`, buff-amplified damage,
  pet share, shield-block tail, Araz ≥3000 skills, PvP) needs other subsystems.
- **Phase 44** — the monster **aggro/hate table** (`CTMonster::m_mapAggro`), replacing the Phase-19 last-hitter
  targeting. Hate is skill-driven (`max(1, GetAggro)`, warrior ×1.5, accumulate, floor 0), keyed
  `(objId<<32)|objType`; the monster holds the **highest-cumulative-aggro** target with a **10% sticky
  hysteresis** (`SetAggro`), and on that target leaving (gone / past the leash) it **re-picks the next in-view
  hostile survivor** (`LeaveAggro`) before giving up. Every retarget runs the C++ `ChgHost` action —
  `EnterBattle` + seed the target's minimal hate + broadcast **`CS_MONHOST_ACK`** (TRUE to the new host). The
  same-country reject + `MT_GOHOME` aggro-immunity are ported; the `dwAggro` magnitude column, the `MT_GOHOME`
  walk-back, and call-for-help / low-HP flee (gated by unloaded chart flags) are deferred.
- **Phase 45** — **host acquisition** (aggro-on-sight, C++ `CTAICmdSetHost`): an idle **aggressive** monster
  picks the nearest recently-moved (`< 3000 ms`) host-eligible player in its 3×3 view and enters battle onto
  them, folding the C++ wake→`ChgHost`→`ChgMode` chain into the Phase-44 retarget. The "aggressive" gate is
  `Monster.Aggressive` — sourced from the DB `TAICHART` AI-script table in C++ (not a monster-chart flag).
  Phase 46 loads that table, so it is now **derived** per monster; before then it defaulted off, and
  the hit-driven aggro of Phase 44 is unaffected. `Character` gains `CanHost`/`LastMoveMs` (C++ `m_bCanHost`/
  `m_dwMoveTick`), stamped on every MOVE.
- **Phase 46** — the **data-driven AI engine** (`TAICHART`): the C++ AI is an *event machine*, not a tick
  sweep, and this ports the interpreter. Three charts load (`TAICMDCHART` opcodes → `TAICONCHART` guards →
  `TAICHART` per-`bAIType` trigger→command bindings, selected by the new `TMONSTERCHART.bAIType`), and
  `MapService.AiEngine.cs` runs them: all **14 triggers** and **15 command opcodes** with their exact
  `OS_*`×`MT_*` state gates and `AN_*` conditions, `AT_AICOMPLETE` chaining (so wake→`ChgHost`→`ChgMode`→
  `BeginAtk`→`Attack` is *data*, not code), and a **local** delayed-command scheduler replacing the C++ world
  round trip — keeping its `m_dwHostKEY` epoch check, `CanRun`/`ExecAI` revalidation order and `bLoop` re-arm.
  `AT_ENTER` becomes event-driven (from `CTCell::EnterPlayer`), `Monster.Aggressive` becomes **derived** from
  its `AT_ENTERLB` engagement chain, and `ChkHost` closes the Phase-45 gap. The four client-reported
  aggro-bound requests (`CS_ENTERLB`/`LEAVELB`/`ENTERAB`/`LEAVEAB_REQ`) are added — engagement is
  client-driven, so without them the chain has no entry point. A monster with **no** script keeps the
  Phase 18-45 hard-coded sweep, so an empty chart degrades instead of going inert.
  **Verified against the live baseline** (2 scripts / 47 bindings; script 1 aggressive with 3192 monsters,
  script 2 passive with 344) — which corrected the Phase-45 guess that aggression keys off
  `SetHost`/`AT_ENTER`: both live scripts bind that, so it would have made every monster aggressive.

Everything degrades DB-free. See [`PORT_STATUS.md`](PORT_STATUS.md) for the exhaustive done-vs-deferred
breakdown.

## What TMapSvr is

Unlike `TWorldSvr` (accept-only, server↔server plaintext), the map server is a **hybrid**:

- a **TCP listener for game clients** on the `CS_MAP` plane — **encrypted** (RC4-outermost inbound,
  XOR-only outbound; the deployed C++ sets `m_bUseCrypt = TRUE` at accept), port **5816**;
- an **outbound TCP client to the World server** on the `MW`/`SM`/`DM` planes — **plaintext**, port
  **3816** (set `WorldPort` to **3815** to talk to `TWorldSvr.Net`, which listens there).

## Architecture (C++ → C#)

- **Accept loop** (`ClientListener`) replaces the C++ AcceptEx/IOCP control thread; each client is a
  `ClientConnection` (recv-frame-decrypt / encrypt-send).
- **World link** (`WorldLink`) is the C++ `m_world` session: connect, `MW_CONNECT_ACK`, reconnect on drop.
- **One serialized batch task** (`MapWorker`) processes every client packet, world packet, disconnect and
  1-second tick in order, so `MapState` needs no locks (replaces the C++ batch thread + global lock).
- **`MapService`** is the C# counterpart of the `CTMapSvrModule` handler methods — one `sealed partial
  class` split into `MapService.<Feature>.cs` (Enter, Movement, Chat, Misc). Handlers are `On<MSG>`;
  senders `SendMW_*` / `SendCS_*`.
- **DB** (`GameDatabase`, `Microsoft.Data.SqlClient`) — the C++ `TMapSvr` embeds the DB-manager role and
  loads a char on its own connection during the handshake; we do the same directly, and degrade DB-free.

## Projects

| Project | What it is |
|---|---|
| `TMap.Protocol` | Wire codec (header/reader/writer/framer), asymmetric `SessionCipher` + `Crypto/*`, `Msg` IDs + enums. Dependency-free. |
| `TMap.Data` | `GameDatabase` (char + inventory/item/skill/maintain/hotkey load + item/magic/formula/class/race/item-attr/item-grade/monster/**skill**/**NPC** template charts) + `TemplateStore` (`ItemTemplate`/`MagicTemplate`/`FormulaRow`/`StatSeed`/`ItemAttr`/`SkillTemplate`/`NpcDef`/`QuestTemplate`) + `SqlProc`/`SqlExtensions` helpers. `Microsoft.Data.SqlClient` only. |
| `TMap.Server` | The worker host: `Net/` (listener, client connection, world link), `Map/` (`MapService.*` incl. `.Items`/`.Stats`/`.Npc`/`.Quest`/`.Persist`, `MapState`, `MapGrid`, `Cell`, `ClientSession`, `Character`, `Item`, `Skill`, `Npc`, `QuestProgress`, `Hotkey`, `StatEngine`), `MapWorker`, `Program`. |
| `*.Tests` | xUnit, DB-free (456 tests; the 4 `TMAP_TEST_GAME`-gated ones are counted here but assert nothing until a game DB is configured). Codec/cipher round-trips, the full enter handshake, movement/view/chat, disconnect, ping, item `WrapPacketClient` round-trip, populated CHARINFO/ENTER inventory + gear + skills + hotkeys (incl. C++-map key-order serialization), the spatial grid (cell math + 3×3 visibility + enter/leave-on-move diff), the jump/block broadcast sets (near-filter, self-echo) + first-spawn `bNewMember`, the item template charts (template `RefineMax` + computed `GetMagicValue`), the stat engine (derived stats + computed MaxHP/MaxMP + equip enchants + level scaling + HP clamp), the AP/DP sheet (naked + equipped-weapon attr + broken-item skip + the 87-byte `CS_CHARSTATINFO`), the skill-use spine (MP/HP cost + reuse cooldown math, the `<`/`<=` guards, the byte-exact `CS_SKILLUSE_ACK` payload), the skill-data engine (GetValue/Calculate `SVI_*` + attack-type/long classification + the CS_DEFEND damage scaling), HP/MP regen (flat/25% amounts, the battle-mode suppression + anchor reset), loot/exp (attacker-scaled exp + level-up + money corpse drop/take + the anti-farm rules), monster idle roam (CS_MONACTION_ACK on-radius destination, host-gated), monster aggro/chase (target-on-hit → TA_FOLLOW chase → leash drop-aggro), monster-attacks-player (melee AP−DP hit + cadence + player death), player revival (reposition + HP/MP-by-type restore + live-guard), item drop-loot (weighted drop roll → corpse items → CanPush take), NPC shops (talk country-gate + buy-charges-gold/adds-item + sell-earns-¼/removes + quest-free-buy), quests (accept→objective(get-item/kill/talk)→turn-in→reward, level/parent gates, fetch-item hand-over, drop, possible-quest list), skill learning (add-only grant + class-mask gate + `CS_SKILLBUY_ACK` byte layout), the cabinet/warehouse (open cost/already/max, put-in tradable-gate/merge/full, whole-stack take-out + fee, item-list byte layout, cabinet-storage save stamping), party gameplay (value-exact shared-exp split + level-weighting, non-member exclusion, solo-unchanged, party-keeper assignment, any-member loot + non-member denial + loot broadcast, PT_SOLO mask), player deal/trade (ask-notify, accept-opens-both, decline, add-notifies-partner, full item+money swap, first-confirm-no-execute, cancel, untradable-item, receiver-bag-full, not-enough-money), player store (open+broadcast, untradable/over-owned fails, close, browse layout, item+money buy, auto-close-on-last-sold, partial-offer remainder, not-enough-money, buyer-bag-full), the shield-block roll (rate-100-always/rate-0-never/no-shield/enchant-adds-to-rate+power/broken-shield-inert/magic-multivajra-variant, plus live monster→shielded-player reduces-damage+HT_BLOCK, unshielded full-damage, lethal-through-shield reports HT_LASTHIT), the CalcDamage exec dispatch (byte-exact vs the parsed damage map + world packet: MTYPE_HP/MP direct drain keyed 14/22, HP-heal restores+clamps, MTYPE_MDAMAGE MP-damage keyed 88 with HP untouched, multi-row two-entry map, full-roll-on-kill, SATT_NONE-deals-nothing, MTYPE_HI/MI MW_GETBLOOD_ACK lifedrain + no-blood-when-that-pool-untouched), the HP/MP transfer/swap execs (HPTRANS/MPTRANS add-from-transferred+clamp, HPMPCHANGE swaps-pools+per-side-clamp+MP0-fails, HPTOMP half-HP-into-MP+clamp), the monster aggro table (warrior ×1.5, accumulate+floor, same-country reject, the 10% hysteresis steal, LeaveAggro survivor pick, the GetAggro magnitude formula, the CS_MONHOST_ACK wire bytes, end-to-end leash re-pick of an in-view survivor over going home), host acquisition (aggressive-acquires-recent-player + CS_MONHOST_ACK, passive-never-auto-aggros, nearest-player pick, lazy-activation fallback, end-to-end MOVE→eligibility→acquire), persistence gating (IsSaveDue throttle/gate, the TSaveChar snapshot round-trip, dirty-quest collection + flag-clear), and the inventory snapshot (container/item DTOs, new-item dlID stamping, magic-slot packing). |

## Build & test

```bash
dotnet build TMapSvr.slnx -c Release
dotnet test  TMapSvr.slnx            # 456 tests, DB-free
```

### The DB-gated tests

Four `TMap.Data.Tests` cases exercise the real query path (ping, unknown-char load, the item/magic chart
load, and the Phase-46 `TAICHART` AI-script load). They are gated on `TMAP_TEST_GAME`, and **without it they
no-op** — they pass while asserting nothing, and log a `… — skipping` line saying so. A green suite is not
evidence the DB path works; point them at a live game DB to get that:

```powershell
# against the compose stack (repo-root docker-compose.yml; SA_PASSWORD comes from the .env beside it)
$env:TMAP_TEST_GAME = "Server=localhost,11433;Database=TGame_gsp;User ID=sa;Password=$env:SA_PASSWORD;TrustServerCertificate=True;Encrypt=False"
dotnet test TMapSvr.slnx
```

`LoadTemplates_PopulatesAiScripts_WhenConfigured` pins the live chart shape (2 scripts / 47 bindings /
6 conditions, script 1 aggressive + script 2 not). If a migration ever changes `TAICHART`, that test is the
one that should make you re-read the scripts — so it is worth running it deliberately after a DB change.

## Run

### Console executable

```bash
pwsh ./publish-console.ps1           # self-contained win-x64 + linux-x64 single-file exes
```

Then run `TMap.Server(.exe)`. It reads `appsettings.json` (section `Map`) and env overrides. It listens
for clients on `Port`, dials `WorldIp:WorldPort`, and runs DB-free if `Db:GameConnectionString` is empty
or unreachable.

### Docker

```bash
docker build -t tmapsvr .            # EXPOSE 5816
```

### The local stack (recommended)

The repo-root `docker-compose.yml` runs the whole cluster — SQL Server + the baseline restore/migrations,
then `loginsvr` (4816), `worldsvr` (3816), `patchsvr` (3716), `controlsvr` (3615) and **`mapsvr` (5816)**:

```bash
docker compose up -d --build         # from the repo root
docker compose logs -f mapsvr        # watch the map↔world handshake
```

`mapsvr` waits on the migration job and on `worldsvr`, then dials `worldsvr:3816` (the compose
`World__Port` override lines the world up with this server's `WorldPort` default — the world's own default
is 3815). A client's path through the stack is `patchsvr → loginsvr → mapsvr`.

## Configuration (`Map` section / env with `Map__` prefix, `__` nesting)

| Key | Default | Notes |
|---|---|---|
| `Port` | 5816 | Client listen port (C++ `GamePort`). |
| `WorldIp` / `WorldPort` | 127.0.0.1 / 3816 | The world to connect to (use 3815 for `TWorldSvr.Net`). |
| `GroupId` / `ServerId` | 1 / 1 | This map's identity in the topology. |
| `Channels` | `[1]` | Channels announced in `MW_CONNECT_ACK`. |
| `LogIp` / `LogPort` | 127.0.0.1 / 7000 | UDP log-sink endpoint (integration deferred). |
| `NoCrypt` | false | Set true to accept clients in plaintext (interop with a plaintext bot/client). |
| `Db:GameConnectionString` | — | Empty ⇒ run DB-free (char synthesized). |
| `Db:GlobalConnectionString` | — | Account security codes; falls back to the game server/credentials with catalog `TGlobal_gsp`. |

## Roadmap

See the current completed modules and remaining handlers in [`PORT_STATUS.md`](PORT_STATUS.md).
Security codes (S7d), cloaks (S7e) and meeting rooms (S7f) are complete. The remaining S7g miscellaneous
handlers and the relay/multi-map/anti-cheat planes are outside this stack's current scope.
