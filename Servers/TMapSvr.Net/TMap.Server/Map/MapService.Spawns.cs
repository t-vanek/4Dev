using Microsoft.Extensions.Logging;
using TMap.Data;

namespace TMap.Server.Map;

/// <summary>
/// The monster spawn pipeline — the C# port of the C++ startup spawn wiring
/// (<c>TMapSvr.cpp</c> SE_DEFAULT loop → <c>CTMap::AddMonSpawn</c> → <c>InitMonster</c>) plus the per-slot
/// regen (<c>CTAICmdRegen::ExecAI</c>, TAICmdRegen.cpp:30). At bring-up each auto-spawn point gets its
/// <c>Count</c> monster slots (one per channel); on each 1-second tick an empty, due slot rolls the spawn
/// probability and — on success — picks a monster type (weighted by prob), resolves its template + level-attr,
/// scatters a position within the spawn radius, sets HP/MP to max, and makes it visible via
/// <see cref="SpawnMonster"/>.
///
/// <para>Deferred (documented — PORT_STATUS.md): the <c>TSVRCHART</c> multi-machine server/unit topology
/// filter on the spawn load (this single-server port loads all rows); the leader-cluster and group-order
/// spawn branches (need live AI / <c>OS_WAKEUP</c>); linked-spawn kill (<c>m_wRegenDelSpawn</c>) and the
/// occupation-zone country override (<c>m_wLocalID</c>); the initial-delay fidelity is approximate (slots
/// use the spawn <c>Delay</c> off the map clock). Because monster death/combat is deferred, a filled slot
/// never empties — so the respawn cycle only ever runs its first (initial) regen. The RNG is .NET
/// <see cref="System.Random"/> (not C <c>rand()</c>): the selection/scatter LOGIC is exact, the concrete
/// values are not — and were never wire-comparable to a C++ instance regardless.</para>
/// </summary>
public sealed partial class MapService
{
    // SPAWN_EVENT (TMapType.h:371): SE_DEFAULT auto-spawns at load; SE_QUEST/SE_QUESTDEL are quest-added
    // time-limited spawns; SE_DYNAMIC is a quest Regen one-shot replacement (one-shot: never re-arms, auto-frees
    // + recycles its id on removal).
    private const byte SeDefault = 0, SeQuest = 2, SeQuestDel = 3, SeDynamic = 4;
    private const byte TContryN = 3;   // TCONTRY_N — the neutral country a quest-spawned monster takes

    private readonly List<SpawnPoint> _spawns = new();

    // Dynamic spawn templates minted by the quest Regen subtype (C++ RegenDynamicMonster + m_mapExtraSpawnID),
    // keyed by a recycled id drawn from a reserved range above the chart spawn ids.
    private readonly Dictionary<ushort, MonsterSpawnDef> _dynamicSpawns = new();
    private readonly Stack<ushort> _dynamicIdFree = new();
    private int _dynamicIdNext = -1;   // lazily seeded to max(chart spawn id) + 1

    /// <summary>The spawn template for an id — the chart templates (C++ <c>m_mapTMONSPAWN</c>) plus the
    /// runtime dynamic templates. Used by the quest SpawnMon/DieMon/Regen subtypes.</summary>
    private MonsterSpawnDef? SpawnById(ushort id)
    {
        if (_dynamicSpawns.TryGetValue(id, out var dyn)) return dyn;
        foreach (var def in _templates.MonsterSpawns) if (def.Spawn.Id == id) return def;
        return null;
    }

    /// <summary>C++ <c>CTMapSvrModule::RegenDynamicMonster</c> (TMapSvr.cpp:11866) — mint a fresh one-shot
    /// <c>SE_DYNAMIC</c> spawn template (prob 100 / count 1 / range 0 / delay 0) for a single monster kind at a
    /// position, from a recycled reserved id. Returns the new spawn id, or 0 if the monster kind is unknown or
    /// the id pool is exhausted (<c>m_mapExtraSpawnID.empty()</c>). The caller instantiates it with
    /// <see cref="AddTimelimitedMon"/>. (<paramref name="roamType"/> is the term count — accepted for parity but
    /// inert here, as the port's roam uses the spawn range.)</summary>
    public ushort RegenDynamicMonster(ushort mapId, byte country, ushort monId, float x, float y, float z, byte roamType, uint delay = 0)
    {
        if (!_templates.MonsterTemplates.ContainsKey(monId)) return 0;   // C++ FindTMonster must resolve

        if (_dynamicIdNext < 0)   // seed the reserved pool above the chart ids
        {
            int max = 0;
            foreach (var def in _templates.MonsterSpawns) max = Math.Max(max, def.Spawn.Id);
            _dynamicIdNext = max + 1;
        }
        ushort id;
        if (_dynamicIdFree.Count > 0) id = _dynamicIdFree.Pop();
        else if (_dynamicIdNext <= ushort.MaxValue) id = (ushort)_dynamicIdNext++;
        else return 0;   // pool exhausted

        _dynamicSpawns[id] = new MonsterSpawnDef(
            new MonSpawnRow(Id: id, MapId: mapId, PosX: x, PosY: y, PosZ: z, Dir: 0, Country: country,
                Count: 1, Range: 0, Prob: 100, Region: 0, Delay: delay, Event: SeDynamic),
            new List<MapMonRow> { new(SpawnId: id, MonId: monId, Leader: 0, Essential: 0, Prob: 100) });
        return id;
    }

    /// <summary>The RNG for the regen prob roll, weighted type-pick, and radius scatter. Defaults to a fresh
    /// <see cref="System.Random"/>; tests assign a seeded instance for determinism.</summary>
    public Random SpawnRng { get; set; } = new();

    private sealed class SpawnPoint
    {
        public required MonsterSpawnDef Def;
        public required byte Channel;
        public required SpawnSlot[] Slots;
        public bool Removed;   // set by DelMonSpawn so a regen sweep already in flight skips it
        /// <summary>C++ <c>AddTimelimitedMon</c>'s <c>bCountry</c> — the country its monsters fight for, over the spawn's own.</summary>
        public byte? Country;
        /// <summary>C++ <c>MONSPAWN_SUSPEND</c> (a territory at war): a dead monster does not come back, and its death is a
        /// battle-zone event (MapService.Mission.cs).</summary>
        public bool Suspended;
    }

    private sealed class SpawnSlot
    {
        public byte Index;
        public Monster? Live;      // occupied while the monster is alive (no death yet ⇒ stays filled)
        public long NextRegenMs;   // earliest map-clock tick (ms) this empty slot may regen

        /// <summary>C++ <c>CTMonster::m_wRegenDelSpawn</c> (set by quest Regen): a dynamic spawn to force-kill
        /// when this slot's monster next respawns. Stashed on the slot (not the monster) so it survives the
        /// death→respawn cycle. 0 = none.</summary>
        public ushort RegenDelSpawn;
    }

    /// <summary>Builds the auto-spawn (SE_DEFAULT) points at bring-up — one per channel, each with its
    /// spawn's <c>Count</c> slots (C++ <c>InitMonster</c>, RT_ETERNAL). The first regen of each slot is
    /// scheduled at the spawn's <c>Delay</c>. Idempotent (safe to call again — it rebuilds).</summary>
    public void InitMonsterSpawns()
    {
        _spawns.Clear();
        var channels = _opt.Channels is { Length: > 0 } ? _opt.Channels : new byte[] { 1 };
        foreach (var def in _templates.MonsterSpawns)
        {
            if (def.Spawn.Event != SeDefault) continue;
            foreach (var ch in channels)
            {
                var slots = new SpawnSlot[def.Spawn.Count];
                for (byte i = 0; i < def.Spawn.Count; i++)
                    slots[i] = new SpawnSlot { Index = i, NextRegenMs = def.Spawn.Delay };
                _spawns.Add(new SpawnPoint { Def = def, Channel = ch, Slots = slots });
            }
        }
        if (_spawns.Count > 0)
            _log.LogInformation("Initialized {Spawns} monster spawn points ({Slots} slots).",
                _spawns.Count, _spawns.Sum(s => s.Slots.Length));
    }

    /// <summary>The regen tick (C++ <c>CTAICmdRegen::ExecAI</c>): each empty, due slot rolls the spawn
    /// probability; on success it spawns a monster, on failure (or missing chart data) it reschedules one
    /// delay later.</summary>
    public void RunMonsterRegen(long nowMs)
    {
        // Snapshot: filling an original's slot may fire its regen-del link, which DelMonSpawns a dynamic spawn
        // (mutating _spawns). Skip any spawn removed earlier this sweep. The snapshot buffer is reused and the
        // removal check is a flag: a fresh ToList + List.Contains per spawn was O(n²) — ~235 ms per tick with
        // 18k spawn points — plus a large-object-heap allocation every second.
        int count = _spawns.Count;
        if (_regenSnapshot.Length < count) _regenSnapshot = new SpawnPoint[count + count / 4];
        _spawns.CopyTo(_regenSnapshot);
        for (int i = 0; i < count; i++)
        {
            var sp = _regenSnapshot[i];
            if (sp.Removed) continue;
            foreach (var slot in sp.Slots)
            {
                if (slot.Live is not null || nowMs < slot.NextRegenMs) continue;
                // Prob-gate the regen, then fill; on a failed roll OR missing chart data, reschedule one delay on.
                if (!TryFillSlot(sp, slot, nowMs, rollProb: true)) slot.NextRegenMs = nowMs + sp.Def.Spawn.Delay;
            }
        }
        Array.Clear(_regenSnapshot, 0, count);   // don't keep removed spawn points alive until the next tick
    }

    private SpawnPoint[] _regenSnapshot = Array.Empty<SpawnPoint>();

    /// <summary>Fills one empty spawn slot with a monster (C++ regen body / <c>InitMonster</c>): optionally
    /// prob-gated, then a weighted type-pick, level-attr resolve, radius scatter, and <see cref="SpawnMonster"/>.
    /// Returns false (nothing spawned) on a failed prob roll or missing chart data.</summary>
    private bool TryFillSlot(SpawnPoint sp, SpawnSlot slot, long nowMs, bool rollProb)
    {
        var s = sp.Def.Spawn;
        if (rollProb && SpawnRng.Next(100) >= s.Prob) return false;   // C++ rand() % 100 < m_bProb

        var type = PickMonsterType(sp.Def.Types);
        if (type is null
            || !_templates.MonsterTemplates.TryGetValue(type.MonId, out var tpl)
            || _templates.MonAttr(tpl.MonAttr, tpl.Level) is not { } attr)   // C++ returns FALSE on null attr
            return false;

        var (x, z) = PickSpawnPosition(s);
        var m = new Monster
        {
            Id = Monster.MakeId(s.Id, sp.Channel, slot.Index),
            ChartId = tpl.Id,
            Level = tpl.Level,
            Attr = attr,
            MaxHp = attr.MaxHp, Hp = attr.MaxHp,
            MaxMp = attr.MaxMp, Mp = attr.MaxMp,
            DefendPower = attr.DefendPower,
            AtkMin = attr.AtkMin, AtkMax = attr.AtkMax, AtkSpeed = attr.AtkSpeed,
            MinWap = attr.MinWap, MaxWap = attr.MaxWap, Wdp = attr.Wdp,
            MagicDefPower = attr.MagicDefPower, DefendLevel = attr.DefendLevel,    // combat quality
            MagicDefLevel = attr.MagicDefLevel, CritProb = attr.CritProb, AttackLevel = attr.AttackLevel,
            Exp = tpl.Exp, MoneyProb = tpl.MoneyProb, MinMoney = tpl.MinMoney, MaxMoney = tpl.MaxMoney,
            ItemProb = tpl.ItemProb, DropCount = tpl.DropCount,        // item loot
            MaxWeight = tpl.MaxWeight, DropRows = tpl.DropRows,
            PosX = x, PosY = s.PosY, PosZ = z,
            StartX = x, StartY = s.PosY, StartZ = z,   // roam anchor
            Area = s.Area, ChaseRange = tpl.ChaseRange, RoamNextMs = nowMs + RoamDelayMs,
            Dir = s.Dir, Mode = 0,               // MT_NORMAL
            Country = sp.Country ?? s.Country, Region = s.Region,
            Channel = sp.Channel, MapId = s.MapId,
        };

        // Resolve the AI script (C++ pMON->m_pAI = FindTMonsterAI(bAIType), with the DEFAULT_AI
        // fallback) and derive auto-aggro from it — a monster is "aggressive" iff its script binds AC_SETHOST
        // under AT_ENTER. With no chart loaded both stay null/false and the monster runs the legacy sweep.
        m.Ai = _templates.AiScriptFor(tpl.AiType);
        m.Aggressive = m.Ai?.IsAggressive ?? false;

        slot.Live = m;
        SpawnMonster(m);

        // C++ CTMonster::Initialize (TMonster.cpp:468) — a freshly placed monster runs its AT_DELETE/0 entry,
        // which is where a script arms its standing behaviour (the roam loop, the host scan).
        OnAiEvent(m, AiTrigger.Delete);

        // C++ CTAICmdRegen link (TAICmdRegen.cpp:172): when the monster that a quest Regen tagged respawns,
        // force-remove the temporary dynamic spawn it created.
        if (slot.RegenDelSpawn != 0)
        {
            DelMonSpawn(slot.RegenDelSpawn, sp.Channel);
            slot.RegenDelSpawn = 0;
        }
        return true;
    }

    /// <summary>C++ <c>CTMap::AddTimelimitedMon</c> (TMap.cpp:454) — the quest SpawnMon path: instantiate a live
    /// spawn point from a template id and spawn its monsters immediately (no prob gate). The spawn then persists
    /// + regens on death like an SE_DEFAULT one (RT_TIMELIMIT ≡ RT_ETERNAL — the C++ auto-expire is commented
    /// out), until an explicit <see cref="DelMonSpawn"/>. Returns false if the template is unknown or a live
    /// non-empty spawn for that (id, channel) already exists (C++ de-dup guard, TMap.cpp:484). <paramref
    /// name="regenType"/> is the term's REGEN_TYPE — accepted for parity but behaviorally inert here.</summary>
    public bool AddTimelimitedMon(ushort spawnId, byte channel, byte regenType, long nowMs, byte? country = null)
    {
        if (SpawnById(spawnId) is not { } def) return false;
        // De-dup: refuse if a live spawn for this (id, channel) already has monsters out.
        if (_spawns.Any(p => p.Def.Spawn.Id == spawnId && p.Channel == channel && p.Slots.Any(sl => sl.Live is not null)))
            return false;

        var existing = _spawns.FirstOrDefault(p => p.Def.Spawn.Id == spawnId && p.Channel == channel);
        var sp = existing ?? new SpawnPoint
        {
            Def = def, Channel = channel,
            Slots = BuildSlots(def.Spawn.Count, nowMs),
        };
        if (existing is null) _spawns.Add(sp);
        if (country is not null) sp.Country = country;
        foreach (var slot in sp.Slots) { if (slot.Live is null) TryFillSlot(sp, slot, nowMs, rollProb: false); }
        return sp.Slots.Any(sl => sl.Live is not null);
    }

    /// <summary>C++ <c>CTMap::DelMonSpawn</c> (TMap.cpp:441) — the quest QTT_SPAWNID_DEL path: hard-remove a live
    /// spawn and all its monsters immediately (each broadcasting <c>CS_DELMON_ACK</c>, no death anim / no re-arm).</summary>
    public void DelMonSpawn(ushort spawnId, byte channel)
    {
        foreach (var sp in _spawns.Where(p => p.Def.Spawn.Id == spawnId && p.Channel == channel).ToList())
        {
            foreach (var slot in sp.Slots)
                if (slot.Live is { } m) { DespawnMonster(m, exitMap: true); slot.Live = null; }
            sp.Removed = true;
            _spawns.Remove(sp);
        }
        // Recycle a dynamic (SE_DYNAMIC) spawn's reserved id once no channel still holds it.
        if (_dynamicSpawns.ContainsKey(spawnId) && !_spawns.Any(p => p.Def.Spawn.Id == spawnId))
        {
            _dynamicSpawns.Remove(spawnId);
            _dynamicIdFree.Push(spawnId);
        }
    }

    private static SpawnSlot[] BuildSlots(byte count, long nowMs)
    {
        var slots = new SpawnSlot[count];
        for (byte i = 0; i < count; i++) slots[i] = new SpawnSlot { Index = i, NextRegenMs = nowMs };
        return slots;
    }

    /// <summary>Enumerates the live monsters of a spawn on a (channel) — the port equivalent of the C++
    /// <c>CTMonSpawn::m_vTMON</c> (used by the DieMon force-kill). <paramref name="questDel"/> reports whether
    /// the spawn is an <c>SE_QUESTDEL</c> one (silent removal, no reward).</summary>
    private IEnumerable<(SpawnPoint Sp, SpawnSlot Slot, Monster Mon)> LiveMonstersOfSpawn(ushort spawnId, byte channel)
    {
        foreach (var sp in _spawns.Where(p => p.Def.Spawn.Id == spawnId && p.Channel == channel))
            foreach (var slot in sp.Slots)
                if (slot.Live is { } m) yield return (sp, slot, m);
    }

    /// <summary>C++ <c>CQuestDieMon</c> force-kill of every live monster of a spawn. An <c>SE_QUESTDEL</c> spawn
    /// is removed silently (C++ <c>m_bRemove</c> + <c>OnDie(0,OT_NONE,0)</c> with no keeper): <c>CS_DIE_ACK</c> +
    /// despawn, no exp/loot, and the slot never re-arms. Any other spawn is a credited kill — the quester is
    /// stamped as keeper and the monster runs the normal <see cref="OnMonsterDeath"/> (exp/loot/kill-quest + the
    /// usual corpse/re-arm).</summary>
    /// <summary>Stashes a quest-Regen link (C++ <c>m_wRegenDelSpawn</c>) on the killed monster's spawn slot, so
    /// that slot's next respawn force-removes the dynamic spawn. No-op if the killed monster isn't a spawn-slot
    /// monster (the dynamic spawn then simply lives until killed).</summary>
    private void LinkRegenDel(uint monId, byte channel, ushort delSpawnId)
    {
        ushort spawnId = (ushort)(monId >> 16);
        byte slotIdx = (byte)(monId & 0xFF);
        var sp = _spawns.FirstOrDefault(p => p.Def.Spawn.Id == spawnId && p.Channel == channel);
        if (sp is not null && slotIdx < sp.Slots.Length) sp.Slots[slotIdx].RegenDelSpawn = delSpawnId;
    }

    private void ForceKillSpawn(Character ch, ushort spawnId, byte channel)
    {
        foreach (var (sp, slot, mon) in LiveMonstersOfSpawn(spawnId, channel).ToList())
        {
            if (sp.Def.Spawn.Event == SeQuestDel)
            {
                foreach (var p in _state.PlayersAround(mon)) SendCS_DIE_ACK(p, mon.Id, Monster.OtMon);
                DespawnMonster(mon);
                slot.Live = null;
                slot.NextRegenMs = long.MaxValue;   // C++ m_bRemove ⇒ AT_TIMEOUT ⇒ never re-arms
            }
            else
            {
                mon.Hp = 0;
                mon.AddDamage(ch.CharId, ch.GetPartyId(), mon.MaxHp);   // credit the keeper (party if partied; crosses 10%)
                OnMonsterDeath(mon);                    // exp/loot/CS_DIE + corpse or re-arm
            }
        }
    }

    /// <summary>Frees a dead monster's spawn slot and schedules its respawn one <c>Delay</c> later (C++
    /// <c>CTAICmdLeave</c> setting <c>OS_DISAPPEAR</c> + <c>CTAICmdRegen</c>'s delay). The monster's instance
    /// id decodes to <c>(spawnId, channel, slot)</c>. No-op for monsters not owned by a spawn (API-spawned).</summary>
    /// <summary>The spawn point a monster id belongs to (<see cref="Monster.MakeId"/>: spawn · channel · slot).</summary>
    private SpawnPoint? SpawnOf(uint monsterId)
    {
        ushort spawnId = (ushort)(monsterId >> 16);
        byte channel = (byte)((monsterId >> 8) & 0xFF);
        return _spawns.FirstOrDefault(p => p.Def.Spawn.Id == spawnId && p.Channel == channel);
    }

    private void RearmSpawnSlot(uint monsterId, long nowMs)
    {
        ushort spawnId = (ushort)(monsterId >> 16);
        byte channel = (byte)((monsterId >> 8) & 0xFF);
        byte slotIdx = (byte)(monsterId & 0xFF);
        var sp = SpawnOf(monsterId);
        if (sp is null || slotIdx >= sp.Slots.Length) return;

        // A dynamic (quest Regen) spawn is one-shot: on its monster's removal, free the whole spawn + recycle its
        // id (C++ SE_DYNAMIC → TAICmdRemove::DelMonSpawn) rather than re-arm.
        if (sp.Def.Spawn.Event == SeDynamic) { DelMonSpawn(spawnId, channel); return; }

        sp.Slots[slotIdx].Live = null;
        sp.Slots[slotIdx].NextRegenMs = sp.Suspended ? long.MaxValue : nowMs + sp.Def.Spawn.Delay;   // suspended: until released
    }

    /// <summary>C++ weighted pick over the non-essential types by <c>m_bProb</c> (TAICmdRegen.cpp:51-101):
    /// <c>roll = rand() % Σprob</c>, then the first type whose running total exceeds the roll.</summary>
    private MapMonRow? PickMonsterType(List<MapMonRow> types)
    {
        int total = 0;
        foreach (var t in types) if (t.Essential == 0) total += t.Prob;
        int roll = total > 0 ? SpawnRng.Next(total) : 0;
        int acc = 0;
        foreach (var t in types)
            if (t.Essential == 0)
            {
                acc += t.Prob;
                if (roll < acc) return t;
            }
        return null;
    }

    /// <summary>C++ position pick (TAICmdRegen.cpp:146-158): a random point within <c>Range</c> of the anchor
    /// (<c>len = rand()%Range</c>, <c>rad = (rand()%360)·π/180</c>), else the anchor exactly.</summary>
    private (float x, float z) PickSpawnPosition(MonSpawnRow s)
    {
        if (s.Range == 0) return (s.PosX, s.PosZ);
        float len = SpawnRng.Next(s.Range);
        float rad = SpawnRng.Next(360) * (float)Math.PI / 180f;
        return (s.PosX + len * MathF.Cos(rad), s.PosZ + len * MathF.Sin(rad));
    }
}
