using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Phase 31 — the maintained-skill (active buff / debuff) engine: the C++ <c>CTObjBase</c> buff lifecycle
/// (<c>MaintainSkill</c> / <c>UpdateBuffSkill</c> / <c>PushMaintainSkill</c> / <c>CheckMaintainSkill</c> /
/// <c>EraseMaintainSkill</c> / <c>ReleaseMaintain</c> / <c>ForceMaintain</c>). A buff is one
/// <see cref="MaintainSkill"/> in the owner's list; its <c>SA_BUFF</c> <c>SDT_ABILITY</c> rows modify the
/// owner's stats on demand via <see cref="StatEngine.CalcAbilityValue"/> (there is no per-tick HoT — a MaxHP
/// buff surfaces through the existing regen path). Buffs land three ways: a player's buff-type
/// <c>CS_DEFEND</c> (self/ally = <see cref="ApplyMaintainToPlayer"/>, monster debuff =
/// <see cref="ApplyMaintainToMonster"/>), the <see cref="ForceMaintain"/> direct grant (quest DefendSkill,
/// revival, …), and DB reload on enter. Expiry is the per-second <see cref="RunMaintainSkills"/> sweep;
/// removal broadcasts <c>CS_SKILLEND_ACK</c> and re-clamps the vitals.
///
/// <para><b>Deferred (documented — PORT_STATUS.md):</b> cure/dispel (<c>PerformSkill</c> <c>SDT_CURE</c> +
/// <c>DeletePositive</c>/<c>NegativeMaintainSkill</c>), the <c>ApplyEffectionBuff</c> amplifier, the AutoExp
/// buff, loop/channeled skills (<c>CS_LOOPSKILL</c>), the action-triggered erasers
/// (<c>EraseBuffByAttack</c>/<c>Defend</c>/<c>Ride</c>), monster self-buff (<c>Transformation</c>), the
/// server-authoritative <c>FINISHSKILL</c> path, the <c>Posture</c>/<c>ORadius</c>/<c>Trans</c>/silence
/// branches of <c>UpdateBuffSkill</c> (their chart fields aren't loaded — flat-value buffs are exact), and
/// the DB save-back of maintains (load + reconstruct only, matching the deferred char-data-save family).</para>
/// </summary>
public sealed partial class MapService
{
    private const byte SaBuff = 3;   // SKILL_ACTION SA_BUFF
    // SKILL_CURE_TYPE (NetCode.h:1554) — the SDT_CURE execs the cure/dispel path handles (Phase 35 + 43).
    private const byte SctPosRemove = 6, SctNegRemove = 7, SctHp = 8, SctMp = 19, SctHpTrans = 15, SctMpTrans = 16;

    /// <summary>The 15 combat/context fields snapshotted onto a new maintained skill (C++ <c>SetMaintain</c>,
    /// TSkill.cpp:35).</summary>
    private readonly record struct MaintainSnapshot(
        uint AttackId, byte AttackType, uint HostId, byte HostType, byte Hit,
        ushort AttackLevel, byte AttackerLevel, uint PysMin, uint PysMax, uint MgMin, uint MgMax,
        byte CanSelect, byte AttackCountry, float PosX, float PosY, float PosZ);

    // ==================== per-tick expiry sweep (C++ CheckMaintainSkill in OnTimer, before Recover) ====================

    /// <summary>The per-second maintained-skill sweep — expires ended buffs on every in-game player and live
    /// monster (C++ <c>CTPlayer::OnTimer</c> → <c>CheckMaintainSkill</c>, run before <c>Recover</c>). Public so
    /// tests can drive it at a chosen <paramref name="now"/> (the map ms clock).</summary>
    public void RunMaintainSkills(uint now)
    {
        foreach (var s in _state.AllInGame())
            if (s.Char is { } ch) { CheckMaintainPlayer(s, ch, now); CheckMaintainSummons(ch, now); }
        foreach (var mon in _state.AllMonsters())
            CheckMaintainMonster(mon, now);
    }

    private void CheckMaintainPlayer(ClientSession s, Character ch, uint now)
    {
        bool expBuff = false;
        for (int i = 0; i < ch.MaintainSkills.Count;)
        {
            if (ch.MaintainSkills[i].IsEnd(now)) { expBuff |= ch.MaintainSkills[i].SkillId == TpcbangSkill; EraseMaintainPlayer(s, ch, i); }
            else i++;
        }
        if (expBuff) HangExpBuff(s, ch);                                  // C++ TObjBase.cpp:2558: whatever benefit is left
    }

    private void CheckMaintainMonster(Monster mon, uint now)
    {
        for (int i = 0; i < mon.MaintainSkills.Count;)
        {
            if (mon.MaintainSkills[i].IsEnd(now)) EraseMaintainMonster(mon, i);
            else i++;
        }
    }

    // ==================== removal (C++ EraseMaintainSkill) ====================

    /// <summary>C++ <c>CTObjBase::EraseMaintainSkill</c> (TObjBase.cpp:2621) for a PC owner: remove the buff,
    /// broadcast <c>CS_SKILLEND_ACK</c> to the near players so their clients drop the visual, then recompute the
    /// vitals — a MaxHP/MP buff falling off lowers the cap, so clamp current down and re-broadcast the bar if a
    /// cap changed.</summary>
    private void EraseMaintainPlayer(ClientSession s, Character ch, int index)
    {
        var m = ch.MaintainSkills[index];
        TournamentHideEnded(ch, m);                                       // a match's hide time (MapService.TournamentMatch.cs)
        uint oldMaxHp = MaxHpFor(ch), oldMaxMp = MaxMpFor(ch);
        ch.MaintainSkills.RemoveAt(index);

        var end = BuildSkillEndAck(ch.CharId, OtPc, m.SkillId);
        foreach (var p in _state.NearView(s)) p.Send(end);

        uint newMaxHp = MaxHpFor(ch), newMaxMp = MaxMpFor(ch);
        if (ch.Hp > newMaxHp) ch.Hp = newMaxHp;
        if (ch.Mp > newMaxMp) ch.Mp = newMaxMp;
        if (newMaxHp != oldMaxHp || newMaxMp != oldMaxMp) BroadcastHpMp(s, ch);
        SendCS_CHARSTATINFO_ACK(s, ch);                                   // C++ TObjBase.cpp:2750
    }

    /// <summary>C++ <c>EraseMaintainSkill</c> for a monster owner: remove the debuff + broadcast
    /// <c>CS_SKILLEND_ACK</c> to the near players. A monster's MaxHP/MP is chart-fixed (not stat-derived), so
    /// there is no vitals recompute.</summary>
    private void EraseMaintainMonster(Monster mon, int index)
    {
        var m = mon.MaintainSkills[index];
        mon.MaintainSkills.RemoveAt(index);
        var end = BuildSkillEndAck(mon.Id, Monster.OtMon, m.SkillId);
        foreach (var p in _state.PlayersAround(mon)) p.Send(end);
    }

    /// <summary>C++ <c>CTObjBase::ReleaseMaintain</c> (TObjBase.cpp:4928) — drop every non-static buff (called
    /// on death/logout). <paramref name="notify"/> chooses the packet-broadcasting erase vs a silent drop.</summary>
    private void ReleaseMaintainPlayer(ClientSession s, Character ch, bool notify)
    {
        for (int i = ch.MaintainSkills.Count - 1; i >= 0; i--)
            if (!ch.MaintainSkills[i].IsStatic)
            {
                if (notify) EraseMaintainPlayer(s, ch, i);
                else ch.MaintainSkills.RemoveAt(i);
            }
    }

    private const byte BeaAttack = 1, BeaDefend = 2;   // BUFFERASEACTION_TYPE (TMapType.h:509) — tested as bits, as in the C++

    /// <summary>C++ <c>CTObjBase::EraseBuffByAttack</c> (TObjBase.cpp:4663) — using a skill ends one's own buffs that end on
    /// attacking (<c>m_bEraseAct &amp; BEA_ATTACK</c>). A hide buff ends only when the skill used says so
    /// (<c>m_bEraseHide</c>). Taking a stance or summoning ends nothing.</summary>
    private void EraseBuffByAttack(ClientSession s, Character ch, SkillTemplate cur)
    {
        if (cur.IsPosture() || cur.IsRecall()) return;
        EraseByAction(ch.MaintainSkills, cur, BeaAttack, i => EraseMaintainPlayer(s, ch, i));
    }

    /// <summary>C++ <c>CTObjBase::EraseBuffByDefend</c> (TObjBase.cpp:4626) — a hostile skill from someone else, hit or
    /// miss, ends the target's buffs that end on being attacked (<c>BEA_DEFEND</c>), hide buffs as above. A stance skill ends
    /// nothing.</summary>
    private static void EraseBuffByDefend(List<MaintainSkill> list, SkillTemplate cur, Action<int> eraseAt)
    {
        if (cur.IsPosture()) return;
        EraseByAction(list, cur, BeaDefend, eraseAt);
    }

    private static void EraseByAction(List<MaintainSkill> list, SkillTemplate cur, byte action, Action<int> eraseAt)
    {
        for (int i = 0; i < list.Count;)
            if (list[i].Template is { } t && (t.EraseAct & action) != 0 && (!t.IsHideSkill || (cur.EraseHide & action) != 0))
                eraseAt(i);
            else i++;
    }

    /// <summary>C++ <c>CTPlayer::CheckEquipSkill</c> (TPlayer.cpp:3964), run after an equip change and after each of the
    /// player's own hits: a buff the player cast on themself falls off when no equipped item is a weapon it needs
    /// (<see cref="IsEquipSkillItem"/>). If that buff was a stance, the buffs tied to that stance
    /// (<c>m_wPosture</c>) fall off with it.</summary>
    private void CheckEquipSkill(ClientSession s, Character ch)
    {
        ushort posture = 0;
        for (int i = 0; i < ch.MaintainSkills.Count;)
        {
            var m = ch.MaintainSkills[i];
            if (m.AttackType == OtPc && m.AttackId == ch.CharId && !IsEquipSkillItem(ch, m.Template))
            {
                if (m.Template!.IsPosture()) posture = m.SkillId;
                EraseMaintainPlayer(s, ch, i);
            }
            else i++;
        }
        if (posture == 0) return;
        for (int i = 0; i < ch.MaintainSkills.Count;)
        {
            var m = ch.MaintainSkills[i];
            if (m.AttackType == OtPc && m.AttackId == ch.CharId && m.Template?.Posture == posture) EraseMaintainPlayer(s, ch, i);
            else i++;
        }
    }

    /// <summary>C++ <c>CTObjBase::IsEquipSkillItem</c> (TObjBase.cpp:2778) — the skill needs no weapon
    /// (<c>m_dwWeapon</c> 0), or an equipped item's kind is one of them (bit <c>kind − 1</c>). A buff with no chart
    /// template (DB-free) needs nothing.</summary>
    private static bool IsEquipSkillItem(Character ch, SkillTemplate? tpl)
        => tpl is not { WeaponId: not 0 } t
           || ch.Equipped?.Items.Any(it => it.Template is { } e && MatchesSkillWeaponKind(e.Kind, t.WeaponId)) == true;

    private void ReleaseMaintainMonster(Monster mon, bool notify)
    {
        for (int i = mon.MaintainSkills.Count - 1; i >= 0; i--)
            if (!mon.MaintainSkills[i].IsStatic)
            {
                if (notify) EraseMaintainMonster(mon, i);
                else mon.MaintainSkills.RemoveAt(i);
            }
    }

    // ==================== apply (C++ MaintainSkill → UpdateBuffSkill → PushMaintainSkill) ====================

    /// <summary>Build + stack-resolve + push a buff onto a PC owner, then recompute the vitals. Returns the
    /// applied maintain (for the caller's <c>CS_DEFEND_ACK</c> broadcast) or null if the skill isn't a maintain
    /// type, the stack resolver rejected it, or the owner is dead (non-static).</summary>
    private MaintainSkill? ApplyMaintainToPlayer(ClientSession s, Character ch, SkillTemplate tpl, byte level,
                                                 uint remainTick, in MaintainSnapshot snap, uint now)
    {
        if (!tpl.IsMaintainType()) return null;
        uint oldMaxHp = MaxHpFor(ch), oldMaxMp = MaxMpFor(ch);

        var neu = BuildMaintain(tpl, level, remainTick, snap, now);
        if (!UpdateBuffSkill(ch.MaintainSkills, neu, ch.CharId, OtPc, i => EraseMaintainPlayer(s, ch, i)))
            return null;
        if (!PushMaintain(ch.MaintainSkills, ch.Hp == 0, neu)) return null;
        if (tpl.IsTrans()) DropGodBall(ch);                               // C++ SDT_TRANS: a transformed carrier drops its god ball

        uint newMaxHp = MaxHpFor(ch), newMaxMp = MaxMpFor(ch);
        if (ch.Hp > newMaxHp) ch.Hp = newMaxHp;
        if (ch.Mp > newMaxMp) ch.Mp = newMaxMp;
        if (newMaxHp != oldMaxHp || newMaxMp != oldMaxMp) BroadcastHpMp(s, ch);
        SendCS_CHARSTATINFO_ACK(s, ch);                                   // C++ Defend, TObjBase.cpp:1255
        return neu;
    }

    /// <summary>Build + stack-resolve + push a debuff onto a monster owner (no vitals recompute — a monster's
    /// caps are chart-fixed).</summary>
    private MaintainSkill? ApplyMaintainToMonster(Monster mon, SkillTemplate tpl, byte level, uint remainTick,
                                                  in MaintainSnapshot snap, uint now)
    {
        if (!tpl.IsMaintainType()) return null;
        var neu = BuildMaintain(tpl, level, remainTick, snap, now);
        if (!UpdateBuffSkill(mon.MaintainSkills, neu, mon.Id, Monster.OtMon, i => EraseMaintainMonster(mon, i)))
            return null;
        if (!PushMaintain(mon.MaintainSkills, mon.Hp == 0, neu)) return null;
        return neu;
    }

    /// <summary>C++ <c>CTObjBase::ForceMaintain</c> (TObjBase.cpp:4397) — the direct grant path (quest
    /// DefendSkill, revival, PC-bang…): build a level-1 buff from the global skill chart, push it, and broadcast
    /// the maintain <c>CS_DEFEND_ACK</c> to the owner's view. Returns whether it applied.</summary>
    public bool ForceMaintain(ClientSession s, Character ch, ushort skillId, uint hostId, byte hostType,
                              uint attackId, byte attackType, uint remainTick)
    {
        if (skillId == 0) return false;
        if (_templates.Skill(skillId) is not { } tpl) return false;

        var snap = new MaintainSnapshot(attackId, attackType, hostId, hostType, 0, 0, 0, 0, 0, 0, 0,
            1, ch.Country, ch.PosX, ch.PosY, ch.PosZ);
        var m = ApplyMaintainToPlayer(s, ch, tpl, level: 1, remainTick, snap, NowMs);
        if (m is null) return false;

        uint mt = remainTick != 0 ? remainTick : tpl.GetMaintainTick(1);
        var ack = BuildDefendAckMaintain(attackId, attackType, ch.CharId, OtPc, skillId, m.Level, isMaintain: 1, mt,
            ch.PosX, ch.PosY, ch.PosZ);
        foreach (var p in _state.NearView(s)) p.Send(ack);
        return true;
    }

    /// <summary>A player's buff-type <c>CS_DEFEND</c> cast on self or a visible ally (the positive-maintain
    /// branch of C++ <c>Defend</c>): snapshot the caster's power context, apply the buff to the target, then
    /// broadcast the maintain <c>CS_DEFEND_ACK</c> to the target's view. A debuff on another player comes with its hit
    /// (MapService.PvP.cs).</summary>
    private void ApplyPlayerMaintain(ClientSession casterSession, Character caster, uint targetId,
                                     SkillTemplate tpl, byte level, uint attackId, float px, float py, float pz)
    {
        ClientSession targetSession;
        Character targetCh;
        if (targetId == caster.CharId) { targetSession = casterSession; targetCh = caster; }
        else if (_state.FindByChar(targetId) is { State: EnterState.InGame, Char: { } tc } ts) { targetSession = ts; targetCh = tc; }
        else return;

        // C++ Defend snapshots the caster's re-derived power bands / attack level / crit prob onto the maintain.
        uint pysMin = StatEngine.MinAp(caster, arrow: false, _templates), pysMax = StatEngine.MaxAp(caster, arrow: false, _templates);
        uint mgMin = StatEngine.MinMagicAp(caster, _templates), mgMax = StatEngine.MaxMagicAp(caster, _templates);
        ushort atkLvl = StatEngine.AttackLevel(caster, _templates);
        byte cp = StatEngine.CriticalPysProb(caster, _templates);
        var snap = new MaintainSnapshot(attackId, OtPc, attackId, OtPc, cp, atkLvl, caster.Level,
            pysMin, pysMax, mgMin, mgMax, 1, caster.Country, px, py, pz);

        var m = ApplyMaintainToPlayer(targetSession, targetCh, tpl, level, 0, snap, NowMs);
        if (m is null) return;
        var ack = BuildDefendAckMaintain(attackId, OtPc, targetCh.CharId, OtPc, tpl.Id, m.Level, isMaintain: 1, m.MaintainTick, px, py, pz);
        foreach (var p in _state.NearView(targetSession)) p.Send(ack);
    }

    /// <summary>C++ <c>PerformSkill</c>'s <c>SDT_CURE</c> switch (TObjBase.cpp:3390) for a cure skill cast on
    /// self/an ally via <c>CS_DEFEND</c>: strips positive/negative maintains and/or instant-heals HP/MP, then
    /// broadcasts the cure <c>CS_DEFEND_ACK</c> (+ <c>CS_HPMP_ACK</c> on a heal). Phase 35.
    ///
    /// <para><b>Handled execs:</b> <c>SCT_POSREMOVE</c> (strip buffs), <c>SCT_NEGREMOVE</c> (strip debuffs —
    /// C++ <c>== SPT_NEGATIVE</c>, so <c>SPT_NONE</c> buffs are NOT stripped), <c>SCT_HP</c>/<c>SCT_MP</c>
    /// (instant heal with the 0-15% over-heal roll), and <c>SCT_HPTRANS</c>/<c>SCT_MPTRANS</c> (Phase 43 — add
    /// HP/MP from the attacker's transferred amount <paramref name="transHp"/>/<paramref name="transMp"/>, no
    /// over-heal roll). <b>Deferred (documented):</b> the <c>CalcCure</c> stat-layer counteraction (needs the
    /// mid-cast instance-skill threading), <c>SCT_CANCEL</c>/<c>SCT_DIE</c> (block/die buff flags aren't loaded),
    /// the recall/aftermath/revival/reset execs (unported subsystems), and the
    /// <c>SCT_MCPOWER/POISON/WOUND/DISEASE</c> no-ops.</para></summary>
    private void ApplyPlayerCure(ClientSession casterSession, Character caster, uint targetId, SkillTemplate tpl,
                                 byte level, uint attackId, ushort transHp, ushort transMp, float px, float py, float pz)
    {
        ClientSession targetSession;
        Character target;
        if (targetId == caster.CharId) { targetSession = casterSession; target = caster; }
        else if (_state.FindByChar(targetId) is { State: EnterState.InGame, Char: { } tc } ts) { targetSession = ts; target = tc; }
        else return;

        ApplyDeathCures(targetSession, target, attackId, tpl, level);

        bool changed = false;
        for (int i = 0; i < tpl.Data.Count; i++)
        {
            var d = tpl.Data[i];
            if (d.Type != SkillTemplate.SdtCure) continue;
            switch (d.Exec)
            {
                case SctPosRemove: StripMaintains(targetSession, target, positive: true); break;
                case SctNegRemove: StripMaintains(targetSession, target, positive: false); break;
                case SctHp:
                {
                    int n = tpl.Calculate(level, i, MaxHpFor(target));
                    long v = (long)target.Hp + n + (long)n * CombatRng.Next(16) / 100;   // 0-15% over-heal
                    target.Hp = (uint)Math.Min(Math.Max(v, 0), MaxHpFor(target));
                    changed = true;
                    break;
                }
                case SctMp:
                {
                    int n = tpl.Calculate(level, i, MaxMpFor(target));
                    long v = (long)target.Mp + n + (long)n * CombatRng.Next(16) / 100;
                    target.Mp = (uint)Math.Min(Math.Max(v, 0), MaxMpFor(target));
                    changed = true;
                    break;
                }
                case SctHpTrans:   // C++ m_dwHP += Calculate(level, i, wTransHP) — no over-heal roll; clamped by the Defend tail
                {
                    int n = tpl.Calculate(level, i, transHp);
                    target.Hp = (uint)Math.Min(Math.Max((long)target.Hp + n, 0), MaxHpFor(target));
                    changed = true;
                    break;
                }
                case SctMpTrans:
                {
                    int n = tpl.Calculate(level, i, transMp);
                    target.Mp = (uint)Math.Min(Math.Max((long)target.Mp + n, 0), MaxMpFor(target));
                    changed = true;
                    break;
                }
            }
        }

        if (changed) BroadcastHpMp(targetSession, target);   // C++ Defend re-clamps + broadcasts CS_HPMP_ACK
        var ack = BuildDefendAckMaintain(attackId, OtPc, target.CharId, OtPc, tpl.Id, level, isMaintain: 0, 0, px, py, pz);
        foreach (var p in _state.NearView(targetSession)) p.Send(ack);
    }

    /// <summary>C++ <c>PerformSkill</c>'s <c>SDT_STATUS</c> HP↔MP execs (TObjBase.cpp:3688) on a self/ally
    /// <c>CS_DEFEND</c> target: <c>SDT_STATUS_HPMPCHANGE</c> swaps HP↔MP (each side self-clamped to its own max),
    /// <c>SDT_STATUS_HPTOMP</c> sacrifices half the current HP and adds <c>Calculate(...)</c> of it to MP. Both
    /// fail silently (C++ <c>PERFORM_FAIL</c>) when the required pool is empty (MP == 0 / HP == 0). Broadcasts
    /// <c>CS_HPMP_ACK</c> on a change (Phase 43).
    ///
    /// Teleport/warp/return effects execute through <see cref="ApplyPlayerMovementStatus"/>. Persistent
    /// status flags are handled by their maintained buffs rather than this instant-effect path.</summary>
    private void ApplyPlayerStatus(ClientSession casterSession, Character caster, uint targetId, SkillTemplate tpl,
                                   byte level, uint attackId, float px, float py, float pz)
    {
        ClientSession targetSession;
        Character target;
        if (targetId == caster.CharId) { targetSession = casterSession; target = caster; }
        else if (_state.FindByChar(targetId) is { State: EnterState.InGame, Char: { } tc } ts) { targetSession = ts; target = tc; }
        else return;

        if (tpl.HasMovementStatus() && (targetSession.Channel != casterSession.Channel || target.MapId != caster.MapId)) return;

        bool changed = false;
        for (int i = 0; i < tpl.Data.Count; i++)
        {
            var d = tpl.Data[i];
            if (d.Type != SkillTemplate.SdtStatus) continue;
            switch (d.Exec)
            {
                case SkillTemplate.SdtStatusHpMpChange:   // swap HP↔MP (C++ returns PERFORM_FAIL when MP == 0)
                {
                    if (target.Mp == 0) break;
                    uint maxHp = MaxHpFor(target), maxMp = MaxMpFor(target), oldHp = target.Hp;
                    target.Hp = maxHp > target.Mp ? target.Mp : maxHp;   // new HP = min(old MP, MaxHP)
                    target.Mp = maxMp > oldHp ? oldHp : maxMp;           // new MP = min(old HP, MaxMP)
                    changed = true;
                    break;
                }
                case SkillTemplate.SdtStatusHpToMp:   // sacrifice half current HP into MP (C++ PERFORM_FAIL when HP == 0)
                {
                    if (target.Hp == 0) break;
                    uint dec = target.Hp / 2;
                    int n = tpl.Calculate(level, i, dec);
                    target.Mp = (uint)Math.Min(Math.Max((long)target.Mp + n, 0), MaxMpFor(target));
                    target.Hp -= dec;
                    changed = true;
                    break;
                }
            }
        }

        if (changed) BroadcastHpMp(targetSession, target);
        ApplyPlayerMovementStatus(targetSession, target, tpl, level, px, py, pz);
        var ack = BuildDefendAckMaintain(attackId, OtPc, target.CharId, OtPc, tpl.Id, level, isMaintain: 0, 0, px, py, pz);
        foreach (var p in _state.NearView(targetSession)) p.Send(ack);
    }

    /// <summary>Strip every positive (buff) or strictly-negative (debuff) maintained skill from a PC — C++
    /// <c>DeletePositiveMaintainSkill</c> / <c>DeleteNegativeMaintainSkill</c>. Negative uses the strict
    /// <c>m_bPositive == SPT_NEGATIVE</c> (0), NOT <c>IsNegative</c>, so <c>SPT_NONE</c> buffs survive. Removal
    /// goes through <see cref="EraseMaintainPlayer"/> (broadcasts <c>CS_SKILLEND_ACK</c> + recomputes vitals).</summary>
    private void StripMaintains(ClientSession s, Character ch, bool positive)
    {
        for (int i = 0; i < ch.MaintainSkills.Count;)
        {
            var m = ch.MaintainSkills[i];
            bool match = positive ? m.IsPositive : m.Template is { Positive: 0 };
            if (match) EraseMaintainPlayer(s, ch, i);   // removes at i (list shrinks) ⇒ do not advance
            else i++;
        }
    }

    /// <summary>C++ <c>CTSkill</c> build + <c>SetMaintain</c> + <c>SetEndTick</c>/<c>SetLoopEndTick</c>
    /// (TObjBase.cpp:3997, TSkill.cpp:35/133/140) — allocate the buff, snapshot the combat context, arm the
    /// duration (explicit remaining ⇒ loop end-tick, else the template duration). Not yet pushed.</summary>
    private static MaintainSkill BuildMaintain(SkillTemplate tpl, byte level, uint remainTick,
                                               in MaintainSnapshot snap, uint now)
    {
        var m = new MaintainSkill
        {
            SkillId = tpl.Id, Level = level, Template = tpl,
            AttackId = snap.AttackId, AttackType = snap.AttackType,
            HostId = snap.HostId, HostType = snap.HostType, Hit = snap.Hit,
            AttackLevel = snap.AttackLevel, AttackerLevel = snap.AttackerLevel,
            PysMinPower = snap.PysMin, PysMaxPower = snap.PysMax, MgMinPower = snap.MgMin, MgMaxPower = snap.MgMax,
            CanSelect = snap.CanSelect, AttackCountry = snap.AttackCountry,
            PosX = snap.PosX, PosY = snap.PosY, PosZ = snap.PosZ,
        };
        if (remainTick != 0) m.SetLoopEndTick(now, remainTick);
        else m.SetEndTick(now);
        m.RemainTick = m.GetRemainTick(now);
        return m;
    }

    /// <summary>C++ <c>CTObjBase::PushMaintainSkill</c> (TObjBase.cpp:4614) — reject a non-static buff on a dead
    /// owner, else append.</summary>
    private static bool PushMaintain(List<MaintainSkill> list, bool ownerDead, MaintainSkill m)
    {
        if (!m.IsStatic && ownerDead) return false;
        list.Add(m);
        return true;
    }

    /// <summary>C++ <c>CTObjBase::UpdateBuffSkill</c> (TObjBase.cpp:2953) — stack/collision resolution against
    /// the existing buffs before a new one is pushed. A debuff (<c>IsNegative</c>) silently replaces any same-id
    /// entry and always applies. A buff contends with existing buffs sharing an <c>SA_BUFF</c> <c>SDT_ABILITY</c>
    /// row: resolve by priority, then by each side's own flat ability value, then by self-cast ownership, then an
    /// area buff (<c>m_bORadius</c>) is kept; the loser is erased (broadcasting <c>CS_SKILLEND_ACK</c>). A new
    /// <b>stance</b> (<c>IsPosture</c>) replaces the current stance and ends the owner's own buffs that belong to
    /// another stance (<c>m_wPosture</c>); an existing stance is never replaced by an ordinary buff. Returns whether
    /// the new buff should be pushed (<c>!count || erasedAny || !collision</c>).
    ///
    /// <para><b>Simplified (documented):</b> the value comparison uses each skill's flat SA_BUFF delta (base 0)
    /// — exact for INCREASE/DECREASE buffs (the overwhelming majority); MULTIPLY/DIVIDE/PERCENT compare as 0.
    /// The Trans and non-ability branches are omitted.</para></summary>
    private bool UpdateBuffSkill(List<MaintainSkill> list, MaintainSkill neu, uint ownerId, byte ownerType,
                                 Action<int> eraseAt)
    {
        if (neu.IsNegative)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].SkillId == neu.SkillId) list.RemoveAt(i);   // silent replace (C++ delete+erase)
            return true;
        }

        int count = list.Count;
        bool erasedAny = false, collision = false;
        for (int i = 0; i < list.Count;)
        {
            bool erase = false;
            var ex = list[i];
            if (ex.IsNegative)
                erase = false;   // a new buff never strips an existing debuff
            else if (neu.Template?.IsPosture() == true)
                erase = ex.Template is { } pt && (pt.IsPosture()
                    || (pt.Posture != 0 && pt.Posture != neu.SkillId && ex.AttackType == ownerType && ex.AttackId == ownerId));
            else if (ex.Template?.IsPosture() == true)
                erase = false;   // an ordinary buff never replaces a stance
            else if (ex.Template is { } et && neu.Template is { } nt && et.SharedBuffAbility(nt, out byte exec))
            {
                uint v1 = (uint)Math.Max(0, et.CalcAbilityValue(ex.Level, SaBuff, exec, 0));
                uint v2 = (uint)Math.Max(0, nt.CalcAbilityValue(neu.Level, SaBuff, exec, 0));
                byte p1 = et.Priority, p2 = nt.Priority;
                bool exSelf = ex.AttackType == ownerType && ex.AttackId == ownerId;
                bool nuSelf = neu.AttackType == ownerType && neu.AttackId == ownerId;
                if (p1 < p2) erase = true;                 // higher-priority new buff replaces the old
                else if (p1 > p2) collision = true;        // weaker new buff rejected
                else if (v1 > v2) collision = true;        // existing is stronger ⇒ keep it
                else if (v1 < v2) erase = true;            // new is stronger ⇒ replace
                else if (exSelf && !nuSelf) collision = true;
                else if (!exSelf && nuSelf) erase = true;
                else if (et.ORadius != 0) collision = true;  // an area buff holds its place
                else erase = true;                         // same skill/caster ⇒ refresh
            }

            if (erase) { eraseAt(i); erasedAny = true; }   // eraseAt removes at i ⇒ do not advance
            else i++;
        }
        return count == 0 || erasedAny || !collision;
    }

    // ==================== CS_SKILLEND_REQ (client ends/cancels a buff) ====================

    /// <summary>C++ <c>OnCS_SKILLEND_REQ</c> (CSHandler.cpp:10384) — the client ends a maintained buff on a PC
    /// (self) or a field monster. Matches the entry by (<c>dwAttackID</c>, <c>bAttackType</c>, <c>wSkillID</c>);
    /// a hit erases it (broadcasting <c>CS_SKILLEND_ACK</c> to the near players), a miss just echoes
    /// <c>CS_SKILLEND_ACK</c> so clients clear the visual. RECALL/SELF owner types are unported.</summary>
    private void OnCS_SKILLEND_REQ(ClientSession s, PacketReader r)
    {
        uint objId = r.ReadUInt32();
        byte objType = r.ReadByte();
        uint hostId = r.ReadUInt32(); // dwHostID — whose summon, for OT_RECALL
        uint attackId = r.ReadUInt32();
        byte attackType = r.ReadByte();
        ushort skillId = r.ReadUInt16();
        r.ReadUInt16();               // wMapID
        r.ReadByte();                 // bChannelID

        if (s.State != EnterState.InGame || s.Char is not { } ch) return;

        if (objType == OtPc)
        {
            int idx = ch.MaintainSkills.FindIndex(m =>
                m.AttackId == attackId && m.AttackType == attackType && m.SkillId == skillId);
            if (idx >= 0) { EraseMaintainPlayer(s, ch, idx); return; }
            var end = BuildSkillEndAck(objId, objType, skillId);
            foreach (var p in _state.NearView(s)) p.Send(end);
        }
        else if (objType == Monster.OtMon && _state.FindMonster(objId) is { } mon)
        {
            int idx = mon.MaintainSkills.FindIndex(m =>
                m.AttackId == attackId && m.AttackType == attackType && m.SkillId == skillId);
            if (idx >= 0) { EraseMaintainMonster(mon, idx); return; }
            var end = BuildSkillEndAck(objId, objType, skillId);
            foreach (var p in _state.PlayersAround(mon)) p.Send(end);
        }
        else if ((objType == RecallMon.OtRecall ? _state.FindByChar(hostId)?.Char?.Recalls
                  : objType == RecallMon.OtSelf ? (IReadOnlyDictionary<uint, RecallMon>)ch.SelfObjs : null) is { } own
                 && own.TryGetValue(objId, out var summon) && summon.InMap)
        {
            int idx = summon.MaintainSkills.FindIndex(m =>
                m.AttackId == attackId && m.AttackType == attackType && m.SkillId == skillId);
            if (idx >= 0) { EraseMaintainSummon(summon, idx); return; }
            var end = BuildSkillEndAck(objId, objType, skillId);
            foreach (var p in _state.PlayersAround(summon)) p.Send(end);
        }
        else
        {
            s.Send(BuildSkillEndAck(objId, objType, skillId));   // unresolved owner ⇒ echo to self
        }
    }

    // ==================== senders ====================

    /// <summary>C++ <c>SendCS_SKILLEND_ACK</c> (CSSender.cpp:1683) — <c>{dwObjID, bObjType, wSkillID}</c>.</summary>
    private static byte[] BuildSkillEndAck(uint objId, byte objType, ushort skillId)
    {
        var w = new PacketWriter(Msg.CS_SKILLEND_ACK, capacity: 8);
        w.WriteUInt32(objId);
        w.WriteByte(objType);
        w.WriteUInt16(skillId);
        return w.ToArray();
    }

    /// <summary>A buff-grant / cure <c>CS_DEFEND_ACK</c> (the C++ <c>Defend</c>/<c>ForceMaintain</c> broadcast) —
    /// the full DEFEND_ACK wire layout with no host / power / damage. <paramref name="isMaintain"/> is 1 for a
    /// buff grant (with <paramref name="maintainTick"/>), 0 for a cure. Byte-layout identical to
    /// <see cref="BuildCS_DEFEND_ACK"/>.</summary>
    private static byte[] BuildDefendAckMaintain(uint attackId, byte attackType, uint targetId, byte targetType,
                                                 ushort skillId, byte skillLevel, byte isMaintain, uint maintainTick,
                                                 float px, float py, float pz)
    {
        var w = new PacketWriter(Msg.CS_DEFEND_ACK, capacity: 96);
        w.WriteUInt32(attackId);      // dwAttackID
        w.WriteUInt32(targetId);      // dwTargetID
        w.WriteByte(attackType);      // bAttackType
        w.WriteByte(targetType);      // bTargetType
        w.WriteUInt32(0);             // dwHostID
        w.WriteByte(OtPc);            // bHostType
        w.WriteUInt32(0);             // dwActID
        w.WriteUInt32(0);             // dwAniID
        w.WriteByte(isMaintain);      // bIsMaintain
        w.WriteUInt32(maintainTick);  // dwMaintainTick
        w.WriteByte(0);               // bHit (bCP)
        w.WriteByte(HtNormal);        // bAtkHit
        w.WriteUInt16(0);             // wAttackLevel
        w.WriteByte(0);               // bAttackerLevel
        w.WriteUInt32(0); w.WriteUInt32(0);   // dwPysMinPower / Max
        w.WriteUInt32(0); w.WriteUInt32(0);   // dwMgMinPower / Max
        w.WriteByte(1);               // bCanSelect
        w.WriteByte(0);               // bCancelCharge
        w.WriteByte(0);               // bAttackCountry
        w.WriteByte(0);               // bAttackAidCountry
        w.WriteUInt16(skillId);       // wSkillID
        w.WriteByte(skillLevel);      // bSkillLevel
        w.WriteUInt16(0);             // wBackSkillID
        w.WriteByte(1);               // bPerform (success)
        w.WriteFloat(px); w.WriteFloat(py); w.WriteFloat(pz);   // atk pos
        w.WriteFloat(px); w.WriteFloat(py); w.WriteFloat(pz);   // def pos
        w.WriteByte(0);               // empty damage map
        return w.ToArray();
    }
}
