using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Summon combat — a summon or placed object attacking (C++ <c>OnCS_SKILLUSE_REQ</c> / <c>OnCS_FINISHSKILL_ACK</c>
/// with an <c>OT_RECALL</c> / <c>OT_SELF</c> attacker, CSHandler.cpp:2496-2528, 20266-20420). The owner's client drives
/// its summons: it announces their swings (<c>CS_SKILLUSE_REQ</c>) and reports what they hit
/// (<c>CS_FINISHSKILL_ACK</c>); only the owner is accepted for either.
///
/// <para>A summon casts its own chart skills, paying MP/HP from its own pool, with its own cooldowns. Its hits use its
/// stats row plus 55% of its owner's gear (C++ <c>CTRecallMon::GetMinAP</c>… <c>RECALL_ITEMABILITY_RATE</c>) — except a
/// "skill" placed object (Rain of Arrows, Ice Rain…), which hits with its owner's live figures. An auto-AI summon deals
/// ×3 damage; a doppelganger's fake hit always misses. The owner gets the credit: exp, loot and the monster's hate
/// (on the summon itself when its template is selectable, else on the owner).</para>
///
/// <para>Faithful, flagged: a summon's magic crit takes no gear bonus (the C++ reads the summon's own gear, which is
/// always empty). Hits on players and on others' summons: MapService.SummonPvP.cs. <b>Not ported:</b> summon buffs, the owner's passive skills, and the
/// fireball mine's self-destruct (only on the classic <c>CS_DEFEND_REQ</c> path in the C++, which this client does not
/// use for summons).</para>
/// </summary>
public sealed partial class MapService
{
    private const float RecallItemAbilityRate = 0.55f;     // RECALL_ITEMABILITY_RATE

    /// <summary>The owner's summon or placed object with this id (C++ <c>FindRecallMon</c> / <c>FindSelfObj</c> on the
    /// sender), or null. A moving summon also needs the owner to be the main copy.</summary>
    private static RecallMon? OwnSummon(ClientSession s, Character ch, byte type, uint id) => type switch
    {
        RecallMon.OtRecall when s.IsMain => ch.Recalls.GetValueOrDefault(id),
        RecallMon.OtSelf => ch.SelfObjs.GetValueOrDefault(id),
        _ => null,
    };

    /// <summary>C++ <c>CTRecallMon::GetMinAP/GetMaxAP/GetMinMagicAP/…</c> (TRecallMon.cpp:178-367): the stats row, plus
    /// the owner's gear — AP/DP at 55% of the better of the physical and magic figure, levels and physical crit in full.</summary>
    private AttackerPower SummonPower(RecallMon m, SkillTemplate? tpl, Character? owner)
    {
        var a = m.Attr ?? new MonAttrRow(0, 0, 0, 0, 0);
        int G(StatEngine.Ab x) => owner is null ? 0 : StatEngine.SumGetter(owner, x, _templates);
        int M(byte mtype) => owner is null ? 0 : StatEngine.SumMagic(owner, mtype, _templates);
        // !HaveDisWeapon: the weapon band and the owner's gear share; then the summon's own buffs on the ability.
        bool armed = !BuffLayer.HasStatus(m.MaintainSkills, BuffLayer.StatusDisWeapon);
        uint Ap(uint raw, uint wap, StatEngine.Ab x, StatEngine.Ab y, byte mtype)
            => BuffLayer.Apply(m.MaintainSkills, raw + (armed ? wap + (uint)(Math.Max(G(x), G(y)) * RecallItemAbilityRate) : 0), mtype);
        uint B(uint v, byte mtype) => BuffLayer.Apply(m.MaintainSkills, v, mtype);

        bool magic = tpl?.GetAttackType() == SkillTemplate.SatMagic;
        bool isLong = tpl?.IsLongAttack() ?? false;
        return new AttackerPower(
            ShortMin: Ap(a.Ap, a.MinWap, StatEngine.Ab.MinAp, StatEngine.Ab.MinMap, BuffLayer.Pap),
            ShortMax: Ap(a.Ap, a.MaxWap, StatEngine.Ab.MaxAp, StatEngine.Ab.MaxMap, BuffLayer.Pap),
            LongMin: Ap(a.LongAp, a.MinWap, StatEngine.Ab.MinLap, StatEngine.Ab.MinMap, BuffLayer.Lap),
            LongMax: Ap(a.LongAp, a.MaxWap, StatEngine.Ab.MaxLap, StatEngine.Ab.MaxMap, BuffLayer.Lap),
            MgMin: Ap(a.MagicAp, a.MinWap, StatEngine.Ab.MinMap, StatEngine.Ab.MinAp, BuffLayer.Map),
            MgMax: Ap(a.MagicAp, a.MaxWap, StatEngine.Ab.MaxMap, StatEngine.Ab.MaxAp, BuffLayer.Map),
            AttackLevel: magic ? (ushort)B((uint)(a.MagicAtkLevel + M(BuffLayer.Mal)), BuffLayer.Mal)
                               : (ushort)B((uint)(a.AttackLevel + M(BuffLayer.Al)), BuffLayer.Al),
            Crit: magic ? (byte)B(a.CritMagicProb, BuffLayer.Mcr) : (byte)B((uint)(a.CritProb + M(BuffLayer.Cr)), BuffLayer.Cr),   // magic: no gear
            IsMagic: magic, IsLong: isLong, Level: m.Level, Country: m.Country, AidCountry: m.AidCountry,
            Class: m.Template?.Class ?? 0);
    }

    /// <summary>C++ <c>CTRecallMon::GetDefendPower</c> / <c>GetMagicDefPower</c> (TRecallMon.cpp:271): the stats row, plus —
    /// unless a DISDEFEND debuff is on — its weapon defence and 55% of the better of the owner's gear defences; then its
    /// own buffs.</summary>
    private static uint SummonDefendPower(RecallMon m, Character? owner, TemplateStore t, bool magic)
    {
        var a = m.Attr ?? new MonAttrRow(0, 0, 0, 0, 0);
        uint dp = magic ? a.RawMdp : a.RawDp;
        if (!BuffLayer.HasStatus(m.MaintainSkills, BuffLayer.StatusDisDefend))
        {
            int G(StatEngine.Ab x) => owner is null ? 0 : StatEngine.SumGetter(owner, x, t);
            dp += a.Wdp + (uint)(Math.Max(G(StatEngine.Ab.Pdp), G(StatEngine.Ab.Mdp)) * RecallItemAbilityRate);
        }
        return BuffLayer.Apply(m.MaintainSkills, dp, magic ? BuffLayer.Mdp : BuffLayer.Pdp);
    }

    /// <summary>C++ <c>CTRecallMon::GetDefendLevel</c> / <c>GetMagicDefLevel</c>: the stats row plus the owner's gear, then
    /// its own buffs.</summary>
    private static uint SummonDefendLevel(RecallMon m, Character? owner, TemplateStore t, bool magic)
    {
        var a = m.Attr ?? new MonAttrRow(0, 0, 0, 0, 0);
        byte mtype = magic ? BuffLayer.Mdl : BuffLayer.Dl;
        uint dl = (uint)Math.Max(0, (magic ? a.MagicDefLevel : a.DefendLevel) + (owner is null ? 0 : StatEngine.SumMagic(owner, mtype, t)));
        return BuffLayer.Apply(m.MaintainSkills, dl, mtype);
    }

    /// <summary>The power a summon's hit uses: a "skill" placed object hits with its owner's live figures
    /// (CSHandler.cpp:20400), everything else with its own.</summary>
    private AttackerPower HitPower(RecallMon m, SkillTemplate? tpl, Character owner)
        => m.IsSelf && m.RecallType == TrecallSkill ? AttackerPower.Of(owner, _templates, tpl) with { Level = m.Level }
           : SummonPower(m, tpl, owner);

    // ================================ skill use ================================

    /// <summary>The summon branch of <c>OnCS_SKILLUSE_REQ</c>: the owner's client announces its summon's skill.</summary>
    private void SummonSkillUse(ClientSession s, Character ch, byte type, uint id, ushort skillId, byte actionId, uint actId,
        uint aniId, float x, float y, float z, (uint id, byte type)[] targets)
    {
        if (OwnSummon(s, ch, type, id) is not { InMap: true } m)
        { _log.LogDebug("[summon] SKILLUSE {Type}:{Id} skill {Skill}: not {Char}'s summon on this map; dropped.", type, id, skillId, ch.CharId); return; }
        _log.LogDebug("[summon] SKILLUSE {Type}:{Id} skill {Skill} -> {Targets}.", type, id, skillId,
            string.Join(",", targets.Select(t => $"{t.type}:{t.id}")));
        if (m.Skills.FirstOrDefault(k => k.SkillId == skillId) is not { } skill)
        { _log.LogDebug("[summon] SKILLUSE {Id} skill {Skill} refused.", id, skillId); SendSkillUseFail(s, SkillUseResult.NotFound, id, type, skillId, actionId, actId, aniId); return; }

        uint needMp = skill.GetRequiredMp(m.MaxMp);
        if (m.Mp < needMp) { _log.LogDebug("[summon] SKILLUSE {Id} skill {Skill} refused.", id, skillId); SendSkillUseFail(s, SkillUseResult.NeedMp, id, type, skillId, actionId, actId, aniId); return; }
        uint needHp = skill.GetRequiredHp(m.MaxHp);
        if (m.Hp <= needHp) { _log.LogDebug("[summon] SKILLUSE {Id} skill {Skill} refused.", id, skillId); SendSkillUseFail(s, SkillUseResult.NeedHp, id, type, skillId, actionId, actId, aniId); return; }
        if (!HasActiveSkill(m.MaintainSkills, skill.Template?.PrevActiveId ?? 0))
        { SendSkillUseFail(s, SkillUseResult.NeedPrevAct, id, type, skillId, actionId, actId, aniId); return; }
        if (!skill.CanUse(NowMs)) { _log.LogDebug("[summon] SKILLUSE {Id} skill {Skill} refused.", id, skillId); SendSkillUseFail(s, SkillUseResult.SpeedyUse, id, type, skillId, actionId, actId, aniId); return; }

        skill.UseSkill(NowMs, m.Attr?.AtkSpeed ?? 0, 100);             // CTMonster::GetAtkSpeed = attr dwAtkSpeed
        if (needHp != 0 || needMp != 0) { m.Hp -= needHp; m.Mp -= needMp; }

        var p = SummonPower(m, skill.Template, ch);
        byte canSelect = type == RecallMon.OtRecall ? (m.Template?.CanSelect ?? 1) : (byte)1;
        var ack = BuildCS_SKILLUSE_ACK(SkillUseResult.Success, id, type, skillId, actionId, actId, aniId,
            skill.Level, backSkill: 0, p.AttackLevel, p.Level, p.PysMin, p.PysMax, p.MgMin, p.MgMax,
            transHp: 0, transMp: 0, curseProb: 0, equipSpecial: 0, canSelect, p.Country, p.AidCountry, p.Crit, x, y, z, targets);
        foreach (var viewer in _state.PlayersAround(m))
        {
            viewer.Send(ack);
            if (needHp != 0 || needMp != 0) SendSummonHpMp(viewer, m);
        }
    }

    // ================================ skill finish ================================

    /// <summary>The summon branch of <c>OnCS_FINISHSKILL_ACK</c>: each monster its owner's client says the summon hit
    /// takes the hit, credited to the owner.</summary>
    private void SummonFinishSkill(ClientSession s, Character ch, byte type, uint id, SkillTemplate tpl, bool fake,
        float x, float y, float z, (uint Id, byte Type)[] targets)
    {
        if (OwnSummon(s, ch, type, id) is not { InMap: true } m)
        { _log.LogDebug("[summon] FINISHSKILL {Type}:{Id} skill {Skill}: not {Char}'s summon on this map; dropped.", type, id, tpl.Id, ch.CharId); return; }
        _log.LogDebug("[summon] FINISHSKILL {Type}:{Id} skill {Skill} -> {Targets}.", type, id, tpl.Id,
            string.Join(",", targets.Select(t => $"{t.Type}:{t.Id}")));
        var skill = m.Skills.FirstOrDefault(k => k.SkillId == tpl.Id);   // FindTSkill(m_wTriggerID)
        byte level = skill?.Level ?? 0;
        var power = HitPower(m, tpl, ch);
        // Who the monster hates. The old games (Source 3.3, OLD SOURCES) had no FINISHSKILL: a summon's hit came as
        // CS_DEFEND_REQ carrying the client's m_vTSKILLDATA.m_bAglow, and the monster hated the summon when it was set
        // (and its owner once it died). That flag starts as the template's bCanSelect (CTClientRecall::InitRecall) and
        // is overwritten by CS_SKILLUSE_ACK — the template's for OT_RECALL, TRUE for OT_SELF (OnCS_SKILLUSE_REQ). A
        // "skill" placed object never sends SKILLUSE (CheckAutoSKILL → ShotSkill → RangeSHOT → Defend), so it keeps
        // its template's: Mana Storm / Rain of Arrows / Ice Rain (not selectable) anger the owner, the Dark Crystal
        // and the eyes themselves. The 5.0 FINISHSKILL (bType == OT_PC: always the owner) is not kept.
        byte canSelect = type == RecallMon.OtRecall || m.RecallType == TrecallSkill ? (m.Template?.CanSelect ?? 1) : (byte)1;
        bool triple = type == RecallMon.OtRecall && m.RecallType == TrecallAutoAi;
        byte attackCountry = GetAttackCountry(m.Country, m.AidCountry);

        foreach (var (targetId, targetType) in targets)
        {
            // A player or someone else's summon (MapService.SummonPvP.cs) — the owner's kill. Its own buffs: not ported.
            if (targetType == OtPc)
            {
                if (_state.FindByChar(targetId) is not { State: EnterState.InGame, Char: { } tch } ts
                    || tch.CharId == ch.CharId || ts.Channel != s.Channel || tch.MapId != ch.MapId || !CanDuel(tch, ch)) continue;
                if (!tpl.IsNegative) continue;
                HitPlayer(s, ch, ch.CharId, id, type, tpl, level, tpl.Id, canSelect, ts, tch, 0, 0,
                    x, y, z, tch.PosX, tch.PosY, tch.PosZ, power, fake);
                continue;
            }
            if (targetType is RecallMon.OtRecall or RecallMon.OtSelf)
            {
                if (SummonInReach(ch, s, targetType, targetId) is { } pet && tpl.IsNegative)
                    HitSummon(ch, power, id, type, ch.CharId, tpl, level, tpl.Id, canSelect, pet, 0, 0, fake,
                        x, y, z, pet.PosX, pet.PosY, pet.PosZ);
                continue;
            }
            if (targetType != Monster.OtMon) continue;
            if (_state.FindMonster(targetId) is not { Hp: > 0 } mon) continue;
            if (tpl.IsNegative && mon.Country != TcontryN && (attackCountry == TcontryB || attackCountry == mon.Country)) continue;
            HitMonster(ch, power, id, type, ch.CharId, tpl, level, canSelect, mon, 0, 0, tpl.Id,
                forceMiss: fake, triple, x, y, z, mon.PosX, mon.PosY, mon.PosZ);
        }
    }

    /// <summary>C++ <c>SendCS_HPMP_ACK</c> for a summon (its own id and type).</summary>
    private static void SendSummonHpMp(ClientSession p, RecallMon m)
    {
        var w = new PacketWriter(Msg.CS_HPMP_ACK, capacity: 24);
        w.WriteUInt32(m.Id); w.WriteByte(m.ObjType);
        w.WriteUInt32(m.MaxHp); w.WriteUInt32(m.Hp); w.WriteUInt32(m.MaxMp); w.WriteUInt32(m.Mp);
        p.Send(w);
    }
}
