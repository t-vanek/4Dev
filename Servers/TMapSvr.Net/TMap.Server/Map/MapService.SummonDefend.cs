using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Summons as targets. A monster's hate can land on a summon (when its template is selectable); it then chases and
/// swings at the summon, and — its host being the summon's owner (the hate entry's host) — the owner's client
/// reports the hit with <c>CS_DEFEND_REQ</c> (target <c>OT_RECALL</c>/<c>OT_SELF</c>/<c>OT_COMPANION</c>). The C++
/// resolves that target among the reporter's own objects only (<c>FindTarget(pPlayer, …)</c>, TMapSvr.cpp:6441).
///
/// <para>The hit is the monster-vs-non-player roll (<c>GetAtkHitType</c>'s monster-defender branch, on the summon's
/// defend level) against the summon's defence — its stats row plus 55% of the owner's gear
/// (<c>CTRecallMon::GetDefendPower</c>). The summon goes into battle (<c>CTRecallMon::OnDamage</c>); at 0 HP it dies —
/// a summon through the world, a placed object here — and the monsters that hated it turn on its owner.</para>
/// </summary>
public sealed partial class MapService
{
    /// <summary>What a monster is after: a player, or one of a player's summons.</summary>
    private readonly record struct MonTarget(uint Id, byte Type, float X, float Y, float Z, Character? Player, RecallMon? Summon)
    {
        public uint OwnerId => Player?.CharId ?? Summon!.OwnerId;
    }

    /// <summary>The monster's live target, or null when it is gone or dead.</summary>
    private MonTarget? ResolveMonTarget(Monster mon)
    {
        if (mon.TargetId == 0) return null;
        byte type = mon.TargetType != 0 ? mon.TargetType : OtPc;
        if (type == OtPc)
            return _state.FindByChar(mon.TargetId) is { State: EnterState.InGame, Char: { Hp: > 0 } p }
                ? new MonTarget(p.CharId, OtPc, p.PosX, p.PosY, p.PosZ, p, null) : null;
        return _state.FindRecall(type, mon.TargetId) is { InMap: true, Hp: > 0 } m
               && _state.FindByChar(m.OwnerId) is { State: EnterState.InGame }
            ? new MonTarget(m.Id, m.ObjType, m.PosX, m.PosY, m.PosZ, null, m) : null;
    }

    /// <summary>A reported monster hit on one of the reporter's own summons (C++ <c>OnCS_DEFEND_REQ</c>, target a
    /// summon → <c>CTRecallMon::OnDamage</c>).</summary>
    private void MonsterHitsSummon(ClientSession reporter, Character owner, Monster mon, RecallMon target, ushort skillId,
        uint actId, uint aniId, float atkX, float atkY, float atkZ, float defX, float defY, float defZ, MonsterHitEcho echo)
    {
        var tpl = _templates.Skill(skillId);
        if (!HasActiveSkill(target.MaintainSkills, tpl?.TargetActiveId ?? 0)) return;
        var power = AttackerPower.Of(mon, tpl);
        var defender = DamageTarget.Of(target, owner, _templates);
        byte hitType = HitTypeVsMonster(CombatRng, _templates.Formula(power.IsMagic ? FtypeMar : FtypePar),
            mon.Level, target.Level, defender.DefendLevel(power.IsMagic), power.Crit, power.AttackLevel);
        var dmg = CalcDamage(power, defender, tpl, 1, hitType, power.IsMagic, power.IsLong);
        uint hpBefore = target.Hp, mpBefore = target.Mp;
        if (dmg.DamageHp > 0) target.Hp -= Math.Min((uint)dmg.DamageHp, target.Hp);
        else if (dmg.DamageHp < 0) target.Hp = (uint)Math.Min((long)target.MaxHp, (long)target.Hp - dmg.DamageHp);
        if (dmg.DamageMp > 0) target.Mp -= Math.Min((uint)dmg.DamageMp, target.Mp);
        else if (dmg.DamageMp < 0) target.Mp = (uint)Math.Min((long)target.MaxMp, (long)target.Mp - dmg.DamageMp);

        SummonEnterBattle(target);                                      // CTRecallMon::OnDamage → ChgMode(MT_BATTLE)

        byte atkHit = hitType == HtMiss ? HtMiss : target.Hp == 0 ? HtLastHit : hitType;
        byte isMaintain = 0; uint maintainTick = 0;
        if (tpl is { IsNegative: true } hostile)
            EraseBuffByDefend(target.MaintainSkills, hostile, i => EraseMaintainSummon(target, i));
        if (hitType != HtMiss && hpBefore != 0 && target.Hp != 0 && tpl is not null)
        {
            foreach (var row in tpl.Data)
                if (row.Type == SkillTemplate.SdtCure && row.Exec is SctPosRemove or SctNegRemove)
                    StripSummonMaintains(target, positive: row.Exec == SctPosRemove);
            var snap = new MaintainSnapshot(mon.Id, Monster.OtMon, reporter.CharId, OtPc, power.Crit,
                power.AttackLevel, power.Level, power.PysMin, power.PysMax, power.MgMin, power.MgMax,
                echo.CanSelect, power.Country, atkX, atkY, atkZ);
            if (ApplyMaintainToSummon(target, tpl, 1, 0, snap) is { } applied)
            { isMaintain = 1; maintainTick = applied.MaintainTick; }
        }
        var hitAck = BuildMonsterHitAck(mon, target.Id, target.ObjType, dmg, atkHit, landed: hitType != HtMiss, skillId, 1,
            reporter.CharId, actId, aniId, atkX, atkY, atkZ, defX, defY, defZ, echo, power, isMaintain, maintainTick);
        foreach (var p in _state.PlayersAround(mon))
        {
            p.Send(hitAck);
            if (target.Hp != hpBefore || target.Mp != mpBefore) SendSummonHpMp(p, target);
        }
        if (target.Hp == 0) SummonDied(reporter, owner, target);
    }
}
