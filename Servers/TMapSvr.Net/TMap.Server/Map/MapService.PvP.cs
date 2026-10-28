using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Player against player — C++ <c>CTPlayer::Defend</c> → <c>CTObjBase::Defend</c> with a PC defender, the death it can
/// cause (<c>CTPlayer::OnDie</c>) and the kill's PvP points (<c>CTMapSvrModule::PvPEvent</c>, TMapSvr.cpp:10190).
/// <para>The server does not decide who may be attacked: like the C++, it takes the client's word for the target (the
/// client only lets a player attack an enemy) and checks only what the C++ checks here — a live target on the same
/// map, the attacker is not the target, and a player in a duel is only open to its opponent (MapService.Duel.cs). The C++
/// peace-zone and battle-zone gates concern systems that are not ported (territories, the lounge, tournaments), and always
/// pass.</para>
/// <para>The hit is the one a monster takes (<see cref="CalcDamage"/>): the defender's defence, its shield roll (a
/// successful one reports <c>HT_BLOCK</c>), its buffs on the damage and its immunity statuses. A kill costs the victim
/// total points and pays the killer — or, in a party, pays the killer the same and each partner nearby 12 — see
/// <see cref="PvPKill"/>. Each kill and death is recorded (<see cref="RecordPvP"/>). A landed skill also leaves its debuff
/// (a stun, a slow, a curse…) through the buff engine, as it does on a monster; a dispel strips the target's buffs; and
/// a hostile skill, hit or miss, ends the target's buffs that stop on being hit (<c>EraseBuffByDefend</c>). A kill in a fort at
/// war goes by the local chart and is written down for the fort (MapService.Fort.cs).</para>
/// </summary>
public sealed partial class MapService
{
    private const byte PvpsNormal = 1;               // PVP_STATUS PVPS_NORMAL (no local battle, no tournament)
    private const byte PvpeKillE = 2;
    private const int PvpLevelGap = 5, PvpPartnerPoint = 12;
    private const long PvpSameVictimWindowMs = 600_000;   // 600 s — three wins on one name within it pay nothing
    private const int PvpRecentCount = 10;           // PVP_RECENTRECORDCOUNT

    /// <summary>One negative hit from <paramref name="ch"/> on another player, <paramref name="target"/> — or, with
    /// <paramref name="power"/>, from one of <paramref name="ch"/>'s summons (its figures; <paramref name="forceMiss"/> is a
    /// doppelganger's fake hit). Either way <paramref name="ch"/> owns the kill.</summary>
    private void HitPlayer(ClientSession s, Character ch, uint hostId, uint attackId, byte attackType, SkillTemplate? tpl,
        byte level, ushort skillId, byte canSelect, ClientSession ts, Character target, uint actId, uint aniId,
        float atkX, float atkY, float atkZ, float defX, float defY, float defZ, AttackerPower? power = null, bool forceMiss = false)
    {
        if (target.Hp == 0 && tpl?.CanDefendAtDie() != true) return;                     // OS_DEAD && !CanDefendAtDie

        var p = power ?? AttackerPower.Of(ch, _templates, tpl) with { Level = ch.Level };
        bool isMagic = p.IsMagic, isLong = p.IsLong;
        if (power is null) ch.EnterBattle(NowMs, RecoverInit);
        target.EnterBattle(NowMs, RecoverInit);                                          // CTPlayer::Defend ChgMode(MT_BATTLE)

        byte hitType = forceMiss ? HtMiss : IsGuildSkill(skillId) ? HtNormal : HitTypeVsPlayer(CombatRng, p.Crit, p.AttackLevel);   // a guild skill always hits (C++ GetAtkHitType)
        uint maxHp = MaxHpFor(target), maxMp = MaxMpFor(target);
        var def = DamageTarget.Of(target, self: false, maxHp, maxMp, CombatRng, _templates, v => DistributeSkill(ts, target, v));
        var dmg = CalcDamage(p, def, tpl, level, hitType, isMagic, isLong);
        uint hpBefore = target.Hp, mpBefore = target.Mp;
        ApplyPlayerDamage(target, dmg, maxHp, maxMp);
        if (dmg.DamageHp > 0 && target.Hp != 0) CheckSoulHp(ts, target);                // C++ OnDamage (MapService.Soulmate.cs)

        byte atkHit = hitType == HtMiss ? HtMiss : target.Hp == 0 ? HtLastHit : dmg.Blocked ? HtBlock : hitType;
        if (hitType != HtMiss && tpl is { } lt) SendLifeDrain(lt, level, attackId, attackType, hostId, dmg);

        // C++ Defend → MaintainSkill on a landed hit: the skill's debuff stays on the player (it does not take on one who
        // just died — PushMaintainSkill refuses a dead owner unless the skill is static). The ACK announces it (the new stat
        // sheet goes with ApplyMaintainToPlayer).
        // C++ Defend: a hostile skill from someone else, hit or miss, first ends the target's buffs that stop on being hit.
        if (tpl is { IsNegative: true } hostile) EraseBuffByDefend(target.MaintainSkills, hostile, i => EraseMaintainPlayer(ts, target, i));

        // C++ PerformSkill's SDT_CURE on a landed hit: a dispel at an enemy strips its buffs (SCT_POSREMOVE) or its debuffs
        // (SCT_NEGREMOVE). Done before the skill's own debuff lands — the C++ pushes that one after PerformSkill.
        if (hitType != HtMiss && hpBefore != 0 && tpl is not null)
            foreach (var d in tpl.Data)
                if (d.Type == SkillTemplate.SdtCure && d.Exec is SctPosRemove or SctNegRemove)
                    StripMaintains(ts, target, positive: d.Exec == SctPosRemove);

        if (hitType != HtMiss && target.Hp != 0 && tpl is { } movement)
            ApplyPlayerMovementStatus(ts, target, movement, level, atkX, atkY, atkZ);

        byte isMaintain = 0; uint maintainTick = 0;
        if (hitType != HtMiss && hpBefore != 0 && tpl is { } dt && dt.IsMaintainType())
        {
            uint apMin = p.ApMin(isMagic, isLong), apMax = p.ApMax(isMagic, isLong);
            var snap = new MaintainSnapshot(attackId, attackType, hostId, OtPc, p.Crit, p.AttackLevel, p.Level,
                isMagic ? 0 : apMin, isMagic ? 0 : apMax, isMagic ? apMin : 0, isMagic ? apMax : 0,
                canSelect, p.Country, atkX, atkY, atkZ);
            if (ApplyMaintainToPlayer(ts, target, dt, level, 0, snap, NowMs) is { } applied)
            {
                isMaintain = 1;
                maintainTick = applied.MaintainTick;
            }
        }

        var ack = BuildCS_DEFEND_ACK(attackId, hostId, target.CharId, OtPc, attackType, actId, aniId,
            p.AttackLevel, p.Level, p.ApMin(isMagic, isLong), p.ApMax(isMagic, isLong), isMagic, p.Crit, canSelect,
            p.Country, p.AidCountry, skillId, level, atkHit, hitType != HtMiss, atkX, atkY, atkZ, defX, defY, defZ, dmg.Map,
            isMaintain, maintainTick);
        bool hpmp = target.Hp != hpBefore || target.Mp != mpBefore;
        var viewers = _state.InView(ts).ToList();
        foreach (var v in viewers)
        {
            v.Send(ack);
            if (hpmp) SendSelfHpMp(v, target.CharId, maxHp, target.Hp, maxMp, target.Hp != 0 ? target.Mp : 0);
        }

        if (hitType == HtMiss || target.Hp != 0 || hpBefore == 0) return;
        if (target.DuelId != 0 && DuelLose(ts, target, hostId)) return;              // a duel's loser does not die
        PlayerDied(target, viewers);
        PvPKill(ts, target, s, ch);
    }

    /// <summary>C++ <c>CTObjBase::DistributeSkill</c> (TObjBase.cpp:4168) — with a damage-sharing buff (the sorcerer's skill
    /// 635, <c>SDT_STATUS_DISTRIBUTE</c>) on its main summon, the summon takes that % of the player's HP loss: its bar is
    /// shown around, it is sent away if that kills it, and what it really lost is taken off the player's damage.</summary>
    private uint DistributeSkill(ClientSession s, Character ch, uint damage)
    {
        if (ch.Recalls.Values.FirstOrDefault(m => m.RecallType == TrecallMain) is not { } recall) return 0;
        MaintainSkill? share = null;
        foreach (var m in recall.MaintainSkills)
            if (m.Template?.Data.Any(d => d.Type == SkillTemplate.SdtStatus && d.Exec == SdtStatusDistribute) == true) share = m;
        if (share?.Template is not { } t || recall.Hp == 0) return 0;

        var row = t.Data.First(d => d.Type == SkillTemplate.SdtStatus && d.Exec == SdtStatusDistribute);
        uint part = (uint)((ulong)damage * (uint)Math.Max(0, t.GetValue(row, share.Level)) / 100);
        if (part == 0) return 0;
        uint before = recall.Hp;
        recall.Hp = recall.Hp > part ? recall.Hp - part : 0;
        foreach (var p in _state.InView(s)) SendSummonHpMp(p, recall);
        if (recall.Hp == 0) SendMW_RECALLMONDEL_ACK(ch.CharId, s.Key, recall.Id);
        return before - recall.Hp;
    }

    private const byte SdtStatusDistribute = 20;   // SDT_STATUS_DISTRIBUTE

    /// <summary>C++ <c>CTObjBase::OnDamage</c> for a player: damage floors at 0, a heal is capped at the maximum.</summary>
    private static void ApplyPlayerDamage(Character ch, in DamageResult r, uint maxHp, uint maxMp)
    {
        if (r.DamageHp > 0) ch.Hp = ch.Hp > (uint)r.DamageHp ? ch.Hp - (uint)r.DamageHp : 0;
        else if (r.DamageHp < 0) ch.Hp = (uint)Math.Min((long)maxHp, (long)ch.Hp - r.DamageHp);
        if (r.DamageMp > 0) ch.Mp = ch.Mp > (uint)r.DamageMp ? ch.Mp - (uint)r.DamageMp : 0;
        else if (r.DamageMp < 0) ch.Mp = (uint)Math.Min((long)maxMp, (long)ch.Mp - r.DamageMp);
    }

    /// <summary>C++ <c>PvPEvent(PVPE_KILL_E, victim, killer)</c>. The event is the level gap between the two sides, each
    /// side's level being its highest party member in view: more than 5 above the victim's side is <c>PVPE_KILL_L</c>, more
    /// than 5 below <c>PVPE_KILL_H</c>, else <c>PVPE_KILL_E</c>. The kill chart row for it gives two percentages of the
    /// victim level's <c>wPvPoint</c>: the victim loses the first from its total, the killer gains the second (cut by
    /// half the victim's death-penalty step, in %) in total and useable. A killer in a party instead shares with every
    /// partner in view, who gets 12.</summary>
    private void PvPKill(ClientSession vs, Character victim, ClientSession ks, Character killer)
    {
        if (victim.ArenaId != 0 || IsTournamentMap(victim.MapId)) return;              // C++ PvPEvent: no points in an arena or a tournament
        // C++ CTPlayer::OnDie: the kill counts in the victim's territory (m_mapKills).
        if (victim.LocalId != 0 && _territories.TryGetValue(victim.LocalId, out var at)) at.Kills[killer.CharId] = at.Kills.GetValueOrDefault(killer.CharId) + 1;
        var local = LocalOf(victim) is { Status: BsBattle } l ? l : null;      // GetPvPStatus: PVPS_LOCAL in a territory at war
        var atkParty = killer.GetPartyId() != 0
            ? _state.InView(ks).Where(x => x.Char is { } c && c.GetPartyId() == killer.GetPartyId()).ToList()
            : new List<ClientSession>();
        int atkMax = killer.Level, defMax = victim.Level;
        foreach (var x in atkParty) atkMax = Math.Max(atkMax, x.Char!.Level);
        if (victim.GetPartyId() != 0)
            foreach (var x in _state.InView(vs))
                if (x.Char is { } c && c.GetPartyId() == victim.GetPartyId()) defMax = Math.Max(defMax, c.Level);

        byte evt = atkMax > defMax + PvpLevelGap ? PvpeKillL : atkMax + PvpLevelGap < defMax ? PvpeKillH : PvpeKillE;
        if (!_templates.PvPointKill.TryGetValue((local is null ? PvpsNormal : PvpsLocal, evt), out var row)) return;
        if (!_templates.LevelPvPoint.TryGetValue(victim.Level, out var worth)) return;

        uint dec = worth * row.Dec / 100, inc = worth * row.Inc / 100;
        if (local is not null) LocalRecord(local, victim, evt, dec, gain: false);
        else inc = inc * (uint)(100 - victim.Persist.Aftermath / 2) / 100;              // no local battle

        UsePvPoint(vs, victim, dec, evt, PvpTotal, killer);
        if (atkParty.Count == 0)
        {
            if (local is not null) LocalRecord(local, killer, evt, inc, gain: true);
            GainPvPoint(ks, killer, inc, evt, PvpTotal | PvpUseable, victim);
            return;
        }
        for (int i = atkParty.Count - 1; i >= 0; i--)                                    // vParty.back() first
        {
            var x = atkParty[i];
            if (local is not null) LocalRecord(local, x.Char!, evt, PvpPartnerPoint, gain: true);
            if (x.Char!.CharId == killer.CharId) GainPvPoint(x, x.Char, inc, evt, PvpTotal | PvpUseable, victim);
            else if (x.IsMain) GainPvPoint(x, x.Char, PvpPartnerPoint, evt, PvpTotal | PvpUseable, victim);
        }
    }

    /// <summary>The kill record part of C++ <c>GainPvPoint</c> / <c>UsePvPoint</c> (the <c>pRec</c> branch) and
    /// <c>CTPlayer::RecordPvP</c> (TPlayer.cpp:5385): a win on a name already beaten 3 times in the last 600 s pays nothing;
    /// the result joins the recent list, the class record and the month's and all-time tallies (with their titles), and a
    /// win is logged (<c>TSaveCharKill</c>). Returns the points to apply.</summary>
    private uint RecordPvP(ClientSession s, Character ch, Character other, bool win, uint point)
    {
        long now = NowMs;
        if (win && ch.PvpRecent.Count(r => now - r.TimeMs < PvpSameVictimWindowMs && r.Win && r.Name == other.Name) >= 3)
            point = 0;
        ch.PvpRecent.Add(new PvpRecord(other.Name, win, point, now, other.Class, other.Level, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (win && _gameDb is { } db) { uint k = ch.CharId, t = other.CharId; _ = EnqueueDbWrite(() => db.SaveCharKillAsync(k, t)); }
        // Past PVP_RECENTRECORDCOUNT, the records older than PVP_SAVETIME go.
        for (int i = 0; i < ch.PvpRecent.Count && ch.PvpRecent.Count > PvpRecentCount;)
            if (now - ch.PvpRecent[i].TimeMs > PvpSameVictimWindowMs) ch.PvpRecent.RemoveAt(i);
            else i++;

        if (other.Class < 6) ch.PvpRecord[other.Class * 2 + (win ? 1 : 0)]++;
        if (win)
        {
            ch.MonthWin++; ch.TotalWin++;
            GetTitle(s, ch, DefeatsMonthTitle, ch.MonthWin, start: true);
        }
        else
        {
            ch.MonthLose++; ch.TotalLose++;
            GetTitle(s, ch, DeathMonthTitle, ch.MonthLose, start: true);
        }
        GetTitle(s, ch, VictoryMonthTitle, ch.MonthRankPercent, start: true);
        return point;
    }
}
