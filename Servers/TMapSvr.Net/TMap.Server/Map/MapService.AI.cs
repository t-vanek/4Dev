using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Monster AI — the idle-roam slice (C++ <c>CTAICmdRoam::ExecAI</c>, TAICmdRoam.cpp:30). On its per-monster
/// roam timer, an idle (<c>MT_NORMAL</c>, alive) monster that at least one player can see picks a wander
/// destination within its spawn radius and broadcasts <c>CS_MONACTION_ACK</c> (the destination + action
/// verb) to the players in view — exactly the packet the C++ AI emits. A monster no one can see stays
/// dormant (C++ roam requires a host, else fires <c>AT_LEAVE</c>).
///
/// <para><b>Faithful-to-wire, with documented approximations (PORT_STATUS.md):</b> this ports the roam
/// <b>broadcast</b> 1:1; the roam parameters that live in charts / the AI-command table I haven't loaded
/// (<c>m_bArea</c>, <c>m_bRoamType</c>, <c>m_bRoamProb</c>, the per-command roam delay) are approximated by
/// the <b>spawn Range</b> (radius), a fixed <b>WALK</b> action, and a constant roam interval
/// (<c>base + rand()%(4·base)</c>, matching the C++ jitter shape). The server keeps the monster's
/// authoritative position at the anchor: the <b>client-authoritative</b> position echo (C++
/// <c>CS_MONMOVE_REQ</c> → <c>CS_MONMOVE_ACK</c>) that actually advances <c>m_fPos</c> is deferred, so the
/// client animates the wander while the server grid cell (visibility) is computed from the anchor.</para>
///
/// <para><b>Deferred (slice 2 — documented):</b> the whole BATTLE branch — aggro accrual (<c>SetAggro</c>/
/// <c>m_mapAggro</c>), host lifecycle (<c>SetHost</c>/<c>ChkHost</c>/<c>ChgHost</c>/<c>m_dwHostKEY</c>),
/// <c>ChgMode</c> state machine, chase (<c>CTAICmdFollow</c>) + <c>m_wChaseRange</c> leash, the monster
/// attack (<c>CTAICmdBeginAtk</c>/<c>CTAICmdAttack</c> → <c>CS_MONATTACK_ACK</c> + monster→player
/// <c>CS_DEFEND</c>), <c>MT_GOHOME</c> return, pack/leader/patrol movement, and call-for-help / flee.</para>
/// </summary>
public sealed partial class MapService
{
    /// <summary>Base roam-step interval (ms). Approximates the AI-command-table delay; the actual wait is
    /// <c>RoamDelayMs + rand()%(4·RoamDelayMs)</c> (C++ <c>m_dwDelay + rand()%(ROAM_DELAY_BOUND·m_dwDelay)</c>,
    /// ROAM_DELAY_BOUND = 4).</summary>
    private const int RoamDelayMs = 5000;
    private const int ChaseIntervalMs = 1000;  // how often a chasing monster re-broadcasts its follow target
    private const float AttackRange = 50f;     // melee reach: within this of the anchor the monster attacks instead of chasing
    private const int DefaultAtkSpeedMs = 2000; // attack cadence when the attr has no m_dwAtkSpeed
    private const byte TaWalk = 3, TaFollow = 9; // TACTION_TYPE TA_WALK / TA_FOLLOW

    /// <summary>The per-tick monster AI sweep: a monster in <c>MT_BATTLE</c> chases its aggro target
    /// (<c>CTAICmdFollow</c>); an idle <c>MT_NORMAL</c> monster roams (<c>CTAICmdRoam</c>). Public so tests
    /// can drive it at a chosen <paramref name="nowMs"/>.</summary>
    public void RunMonsterAI(long nowMs)
    {
        foreach (var mon in _state.AllMonsters())
        {
            if (mon.Hp == 0) continue;                          // dead corpse — no AI
            // A monster with a TAICHART script is driven entirely by the event machine and its
            // scheduled commands (RunScheduledAi), never by this sweep. Only script-less monsters — the
            // DB-free path and every pre-46 test — fall back to the hard-coded behaviour below.
            if (mon.Ai is not null) continue;
            if (mon.Mode == MtBattle) { ChaseTarget(mon, nowMs); continue; }
            if (mon.Mode != MtNormal) continue;
            // An aggressive monster looks for a host on sight (C++ AT_ENTER → CTAICmdSetHost); on a pull it enters
            // battle here and chases/attacks from the next tick. A passive monster (the default) just roams.
            if (mon.Aggressive && TryAcquireHost(mon, (uint)nowMs)) continue;
            if (mon.Area > 0) Roam(mon, nowMs);
        }
    }

    /// <summary>C++ <c>CTAICmdRoam::ExecAI</c> — an idle monster a player can see wanders within its radius.</summary>
    private void Roam(Monster mon, long nowMs)
    {
        if (nowMs < mon.RoamNextMs) return;

        var viewers = _state.PlayersAround(mon).ToList();
        if (viewers.Count == 0)                       // no host/observer ⇒ dormant (C++ AT_LEAVE)
        {
            mon.RoamNextMs = nowMs + RoamDelayMs;     // re-check later
            return;
        }

        var (nx, nz) = RoamDestination(mon);
        mon.NextX = nx;
        mon.NextZ = nz;
        foreach (var p in viewers) SendCS_MONACTION_ACK(p, mon.Id, TaWalk, nx, mon.StartY, nz);

        mon.RoamNextMs = nowMs + RoamDelayMs + SpawnRng.Next(4 * RoamDelayMs); // C++ delay + rand()%(4·delay)
    }

    /// <summary>C++ <c>CTAICmdChgMode</c>/<c>CTAICmdFollow</c> — a battle monster chases its aggro target,
    /// re-broadcasting <c>CS_MONACTION_ACK</c> (<c>TA_FOLLOW</c>) toward the target; it drops aggro (→ NORMAL,
    /// re-enabling HP regen) when the target is gone or has fled past the leash (distance from the anchor).
    /// The monster's authoritative position stays at the anchor (client-authoritative move echo deferred), so
    /// the leash naturally tightens as the target flees.</summary>
    private void ChaseTarget(Monster mon, long nowMs)
    {
        if (ResolveMonTarget(mon) is not { } target)   // a player or one of its summons
        {
            Disengage(mon, mon.TargetId, mon.TargetType != 0 ? mon.TargetType : OtPc, (uint)nowMs); // target gone / dead → re-pick or go home
            return;
        }

        float dx = target.X - mon.StartX, dz = target.Z - mon.StartZ;
        float dist2 = dx * dx + dz * dz;
        // fled past the leash (C++ m_wChaseRange < GetDistance(anchor,pos); the port measures the target's distance
        // from the anchor since the monster is pinned there): drop this target, re-pick the next in-view attacker.
        if (dist2 > mon.ChaseRange * mon.ChaseRange) { Disengage(mon, mon.TargetId, mon.TargetType != 0 ? mon.TargetType : OtPc, (uint)nowMs); return; }

        if (dist2 <= AttackRange * AttackRange) // in melee range ⇒ attack
        {
            if (nowMs >= mon.AtkNextMs)
            {
                AttackTarget(mon, target, nowMs);
                mon.AtkNextMs = nowMs + (mon.AtkSpeed != 0 ? mon.AtkSpeed : DefaultAtkSpeedMs);
            }
            return;
        }

        if (nowMs < mon.RoamNextMs) return; // chase cadence
        foreach (var p in _state.PlayersAround(mon))
            SendCS_MONACTION_ACK(p, mon.Id, TaFollow, target.X, target.Y, target.Z, target.Id, target.Type);
        mon.RoamNextMs = nowMs + ChaseIntervalMs;
    }

    /// <summary>C++ <c>CTAICmdBeginAtk</c>/<c>CTAICmdAttack</c> — the monster's swing <b>announce</b>, and nothing
    /// more. The server does not deal the damage here: the host client animates the swing and, when it lands,
    /// reports it with <c>CS_DEFEND_REQ</c> (attacker = the monster), which <see cref="MonsterHitsPlayer"/>
    /// resolves. Dealing it at the announce hit players at any distance — even running away — and ahead of the
    /// animation.</summary>
    private void AttackTarget(Monster mon, MonTarget target, long nowMs)
    {
        // C++ BeginAtk/Attack only swing when the monster has an m_pNextSkill, and announce that skill's id. A
        // monster's basic attack is one of its chart skills (there is no skill 0) — and this client dereferences
        // the skill-chart lookup unguarded in BOTH OnCS_MONATTACK_ACK and OnCS_DEFEND_ACK, so an unknown id
        // does not degrade gracefully, it crashes the client. A monster with no usable skill therefore does
        // not attack, exactly as in the C++.
        ushort skillId = SelectMonsterSkill(mon);
        if (skillId == 0) return;
        _log.LogDebug("[mon] ATTACK mon {Mon} -> {Type}:{Target} skill {Skill} at distance {Dist:F1}.",
            mon.Id, target.Type, target.Id, skillId,
            MathF.Sqrt((mon.PosX - target.X) * (mon.PosX - target.X) + (mon.PosZ - target.Z) * (mon.PosZ - target.Z)));

        // CS_MONATTACK_ACK goes to the HOST alone (C++ pHOST->SendCS_MONATTACK_ACK): it is an instruction, not
        // a broadcast — the host answers with CS_SKILLUSE_REQ for the monster, whose ACK animates everyone.
        // Broadcasting it made every nearby client send that request, playing the swing once per viewer. A
        // monster with no assigned host (the script-less path) is hosted by the player it is attacking.
        _state.FindByChar(mon.HostId != 0 ? mon.HostId : target.OwnerId)?.Send(BuildMonsterAttackAck(mon, target.Id, target.Type, skillId));
    }

    /// <summary>The landed half of a monster swing: C++ <c>OnCS_DEFEND_REQ</c> with <c>bAttackType == OT_MON</c>
    /// (CSHandler.cpp:1438) → <c>CTPlayer::Defend</c>. The reporting client becomes the host; the power, crit rate
    /// and attack level are the monster's own, never the client's. The AP−DP roll uses the monster's
    /// <c>GetMinAP</c>/<c>GetMaxAP</c> band vs the player's <c>GetDefendPower</c>; the player enters battle
    /// (suppressing HP regen) and, at 0 HP, dies.</summary>
    private void MonsterHitsPlayer(Monster mon, Character target, uint hostId, ushort skillId, byte skillLevel,
        uint actId, uint aniId, float atkX, float atkY, float atkZ, float defX, float defY, float defZ, long nowMs,
        MonsterHitEcho? echo = null)
    {
        var e = echo ?? MonsterHitEcho.Default;
        uint hpBefore = target.Hp;
        // C++ order: GetAtkHitType (attacker side) decides miss/normal/crit FIRST; then Defend→CalcDamage runs
        // the DEFENDER's shield-block roll (GetShieldDP), and only on a landed hit (a miss short-circuits before
        // CalcDamage, so it can never become a block). A successful roll returns the shield's defence power,
        // which is ADDED to normal defence (additive reduction — the 5/7 floor still holds) and flags HT_BLOCK.
        byte hitType = HitTypeVsPlayer(CombatRng, mon.GetCritProb(), mon.GetAttackLevel());
        uint shieldDp = hitType == HtMiss ? 0u : StatEngine.ShieldBlockDp(target, CombatRng, _templates); // physical melee
        uint dp = StatEngine.DefendPower(target, _templates) + shieldDp;
        int a = Math.Max((int)(mon.GetMinAp() - dp), 5);          // C++ CalcDamage floors (min 5 / max 7)
        int b = Math.Max((int)(mon.GetMaxAp() - dp), 7);

        uint baseDmg = hitType switch
        {
            HtMiss => 0u,
            HtCritical => CritDamage(CombatRng, _templates.Formula(FtypePcd), (uint)b), // physical crit off the max band
            _ => (uint)(a + CombatRng.Next(Math.Max(b - a, 1))),
        };
        if (hitType != HtMiss && _state.FindByChar(target.CharId) is { } shared)
            baseDmg -= DistributeSkill(shared, target, baseDmg);   // C++ CalcDamage: dwValue -= DistributeSkill(dwValue)
        uint dmg = (uint)Math.Min((int)baseDmg, (int)target.Hp); // clamp to remaining HP
        if (hitType != HtMiss) target.Hp -= dmg;
        if (dmg != 0 && target.Hp != 0 && _state.FindByChar(target.CharId) is { } hurt) CheckSoulHp(hurt, target);   // C++ OnDamage
        // C++ Defend: the monster's hostile skill, hit or miss, ends the player's buffs that stop on being hit.
        if (_templates.Skills.TryGetValue(skillId, out var hostile) && hostile.IsNegative && _state.FindByChar(target.CharId) is { } hit)
            EraseBuffByDefend(target.MaintainSkills, hostile, i => EraseMaintainPlayer(hit, target, i));
        target.EnterBattle((uint)nowMs, RecoverInit);         // player enters battle ⇒ HP regen suppressed

        // The reported hit result: a kill downgrades to HT_LASTHIT (C++ Defend `m_dwHP ? bAtkHit : HT_LASTHIT`);
        // otherwise a successful shield roll flags HT_BLOCK (set last in CalcDamage, so it wins over a crit report).
        byte atkHit = hitType == HtMiss ? HtMiss
            : target.Hp == 0 ? HtLastHit
            : shieldDp != 0 ? HtBlock
            : hitType;
        var hitAck = BuildMonsterHitAck(mon, target.CharId, OtPc, dmg, atkHit, landed: hitType != HtMiss, skillId, skillLevel,
            hostId, actId, aniId, atkX, atkY, atkZ, defX, defY, defZ, e);
        // C++ Defend: CS_HPMP_ACK only when the hit changed something (bHPMP), with MP shown as 0 on a kill.
        bool hpmp = target.Hp != hpBefore;
        foreach (var p in _state.PlayersAround(mon))
        {
            p.Send(hitAck);
            if (hpmp) SendSelfHpMp(p, target.CharId, MaxHpFor(target), target.Hp, MaxMpFor(target), target.Hp != 0 ? target.Mp : 0);
        }

        if (hitType != HtMiss && target.Hp == 0) // player death — CS_DIE_ACK
        {
            PlayerDied(target, _state.PlayersAround(mon));
            DropAggro(mon, nowMs); // the corpse isn't a target — leave battle + clear the hate table
        }
    }

    /// <summary>C++ <c>CTPlayer::OnDie</c>, the part the port models: <c>CS_DIE_ACK</c> to <paramref name="viewers"/>, the
    /// player's summons and companion die with it, and every non-static buff is dropped silently
    /// (<c>ReleaseMaintain(FALSE)</c>).</summary>
    private void PlayerDied(Character target, IEnumerable<ClientSession> viewers)
    {
        _log.LogWarning("Player death char {Char} on map {Map}, HP {Hp}, position ({X},{Y},{Z}).", target.CharId, target.MapId, target.Hp, target.PosX, target.PosY, target.PosZ);
        foreach (var p in viewers) SendCS_DIE_ACK(p, target.CharId, OtPc);
        if (_state.FindByChar(target.CharId) is not { } dead) return;
        CastleOnDeath(target);                                                  // a carried god ball falls; a kill point (MapService.Castle.cs)
        RecallsOwnerDied(dead, target);
        CompanionOwnerDied(dead, target);
        ReleaseMaintainPlayer(dead, target, notify: false);
        ArenaOnDeath(target);                                                   // a side wiped out loses (MapService.Arena.cs)
        TournamentOnDeath(target);                                              // a kill point for the other side (MapService.TournamentMatch.cs)
    }

    /// <summary>
    /// C++ <c>CTMonster::SelectSkill</c> (TMonster.cpp:1461), reduced to the guarantee the wire needs: the first
    /// of the monster's chart skills (<c>wSkill1..wSkill4</c>) that exists in the skill chart, or 0 when it has
    /// none. Live data: 3518 of 3536 monsters carry a real <c>wSkill1</c> (2447 of them skill 700, the generic
    /// melee), 8 have no skill at all, and 1 names a skill missing from <c>TSKILLCHART</c> — which the existence
    /// check skips rather than sending an id that would crash the client.
    /// <para><b>Deferred:</b> the C++ chooses among several skills by target distance against each skill's
    /// <c>m_wMinRange</c>/<c>m_wMaxRange</c> and by reuse readiness; those columns are not loaded, so a
    /// multi-skill monster always opens with its first valid one.</para>
    /// </summary>
    private ushort SelectMonsterSkill(Monster mon)
    {
        if (!_templates.MonsterTemplates.TryGetValue(mon.ChartId, out var tpl)) return 0;
        foreach (var id in tpl.Skills)
            if (_templates.Skills.ContainsKey(id)) return id;
        return 0;
    }

    /// <summary>The <c>CS_DEFEND_REQ</c> fields a monster hit's <c>CS_DEFEND_ACK</c> repeats back unchanged (C++
    /// Defend passes the request's values through; only power, crit and attack level are the monster's own here).</summary>
    internal readonly record struct MonsterHitEcho(byte CanSelect, uint MgMin, uint MgMax, byte AidCountry)
    {
        public static readonly MonsterHitEcho Default = new(1, 0, 0, 0);
    }

    /// <summary>C++ <c>SendCS_MONATTACK_ACK</c> (CSSender.cpp:1208) — the monster's swing announce.</summary>
    private static byte[] BuildMonsterAttackAck(Monster mon, uint targetId, byte targetType, ushort skillId)
    {
        var w = new PacketWriter(Msg.CS_MONATTACK_ACK, capacity: 16);
        w.WriteUInt32(mon.Id);       // dwAttackID
        w.WriteUInt32(targetId);     // dwTargetID
        w.WriteByte(Monster.OtMon);  // bAttackType
        w.WriteByte(targetType);     // bTargetType — a player, or one of its summons
        w.WriteUInt16(skillId);      // wSkillID — must exist in the client's skill chart
        return w.ToArray();
    }

    /// <summary>The hit result for a monster→player attack — the same <c>CS_DEFEND_ACK</c> layout as the
    /// player→monster path, with the monster as attacker and the player as target.</summary>
    private static byte[] BuildMonsterHitAck(Monster mon, uint targetId, byte targetType, uint dmg, byte atkHit, bool landed, ushort skillId,
        byte skillLevel, uint hostId, uint actId, uint aniId,
        float atkX, float atkY, float atkZ, float defX, float defY, float defZ, MonsterHitEcho echo)
    {
        var w = new PacketWriter(Msg.CS_DEFEND_ACK, capacity: 96);
        w.WriteUInt32(mon.Id);        // dwAttackID
        w.WriteUInt32(targetId);      // dwTargetID
        w.WriteByte(Monster.OtMon);   // bAttackType
        w.WriteByte(targetType);      // bTargetType
        w.WriteUInt32(hostId);        // dwHostID — the client that reported the hit (C++ dwHostID = pPlayer->m_dwID)
        w.WriteByte(OtPc);            // bHostType (C++ Defend passes OT_PC)
        w.WriteUInt32(actId);         // dwActID — echoed from the request
        w.WriteUInt32(aniId);         // dwAniID
        w.WriteByte(0);               // bIsMaintain
        w.WriteUInt32(0);             // dwMaintainTick
        w.WriteByte(mon.GetCritProb());    // bHit == bCP (the monster's crit prob)
        w.WriteByte(atkHit);          // bAtkHit (HT_MISS/NORMAL/CRITICAL, HT_LASTHIT on the killing blow)
        w.WriteUInt16(mon.GetAttackLevel()); // wAttackLevel (GetAttackLevel)
        w.WriteByte(mon.Level);       // bAttackerLevel
        w.WriteUInt32(mon.GetMinAp());    // dwPysMinPower
        w.WriteUInt32(mon.GetMaxAp());    // dwPysMaxPower
        w.WriteUInt32(echo.MgMin);    // dwMgMinPower  — the reporter's value, as the C++ echoes it
        w.WriteUInt32(echo.MgMax);    // dwMgMaxPower
        w.WriteByte(echo.CanSelect);  // bCanSelect    — echoed
        w.WriteByte(0);               // bCancelCharge
        w.WriteByte(mon.Country);     // bAttackCountry
        w.WriteByte(echo.AidCountry); // bAttackAidCountry — echoed
        w.WriteUInt16(skillId);       // wSkillID — must exist in the client's skill chart
        w.WriteByte(skillLevel);      // bSkillLevel
        w.WriteUInt16(0);             // wBackSkillID
        w.WriteByte((byte)(landed ? 1 : 0)); // bPerform — 0 on a miss (PERFORM_MISS)
        w.WriteFloat(atkX); w.WriteFloat(atkY); w.WriteFloat(atkZ);   // the request's positions, as the C++ echoes
        w.WriteFloat(defX); w.WriteFloat(defY); w.WriteFloat(defZ);
        if (dmg > 0)
        {
            w.WriteByte(1);               // damage-map count
            w.WriteByte(MtypeDamage);     // key
            w.WriteUInt32((ushort)dmg);   // damage (WORD-truncated, matching the C++)
        }
        else
        {
            w.WriteByte(0);               // empty damage map on a miss
        }
        return w.ToArray();
    }

    /// <summary>Leave battle for good (C++ <c>ResetHost</c>-lite, TMonster.cpp:2392): clear the target/host and
    /// the whole hate table, drop to <c>MT_NORMAL</c> (HP regen resumes), and return to roaming. Used
    /// when no hostile attacker remains in view (the C++ <c>AT_LEAVELB</c> outcome, the port collapsing the
    /// <c>MT_GOHOME</c> walk-back since the monster is pinned at its anchor).</summary>
    private static void DropAggro(Monster mon, long nowMs)
    {
        mon.TargetId = 0;
        mon.TargetType = 0;
        mon.HostId = 0;
        mon.ClearAggro();
        mon.Mode = MtNormal;
        mon.RoamNextMs = nowMs + RoamDelayMs;
    }

    /// <summary>C++ <c>CTMonster::MoveNext</c> for a path-less spawn (TMonster.cpp:1869-1901): a monster that
    /// has drifted outside its roam area walks straight back to its anchor; otherwise it picks a random point
    /// on the circle of radius <see cref="Monster.Area"/> around the anchor (<c>rad = (rand()%360)·π/180</c>).
    /// Path-point spawns and roam types are not loaded.</summary>
    private (float x, float z) RoamDestination(Monster mon)
    {
        float dx = mon.PosX - mon.StartX, dz = mon.PosZ - mon.StartZ;
        if (dx * dx + dz * dz > mon.Area * mon.Area) return (mon.StartX, mon.StartZ);   // bGo: back to the anchor

        float rad = SpawnRng.Next(360) * (float)Math.PI / 180f;
        return (mon.StartX + mon.Area * MathF.Cos(rad), mon.StartZ + mon.Area * MathF.Sin(rad));
    }

    /// <summary>C++ <c>SendCS_MONACTION_ACK</c> (CSSender.cpp:1096) — the AI move/action command: monster id,
    /// action verb, ground destination, and an optional target (0 for a plain roam step).</summary>
    private static void SendCS_MONACTION_ACK(ClientSession p, uint monId, byte action, float x, float y, float z,
        uint targetId = 0, byte targetType = 0)
    {
        var w = new PacketWriter(Msg.CS_MONACTION_ACK, capacity: 24);
        w.WriteUInt32(monId);
        w.WriteByte(action);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        w.WriteUInt32(targetId);
        w.WriteByte(targetType);
        p.Send(w);
    }
}
