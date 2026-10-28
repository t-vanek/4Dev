using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Summons in PvP — C++ <c>OnCS_FINISHSKILL_ACK</c> with a summon on either side (CSHandler.cpp:20458): the target list
/// may name any summon or placed object in the attacker's cells, not only the sender's own, and an <c>OT_RECALL</c> /
/// <c>OT_SELF</c> attacker may name players.
/// <list type="bullet">
/// <item>A hostile hit on someone else's summon (<c>CTRecallMon::Defend</c> → <c>CTObjBase::Defend</c>): the
/// monster-vs-non-player hit roll on the summon's defend level, <c>CalcDamage</c> against its defence (its stats row plus
/// 55% of its owner's gear), the buffs that stop on being hit end, a dispel strips its buffs, the skill's debuff stays on
/// it, and the summon goes into battle. At 0 HP it dies (<c>OnDie</c>) and goes the usual way: a summon through the
/// world, a placed object here.</item>
/// <item>A summon's hit on a player is the player-defender hit (<see cref="HitPlayer"/>) with the summon's figures.
/// The kill is its owner's: C++ <c>CTPlayer::OnDie</c> gets <c>dwHostID</c>, and pays PvP points to it
/// (<c>PvPEvent(PVPE_KILL_E, this, dwAttackID)</c> for any non-monster attacker).</item>
/// <item>A buff can be put on anyone's summon, as the C++ finds the target in the cells.</item>
/// </list>
/// <para>As for players, the server takes the client's word for who is an enemy; one's own summons never take one's own
/// hostile skill. <b>Not ported:</b> the summon's own buff layer on the damage and its immunity statuses (as for
/// monsters), and the peace-zone / battle-zone gates (territories are not ported).</para>
/// </summary>
public sealed partial class MapService
{
    /// <summary>One of the attacker's own summons or placed objects, else one on its map and channel (C++ the attacker's
    /// neighbour cells).</summary>
    private RecallMon? SummonInReach(Character attacker, ClientSession s, byte type, uint id)
    {
        IReadOnlyDictionary<uint, RecallMon> mine = type == RecallMon.OtSelf ? attacker.SelfObjs : attacker.Recalls;
        if (mine.TryGetValue(id, out var own)) return own.InMap ? own : null;
        return _state.FindRecall(type, id) is { InMap: true } m && m.MapId == attacker.MapId && m.Channel == s.Channel ? m : null;
    }

    /// <summary>One hostile hit on someone else's summon, from a player or a summon. <paramref name="owner"/> is the
    /// attacking side's player; <paramref name="p"/> the attacker's figures.</summary>
    private void HitSummon(Character owner, AttackerPower p, uint attackId, byte attackType, uint hostId, SkillTemplate? tpl,
        byte level, ushort skillId, byte canSelect, RecallMon target, uint actId, uint aniId, bool forceMiss,
        float atkX, float atkY, float atkZ, float defX, float defY, float defZ)
    {
        if (!HasActiveSkill(target.MaintainSkills, tpl?.TargetActiveId ?? 0)) return;
        if (target.OwnerId == owner.CharId) return;                                      // never one's own
        if (target.Hp == 0 && tpl?.CanDefendAtDie() != true) return;                     // OS_DEAD && !CanDefendAtDie
        var victimOwner = _state.FindByChar(target.OwnerId) is { State: EnterState.InGame, Char: { } vo } vs ? (vs, vo) : default;

        bool isMagic = p.IsMagic, isLong = p.IsLong;
        var def = DamageTarget.Of(target, victimOwner.vo, _templates);
        byte hitType = forceMiss ? HtMiss : HitTypeVsMonster(CombatRng, _templates.Formula(isMagic ? FtypeMar : FtypePar),
            p.Level, target.Level, def.DefendLevel(isMagic), p.Crit, p.AttackLevel);
        var dmg = CalcDamage(p, def, tpl, level, hitType, isMagic, isLong);

        uint hpBefore = target.Hp;
        if (dmg.DamageHp > 0) target.Hp = target.Hp > (uint)dmg.DamageHp ? target.Hp - (uint)dmg.DamageHp : 0;
        else if (dmg.DamageHp < 0) target.Hp = (uint)Math.Min((long)target.MaxHp, (long)target.Hp - dmg.DamageHp);
        if (dmg.DamageMp > 0) target.Mp = target.Mp > (uint)dmg.DamageMp ? target.Mp - (uint)dmg.DamageMp : 0;
        else if (dmg.DamageMp < 0) target.Mp = (uint)Math.Min((long)target.MaxMp, (long)target.Mp - dmg.DamageMp);
        SummonEnterBattle(target);

        if (tpl is { IsNegative: true } hostile) EraseBuffByDefend(target.MaintainSkills, hostile, i => EraseMaintainSummon(target, i));
        if (hitType != HtMiss && hpBefore != 0 && tpl is not null)
            foreach (var d in tpl.Data)
                if (d.Type == SkillTemplate.SdtCure && d.Exec is SctPosRemove or SctNegRemove)
                    StripSummonMaintains(target, positive: d.Exec == SctPosRemove);

        byte isMaintain = 0; uint maintainTick = 0;
        uint apMin = p.ApMin(isMagic, isLong), apMax = p.ApMax(isMagic, isLong);
        if (hitType != HtMiss && target.Hp != 0 && tpl is { } dt && dt.IsMaintainType())
        {
            var snap = new MaintainSnapshot(attackId, attackType, hostId, OtPc, p.Crit, p.AttackLevel, p.Level,
                isMagic ? 0 : apMin, isMagic ? 0 : apMax, isMagic ? apMin : 0, isMagic ? apMax : 0,
                canSelect, p.Country, atkX, atkY, atkZ);
            if (ApplyMaintainToSummon(target, dt, level, 0, snap) is { } applied) { isMaintain = 1; maintainTick = applied.MaintainTick; }
        }
        if (hitType != HtMiss && tpl is { } lt) SendLifeDrain(lt, level, attackId, attackType, hostId, dmg);

        byte atkHit = hitType == HtMiss ? HtMiss : target.Hp == 0 ? HtLastHit : hitType;
        var ack = BuildCS_DEFEND_ACK(attackId, hostId, target.Id, target.ObjType, attackType, actId, aniId,
            p.AttackLevel, p.Level, apMin, apMax, isMagic, p.Crit, canSelect, p.Country, p.AidCountry,
            skillId, level, atkHit, hitType != HtMiss, atkX, atkY, atkZ, defX, defY, defZ, dmg.Map, isMaintain, maintainTick);
        foreach (var v in _state.PlayersAround(target))
        {
            v.Send(ack);
            if (target.Hp != hpBefore) SendSummonHpMp(v, target);
        }

        if (hitType != HtMiss && hpBefore != 0 && target.Hp == 0 && victimOwner.vo is { } o) SummonDied(victimOwner.vs, o, target);
    }

    /// <summary>C++ <c>CTRecallMon::OnDamage</c> → <c>ChgMode(MT_BATTLE)</c>, shown around.</summary>
    private void SummonEnterBattle(RecallMon target)
    {
        if (target.Mode == MtBattle) return;
        target.Mode = MtBattle;
        var mode = new PacketWriter(Msg.CS_CHGMODE_ACK, capacity: 8);
        mode.WriteUInt32(target.Id); mode.WriteByte(target.ObjType); mode.WriteByte(MtBattle);
        var modeAck = mode.ToArray();
        foreach (var p in _state.PlayersAround(target)) p.Send(modeAck);
    }

    /// <summary>A summon at 0 HP (C++ Defend → OnDie, TObjBase.cpp:1287): shown dead, then removed — a summon through the
    /// world, a companion likewise, a placed object here.</summary>
    private void SummonDied(ClientSession ownerSession, Character owner, RecallMon target)
    {
        foreach (var p in _state.PlayersAround(target)) SendCS_DIE_ACK(p, target.Id, target.ObjType);
        switch (target.ObjType)
        {
            case RecallMon.OtSelf: DeleteSelfObj(owner, target.Id); break;
            case RecallMon.OtCompanion: SendMW_SPOLECNIKMONDEL_ACK(owner.CharId, ownerSession.Key, target.Id); break;
            default: SendMW_RECALLMONDEL_ACK(owner.CharId, ownerSession.Key, target.Id); break;
        }
    }

    /// <summary>C++ <c>DeletePositiveMaintainSkill</c> / <c>DeleteNegativeMaintainSkill</c> on a summon (see
    /// <see cref="StripMaintains"/>).</summary>
    private void StripSummonMaintains(RecallMon m, bool positive)
    {
        for (int i = 0; i < m.MaintainSkills.Count;)
        {
            var b = m.MaintainSkills[i];
            if (positive ? b.IsPositive : b.Template is { Positive: 0 }) EraseMaintainSummon(m, i);
            else i++;
        }
    }
}
