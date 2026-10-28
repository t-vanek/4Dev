# TMapSvr → TMapSvr.Net — Port Status

The current priority is a complete playable Gameforge baseline before custom gameplay changes. The
[readiness checklist](../../Gameforge/baseline-readiness.json) separates implementation, reference evidence,
database persistence and real-client verification. The baseline remains incomplete.

The [2026-10-28 deployment](../../Gameforge/deployments/2026-10-28-baseline.json) records the current
committed runtime: all five Docker services rebuilt without cache and running, migration 040 applied,
and a read-only probe of the deployed loader against live SQL. The client Release build is in `Game/`.
Map passes 1,492 tests and World 164. Across all five solutions 1,838 pass; five unchanged Control
local-process tests reproduce earlier Linux failures. Source-validation notes below describe their
original snapshots; their uncommitted/undeployed flags are historical. Real-client gameplay is pending.

Monster HP recovery now proportionally reduces every personal/party and guild damage bucket, truncating
each result as the inherited `CTMonster::Recover` does. Full recovery removes the remaining credit; an
eligible HP-recovery pass releases the keeper at `maxHP - floor(maxHP * 10 / 100)`. A new attacker can then
claim the monster and receive its existing EXP/loot rewards. MP-only, blocked, dead and not-yet-due HP
passes leave this state alone. Recovery preserves aggro, host, target, loot-window locks and AI epochs.
Monster appearance and colour notifications now use each viewer's faction and personal/party keeper;
notifications cover claim/release, AI mode changes and fort capture, with HP/MP preceding recovery colour.
Relevant history was reviewed before editing: initial port `34a9afe`, AI fix `3515365`, cancellation
performance `0ca6e5e`, loot locks `7a240ad` and fort fix `6a08080`. The previous transition-only recovery
anchors, O(1) cancellation and next-war fort reset remain. Validation: 35 new cases and 1,492 map-solution
tests pass on Linux .NET 10; 22 earlier retained-code checks and seven focused checks pass. No existing
assertion was removed for this slice. Evidence: `Gameforge/monster-recovery-credit.json`. Source is now
deployed as recorded above; pinned-client gameplay and independent official-rule parity remain unverified.

The targeted [C# history audit](../../Gameforge/port-history-audit.json) checks the current changes
against 13 earlier commits, including movement/fall damage, summon combat, buffs, authentication,
loot locks and AI cancellation. Seven complete files and 15 selected methods match HEAD. Five changed
legacy assertion lines are documented explicitly: authenticated PC host, authoritative monster magic
power and waiting for the World verdict during teleport. The audit found that take-all bypassed the
new party-money/access rules; it now shares distribution and access gates while retaining the earlier
trade/capacity/item-owner protections. Separate item/money requests also honor the trade gate.
Nineteen new regressions pass; the Linux map solution passes 1,457 tests. History review is now required
before changing existing .NET behaviour. This bounded review does not certify every C# fix or full parity;
that audit was recorded before commit and deployment; the current deployment is recorded above.

Weapon and ammunition requirements are implemented for ordinary player casts, loop repeats and act-item skills.
The matching equipped weapon must have power; its ammo kind/base count come from the newly loaded existing
`bUseItemKind/bUseItemCount` columns. Server-selected multi-hits add ammunition cost. Required skill items and
ammunition are reserved together before any consumption, HP/MP debit or cooldown, avoiding partial losses and
BYTE overflow. Update/delete packets use the existing incremental persistence hooks. Unsupported legacy
infinite-ammunition IDs are not exemptions: the pinned Gameforge item catalogue contains none of 25020–25022.
Validation passed 1,320 map-solution tests on Linux .NET 10, including 36 new gameplay cases and one new
database-gated field check. That field check also passed separately against live SQL, read-only, for 142 templates.
Windows blocked the rebuilt server DLL with application-control error `0x800711C7`; no host security policy was
changed. The current deployment is recorded above; pinned-client playthroughs and actual logout/restart persistence remain unverified.
Ordinary cast wear and active-effect prerequisites are now implemented as described below.

`wPrevActiveID` gates actions, ordinary casts and act items; the original loop's `wTargetActiveID`
gates repeats on the caster. Summons use their own effects. The required effect must be maintained and
unexpired. Target-active effects gate landed damage, buffs and cures, through FINISHSKILL and legacy
DEFEND. `bDuraSlot` selects the equipment worn by an accepted ordinary player cast: ranged weapon,
both melee weapons excluding shields, one of six armor slots, or the secondary shield. The inherited
0–2 wear roll, forced user-item exhaustion, durability packets, break/delete handling, stat refresh
and persistence snapshots are implemented. Natural wear never destroys an indestructible 0/0 item.
Actions, loops and impact replays do not apply ordinary cast wear. Item-option `wAutoSkill` uses its
stored option value and ID order; only the server-rolled effect is announced and authorized at level 1.
Validation: 46 new gameplay cases, 1,367 map-solution tests on Linux .NET 10, one separate live read-only
SQL check on 765 skills / 27 magic-option templates, and five migration checks on connection-local temp
tables. The pinned original client's three fields match on all 765 shared IDs except skill 201's absent
target-active effect 200 in SQL. Migration 040 restores that one field; its frozen evidence is
`Gameforge/normalization/skill-active-wear.json`. Migration 040 and the source changes are now deployed.
Original-client gameplay and real logout/restart durability persistence still need verification.
The inherited wear/proc probability is not independent evidence of official Gameforge balance.

Defensive equipment wear and reactive item branches are now implemented. A landed damage skill wears
the secondary shield on block; otherwise positive net HP loss selects one of the six native armor slots.
It happens after CalcDamage and before OnDamage, including lethal hits. Misses, healing, attribute-zero
rows, immunity and unblocked MP-only damage do not wear armor. The target-delete flag, durability packets,
break/repairability rules, stat refresh and queued persistence share the ordinary cast helper. Native
CanFight gates the rolled reaction after wear. Damage-modifier options apply DAMAGE then HP scaling at
the incoming skill's level; mirrors forward only HP loss through World; other rolled effects are announced
as a defender counter-skill and receive a bounded level-one authorization against the attacker.
The original damage map remains the pre-reaction display, while HP/MP packets carry the resulting bars.
The missing `MW_GETBLOOD_REQ` receiver now applies HP/MP lifedrain and reflection to the routed main
player, its hosted monster or its own recall/self/companion. Key, main/map/channel/ownership and live-object
checks prevent stale or foreign updates. Gains cap at max without overflow; reflection leaves at least
1 HP and never re-enters Defend. A game client cannot send this World-only instruction.
Validation: 71 new cases (32 defensive-equipment + 39 blood-return), 1,438 map-solution tests passed
on Linux .NET 10. Source/data evidence is in `Gameforge/normalization/defensive-equipment.json`.
Read-only live SQL inspection found no configured automatic option-skill links. The branches are tested
with isolated templates; official assignments and probabilities are not inferred or added to live data.
No deployment or production chart/character writes were performed during that initial validation. Original-client playthroughs and
actual logout/restart durability persistence remain pending.

Channel switching is implemented for the hosted `Map:Channels` on one map server. `CS_CHGCHANNEL_REQ`
starts the full BEGINTELEPORT → STARTTELEPORT → TELEPORT/CONLIST → CHECKMAIN/CONRESULT → client CONREADY
cycle so channel-specific grids and views actually change. It preserves the character, inventory, vitals,
position and following summons. Unhosted channels, private meeting rooms, arena participants and unavailable
World links receive `CHC_NOCHANNEL` before loading. A current-channel request is ignored. Pending moves block
overlapping gameplay and premature ready packets; World loss closes a loading session for relog. Old cast
budgets are cleared on leaving. `ChannelTests` adds 18 cases, and the World socket suite adds two routing cases.
The inherited acknowledgement-only shared-map branch and multi-map-server handoff are not claimed by this
single-server implementation. Source is deployed with channel 1; real multi-channel/pinned-client verification remains pending.
Validation: 1,283 map-solution tests and 164 world-solution tests passed, including the 20 new channel cases.

Natural HP/MP recovery now respects STOPRECOVER on players and monsters. Monster recovery also uses the
same action-blocking gate as the inherited Recover implementation and applies HPR/MPR modifiers to its
quarter-max recovery amounts. Player battle timeout continues while recovery is blocked; HOLD alone
does not stop player recovery. `RecoverTests` covers blocking, expiry, resumed recovery and modifier bounds.
This is a functional correction against the inherited implementation, not independent proof of publisher balance.

The gameplay-integrity audit fixes stale personal-store offers (item identity, tradability and live quantity),
foreign-player skill/loop casts, unpaid or replayed player impacts through both FINISHSKILL and legacy DEFEND,
and missing-skill fallback hits. Connection-local cast budgets retain chart-defined repeats, missiles and
linked effects. The skill loader now also reads `bRepeatCount`, `wTriggerID` and `dwActionTime`; no new SQL
columns or packet fields are required. Act-item casts authorize their paid effects separately from learned skills.

Monster attacks use the common typed HP/MP damage engine with physical/magic defence, immunities and maintained
effects. Attack-blocking statuses gate player casts and monster attack starts. Quest magic-item and skill
rewards are granted before consuming ingredients; invalid or unsupported selected rewards leave requirements
intact. Party corpse money is shared by level, with rounding residue paid to the collector, and direct hunter
loot requests enforce the same access rule as opening the corpse. `GameplayIntegrityTests` covers these fixes;
older combat fixtures now perform the real cast-to-impact sequence. These changes do not certify full Gameforge parity.

The gameplay target is now **Gameforge 5.0+**, anchored in the documented 5.0.1 rules. The
[version audit](../../Gameforge/version-target/report.html) and [profile](../../Gameforge/target.json)
take precedence when choosing current gameplay rules. Older port phases below describe implementation
history, not certified 5.x parity. The publisher-team EXP curve through 140 is now deployed (035),
including 27 corrected old thresholds and 15 new level rows. The new non-EXP/non-client-fee fields use
the last existing level's compatibility values; they are not certified original curves.
The wire version `0x2918` is separate from a Gameforge release number. Companion level 20 is enforced
on both level-up and EXP-item requests; the remaining point budget and reserve-bag rules need separate work.
Current Linux .NET 10 validation: **1,177 tests passed**, including 19 progression and six companion-cap
cases. SQL-backed probes against deployed DLLs verify 15 level boundaries, 12 delivery quests and
26 purchases at two new shops. World units for Anbari/Undred are routed (036), but monster populations,
126 other missions and nine portal NPCs remain missing. See the [report](../../Gameforge/progression-140/report.html),
[tests](../../Gameforge/progression-140/tests.json) and [deployment](../../Gameforge/progression-140/deployment.json).
Counts in older phase notes are historical.

Skill movement now executes on the landed-skill path: `SDT_STATUS_RETURN` uses the character's bound
spawn, `RETURNPOS` uses the effect row's spawn, and `TELEPORT`/`WARP` send the legacy `CS_WARP_ACK` to
the target's client (or the host for a forced monster warp). The client validates the landing and
synchronizes it through its ordinary movement packet. This fixes casts that animated and started their
cooldown without moving the character. No skill-chart values or saved return positions are changed.
`TeleportSkillTests` cover the cast-to-return sequence, blink wire layout, dismount, explicit return,
dead/unlearned casters, map isolation and protected monster templates. Validation passed all 1,191
Windows .NET 10 map-server tests. A read-only SQL-backed probe using the deployed Linux DLLs also
verified successful casts and movement packets for Return to Village (35) and Shadow Jump (437).

C#/.NET 10 port of the C++/ATL `TMapSvr` (the per-map/zone game server). Byte-exact wire/DB compat with
this repo's client + SQL baselines, mirroring the sibling `TLoginSvr.Net` / `TWorldSvr.Net` ports (same
`.slnx` layout, `Microsoft.Data.SqlClient`, Serilog worker host, single serialized batch task, DB-free
test harness). **456 tests passing** (xUnit, DB-free — 4 of them are `TMAP_TEST_GAME`-gated and assert
nothing unless a live game DB is configured; they log the skip) · listens on **:5816** for clients,
connects out to the world on **:3816**. Phases 1–13 were **audited against the C++** — the wire layouts, DB reads, grid math,
the `OnMove` visibility diff, and the item/stat/combat formulas are byte/value-exact (see the audit notes under
Phases 2, 3, 4, 5 and the consolidated "Audit (Phases 6–13)" note after Phase 13). **Phase 14** added
`CS_SKILLUSE` — the attack-announce half (skill chart + caster MP/HP cost + reuse cooldown + power broadcast),
closing the two-packet attack begun by the Phase-13 `CS_DEFEND` damage half. **Phases 15–18** filled out the
combat/monster loop: **15** skill-data damage scaling (`TSKILLDATA`/`m_vData` → the `CS_DEFEND` roll +
attack-type/long classification), **16** HP/MP regen (`Recover`), **17** loot/exp on death (attacker-scaled
exp + level-up + money corpse), **18** monster idle roam (`CTAICmdRoam` → `CS_MONACTION_ACK`), and **19**
monster aggro + chase (`CTAICmdChgMode`→BATTLE + `CTAICmdFollow`, leash drop-aggro), and **20**
monster-attacks-player (`CTAICmdAttack` → melee AP−DP hit + player death), **21** player revival
(`CS_REVIVAL` → reposition + HP/MP-by-type restore), and **22** item drop-loot (`TMONITEMCHART` → corpse
items + `CS_MONITEMTAKE`). **Phase 23** added **NPC shops** — the NPC registry (`TNPCCHART`/`TNPCITEMCHART`)
+ `CS_NPCTALK` (country-gated), `CS_ITEMBUY` (charge gold, add item) and `CS_ITEMSELL` (earn ¼, remove item).
**Phase 24** added the **quest engine** core — the `CQuest` template graph + trigger index + `CheckQuest`
hook + `m_mapQUEST` progress: accept (`CS_QUESTEXEC`) → objective advance on get-item/kill/talk
(`CS_QUESTUPDATE`) → turn-in + reward (`CS_QUESTCOMPLETE`), for the foundational subtypes.
**Phase 25** added **persistence** — the char record (`TSaveChar`) + quest progress (`TSaveQuest`/
`TSaveQuestTerm`) are written back to SQL Server on a 30-min timer / disconnect / shutdown (the first `DM_*`
save path). **Phase 26** extended it to **inventory** (`TSaveInven`/`TSaveItem` under the `TSaveItemData*` staging→live
bracket) — items now persist alongside the char record, closing the money↔inventory relog desync. **Phase 27**
added the **incremental item fast-path** (`TSaveItemDirect`): each item change persists per-tick, so a hard
crash no longer loses up to 30 min of item state. All Phase 25/26/27 save procs are **live-DB round-trip
verified** (rolled-back smoke tests against `araz-mssql`, which also corrected the Phase-26 bracket choice + a
NULL-vs-`1900-01-01` expiry-sentinel bug). **Phase 28** added **combat quality** — `GetAtkHitType` (a
level-scaled miss/normal/crit roll, both attack directions) + the **magic-damage branch** (magic AP vs monster
`wMDP`) + the `FTYPE_PCD`/`MCD` crit-damage formulas. **Phase 29** made the quest engine live — the **DB
quest-template load** (4 charts → 6261 templates, live-DB verified) + 5 more subtypes (DeleteItem / DropQuest /
ChapterMsg / Routing / same-map Teleport). **Phase 30** added **quest-progress load-on-enter** (closing the
Phase-25 save→no-reload hole) + the **enter-time `CS_QUESTLIST`** (the quest log on login).
**Phase 31** added the **maintained-skill (buff/debuff) engine** — `CalcAbilityValue` now layers active buffs
onto every stat getter (`base → items → buffs`), buffs apply via a buff-type `CS_DEFEND` (self/ally +
monster debuff) and the `ForceMaintain` grant, expire on the per-tick `CheckMaintainSkill` sweep (new
`CS_SKILLEND_ACK`), and are dropped on death — unblocking the Phase-29-deferred **quest DefendSkill** subtype.
**Phase 32** added the **map switch/gate subsystem** — chart-driven per-(channel, map) switches + switch-driven
gates placed in the grid (add/del on enter/move like monsters), the player toggle (`CS_SWITCHCHANGE_REQ` with
lock + duration-cooldown gating), gate propagation (`GT_MULTISWITCH` = all-switches-match), the `dwDuration`
auto-revert, and the `TT_RUNSWITCH`/`TT_RUNGATE` quest triggers + `QCT_SWITCH` condition — unblocking the
Phase-29-deferred **quest Switch** subtype.
**Phase 33** added **quest-driven monster spawn** — the time-limited-spawn engine (`AddTimelimitedMon`/
`DelMonSpawn` on top of the Phase-12 spawn model) — unblocking three more Phase-29-deferred subtypes:
**SpawnMon** (`QTT_SPAWNID` add / `QTT_SPAWNID_DEL` remove), **DieMon** (force-kill a spawn — credited or
`SE_QUESTDEL`-silent), and **DropItem** (attach an owner-locked item to a killed monster's corpse).
**Phase 34** added the last quest-spawn subtype, **Regen** — killing a monster mints a one-shot dynamic
replacement of another kind at the death spot (`RegenDynamicMonster` → `AddTimelimitedMon`), linked so the
original's respawn force-removes the temp (`m_wRegenDelSpawn`); the `SE_DYNAMIC` spawn never re-arms and its
reserved id is recycled. The monster-spawn subtype family (SpawnMon/DieMon/DropItem/Regen) is now complete;
the remaining deferred subtypes are **Craft**/**GiveSkill**/**SendPost** (self-completion / skill-learn / mail).
**Phase 35** added the **cure/dispel** layer completing the buff engine — a cure skill (`SDT_CURE` rows) cast on
self/an ally via `CS_DEFEND` strips positive/negative maintains (`SCT_POSREMOVE`/`SCT_NEGREMOVE`) and instant-heals
HP/MP (`SCT_HP`/`SCT_MP`, with the 0-15% over-heal roll).
**Phase 36** added the **skill-learn** path (`CTObjBase::UpdateSkill` — add-only grant + `CS_SKILLBUY_ACK`),
completing the quest **GiveSkill** subtype (`CQuestGiveSkill` — learn the `QTT_SKILLID` term's skill, class-mask
gated, recurse children only when newly learned); the remaining deferred subtypes are now just **Craft**/**SendPost**.
**Phase 37** added the **player cabinet** (item warehouse) — up to 3 per-character cabinets × 16 items:
open (per-id gold cost) / list / item-list / put-in (tradable-gated, merge-then-one-new-slot) / take-out
(whole-stack, per-id fee), loaded on enter and persisted incrementally as `STORAGE_CABINET` item rows.
**Phase 38** added the map-side **party gameplay** — a party-member kill makes the party the monster's keeper
(`OWNER_PARTY`), its **exp splits** among the near party members (size-bonus × level-weighted share × level-gap
scale), and its corpse is **lootable by any member** (PT_FREE), broadcasting the loot to near members. Party
membership stays world-authoritative (management relays deferred).
**Phase 39** added player-to-player **deal (trade)** — a same-map two-party state machine (ASK → accept → offer
→ two-phase confirm → atomic item+money swap), with a full guard (offers re-validated + both bags fit) before any
mutation so a full bag aborts cleanly.
**Phase 40** added the personal **store** (player vendor) — open a listing over own bag items at prices, nearby
players browse + buy (money buyer→seller, item copied to the buyer, offer + seller stack decremented, auto-close
when sold out); the store flag rides `CS_ENTER_ACK` for late-joiner visibility.
**Phase 41** ported the **shield-block roll** (`GetShieldDP`/`GetShieldMDP`) — the defender-side block roll inside
`CalcDamage`: an equipped shield rolls `ABILITY_SDR` and, on success, adds its `ABILITY_SDP` to defence
(additive reduction, 5/7 floor holds) and flags `HT_BLOCK`; wired live into the monster→player PC-defender path.
**Phase 42** completed **`CalcDamage`** — the exec-aware per-data-row damage dispatch: a skill's `SDT_ABILITY`
rows resolve by attr (physical/magic → AP band + DP) then exec — `MTYPE_DAMAGE` (30, HP), `MTYPE_MDAMAGE` (88,
MP), `MTYPE_HP`/`MTYPE_MP` (direct heal/drain) — accumulating the net HP/MP change + the per-exec `CS_DEFEND_ACK`
damage map (full roll, `OnDamage` floors/clamps); plus `MTYPE_HI`/`MI` **lifedrain** (→ `MW_GETBLOOD_ACK`).
**Phase 43** finished the combat-core vitals family — the direct HP/MP **transfer &amp; swap** execs on the
self/ally `CS_DEFEND` path: `SCT_HPTRANS`/`SCT_MPTRANS` (add HP/MP from the attacker's transferred amount) and
`SDT_STATUS_HPMPCHANGE` (swap HP↔MP) / `SDT_STATUS_HPTOMP` (sacrifice half HP into MP).
**Phase 44** replaced the Phase-19 last-hitter targeting with the real **aggro table** (`m_mapAggro`): hate is
skill-driven (`max(1, GetAggro)`, warrior ×1.5, accumulate, floor 0), keyed `(objId&lt;&lt;32)|objType`, and the
monster holds the **highest-cumulative-aggro** target with a **10% sticky-target hysteresis** (`SetAggro`);
on the target leaving (gone / past the leash) it **re-picks the next in-view hostile survivor** before giving
up (`LeaveAggro`), and every retarget broadcasts **`CS_MONHOST_ACK`** (TRUE to the new host) — the C++
`ChgHost` action. The same-country reject + `MT_GOHOME`-aggro-immunity are ported; the `dwAggro` magnitude
column, the `MT_GOHOME` walk-back, and call-for-help / low-HP flee (gated by unloaded chart flags) are deferred.
**Phase 45** added **host acquisition (aggro-on-sight)** — the C++ `CTAICmdSetHost` scan: an idle **aggressive**
monster picks the nearest recently-moved (`< 3000 ms`) host-eligible (`CanHost`) player in its 3×3 view and
enters battle onto them, folding the C++ wake→`ChgHost`→`ChgMode` chain into the Phase-44 retarget. The
"aggressive" gate (`Monster.Aggressive`) is sourced from the DB `TAICHART` AI-script table in C++ (not a
monster-chart flag); Phase 45 shipped it defaulting off because that table was unloaded. **Phase 46** loads
`TAICHART`/`TAICMDCHART`/`TAICONCHART` and ports the whole event machine on top of them — the 14 triggers, the
15 command opcodes with their state gates and `AN_*` guards, `AT_AICOMPLETE` chaining, the delayed-command
scheduler with the `m_dwHostKEY` epoch (local, replacing the C++ world round trip), and the `bLoop` re-arm — so
`Aggressive` is now **derived** per monster from the script's `AT_ENTERLB` engagement chain, and scripted monsters run their
real chart behaviour instead of the built-in roam/chase sweep (which stays as the no-script fallback).
**Phase 7** added the `CS_MOVEITEM` item-manipulation handler (move/swap/split/merge/
drop + equip/unequip with live stat recompute + the `CS_EQUIP_ACK` appearance broadcast); **Phase 8** added
`CS_ITEMUSE` for HP/MP potions (heal + clamp + consume + the `CS_HPMP_ACK` bar broadcast).
**Phase 4** added the item/magic **template charts** (template `RefineMax`, computed `GetMagicValue`).
**Phase 5** added the `CTObjBase` **stat/vitals core** — the derived primary stats and computed
`GetMaxHP`/`GetMaxMP`, so the MaxHP/MaxMP the `CHARINFO`/`ENTER` packets carry are now real, not synthesized.
**Phase 6** completed the stat sheet — **AP/DP + attack-speed/crit/charge** and the **`CS_CHARSTATINFO`** packet.

**Phase 1** delivered the enter + movement/chat vertical slice. **Phase 2** added character data
completeness — the player's **inventory, equipped gear, skills, and hotkeys** are now loaded from the DB
and serialized into `CS_CHARINFO_ACK` and `CS_ENTER_ACK` (byte-exact `CTItem::WrapPacketClient`).
**Phase 3** added the **`CTCell` spatial grid** — visibility is now the real 64-unit 3×3 cell block with
proper enter/leave-on-move, replacing the Phase-1 whole-map broadcast.

> **Scale note (read this first).** `TMapSvr` is by far the largest server in the cluster: **~54,000 lines**
> across its four handler/sender files alone (`CSHandler.cpp` 21k, `SSHandler.cpp` 20k, `CSSender.cpp` 7k,
> `SSSender.cpp` 5.5k), **305 client handlers**, **325 server handlers**, and **250 DB queries** — plus the
> deep combat/stat/skill/monster-AI/quest engines in `TObjBase`/`TMonster`/`TMap`/`Quest*`. A byte-complete
> port is a multi-phase effort, exactly as `TWorldSvr.Net` was built up over its "Phases 1–4". **This is
> Phase 1: the foundation + the client-enter vertical slice that actually runs** — a client connects,
> completes the full map↔world handshake, spawns, and moves/jumps/chats with other players in view, all
> degrading DB-free. The large remaining surface is enumerated as a roadmap at the bottom, honestly marked.

---

## ✅ Done (Phase 1 — the enter + movement vertical slice)

- [x] **Solution + projects** — `TMapSvr.slnx` with `TMap.Protocol` / `TMap.Data` / `TMap.Server` (+ three
  xUnit test projects), net10.0, `Microsoft.NET.Sdk.Worker`, Serilog, Dockerfile (`EXPOSE 5816`),
  `publish-console.ps1`. Identical conventions to the sibling ports.
- [x] **Protocol layer** — the full TNetLib wire codec, ported verbatim from the login/world ports:
  `PacketHeader`/`PacketReader`/`PacketWriter`/`PacketFramer`, the asymmetric `SessionCipher`
  (client plane: RC4-outermost inbound, XOR-only outbound; server plane: plaintext), and the
  `Crypto/*` (XOR body+header + checksum, RC4, the `g_4skey` table + MD5 secret). `NetCode.cs` carries
  the `CS_*`/`MW_*`/`SM_*` message IDs the map uses, the `ConnectResult`/`ChatGroup`/`SvrType` enums, and
  the `Proto` helpers (`MakeServerId`, `IpToUInt`, `ComputeConnectChecksum`).
- [x] **Hybrid networking** — the map is both a **crypto-ON TCP listener for clients** (`ClientListener` +
  `ClientConnection`, the CS_MAP plane, cipher on by default like the deployed C++) **and an outbound
  plaintext TCP client to the world** (`WorldLink`, the MW/SM/DM plane, with connect + reconnect backoff).
  Neither sibling port had the "connect out to the world" piece; it's new here.
- [x] **Single serialized batch task** — `MapWorker` funnels every client packet, world packet, disconnect
  and 1-second tick into one `Channel<BatchItem>`, so `MapState` is lock-free (replaces the C++ batch
  thread + global lock), exactly as `TWorldSvr.Net`.
- [x] **World registration** — on (re)connect the map sends `MW_CONNECT_ACK` (`MAKEWORD(serverId, SVR_MAP)`
  + its channel list), matching `SendMW_CONNECT_ACK`.
- [x] **Full client-enter handshake** — the complete map side of the sequence, each world-driven `MW_*_REQ`
  answered independently:
  `client CS_CONNECT_REQ` (version + anti-tamper checksum validated) → `MW_ADDCHAR_ACK` →
  `MW_ENTERSVR_REQ`→`MW_ENTERSVR_ACK` (char resolved here, DB-manager role folded in-process) →
  `MW_CHARDATA_REQ`→`MW_CHARDATA_ACK` → `MW_CHARINFO_REQ`→**`CS_CHARINFO_ACK`** (the client gets its
  character) → `MW_ROUTE_REQ`→`MW_ROUTE_ACK` → `MW_ENTERCHAR_REQ`→`MW_ENTERCHAR_ACK` (world's live char
  state overlaid) → `MW_CHECKMAIN_REQ`→`MW_CHECKMAIN_ACK` → `MW_CONRESULT_REQ`→**`CS_CONNECT_ACK`** (enter
  granted) → `client CS_CONREADY_REQ` → player goes live and exchanges `CS_ENTER_ACK` with neighbours.
- [x] **Character resolution** — `GameDatabase.LoadCharAsync` loads the persistent appearance row from
  `TCHARTABLE` (`CTBLChar` subset), overlaid with world-provided name/map/position/guild/party from the
  handshake packets. **Degrades DB-free**: with no reachable game DB the char is synthesized (neutral
  newbie at the observed fresh spawn) so the whole flow still completes.
- [x] **Movement / view** — `CS_MOVE_REQ`→`CS_MOVE_ACK` (incl. the `> 3.40` speed-hack kick),
  `CS_JUMP_REQ`→`CS_JUMP_ACK`, `CS_BLOCK_REQ`→`CS_BLOCK_ACK`, broadcast to neighbours; `CS_ENTER_ACK`
  (the full appearance/position/state block) and `CS_LEAVE_ACK` on entry/exit.
- [x] **Chat** — `CS_CHAT_REQ`→`CS_CHAT_ACK`: local `CHAT_NEAR` broadcast and on-map `CHAT_WHISPER` are
  delivered directly (with sender-spoof rejection); party/guild/tactics/map/force + off-map whispers are
  forwarded to the world via `MW_CHAT_ACK`.
- [x] **Session housekeeping** — `CS_PINGMEASUREMENT_REQ`→`CS_PINGMEASUREMENT_ACK`, `CS_DISCONNECT_REQ`,
  `CS_TERMINATE_REQ` (the C++ backdoor-probe no-op), and clean disconnect (view-leave broadcast +
  `MW_CLOSECHAR_ACK` to the world). Inbound `MW_TERMINATE_REQ` / `MW_CLOSECHAR_REQ` drop the char.

> **Fidelity notes / documented simplifications (Phase 1):**
> - ~~Visibility uses a **whole-map broadcast**~~ — **superseded by Phase 3**: visibility is now the C++
>   `CTCell` `CELL_SIZE` (64-unit) 3×3 grid fan-out.
> - The map is a **single-server deployment** (`MW_ROUTE_ACK` returns an empty neighbour-server list); the
>   cross-map-server routing / relay handoff is not exercised.
> - World-relayed chat **delivery** back to clients (`OnMW_CHAT_REQ`) is a no-op stub (the exact
>   `MW_CHAT_REQ` payload layout isn't transcribed yet); local chat works end-to-end.
> - Historical Phase-1 deferrals: mode/region handlers were implemented in later phases; channel switching is now implemented as described above.

## ✅ Done (Phase 2 — character data: inventory, gear, skills, hotkeys)

- [x] **`CTItem` + `WrapPacketClient`** — the item instance model with the **byte-exact** client serializer
  (`Map/Item.cs`): slot, template, gem/mogg/companion, count, durability, refine, glevel, `__time64_t`
  expiry, grade, the ELD/WRAP/COLOR/CUSTOMTEX ext values (the `m_dwExtValue[]` array, indexed by `IEV_*`),
  the guild-registration flag (`Ext[IEV_GUILD] == ownerId`), and the magic-option list (`{id, value}`).
- [x] **`CTInven` containers** (`Map/Item.cs`) — inventory containers keyed by id (backpack `0xFF`,
  equipped `0xFE`, timed bags), each with template + `__time64_t` expiry + its items.
- [x] **Skills / maintained skills / hotkeys** (`Map/Skill.cs`, `Map/Hotkey.cs`) — learned-skill entries,
  the full 19-field maintained-skill block, and hotkey pages (`MAX_HOTKEY_POS` = 12 slots).
- [x] **DB load** (`GameDatabase`) — `CTBLInven` (bags), `CTBLItem` (the 32 of 35 columns the map needs —
  `dlID`/`bOwnerType`/`dwOwnerID` are omitted by the explicit-column read), `CTBLSkill`,
  `CTBLSkillMaintain` (the 8 persisted fields), and `CTBLHotKey` (positional `SELECT *`, guarded against an
  unexpected DDL). `dEndTime` datetimes are converted to `__time64_t`. Items are bucketed into containers
  by `(bStorageType == STORAGE_INVEN, dwStorageID)`.
- [x] **Serialized into the wire** — `CS_CHARINFO_ACK` now carries the real inventory (containers + items),
  learned skills, maintained skills and hotkey pages; `CS_ENTER_ACK` now carries the maintained-skill block
  and the equipped-gear items. All still **degrade DB-free** (no DB ⇒ empty containers, handshake still
  completes).

> **Fidelity notes (Phase 2):**
> - ~~`RefineMax` per-instance~~ / ~~magic loop emits raw `wValue`~~ — **closed in Phase 4**: `RefineMax`
>   now comes from the item template and the magic loop emits the computed `GetMagicValue()` (raw fallback
>   only when DB-free).
> - ~~Cabinet/warehouse items are loaded-but-skipped~~ — **closed in Phase 37**: `STORAGE_CABINET` items now
>   load into the player's cabinets; `STORAGE_POST` (mail) is still excluded (deferred).
> - Item **cooltime** and the `CS_CHARINFO_ACK` item-cooldown sub-loop remain 0 (the cooldown engine is
>   deferred).

> **Audit (Phase 1 + 2).** All packet layouts (`CS_CHARINFO_ACK` 56 fields + 5 sub-loops, `CS_ENTER_ACK`,
> `CTItem::WrapPacketClient`, the full MW handshake, movement/jump/block, chat) and every DB read were
> walked field-by-field against the C++ `TMapSvr` and found **byte/column-exact** (widths, order, count
> prefixes, packet IDs). Fixes from the audit: collections are now emitted in the C++ `std::map` key order
> (items by slot, containers by id, magic by id, skills by id, hotkey pages by inven-key) for exact
> byte-sequence parity; the "35-column" item-read wording corrected to "32 of 35". Known non-layout gaps
> (all tied to the not-yet-modelled GM/operator concept, so out of Phase 1–3 scope): the move speed-hack
> gate and `MW_CHAT_ACK` `bType` omit the operator exemption, and chat-spoof drops rather than issuing the
> C++ 30 s chat-ban. Open verification debt: the hotkey `SELECT *` positional order matches the C++ bind
> order but is unconfirmed against the live schema (docker was down) — recheck `THOTKEYTABLE` ordinals when
> the DB is up.

## ✅ Done (Phase 3 — the `CTCell` spatial grid)

- [x] **`MapGrid` + `Cell`** (`Map/MapGrid.cs`, `Map/Cell.cs`) — the C# port of a C++ `CTMap`'s cell map
  (`m_mapTCELL`) and `CTCell`. Byte-exact grid math from `NetCode.h`/`TMap.cpp`: `CELL_SIZE = 64`; cell
  index per axis = `(WORD)pos / CELL_SIZE` (float cast to **unsigned 16-bit first**, indexed by world
  **X and Z**, no origin offset); cell key = `MAKELONG(cellX, cellZ)` (X low word, Z high). Cells are
  created lazily (single-server ⇒ every cell enabled ⇒ absent cell == empty cell).
- [x] **3×3 visibility fan-out** (`CTMap::GetNeighbor`) — a player sees the occupants of the centre cell
  plus its 8 neighbours (scan origin = centre − 1 per axis, negative indices skipped, outer loop Z / inner
  X). `MapState.Neighbors` now returns this block instead of the whole map, so **all** existing broadcasts
  (move/jump/block, near-chat, enter/leave) are cell-scoped for free.
- [x] **Per-(channel, map) grids** — `MapState` owns one `MapGrid` per `(channel, mapId)`, the C#
  counterpart of the C++ `CTChannel → CTMap` split. A player is placed into its grid on entry
  (`EnterWorld`, C++ `CTMap::EnterMAP`) and removed on disconnect/leave (`LeaveWorld`, `CTMap::LeaveMAP`).
  Each session caches its grid + cell key (C++ `CTPlayer::m_pMAP`), so membership never desyncs from
  position.
- [x] **Enter/leave-on-move diff** (`CTMap::OnMove`) — `MapGrid.Relocate` re-buckets a moved player and
  returns the set-difference of the old 3×3 cell block against the new one: players only in the new block
  get a bidirectional **`CS_ENTER_ACK`**, players only in the old block get a bidirectional
  **`CS_LEAVE_ACK`** (`bExitMap = FALSE`), players in both are left untouched; a move that stays inside one
  cell is a no-op (just the move broadcast). Wired into `CS_MOVE_REQ`/`CS_JUMP_REQ`/`CS_BLOCK_REQ` via a
  shared `RelocateAndExchangeView` helper.

> **Fidelity notes (Phase 3):**
> - Cells hold **players** (`m_mapPLAYER`), **field monsters** (`m_mapMONSTER`, Phase 11) and **switches/gates**
>   (`m_mapSWITCH`/`m_mapGATE`, Phase 32); recall-mons, self-objs, companions — and the per-channel border-cell
>   flags (`m_vEnable`/`m_vExtCell`/`m_vServerID[8][51]`) that drive cross-server/cross-channel visibility via
>   `IsMainCell`/`IsEnable` — are single-server-inert and deferred.
> - The tighter `GetNeerPlayer`/`GetNeerMonster` (3×3 ∩ Euclidean ≤ `CELL_SIZE`) filter and the whole-unit
>   `GetUnitPlayer`/`GetMapPlayers` queries aren't needed yet; near-chat uses the 3×3 view block.
> - Instanced dungeons (`MAP_INDUNTEMP` template → `MAP_INDUN` clone keyed by `(mapID, partyID)`) are
>   deferred; a `(channel, mapId)` grid is created on demand for the base maps.

> **Audit (Phase 3).** The grid math (`Coord`=`(WORD)pos/64`, `KeyOf`=`MAKELONG(X,Z)`, `Split`, the 3×3
> `GetNeighbor` fan-out) and the `OnMove` visibility diff were walked against `TMap.cpp`/`TCell.cpp` and are
> exact: the C# player-level set-diff (old-3×3 vs new-3×3) is provably equivalent to the C++ cell-level
> diff, bidirectional enter/leave and the `exitMap` flag are correct, and the `CellKey` invariant is sound.
> **Fixes from the audit** (all were broadcast-set bugs from routing jump/block through the move helper):
> jump now broadcasts via `GetNeerPlayer` (3×3 ∩ Euclidean ≤ `CELL_SIZE`, **including self** — the C++ echoes
> to the mover); block via `GetNeighbor` (full 3×3, **including self**); move stays self-excluded; the
> first-spawn `bNewMember` flag is now set on the existing-player-sees-newcomer `CS_ENTER_ACK`; and the MOVE
> handler no longer writes `m_wMapID` (the C++ leaves it untouched). Correctly-deferred (need unbuilt
> subsystems): the dungeon `!pNEW` `CloseSession`, and the ghost/dead-move special-casing (the `bGhost` byte
> is parsed but unused). One inert caveat: for world coords ≥ 65536 the `(ushort)` cast may saturate where
> C++ `WORD()` wraps — outside where the C++ index math is itself correct, and real coords are ~thousands.

## ✅ Done (Phase 4 — item & magic template charts)

- [x] **Template charts loaded at startup** — `GameDatabase.LoadTemplatesAsync` reads `TITEMCHART`
  (C++ `CTBLItemChart` → `m_mapTITEM`) and `TITEMMAGICCHART` (`CTBLItemMagicChart` → `m_mapTItemMagic`)
  once into a `TemplateStore` (`TMap.Data`), keyed identically: items by `wItemID`, magic by magic id.
  `MapWorker` loads it best-effort after the DB ping; DB-free ⇒ an empty store.
- [x] **Template link on load** — each persisted item resolves its `ItemTemplate` (C++ `m_pTITEM`) from
  `wItemID` and each magic option its `MagicTemplate` (`m_pMagic`). The C++ **drop-unknown-template** rule
  is applied (an item whose `wItemID` has no chart row is skipped) — but only once the chart is actually
  loaded, so DB-free operation never drops everything.
- [x] **Derived item values (closes the Phase-2 gaps)** — `CTItem::WrapPacketClient` now emits the
  template `RefineMax` (`m_pTITEM->m_bRefineMax`) and the computed `GetMagicValue()` instead of the raw
  per-instance values. `GetMagicValue` is byte-exact: `max( WORD(fRevision · wValue · maxValue) / 100, 1 )`
  with `fRevision = RvType==0 ? 1.0 : ItemTemplate.Revision[RvType-1]`, the `WORD(float)` cast (truncate to
  int32, mask 16 bits) applied **before** the `/100`. Raw fallback when templates are absent (DB-free).

> **Fidelity notes (Phase 4):**
> - Only the columns the client wire needs are loaded from the 50-column `TITEMCHART` (`wItemID`,
>   `bRefineMax`, the 4 revision floats). The rest of the item template — and the linked `tagITEMATTR` base
>   AP/DP chart (`TITEMATTR`, via `wAttrID`) — is deferred to the combat/stat engine.
> - **Skills need no template engine yet** (confirmed against the C++): the `CS_CHARINFO_ACK` learned-skill
>   block needs only (skillId, level, reuseTick), read straight from `TSKILLTABLE`, and the maintained-skill
>   block is empty at enter. `CTBLSkillChart` is required only for skill use/combat/learning — **loaded in
>   Phase 14** (cost/reuse fields) and linked onto each learned skill.

> **Audit (Phase 4).** `GetMagicValue` (formula + cast-before-`/100`), template `RefineMax`, the
> drop-unknown-template rule, the magic-id sort, and all chart column reads (`TITEMCHART`/`TITEMMAGICCHART`)
> were walked against `TItem.cpp`/`DBAccess.h` and are byte-exact. **One bug found + fixed:** the persisted
> magic load only checked `id != 0`, whereas the C++ `CreateItem` requires `id && value && GetItemMagic(id)`
> and de-dups by id (last-wins) — so value-0 slots, unknown-magic ids, and duplicates were inflating the
> serialized magic-count byte and pairs. Now filtered via `Item.AddPersistedMagic` (chart-gated so DB-free
> keeps raw values), with regression tests.

## ✅ Done (Phase 5 — the `CTObjBase` stat/vitals core: primary stats + MaxHP/MaxMP)

- [x] **Stat charts loaded at startup** — `LoadTemplatesAsync` now also reads `TFORMULACHART`
  (C++ `CTBLFormulaChart` → `m_mapTFORMULA`, the `Init`/`RateX`/`RateY` per `FTYPE_*`), `TCLASSCHART`
  (`CTBLClassChart` → `m_mapTCLASS`) and `TRACECHART` (`CTBLRaceChart` → `m_mapTRACE`), caching the
  per-level growth factor `Rate1st` = `FormulaChart[FTYPE_1ST].RateX` (C++ `f1stRateX`).
- [x] **Primary stats fully derived** (`StatEngine`, ported value-exact from `CTObjBase::GetBaseStatValue`/
  `GetSTR…`) — `stat = max(baseMin, BASE_STAT + race + class) · pow(Rate1st, level-1) + Σ equipped-gear
  enchants` (`CalcItemAbility` over the `INVEN_EQUIP` container, using the per-item computed
  `GetMagicValue(MTYPE_*)`). There is **no per-character stat storage** — matching the C++.
- [x] **Computed `GetMaxHP` / `GetMaxMP`** — `FormulaInit(FTYPE_HP) + trunc(GetCON · RateX(FTYPE_HP)) + Σ
  equip MaxHP enchants` (and `FTYPE_MP`/`GetMEN` for MP). Floats accumulate; the truncation to integer is a
  toward-zero cast at the `Calc2ndAbility` boundary, matching the C++.
- [x] **Wired into the already-shipped packets** — `CS_CHARINFO_ACK`, `CS_ENTER_ACK` and the world-plane
  `MW_CHARDATA_ACK` now emit the computed `GetMaxHP()`/`GetMaxMP()` (was synthesized 100), with the
  **current** HP/MP clamped down to max (C++ load-time clamp). Current HP/MP are loaded from
  `TCHARTABLE.dwHP`/`dwMP` (max HP/MP are computed, never stored).

> **Fidelity notes (Phase 5):**
> - Deferred/stubbed to 0 (their subsystems aren't ported): the buff/skill delta (`CalcAbilityValue`), pet
>   bonuses (`CalcPetATTR`/`CalcPetBonus`), the death-penalty stat reduction (`CalcAfterMath`) and the guild
>   `StatLevel` bonus. A naked or geared character's stats/vitals are otherwise value-exact.
> - Max HP/MP are computed **live at serialize** (as the C++ `GetMaxHP()` does), with a DB-free fallback to
>   the synthesized value; equipment changes mid-session (item manipulation) are deferred, so a load-time
>   snapshot would be equivalent for now.
> - **AP/DP and the `CS_CHARSTATINFO` stat packet are deferred to the next phase** — done in Phase 6.

> **Audit (Phase 5).** `GetBaseStatValue`, the effective-stat + equip-enchant sum (with `HasPower`), the
> `MaxHP`/`MaxMP` composition + truncation points, the `ABILITY_MAXHP→MTYPE_MHP` mapping, and the class/
> race/formula chart reads were walked against `TObjBase.cpp`/`DBAccess.h` and are value-exact. **One bug
> found + fixed:** the base-stat growth used single-precision `MathF.Pow` + a float multiply, but C++
> `pow()` computes in **double** and narrows once — for a non-unit `Rate1st` that drifts by 1 ULP and, after
> the toward-zero truncation, could off-by-one a stat/MaxHP; now computed in double. Also **hardened** the
> current-HP/MP load to clamp into `ch.Hp`/`ch.Mp` at load (C++ clamps `m_dwHP` at load; was clamped only on
> the wire copy — output-exact at enter, but a latent bug once regen/damage/save read it).

## ✅ Done (Phase 6 — AP/DP + the `CS_CHARSTATINFO` stat sheet)

- [x] **Item-attribute charts** — `LoadTemplatesAsync` now also reads `TITEMATTRCHART` (C++ `CTBLItemAttrChart`
  → `m_mapTItemAttr`, keyed by `wID`: min/max AP, DP, magic AP/DP, block prob) and `TITEMGRADECHART`
  (`CTBLItemGradeChart` → the per-level grade byte). The item template now also carries `bType`, `wAttrID`
  and `dwSpeedInc`. Each equipped item resolves its `m_pTITEMATTR` at load via **`SetItemAttr`** — key
  `(wAttrID + itemgrade[level].grade + gem)` truncated to 16 bits, with the lowest-id row as the fallback.
- [x] **Per-item AP/DP getters** (`Item.GetMaxAP/GetMinAP/GetMaxLAP/GetMinLAP/GetMaxMagicAP/GetMinMagicAP/
  GetDefendPower/GetMagicDefPower`, C++ `CTItem::*`) — each = the item's enchant magics of the relevant
  `MTYPE_*` + the attr-row base value gated on `m_bType` (`IT_WEAPON`/`IT_LONG`; DP added unless `IT_SHIELD`).
  `HasPower()` skips broken items (0 current durability) from the sums.
- [x] **The full combat sheet in `StatEngine`** (C++ `CTObjBase`) — `Calc2ndAbility` extended to every
  `FTYPE_*` (AP←STR/DEX/INT, level-scaled DP, attack/defend levels, the raw-seed NAS/PCR/MSP cases),
  `CalcItemAbility` split into the enchant-sum and item-getter-sum over the equip container, and the public
  accessors `MaxAp`/`MinAp` (melee + ranged, min clamped to max), `MaxMagicAp`/`MinMagicAp`, `DefendPower`,
  `MagicDefPower`, `AttackLevel`/`DefendLevel`/`MagicAtkLevel`/`MagicDefLevel`, `AtkSpeed`/`AtkSpeedRate`,
  `CriticalPysProb`/`CriticalMagicProb`, `ChargeSpeed`/`ChargeProb`.
- [x] **`CS_CHARSTATINFO_REQ` → `CS_CHARSTATINFO_ACK`** (`Map/MapService.Stats.cs`) — the 31-field / 87-byte
  stat block, every field computed value-exact. Resident targets are answered directly; a non-resident
  (cross-server) target logs a deferral (the C++ forwards to the world).

> **Fidelity notes (Phase 6):**
> - Deferred/stubbed to 0 (unported subsystems), matching Phase 5: the buff/skill delta (`CalcAbilityValue`),
>   pet bonuses (`CalcPetBonus` on the level/crit/charge fields), and the guild `StatLevel` level bonus
>   (`CALCULATE_DAMAGE`/`CALCULATE_ARMOR`, guarded by guild membership). Field 30 (`m_wSkillPoint`) is 0 (not
>   modelled yet, as in `CHARINFO`).
> - Shield block-rate / avoid and the `MW_CHARSTATINFO` **world forward** for non-resident targets are
>   deferred — a self / same-map stat request is byte-exact.

## ✅ Done (Phase 7 — item manipulation: `CS_MOVEITEM`)

- [x] **`CS_MOVEITEM_REQ` → `CS_MOVEITEM_ACK`** (`Map/MapService.Items.cs`, C++ `OnCS_MOVEITEM_REQ`) — the
  5-byte request `[srcInven, srcSlot, dstInven, dstSlot, count]` (container = 1-byte id `0xFF`/`0xFE`/`0xFC`,
  slot = 1 byte) with the single-byte `TMOVEITEM_RESULT` reply.
- [x] **All inventory operations** — move to an empty slot, **split** a stack (`count < stack`), **swap**
  two different items (2× `UPDATEITEM`), **merge** identical stacks up to the template `bStack` cap, and
  **drop/destroy** (dest `INVEN_NULL`: full `DELITEM` or partial decrement). Item equality is the C++
  `CTItem::operator==` (template/level/gem/mogg/glevel/dura/refine/expiry/ext/attr/magic) — it decides
  swap-vs-merge. Emits the matching `CS_UPDATEITEM_ACK`/`CS_ADDITEM_ACK`/`CS_DELITEM_ACK` (item block via
  `WrapPacketClient`).
- [x] **Equip / unequip** — moving to/from `INVEN_EQUIP` forces `count = 1`, canonicalizes the sub-slot to
  the primary slot, and validates via **`CanEquip`** (wrapped→`MI_WRAP`, slot-mask→`MI_CANNOTEQUIP`,
  class-mask→`MI_NOMATCHCLASS`, level→`MI_LOWLEVEL`). On success the stats recompute (live) and the actor's
  full equipped set is broadcast to the whole 3×3 view (incl. self) via **`CS_EQUIP_ACK`** — so neighbours
  see the gear change. New item-template columns loaded for this: `bLevel`/`dwClassID`/`dwSlotID`/
  `bPrmSlotID`/`bSubSlotID`/`bStack`.

> **Fidelity notes (Phase 7):**
> - ~~The **2H multi-item auto-eviction**~~ — **done in Phase 9**: equipping a 2H weapon now evicts the
>   off-hand occupant into the bags via the `CanPush`/`PushTItem` allocator (or `MI_INVENFULL`).
> - The **skill-requirement gate** (`MI_NOSKILL`) is treated as satisfied (skills unported), warrior stance
>   auto-buffs, the deal/store/secure-code guards, and the special-bag `MI_CANTDROP` rule are deferred.
> - ~~`CS_HPMP_ACK` on equip is deferred~~ — **folded in (Phase 6–13 audit):** `ChangeEquipItem` now sends the
>   self `CS_HPMP_ACK` (and its own `CS_MOVEITEM_ACK`) after clamping, matching the C++.
>   The stat sheet (`CS_CHARSTATINFO_ACK`) is sent. **No DB persistence** — the C++ move path saves nothing
>   either; the whole-inventory `DM_SAVEITEM` flush is the (deferred) persistence path.

## ✅ Done (Phase 8 — item use: `CS_ITEMUSE` HP/MP potions)

- [x] **`CS_ITEMUSE_REQ` → `CS_ITEMUSE_ACK`** (`Map/MapService.Items.cs`, C++ `OnCS_ITEMUSE_REQ`) — reads
  `WORD wTempID, BYTE bInven, BYTE bItem, WORD wDelayGroup, BYTE targets` (+ the per-target block), replies
  `BYTE result, WORD delayGroup, BYTE kind, DWORD delay`. Result codes are the `TITEMUSE_RESULT` enum.
- [x] **HP/MP potion effect** — switch on the item template `bKind`: `IK_HP`/`IK_MP` add `wUseValue`,
  `IK_MAXHP`/`IK_MAXMP` restore to full; the result is clamped to `GetMaxHP`/`GetMaxMP` (the Phase-5/6 live
  stat engine). Already-full ⇒ `IU_FULL` and **no consume**; otherwise the new bar is broadcast to the 3×3
  view via **`CS_HPMP_ACK`** (`dwID, OT_PC, maxHP, HP, maxMP, MP` — no level byte) and one is consumed
  (`DELITEM`/`UPDATEITEM`). Two template columns loaded for this: `bKind`, `wUseValue`, `dwDelay`.
- [x] **Guards** — not-found (`IU_NOTFOUND`, incl. wrong template id / dead-proxy 0-HP), wrapped
  (`IU_WRAPPING`), level (`IU_NEEDLEVEL`). The ack's `delay` field echoes the template `dwDelay`.

> **Fidelity notes (Phase 8):** only the HP/MP kinds are implemented; every other `IK_*` (buffs, boxes,
> money, skill/revival, cash, race-change…) plus the server-side cooldown enforcement
> (`m_mapItemCoolTime`/`dwDelay` — the ack still carries `dwDelay` for the client's own timer), the
> targeted-item path, and the deal/store/riding/secure-code/tournament guards are deferred. No DB save.

## ✅ Done (Phase 9 — two-handed weapon auto-eviction: completing the equip system)

- [x] **`CanPush` / `PushTItem` blank-slot allocator** (`Map/MapService.Items.cs`, `Map/Item.cs`,
  `Map/Character.cs`) — the C++ inventory allocator (`CTObjBase::CanPush(vector)` + `CTPlayer::PushTItem` +
  `CTInven::GetBlankPos`/`GetEasePos`), byte-faithful: for each pushed item, ease-pos (fill an existing
  same-stack with room) then blank-pos (lowest free slot), searched `INVEN_DEFAULT` first then the other
  non-equip bags. `PushTItem` emits `CS_UPDATEITEM_ACK` (merge) / `CS_ADDITEM_ACK` (new slot); `CanPush` is
  its non-mutating dry run (→ `MI_INVENFULL`). Backpack capacity is `Inven.SlotCount` (the C++
  `m_pTITEM->m_bSlotCount`; DB-free default 100 — the live server takes it from the bag item template).
- [x] **The two-handed eviction in `OnCS_MOVEITEM_REQ`** (C++ 1988-2037) — "two-handed" is detected purely
  from the item template (`m_bSubSlotID != INVALID_SLOT`), not the kind. Equipping a 2H weapon evicts
  whatever occupies its off-hand slot (`ES_SNDWEAPON`) back into the bags via `PushTItem` (each evicted item:
  `CS_DELITEM_ACK` from the equip inven, then `CS_ADDITEM_ACK`/`CS_UPDATEITEM_ACK` into a bag), gated on
  `CanPush` first — **never auto-dropped** (`MI_INVENFULL` when the bags are full). Also ports the
  equip-from-a->1-stack eviction of the displaced dest item.
- [x] **The unequip-into-occupied-slot normalization** (C++ 1950-1967) — moving an equipped item onto an
  occupied bag slot now swaps the src/dst roles so it runs through the equip/eviction path uniformly (the
  incoming bag item is `CanEquip`-validated and equipped, the equipped item returns to the bag), matching the
  C++ exactly. The `MI_BOTHHANDWEAPON` reverse guard (filling the off-hand while a 2H reserves it) is
  retained, and equipping a 1H/shield over an equipped 2H stays a **straight 1:1 swap** (no eviction).

> **Fidelity notes (Phase 9):** the eviction only ever pushes equipment (non-stackable), so the allocator's
> stack-merge path is exercised only by the general reuse case; the one documented divergence is that
> `CanPush`'s ease-pos considers pre-existing stacks only, not a partial stack created earlier in the *same*
> push (reachable only when a single pushed stack overflows one slot — no equip-eviction produces that). The
> skill-requirement gate (`MI_NOSKILL`), warrior stance auto-buffs, and the deal/store/secure-code guards
> remain deferred (as in Phase 7). No DB save (the C++ move path saves nothing).

## ✅ Done (Phase 10 — the money currency core + durability repair: `CS_DURATIONREP`)

- [x] **Money currency core** (`Map/Character.cs`, C++ `CalcMoney` + `CTPlayer::UseMoney`/`EarnMoney`) — money
  is the three DWORD tiers **gold/silver/copper** combined on base **1000** (`MONEY_MULTIPLY`):
  `total = cooper + silver·1000 + gold·1000²`. `MoneyTotal`/`SetMoneyTotal` are the C++ `CalcMoney`
  combine/split; `UseMoney(cost, commit)` is the two-phase spend (commit=false ⇒ affordability check only,
  a zero cost always passes); `EarnMoney(amount)` adds (false on zero). The tiers are now **loaded from
  `TCHARTABLE.dwGold/dwSilver/dwCooper`** at char-load and were already serialized into `CS_CHARINFO_ACK`;
  `CS_MONEY_ACK` (gold/silver/cooper) is sent on any change. (The C++ `GOLD_TITLE` side effect is deferred —
  titles unported.)
- [x] **`CS_DURATIONREP_REQ` → `CS_DURATIONREP_ACK`** (`Map/MapService.Repair.cs`, C++ `OnCS_DURATIONREP_REQ`)
  — the 8-byte request `[bNeedCost, bType, bInven, bItem, wNpcID, bNpcInven, bNpcItem]`. Modes: **`RPT_NORMAL`**
  (one item — `NOTFOUND`/`DISALLOW` guards), **`RPT_EQUIP`** (all equipped), **`RPT_ALL`** (every repairable
  item across all bags, incl. equipped). Per-item filter `DuraMax && DuraCur < DuraMax && m_bCanRepair`. A
  **`bNeedCost`** request short-circuits to `CS_DURATIONREPCOST_ACK` (the un-discounted quote + rate). Then
  two-phase money: `UseMoney(cost, false)` → `ITEMREPAIR_NEEDMONEY` if short, else commit + `CS_MONEY_ACK`;
  durability restored to full; the ACK carries `result, count, count×{inven, slot, duraMax, duraCur}` in
  **reverse** list order (the C++ pops from the back). The optional **portable-smith** path (`IK_NPCCALL`
  item, only on maps 0/8) is consumed after the money check, before the commit.
- [x] **Repair cost** (`GetRepairCost`, C++ `CTItem::GetRepairCost`) — `max(1, RepairCost[powerLevel] ·
  (DuraMax − DuraCur) · fPrice / DuraMax)`. Loads two new template fields — `fPrice`/`bCanRepair` on the item
  chart and the per-level `dwRepairCost` from the new **`TLEVELCHART`** load (`TemplateStore.RepairCostByLevel`).
  A missing level row ⇒ cost **0 (free)**, matching the C++ `FindTLevel`-null path (so DB-free repair is free).

> **Fidelity notes (Phase 10):** the NPC **discount** (`GetDiscountRate` — NPCs unported ⇒ rate 0, full
> price) and the PC-bang bonus, the **secure-code** guard (unported ⇒ treated unlocked), the per-item
> **`MTYPE_REPCOST`** cost magic, and the enchant-based **weapon/shield power level** (`GetPowerLevel` uses
> the item's attr grade — the C++ fallback) are deferred. No DB save (the C++ repair path saves nothing; the
> money/item persistence is the deferred `DM_*` flush). The `CS_DURATIONREP` handler has no deal/store guard
> in the C++, so none is ported.

## ✅ Done (Phase 11 — field monsters: grid occupancy + visibility)

- [x] **`Monster` model + cell occupancy** (`Map/Monster.cs`, `Map/Cell.cs`) — a focused port of `CTMonster`
  (which extends `CTObjBase`): the instance id (the deterministic composite `(spawnId<<16)|(channel<<8)|slot`,
  **not** a counter), the chart id / level, MaxHP/MaxMP + current HP/MP, position/facing/mode, country/region.
  Cells now carry a **`m_mapMONSTER`** alongside `m_mapPLAYER`; `MapGrid` gained `AddMonster`/`RemoveMonster`
  and the 3×3 monster fan-out, and `MapState` a monster registry (`AddMonster`/`RemoveMonster`/`FindMonster`/
  `MonstersInView`/`PlayersAround`).
- [x] **`CS_ADDMON_ACK` / `CS_DELMON_ACK`** (`Map/MapService.Monsters.cs`, C++ `SendCS_ADDMON_ACK`
  CSSender.cpp:913 / `SendCS_DELMON_ACK` :1011) — monsters use their **own** packets, not the player
  `CS_ENTER_ACK`. The add body is byte-exact: `dwID, wChartID, bLevel, MaxHP, HP, MaxMP, MP, posX/Y/Z, pitch,
  dir, mouseDir, keyDir, action, mode, bNewMember, country, color, region, maintainCount`. There is **no name
  or size on the wire** (the client resolves both from `wChartID`). The del body is `dwMonID, bExitMap`.
- [x] **The visibility exchange** (C++ `CTMap::EnterMAP`/`OnMove` → `CTCell::EnterMonster`/`EnterPlayer`) —
  `SpawnMonster` places a monster and announces it (`bNewMember = TRUE`) to the players in its 3×3 view;
  a player going live (`CS_CONREADY_REQ`) learns of the monsters already in its view (`bNewMember = FALSE`);
  and a player **moving** between cells gets the monster enter/leave diff (`CellDiff.EnteredMonsters` →
  `CS_ADDMON_ACK`, `LeftMonsters` → `CS_DELMON_ACK`) computed alongside the existing player diff.
  `DespawnMonster` sends `CS_DELMON_ACK` to the view and removes it.

> **Fidelity notes (Phase 11):** this is spawn-mechanism + visibility only. **Deferred (Phase 12+):** the DB
> spawn-chart pipeline (`TMONSPAWNCHART`/`TMAPMONCHART`/`TMONSTERCHART`/`TMONATTRCHART` load) and the
> regen/respawn timer with its RNG weighted type-pick + radius-scatter position — so a live server currently
> spawns nothing until that lands (monsters enter via the `SpawnMonster` API; tests seed directly). Also
> deferred: monster **movement/roam** and all **AI** (aggro/host/attack/gohome), **combat/damage/death/loot**,
> the `GetColor` faction byte (a constant 0 is sent, as for the player `bColor`), monster **maintain/buff
> skills** (a fresh monster has none ⇒ a 0 count), and recall-mons/companions/self-objs/NPCs (their own
> `ADD*` packets). Monster movement doesn't broadcast yet, so visibility changes only when a **player** moves
> or a monster spawns/despawns.

## ✅ Done (Phase 12 — the monster spawn pipeline)

- [x] **The four spawn charts** (`TMap.Data/MonsterCharts.cs`, `GameDatabase.LoadTemplatesAsync`) — loaded
  once at startup into `TemplateStore`: `TMONSTERCHART` → `MonsterTemplates` (id/level/attr-key subset),
  `TMONATTRCHART` → `MonAttrs` (level-scaled MaxHP/MaxMP, keyed by `MAKELONG(attrId, level)`),
  `TMONSPAWNCHART` → spawn points, and `TMAPMONCHART` → each spawn's weighted monster-type table
  (`MonsterSpawnDef` = spawn + types). DB-free ⇒ empty (no monsters).
- [x] **Spawn build at bring-up** (`Map/MapService.Spawns.cs`, C++ SE_DEFAULT loop → `CTMap::AddMonSpawn` →
  `InitMonster`) — `InitMonsterSpawns` creates a spawn point per **`SE_DEFAULT`** spawn × channel, each with
  its `Count` monster slots (composite ids `(spawnId<<16)|(channel<<8)|slot`), the first regen scheduled at
  the spawn `Delay`. Called from `MapWorker` after the charts load.
- [x] **The regen tick** (`RunMonsterRegen`, C++ `CTAICmdRegen::ExecAI`) — driven by the 1-second
  `OnTimerAsync` (map clock in ms). Each empty, due slot rolls the spawn probability (`rand()%100 < bProb`);
  on success it **weighted-picks** a non-essential monster type by `bProb`, resolves its template + level-attr
  (a missing attr aborts, as the C++ does), **scatters a position** within the spawn radius
  (`len=rand()%Range`, `rad=(rand()%360)·π/180`, or the anchor when `Range=0`), sets HP/MP to max, and makes
  it visible via the Phase-11 `SpawnMonster`. On a failed roll it reschedules one delay later.

> **Fidelity notes (Phase 12):** the `TSVRCHART` multi-machine server/unit topology filter on the spawn load
> is dropped (single-server ⇒ all rows loaded, bucketed by (channel, map)). Deferred: the **leader-cluster**
> and **group-order** spawn branches (need live AI / `OS_WAKEUP`), **essential** monsters (the C++ immediate
> path — the weighted pick excludes them, so they don't spawn yet), ~~linked-spawn kill (`m_wRegenDelSpawn`)~~
> (**done in Phase 34** — the quest-Regen dynamic-spawn link) and
> the occupation-zone country override (`m_wLocalID`), and the monster's skill list (combat). ~~Because
> death/combat is deferred a filled slot never empties~~ — **Phase 13 wired death→respawn**: killing a
> monster frees its slot and reschedules regen after `Delay`, so the full spawn↔death cycle now runs. The
> RNG is .NET `System.Random` (not C `rand()`) — the selection/scatter **logic** is exact, the
> concrete values are not (and monster positions were never wire-comparable to a C++ instance regardless).

## ✅ Done (Phase 13 — the combat spine: physical hit → damage → death → respawn)

- [x] **`CS_DEFEND_REQ` → damage application** (`Map/MapService.Combat.cs`, C++ `OnCS_DEFEND_REQ`
  CSHandler.cpp:1438 → `CTObjBase::Defend` → `CalcDamage` → `OnDamage`) — a player's reported hit on a field
  monster. The 33-field request is read; the server **re-derives** the attacker's physical AP
  (`StatEngine.MinAp`/`MaxAp`) rather than trusting the client. **Damage (value-exact, `HT_NORMAL`):**
  `dp = monster DP`, `a = max((int)(apMin−dp), 5)`, `b = max((int)(apMax−dp), 7)`,
  `dmg = a + rand()%max(b−a, 1)`, clamped to remaining HP (the DWORD→int underflow-then-floor is reproduced).
  Monster DP is loaded from `TMONATTRCHART.wDP` (added to `MonAttrRow`/`Monster`).
- [x] **The hit broadcast** — `CS_DEFEND_ACK` (byte-exact; the `bHit` wire field carries the crit-prob and
  `bAtkHit` carries the hit result — `HT_LASTHIT` on the killing blow — with a trailing damage map keyed by
  `MTYPE_DAMAGE`) and `CS_HPMP_ACK` (the monster's new HP bar) to the players who can see the monster.
- [x] **Death → respawn** (C++ `OnDie` → `CTAICmdLeave`/`CTAICmdRegen`) — at 0 HP: `CS_DIE_ACK`, then the
  corpse is removed (`CS_DELMON_ACK` via `DespawnMonster`), and the spawn slot is **re-armed** (the monster
  id decodes to its `(spawnId, channel, slot)`; slot freed, respawn scheduled one `Delay` later) — so the
  Phase-12 regen tick brings it back. **This closes the spawn↔death loop.**

> **Fidelity notes (Phase 13):** a PLAYER's basic PHYSICAL melee hit on a field MONSTER only. Deferred:
> `CS_SKILLUSE_REQ` (the announce/cost/aggro/power-broadcast half — the client sends it first, but the
> server re-derives power here regardless) — **now ported in Phase 14**; the skill-data damage scaling (a basic attack is
> the raw AP−DP roll, `CalcAbilityValue` buff mods are identity); **crit/miss/accuracy** (`GetAtkHitType`) +
> **magic damage** — **now ported in Phase 28**; long/ranged damage; the shield-block roll (**now ported in
> Phase 41** — live for a PC defender; a monster defender here has no shield); PvP;
> loot/exp/drop award; and HP/MP regen (`Recover`). The hit broadcasts to the monster's 3×3 view (not the
> tighter C++ `GetNeerPlayer` distance set). RNG is .NET `System.Random` (`CombatRng`, seedable) — the
> AP−DP roll logic is exact, the value is not. No DB save.

> **Audit (Phases 6–13).** Four parallel readers walked the C# against the C++ file-by-file (CS_CHARSTATINFO
> + the stat sheet; CS_MOVEITEM + the CanPush/PushTItem allocator + 2H eviction/normalization; CS_ITEMUSE +
> money + CS_DURATIONREP; and the monster visibility/spawn/combat packets & formulas). Every wire layout,
> every formula, every DB column order, the RNG order, and the constant ids matched **except 7 defects, now
> fixed (with regression tests):**
> 1. `Calc2ndAbility` FTYPE_PDP/FTYPE_MDP used single-precision `MathF.Pow` where C++ uses `pow` (double) —
>    an off-by-one on physical/magic **defence** for non-unit per-level rates (now `Math.Pow`, as `BaseStat`).
> 2. `CTItem::GetEquipLevel` — the ELD reduction must apply **only when `DefaultLevel > ELD`** (C++
>    TItem.cpp:633); the port always subtracted, so an ELD ≥ the requirement wrongly let any level equip.
> 3. `CS_DEFEND_ACK.bPerform` sent 0; C++ sends `(bPerform==PERFORM_SUCCESS(0)?TRUE:FALSE)` ⇒ **1** on a hit.
> 4. `CS_DEFEND_ACK` damage-map value not `WORD`-truncated (C++ stores `(WORD)dmg` then widens) — masked to 16 bits.
> 5-7. `CS_DEFEND_ACK` `dwHostID`/`wAttackLevel`/`bAttackCountry`/`bAttackAidCountry` echoed the client; C++
>    keeps the client `dwHostID` for a PC attacker but **re-derives** the attack level (`GetAttackLevel`) and
>    country from the attacker (CSHandler.cpp:1563-1587) — now re-derived server-side.
>
> **Folded in after the audit (with regression tests):** (a) `ChangeEquipItem` now matches the C++ order and
> emits its own `CS_MOVEITEM_ACK(MI_SUCCESS)` + the self `CS_HPMP_ACK`, so an equip/unequip yields the two
> success acks the C++ sends; (b) the partial-drop path routes through `UseItemByTemplate` — the C++
> `UseItem(wItemID, count)` that consumes `count` of the template across all bags in id/slot order (first-match
> stack), matching the `count != stack` branch; (c) loaded `wDelayGroupID` + `bConsumable` onto the item chart
> and added the CS_ITEMUSE **delay-group anti-tamper guard** (chart-gated) and the **`m_bConsumable`
> consume-gate**. **One deviation kept intentionally:** CS_ITEMUSE uses a stricter template check
> (`slot.TemplateId==tempId`) instead of the C++ global-chart lookup — identical for legit clients and safer
> against a malformed `wTempItem`.

---

## ✅ Done (Phase 14 — `CS_SKILLUSE`: the attack-announce half + caster cost + cooldown)

The packet the client sends **first** when swinging a skill/attack (the Phase-13 `CS_DEFEND` is the per-target
damage half that follows). C++ `OnCS_SKILLUSE_REQ` (CSHandler.cpp:2429).

- [x] **The skill chart** (`TSKILLCHART` → `SkillTemplate`, C++ `CTBLSkillChart`→`CTSkillTemp`) — loaded once
  at startup (`TMap.Data/SkillCharts.cs`, `GameDatabase.SkillChartSql`), keyed by `wID`, and **linked** onto each
  learned skill at char load (`Skill.Template`, C++ `CTSkill::m_pTSKILL`). The DB `bLevel` column maps to
  `StartLevel` and every template is stamped with the global `f1stRateX = TFORMULACHART[FTYPE_1ST].fRateX`
  (= `TemplateStore.Rate1st`), exactly as the C++ loader does (TMapSvr.cpp:2596-2655).
- [x] **Caster MP/HP cost (value-exact)** — `Skill.GetRequiredMp/Hp` (C++ TSkill.cpp:185-217): type 1 = flat
  `m_dwUseMP·pow(f1stRateX, StartLevel+(lvl-1)·NextLevel)/100`, type 2 = `PureMax·m_dwUseMP/100`, type 0 = free.
  Cost is checked against **`GetPureMaxMP`/`GetPureMaxHP`** (base formula only — added to `StatEngine`) with the
  C++ comparators: MP **`<`** ⇒ `SKILL_NEEDMP`, HP **`<=`** ⇒ `SKILL_NEEDHP` (can't self-kill). Deducted from
  the caster after all guards pass (`if(dwHP||dwMP)`), then broadcast via `CS_HPMP_ACK`.
- [x] **Reuse cooldown (value-exact)** — `Skill.CanUse`/`GetReuseRemainTick`/`GetReuseDelay`/`Use` (C++
  TSkill.cpp:70-114) + the group-cooldown `SkillUse` (C++ TObjBase.cpp:4568 — `SDELAY_SKILL` for the skill,
  `SDELAY_KIND` for every same-`m_bKind` skill when `m_dwKindDelay`); delay = `(base+atkSpeed)·rate/100` off the
  Phase-6 `GetAtkSpeed`/`GetAtkSpeedRate`. Cooldown is enforced against a new map ms clock (`MapService.NowMs`,
  the C++ module `m_dwTick`); a recast still cooling ⇒ `SKILL_SPEEDYUSE`.
- [x] **The broadcast** — `CS_SKILLUSE_ACK` (byte-exact, `Map/MapService.Skills.cs`; `wBackSkill` sits right
  after `wSkillID` — the header comment is stale) carrying the caster's attack-power payload
  (`wAttackLevel`, phys/magic min-max AP, `bCP`, level, country) + the echoed target list, sent to the near
  players (`GetNeerPlayer` ≈ `NearView`, includes the caster). The follow-up `CS_DEFEND` reads this payload back.

> **Historical fidelity notes (Phase 14; superseded by later phases and the current status above):**
> OT_PC casters only (OT_MON/OT_RECALL/OT_SELF summon/recall objects unported ⇒
> silent return). Guards ported in the C++ order (skill-known → MP → HP → cooldown); **deferred guards** each
> gate an unported subsystem: toggleable-recast, wrong-region, peace-zone, tournament/arena, premium-skill
> medals, `CheckAttack` (stun/hold buffs), `CheckPrevAct`, and `UseSkillItem` (weapon/consumable). The power
> payload uses the **physical-melee** path — `GetAttackType()`/`IsLongAttack()` (derived from the skill-data
> rows `m_vData`) are deferred, so every skill is treated as `SAT_PHYSIC` short: `wAttackLevel =
> GetAttackLevel()`, phys AP from the melee set, `bCP = CriticalPysProb`; magic AP is still populated.
> `wBackSkill` (weapon-durability `DurationDec`), `wTransHP/MP` (SCT_HPTRANS/MPTRANS transform-cost),
> `bEquipSpecial` and aggro are 0/deferred; target validation is deferred (requested targets echoed verbatim).
> No DB save.

---

## ✅ Done (Phase 15 — skill-data damage scaling: `TSKILLDATA`/`m_vData`)

- [x] **The skill-effect chart** (`TSKILLDATA` → `SkillTemplate.Data`, C++ `CTSkillTemp::m_vData`) — loaded
  per skill (`GameDatabase.SkillDataChartSql`, bulk-bucketed by `wSkillID`; `TMap.Data/SkillCharts.cs`
  `SkillDataRow`).
- [x] **The value engine (value-exact)** — `DataValue` (C++ `GetValue`, `m_bCalc` 0 flat / 1 level-linear /
  2 `pow`-scaled double-÷100 / 3 int-decrease), `Calculate` (the `SVI_*` operators, returning the delta with
  the DWORD-wrap arithmetic), and `CalcValue` (Σ over matching `(bType, bExec)` rows). `GetAttackType`
  (SATT_* → SAT_PHYSIC/MAGIC) and `IsLongAttack` (MTYPE_LAP / SATT_LONG).
- [x] **Wired into `CS_DEFEND`** (`MapService.Combat.cs`): the attacking skill's `MTYPE_DAMAGE` row scales the
  AP−DP roll (`ScaleDamage` = the live instance-skill term of `CalcAbilityValue`); `IsLongAttack` picks the
  melee/long AP set; `GetAttackType` picks the physical/magic attack level. A basic attack (no skill / no
  damage row) leaves the raw roll unchanged — Phase-13 behavior preserved.
- [x] **Folded into `CS_SKILLUSE`** — `GetAttackType`/`IsLongAttack` now drive the power payload
  (`wAttackLevel` physical-vs-magic, the long AP set, `bCP` phys-vs-magic crit), closing Phase 14's
  "always physical-melee" deferral.

> **Deferred (documented):** ~~the magic-damage branch~~ and ~~crit (non-`HT_NORMAL`) damage~~ — **both ported
> in Phase 28** (magic AP vs monster `wMDP`; the `FTYPE_PCD`/`MCD` crit formulas + `GetAtkHitType` miss/crit
> roll). `MTYPE_MDAMAGE` (MP drain) / direct `MTYPE_HP`/`MP` execs + the multi-entry `mapDamage`
> find-by-attr/insert-by-exec quirk are **now ported in Phase 42**. Still deferred: the maintain/remain-buff +
> cure + `ApplyEffectionBuff` amplification layers of `CalcAbilityValue`, and `DistributeSkill` (pet share).

## ✅ Done (Phase 16 — HP/MP regen: `Recover`)

- [x] **`RunRecover`** (`MapService.Recover.cs`, C++ `CTObjBase::Recover` TObjBase.cpp:1322 /
  `CTMonster::Recover` TMonster.cpp:1920), driven off the 1-second tick. Each resource regenerates once every
  `RECOVER_TIME` (3000ms) while below max; a change broadcasts `CS_HPMP_ACK` to the 3×3 view.
- [x] **Player** — HP only while `MT_NORMAL`, MP always; flat `GetHPR`/`GetMPR` (added to `StatEngine`:
  `FTYPE_HPR`←CON / `FTYPE_MPR`←MEN formula + `MTYPE_HPR`/`MPR` enchants). **Monster** — HP while not
  `MT_BATTLE`, MP always; amount = 25% of max (`maxHP/4`).
- [x] **Battle mode + reset-on-hit** (`Character.EnterBattle`/`Monster.EnterBattle`, C++ `ChgMode(MT_BATTLE)`):
  attacking (`CS_DEFEND`/offensive `CS_SKILLUSE`) or being hit enters battle on the transition, pushing both
  recover anchors to `now + RECOVER_INIT` (5000ms) and refreshing `LastAtkTick`; a player leaves battle 5s
  after the last hit (re-enabling HP regen next tick).

> **Deferred/approximated (documented):** the buff layer (`CalcAbilityValue`) and `HaveStopRecover` are
> 0/false (buffs unported); the monster battle→normal transition (AI-driven gohome in C++) is approximated by
> the same 5s no-hit timeout until Phase-18+ AI refines it; world-entry anchor init is omitted.

## ✅ Done (Phase 17 — loot/exp on monster death)

- [x] **Ownership** (`Monster.AddDamage`, C++ `SetAggro`/`SetKeeper`) — per-attacker cumulative damage; the
  first to cross 10% of MaxHP (`MONKEEP_PER`) becomes the `OWNER_PRIVATE` keeper.
- [x] **EXP (value-exact)** — `MapService.Loot.cs`: `GetExp` = `wExp · MonExpRate(monLevel)/100` (non-Korea
  curve `64 − min(39,lvl)·9/39`), `· GetLevelRate` (`1 − min(max(pl−ml,0),10)·0.1`), ceil'd (`+0.99`). `GainExp`
  adds exp, levels up across the `TLEVELCHART.dwEXP` thresholds (full heal + `bSkillPoint`), and broadcasts
  `CS_LEVEL_ACK` + `CS_HPMP_ACK` + `CS_EXP_ACK`.
- [x] **Money loot via a corpse** — on death the monster rolls `m_dwMoney` (`bMoneyProb%`, `[min,max)`) onto
  itself and persists as a lootable corpse (not attackable, no regen) until taken or it expires; a loot-less
  kill despawns immediately (Phase-13 behavior). `CS_MONITEMLIST_REQ` reports the corpse money;
  `CS_MONMONEYTAKE_REQ` credits the keeper (`EarnMoney` + `CS_MONEY_ACK`).
- [x] **Anti-farm** — 0 exp at ≥10 levels above the monster; no drops at ≥25.

> **Deferred (documented):** the **item** drop table is **Phase 22** (magic/rare option generation still
> deferred); **party exp split & free-for-all loot are Phase 38** (money level-split, the exotic loot modes, and
> the cross-map `MW_*` relay for remote party members still deferred), quests (`CheckQuest`), the
> vital/soul/premium/exp-buff/pet-collection bonuses (→ 0),
> and `SendCS_CHARSTATINFO` on level-up. A zero-exp kill skips the C++ 0-exp `CS_EXP_ACK`.

## ✅ Done (Phase 18 — monster idle roam)

- [x] **`RunMonsterAI`** (`MapService.AI.cs`, C++ `CTAICmdRoam::ExecAI` TAICmdRoam.cpp:30) — on a per-monster
  roam timer, an alive, idle (`MT_NORMAL`) monster that ≥1 player can see picks a destination on its
  spawn-radius circle (`rad = rand()%360`, C++ `MoveNext` in-bounds branch) and broadcasts `CS_MONACTION_ACK`
  (byte-exact: `dwMonID, bAction=TA_WALK, fPos, dwTargetID=0, bTargetType=0`) to the viewers. A monster no one
  sees stays dormant (C++ roam requires a host → else `AT_LEAVE`).

> **Faithful-to-wire, with documented approximations:** the roam parameters that live in charts / the
> AI-command table not loaded here (`m_bArea`, `m_bRoamType`, `m_bRoamProb`, the per-command roam delay) are
> approximated by the spawn `Range` (radius), a fixed `TA_WALK`, and a constant interval (`base + rand()%(4·base)`,
> the C++ jitter shape). The server keeps the monster's authoritative position at the anchor: the
> **client-authoritative** position echo (`CS_MONMOVE_REQ` → `CS_MONMOVE_ACK`) that advances `m_fPos` is
> deferred, so the client animates the wander while the grid cell (visibility) is computed from the anchor.
> **Deferred (slice 2):** the whole BATTLE branch — aggro accrual, host lifecycle (`SetHost`/`ChkHost`/
> `m_dwHostKEY`), the `ChgMode` state machine, chase (`CTAICmdFollow`) + leash, the monster attack
> (`CS_MONATTACK_ACK` + monster→player `CS_DEFEND`), `MT_GOHOME`, and pack/leader/patrol movement.

## ✅ Done (Phase 19 — monster aggro + chase)

- [x] **Aggro on hit** (`MapService.Combat.cs`) — a `CS_DEFEND` hit sets the monster's `TargetId` to the
  attacker and enters `MT_BATTLE` (via the Phase-16 `EnterBattle`). (**Superseded by Phase 44:** the target is
  now the highest-cumulative-aggro attacker via the real `m_mapAggro` table, not the last hitter.)
- [x] **Chase** (`MapService.AI.cs` `ChaseTarget`, C++ `CTAICmdChgMode`/`CTAICmdFollow`) — a battle monster
  re-broadcasts `CS_MONACTION_ACK` with `TA_FOLLOW` toward the target's live position (`dwTargetID` +
  `bTargetType = OT_PC`) to its viewers, on a chase cadence.
- [x] **Leash / aggro-loss** (`DropAggro`) — when the target is gone (offline) or has fled past the leash
  (distance from the spawn anchor > `ChaseRange`), the monster drops aggro → `MT_NORMAL`, which re-enables its
  HP regen (Phase 16) and returns it to roaming. This is the AI-managed battle exit that **replaces the
  Phase-16 5s-timeout stand-in**.

> **Approximations (documented):** the per-monster chase-range (`m_wChaseRange`) and chase delay aren't in the
> loaded charts — a constant leash + interval stand in. Aggro is last-attacker (not the C++ highest-cumulative
> `m_mapAggro`). The monster's authoritative position stays at the anchor (client-authoritative move echo
> deferred), so the leash tightens as the target flees. **Deferred (next slice):** the actual
> **monster-attacks-player** damage — `CTAICmdBeginAtk`/`CTAICmdAttack` → `CS_MONATTACK_ACK` + the monster→player
> `CS_DEFEND` (needs the monster attack-power attr columns `wAP`/`wMinWAP`/`wMaxWAP` — present in `TMONATTRCHART`
> but not yet loaded — and **player death**/revival, unported); plus `MT_GOHOME`, pack/leader assist-aggro, and
> the host lifecycle (`SetHost`/`ChkHost`/`m_dwHostKEY`).

## ✅ Done (Phase 20 — monster-attacks-player)

- [x] **Monster attack power** — loaded `wAP`/`wMinWAP`/`wMaxWAP`/`dwAtkSpeed` from `TMONATTRCHART` onto
  `MonAttrRow`/`Monster`; the physical AP band is `AtkMin = wAP + wMinWAP`, `AtkMax = wAP + wMaxWAP` (C++
  `CTMonster::GetMinAP`/`GetMaxAP`, TMonster.cpp:1600).
- [x] **The melee hit** (`MapService.AI.cs` `AttackPlayer`, C++ `CTAICmdAttack`) — when a battle monster's
  target is within melee range of the anchor, on the attack cadence (`m_dwAtkSpeed`) it rolls `CalcDamage`
  (the monster AP band vs the player's `GetDefendPower`, same AP−DP floors), reduces the player's HP, enters
  the player into battle (suppressing HP regen), and broadcasts `CS_MONATTACK_ACK` (announce) +
  `CS_DEFEND_ACK` (the hit, monster-attacker layout) + `CS_HPMP_ACK` (the player's bar) to the viewers.
- [x] **Player death** — at 0 HP the player dies (`CS_DIE_ACK`) and the monster drops aggro (→ `MT_NORMAL`).

> **Deviations (documented):** the monster damage is applied **server-side** (the C++ announces
> `CS_MONATTACK_ACK` and the host client reports the hit via `CS_DEFEND_REQ`; the client-authoritative echo is
> deferred, so the server rolls + applies it directly — consistent with the Phase-18/19 move shim). Melee
> range + the default attack cadence are constants (the C++ skill `m_wMaxRange` / attack-speed detail isn't
> fully wired). **Player revival is Phase 21** (`CS_REVIVAL`); the death penalty / dead-state input-gating
> are still not enforced. Crit/miss, magic/ranged monster attacks, and monster skills are deferred.

## ✅ Done (Phase 21 — player revival)

- [x] **`OnCS_REVIVAL_REQ`** (`MapService.Revival.cs`, C++ CSHandler.cpp:1067 → `CTPlayer::Revival`
  TPlayer.cpp:3610) — a dead player (Phase 20) sends its revival point + `REVIVAL_TYPE`; the server
  repositions it there (reusing the movement `RelocateAndExchangeView`: grid re-bucket + `CS_ENTER/LEAVE`
  diff), brings it back to `MT_NORMAL`, and restores HP/MP.
- [x] **Restore by type (value-exact)** — `REVIVAL_NPC` (town, C++ `AFTERMATH_ATONCE`) = 30%, `REVIVAL_GHOST`
  (in-place) = 40% of max (`DWORD` truncation), HP clamped to ≥ 1 (C++ `if(!m_dwHP) m_dwHP = 1`). Broadcasts
  `CS_REVIVAL_ACK` (charId + pos) + `CS_HPMP_ACK` (restored bars) to the 3×3 view; HP regen (Phase 16) resumes.

> **Deferred (documented):** the death penalty (`SetAftermath` — the aftermath stat reduction is a buff
> subsystem, already stubbed to 0 in the stat sheet), `RespawnCompanion` (pets), the `TREVIVAL_SKILL`
> revival-protection buff (`ForceMaintain`), the `REVIVAL_HELP` priest-resurrection ask flow
> (`CS_REVIVALASK`/`CS_REVIVALREPLY`), and BoW/BR respawn placement. Added a `Hp == 0` guard (the C++ handler
> has none) — a live client never sends this, and it prevents a stray request from teleporting/resetting a
> live player.

## ✅ Done (Phase 22 — item drop-loot)

- [x] **Drop table** — `TMONITEMCHART` → `MonsterTemplate.DropRows` (bulk-loaded, bucketed by `wMonID`) +
  `bItemProb`/`bDropCount` on the monster chart; `MaxWeight` = Σ row weights, threaded onto the `Monster` at
  spawn.
- [x] **The roll** (`MapService.Loot.cs` `RollLoot`, C++ `CTMonster::AddItem` TMonster.cpp:1080) — gated on
  `MaxWeight > 0` (**no drop table ⇒ no loot at all, money included** — the C++ early-return); then, per the
  `bDropCount` attempts, an `m_bItemProb` gate → the blank corpse slot → a weighted pick (`TRand(MaxWeight)`,
  first cumulative) → the four "normal" probability gates → a chart-type fixed item created into the corpse
  inventory (`LinkItemAttr` for its attr row).
- [x] **List + take** — `CS_MONITEMLIST_ACK` now serializes the corpse items (`WrapPacketClient`, count
  prefix); `OnCS_MONITEMTAKE_REQ` → `MonItemTake` (TMapSvr.cpp:9089, solo path): `CanPush` → `PushTItem` into
  the taker's bags + `EraseItem` from the corpse + `CS_GETITEM_ACK`, else `MIT_FULLINVEN`; then the list update
  + `CS_MONITEMTAKE_ACK`.

> **Deferred (documented):** magic/rare option rolls (`MakeSpecialItem` / `bItemMagicOpt`/`bItemRareOpt` — a
> dropped item is a base item, no durability/glevel/enchants set), ranged picks (`MonChoiceItem`, `wItemID` 0
> + min/max + country/level filter), pre-built magic items (`bChartType` 0), party loot modes
> (`PT_LOTTERY`/`CHIEF`/`HUNTER`/`ORDER`) + item owner-tags/routing, the deal guard, and the cross-map relay
> (`SendMW_ADDITEM_ACK`). The take honors the free-slot allocator rather than the client's exact target slot.

---

## ✅ Done (Phase 23 — NPC shops: talk / buy / sell)

- [x] **NPC registry** — `TNPCCHART` → `TemplateStore.Npcs` (`NpcDef`) + the per-NPC shop stock from
  `TNPCITEMCHART` (bulk-loaded like the C++ `CTBLNpcItemAll`, bucketed by `wNpcID`); `InitNpcs`
  (`MapService.Npc.cs`) builds the runtime `Npc` objects at bring-up (resolving item ids → templates for the
  `TNPC_ITEM`/`TNPC_PVPOINT` shop types) into the `MapState._npcs` registry (C++ module `m_mapTNpc` →
  `FindTNpc`). NPCs are static server-side fixtures — the client already knows their positions from its own map
  files, so **nothing is broadcast**; the server only validates by id.
- [x] **Talk** (`OnCS_NPCTALK_REQ`, CSHandler.cpp:3506) — `FindNpc` → `CTNpc::CanTalk` country gating
  (own/allied country + disguise match; a neutral `TCONTRY_N` NPC serves everyone — ported value-exact) →
  `CS_NPCTALK_ACK(questId, npcId)`. `CheckQuest` deferred ⇒ `questId` is always 0 (plain talk).
- [x] **Buy** (`OnCS_ITEMBUY_REQ`, CSHandler.cpp:6247) — `FindNpc` + `CanTalk`, resolve the item from the NPC's
  stock (`GetItem`), clamp to the template stack, charge gold (`GetItemPrice` = `dwMoney[grade]·fPrice`, ceil
  via `+0.99`, two-phase `UseMoney` check→commit around `CanPush`/`PushTItem`), then `CS_ITEMBUY_ACK`
  (SUCCESS/NOTFOUND/NEEDMONEY/CANTPUSH + the three money tiers). The portable-NPC-call block (map 0/8 +
  `IK_NPCCALL` consume) is ported (mirrors repair).
- [x] **Quest-object pickups** — nonzero quest IDs resolve only a runnable `Routing` quest's `QTT_ITEMID`
  from the item chart, bound to its trigger NPC and the player's map. The object needs no shop stock.
  Ordinary stock purchases still pay gold/PvP points. Unknown quest IDs cannot bypass payment.
  `QuestPickupTests` covers collection → objective update → turn-in/reward, repeats, prerequisites,
  NPC/map/item/quest mismatches and inventory-capacity retry. The deployed-DLL probe for mission 8003
  (`Byliny pro Karla`, pickup 8005, NPC 26193, item 9656) is in `Gameforge/quest-herbs/`.
- [x] **Sell** (`OnCS_ITEMSELL_REQ`, CSHandler.cpp:6623) — the `INVEN_EQUIP` guard, `FindTItem` + count checks,
  the `m_bIsSell & ITEMTRADE_SELL` sellable gate → `CTItem::GetPrice()/4 · bCount` (`GetSellUnitPrice`,
  grade = attr-grade fallback or default level; 0 without a resolved attr — the C++ guard) → `EarnMoney` →
  remove (`CS_DELITEM_ACK`) or decrement (`CS_UPDATEITEM_ACK`) → `CS_ITEMSELL_ACK` (+ money tiers).
- [x] **Data** — `ItemTemplate.IsSell` (`m_bIsSell`), `TemplateStore.LevelMoney` (`TLEVELCHART.dwMoney`), the
  two NPC SQL loads, and `Npc.CanTalk` (`TCONTRY_*`/`SDT_TRANS_DISGUISE_*` value-exact). 12 tests
  (`NpcShopTests.cs`): neutral-talk ACK, hostile-country no-ack, buy-charges/adds, need-money, item-not-stocked,
  unknown-npc, unknown-quest rejection, sell full/partial, not-sellable, equipped-ignored, `InitNpcs` stock resolution.

> **Historical Phase-23 deferrals:** the quest engine was added in later phases; quest-object item
> resolution is now implemented and validated as described above. The NPC **discount**
> (`GetDiscountRate` — occupation/guild/hero data unported ⇒ rate 0), **`TNPC_PVPOINT`** PvP-point-currency
> shops (`GetItemPvPrice`/`UsePvPoint`) + **BoW-mode** pricing (a gold NPC is assumed), the sell **price-up
> buff** (`SDT_STATUS_PRICEUP`), the secure-code / player-store / deal(trade) / tournament guards, the item
> count/quest UDP logging, and the other NPC types (skill-master/rent, make/upgrade/refine, portal/return,
> warehouse, auction/arena, cash/magic-item shops). No DB save (the C++ buy/sell path saves nothing back).

---

## ✅ Done (Phase 24 — the quest engine core: accept → objective → turn-in → reward)

- [x] **Data model** — `QuestTemplate` (C++ `tagQUESTTEMP`: type/trigger/parent/count-max/condition-mode +
  `QuestCondition`/`QuestTerm`/`QuestReward` lists) in `TMap.Data`; `TemplateStore.Quests` + `Quest(id)`
  (`FindQuestTemplate`). The runtime state is `QuestProgress` (`m_mapQUEST` entry: `RunningTerm` counters +
  trigger/complete counts + timer + `Save`), on `Character.Quests`/`LevelQuest` with `FindQuest`/`IsRunningQuest`.
- [x] **Trigger index** (`InitQuests`, C++ `m_mapTRIGGER`) — every quest indexed by (trigger type → trigger id);
  child quests land under `TT_EXECQUEST` keyed by their parent id (how the base `ExecQuest` recurses).
- [x] **The engine** (`Map\MapService.Quest.cs`) — `CheckQuest` (module: advance running terms + fire eligible
  triggered quests), the player term-advance + `CS_QUESTUPDATE_ACK`, `CanRunQuest` + `CheckQuestCondition` +
  `CheckLevelCondition` (level / parent / prereq-quest (have/after/before) / class / item / position / same-level
  / country / sex / maintain-skill conditions + the AND/OR `m_bConditionCheck` mode; unmodelled types fail
  safe), `CheckComplete`/`CheckTermStatus` (GETITEM count / HUNT·TALK·USEITEM counters / TIMER / position /
  QUESTCOMPLETED), `GetTermCount`, `DropQuest`.
- [x] **Subtypes** (the C++ `CQuest` class hierarchy → a switch on `QUESTTEMP.m_bType`): **NpcTalk** (offer via
  `CS_NPCTALK_ACK`), **Mission**/**Guild** (`ExecMission`: register + `CS_QUESTADD_ACK` + trigger-count + prime
  terms + hand over fetch items + `CS_QUESTSTARTTIMER_ACK` + recurse children), **Complete** (`ExecComplete`:
  find the COMPQUEST term → `CheckComplete` → `OnQuestComplete` → `CS_QUESTCOMPLETE_ACK` → complete-count +
  reset + fire `TT_COMPLETE`/children, else `QR_TERM`), **GiveItem** (grant the ITEMID item / run the referenced
  quest), **DefTalk** (no-op), base (recurse children).
- [x] **Reward grant** (`OnQuestComplete`) — item rewards (RM_SELECT/PROB/RANDOM/DEFAULT take-method + class gate
  + `CanPush`→`QR_INVENTORYFULL` + `PushTItem` + `CheckQuest(GETITEM)`), **gold** (`EarnMoney`+`CS_MONEY_ACK`),
  **exp** (`GainExp`), then consumes the fetch (`QTT_GETITEM`) items from the bags (`CS_DELITEM`/`CS_UPDATEITEM`).
- [x] **Handlers** — `OnCS_QUESTEXEC_REQ` (accept/run, with the `TT_TALKNPC` same-map guard), `OnCS_QUESTDROP_REQ`
  (`QR_DROP`), `OnCS_QUESTLIST_POSSIBLE_REQ` (per-NPC runnable-quest list + the min-level filter). Wired
  `CheckQuest` into **NPC-talk** (un-stubbed — the player term-advance echoes the objective quest id), **buy**
  (`TT_GETITEM`), **sell** (get-item refresh), and **monster death** (`TT_KILLMON`, keyed by monster chart id).
  11 tests (`QuestTests.cs`): accept, level-gate, get-item-on-buy advance, complete+reward+consume, unmet-term,
  drop, kill-hunt advance, talk-objective+echo, fetch-item-on-accept, possible-list, already-done no-op.

> **Deferred (documented):** the other **15 `CQuest` subtypes** (`GiveSkill`/`DropItem`/`SpawnMon`/`Teleport`/
> `Routing`/`DropQuest`/`ChapterMsg`/`Switch`/`DieMon`/`DefendSkill`/`DeleteItem`/`SendPost`/`Craft`/`Regen`);
> the exotic **term/condition types** (switch state, monster-instance `QTT_SPAWNID_DEL` proximity hunts, arena);
> **RT_MAGICITEM / RT_SKILL / RT_SKILLUP / RT_TITLE / RT_SOUL / RT_POINT** rewards (skill-learn + magic-item
> build unported); the enter-time **running/complete quest lists** (`CS_QUESTLIST`/`CS_QUESTLIST_COMPLETE`);
> quest **persistence** is **done in Phase 25** (the `Save` dirty flag drives `TSaveQuest`/`TSaveQuestTerm`);
> still deferred is the **DB quest-template load** (the quest-table schema is unverified — quests
> are injected into `TemplateStore.Quests`; the client-side `TQuest.mpq`/`TQNode.qpd` node graph is a client asset).

---

## ✅ Done (Phase 25 — persistence: the char record + quest progress written back to SQL)

- [x] **Save procs** (`TMap.Data\GameDatabase.cs`, via the positional `SqlProc.ExecAsync` helper) — `SaveCharAsync`
  (`TSaveChar`: one `UPDATE TCHARTABLE` by `dwCharID`, the 29-input param set in exact `DBAccess.h` order) +
  `SaveQuestsAsync` (`TSaveQuest` per dirty quest + `TSaveQuestTerm` per running term, upsert keyed on
  `(charId, questId)` / `(+termId)`). Proc bodies live in the `.bak` baseline; a missing proc (SQL error 2812)
  is tolerated silently, matching the sibling ports.
- [x] **Full-row round-trip** — the char load (`CharSql`/`CharLoadRow`) was extended from the 20-column Phase-1
  subset to the full `CTBLChar` set (`dwEXP`, `wSkillPoint`, `bGuildLeave`/`dwGuildLeaveTime`, `wSpawnID`/
  `wLastSpawnID`/`dwLastDestination`/`wTemptedMon`/`bAftermath`, `bStatLevel`/`bStatPoint`/`dwStatExp`) so
  `TSaveChar` (which rewrites all 28 value columns) writes real values back, not zeros. The columns the map
  doesn't model live on `Character.Persist` (`CharPersistExtras`); the two pc-bang columns are 0 (unported).
  Exp + skill-point are now actually loaded (were synthesized 0 before).
- [x] **Gating** — `Character.DbLoaded` (set only on a real `TCHARTABLE` row) + `ClientSession.IsMain` gate the
  save; a synthesized DB-free char is never written. `Character.LastSaveMs` drives the 30-min throttle
  (`IsSaveDue`).
- [x] **Snapshots + off-thread write** (`Map\MapService.Persist.cs`) — `BuildCharSave`/`BuildQuestSaves` snapshot
  on the batch thread (clearing the quest `Save` dirty flag, C++ `m_bSave` reset) and the SQL runs off-thread
  (`FlushSaveAsync`, fire-and-forget with logged errors) so the map never blocks on the DB — replacing the C++
  dedicated DB job queue.
- [x] **Triggers** (C++ parity) — periodic **30-min** per-char save (`CHAR_SAVE_TICK`) in `OnTimerAsync`
  (`RunPeriodicSaves`); **save-on-disconnect/logout** in `OnClientDisconnect` (C++ `SetEventCloseSession`);
  **shutdown flush** (`SaveAllCharDataAsync`) after the batch loop drains in `MapWorker` (C++ `SaveAllCharData`).
  8 tests (`PersistTests.cs`): the `IsSaveDue` gate/throttle matrix, the `BuildCharSave` snapshot (mutable +
  round-trip columns + pc-bang 0), and `BuildQuestSaves` (collects only dirty quests + clears the flag).

> **Follow-up (now shipped in Phase 26):** the **inventory/item save** (`TSaveInven`/`TSaveItem`, bracketed by
> **`TSaveItemDataStart`/`End`** — the delete-then-reinsert rewrite that promotes staging → live `TITEMTABLE`,
> with the per-item `dlID` PK). This closes the money↔item relog desync. See the Phase-26 section below,
> including the live-DB round-trip verification that corrected the bracket choice. Still deferred: the skill /
> maintain-skill / hotkey / cabinet / companion / pet / recall-mon / PvP-record / duel / medal / secure-code
> saves + the incremental single-item fast-path (`TSaveItemDirect`).

---

## ✅ Done (Phase 26 — inventory persistence: items saved with the char record)

- [x] **The rewrite** (`GameDatabase.SaveInventoryAsync`) — the C++ char-item save (`SSHandler.cpp
  OnDM_SAVEITEM_REQ`), the delete-then-reinsert bracketed by `TSaveItemDataStart(charId)` → per-container
  `TSaveInven` (charId, invenId, wItemID, dEndTime, bELD) → per-item `TSaveItem` (the full 35-value set in exact
  `DBAccess.h` param order) → `TSaveItemDataEnd(charId)`, all on one connection. Start clears the char's
  **staging** tables (`TTEMPINVENTABLE`/`TTEMPITEMTABLE`); Start/End's inner transactions see the staged rows;
  End atomically promotes staging → live `TINVENTABLE` + `TITEMTABLE` (filtered `bOwnerType=0 AND bStorageType=0`
  = the `TOWNER_CHAR`/`STORAGE_INVEN` we write). Missing procs (2812) tolerated. `__time64_t` expiries convert to
  SQL `smalldatetime` (`FromTime64`, inverse of the load's `ToTime64`) — **0 ⇒ `1900-01-01`** (the C++
  `__TIMETODB` sentinel, read back as 0 by `__DBTOTIME`'s `year<2000`), **never NULL** (the `dEndTime` columns,
  incl. staging, are `NOT NULL`).
  > ⚠️ **Live-DB-verified correction:** the initial cut used the `TSaveCharData*` bracket — but in this baseline
  > *that* bracket's item promotion (`INSERT TITEMTABLE SELECT … TTEMPITEMTABLE`) is **commented out**, so items
  > staged but never reached live. Reading the live proc bodies exposed it; the C++ (`SSHandler.cpp:7655`) uses
  > `CSPSaveItemData*`. Both fixes (bracket + the NULL→1900 sentinel, which `NOT NULL` staging would have
  > rejected outright) were confirmed by a rolled-back round-trip (see the verification note below).
- [x] **The `dlID` PK** — the item load now selects `dlID` (`FullItemSql`/`FullItemRow` → `Item.DlId`), so an
  existing item keeps its row id across the delete-then-reinsert. An in-session item (`DlId == 0`) is stamped
  from the per-server id counter — `GenItemId` (`++`, C++ `m_dlGenItemID`) seeded at bring-up by
  `InitGenItemIdAsync` → `TInitGenItemID(@out, serverId)` (C++ `CSPInitGenItemID`). `Inven.Eld` (`bELD`) is loaded
  + saved too.
- [x] **Safety gate** — the inventory rewrite runs only once the id seed is ready (`_itemIdReady`); an unseeded
  counter would mint `dlID`s that collide with existing `TITEMTABLE` PKs, so when the seed proc is unavailable
  the inventory is left untouched (the DB keeps its last item state) rather than doing a destructive/colliding
  rewrite. Char + quest save still run.
- [x] **Wired into the save flow** — `BuildInvenSaves` snapshots containers + items on the batch thread (stamping
  new `dlID`s, packing the magic set into the fixed 6 (`bMagic`,`wValue`) slots) and `FlushSaveAsync` now also
  calls `SaveInventoryAsync`, so char + quest + inventory persist together on the same triggers (30-min /
  disconnect / shutdown). **The money↔inventory relog desync from Phase 25 is resolved.**
  4 tests (`PersistTests.cs`): container/item snapshot, new-item `dlID` stamping (+ write-back), magic-slot
  packing, `GenItemId` increment.

- [x] **Live-DB round-trip verification (done)** — against the running `araz-mssql` container (`TGame_gsp`):
  1. **Signatures** — all 8 save procs (`TSaveChar`/`TSaveInven`/`TSaveItem`/`TSaveQuest`/`TSaveQuestTerm`/
     `TSaveItemDataStart`/`End`/`TInitGenItemID`) exist; their live parameter count, order, and types match the
     C# positional bindings 1:1 (`TSaveItem`'s live `@dwTime1..6` are the `Ext[0..5]` slots — same positions).
  2. **Round-trip** — in a `BEGIN TRAN … ROLLBACK` (no persistence): `TSaveItemData*` + `TSaveInven`/`TSaveItem`
     promoted a synthetic item into live `TITEMTABLE` with exact values (count/dura/magic/value/`1900-01-01`
     endtime); `TSaveChar` (29 params) updated gold/level/exp/hp/mp/pos on a real char; `TSaveQuest`/`Term`
     upserted into `TQUESTTABLE`/`TQUESTTERMTABLE`. Every change reverted on rollback — no live data touched.
  This is what surfaced the two bugs above.

> **Follow-up (now shipped in Phase 27):** the incremental single-item fast-path (`TSaveItemDirect`).
> **Cabinet items (`STORAGE_CABINET`) + the `TSaveCabinet` open-state are modelled in Phase 37** (via the same
> incremental item path, cabinet-storage-stamped). Still deferred: the `TSaveSkill`/`TSaveSkillMaintain`/
> `TSaveHotkey`/companion/pet/recall-mon/PvP/duel/medal/secure-code saves.

---

## ✅ Done (Phase 27 — incremental item persistence: the `TSaveItemDirect` fast-path)

The Phase-26 full inventory rewrite runs on the 30-min timer / disconnect / shutdown, so a **hard crash** could
lose up to 30 min of item changes. Phase 27 adds a per-tick incremental save so each change is persisted almost
immediately — and every item-mutating handler already ported (move/split/merge/drop, equip/unequip, use/consume,
buy, sell, loot-take, quest give/consume, repair) is covered because they all funnel through three senders.

- [x] **The choke-point hook** — `SendCS_ADDITEM_ACK`/`SendCS_UPDATEITEM_ACK` enqueue the item for an upsert;
  `SendCS_DELITEM_ACK` (now taking the removed `Item`, so it has the `dlID`) enqueues a delete. The wire packets
  are unchanged. Repair restores durability via its bespoke `CS_DURATIONREP_ACK` (not `UPDATEITEM`), so each
  mended item is enqueued explicitly.
- [x] **The queues** (`MapService.Persist.cs`) — `_pendingItemUpserts` (keyed by `dlID`, last-write-wins so
  repeated changes to one item collapse to one write) + `_pendingItemDeletes`. A pending upsert and delete of the
  same row are mutually exclusive (each op cancels the other), so **pick-up-then-drop in one tick nets to a
  delete and never resurrects an item**.
- [x] **The gate** (`CanPersistItems`) — a main, DB-loaded char with the item-id base seeded (`_itemIdReady`);
  an in-session item (`dlID == 0`) is stamped from `GenItemId` so its row has a stable PK. **Same seed gate as
  the full save** — an unseeded counter never mints colliding `dlID`s. Independent of `_gameDb` so the enqueue
  logic is unit-testable DB-free.
- [x] **The drain** (`FlushItemDirect`, in `OnTimerAsync`) — snapshots + clears the queues on the batch thread,
  then fires one `TSaveItemDirect` (upsert straight to live `TITEMTABLE`, delete-by-`dlID`-then-insert) per
  changed item and one `DELETE … WHERE dlID` per removed row, **off-thread** (fire-and-forget, logged). No-op
  DB-free. The **periodic full save (Phase 26) stays the authoritative reconciler** and supersedes any in-flight
  direct op.
- [x] **DB layer** (`GameDatabase`) — `SaveItemDirectAsync` (the shared `ItemArgs` 35-value set, `TSaveItem`
  and `TSaveItemDirect` are param-identical) + `DeleteItemDirectAsync` (a single-row parameterized delete by PK
  — exactly what `TSaveItemDirect` does internally before re-inserting; never a shared/mass delete). Missing
  proc (2812) tolerated.
- [x] **Live-DB round-trip verified** — a rolled-back smoke test proved `TSaveItemDirect` **inserts** a new row,
  **upserts** the same `dlID` idempotently (one row, values updated 3→9 count / 88→50 dura), and the delete
  removes it by `dlID`; nothing persisted.
- 12 tests (`ItemDirectTests.cs`): dlID stamping/preservation, upsert/delete queueing, the save↔delete
  mutual-exclusion both orders, last-write-wins collapse, the three gates (unseeded / not-main / synth char), the
  DB-free drain, and a drop through the real move handler.

> **Deviation (documented):** the C++ fires `TSaveItemDirect` only for server-side grants (auction/guild) and
> relies on the periodic full save for client-driven item changes; this port additionally uses it as an
> incremental crash-safety layer for the ported client mutations. A moved item gets a fresh `dlID` (the port
> clones stacks on move — a pre-existing property the full save shares); functionally correct, only the PK
> churns. Direct op vs. concurrent full save is a benign last-write-wins race that the next full save reconciles.

---

## ✅ Done (Phase 28 — combat quality: crit / miss / accuracy + the magic-damage branch)

Phase 13 always dealt a `HT_NORMAL` physical hit. Phase 28 ports `CTObjBase::GetAtkHitType` (TObjBase.cpp:2266)
and the magic branch of `CalcDamage`, so a swing now misses, lands, or crits, and a magic skill is resolved
against magic power/defence.

- [x] **Hit-type resolution** (`MapService.HitTypeVsMonster` / `HitTypeVsPlayer`, static + pure) — against a
  **monster** defender: a level-scaled accuracy roll `dwAtk = min(Init, max(RateY − pow(RateX, defLvl−atkLvl)·
  monDefLevel / attackerAL, 0)·100)` from `FTYPE_PAR`/`FTYPE_MAR`, clamped ≥ 20 (the monster's `GetAvoidProb` = 0
  — no item enchants), then a crit roll on the attacker's crit rate. Against a **player** defender (monster→PC):
  the swing always connects unless the attacker's attack level is 0/1, then the crit roll. `bCR == 0xFF` ⇒ miss.
- [x] **Crit damage** (`MapService.CritDamage`) — `base + (uint)(base·(RateX + rand%max(Init,1))/100)` from
  `FTYPE_PCD` (physical, off the **max** band `b`) or `FTYPE_MCD` (magic, off the **min** band `a`) — float math,
  one toward-zero truncation, matching C++ `CalcDamage` TObjBase.cpp:398/563. Live chart: `Init 21, RateX 20`
  ⇒ +20–40% of base.
- [x] **Magic branch** — when the skill's `GetAttackType()` is `SAT_MAGIC`, the damage uses the attacker's
  **magic** AP band (`StatEngine.MinMagicAp`/`MaxMagicAp`) vs the monster's **magic DP** (`wMDP`), with the
  magic crit rate (`CriticalMagicProb`) and magic attack level (`MagicAtkLevel`); physical otherwise. The
  `CS_DEFEND_ACK` routes the power band to the magic or physical fields accordingly.
- [x] **A miss** deals 0, leaves HP untouched, and carries an **empty damage map** (`bAtkHit = HT_MISS`) — but
  still enters battle + aggros (the swing happened). A landed hit is scaled by the skill-data row (Phase 15) then
  clamped to HP; `bAtkHit` = `HT_NORMAL`/`HT_CRITICAL`, overridden to `HT_LASTHIT` on the kill.
- [x] **Monster combat stats loaded** — `TMONATTRCHART.wMDP`/`wDL`/`wMDL` (as the defender) + `bCriticalPP`/`wAL`
  (as the attacker) added to `MonAttrRow`/`Monster` and threaded at spawn. Both attack directions (PC→monster
  and monster→PC) now resolve crit/miss.
- 13 tests (`CombatQualityTests.cs`): the always-hit/never-crit/full-crit seed-independent cases, the
  accuracy-floor (~20%) and crit-split (~30%) probability samples, the PC-defender attack-level guard, and the
  crit-damage formula (exact + the +20–40% band).

> **Shield block** (`GetShieldDP`/`GetShieldMDP`) is **now ported in Phase 41** and wired live into this
> monster→player path (the PC defender's equipped shield can block + reduce the incoming hit + flag `HT_BLOCK`).
> **Still deferred (documented):** the skill **hit-test / premium / guild / boss-special** early-outs of `GetAtkHitType` (their skill flags —
> `m_bHitTest`/`m_bHitInit`/`m_bHitInc`, the premium/guild skill lists, `m_bIsSpecial` — aren't loaded, so the
> level-based accuracy path is always taken); long/ranged as a distinct branch (folded into physical via
> `IsLongAttack`'s AP set); PvP; the buff/pet damage layers (`CalcAbilityValue`/`DistributeSkill`).

---

## ✅ Done (Phase 29 — rest of the quest engine: DB template load + 5 more subtypes)

Phase 24 built the quest engine but it ran on **test-injected** templates only — a live server had **zero
quests**. Phase 29 loads the templates from the DB and adds five more subtypes.

- [x] **DB quest-template load** (`GameDatabase.LoadTemplatesAsync`, C++ `LoadQuestTemp` TMapSvr.cpp:3496-4897) —
  the 4-table bulk load (`TQUESTCHART` + `TQCONDITIONCHART` + `TQREWARDCHART` + `TQUESTTERMCHART`) assembled by
  `dwQuestID` into `TemplateStore.Quests`. **Live-DB verified** against `TGame_gsp`: all 4 SELECTs resolve, and
  the data is real — **6261 quest templates, 5712 conditions, 4322 rewards, 10687 terms**; a spot-check (quest 9)
  assembled coherently (a country + level-8-12 gated 3-item collect quest rewarding exp + gold). The load-bearing
  `ORDER BY dwParentID, bMain DESC` is preserved as the `Quests` dict insertion order, which `InitQuests` indexes
  — so `ExecChildren` attempts the main branch first, matching the C++ trigger-vector order.
- [x] **5 more subtypes** (`MapService.Quest.cs` `ExecQuest` + new `Exec*`, each 1:1 with its `Quest*.cpp`):
  **DeleteItem** (remove every QTT_ITEMID stack), **DropQuest** (abandon each term's quest + `QR_DROP`),
  **ChapterMsg** (`CS_CHAPTERMSG_ACK`), **Routing** (`CS_NPCITEMLIST_ACK` — the trigger NPC's item list),
  **Teleport** (same-map reposition via the Phase-21 grid re-exchange; recurse children). New senders
  `CS_CHAPTERMSG_ACK` (`CS_MAP+0xE0`) / `CS_NPCITEMLIST_ACK` (`CS_MAP+0x83`, `TNPC_BOX`).
- 6 tests (`QuestSubtypeTests.cs`): each subtype driven through the real `CS_QUESTEXEC` handler, plus the
  cross-map-teleport no-reposition case.

> **Deferred (documented) — each blocked on an unported subsystem or ambiguous self-state, discovered while
> mapping the C++ `Quest*.cpp`:**
> - **Switch** — `CTPlayer::ChangeSwitch` needs the **map switch/gate subsystem** (`CTMap::FindSwitch`, TSWITCH
>   lock/duration/opened state, TGATE) — not just a char flag. QCT_SWITCH condition likewise. Reclassified from
>   the plan's "portable" (the classification was caveated on `ChangeSwitch` existing).
> - **SpawnMon / Regen** — need **time-limited / dynamic monster spawn** (`AddTimelimitedMon`,
>   `RegenDynamicMonster`, `DelMonSpawn`) — a spawn-infra follow-up.
> - **DieMon / DropItem** — force-kill a spawn's monsters / attach quest loot to a monster; both touch the
>   death/loot flow with per-instance side effects.
> - **Craft** — self-completion via the quest's *own* running terms (subtle running-vs-runnable gate).
> - **GiveSkill** (skill learning `UpdateSkill`), **DefendSkill** (active buffs `ForceMaintain`), **SendPost**
>   (mail `SendDM_QUESTSENDPOST_REQ`).
> - Cross-map teleport (a different `wMapID`); RT_MAGICITEM/SKILL/SKILLUP/CHGCLASS/TITLE/SOUL/POINT rewards;
>   `CS_QUESTLIST_COMPLETE_ACK` (the C++ never dispatches it). *(The enter-time `CS_QUESTLIST` + quest-progress
>   load-on-enter shipped in Phase 30.)*

---

## ✅ Done (Phase 30 — quest persistence load-on-enter + the enter-time quest list)

Phase 25 **saved** quest progress (`TSaveQuest`/`TSaveQuestTerm`) but nothing reloaded it — accepted quests
vanished on relog. Phase 30 closes that round-trip and shows the quest log on login.

- [x] **Quest-progress load-on-enter** (`GameDatabase.LoadQuestsAsync` + `MapService.LoadQuestProgress`, C++
  `CTBLQuestTable`/`CTBLQuestTermTable` DBAccess.h:2410-2468, `WHERE dwCharID=?`) — reads `TQUESTTABLE`
  (dwQuestID, dwTick, bCompleteCount, bTriggerCount) + `TQUESTTERMTABLE` (dwQuestID, dwTermID, bTermType, bCount)
  and rebuilds `m_mapQUEST`: each quest's trigger/complete counts, its running-term counters, the remaining
  timer (`dwTick` → `TimerTick` + `BeginTick = now`, resuming with that many ms left), and the level-quest
  index. A quest whose template is no longer in the chart is skipped. Called in the enter char-data load
  (`TryLoadInventoryAsync`), best-effort, gated on a DB-loaded char. **Live-DB verified**: both SELECTs resolve;
  char 2's 2 saved quests (both complete 1 / trigger 1) rebuild correctly as *completed* (not running), the term
  table empty (completed quests carry no live counters).
- [x] **Enter-time `CS_QUESTLIST_ACK`** (`CS_MAP+0x51`) — sent right after `CS_CHARINFO_ACK` in the
  `MW_CHARINFO` enter step (C++ `SendCS_QUESTLIST_ACK`, SSHandler.cpp:2019): the in-progress quests
  (`CompleteCount < TriggerCount`), each with `bType`/`bCountMax`/`bTermCount` then per template term
  `{ dwTermID, bTermType, bNeedCount, bCurrentCount, bStatus }`. Byte layout follows the C++ **code** (which
  emits `bCountMax` — the header comment omits it). A non-creating running-term lookup (the read must not spawn
  empty counters). `CS_QUESTLIST_COMPLETE_ACK` is not ported (the C++ has no caller).
- 4 tests (`QuestLoadTests.cs`): the rebuild (running quest + term counters), unknown-template skip, timer
  restore, and the enter QUESTLIST packet.

> **Byte-audit (Phases 28–30).** Three parallel adversarial readers walked the C# against the C++ field-by-field
> / value-by-value. **Phase 29 clean.** Fixed **7 defects**: (28) `CS_DEFEND_ACK` `bHit` now carries the attacker
> crit-prob (`bCP`) not 0, `bPerform` is 0 on a miss (`PERFORM_MISS`) not always 1, `bSkillLevel` uses the server
> skill level not the client's, the damage map is empty when final damage is 0 (C++ `if(dwValue)`); the monster's
> `GetDefendPower`/`GetMagicDefPower` now include the `wWDP` weapon-DP term (`m_wDP+m_wWDP` / `m_wMDP+m_wWDP`,
> TMonster.cpp:1642/1736 — it was silently skewing player→monster damage high). (30) quest load now drops a
> `QT_NONE` (type-0) template (C++ `if(pQuestTemp && m_bType)`), re-flags a timer quest dirty (`m_bSave`) so the
> decaying timer re-persists, and sends `SendQuestTimer` (`CS_QUESTSTARTTIMER_ACK`) on enter to restore the
> countdown UI; QUESTLIST ordered by quest id + LevelQuest keep-first for parity. Each fix verified against the
> cited C++ line before applying; 4 regression tests added (`MonsterAttackTests`, `QuestLoadTests`). Documented
> deviation kept: the monster→PC `CS_DEFEND_ACK` uses `dwHostID=monId`/`bHostType=OT_MON` (server-applied damage;
> the C++ host-client-reports model is deferred).

---

## ✅ Done (Phase 31 — the maintained-skill (buff/debuff) engine)

`CalcAbilityValue` was stubbed to 0 across the whole stat sheet — combat, the vitals and three deferred
quest/skill subtypes all ran without buffs. Phase 31 ports the C++ `CTObjBase` buff lifecycle value-exact.
The maintained skill is the port's existing `MaintainSkill` (one C++ `CTSkill` in `m_vMaintainSkill`); its
`SA_BUFF`/`SDT_ABILITY` rows modify the owner's stats **on demand** (there is no per-tick HoT — a MaxHP buff
surfaces through the existing regen path).

- [x] **The stat layer** (`StatEngine.CalcAbilityValue` + `Buffed`, C++ `CTObjBase::CalcAbilityValue`
  TObjBase.cpp:1409) — the **third and final layer of every stat getter** (`base → items → buffs`), keyed on
  `MTYPE_*`: `Stat` (STR…MEN — the C++ `GetSTR…` add-the-return-delta form), MaxHp/MaxMp, HpRecover/MpRecover,
  Max/MinAp (+ arrow), Magic Ap, DefendPower, MagicDefPower, the four level getters, crit/charge. Sums each
  active buff's `SA_BUFF` `SDT_ABILITY` rows via the existing `SkillTemplate.Calculate` and 0-clamps. A buff
  with no linked chart template (DB-free) contributes nothing — so all 287 prior tests stayed green.
- [x] **Data** — `SkillTemplate` gained `dwDuration`/`dwDurationInc`/`bMaintainType`/`bPriority`/`bStatic`
  (+ `IsMaintainType`/`GetMaintainTick`/`CalcAbilityValue`/`HaveSkillData`/`SharedBuffAbility`);
  `MaintainSkill` gained the timing state (`StartTick`/`MaintainTick`/`ChargeTick`, wrap-tolerant tick-ms), the
  `Template` link, and `IsEnd`/`GetRemainTick`/`SetEndTick`/`SetLoopEndTick` (C++ TSkill.cpp:100/133/140/233).
- [x] **Apply** (`MapService.Buff.cs`, C++ `MaintainSkill`→`UpdateBuffSkill`→`PushMaintainSkill`) — three
  entries: a player's positive buff-type `CS_DEFEND` on self/an ally (`ApplyPlayerMaintain`), a debuff landed on
  the target monster by an offensive buff-type `CS_DEFEND` (`ApplyMaintainToMonster`, alongside the damage; the
  `CS_DEFEND_ACK` now carries `bIsMaintain`/`dwMaintainTick`), and the `ForceMaintain` direct grant. `UpdateBuffSkill`
  resolves stacking: a debuff silently replaces its same-id entry; a buff contends with buffs sharing an
  `SA_BUFF` `SDT_ABILITY` row, resolved by priority → flat value → self-cast ownership (loser erased).
- [x] **Expiry + removal** — `RunMaintainSkills` (the per-second `CheckMaintainSkill` sweep in `OnTimerAsync`,
  before `Recover`, C++ order) drops `IsEnd` buffs; `EraseMaintainSkill` broadcasts the new **`CS_SKILLEND_ACK`**
  (`{dwObjID, bObjType, wSkillID}`, `CS_MAP+0x37`) and re-clamps the owner's vitals. `OnCS_SKILLEND_REQ`
  (`CS_MAP+0x36`) lets the client end a buff. `ReleaseMaintain` drops all non-static buffs on death (player +
  monster). Buffs survive relog: the DB-loaded maintains (`CTBLSkillMaintain`) are template-linked and their
  saved remaining-tick is rebuilt into the full duration from the login tick (C++ SSHandler.cpp:4955); the
  `CS_ADDMON_ACK` maintain block now serializes a monster's real debuffs, and every wire block reports the live
  `GetRemainTick`.
- [x] **Quest DefendSkill** (`ExecDefendSkill`, C++ QuestDefendSkill.cpp) — grants each `QTT_SKILLID` term's
  skill as a level-1 self-buff via `ForceMaintain`, then recurses children. Unblocks the Phase-29-deferred
  subtype. **Gate fix found during the port:** the C++ `if(!CanRunQuest(...))` fires when the quest **is**
  runnable (`CanRunQuest` returns `QCT_NONE`=0); the first draft had the gate inverted — corrected to match the
  standard subtype gate (`!= QCT_NONE ⇒ return`).
- 16 tests (`BuffEngineTests.cs`): CalcAbilityValue sum + per-level scale, buff-raises-MaxHp, IsMaintainType
  gate, ForceMaintain grant + broadcast, DEFEND self-buff + monster debuff, same-id refresh, priority
  replace/reject, tick expiry → SKILLEND, permanent buff, SKILLEND_REQ, death drops-non-static/keeps-static,
  quest DefendSkill grant.

> **Fidelity notes / deferred (documented):** the **remain/passive** layer (`m_vRemainSkill`,
> `SA_CONTINUE`/`SA_PASSIVE`), **cure/dispel** (`PerformSkill` `SDT_CURE` + `DeletePositive`/`NegativeMaintainSkill`),
> the **`ApplyEffectionBuff`** percent amplifier (`SDT_STATUS_MAGIC`) and `CalcCure`, the **AutoExp** buff, the
> **loop/channeled** skills (`CS_LOOPSKILL`), the **action-triggered erasers** (`EraseBuffByAttack`/`Defend`/`Ride`),
> **monster self-buff** (`Transformation`), the **server-authoritative `FINISHSKILL`** path, the
> **Posture/ORadius/Trans/non-ability** branches of `UpdateBuffSkill` (their chart fields aren't loaded — flat-value
> buffs are byte-exact), the **atk-speed-rate** buff (a multiplicative-rate composition), and the **DB save-back**
> of maintains (load + reconstruct only — `TSaveSkillMaintain` belongs to the deferred char-data-save family).
> PvP debuffs on another PC are deferred (only positive buffs on a friendly PC apply via the self/ally `CS_DEFEND`).

---

## ✅ Done (Phase 32 — the map switch/gate subsystem)

Unblocks the Phase-29-deferred quest **Switch** subtype (and gated content generally). Switches and gates are
static per-(channel, map) view objects — the C++ `TSWITCH`/`TGATE` graph — placed in the spatial grid and
synchronized to clients; a gate is a **visual door** (it does not block movement server-side — the C++ handles
door collision client-side).

- [x] **Charts + build** — `TSWITCHCHART` (`SwitchDef`: id/pos/`bStart`/`bLockOnOpen`/`bLockOnClose`/`dwDuration`)
  and `TGATECHART` (`GateDef`: gateId/switchId/`bType`/pos) load into `TemplateStore`. `InitSwitches`
  (`MapService.Switch.cs`, called in `MapWorker` after `InitNpcs`) builds a runtime `MapSwitch`/`MapGate` per
  channel × map, wiring each gate to every switch its chart rows reference (multi-row → multi-switch); the gate's
  initial state is seeded from its switch. Placed in the grid by position (`MapGrid.AddSwitch`/`AddGate` + the
  per-map id registries + `Cell.Switches`/`Gates`), so they add/del on enter/move exactly like monsters.
- [x] **Player toggle** (`ChangeSwitchPlayer`, C++ `CTPlayer::ChangeSwitch` TPlayer.cpp:3838) — `CS_SWITCHCHANGE_REQ`
  (`CS_MAP+0xEF`, `{dwSwitchID}`, gated on in-game + main) toggles the switch subject to the lock flags
  (`LockOnOpen`/`LockOnClose`) and the `dwDuration` re-flip cooldown, fires the **`TT_RUNSWITCH`** quest trigger,
  broadcasts **`CS_SWITCHCHANGE_ACK`** (`CS_MAP+0xF0`, `{bResult, dwSwitchID, bOpened}` — the `bResult`-first layout
  the header comment omits) to the switch's 3×3, then drives each linked gate (`GT_MULTISWITCH` flips only when all
  its switches match the new state), **toggling** each + firing **`TT_RUNGATE`** + broadcasting **`CS_GATECHANGE_ACK`**
  (`CS_MAP+0xEC`).
- [x] **Module toggle + auto-revert** (`ChangeSwitchModule`, C++ `CTMapSvrModule::ChangeSwitch` TMapSvr.cpp:7774) —
  the `SWC_TOGGLE`/`OPEN`/`CLOSE` path, ignores locks/duration and **assigns** (not toggles) the switch state to its
  gates. The `dwDuration` auto-revert is a local one-shot queue (`_switchReverts`) swept in `OnTimerAsync`
  (`RunSwitchReverts`), collapsing the C++ AI-server round-trip (`SM_SWITCHSTART_REQ`→`SM_SWITCHCHANGE_REQ`).
- [x] **Visibility** — `CS_SWITCHADD_ACK`/`CS_GATEADD_ACK` (`+0xED`/`+0xEA`, `{id, bOpened}`) sent on enter
  (`CS_CONREADY_REQ`, switches then gates) + on the move cell-diff (via `CellDiff.EnteredSwitches`/`Gates`);
  `CS_SWITCHDEL_ACK`/`CS_GATEDEL_ACK` (`+0xEE`/`+0xEB`) on leaving view.
- [x] **Quests** — `QCT_SWITCH` (20) condition eval (id = switch id, count = expected open state; passes iff the
  switch on the player's map instance matches) wired into `CheckQuestCondition`; the **Switch** subtype
  (`ExecSwitch`, C++ QuestSwitch.cpp) flips each `QTT_SWITCH` (15) term's switch via `ChangeSwitchPlayer`, then
  recurses children (runnable gate, matching the DefendSkill fix).
- 11 tests (`SwitchGateTests.cs`): build + gate-link, add-on-enter, toggle+broadcast, lock-open/lock-close block,
  duration cooldown + auto-revert, one-switch gate follows, multi-switch AND, quest Switch flip, QCT_SWITCH gate.

> **Correctness fix found during the port:** the duration-cooldown gate `NowMs − StartTime < Duration` wrongly
> blocked a switch's *first* toggle, because the port's map clock starts at 0 and a never-toggled switch has
> `StartTime == 0` (the C++ passes only because its tick is large at load). Guarded `StartTime != 0` so a
> never-toggled switch has no active cooldown.

> **Fidelity notes / deferred (documented):** the `GT_SELECTSWITCH` random-switch pick (a template-clone nuance
> for **instanced dungeons**, which the port doesn't clone — the base build links all switches); the **battle-zone
> `m_bGateOpened`/gatekeeper-boss** gating (a separate siege subsystem — the switch machinery is here, the zone
> layer is not); the `SM_*` AI-plane round-trip (collapsed into the local tick). Switch/gate state is per-(channel,
> map) runtime, rebuilt from the chart on load and never persisted — matching the C++.

---

## ✅ Done (Phase 33 — quest-driven monster spawn: SpawnMon / DieMon / DropItem)

Builds a time-limited-spawn engine on top of the Phase-12 spawn model and wires three more Phase-29-deferred
quest subtypes. Key C++ fact: **`RT_TIMELIMIT` ≡ `RT_ETERNAL`** — the auto-expire kick is commented out in the
C++ (`TMap.cpp:601`), so a quest spawn persists + regens like a normal one; its lifetime is realized by an
explicit despawn or by death.

- [x] **Time-limited spawn engine** (`MapService.Spawns.cs`) — `AddTimelimitedMon(spawnId, channel, regenType,
  now)` (C++ `CTMap::AddTimelimitedMon`) instantiates a live spawn from any chart template (`SpawnById`) and
  fills its slots immediately (shared `TryFillSlot`, extracted from the regen tick), with the C++ de-dup guard
  (refuse if a live non-empty spawn for that (id, channel) already exists). `DelMonSpawn(spawnId, channel)` (C++
  `CTMap::DelMonSpawn`) hard-removes the live spawn + its monsters (`CS_DELMON_ACK`). All chart events load now
  (SE_DEFAULT still auto-builds at bring-up; SE_QUEST/SE_QUESTDEL are quest-added).
- [x] **SpawnMon** (`ExecSpawnMon`, C++ QuestSpawnMon.cpp) — each `QTT_SPAWNID` term adds a spawn (the term's
  count is the `REGEN_TYPE`, **not** a slot count — the count comes from the template); each `QTT_SPAWNID_DEL`
  term removes one. Recurses children only if a spawn was added. Runnable-gated.
- [x] **DieMon** (`ExecDieMon`→`ForceKillSpawn`, C++ QuestDieMon.cpp) — force-kills every live monster of each
  `QTT_SPAWNID` term's spawn: an `SE_QUESTDEL` spawn is removed silently (`CS_DIE_ACK` + despawn, no reward, no
  re-arm — C++ `m_bRemove`); any other spawn is a **credited** kill (the quester is stamped keeper → the normal
  `OnMonsterDeath` awards exp/loot + corpse/re-arm). Always recurses children. Runnable-gated.
- [x] **DropItem** (`ExecDropItem`, C++ QuestDropItem.cpp) — **death-triggered** (the `TT_KILLMON` hook fires it,
  so the target is the just-killed monster): picks one random `QTT_ITEMID` term and appends an owner-locked item
  (owner = the quest holder, count = the term count) to the corpse via `AddCorpseItem` — **not** gated on
  `MaxWeight`, so it drops even off a table-less monster. New `Item.OwnerId`; the owner filter is honored in
  `CS_MONITEMLIST_ACK` (a non-owner sees an empty corpse) and `CS_MONITEMTAKE_REQ` (only the owner may take it,
  bypassing the public-loot keeper rule). No child recursion, no runnable gate (C++).
- 8 tests (`QuestSpawnTests.cs`): SpawnMon add / del / de-dup, DieMon credited-kill-gives-exp, DieMon
  SE_QUESTDEL-silent-no-exp, DropItem corpse-attach, DropItem hidden-from-non-owner, DropItem owner-only-take.

> **Deferred (documented):** the **Regen** subtype (`CQuestRegen`/`RegenDynamicMonster`) — **now done in Phase
> 34** (below). Also deferred: the leader-cluster / group-order spawn branches, essential monsters, the loot
> magic/rare-option rolls, and the party-loot owner-tag routing (public loot stays solo/keeper-only).
> RT_TIMELIMIT self-expiry is intentionally inert (matches the C++).

---

## ✅ Done (Phase 34 — quest Regen: dynamic monster replacement)

Completes the monster-spawn subtype family. **Regen** (C++ `CQuestRegen`) is death-triggered (the `TT_KILLMON`
hook): for each `QTT_MONID` term it mints a **one-shot dynamic spawn** of that monster kind at the killed
monster's death position and force-removes it when the original respawns.

- [x] **`RegenDynamicMonster`** (`MapService.Spawns.cs`, C++ TMapSvr.cpp:11866) — mints a synthetic `SE_DYNAMIC`
  spawn template (prob 100 / count 1 / range 0 / delay 0) for one monster kind at a position, from a recycled
  reserved id (`_dynamicIdFree` stack + a counter seeded above the chart spawn ids — the port's stand-in for
  `m_mapExtraSpawnID`). Returns 0 if the monster kind is unknown or the pool is exhausted. `SpawnById` now sees
  these dynamic templates alongside the chart ones; the caller instantiates via `AddTimelimitedMon(RT_ETERNAL)`.
- [x] **The regen-del link** — `ExecRegen` stashes the minted spawn id on the *killed* monster's spawn slot
  (`SpawnSlot.RegenDelSpawn`, the port's `m_wRegenDelSpawn` — kept on the slot so it survives the death→respawn
  cycle). When `RunMonsterRegen` re-fills that slot (the original respawns), it `DelMonSpawn`s the linked
  dynamic spawn (C++ `CTAICmdRegen` link, TAICmdRegen.cpp:172).
- [x] **One-shot lifetime** — a dynamic (`SE_DYNAMIC`) spawn never re-arms: `RearmSpawnSlot` `DelMonSpawn`s it
  and recycles its id instead (C++ `SE_DYNAMIC` → `TAICmdRemove::DelMonSpawn`). `RunMonsterRegen` iterates a
  snapshot + skips spawns removed mid-sweep (the link can `DelMonSpawn` during a fill).
- [x] **`ExecRegen`** (`MapService.Quest.cs`, C++ QuestRegen.cpp) — runnable-gated; per `QTT_MONID` term:
  `RegenDynamicMonster` at the killed monster's position (resolved from the still-registered corpse) →
  `AddTimelimitedMon` → `LinkRegenDel`. Recurses children only if a spawn was minted. (The term count is the
  roam type — accepted for parity, inert in the port's range-based roam.)
- 4 tests (`QuestRegenTests.cs`): mint-at-death-position, force-removed-on-original-respawn (the full temporal
  loop), unknown-monster-kind mints nothing, one-shot-does-not-re-arm.

> **Deferred (documented):** the leader-cluster / group-order spawn branches and essential monsters remain (as
> for the whole spawn pipeline). Party/dungeon map instances are single per (channel, map).

---

## ✅ Done (Phase 35 — the cure/dispel layer)

Completes the maintained-skill (buff) engine. A **cure skill** — any skill template carrying `SDT_CURE` data
rows — cast on self/an ally through the PC→PC `CS_DEFEND` branch runs its cure effects on the target (C++
`PerformSkill`'s `SDT_CURE` switch, TObjBase.cpp:3390; the effect fires per `SDT_CURE` row).

- [x] **Dispel** (`ApplyPlayerCure`→`StripMaintains`, `MapService.Buff.cs`) — `SCT_POSREMOVE` strips positive
  maintains (`DeletePositiveMaintainSkill`, `IsPositive`), `SCT_NEGREMOVE` strips debuffs
  (`DeleteNegativeMaintainSkill`, the **strict** `m_bPositive == SPT_NEGATIVE` — so `SPT_NONE` buffs survive,
  *not* the looser `IsNegative`). Removal routes through `EraseMaintainPlayer` (broadcasts `CS_SKILLEND_ACK` +
  recomputes vitals).
- [x] **Instant heal** — `SCT_HP`/`SCT_MP` add `Calculate(level, row, GetMaxHP/MP)` plus a **0-15% over-heal**
  roll (`+ n·(rand%16)/100`), clamped to max, then broadcast `CS_HPMP_ACK` (C++ clamps + broadcasts in `Defend`
  after `PerformSkill`).
- [x] Wired into the DEFEND PC→PC branch alongside the Phase-31 self/ally buff (C++ `Defend` runs
  `MaintainSkill` + `PerformSkill` both), and broadcasts the cure `CS_DEFEND_ACK` (`bIsMaintain 0`). New
  `SkillTemplate.HasCure()` + `SdtCure`.
- 6 tests (`CureDispelTests.cs`): HP heal + broadcast, MP restore, heal clamp-to-max, POSREMOVE strips
  buffs-keeps-debuffs/none, NEGREMOVE strips debuffs-keeps-buffs/none, combined heal+dispel.

> **Deferred (documented):** the **stat-layer `CalcCure`** counteraction (the sign-guarded term in
> `CalcAbilityValue` — it needs the mid-cast `m_pInstanceSkill` threading the port doesn't model; the *instant*
> cure is what's ported); `SCT_CANCEL`/`SCT_DIE` (their block/die buff flags — `IsBlockType`/`IsDie` — aren't
> loaded); the recall-drain / aftermath / revival / reset-cooltime / trans-cost execs (unported subsystems);
> and the `SCT_MCPOWER/POISON/WOUND/DISEASE` no-ops. Cure is applied on a PC target only (self/ally). The buff
> engine's remaining deferrals (remain/passive `m_vRemainSkill`, `ApplyEffectionBuff`, loop skills) stand.

---

## ✅ Done (Phase 36 — the skill-learn path + quest GiveSkill)

Ports `CTObjBase::UpdateSkill` (TObjBase.cpp:2495) and completes the Phase-29-deferred quest **GiveSkill**
subtype (`CQuestGiveSkill::ExecQuest`, QuestGiveSkill.cpp:20) — the only server-side caller of `UpdateSkill`.

- [x] **`UpdateSkill`** (`MapService.SkillLearn.cs`) — **add-only**: if the character does not already know
  the skill, a learned `Skill` is created at the requested level (keyed by the template id) and
  `CS_SKILLBUY_ACK(SKILL_SUCCESS)` is pushed; an already-known skill (at *any* level) is a no-op returning
  `false` (there is no level-up here). Level stored verbatim; the caller floors it at 1.
- [x] **`ExecGiveSkill`** (`MapService.Quest.cs`) — learn the `QTT_SKILLID` term's skill (id = term id, level =
  the term count, `max(count,1)`), gated on the skill existing in the chart **and** its class mask matching
  (`m_dwClassID & BITSHIFTID(m_bClass)` — the gate lives in the caller, matching the C++). Recurses children
  **only** when the skill was newly granted (the C++ `&&` short-circuits on `UpdateSkill` returning TRUE, so an
  already-known skill blocks the completion chain). Runnable-gated (`CanRunQuest == QCT_NONE`).
- [x] `SkillTemplate.ClassId` (+ `dwClassID` in the `TSKILLCHART` load) + `IsClassMatch(class)`;
  `CS_SKILLBUY_REQ`/`_ACK` ids; the `CS_SKILLBUY_ACK` sender (byte-exact: `bRet, wSkillID, bLevel, Tick,
  gold, silver, cooper, skillPoint, kind[4]` — the `DWORD Tick` field the header comment omits *is* present).
  Reuses the shared `SkillUseResult` (= `TSKILL_RESULT`) enum.
- 8 tests (`SkillLearnTests.cs`): learn + ack, level-from-term-count, zero-count floored to 1, class mismatch
  (no learn/ack), unknown skill (no learn), newly-learned recurses child, already-known no-op + blocks child
  chain, `CS_SKILLBUY_ACK` byte layout.

> **Deferred (documented):** the **NPC-purchase handler `OnCS_SKILLBUY_REQ`** (CSHandler.cpp:2170) — both its
> LEARN-new and LEVEL-UP branches — blocked on subsystems this port does not model: the **skill-point
> currency** (`m_wSkillPoint`/`IsEnoughSkillPoint`/`GetNeedSkillPoint`/`GetNextSkillPoint`), the **level-cost /
> price tables** (`FindTLevel(...).m_dwMoney` × `GetPrice`), the **NPC skill-teaching lists** (`pNpc->GetSkill`),
> **parent-skill prerequisites** (`m_wParentSkillID`/`CheckParentSkill`), and the trade lock. Also deferred:
> `RemainSkill` (the passive `m_vRemainSkill` registry — a no-op for non-remain skills anyway), `AutoEquipSkill`
> (auto-grant class skills on level-up), the skill-reset path (`CS_SKILLINIT`/`InitializeSkill`), and the
> server-push full list (`CS_SKILLLIST_ACK` — the login list already rides inside CHARINFO). The ack emits
> `skillPoint`/`kind[4]` as 0 (SP currency unmodelled — CHARINFO also emits 0). The skill-learn DB save-back
> (`TSaveSkill`) is out of scope (as with the other in-memory-only grants).

---

## ✅ Done (Phase 37 — the player cabinet / item warehouse)

Ports `CTPlayer::m_mapCabinet` + the five `OnCS_CABINET*` handlers (CSHandler.cpp:5528-5902) and the transfer
core (`PutinCabinetItem`/`TakeoutStorageItem`, TPlayer.cpp:1542-1715). **Verdict from the C++ map: LOCAL,
in-memory on the player** (no world round-trip). Per-character; no money storage; up to **3** cabinets
(ids 0/1/2), **16** items each.

- [x] **Open** (`OnCS_CABINETOPEN_REQ`) — `CABINET_ALREADY` for an already-open one, `CABINET_MAX` for a
  full/out-of-range new one, else charge the per-id gold cost `[0, 10000, 1000000]` (`UseMoney` + `CS_MONEY_ACK`)
  and mark it used. `CS_CABINETOPEN_ACK{bResult, bCabinetID}`.
- [x] **List / item-list** — `CS_CABINETLIST_ACK` (count + `{bCabinetID, bUse}`); `CS_CABINETITEMLIST_ACK`
  (`bResult`, and on success `bCabinetID` + `DWORD count` + per item `dwStItemID` + the item block via
  `WrapPacketClient(addItemId: false)` — the leading slot byte is **omitted**, matching the C++ `bAddItemID`).
- [x] **Put-in** (`PutinCabinetItem`) — reject equipped source; gate on the `ITEMTRADE_CABINET` (=4)
  `m_bIsSell` bit (chart-gated, so a template-less DB-free item is permissive); fill existing same-stacks
  (ascending `StItemId`), then create exactly **one** new slot for up to a stack of the remainder (if under 16);
  reduce/remove the source bag item; refresh via `CS_CABINETITEMLIST_ACK`. 16-full-with-no-mergeable → `CABINET_FULL`.
- [x] **Take-out** (`TakeoutStorageItem`) — **whole-stack only** (exact count); place at the requested dest
  slot if free (`CS_ADDITEM_ACK`), else general-push (`CanPush`/`PushTItem`) with no partial move; charge the
  per-id fee `[100, 100, 300]` **only on a successful move**.
- [x] **`StItemId`** allocation = `1` when empty else max-existing+1 (C++ `GetCabinetItemIndex` — reused after
  the top is removed, gaps left; not monotonic). `Item.StItemId` + `WrapPacketClient(bool addItemId)`.
- [x] **Persistence** — LOAD on enter: `TCABINETTABLE` headers (`LoadCabinetsAsync`) + the `STORAGE_CABINET`
  items already returned by the char-item load, dispatched into cabinets (`dwStorageID`→`StItemId`,
  `bItemID`→cabinet id). SAVE: cabinet items ride the **incremental** direct path stamped `STORAGE_CABINET` /
  `StItemId` / cabinet-id (`EnqueueCabinetItemSave`; take-out enqueues a delete) — the full inventory snapshot is
  `bStorageType=0`-scoped so it never clobbers cabinet rows; the open-state (`bUse`) is written on open
  (`TSaveCabinet`).
- 17 tests (`CabinetTests.cs`): open success/cost/broke/already/out-of-range, list open-state, item-list
  notuse/missing, put-in new-slot/tradable-gate/merge/full, take-out whole-stack+fee/wrong-count/bag-full-no-fee,
  the item-list byte layout (slot byte omitted), and the cabinet-storage save stamping.

> **Deferred (documented):** the account **secure-code** gate; the **store/trade/tournament** idle gates (state
> the port doesn't model); the **remote NPC-call scroll** path (`bNpcInvenID/bNpcItemID` set → the map-0/8
> restriction + the `IK_NPCCALL` scroll validate/consume) — the normal cabinet-NPC-adjacent access is fully
> ported; the scroll path performs the transfer but skips the scroll consume. The DB round-trip
> (`TCABINETTABLE`/`TSaveCabinet` + the `STORAGE_CABINET` item rows) is wired but **live-DB unverified this
> session**. The separate **guild warehouse** and premium **cash cabinet** are distinct subsystems, out of scope.

---

## ✅ Done (Phase 38 — party gameplay: shared exp + party loot)

Ports the map-side gameplay of being in a party. **Party membership is world-authoritative** (C++ `CTParty`
lives on TWorldSvr; the map holds only the replicated scalars `m_wPartyID`/`m_bPartyType`/`m_dwPartyChiefID`/
`m_wCommanderID`, pushed via MW). The map's *gameplay* keys entirely off those scalars among near players, so it
is fully local and DB-free-testable.

- [x] **Replicated state** — `Character.PartyType` (now captured on enter — the byte was previously discarded)
  + the PT_SOLO-masked accessors `GetPartyId()`/`GetPartyChiefId()`/`GetCommanderId()` (C++ `GetPartyID` etc. —
  a member in **PT_SOLO** reports no party and opts out of all sharing). Enums `PartyType` (PT_*) / `OwnerType`.
- [x] **Party keeper** (`Monster.AddDamage(charId, partyId, dmg)` + `KeeperType`) — damage accumulates into the
  attacker's **party bucket** (C++ `nKey = wPartyID ? MAKEINT64(wPartyID,0) : dwHostID`); when a party bucket
  first crosses `MONKEEP_PER` (10% of MaxHP) the keeper is the party (`OWNER_PARTY`, keeper id = party id),
  else the char (`OWNER_PRIVATE`). Combat passes `ch.GetPartyId()`.
- [x] **Shared exp** (`AwardPartyKill`, C++ `OnDie` OWNER_PARTY path) — the near members (the monster's 3×3
  `GetNeighbor` whose effective party id matches the keeper) share: `total = GetExp() × (1 + 0.01·(n²/2 + n −
  1.5))` (party-size bonus; n=1 ⇒ ×1.0), split per member `× memberLevel / ΣmemberLevel`, then `× GetLevelRate`
  (−10%/level over the mob, floored at 0) with the C++ `+0.99` ceil. Solo (`OWNER_PRIVATE`) keeps the Phase-17
  path. The hunt-quest advance fires per recipient.
- [x] **Party loot** — a party-keeper corpse is lootable by any member (`CanLootKeeper`: keeper char, or any
  member of the keeper party — this is PT_FREE free-for-all); on a successful party-corpse item take, the near
  party members are notified via `CS_PARTYITEMTAKE_ACK` (dwCharID + item block, `addItemId=false`).
- 9 tests (`PartyTests.cs`): exp split (value-exact 322/322 same-level, 293/316 level-weighted), non-member
  exclusion, solo-unchanged, party-keeper assignment, other-member-can-loot, non-member-denied, loot broadcast,
  the PT_SOLO accessor mask.

> **Deferred (documented):** party **management** (`OnCS_PARTYADD`/`JOIN`/`DEL`/`MOVE` invite/accept/leave — pure
> MW relays to the world's `CTParty`, which owns `GenPartyID`/`JoinParty`/`LeaveParty`; that logic belongs in
> **TWorldSvr.Net**, not the map) and the inbound `MW_PARTYJOIN`/`ATTR`/`DEL` state-writes + client relays; the
> **exotic loot modes** PT_HUNTER (`MIT_AUTHORITY` for a non-hunter) / PT_LOTTERY (roll) / PT_CHIEF
> (`PartyChiefItemTake`) / PT_ORDER (world round-robin); the **party money level-split** (a member takes the full
> corpse money here — the C++ splits it by member level); the **party MANSTAT/ATTR** stat/recolor broadcasts (the
> world fans MANSTAT to all members across maps); cross-map member exp (`SendMW_MONSTERDIE_ACK`); soulmate +10%;
> the pcbang/scroll/skill `wBonus`; the party-kill 25-level anti-farm gate. The **client-shown** party cap is 6,
> the server cap `MAX_PARTY_MEMBER` is 7.

---

## ✅ Done (Phase 39 — player-to-player deal / trade)

Ports the C++ inline deal machine (`OnCS_DEALITEM*`, CSHandler.cpp:10774-11145) + `tagDEALITEM`. A same-map,
**in-memory**, two-party state machine paired by partner **name** (no partner id). `Deal` on the session:
`Status` (paired READY/START/CONFORM), `Dealing` (per-side READY/WAIT/ADDITEM/CONFORM), the offered
money + item copies. Enums `DealResult` (DEALITEM_*) / `DealStatus` (DEAL_*).

- [x] **ASK → RLY** — invite by name (`CS_DEALITEMASK_ACK` to the target), accept (`SetTarget` on both →
  START/WAIT + `CS_DEALITEMSTART_ACK` to both) or decline (`CS_DEALITEMEND_ACK` + clear both).
- [x] **ADD** (one-shot per side, gated `Dealing == WAIT`) — record the offered money (`UseMoney` dry-run
  affordability) + whole-slot item copies, re-validating each (`FindInven`→NOINVEN, item+count→NOITEM,
  `Item.CanDeal()`→NOITEM, dedup→INVALIDITEM) and the partner's bag space (`CanPush`→CANTRECV); push
  `CS_DEALITEMADD_ACK` (offer preview, `addItemId=false`) to the partner. Nothing leaves the bag yet.
- [x] **Confirm** (`CS_DEALITEM_REQ`) — two-phase state-encoded lock (no bOkey field): the first confirm sets
  both `Status = CONFORM`; the second runs the exchange. `bOkey==0` cancels (CANCEL + clear both).
- [x] **Exchange** — guard first (`ValidDeal` both: offered items still present/identical + money still
  affordable; then `CanPush` both bags) → **abort clean** (BUSY/CANTRECV) on any failure with no mutation;
  else commit each side: `EarnMoney(recv)` → `UseMoney(send)` → erase offered from bag (`CS_DELITEM_ACK`) →
  `PushTItem(received)` (`CS_ADDITEM/UPDATEITEM_ACK`) → `CS_DEALITEMEND_ACK(SUCCESS)` + `CS_MONEY_ACK` + clear.
  Received copies get fresh dlIDs, so the incremental item-save re-homes them (sender's rows deleted).
- 10 tests (`DealTests.cs`): ask-notify, accept-opens-both, decline, add-notifies-partner, the full item+money
  swap, first-confirm-doesn't-execute, cancel, untradable-item, receiver-bag-full, not-enough-money.

> **Deferred (documented):** the invite gates `CheckProtected` (block-list) / `IsActionBlock` / `CanTalk`
> (nation/war eligibility); the ~20 whole-player **deal-lock** guards (`m_bStatus >= DEAL_START` blocking
> move/sell/use/drop/bank/mail mid-deal) — the execution-time `ValidDeal` re-check already guarantees integrity
> if an offered item is moved/consumed; the cross-map `MW_DEALITEMERROR` teardown (deal is same-map); and the
> C++ UDP trade log (a no-op in that build) + the `DM_DELETEDEALITEM`/`SAVEITEM` batch (item persistence rides
> the existing incremental DEL/ADD item saves).

---

## ✅ Done (Phase 40 — personal store / player vendor)

Ports the C++ inline store handlers (`OnCS_STORE*`, CSHandler.cpp:11147-11545) + `m_bStore`/`m_strStoreName`/
`m_mapStoreItem`. A same-map, in-memory listing: the offered items **stay in the seller's bag** (the store only
holds references — inven+slot+count+price), looked up live on browse/buy. `Store` on the session:
`IsOpen`/`Name`/`Items` (each a `StoreItem` = slotKey, gold/silver/cooper/credits, inven, slot, count). Enum
`StoreResult` (STORE_*).

- [x] **Open** (`OnCS_STOREOPEN_REQ`) — validate each offer (owned / `Item.CanDeal()` / not duplicate-slot /
  enough count → NOITEM/NOTDEAL/NOITEMCOUNT), record references, set `IsOpen`; send `CS_STOREOPEN_ACK(SUCCESS)`
  + the seller's own `CS_STOREITEMLIST_ACK` to self, and broadcast `CS_STOREOPEN_ACK` to the 3×3 neighbors
  (excl self). A failure clears and replies to self only.
- [x] **Close** (`StoreClose`) — clear + broadcast `CS_STORECLOSE_ACK` to the neighbors **including self**.
- [x] **Browse** (`OnCS_STOREITEMLIST_REQ`) — `CS_STOREITEMLIST_ACK`: seller id, name, count, then per live offer
  `{ slotKey, credits, gold, silver, cooper, item block (addItemId=false) }` (only offers whose seller item
  still exists are listed).
- [x] **Buy** (`OnCS_STOREITEMBUY_REQ`) — guard money (`UseMoney` dry-run) + bag space (`CanPush`) **before**
  any mutation, then `PushTItem` the copy to the buyer, deduct buyer money, decrement the offer + the seller's
  live stack (DEL/UPDATE item ack to the seller), credit the seller, `CS_STOREITEMSELL_ACK` to the seller,
  auto-close when the last offer sells out, refresh the buyer's list, `CS_STOREITEMBUY_ACK(SUCCESS)`.
- [x] **Visibility** — `CS_ENTER_ACK` now carries the real `bStore` + store name (the layout already had the
  placeholders) so a late arrival sees an open store's icon.
- 10 tests (`StoreTests.cs`): open+broadcast, untradable-fails, over-owned-fails, close+broadcast, browse layout,
  the item+money buy, auto-close-on-last, partial-offer-leaves-remainder, not-enough-money, buyer-bag-full.

> **Deferred (documented):** the account **secure-code** / riding / transform open guards + the movement-lock
> while storing; the `TSTORE_SKILL` (804) "storing" buff (visual); the faction / free-trade-zone
> (`GetWarCountry`) browse/buy gate; and the **credits** (PvP-point) alternate price path (a credits-priced offer
> returns `NEEDMONEY`). A self-buy is rejected (a port safety — the C++ has none).

---

## ✅ Done (Phase 41 — the shield-block roll)

Ports the C++ `CTObjBase::GetShieldDP`/`GetShieldMDP` (TObjBase.cpp:2171/2184), the per-damage-component block
roll run inside `CalcDamage`. Block is **defender-side** (not a hit-type outcome of the attacker's
`GetAtkHitType`): a defender's equipped shield rolls its block probability and, on success, adds its defence
power to normal defence and downgrades the reported hit to **`HT_BLOCK`** (=3).

- [x] **Pure function** — `StatEngine.ShieldBlockDp` (physical) / `ShieldBlockMdp` (magic): the rate
  `dwR = ABILITY_SDR` (Σ equipped `MTYPE_SDR` enchants + an equipped `IT_SHIELD`/`IK_SHIELD` shield's base
  `bBlockProb`) folded through the `MTYPE_SDR` buff layer (`Buffed`); on `dwR > rand()%100` (**strictly**
  greater — rate 0 never blocks, 100 always) it returns `ABILITY_SDP` (Σ `MTYPE_SPDPOW` enchants + the shield's
  base `m_wDP`) folded through the `MTYPE_SPDPOW` buff layer, else 0. The magic variant uses `MTYPE_SMDR`/
  `MTYPE_SMDPOW` + an `IK_MULTIVAJRA` off-hand's `bBlockProb`/`m_wMDP`. Broken items skipped (the C++
  `HavePower` gate at the head of the `CalcItemAbility` loop).
- [x] **Live wiring** — the monster→player melee path (`MapService.AI.cs` `AttackPlayer`, a PC defender with an
  equipped-item model): hit-type rolls first (C++ order), then the shield roll on a landed hit; the returned
  power is **added to** `DefendPower` (additive reduction — the 5/7 min-damage floor still holds, never a fixed
  %/full negation), and a nonzero roll flags `HT_BLOCK`. A kill still wins the report (`HT_LASTHIT`), matching
  the C++ `m_dwHP ? bAtkHit : HT_LASTHIT`.
- [x] **PC→monster** (`MapService.Combat.cs`) — a monster defender has no equipped-item model, so its shield DP
  stays 0 (matching the C++ where a fieldmob carries no shield); the block path is present but inert there.
- 9 tests (`ShieldBlockTests.cs`): rate-100-always / rate-0-never / no-shield / enchant-adds-to-rate+power /
  broken-shield-contributes-nothing / magic-variant-uses-multivajra+MDP (pure); and live monster→shielded-player
  reduces-damage+`HT_BLOCK`, unshielded full-damage+`HT_NORMAL`, lethal-through-shield reports `HT_LASTHIT`.

> **Deferred (documented):** the `CalcItemAbility` **disguise gates** (`HaveDisguiseBuff`/`HaveDisWeapon`/
> `HaveDisDefend` — the transform/disguise buffs aren't modelled; with no disguise buff they're no-ops, so the
> item sum is exact); the **durability decrement** on block (`DurationDec(4,…)`) and the **`TBLOCK_SKILL`
> reaction** `ForceMaintain` (block-triggered buff) — both fire off the `HT_BLOCK` flag in the C++ `Defend`
> tail. PvP (a PC attacking a shielded PC) is still deferred, but the roll it needs is now in place.

---

## ✅ Done (Phase 42 — the CalcDamage completion: MTYPE_MDAMAGE + direct HP/MP execs + lifedrain)

Ports the full C++ `CTObjBase::CalcDamage` exec dispatch (TObjBase.cpp:344) for a player→monster hit, replacing
the Phase-13 single-roll shortcut. The attacking skill's `SDT_ABILITY` damage rows are iterated; each resolves
on its **attr** (physical/long → physical AP band + `GetDefendPower`; magic → magic AP band +
`GetMagicDefPower`; `SATT_NONE` → skipped) then its **exec**:

- [x] **`MTYPE_DAMAGE` (30)** — HP damage. Crit off the **max** band (physical) / **min** band (magic) via
  `FTYPE_PCD`/`FTYPE_MCD`; instance-skill scaling `CalcValue(SDT_ABILITY, MTYPE_DAMAGE, roll)`. The map value is
  the **full roll** (C++ `nDamageHP` is uncapped) — `OnDamage` floors HP at 0, so a kill now reports the full
  damage, not the clamped remainder (a byte-fidelity fix vs. the old `min(scaled, hp)`).
- [x] **`MTYPE_MDAMAGE` (88)** — MP damage. Crit off the **min** band; reduces the target's MP; keyed **88** in
  the damage map (was previously always `MTYPE_DAMAGE`).
- [x] **`MTYPE_HP` (14) / `MTYPE_MP` (22)** — direct heal/drain: `nInc = CalcValue(SDT_ABILITY, MTYPE_HP, MaxHP)`;
  `nInc < 0` drains (clamped to the working HP, reported keyed 14/22), `nInc ≥ 0` heals (negative `nDamageHP` ⇒
  `OnDamage` restores, clamped to max, **not** shown in the map).
- [x] **Multi-row damage map** — a skill with an HP row and an MP row produces **two** `CS_DEFEND_ACK` entries;
  the C++ find-by-`m_bAttr` / insert-by-`m_bExec` quirk is reproduced (insert-if-absent by exec; value
  `(WORD)dwValue`).
- [x] **Lifedrain** — `MTYPE_HI` (38) / `MTYPE_MI` (40): a `Calculate`-scaled fraction of the HP/MP damage dealt
  is sent to the world as **`MW_GETBLOOD_ACK`** (`MW_BASE+0x008A`: `dwAtkID · bAtkType · dwHostID · bBloodType ·
  dwBlood`), which credits it to the attacker. Only fires when the matching pool took damage.
- [x] **Fallback preserved** — a basic attack (no skill / no damage rows) still takes the single physical-or-magic
  HP-damage roll keyed `MTYPE_DAMAGE` (Phase-13/15 behavior, byte-for-byte). A pure-buff/cure skill on a monster
  keeps its Phase-31/35 maintain/debuff handling.
- 11 tests (`CombatCoreTests.cs`, all **byte-exact** against the parsed damage map + world packet): HP-drain,
  MP-drain, HP-heal (+ clamp-to-max), MDAMAGE keyed 88 (HP untouched), multi-row two-entry map, full-roll-on-kill,
  SATT_NONE-deals-nothing, `MTYPE_HI`/`MI` `MW_GETBLOOD_ACK`, and no-blood-when-that-pool-took-no-damage.

> **Deferred (documented):** the **buff-layer amplification** of the roll (the `CalcAbilityValue` maintain/remain
> terms + `ApplyEffectionBuff` — only the instance-skill `CalcValue` term is applied) and the **stat-layer
> `CalcCure`** counteraction (needs the mid-cast `m_pInstanceSkill` threading the port doesn't model);
> `DistributeSkill` (pet damage share) + the AUTOAI-recall ×3 (pets); the custom **Araz ≥3000** hard-coded skills
> (`%`-max-HP nukes + the 3302 lifesteal buff — build-specific); the cure/status **HP↔MP transfer/swap** execs
> (`SCT_HPTRANS`/`MPTRANS`, `SDT_STATUS_HPMPCHANGE`/`HPTOMP` — **now ported in Phase 43**); and PvP / long-as-a-distinct-branch.

---

## ✅ Done (Phase 43 — the direct HP/MP transfer &amp; swap execs)

Finishes the combat-core vitals family that Phase 35 (`SCT_HP`/`SCT_MP` instant heal) began — the remaining
direct HP/MP writes, cast on self/an ally via `CS_DEFEND` and applied on the resolved target (C++
`PerformSkill`, TObjBase.cpp:3584/3688). The transfer amounts come from the request's `wTransHP`/`wTransMP`
(previously read-and-discarded).

- [x] **`SCT_HPTRANS` (15) / `SCT_MPTRANS` (16)** — added to the `ApplyPlayerCure` `SDT_CURE` switch:
  `target.HP += Calculate(level, i, wTransHP)` (no over-heal roll, unlike `SCT_HP`); clamped to Max via the
  `Defend` tail. `wTransHP`/`wTransMP` are now captured in `OnCS_DEFEND_REQ` and threaded through.
- [x] **`SDT_STATUS_HPMPCHANGE` (50)** — new `ApplyPlayerStatus` handler: swaps HP↔MP, each side self-clamped to
  its own max (`newHP = min(oldMP, MaxHP)`, `newMP = min(oldHP, MaxMP)`); fails silently (C++ `PERFORM_FAIL`)
  when `MP == 0`.
- [x] **`SDT_STATUS_HPTOMP` (51)** — sacrifice half the current HP (`dec = HP/2`) and add `Calculate(level, i,
  dec)` of it to MP (clamped to MaxMP); fails silently when `HP == 0`.
- [x] Both handlers broadcast `CS_HPMP_ACK` on a change + the cure/status `CS_DEFEND_ACK` to the target's view.
  `SkillTemplate.HasVitalsStatus()` gates the status handler; `Msg`-level wiring reuses the Phase-35 path.
- 8 tests (`HpMpExecTests.cs`, byte-observable via the target HP/MP + the parsed `CS_HPMP_ACK`): HP/MP-transfer
  (+ clamp-to-max), HP↔MP swap (+ per-side clamp + the MP == 0 fail), and HP-to-MP (+ MP clamp).

> **This closes the in-process, DB-free combat-core surface.** The remaining combat items require other
> subsystems and stay deferred: the **stat-layer `CalcCure`** + the **buff-amplification** of the damage roll
> (`CalcAbilityValue` maintain/remain/effection terms — need the mid-cast `m_pInstanceSkill` threading the port
> doesn't model), **`DistributeSkill`** + AUTOAI-recall ×3 (pets), the **shield-block durability/reaction tail**,
> the **custom Araz ≥3000** hard-coded skills (build-specific), and **PvP** / long-as-a-distinct-branch.

---

## ✅ Done (Phase 44 — the monster aggro/hate table + retargeting)

Replaces the Phase-19 **last-hitter** targeting with the real C++ hate table (`CTMonster::m_mapAggro`,
`map<__int64, TAGGRO>`), so a monster fights the **highest-cumulative-aggro** attacker — not merely whoever
hit last — and re-picks correctly when that attacker leaves. `Monster` owns the table + the pure arithmetic;
`MapService.Aggro.cs` applies the retarget (the C++ `CTAICmdChgHost` action) + the broadcast.

- [x] **The table** (`Monster.AggroEntry` + `Dictionary<long, AggroEntry>`), keyed `(objId<<32)|objType`
  (C++ `MAKEINT64`), value = `{objType, objId, hostId, aggro, country}` (C++ `tagAGGRO`).
- [x] **`SetAggro`** (TMonster.cpp:141) value-exact: rejects `nAggro==0` / `OT_MON` attacker / `attackId==0`,
  the **`MT_GOHOME && !active`** re-aggro guard, and the **same-country** reject (`attackCountry == WarCountry`);
  **warrior ×3/2** (int) class scaling; accumulate with a **floor at 0**; entry-create gated by `active`. The
  **retarget decision**: increase-branch pulls into battle (`dwNew && !BATTLE`) or steals the target only past
  the **10% sticky threshold** (`dwOld + dwOld*0.1 < dwNew`); decrease-branch rescans the whole table for the
  top hostile-country entry. Returned to the service (no networking in `Monster`).
- [x] **`AddAggro`/`DelAggro`/`FindAggro`/`LeaveAggro`/`ClearAggro`** — the rest of the C++ surface;
  `LeaveAggro` drops the leaver and returns the **highest hostile-country survivor** (ascending-key tie-break =
  lowest object-id, matching `std::map`).
- [x] **`ApplyRetarget`** (`CTAICmdChgHost`, TAICmdChgHost.cpp:25) — set host/target, `EnterBattle` on the pull
  (`MT_NORMAL → MT_BATTLE`), seed the target's minimal hate (`AddAggro(...,1)`), and **`NotifyHost`**.
- [x] **`CS_MONHOST_ACK`** (`CS_MAP + 0x000F`, `dwMonID · bSet`) — the retarget/host-change broadcast: `TRUE`
  to the new host (the packet's `dwHostID`; C++ keeps the client value for a PC attacker, CSHandler.cpp:1523),
  `FALSE` to the rest in view.
- [x] **`Aggravate`** wired into `OnCS_DEFEND_REQ` (the C++ `Defend` opening `SetAggro`): a hostile hit (a basic
  attack, or a skill with `IsNegative`) adds `max(1, GetAggro(level))` hate; a positive skill never aggros.
- [x] **`Disengage`** (`ChaseTarget` leash/target-gone) — `LeaveAggro` + the neighbour recurse-drop: switch to
  the highest in-view survivor, else `DropAggro` (now a `ResetHost`-lite: clear target/host/table → `MT_NORMAL`).
- [x] **`SkillTemplate.GetAggro(level)`** — `m_dwAggro·pow(m_f1stRateX, exp)/100` (DWORD-truncated before /100).
- 9 tests (`MonsterAggroTableTests.cs`): warrior ×1.5, accumulate + floor, same-country reject, the 10%
  hysteresis steal, `LeaveAggro` survivor pick, the `GetAggro` formula, the `CS_MONHOST_ACK` wire bytes, and
  the end-to-end leash re-pick of an in-view survivor over going home.

> **Deferred (documented):** the **`dwAggro` chart column** isn't loaded, so live hate collapses to `max(1,0)=1`
> per hit (warrior ×1.5) — the retarget *mechanics* are exact regardless; tests set `Aggro` directly to exercise
> the magnitude. The **`MT_GOHOME` walk-back** state (the port pins a monster to its spawn anchor, so disengage
> collapses straight to `MT_NORMAL` — no `TA_RUN`-home `CS_MONACTION` / `CS_CHGMODE` sequence), the
> data-driven **`TMONSTERAI`** command table, **call-for-help / low-HP flee** (gated by the unloaded `m_bCall` /
> `m_wKind` / `m_bArea` flags + the multi-tick run-to-helper choreography), pack/leader **`SetEventToFollower`**,
> the **out-of-view previous-host** `CS_MONHOST_ACK(FALSE)`, the `SDT_AI`/`SDT_RUNAWAY` fear debuff, and the
> cross-server `SM_RESETHOST` leash reset all remain.

---

## ✅ Done (Phase 45 — host acquisition / aggro-on-sight)

Ports the C++ `CTAICmdSetHost` scan (TAICmdSetHost.cpp:27) so an idle **aggressive** monster engages a player
**without being hit** — the "how a monster starts fighting" half that complements Phase 44's "who it fights".

- [x] **`Character.CanHost` + `LastMoveMs`** (C++ `m_bCanHost` / `m_dwMoveTick`) — stamped in `OnCS_MOVE_REQ`
  (every move sets `LastMoveMs = NowMs` and `CanHost = true`, C++ CSHandler.cpp:517/555).
- [x] **`Monster.Aggressive`** — the auto-aggro gate. In C++ this is **not a monster-chart field**: a monster
  is aggressive iff its `bAIType` binds `AC_SETHOST` under `AT_ENTER` in the DB `TAICHART` table. That table
  isn't loaded, so this **defaults `false`** (no production regression; Phase-44 hit-aggro is unaffected).
- [x] **`TryAcquireHost`** (`MapService.Aggro.cs`) value-exact vs `SetHost::ExecAI`: gather the 3×3-view players,
  **seed** the first `CanHost` one (no recency — the C++ quirk, line 40), prefer the **nearest by Manhattan**
  that moved within **3000 ms**, and **lazily activate** a never-eligible first player (lines 57-64). On a pick
  it **folds** the C++ wake→`ChgHost`→`ChgMode` chain (SetHost only wakes + assigns host + `SelectSkill`; the
  target/aggro/BATTLE come from the scripted `AT_AICOMPLETE` successors) into the Phase-44 `ApplyRetarget`.
- [x] Wired into `RunMonsterAI`'s `MT_NORMAL` branch: an aggressive monster tries to acquire before roaming; on
  a pull it enters `MT_BATTLE` (+ `CS_MONHOST_ACK`) and chases/attacks from the next tick (Phases 19/20/44).
- 5 tests (`MonsterHostAcquireTests.cs`): aggressive acquires a recent player (+ `CS_MONHOST_ACK` bytes),
  passive never auto-aggros, nearest-player pick, the lazy-activation fallback, and the end-to-end MOVE →
  eligibility → acquire.

> **Deferred at the time — all but two closed by Phase 46:** the **`TAICHART`** AI-script table, the
> event-driven **`AT_ENTER`** trigger, the monster **`OS_*`** status enum and **`ChkHost`** are now ported.
> Still open: `CanHost`'s **ghost branch** (a dead player within `CELL_SIZE/2` — replaced by the `Hp > 0`
> filter, the port not modelling ghost) and `SelectSkill` (monster skill choice — monsters basic-attack only).

---

## ✅ Done (Phase 46 — the data-driven AI engine: `TAICHART`)

Replaces hard-coded monster behaviour with the real C++ interpreter. The C++ AI is **not** a per-tick sweep —
it is an **event machine** over three DB tables. Until now the port had the *behaviours* but none of the
*bindings* that decide which monster does what, when, and after how long.

**The chart** (`TMap.Data/AiCharts.cs`, loaded by `GameDatabase.LoadTemplatesAsync`) — three tables, exactly
the C++ `CTBLAICommand`/`CTBLAICondition`/`CTBLAIChart` queries (DBAccess.h:365-414):

| Table | Columns | Becomes |
|---|---|---|
| `TAICMDCHART` | `dwCmdID, bCmdType` | `AiCommandTemplate` — the opcode (`AI_COMMAND`, 15 kinds) |
| `TAICONCHART` | `dwCmdID, dwConditionID, bConditionType` | its `AiCondition` guards (`AI_CONDITION`, 3 kinds) |
| `TAICHART` | `dwTriggerID, dwCmdID, dwDelay, bTriggerType, bAIType, bLoop` | `AiScript` — per `bAIType`, a 14-slot trigger table of trigger-id → ordered `AiBinding`s |

`TMONSTERCHART.bAIType` (added to `MonsterTemplate`) selects the script, with the C++ `DEFAULT_AI` fallback
(`TemplateStore.AiScriptFor`). The C++ runs one `TAICONCHART` query **per command id** in a loop; we bulk-load
and bucket by `dwCmdID` — same result, one round trip.

**The engine** (`MapService.AiEngine.cs`) — `OnAiEvent` (C++ `CTMonster::OnEvent`) → `DoAiCmd` (`DoAICMD`) →
per-command `GetDelay`/`CanRun`/`ExecAI`, with `AiComplete` firing `AT_AICOMPLETE` keyed on the command's own
id. That last part is the whole point: **chains are data**, so wake → `ChgHost` → `ChgMode` → `BeginAtk` →
`Attack` comes out of the table rather than out of C#.

- [x] All **14 triggers** and **15 command opcodes**, each with its exact hard state gate (`OS_*` × `MT_*`)
  ahead of the chart conditions, then the AND-folded `AN_*` guards.
- [x] `Monster.Status` (`OBJ_STATUS` — the second state axis the C++ gates on) and `Monster.HostKey`
  (`m_dwHostKEY`, the command epoch).
- [x] **`Monster.Aggressive` is derived, not defaulted** — from `AiScript.IsAggressive` at spawn. **The live
  chart forced a correction here; see "What the live data changed" below.** This is what unblocks
  aggro-on-sight in production, which Phase 45 shipped inert.
- [x] **`CS_ENTERLB`/`LEAVELB`/`ENTERAB`/`LEAVEAB_REQ`** (`CS_MAP + 0x18..0x1B`, one shared layout, one
  handler `OnAggroBoundReq`) — the client-reported aggro bounds. Without these the engagement chain has no
  entry point, because engagement is client-driven, not server-polled.
- [x] **`AT_ENTER` is event-driven** — fired from `SendMonstersInView` (C++ `CTCell::EnterPlayer`,
  TCell.cpp:103) instead of polled by the tick sweep.
- [x] **`ChkHost`** (host re-validation, no recency window) — the Phase-45 gap.
- [x] `AT_DEFEND` routes through the chart: the hate table only *decides*; `ApplyRetarget` fires the event and
  the script's `ChgHost`/`ChgMode` carry it out. `AT_DEAD` fires from `OnMonsterDeath`.
- 17 tests (`AiEngineTests.cs`): `Binds`/`DEFAULT_AI` resolution, `SetHost` waking **without** entering battle
  (the unfolded chain), a three-command `AT_AICOMPLETE` chain, the `ChgMode` mode ring, all three conditions,
  hard-zero `GetDelay` overrides ignoring the row delay, delay scheduling, epoch invalidation, `bLoop` re-arm
  vs not, scripted-vs-scriptless sweep routing, and an unknown opcode still chaining.

### Two deliberate divergences

1. **The delay hop is local.** C++ `DoAICMD` ships a delayed command to the **world** server
   (`SendSM_AICMD_REQ`, passing the raw `CTAICommand*` as an opaque handle) which times it and echoes
   `SM_AICMD_ACK` back. That plane exists only because AI scheduling was centralised across map servers;
   single-map it is pure overhead and a pointer-lifetime hazard. Here it is a local `PriorityQueue` on the map
   clock, drained by `RunScheduledAi`. **The epoch check is preserved exactly** — a queued entry captures
   `HostKey` and is dropped if the monster's has moved on, which is what stops a stale command acting on a
   retargeted or dead monster. Revalidation order matches `OnSM_AICMD_ACK`: epoch → `CanRun` → `ExecAI` →
   re-arm on a truthy result.
2. **Scripted monsters bypass the legacy sweep.** A monster that resolves a script is driven entirely by the
   engine; one with no script (no `TAICHART` rows — DB-free, and every pre-46 test) keeps the Phase 18-45
   hard-coded `Roam`/`ChaseTarget`/`TryAcquireHost`. This mirrors the C++ `FindTMonsterAI(DEFAULT_AI)` fallback
   and keeps the server behaving with an empty chart instead of going inert. `MapWorker` logs which case you
   are in at bring-up (script / binding / aggressive counts, or a warning when the chart is empty).

### A preserved bug

C++ `CTAICommand::CheckCondition`'s `AN_CHGHOST` arm (TAICommand.cpp:60) computes
`targetId == rhId && targetType == rhType ? FALSE : TRUE` as a **bare expression statement with no `return`**,
so control falls out of the switch to the trailing `return TRUE` and the condition **always passes**. Shipped
monster behaviour is tuned around that, so the port reproduces it rather than "fixing" it — pinned by
`ChgHostCondition_AlwaysPasses_ReproducingTheOriginalMissingReturn`.

### Deferred (documented)

Monster **skills** (`m_pNextSkill`/`SelectSkill`/`SkillUse`), so `BeginAtk`/`Attack` fall back to the Phase-20
basic melee (same announce, `wSkillID = 0`); **`CheckAttack`** (silence/root — reads as always-true, but is
threaded through every call site so it goes live for free later); the `m_bRoamType == 4` fixed-monster gate and
`m_bIsSelf`, whose chart columns are unloaded; `Getaway`'s **`m_bCall` helper search** (only the plain flee is
ported); `Lottery`'s corpse-item roll; `Regen`'s **group-spawn redirect** (`m_lpvGroup` — the deferred
leader/cluster branch); and `Leave`'s item-routing / inven-lock stall, simplified to "wait until the corpse is
empty". A synchronous-depth cap (16) guards a cyclic chart, which the C++ avoids only by virtue of its world
round trip.

### What the live data changed

Reading the real `TGame_gsp` (47 `TAICHART` / 18 `TAICMDCHART` / 6 `TAICONCHART` rows → **2 scripts**;
**all 3536** monster rows carry a non-zero `bAIType`) **falsified the Phase-45 definition of "aggressive"**,
which this phase had initially carried forward:

- The old test was `Binds(AT_ENTER, AC_SETHOST)`. **Both** live scripts bind exactly that, so it marks
  *every* monster aggressive — clearly wrong.
- Decoding the chains shows why: `AT_ENTER` → `SetHost` → `Roam`(3 s, loop) → `SetHost` → … never reaches
  `ChgMode`, so it cannot enter battle. `AT_ENTER`/`SetHost` **wakes** a monster and gives it a host to drive
  its roam broadcasts; it is not aggro.
- Engagement is **client-reported**: `AT_ENTERLB` → `ChgHost` → `ChgMode`(→ `MT_BATTLE`) → `Follow`. Script 1
  (**3192** monsters) binds it; script 2 (**344** monsters) has no `AT_ENTERLB` row at all. That is the real
  aggressive/passive split, and it is the sane ~90/10 an MMO should have.

So `IsAggressive` is now `Binds(AT_ENTERLB, ChgHost) || Binds(AT_ENTERLB, ChgMode)`, and the four
`CS_*LB`/`*AB_REQ` handlers were added to give that chain its entry point. The rest of the decoded chart:
`AT_DEFEND` → `ChgHost`(BATTLE) → `Follow` / `ChgHost`(NORMAL) → `ChgMode` → `Follow`; `AT_DEAD` →
`Leave`(45 s, loop) → `AT_DELETE` → `Regen`(loop) or `AT_TIMEOUT` → `Remove`; `AT_HELP` → `Getaway`(1 s, loop);
`AT_LOTTERY` → `Lottery`(15 s); `AT_LEAVE` → `ChkHost` → `Roam`; `AT_ATHOME` → `ChgMode`(GOHOME) → `Roam`;
`AT_LEAVELB` → `ChgMode`(BATTLE) → `Gohome`.

> ✅ **Verified against live data.** `araz-mssql` up, `TMAP_TEST_GAME` pointed at `TGame_gsp`:
> `LoadTemplates_PopulatesAiScripts_WhenConfigured` passes (2 scripts / 47 bindings / 6 conditions, script 1
> aggressive + script 2 not, every monster template resolving a script), and a real server boot logs
> `Loaded 2 AI scripts (47 trigger bindings); 1 engage on sight (AT_ENTERLB → ChgHost/ChgMode)` alongside
> 36692 spawn points. Because **every** monster is scripted, the engine — not the legacy sweep — now drives
> all monster behaviour on a live server; the sweep remains only for the DB-free path.

---

## ✅ Done after Phase 46 (by commit, oldest first)

The phase write-ups above stop at Phase 46; what followed is recorded in the commits and the module headers of each
`MapService.*.cs` (each names its C++ sources, the bugs fixed and what is left out). In short:

- **Monsters and summons** — AI fixes, mounts, pets, the Evocator (all invocations, taming, behaviour), monster buffs and
  debuffs, summon fixes; the tick load cut from 25 % to 1 %.
- **Items and skills** — the item shop and loot fix, skill buying, passive skills, skill items and random skills, money and
  special boxes, exp boosts, premium, name/country/look changes, loop skills, take-all loot, helmet/comment, target query,
  monster drops (ranged and magic options).
- **PvP** — earning and paying points, duels, PvP buffs, the combat leftovers (buff erasers, enemy dispel, damage share).
- **Territories** — zones, region buffs, peace zones, war phases, mission / sky garden / fort / castle wars, heroes and
  the warlord's message.
- **Guilds** — founding to recruiting, cabinet, board, fame, mercenaries, point rewards, castle guards, level, guild
  skills.
- **Social** — block list, friends, messenger, soulmates, corps, party member recall; mail; the auction house.
- **Mini-games and events** — gamble, magic shop, soul lottery, rock-paper-scissors, party loot lottery; events (exp,
  drop, craft bonuses, spawns, lucky ticket); the kingdom balance.
- **Battle modes** — the arena, the tournament (windows, sign-up, bets, cheer, matches, the tournament character), the
  BoW and BR queues (the battles themselves run on the BoW/BR map server builds, not in this stack).
- **S7a** — fall damage, act items, medal items, the rewards window, stop-the-clock, waste refine.
- **S7b** — the cash shop: the list, buying, presents, the cabinet (take out, put in, more time), cash bonus items, the
  world's stop and sales; migration 022 fixes the baseline cash procs (buying gave nothing, cabinet time could not be bought).
- **S7c** — the GM tools: an operator's move, move to a player, gifts and chat bans; the world's gifts, chat bans, positions,
  lines, item states and help messages; chat bans held on chat and comments; the map level kick-out; the control server's link
  (`ControlIp`: announcements, kicks, the monster spawn finder and actions, castle info, the monitor).

- **S7d** — account security codes: create/change/disable/enable/unlock/check/remaining-lock-time; session-local unlocking,
  shared account attempts, ten failures → 24-hour lock persisted as UTC seconds, salted PBKDF2 hashes, and the original
  protected-action gates plus trade, cash purchases, medal items and cloaks. Migration 023 preserves legacy codes until their
  next save and clears the plaintext then. Failed security-state reads refuse entry; failed writes close the connection.
- **S7e** — custom-cloak list/change and guild-cloak registration. A custom change checks account and item ownership and
  atomically changes the equipped item and spends 4,000 medals in SQL. Invalid ownership/missing item/insufficient funds
  consume nothing. Guild changes use ordinary item persistence and broadcast the equipped appearance.
  Medal packages consume the item and credit its medals in the same SQL transaction.
- **S7f** — meeting-room rentals: exclusive per-channel private maps, occupied rooms filtered from NPC lists, owner-only
  invitations through the world, 120-second single-use invitations, guest busy checks, one-hour rental expiry, and release
  plus guest evacuation on owner exit/disconnect. The original SM timer round-trip is local to the serialized tick.
- **Sending / allocations** — ready outbound packets coalesce up to 128 packets / 64 KiB per socket write with no intentional
  delay; each client packet keeps its own cipher sequence. Copies and batches use ArrayPool, a slow peer is disconnected at
  4 MiB queued, and close drains replies with a two-second deadline. PacketWriter writes encoded strings directly into its
  buffer, exposes a non-allocating finalized span to network queues, and rejects wire-length overflow.
- **Login bursts** — migration 024 adds username/blacklist/protection lookup indexes; authentication DB sequences are limited
  to 100 concurrently (configurable). Character appearances use two fixed DB reads instead of two extra reads per character.
  Reproducible isolated SQL and loopback measurements are in `Tools/ServerBenchmarks`.

## 🚧 Not yet ported — the roadmap

Found by diffing the C++ `ON_RECEIVE` table (621 handlers enabled outside the BoW/BR builds) against the .NET dispatch
(October 2026). What is left that does something:

| Batch | What | C++ handlers |
|---|---|---|
| S7g | **Misc** — monster tempt/evolve, blood, magic mirror, reset host, other-self, SMS | `MW_MONTEMPT*`, `MW_GETBLOOD`, `MW_MAGICMIRROR`, `CS_RESETHOST`, `CS_OTHERSELF`, `CS_SMSSEND` |

Not needed here:
- **`DM_*`** — the C++ queues its database calls through its own `DM_*` messages; the .NET handlers call the database
  directly, so each `DM_*` pair is covered where its feature is ported.
- **Anti-cheat** (HackShield, nProtect, APEX) — no such client packages in this stack.
- **Map-to-map relays** (`MW_ADDITEM*`, `MW_PARTYORDERTAKEITEM`, `MW_MONSTERDIE`, `MW_TAKEMONMONEY`, `MW_DEALITEMERROR`,
  `MW_RELEASEMAIN`, …) — only between several map servers; this stack runs one.
- **Empty in C++** — `CS_TOURNAMENTJOINLIST_REQ` / `CS_TOURNAMEMTMATCHLIST_REQ` (commented out), the BoW-only
  `CS_CASHBOWRESPAWN` / `CS_EXCHANGEBOWBP` / `CS_BOWRANKING` / `CS_LEAVEBATTLEFIELD`.

### Infrastructure
- `CUdpSocket` / `CDebugSocket` UDP log-sink integration (LogIp/LogPort), `CTTextLinker` clickable
  in-chat links, `CTMiniDump` crash handling.
- ~~Local-stack wiring~~ — **done**: `mapsvr` is now a service in the repo-root `docker-compose.yml`
  (5816, dialing `worldsvr:3816`, gated on the migration job), so `docker compose up` brings the map up
  with the rest of the cluster instead of leaving the world with no peer. The DB-gated `TMap.Data.Tests`
  now log their skip rather than passing silently — see "Build & test" in the README for the
  `TMAP_TEST_GAME` invocation. Verified end-to-end: the map registers with the world
  (`Map server registered: id=1 type=4 channels=[1]`) and all 4 DB-gated tests pass against the live
  `TGame_gsp`.

### A bug the stack bring-up caught: duplicated channels (fixed)

Running the map in the stack for the first time surfaced `Announced to world: server 1, channels [1,1]` —
and it was **not** a compose artifact. The .NET configuration binder **appends** bound array elements to
whatever the target property already holds instead of replacing them, so `MapServerOptions.Channels`'
`= { 1 }` C# default plus the `"Channels": [ 1 ]` in `appsettings.json` bound to `{ 1, 1 }`.

That was live everywhere, not just under Docker, and it was not cosmetic:

| Consumer | Effect of the duplicate |
|---|---|
| `SendMW_CONNECT_ACK` | announced a channel **count of 2** with values `1, 1` |
| `InitMonsterSpawns` | `_spawns` is a **`List`**, so every `SE_DEFAULT` spawn point was added **twice** — **36692 spawn points where 18346 is correct, i.e. twice the intended monsters on the map** |
| `InitSwitches` | harmless — switches/gates are dictionary-keyed by id, so the second pass overwrote |

It also meant a single non-default channel could never be configured: `Map__Channels__0=2` bound to
`{ 1, 2 }`, the default leaking in beside the real value.

**Fix**: `Channels` now defaults to `Array.Empty<byte>()` (so the binder has nothing to append to) and
`MapServerOptions.Normalize()` — called once at the top of `MapWorker.ExecuteAsync`, before anything reads
the list — supplies the C++ default `{ 1 }` when unbound and collapses duplicates. Pinned by 7 tests in
`MapServerOptionsTests.cs` that bind through a real `ConfigurationBuilder` exactly as `Program.cs` does,
including the layered appsettings+env case. Confirmed live: the rebuilt server logs
`channels [1]` and `Initialized 18346 monster spawn points (18823 slots)`.

---

## Genuinely N/A here (no behaviour to add single-map)

- `SM_TIMER_REQ` — the world-driven tick is a no-op; the map's own 1-second `PeriodicTimer` already ticks.
- `SM_DELSESSION_REQ` / `SM_QUITSERVICE_REQ` — session teardown is handled by the socket lifecycle; a
  clean world-driven quit is a shutdown signal with no extra state to unwind single-map.
- Cross-map-server routing (`MW_ROUTE_ACK` neighbour list, `MW_ADDCONNECT`, `MW_MAPSVRLIST`) is inert in a
  single-server deployment.

## Not ported — not in these sources

- **`RW_*` relay plane** — there is no `TRelaySvr` in this release (removed on user request), so the relay
  handoff (`MW_RELAYCONNECT_REQ` etc.) has no peer and is intentionally not ported, exactly as in
  `TWorldSvr.Net`.
