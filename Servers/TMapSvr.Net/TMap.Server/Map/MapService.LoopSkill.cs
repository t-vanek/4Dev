using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Loop skills — C++ <c>OnCS_LOOPSKILL_REQ</c> (CSHandler.cpp:3040). The client sends it for each repeat of a channelled
/// (loop-charge) skill and for a summon's repeating skill: like <c>CS_SKILLUSE_REQ</c>, the server checks the skill, its cooldown
/// (the skill's <c>dwLoopDelay</c>, <c>SDELAY_LOOP</c>) and its MP / HP cost, takes the cost and tells the players around
/// (<c>CS_LOOPSKILL_ACK</c>, with the attack figures the hits are worked out from).
/// <para><b>Multi-attack skills</b> (an <c>SDT_ABILITY</c> / <c>MTYPE_EFC</c> row — Mana Arrows, Flame Missiles…) fire that many
/// missiles: each picked target takes one plus a random few more (up to the skill's <c>bTargetHit</c>), and what is left
/// goes to the first target.</para>
/// <para>Casters: a player and its summons / placed objects; no hostile one in a peace zone. Player repeats recheck
/// the equipped weapon and spend their ammunition/skill items before resource or cooldown changes. Summons use
/// their own resources. Repeats require the caster's wTargetActiveID effect (the original loop gate).
/// <b>Not ported:</b> a monster caster and aggro on bystanders.</para>
/// </summary>
public sealed partial class MapService
{
    private void OnCS_LOOPSKILL_REQ(ClientSession s, PacketReader r)
    {
        uint attackId = r.ReadUInt32();
        byte attackType = r.ReadByte();
        r.ReadByte();                                                   // bChannel
        r.ReadUInt16();                                                 // wMapID
        ushort skillId = r.ReadUInt16();
        float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat();
        byte count = r.ReadByte();
        var listed = new List<(uint Id, byte Type, bool IsTarget)>(count);
        for (int i = 0; i < count; i++) listed.Add((r.ReadUInt32(), r.ReadByte(), r.ReadByte() != 0));

        if (s.State != EnterState.InGame || s.Char is not { } me) return;

        // The caster: a player (C++ any main player by id, as CS_SKILLUSE), or one of the sender's summons.
        Character? pc = null;
        RecallMon? summon = null;
        ClientSession casterSession = s;
        if (attackType == OtPc && s.IsMain && attackId == s.CharId)
        { pc = me; }
        else if (attackType is RecallMon.OtRecall or RecallMon.OtSelf && OwnSummon(s, me, attackType, attackId) is { InMap: true } m)
            summon = m;
        else return;

        var skill = pc is not null ? LearnedSkill(pc, skillId) : summon!.Skills.FirstOrDefault(k => k.SkillId == skillId);
        if (skill?.Template is not { } tpl) { SendLoopSkillFail(s, SkillUseResult.NotFound, attackId, attackType, skillId); return; }
        if (pc is not null && tpl.LoopDelay == 0 || tpl.MapId != InvalidMapId && tpl.MapId != me.MapId)
        { SendLoopSkillFail(s, SkillUseResult.NotFound, attackId, attackType, skillId); return; }
        if (tpl.IsNegative && CheckPeaceZone(me)) { SendLoopSkillFail(s, SkillUseResult.PeaceZone, attackId, attackType, skillId); return; }
        if (tpl.IsNegative && !BuffLayer.CanAttack(pc?.MaintainSkills ?? summon!.MaintainSkills, player: pc is not null))
        { SendLoopSkillFail(s, SkillUseResult.SpeedyUse, attackId, attackType, skillId); return; }

        var targets = PickLoopTargets(tpl, skill.Level, listed);

        if (!skill.CanUse(NowMs)) { SendLoopSkillFail(s, SkillUseResult.SpeedyUse, attackId, attackType, skillId); return; }
        uint pureMaxMp = pc is not null ? StatEngine.PureMaxMp(pc, _templates) : summon!.MaxMp;
        uint pureMaxHp = pc is not null ? StatEngine.PureMaxHp(pc, _templates) : summon!.MaxHp;
        uint needMp = skill.GetRequiredMp(pureMaxMp), needHp = skill.GetRequiredHp(pureMaxHp);
        if ((pc?.Mp ?? summon!.Mp) < needMp) { SendLoopSkillFail(s, SkillUseResult.NeedMp, attackId, attackType, skillId); return; }
        if ((pc?.Hp ?? summon!.Hp) <= needHp) { SendLoopSkillFail(s, SkillUseResult.NeedHp, attackId, attackType, skillId); return; }

        if (!HasActiveSkill(pc?.MaintainSkills ?? summon!.MaintainSkills, tpl.TargetActiveId))
        { SendLoopSkillFail(s, SkillUseResult.NeedPrevAct, attackId, attackType, skillId); return; }

        if (pc is not null && !TryConsumeSkillItems(s, pc, tpl, targets.Length))
        { SendLoopSkillFail(s, SkillUseResult.UnsuitWeapon, attackId, attackType, skillId); return; }

        // pSkill->Use(SDELAY_LOOP, tick, GetAtkSpeed, GetAtkSpeedRate) — the speed of the player who sent it, as the C++.
        byte speedApply = tpl.SpeedApply;
        skill.UseLoop(NowMs, StatEngine.AtkSpeed(me, speedApply, _templates), StatEngine.AtkSpeedRate(me, speedApply, _templates));
        if (pc is not null) { pc.Hp -= needHp; pc.Mp -= needMp; }
        else { summon!.Hp -= needHp; summon.Mp -= needMp; }
        if (pc is not null) AuthorizePlayerCast(s, skill, targets);

        var p = pc is not null ? AttackerPower.Of(pc, _templates, tpl) : SummonPower(summon!, tpl, me);
        byte canSelect = attackType == RecallMon.OtRecall ? (summon!.Template?.CanSelect ?? 1) : (byte)1;
        var ack = BuildCS_LOOPSKILL_ACK(SkillUseResult.Success, attackId, attackType, skillId, skill.Level, p.AttackLevel, p.Level,
            p.PysMin, p.PysMax, p.MgMin, p.MgMax, canSelect, p.Country, p.AidCountry, p.Crit, x, y, z, targets);
        var viewers = pc is not null ? _state.NearView(casterSession) : _state.PlayersAround(summon!);
        foreach (var v in viewers)
        {
            v.Send(ack);
            if (needHp == 0 && needMp == 0) continue;
            if (pc is not null) SendSkillCostHpMp(v, attackId, attackType, pc);
            else SendSummonHpMp(v, summon!);
        }
        _log.LogDebug("[loop] {Type}:{Id} skill {Skill} -> {Count} hit(s).", attackType, attackId, skillId, targets.Length);
    }

    /// <summary>The C++ target pick: the flagged targets (at most <c>MAX_TARGET</c> hits); a multi-attack skill spreads its
    /// missiles — one each, plus a random few more up to <c>bTargetHit</c> — and the rest go to the first target.</summary>
    private (uint Id, byte Type)[] PickLoopTargets(SkillTemplate tpl, byte level, List<(uint Id, byte Type, bool IsTarget)> listed)
    {
        var hits = new List<(uint, byte)>();
        bool multi = tpl.IsMultiAttack();
        int left = multi ? Math.Max(0, tpl.CalcAbilityValue(level, SaOnce, SkillTemplate.MtypeEfc, 1)) : 0;
        foreach (var (id, type, isTarget) in listed)
        {
            if (!isTarget || hits.Count >= MaxTarget) continue;
            if (!multi || left > 0) hits.Add((id, type));
            if (left <= 0) continue;
            left--;
            int roll = CombatRng.Next(tpl.TargetHit + 1);
            for (int hit = 1; left > 0 && hit < roll && hits.Count < MaxTarget; hit++) { hits.Add((id, type)); left--; }
        }
        while (left > 0 && hits.Count > 0 && hits.Count < MaxTarget) { hits.Add(hits[0]); left--; }
        return hits.ToArray();
    }

    private const byte SaOnce = 0;   // SKILL_ACTION SA_ONCE

    private static void SendLoopSkillFail(ClientSession s, SkillUseResult result, uint attackId, byte attackType, ushort skillId)
        => s.Send(BuildCS_LOOPSKILL_ACK(result, attackId, attackType, skillId, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            System.Array.Empty<(uint, byte)>()));

    /// <summary>C++ <c>SendCS_LOOPSKILL_ACK</c> (CSSender.cpp:1620).</summary>
    private static byte[] BuildCS_LOOPSKILL_ACK(SkillUseResult result, uint attackId, byte attackType, ushort skillId, byte skillLevel,
        ushort attackLevel, byte attackerLevel, uint pysMin, uint pysMax, uint mgMin, uint mgMax, byte canSelect, byte country,
        byte aidCountry, byte hit, float x, float y, float z, (uint Id, byte Type)[] targets)
    {
        var w = new PacketWriter(Msg.CS_LOOPSKILL_ACK, capacity: 64 + targets.Length * 5);
        w.WriteByte((byte)result); w.WriteUInt32(attackId); w.WriteByte(attackType); w.WriteUInt16(skillId);
        w.WriteByte(skillLevel); w.WriteUInt16(attackLevel); w.WriteByte(attackerLevel);
        w.WriteUInt32(pysMin); w.WriteUInt32(pysMax); w.WriteUInt32(mgMin); w.WriteUInt32(mgMax);
        w.WriteByte(canSelect); w.WriteByte(country); w.WriteByte(aidCountry); w.WriteByte(hit);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        w.WriteByte((byte)targets.Length);
        foreach (var (id, type) in targets) { w.WriteUInt32(id); w.WriteByte(type); }
        return w.ToArray();
    }
}
