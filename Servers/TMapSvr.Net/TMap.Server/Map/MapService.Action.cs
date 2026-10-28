using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The action/animation gate — the C# port of <c>OnCS_ACTION_REQ</c> (CSHandler.cpp:1233).
///
/// <para>This is the packet the client sends the moment a swing or a cast <i>starts</i>, and it blocks on the
/// ACK before playing the animation and moving on to <c>CS_SKILLUSE</c> / <c>CS_DEFEND</c>. Without it the
/// whole combat chain is unreachable from a real client no matter how complete the rest of it is: the
/// client simply never advances.</para>
///
/// <para>Two details the C++ makes easy to get wrong:</para>
/// <list type="number">
/// <item>The ACK goes to the 3x3 block <b>including the actor</b> — there is no
/// <c>m_dwID != pPlayer->m_dwID</c> guard here, unlike the <c>CS_MOVE</c> broadcast. The caster needs its
/// own copy or it never animates.</item>
/// <item>A failed precondition does <b>not</b> suppress the ACK. The C++ sets <c>bResult</c> and broadcasts
/// anyway, so the client learns <i>why</i> the swing did not start. Returning early would hang it.</item>
/// </list>
///
/// <para>The skill checks here are a pure gate: unlike <c>CS_SKILLUSE</c>, the C++ deducts no HP/MP and arms
/// no cooldown at this point.</para>
///
/// <para>The required previous active effect is checked before the accepted action erases buffs.
/// Summon actors resolve among the sender's own objects. The charge-skill latch remains deferred.</para>
/// </summary>
public sealed partial class MapService
{
    private void OnCS_ACTION_REQ(ClientSession s, PacketReader r)
    {
        // Request layout (CSHandler.cpp:1248-1256).
        uint objId = r.ReadUInt32();      // dwObjID
        byte objType = r.ReadByte();      // bObjType
        byte actionId = r.ReadByte();     // bActionID
        uint actId = r.ReadUInt32();      // dwActID
        uint aniId = r.ReadUInt32();      // dwAniID
        r.ReadByte();                     // bChannel
        r.ReadUInt16();                   // wMapID
        ushort skillId = r.ReadUInt16();  // wSkillID

        // C++ gate is `if(!pPlayer->m_pMAP) return;` — not yet placed in a map ⇒ nothing to broadcast to.
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;

        // ---- resolve the acting object (C++ switch on bObjType) ----
        // OT_PC resolves to the SENDER itself (not a lookup by dwObjID) — the C++ assigns pOBJ = pPlayer.
        float posX, posZ;
        RecallMon? summon = null;
        if (objType == OtPc)
        {
            if (!s.IsMain) return;
            posX = ch.PosX; posZ = ch.PosZ;
        }
        else if (objType == Monster.OtMon)
        {
            if (_state.FindMonster(objId) is not { } mon) return;
            posX = mon.PosX; posZ = mon.PosZ;
        }
        else
        {
            summon = objType switch
            {
                RecallMon.OtRecall when s.IsMain => ch.Recalls.GetValueOrDefault(objId),
                RecallMon.OtSelf => ch.SelfObjs.GetValueOrDefault(objId),
                RecallMon.OtCompanion when s.IsMain => ch.CompanionObjs.GetValueOrDefault(objId),
                _ => null,
            };
            if (summon is not { InMap: true }) return;
            posX = summon.PosX; posZ = summon.PosZ;
        }

        // ---- skill preconditions (C++ runs these only for a PC actor casting a real skill) ----
        // Note: result is carried into the ACK; a failure never suppresses the broadcast.
        var result = SkillUseResult.Success;
        if (skillId != 0 && objType == OtPc)
        {
            var skill = LearnedSkill(ch, skillId);
            if (skill is null)
                result = SkillUseResult.NotFound;
            else if (!skill.CanUse(NowMs))
                result = SkillUseResult.SpeedyUse;
            else if (ch.Mp < skill.GetRequiredMp(StatEngine.PureMaxMp(ch, _templates)))
                result = SkillUseResult.NeedMp;
            else if (ch.Hp < skill.GetRequiredHp(StatEngine.PureMaxHp(ch, _templates)))
                result = SkillUseResult.NeedHp;
            else if (!HasActiveSkill(ch.MaintainSkills, skill.Template?.PrevActiveId ?? 0))
                result = SkillUseResult.NeedPrevAct;
            // Evocate Monster with nothing tamed answers SKILL_NEEDPREVACT (CSHandler.cpp:1321).
            else if (skill.Template is { } mr && mr.IsMonRecall() && ch.Persist.TemptedMon == 0)
                result = SkillUseResult.NeedPrevAct;
            else if (skill.Template is { } st)
                foreach (var d in st.Data)
                    if (d.Type is SdtRecall or SdtTrap && d.Exec != SerMonster
                        && _templates.MonsterTemplates.TryGetValue((ushort)st.GetValue(d, skill.Level), out var mt) && mt.Id != 0)
                        CheckRecallMon(s, ch, mt);   // starting a summon cast sends the old main summon away
            if (result == SkillUseResult.Success && skill?.Template is { } used)
                EraseBuffByAttack(s, ch, used);              // C++ CSHandler.cpp:1362, the accepted skill action
        }

        // ---- broadcast (C++ GetNeerPlayer when a skill is involved, GetNeighbor otherwise; both keep self) ----
        var ack = BuildCS_ACTION_ACK(result, objId, objType, actionId, actId, aniId, skillId);
        var viewers = summon is not null ? _state.PlayersAround(summon) : skillId != 0 ? _state.NearView(s) : _state.InView(s);
        foreach (var viewer in viewers)
            viewer.Send(ack);
    }

    /// <summary>C++ <c>CTPlayer::SendCS_ACTION_ACK</c> (CSSender.cpp:1186) — a flat 17-byte body.</summary>
    private static byte[] BuildCS_ACTION_ACK(SkillUseResult result, uint objId, byte objType,
                                             byte actionId, uint actId, uint aniId, ushort skillId)
    {
        var w = new PacketWriter(Msg.CS_ACTION_ACK);
        w.WriteByte((byte)result);   // bResult
        w.WriteUInt32(objId);        // dwObjID
        w.WriteByte(objType);        // bObjType
        w.WriteByte(actionId);       // bActionID
        w.WriteUInt32(actId);        // dwActID
        w.WriteUInt32(aniId);        // dwAniID
        w.WriteUInt16(skillId);      // wSkillID
        return w.ToArray();
    }
}
