using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The data-driven monster AI engine: a port of the C++ <c>TAICHART</c> interpreter
/// (<c>CTMonster::OnEvent</c> → <c>CTMapSvrModule::DoAICMD</c> → the 15 <c>CTAICmd*</c> classes).
///
/// <para>The C++ AI is <b>not</b> a per-tick sweep — it is an <b>event machine</b>. A trigger
/// (<see cref="AiTrigger"/>) fires on the monster; its script (<c>bAIType</c> → <c>TAICHART</c>) maps
/// (trigger, trigger-id) to an ordered command list; each command is guarded by its
/// <c>TAICONCHART</c> conditions plus a hard state gate, runs immediately or after a delay, and on
/// completion fires <see cref="AiTrigger.AiComplete"/> keyed on <b>its own command id</b> — which is how
/// scripts chain (wake → ChgHost → ChgMode → BeginAtk → Attack is data, not code).</para>
///
/// <para><b>Two deliberate divergences from the C++, both documented in PORT_STATUS.md:</b></para>
/// <list type="number">
/// <item><b>The delay hop is local.</b> C++ <c>DoAICMD</c> ships a delayed command to the <i>world</i>
/// server (<c>SendSM_AICMD_REQ</c>, carrying the raw <c>CTAICommand*</c> as an opaque handle) which times it
/// and echoes <c>SM_AICMD_ACK</c> back. That plane exists only because AI scheduling was centralised across
/// map servers; single-map it is pure overhead and a lifetime hazard. Here the pending command sits in a
/// local <see cref="PriorityQueue{TElement,TPriority}"/> keyed on the map clock, drained by
/// <see cref="RunScheduledAi"/>. The <b>epoch check is preserved exactly</b>: a scheduled entry captures
/// <see cref="Monster.HostKey"/> (<c>m_dwHostKEY</c>) and is discarded on wake-up if the monster's key has
/// moved on — that is what stops a stale command from acting on a retargeted/dead monster.</item>
/// <item><b>Scripted monsters bypass the legacy sweep.</b> A monster whose template resolves an
/// <see cref="AiScript"/> is driven entirely by this engine; one with no script (no <c>TAICHART</c> rows —
/// the DB-free path and every existing test) keeps the hard-coded
/// <c>Roam</c>/<c>ChaseTarget</c>/<c>TryAcquireHost</c> sweep. This mirrors the C++
/// <c>FindTMonsterAI(DEFAULT_AI)</c> fallback shape and keeps the server behaving with an empty chart
/// instead of going inert.</item>
/// </list>
///
/// <para><b>Not yet backed by ported state (each command documents its own gap):</b> monster skills
/// (<c>m_pNextSkill</c>/<c>SelectSkill</c>/<c>SkillUse</c>) so <c>BeginAtk</c>/<c>Attack</c> fall back to the
/// melee swing; <c>CheckAttack</c> (silence/root) which reads as always-true; the
/// <c>m_bRoamType == 4</c> fixed-monster gate and <c>m_bIsSelf</c>, whose chart columns are unloaded;
/// <c>Getaway</c>'s <c>m_bCall</c> helper search; and <c>Lottery</c>'s corpse-item roll.</para>
/// </summary>
public sealed partial class MapService
{
    // C++ OBJ_STATUS (TMapType.h:344). The second state axis alongside TMODE_TYPE: nearly every command
    // gates on it, so the engine needs it even though the pre-46 port only tracked Dead as a bool.
    private const byte OsDisappear = 0, OsWakeup = 1, OsSleep = 2, OsDead = 3;

    private const byte MtGohome = 2;         // TMODE_TYPE MT_GOHOME (MtNormal/MtBattle live in MapService.Recover.cs)
    private const int ZoneHomeSize = 3;      // C++ ZONE_HOMESIZE (TMapType.h:72) — the "arrived home" radius
    private const int RoamDelayBound = 4;    // C++ ROAM_DELAY_BOUND — the roam-delay jitter multiplier
    private const byte TaRun = 4;            // TACTION_TYPE TA_RUN (TaWalk/TaFollow live in MapService.AI.cs)

    /// <summary>A command waiting on its delay — the local stand-in for the C++ world round trip. Holds the
    /// monster's <see cref="Monster.HostKey"/> at schedule time so a stale wake-up can be dropped.</summary>
    private readonly record struct PendingAi(
        Monster Mon, AiBinding Binding, uint HostKey, uint EventHost, uint RhId, byte RhType);

    private readonly PriorityQueue<PendingAi, long> _aiQueue = new();

    /// <summary>Pending scheduled AI commands — for tests and the load-summary log.</summary>
    public int PendingAiCount => _aiQueue.Count;

    // ======================================================================================
    // The event machine
    // ======================================================================================

    /// <summary>
    /// C++ <c>CTMonster::OnEvent</c> (TMonster.cpp:447) — fire a trigger on a monster: look the
    /// (trigger, trigger-id) pair up in its script and dispatch every bound command. A monster with no
    /// script, or a pair the script says nothing about, is a silent no-op (the C++ <c>find</c> miss).
    /// </summary>
    public void OnAiEvent(Monster mon, AiTrigger trigger, uint triggerId = 0,
        uint eventHost = 0, uint rhId = 0, byte rhType = 0)
    {
        var bindings = mon.Ai?.Lookup(trigger, triggerId);
        if (bindings is null) return;

        // Chart data can describe a cycle (an AT_AICOMPLETE chain that loops back, or a Defend→ChgHost→
        // Disengage→Defend bounce through a stale aggro table). The C++ survives those by construction — its
        // chains run through the world round trip, so each hop costs a packet. Inline dispatch has no such
        // brake, so cap the synchronous depth and drop the tail rather than blowing the stack.
        if (_aiDepth >= MaxAiDepth) return;
        _aiDepth++;
        try
        {
            // Indexed, not foreach: a command's ExecAI can fire further events (AT_LEAVE, AT_DEFEND,
            // AT_ATHOME…) that mutate this monster — but never this list, which is immutable chart data.
            for (var i = 0; i < bindings.Count; i++)
                DoAiCmd(bindings[i], mon, eventHost, rhId, rhType);
        }
        finally { _aiDepth--; }
    }

    private const int MaxAiDepth = 16;
    private int _aiDepth;

    /// <summary>
    /// C++ <c>CTMapSvrModule::DoAICMD</c> (TMapSvr.cpp:6677) — run a bound command now, or queue it for its
    /// delay. The C++ special-case is preserved: a removal-flagged monster's <c>AC_LEAVE</c> is forced to a
    /// 1 ms delay so it always goes through the scheduler rather than running inline.
    /// </summary>
    private void DoAiCmd(AiBinding binding, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        uint delay = AiGetDelay(binding, mon);
        if (mon.Remove && binding.Command.Kind == AiCommandKind.Leave) delay = 1;

        if (delay != 0)
        {
            _aiQueue.Enqueue(new PendingAi(mon, binding, mon.HostKey, eventHost, rhId, rhType),
                _tickSeconds * 1000L + delay);
            return;
        }

        // Immediate path: the C++ discards ExecAI's return here, so an undelayed command never loops.
        if (AiCanRun(binding, mon, eventHost, rhId, rhType))
            AiExec(binding, mon, eventHost, rhId, rhType);
    }

    /// <summary>
    /// Drain the due scheduled commands — the local replacement for <c>OnSM_AICMD_ACK</c>
    /// (SSHandler.cpp:1159), including its exact revalidation order: epoch match, then <c>CanRun</c>, then
    /// <c>ExecAI</c>, and on a truthy result re-arm through <see cref="DoAiCmd"/> (the <c>bLoop</c> loop).
    /// </summary>
    public void RunScheduledAi(long nowMs)
    {
        // Bounded drain: a zero-delay loop command would otherwise spin forever inside one tick. The C++ is
        // implicitly bounded by the world round trip; GetDelay returning 0 for a loop is already a chart bug.
        var budget = _aiQueue.Count + 64;
        while (budget-- > 0 && _aiQueue.TryPeek(out _, out var due) && due <= nowMs)
        {
            var p = _aiQueue.Dequeue();

            // C++ OnSM_AICMD_ACK: the monster must still exist in the map, and its epoch must not have moved.
            if (p.Mon.HostKey != p.HostKey) continue;
            // The same object, not just the same id: a respawn reuses its slot's id, and a command left over from
            // the dead body (its Leave, retrying while the corpse had loot) would otherwise run on — and despawn —
            // the new monster.
            if (!ReferenceEquals(_state.FindMonster(p.Mon.Id), p.Mon)) continue;

            if (AiCanRun(p.Binding, p.Mon, p.EventHost, p.RhId, p.RhType)
                && AiExec(p.Binding, p.Mon, p.EventHost, p.RhId, p.RhType))
                DoAiCmd(p.Binding, p.Mon, p.EventHost, p.RhId, p.RhType);
        }
    }

    /// <summary>Invalidate every queued command for a monster that is leaving the map for good, so a dead entry
    /// cannot resurrect it. Like the C++ (the <c>m_dwHostKEY</c> epoch plus the map lookup in
    /// <c>OnSM_AICMD_ACK</c>), this only bumps the epoch: <see cref="RunScheduledAi"/> drops the stale entries
    /// when they come due (epoch mismatch, or the id now maps to a respawned object). Rebuilding the whole queue
    /// here cost ~5 ms and a ~640 KB large-object allocation per monster death.</summary>
    private static void CancelAi(Monster mon) => mon.HostKey++;

    // ======================================================================================
    // The client-reported aggro bounds (CS_ENTERLB / LEAVELB / ENTERAB / LEAVEAB)
    // ======================================================================================

    /// <summary>
    /// C++ <c>OnCS_ENTERLB_REQ</c> / <c>LEAVELB</c> / <c>ENTERAB</c> / <c>LEAVEAB</c>
    /// (CSHandler.cpp:1100-1218) — one handler, four ids: the <b>client</b> decides when its player crosses a
    /// monster's look bound or attack bound and reports it; the server just fires the matching trigger on that
    /// monster. All four share the layout <c>dwCharID, dwTargetID, bTargetType, dwMonID, bChannel, wMapID</c>
    /// and none of them replies.
    ///
    /// <para>This is the real aggro-on-sight path: in the shipped chart, <c>AT_ENTERLB</c> is what leads to
    /// <c>ChgHost</c> → <c>ChgMode</c> → <c>Follow</c>. <c>AT_ENTER</c> (which fires on cell entry, server-side)
    /// only leads to <c>SetHost</c> → <c>Roam</c> — waking the monster, not engaging it.</para>
    /// </summary>
    private void OnAggroBoundReq(ClientSession s, PacketReader r, AiTrigger trigger)
    {
        if (s.State != EnterState.InGame || s.Char is null) return;

        uint charId = r.ReadUInt32();
        uint targetId = r.ReadUInt32();
        byte targetType = r.ReadByte();
        uint monId = r.ReadUInt32();

        // C++ looks the monster up on the player's own map, so a stale/forged id is a silent no-op.
        if (_state.FindMonster(monId) is not { } mon)
        { _log.LogDebug("[mon] {Trigger} from char {Char}: unknown monster {Mon}.", trigger, s.CharId, monId); return; }
        if (mon.Channel != s.Channel || mon.MapId != s.Char.MapId) return;

        _log.LogDebug("[mon] {Trigger} from char {Char} for mon {Mon} (host {Host}, mode {Mode}, target {Target}).",
            trigger, s.CharId, monId, mon.HostId, mon.Mode, mon.TargetId);
        OnAiEvent(mon, trigger, 0, charId, targetId, targetType);
    }

    // ======================================================================================
    // Delay / conditions
    // ======================================================================================

    /// <summary>The per-command <c>GetDelay</c> overrides. Everything not listed uses the binding's own
    /// <c>dwDelay</c> (the base <c>CTAICommand::GetDelay</c>).</summary>
    private uint AiGetDelay(AiBinding b, Monster mon) => b.Command.Kind switch
    {
        // CTAICmdRoam::GetDelay — delay + rand() % (ROAM_DELAY_BOUND * delay); 0 stays 0.
        AiCommandKind.Roam => b.Delay == 0 ? 0u : b.Delay + (uint)SpawnRng.Next((int)(RoamDelayBound * b.Delay)),
        // CTAICmdRegen::GetDelay — the spawn's own respawn delay, not the chart row's.
        AiCommandKind.Regen => SpawnDelayOf(mon),
        // These override GetDelay to a hard 0 (they must run inline within their chain).
        AiCommandKind.SetHost or AiCommandKind.ChkHost or AiCommandKind.ChgHost or AiCommandKind.ChgMode
            or AiCommandKind.BeginAtk or AiCommandKind.Follow or AiCommandKind.Gohome
            or AiCommandKind.Refill or AiCommandKind.Remove => 0u,
        _ => b.Delay,
    };

    /// <summary>
    /// C++ <c>CTAICommand::CanRun</c> plus each subclass's hard state gate. The subclass gate runs first and
    /// short-circuits (exactly as the overrides do), then the chart conditions are AND-folded.
    /// </summary>
    private bool AiCanRun(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        var host = mon.HostId != 0 ? _state.FindByChar(mon.HostId) : null;
        bool hasHost = host is { State: EnterState.InGame, Char: not null };

        bool gate = b.Command.Kind switch
        {
            // CTAICmdRegen: only a despawned slot regenerates.
            AiCommandKind.Regen => mon.Status == OsDisappear && !mon.Remove,
            // CTAICmdLeave: a corpse leaves unless its spawn is suspended and the monster isn't force-removed.
            AiCommandKind.Leave => mon.Status == OsDead && (!SpawnSuspendedOf(mon) || mon.Remove),
            // CTAICmdSetHost: acquire only while idle and alive.
            AiCommandKind.SetHost => mon.Mode != MtBattle && mon.Status != OsDead,
            AiCommandKind.ChkHost => mon.Status == OsWakeup,
            AiCommandKind.ChgHost => mon.Status == OsWakeup,
            // CTAICmdChgMode: the event's host must be ours, and leaving NORMAL requires being able to attack.
            AiCommandKind.ChgMode => mon.Status == OsWakeup && mon.HostId == eventHost
                                     && !(mon.Mode == MtNormal && !CheckAttack(mon)),
            AiCommandKind.BeginAtk => mon.Status == OsWakeup && mon.Mode == MtBattle && mon.TargetId != 0
                                      && hasHost && CheckAttack(mon),
            AiCommandKind.Attack => mon.Status == OsWakeup && mon.Mode == MtBattle && mon.TargetId != 0
                                    && hasHost,
            AiCommandKind.Follow => mon.Status == OsWakeup && mon.Mode == MtBattle && mon.HostId == eventHost
                                    && mon.TargetId != 0 && !IsFixedMonster(mon) && CheckAttack(mon),
            AiCommandKind.Getaway => mon.Status == OsWakeup,
            AiCommandKind.Refill => mon.Status == OsWakeup && mon.HostId == eventHost,
            AiCommandKind.Roam => mon.Status == OsWakeup && mon.Mode != MtBattle && !mon.IsSelf,
            AiCommandKind.Remove => true,               // CTAICmdRemove::CanRun is an unconditional TRUE
            AiCommandKind.Gohome => mon.Status == OsWakeup && mon.Mode == MtGohome,
            AiCommandKind.Lottery => mon.Status == OsDead,
            _ => true,                                   // the default CTAICommand — no state gate
        };
        if (!gate) return false;

        foreach (var cond in b.Command.Conditions)
            if (!CheckAiCondition(cond, mon, rhId, rhType))
                return false;
        return true;
    }

    /// <summary>
    /// C++ <c>CTAICommand::CheckCondition</c> (TAICommand.cpp:50).
    ///
    /// <para><b>The <see cref="AiConditionKind.ChgHost"/> arm reproduces an original bug on purpose.</b> The
    /// C++ case computes <c>targetId == rhId &amp;&amp; targetType == rhType ? FALSE : TRUE</c> as a bare
    /// expression statement with <b>no <c>return</c></b>, so control falls through to the function's trailing
    /// <c>return TRUE</c> and the condition always passes. Shipped monster behaviour is tuned around that, so
    /// "fixing" it here would silently change live AI. Kept, and asserted by a test.</para>
    /// </summary>
    private bool CheckAiCondition(AiCondition cond, Monster mon, uint rhId, byte rhType) => cond.Kind switch
    {
        AiConditionKind.Prob => SpawnRng.Next(100) < cond.Value,
        AiConditionKind.Mode => mon.Mode == cond.Value,
        AiConditionKind.ChgHost => true,   // see the remark above — the C++ arm never returns
        _ => true,
    };

    // ======================================================================================
    // Command bodies
    // ======================================================================================

    /// <summary>Dispatch a command body. Returns the C++ <c>ExecAI</c> result: <c>true</c> = "did work, re-arm
    /// me if I loop", <c>false</c> = "stop this chain here" (an early-out that also skips
    /// <see cref="AiComplete"/>, so no successor fires).</summary>
    private bool AiExec(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType) => b.Command.Kind switch
    {
        AiCommandKind.SetHost => ExecSetHost(b, mon, eventHost, rhId, rhType),
        AiCommandKind.ChkHost => ExecChkHost(b, mon, eventHost, rhId, rhType),
        AiCommandKind.ChgHost => ExecChgHost(b, mon, eventHost, rhId, rhType),
        AiCommandKind.ChgMode => ExecChgMode(b, mon, eventHost, rhId, rhType),
        AiCommandKind.BeginAtk => ExecAttack(b, mon, eventHost, rhId, rhType, isBegin: true),
        AiCommandKind.Attack => ExecAttack(b, mon, eventHost, rhId, rhType, isBegin: false),
        AiCommandKind.Follow => ExecFollow(b, mon, eventHost, rhId, rhType),
        AiCommandKind.Roam => ExecRoam(b, mon, eventHost, rhId, rhType),
        AiCommandKind.Gohome => ExecGohome(b, mon, eventHost, rhId, rhType),
        AiCommandKind.Refill => ExecRefill(b, mon, eventHost, rhId, rhType),
        AiCommandKind.Getaway => ExecGetaway(b, mon, eventHost, rhId, rhType),
        AiCommandKind.Leave => ExecLeave(b, mon, eventHost, rhId, rhType),
        AiCommandKind.Remove => ExecRemove(mon),
        AiCommandKind.Regen => ExecRegen(b, mon, eventHost, rhId, rhType),
        AiCommandKind.Lottery => ExecLottery(b, mon, eventHost, rhId, rhType),
        _ => AiComplete(b, mon, eventHost, rhId, rhType),                        // the default no-op command
    };

    /// <summary>The tail of every C++ <c>ExecAI</c>: <c>CTAICommand::ExecAI</c> (TAICommand.cpp:75) fires
    /// <c>AT_AICOMPLETE</c> keyed on this command's id — the chain link — and reports whether the caller
    /// should re-arm (<c>m_bLoop &amp;&amp; GetDelay</c>).</summary>
    private bool AiComplete(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        OnAiEvent(mon, AiTrigger.AiComplete, b.Command.Id, eventHost, rhId, rhType);
        return b.Loop && AiGetDelay(b, mon) != 0;
    }

    /// <summary>C++ <c>CTAICmdSetHost::ExecAI</c> (TAICmdSetHost.cpp:27) — pick the nearest recently-moved
    /// host-eligible player in view and wake onto it. <b>Host only</b>: unlike
    /// <see cref="TryAcquireHost"/> (which folded the whole chain for the script-less path), this sets the
    /// host and wakes; the target/aggro/BATTLE transition belongs to the scripted <c>ChgHost</c>/<c>ChgMode</c>
    /// successors, which is the entire point of the chart being data.</summary>
    private bool ExecSetHost(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        var pick = PickHost(mon, requireRecentMove: true);
        var old = FindHost(mon, mon.HostId);

        if (pick is null)
        {
            mon.HostKey++;
            ResetHost(mon);
            return false;
        }
        if (pick == old) return false;   // unchanged host ⇒ the C++ returns FALSE (no chain)

        mon.HostKey++;
        mon.HostId = pick.Char!.CharId;
        mon.Status = OsWakeup;
        NotifyHost(mon, old);
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdChkHost::ExecAI</c> (TAICmdChkHost.cpp:26) — revalidate the current host and, if
    /// it can no longer host, hand off to the nearest eligible player in view. Unlike <c>SetHost</c> there is
    /// <b>no recency window</b>: any <c>CanHost</c> player will do.</summary>
    private bool ExecChkHost(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        // FindHost only looks in the monster's own 3×3: a host that walked out of view no longer counts.
        var old = FindHost(mon, mon.HostId);
        if (old is { Char: { Hp: > 0, CanHost: true } }) return false; // still fine

        var pick = PickHost(mon, requireRecentMove: false);
        if (pick is null)
        {
            mon.HostKey++;
            ResetHost(mon);
            return false;
        }
        if (pick == old) return false;

        mon.HostKey++;
        mon.HostId = pick.Char!.CharId;
        NotifyHost(mon, old);
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdChgHost::ExecAI</c> (TAICmdChgHost.cpp:25) — adopt the event's host as ours and
    /// lock onto the event's target. A vanished host degrades to <see cref="Disengage"/> (the C++
    /// <c>LeaveAggro</c> → else <c>ResetHost</c> + re-fire <c>AT_ENTER</c>); a same-country host is refused.</summary>
    private bool ExecChgHost(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        var neu = eventHost != 0 ? _state.FindByChar(eventHost) : null;
        if (neu is not { State: EnterState.InGame, Char: { } nc })
        {
            mon.HostKey++;
            Disengage(mon, rhId, rhType != 0 ? rhType : OtPc, NowMs);
            if (mon.HostId == 0) OnAiEvent(mon, AiTrigger.Enter);   // C++ ResetHost → OnEvent(AT_ENTER)
            return false;
        }
        if (mon.Country == nc.Country) return false;                 // no same-country hosting

        if (eventHost != mon.HostId)
        {
            mon.HostKey++;
            mon.HostId = eventHost;
            NotifyHost(mon);
        }

        mon.TargetId = rhId;
        mon.TargetType = rhType;
        mon.AddAggro(eventHost, rhId, rhType, WarCountryOf(nc), 1);
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdChgMode::ExecAI</c> (TAICmdChgMode.cpp:28) — advance the mode ring
    /// NORMAL → BATTLE → GOHOME → NORMAL, clearing the target on the way out of battle. Bumps the epoch,
    /// which cancels every command scheduled against the previous mode.</summary>
    private bool ExecChgMode(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        switch (mon.Mode)
        {
            case MtNormal:
                ChgMode(mon, MtBattle);                // + the regen-suppression anchors
                break;
            case MtBattle:
                ChgMode(mon, MtGohome);
                mon.TargetId = 0;
                mon.TargetType = 0;
                break;
            case MtGohome:
                ChgMode(mon, MtNormal);
                mon.TargetId = 0;
                mon.TargetType = 0;
                break;
        }
        mon.HostKey++;
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdBeginAtk</c>/<c>CTAICmdAttack</c> (TAICmdBeginAtk.cpp:31, TAICmdAttack.cpp:26) —
    /// swing at the current target. <c>Attack</c> additionally requires the event's target to still be ours.
    ///
    /// <para><b>Approximation:</b> the C++ picks <c>m_pNextSkill</c> via <c>SelectSkill</c> and announces it
    /// (<c>CS_MONATTACK_ACK</c> + <c>SkillUse</c>); monster skills are not ported, so both commands delegate
    /// to the basic melee (<see cref="AttackPlayer"/>), which emits the same announce with
    /// <c>wSkillID = 0</c> plus the damage packet. The attack-speed cadence is still honoured.</para></summary>
    private bool ExecAttack(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType, bool isBegin)
    {
        if (!isBegin && (mon.TargetId != rhId || mon.TargetType != rhType))
            return AiComplete(b, mon, eventHost, rhId, rhType);   // C++ skips the swing but still chains

        if (ResolveMonTarget(mon) is { } target && _tickSeconds * 1000L >= mon.AtkNextMs)
        {
            AttackTarget(mon, target, _tickSeconds * 1000L);
            mon.AtkNextMs = _tickSeconds * 1000L + (mon.AtkSpeed != 0 ? mon.AtkSpeed : DefaultAtkSpeedMs);
        }

        if (isBegin) mon.HostKey++;   // BeginAtk bumps the epoch; Attack does not
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdFollow::ExecAI</c> (TAICmdFollow.cpp:34) — broadcast a <c>TA_FOLLOW</c> move
    /// toward the host. The C++ sends only to the host; the port broadcasts to everyone in view, matching how
    /// the chase already behaves (all viewers must animate the chase, not just the host).</summary>
    private bool ExecFollow(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        _log.LogDebug("[mon] FOLLOW mon {Mon} host {Host} target {Target} mode {Mode}.", mon.Id, mon.HostId, mon.TargetId, mon.Mode);
        if (_state.FindByChar(mon.HostId) is { State: EnterState.InGame, Char: { } host })
            foreach (var p in _state.PlayersAround(mon))
                SendCS_MONACTION_ACK(p, mon.Id, TaFollow, host.PosX, host.PosY, host.PosZ,
                    mon.TargetId, mon.TargetType != 0 ? mon.TargetType : OtPc);

        mon.HostKey++;
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdRoam::ExecAI</c> (TAICmdRoam.cpp:30) — wander within the spawn radius. With no
    /// host the C++ bumps the epoch and fires <c>AT_LEAVE</c> instead (the monster goes dormant).</summary>
    private bool ExecRoam(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        var viewers = _state.PlayersAround(mon).ToList();
        if (viewers.Count == 0)
        {
            mon.HostKey++;
            OnAiEvent(mon, AiTrigger.Leave);
            return false;
        }

        var (nx, nz) = RoamDestination(mon);
        mon.NextX = nx;
        mon.NextZ = nz;
        foreach (var p in viewers) SendCS_MONACTION_ACK(p, mon.Id, TaWalk, nx, mon.StartY, nz);
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdGohome::ExecAI</c> (TAICmdGohome.cpp:26) — run back to the spawn anchor, or fire
    /// <c>AT_ATHOME</c> once inside <c>ZONE_HOMESIZE</c> of it. The C++ also strips block-type maintains on the
    /// way home; monster block-buffs are not ported, so that step is a no-op here.</summary>
    private bool ExecGohome(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        float dx = mon.StartX - mon.PosX, dz = mon.StartZ - mon.PosZ;
        if (dx * dx + dz * dz <= ZoneHomeSize * ZoneHomeSize)
        {
            OnAiEvent(mon, AiTrigger.AtHome, 0, mon.HostId, mon.TargetId, mon.TargetType);
            return false;
        }

        foreach (var p in _state.PlayersAround(mon))
            SendCS_MONACTION_ACK(p, mon.Id, TaRun, mon.StartX, mon.StartY, mon.StartZ);

        mon.HostKey++;
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdRefill::ExecAI</c> (TAICmdRefill.cpp:27) — restore the monster to full HP/MP and
    /// push the new bars to everyone in view.</summary>
    private bool ExecRefill(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        mon.Hp = mon.MaxHp;
        mon.Mp = mon.MaxMp;
        foreach (var p in _state.PlayersAround(mon)) SendMonsterHpMp(p, mon);
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdGetaway::ExecAI</c> (TAICmdGetaway.cpp:26) — flee to a random point on the spawn
    /// radius. <b>Partial:</b> the call-for-help branch (<c>m_bCall</c> → find a same-kind idle neighbour within
    /// 30 units and run to it, then both re-engage) needs the unloaded <c>m_bCall</c>/<c>m_wKind</c> chart
    /// columns, so this ports only the plain flee; with no host, or when the monster can still attack, the C++
    /// gives up fleeing and re-fires <c>AT_DEFEND</c>, which is preserved.</summary>
    private bool ExecGetaway(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        if (_state.FindByChar(mon.HostId) is not { State: EnterState.InGame, Char: not null }) return false;

        if (CheckAttack(mon))   // C++: !m_bCall && CheckAttack() ⇒ bRet = FALSE ⇒ stop fleeing, re-engage
        {
            OnAiEvent(mon, AiTrigger.Defend, 0, mon.HostId, mon.TargetId, mon.TargetType);
            return false;
        }

        mon.HostKey++;
        double rad = SpawnRng.Next(360) * Math.PI / 180.0;
        float fx = mon.PosX + mon.Area * (float)Math.Cos(rad);
        float fz = mon.PosZ + mon.Area * (float)Math.Sin(rad);
        foreach (var p in _state.PlayersAround(mon))
            SendCS_MONACTION_ACK(p, mon.Id, TaRun, fx, mon.PosY, fz);

        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdLeave::ExecAI</c> (TAICmdLeave.cpp:22) — take the corpse off the map. The C++
    /// stalls (returns TRUE, i.e. retry) while loot is still locked or being routed; the port keeps the
    /// simpler "wait until the corpse is empty" form of that stall. Then it clears the hate/damage tables,
    /// leaves the map, and chains to <c>AT_DELETE</c> (respawnable) or <c>AT_TIMEOUT</c> (not).</summary>
    private bool ExecLeave(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        // C++ TAICmdLeave: a corpse someone is looting stays one more pass.
        if (mon.LeaveCount == 0 && mon.InvenLock != 0) { mon.LeaveCount++; return true; }
        if (mon.HasLoot && !mon.Remove) return true;   // stall: retry on the next scheduled pass

        mon.ClearAggro();
        DespawnMonster(mon, exitMap: true);

        if (SpawnDelayOf(mon) != 0 && !mon.Remove)
        {
            OnAiEvent(mon, AiTrigger.Delete, 0, 0, rhId, rhType);
        }
        else
        {
            OnAiEvent(mon, AiTrigger.Timeout, 0, 0, rhId, rhType);
            return false;
        }

        mon.Status = OsDisappear;
        mon.HostId = 0;
        mon.TargetId = 0;
        mon.TargetType = 0;
        mon.InvenLock = 0;
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTAICmdRemove::ExecAI</c> (TAICmdRemove.cpp:24) — delete the monster outright and, when
    /// that empties a dynamic (<c>SE_DYNAMIC</c>) spawn, retire the spawn with it. Always returns FALSE: the
    /// object is gone, so nothing may chain off it.</summary>
    private bool ExecRemove(Monster mon)
    {
        CancelAi(mon);
        DespawnMonster(mon, exitMap: true);
        RearmSpawnSlot(mon.Id, _tickSeconds * 1000L);
        return false;
    }

    /// <summary>C++ <c>CTAICmdRegen::ExecAI</c> (TAICmdRegen.cpp:31) — roll the spawn's probability and refill
    /// its slot. The port already owns this pipeline (<c>RunMonsterRegen</c> weighted pick +
    /// scatter), so the command re-arms the slot and lets that run; the C++ group-spawn redirect
    /// (<c>m_lpvGroup</c>) is the deferred leader/cluster branch.</summary>
    private bool ExecRegen(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        RearmSpawnSlot(mon.Id, _tickSeconds * 1000L);
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    // ======================================================================================
    // Helpers the commands lean on
    // ======================================================================================

    /// <summary>The host scan shared by <c>SetHost</c> and <c>ChkHost</c>: the nearest (Manhattan)
    /// <c>CanHost</c> player in the 3×3 view, optionally restricted to those that moved within the last
    /// 3000 ms, with the C++ quirks preserved — the unconditional first-player seed and the lazy activation
    /// of a never-eligible first player (TAICmdSetHost.cpp:40,57-64).</summary>
    private ClientSession? PickHost(Monster mon, bool requireRecentMove)
    {
        var players = _state.PlayersAround(mon)
            .Where(p => p.State == EnterState.InGame && p.Char is { Hp: > 0 })
            .ToList();
        if (players.Count == 0) return null;

        ClientSession? host = null;
        uint best = 0xFFFFFFFF;

        if (requireRecentMove && players[0].Char!.CanHost) host = players[0];  // the seed (no recency test)

        foreach (var p in players)
        {
            var pc = p.Char!;
            if (!pc.CanHost) continue;
            uint local = (uint)(MathF.Abs(mon.PosX - pc.PosX) + MathF.Abs(mon.PosZ - pc.PosZ));
            if (local >= best) continue;
            if (requireRecentMove && NowMs - pc.LastMoveMs >= 3000) continue;
            host = p;
            best = local;
        }

        if (host is null && !players[0].Char!.CanHost)
        {
            host = players[0];
            players[0].Char!.CanHost = true;
        }
        return host;
    }

    /// <summary>C++ <c>CTMonster::FindHost</c> (TMonster.cpp:485) — the host, but only if it is in the monster's
    /// own 3×3 view.</summary>
    private ClientSession? FindHost(Monster mon, uint hostId) => hostId == 0 ? null
        : _state.PlayersAround(mon).FirstOrDefault(p =>
            p.State == EnterState.InGame && p.Char is { CharId: var cid } && cid == hostId);

    /// <summary>C++ <c>CTObjBase::ChgMode</c> (TObjBase.cpp:295) — set the mode and tell every viewer. The client
    /// needs the ACK: it is what clears the monster's follow target and sets or clears its go-home flag
    /// (TClient CSHandler.cpp:1952). Follow it with each viewer's monster colour, as the native ChgMode does.</summary>
    private void ChgMode(Monster mon, byte mode)
    {
        if (mode == MtBattle && mon.Mode != MtBattle) mon.EnterBattle(NowMs, RecoverInit);
        mon.Mode = mode;
        mon.LastAtkTick = NowMs;

        var w = new PacketWriter(Msg.CS_CHGMODE_ACK, capacity: 8);
        w.WriteUInt32(mon.Id);
        w.WriteByte(Monster.OtMon);
        w.WriteByte(mode);
        var ack = w.ToArray();
        foreach (var p in _state.PlayersAround(mon))
        {
            p.Send(ack);
            SendMonsterColor(p, mon);
        }
    }

    /// <summary>C++ <c>CTMonster::ResetHost</c> (TMonster.cpp:2392) — stand the monster down to <c>MT_NORMAL</c>,
    /// forget host, target and hate, tell the old host it no longer drives it, and — if it was left more than
    /// half a cell from home — send it back to its spawn point (the <c>SM_RESETHOST</c> round trip).</summary>
    private void ResetHost(Monster mon)
    {
        bool hadHost = mon.HostId != 0;
        var oldHost = hadHost ? _state.FindByChar(mon.HostId) : null;   // map-wide, not FindHost

        mon.Action = TaStand;
        ChgMode(mon, MtNormal);
        mon.HostId = 0;
        mon.TargetId = 0;
        mon.TargetType = 0;
        mon.ClearAggro();

        // The C++ tests _AtlModule.FindChar(host), which still finds a player that is logging out (its CTPlayer
        // outlives LeaveMAP). The port has already dropped that session, so "had a host" stands in for it.
        if (!hadHost) return;
        if (oldHost is not null) NotifyHost(mon, oldHost);
        if (Distance(mon.StartX, mon.StartZ, mon.PosX, mon.PosZ) > MapGrid.CellSize / 2f)
            _pendingResetHome.Add(mon);
    }

    /// <summary>Monsters waiting for their <c>SM_RESETHOST_ACK</c>. The C++ posts the request to its own batch
    /// queue, so it lands after the current event chain; this is drained once per tick.</summary>
    private readonly List<Monster> _pendingResetHome = new();

    /// <summary>C++ <c>OnSM_RESETHOST_ACK</c> (SSHandler.cpp:464) — a monster that is still on the map and still
    /// unhosted snaps back to its spawn point, facing the spawn direction, and every viewer is told it now stands
    /// there. Without this an abandoned monster stays wherever its last host left it.</summary>
    private void RunPendingResetHome()
    {
        if (_pendingResetHome.Count == 0) return;
        var due = _pendingResetHome.ToList();
        _pendingResetHome.Clear();

        foreach (var mon in due)
        {
            if (_state.FindMonster(mon.Id) != mon || mon.HostId != 0) continue;
            if (SpawnOf(mon.Id) is not { } sp) continue;

            var s = sp.Def.Spawn;
            mon.StartX = s.PosX; mon.StartY = s.PosY; mon.StartZ = s.PosZ;
            mon.PosY = s.PosY;
            mon.Dir = s.Dir;
            ApplyMonsterMove(mon, s.PosX, s.PosZ);

            mon.MouseDir = Monster.TkdirN; mon.KeyDir = Monster.TkdirN; mon.Action = TaStand;
            var ack = BuildCS_MONMOVE_ACK(new[] { mon });
            foreach (var p in _state.PlayersAround(mon)) p.Send(ack);
        }
    }

    /// <summary>C++ <c>CTObjBase::CheckAttack</c> — whether the monster is able to act offensively (not
    /// blocked, held, hypnotised, frightened, blinded or darkened). The same gate also applies immediately
    /// before an already scheduled attack starts.</summary>
    private static bool CheckAttack(Monster mon) => !mon.Dead && mon.Hp != 0
        && BuffLayer.CanAttack(mon.MaintainSkills, player: false);

    /// <summary>C++ <c>m_pSPAWN-&gt;m_pSPAWN-&gt;m_bRoamType == 4</c> — a pinned "fixed monster" never follows.
    /// The roam-type column is unloaded, so no monster is fixed yet.</summary>
    private static bool IsFixedMonster(Monster mon) => false;

    /// <summary>The monster's spawn respawn delay (C++ <c>m_pSPAWN-&gt;m_pSPAWN-&gt;m_dwDelay</c>), 0 when the
    /// spawn is unknown.</summary>
    private uint SpawnDelayOf(Monster mon) =>
        SpawnById((ushort)(mon.Id >> 16))?.Spawn.Delay ?? 0u;

    /// <summary>C++ <c>m_pSPAWN-&gt;m_bStatus == MONSPAWN_SUSPEND</c> — a suspended spawn holds its corpses.
    /// Spawn suspension (battle-zone events) is not ported, so no spawn is suspended.</summary>
    private static bool SpawnSuspendedOf(Monster mon) => false;
}
