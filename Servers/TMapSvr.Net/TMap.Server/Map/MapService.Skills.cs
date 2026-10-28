using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Skills — the CS_SKILLUSE caster-side attack announce (<c>OnCS_SKILLUSE_REQ</c>, CSHandler.cpp:2429). The
/// client sends this <b>first</b> when a player swings a skill/attack; the server validates, deducts the
/// <b>caster's own</b> MP/HP (never a target's — that's <c>CS_DEFEND</c>, Phase 13), arms the reuse
/// cooldown, and broadcasts <c>CS_SKILLUSE_ACK</c> (the attack-power payload the follow-up <c>CS_DEFEND</c>
/// reads back) + <c>CS_HPMP_ACK</c> (the cost) to the near players. Closes the two-packet attack.
///
/// <para>A player casts only as its own main session. Learned-skill, map, combat/status, resource and cooldown
/// checks run before the weapon/consumable commit. The server selects multi-attack hits, spends the matching
/// equipped weapon's ammunition plus any required skill item, then authorizes the corresponding impacts.
/// Physical, ranged and magic power use the skill's effect rows. Monster and summon casters have separate paths.</para>
/// <para>Accepted ordinary player casts also wear the chart-selected equipment and announce its rolled
/// item skill. Remaining work includes transfer-cost fields and full target-distance validation.</para>
/// </summary>
public sealed partial class MapService
{
    // ---- TATTACK_DELAY (NetCode.h) — the m_bSpeedApply selector fallback when no template is linked. ----
    private const byte TadPhysical = 1;

    /// <summary>C++ <c>FindTSkill</c> for casting: the character's copy of the skill, if it has learned it. A skill
    /// held at level 0 has not been learned — in the old sources (Source 3.3 and OLD SOURCES) it simply does not
    /// exist: <c>CTPlayer::InitializeSkill</c> deletes a skill from <c>m_mapTSKILL</c> (and the hotkeys) when a reset
    /// brings it to 0, so every cast answers <c>SKILL_NOTFOUND</c>. This database lists every class skill at level 0
    /// (<c>TSTARTSKILL</c>, for the skill window's "learn" buttons), so the same rule is applied at lookup.
    /// A skill whose max level is 0 has no levels at all — Stance, Inner Eye, Falcon Eye, Rumapark's Retribution,
    /// Enslave and Evocate Monster: it is innate, held at level 0 for good (the client shows no level gauge for
    /// it), and always usable.</summary>
    private static Skill? LearnedSkill(Character ch, ushort skillId)
        => ch.Skills.FirstOrDefault(k => k.SkillId == skillId && IsLearned(k));

    private static bool IsLearned(Skill k) => k.Level > 0 || k.Template is { MaxLevel: 0 };

    private void OnCS_SKILLUSE_REQ(ClientSession s, PacketReader r)
    {
        // Request layout (CSHandler.cpp:2459-2471) then bCount × {dwTarget, bTargetType, bIsTarget}.
        uint attackId = r.ReadUInt32();     // dwAttackID (the caster object)
        byte attackType = r.ReadByte();     // bAttackType
        r.ReadByte();                       // bChannel
        r.ReadUInt16();                     // wMapID
        ushort skillId = r.ReadUInt16();    // wSkillID
        byte actionId = r.ReadByte();       // bActionID
        uint actId = r.ReadUInt32();        // dwActID
        uint aniId = r.ReadUInt32();        // dwAniID
        float posX = r.ReadFloat(), posY = r.ReadFloat(), posZ = r.ReadFloat();
        byte count = r.ReadByte();          // bCount
        // The client lists EVERY object in the skill's area and flags only the real target(s) with bIsTarget;
        // the rest are bystanders (C++ vMONSTER, used only for heal/buff aggro). Only flagged entries become
        // targets, capped at MAX_TARGET (TMapType.h:76). This matters because the client attacks exactly the
        // list echoed back in CS_SKILLUSE_ACK — echoing bystanders turned a single-target swing into a hit on
        // every monster standing near the target.
        var picked = new List<(uint id, byte type)>(count);
        for (int i = 0; i < count; i++)
        {
            uint tid = r.ReadUInt32();      // dwTarget
            byte ttype = r.ReadByte();      // bTargetType
            byte isTarget = r.ReadByte();   // bIsTarget
            if (isTarget != 0 && picked.Count < MaxTarget) picked.Add((tid, ttype));
        }
        var targets = picked.ToArray();

        if (s.State != EnterState.InGame || s.Char is null) return;

        // This phase: only a player casting. Resolve the attacker player by id (C++ m_mapPLAYER lookup;
        // must be an in-game main char). A missing attacker ⇒ silent return, exactly as the C++.
        // A monster caster: its HOST client sends this on the monster's behalf after CS_MONATTACK_ACK, and the
        // CS_SKILLUSE_ACK reply is what plays the monster's swing animation on every client.
        if (attackType == Monster.OtMon)
        {
            MonsterSkillUse(s, attackId, skillId, actionId, actId, aniId, posX, posY, posZ, targets);
            return;
        }
        // A summon / placed object: its owner's client announces it (CSHandler.cpp:2496-2528).
        if (attackType is RecallMon.OtRecall or RecallMon.OtSelf)
        {
            SummonSkillUse(s, s.Char, attackType, attackId, skillId, actionId, actId, aniId, posX, posY, posZ, targets);
            return;
        }
        if (attackType != OtPc || !s.IsMain || attackId != s.CharId) return;
        var casterSession = s;
        var ch = s.Char;

        // ---- skill known? (pATTACK->FindTSkill(wSkillID)) ----
        var skill = LearnedSkill(ch, skillId);
        if (skill is null) { SendSkillUseFail(s, SkillUseResult.NotFound, attackId, attackType, skillId, actionId, actId, aniId); return; }
        if (skill.Template is { MapId: not InvalidMapId } restricted && restricted.MapId != ch.MapId)
        { SendSkillUseFail(s, SkillUseResult.NotFound, attackId, attackType, skillId, actionId, actId, aniId); return; }
        // ---- a guild skill: the guild, the duty, its time (MapService.GuildSkill.cs) ----
        if (!CanCastGuildSkill(ch, skillId)) { SendSkillUseFail(s, SkillUseResult.NotFound, attackId, attackType, skillId, actionId, actId, aniId); return; }
        if (TournamentSkillBanned(ch, skillId) is { } banned)                             // the tournament (MapService.TournamentMatch.cs)
        { SendSkillUseFail(s, banned, attackId, attackType, skillId, actionId, actId, aniId); return; }
        if (ch.ArenaId != 0 && skillId == TournamentCantUseSkill)                       // not in an arena (MapService.Arena.cs)
        { SendSkillUseFail(s, SkillUseResult.NotFound, attackId, attackType, skillId, actionId, actId, aniId); return; }
        // ---- no hostile skill in a peace zone (C++ CheckPeaceZone, CSHandler.cpp:2584) ----
        if ((skill.Template?.IsNegative ?? true) && CheckPeaceZone(s.Char!))
        { SendSkillUseFail(s, SkillUseResult.PeaceZone, attackId, attackType, skillId, actionId, actId, aniId); return; }
        if (skill.Template is { IsNegative: true } && !BuffLayer.CanAttack(ch.MaintainSkills, player: true))
        { SendSkillUseFail(s, SkillUseResult.SpeedyUse, attackId, attackType, skillId, actionId, actId, aniId); return; }

        // ---- MP cost (GetRequiredMP vs GetPureMaxMP; strict <) ----
        uint needMp = skill.GetRequiredMp(StatEngine.PureMaxMp(ch, _templates));
        if (ch.Mp < needMp) { SendSkillUseFail(s, SkillUseResult.NeedMp, attackId, attackType, skillId, actionId, actId, aniId); return; }

        // ---- HP cost (GetRequiredHP vs GetPureMaxHP; <=, can't drop to/below 0) ----
        uint needHp = skill.GetRequiredHp(StatEngine.PureMaxHp(ch, _templates));
        if (ch.Hp <= needHp) { SendSkillUseFail(s, SkillUseResult.NeedHp, attackId, attackType, skillId, actionId, actId, aniId); return; }

        // ---- cooldown (non-mon caster: !CanUse ⇒ SPEEDYUSE) ----
        if (!HasActiveSkill(ch.MaintainSkills, skill.Template?.PrevActiveId ?? 0))
        { SendSkillUseFail(s, SkillUseResult.NeedPrevAct, attackId, attackType, skillId, actionId, actId, aniId); return; }
        if (!skill.CanUse(NowMs)) { SendSkillUseFail(s, SkillUseResult.SpeedyUse, attackId, attackType, skillId, actionId, actId, aniId); return; }

        if (skill.Template is { } castTemplate)
            targets = PickLoopTargets(castTemplate, skill.Level, targets.Select(t => (t.id, t.type, true)).ToList());
        if (!TryConsumeSkillItems(s, ch, skill.Template, targets.Length))
        { SendSkillUseFail(s, SkillUseResult.UnsuitWeapon, attackId, attackType, skillId, actionId, actId, aniId); return; }

        // ---- arm the cooldown (SkillUse), then deduct the caster's own HP/MP ----
        SkillUse(ch, skill, NowMs);
        if (needHp != 0 || needMp != 0) { ch.Hp -= needHp; ch.Mp -= needMp; }

        // Casting an offensive skill enters battle (C++ CSHandler.cpp:2918) — suppresses HP regen (Phase 16).
        if (skill.Template?.IsNegative ?? true) ch.EnterBattle(NowMs, RecoverInit);

        // ---- compute the attack-power payload (attack type / long from the skill data, Phase 15) ----
        bool isLong = skill.Template?.IsLongAttack() ?? false;
        bool isMagic = skill.Template?.GetAttackType() == SkillTemplate.SatMagic;
        ushort attackLevel = isMagic ? StatEngine.MagicAtkLevel(ch, _templates) : StatEngine.AttackLevel(ch, _templates);
        uint pysMin = StatEngine.MinAp(ch, arrow: isLong, _templates);
        uint pysMax = StatEngine.MaxAp(ch, arrow: isLong, _templates);
        uint mgMin = StatEngine.MinMagicAp(ch, _templates);
        uint mgMax = StatEngine.MaxMagicAp(ch, _templates);
        byte cp = isMagic ? StatEngine.CriticalMagicProb(ch, _templates) : StatEngine.CriticalPysProb(ch, _templates);

        // The announce carries pre-wear power, as in CSHandler.cpp:2953. Only an accepted ordinary cast
        // wears equipment; ACTION, LOOPSKILL and later/replayed impacts must not wear it again.
        ushort backSkill = WearSkillEquipment(s, ch, skill.Template);
        AuthorizePlayerCast(s, skill, targets, backSkill: backSkill);

        if (skill.Template is { } used) EraseBuffByAttack(casterSession, ch, used);   // C++ CSHandler.cpp:2971
        GuildSkillCast(ch, skillId, skill.Level);                                       // C++ CSHandler.cpp:2974: the guild's cooldown

        var ack = BuildCS_SKILLUSE_ACK(SkillUseResult.Success, attackId, attackType, skillId, actionId, actId, aniId,
            skill.Level, backSkill, attackLevel, ch.Level, pysMin, pysMax, mgMin, mgMax,
            transHp: 0, transMp: 0, curseProb: 0, equipSpecial: 0, canSelect: 1,
            ch.Country, ch.AidCountry, cp, posX, posY, posZ, targets);

        // ---- broadcast to the near players (GetNeerPlayer — includes the caster) ----
        foreach (var p in _state.NearView(casterSession))
        {
            p.Send(ack);
            if (needHp != 0 || needMp != 0) SendSkillCostHpMp(p, attackId, attackType, ch);
        }
    }

    /// <summary>C++ <c>MAX_TARGET</c> (TMapType.h:76) — the most targets one skill use may carry.</summary>
    private const int MaxTarget = 16;

    /// <summary>
    /// The monster branch of <c>OnCS_SKILLUSE_REQ</c>: the host client announces the monster's skill, and the map
    /// broadcasts <c>CS_SKILLUSE_ACK</c> so every nearby client animates the swing. Without it a monster attack
    /// dealt its damage with no animation at all.
    /// <para>For a monster the C++ skips the whole cooldown / MP-HP cost / item block (<c>if(bAttackType !=
    /// OT_MON)</c>) — its cooldown is armed server-side when the attack is decided — so this is announce-only.
    /// The attack payload is the monster's own (physical band, attack level, crit); monster magic attacks are
    /// not ported, so the magic band is 0.</para>
    /// <para><b>Hardening:</b> only the monster's host may drive it, the same rule as <c>CS_MONMOVE</c>. The C++
    /// does not check here, but it sends <c>CS_MONATTACK_ACK</c> to the host alone, so the host is the only
    /// legitimate sender.</para>
    /// </summary>
    private void MonsterSkillUse(ClientSession s, uint monId, ushort skillId, byte actionId, uint actId, uint aniId,
        float posX, float posY, float posZ, (uint id, byte type)[] targets)
    {
        if (_state.FindMonster(monId) is not { } mon || mon.HostId != s.CharId)
        { _log.LogDebug("[mon] SKILLUSE for mon {Mon} from char {Char}: not its host; dropped.", monId, s.CharId); return; }
        _log.LogDebug("[mon] SKILLUSE for mon {Mon} skill {Skill} from host {Char}, {Count} target(s).", monId, skillId, s.CharId, targets.Length);

        // The skill must be one of the monster's own chart skills (C++ pATTACK->FindTSkill on the monster).
        if (!_templates.MonsterTemplates.TryGetValue(mon.ChartId, out var mt) || !mt.Skills.Contains(skillId)
            || !_templates.Skills.ContainsKey(skillId))
        {
            SendSkillUseFail(s, SkillUseResult.NotFound, monId, Monster.OtMon, skillId, actionId, actId, aniId);
            return;
        }

        if (!HasActiveSkill(mon.MaintainSkills, _templates.Skill(skillId)?.PrevActiveId ?? 0))
        { SendSkillUseFail(s, SkillUseResult.NeedPrevAct, monId, Monster.OtMon, skillId, actionId, actId, aniId); return; }
        var power = AttackerPower.Of(mon, _templates.Skill(skillId));
        var ack = BuildCS_SKILLUSE_ACK(SkillUseResult.Success, monId, Monster.OtMon, skillId, actionId, actId, aniId,
            skillLevel: 1, backSkill: 0, attackLevel: power.AttackLevel, attackerLevel: mon.Level,
            pysMin: power.PysMin, pysMax: power.PysMax, mgMin: power.MgMin, mgMax: power.MgMax, transHp: 0, transMp: 0, curseProb: 0,
            equipSpecial: 0, canSelect: 1, mon.Country, mon.AidCountry, power.Crit, posX, posY, posZ, targets);
        foreach (var p in _state.PlayersAround(mon)) p.Send(ack);   // C++ GetNeerPlayer around the monster
    }

    /// <summary>C++ <c>CTObjBase::SkillUse</c> (TObjBase.cpp:4568): arm the standard cooldown with the
    /// caster's attack speed/rate, then (if the skill has a kind-delay) the shared same-kind group cooldown.</summary>
    private void SkillUse(Character ch, Skill skill, uint now)
    {
        byte speedApply = skill.Template?.SpeedApply ?? TadPhysical;
        uint rate = StatEngine.AtkSpeedRate(ch, speedApply, _templates);
        skill.UseSkill(now, StatEngine.AtkSpeed(ch, speedApply, _templates), rate);

        if (skill.Template is { KindDelay: > 0 and var kindDelay, Kind: var kind })
            foreach (var other in ch.Skills)
                if (other.Template?.Kind == kind) other.UseKind(now, kindDelay, rate);
    }

    // ---- senders ----

    /// <summary>A failure <c>CS_SKILLUSE_ACK</c> to the caster only (C++ 7-arg overload: result + echo, all
    /// power fields 0, an empty target list).</summary>
    private static void SendSkillUseFail(ClientSession s, SkillUseResult result, uint attackId, byte attackType,
        ushort skillId, byte actionId, uint actId, uint aniId)
        => s.Send(BuildCS_SKILLUSE_ACK(result, attackId, attackType, skillId, actionId, actId, aniId,
            skillLevel: 0, backSkill: 0, attackLevel: 0, attackerLevel: 0, pysMin: 0, pysMax: 0, mgMin: 0, mgMax: 0,
            transHp: 0, transMp: 0, curseProb: 0, equipSpecial: 0, canSelect: 0,
            attackCountry: 0, attackAid: 0, cp: 0, posX: 0, posY: 0, posZ: 0, targets: System.Array.Empty<(uint, byte)>()));

    /// <summary>C++ <c>SendCS_SKILLUSE_ACK</c> (CSSender.cpp:1552-1609) — byte-exact. Note <c>wBackSkill</c>
    /// sits right after <c>wSkillID</c> (the header comment is stale), and the trailing target list is
    /// <c>BYTE count</c> + count×{<c>DWORD id</c>, <c>BYTE type</c>}.</summary>
    private static byte[] BuildCS_SKILLUSE_ACK(SkillUseResult result, uint attackId, byte attackType, ushort skillId,
        byte actionId, uint actId, uint aniId, byte skillLevel, ushort backSkill, ushort attackLevel, byte attackerLevel,
        uint pysMin, uint pysMax, uint mgMin, uint mgMax, ushort transHp, ushort transMp, byte curseProb,
        byte equipSpecial, byte canSelect, byte attackCountry, byte attackAid, byte cp,
        float posX, float posY, float posZ, (uint id, byte type)[] targets)
    {
        var w = new PacketWriter(Msg.CS_SKILLUSE_ACK, capacity: 96);
        w.WriteByte((byte)result);    // bResult
        w.WriteUInt32(attackId);      // dwAttackID
        w.WriteByte(attackType);      // bAttackType
        w.WriteUInt16(skillId);       // wSkillID
        w.WriteUInt16(backSkill);     // wBackSkill  (right after wSkillID — the comment is stale)
        w.WriteByte(actionId);        // bActionID
        w.WriteUInt32(actId);         // dwActID
        w.WriteUInt32(aniId);         // dwAniID
        w.WriteByte(skillLevel);      // bSkillLevel
        w.WriteUInt16(attackLevel);   // wAttackLevel
        w.WriteByte(attackerLevel);   // bAttackerLevel
        w.WriteUInt32(pysMin);        // dwPysMinPower
        w.WriteUInt32(pysMax);        // dwPysMaxPower
        w.WriteUInt32(mgMin);         // dwMgMinPower
        w.WriteUInt32(mgMax);         // dwMgMaxPower
        w.WriteUInt16(transHp);       // wTransHP
        w.WriteUInt16(transMp);       // wTransMP
        w.WriteByte(curseProb);       // bCurseProb (always 0 in this build)
        w.WriteByte(equipSpecial);    // bEquipSpecial
        w.WriteByte(canSelect);       // bCanSelect
        w.WriteByte(attackCountry);   // bAttackCountry
        w.WriteByte(attackAid);       // bAttackAidCountry
        w.WriteByte(cp);              // bCP (crit prob)
        w.WriteFloat(posX); w.WriteFloat(posY); w.WriteFloat(posZ); // fGndPos
        w.WriteByte((byte)targets.Length); // bTargetCount
        foreach (var (id, type) in targets) { w.WriteUInt32(id); w.WriteByte(type); }
        return w.ToArray();
    }

    /// <summary>C++ <c>SendCS_HPMP_ACK</c> (CSSender.cpp:1315) for the skill's caster cost — id/type are the
    /// caster's <c>dwAttackID</c>/<c>bAttackType</c>, and the full (item+buff) MaxHP/MaxMP is reported.</summary>
    private void SendSkillCostHpMp(ClientSession p, uint attackId, byte attackType, Character ch)
    {
        var w = new PacketWriter(Msg.CS_HPMP_ACK, capacity: 24);
        w.WriteUInt32(attackId);
        w.WriteByte(attackType);
        w.WriteUInt32(MaxHpFor(ch));
        w.WriteUInt32(ch.Hp);
        w.WriteUInt32(MaxMpFor(ch));
        w.WriteUInt32(ch.Mp);
        p.Send(w);
        NotifyPartyManStat(p, attackId, attackType, MaxHpFor(ch), ch.Hp, MaxMpFor(ch), ch.Mp);
    }
}
