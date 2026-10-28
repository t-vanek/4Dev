using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The quest engine — the C# port of the C++ <c>CQuest</c> hierarchy + <c>CTMapSvrModule::CheckQuest</c> +
/// the <c>CTPlayer</c> quest state (<c>m_mapQUEST</c>). It drives the classic loop: a quest is <b>offered</b>
/// (an NPC-talk objective indicator / the possible-quest list), <b>accepted / run</b> (<c>CS_QUESTEXEC</c> →
/// the type's <c>ExecQuest</c>), its <b>objectives advance</b> as the universal <see cref="CheckQuest"/> hook
/// fires on talk / get-item / kill, and it is <b>turned in</b> (a <c>QT_COMPLETE</c> quest → <c>CheckComplete</c>
/// → <see cref="OnQuestComplete"/> reward grant → <c>CS_QUESTCOMPLETE_ACK</c>), recursing into child quests.
///
/// <para>Faithful-but-scoped: the 20-subtype C++ class hierarchy is modelled as a switch on the quest
/// <see cref="QuestType"/> (functionally identical). Phase 24 implemented the foundational four — <b>NpcTalk</b>
/// (offer), <b>Mission</b>/<b>Guild</b> (begin + hand over fetch items), <b>Complete</b> (turn-in + reward),
/// <b>GiveItem</b> (grant items) — plus <b>DefTalk</b> (no-op) and the base child-recursion. <b>Phase 29</b> adds
/// the <b>DB quest-template load</b> (so quests exist on a live server — see <c>GameDatabase.LoadTemplatesAsync</c>)
/// and five more subtypes: <b>DeleteItem</b> (remove item), <b>DropQuest</b> (abandon), <b>ChapterMsg</b> +
/// <b>Routing</b> (client packets), <b>Teleport</b> (same-map reposition).</para>
///
/// <para>Done later: <b>DefendSkill</b> (Phase 31, buff via <c>ForceMaintain</c>) and <b>Switch</b> (Phase 32,
/// the map switch/gate subsystem — <see cref="ExecSwitch"/> + the <c>QCT_SWITCH</c> condition). Still deferred
/// (documented, PORT_STATUS.md), each blocked on an unported subsystem or ambiguous self-state: <b>SpawnMon</b>/
/// <b>Regen</b> (need time-limited / dynamic monster spawn — <c>AddTimelimitedMon</c>/<c>RegenDynamicMonster</c>/
/// <c>DelMonSpawn</c>); <b>DieMon</b>/<b>DropItem</b> (monster force-kill / quest-loot attach — touch the
/// death/loot flow); <b>Craft</b> (self-completion via the quest's own running terms); <b>GiveSkill</b> (skill
/// learning), <b>SendPost</b> (mail). Plus: cross-map teleport; the exotic condition/term types (QCT_MONID,
/// monster-instance QTT_SPAWNID_DEL hunts); RT_MAGICITEM/SKILL/SKILLUP/TITLE/SOUL/POINT rewards.</para>
/// </summary>
public sealed partial class MapService
{
    // TRIGGER_TYPE (TT_*)
    private const byte TtExecQuest = 1, TtTalkNpc = 3, TtGetItem = 4, TtKillMon = 5, TtRunSwitch = 7, TtRunGate = 8, TtComplete = 9;
    // TERM_TYPE (QTT_*)
    private const byte QttCompQuest = 1, QttGetItem = 2, QttHunt = 3, QttSkillId = 4, QttItemId = 5, QttTimer = 6,
        QttMonId = 7, QttMapId = 8, QttLeft = 9, QttTop = 10, QttRight = 11, QttBottom = 12, QttTalk = 13,
        QttHeight = 14, QttSwitch = 15, QttSpawnId = 16, QttUseItem = 17, QttQuestCompleted = 18, QttSpawnIdDel = 21;
    // QUEST_CONDITION_TYPE (QCT_*)
    private const byte QctNone = 0, QctUpperLevel = 1, QctLowerLevel = 2, QctHaveQuest = 3, QctHaveItem = 4,
        QctClass = 5, QctMapId = 7, QctLeft = 8, QctTop = 9, QctRight = 10, QctBottom = 11, QctProb = 12,
        QctHaveNoItem = 13, QctCountry = 14, QctAfterQuestComplete = 15, QctSameLevel = 16, QctSex = 17,
        QctBeforeQuestComplete = 18, QctMaintainSkill = 19, QctSwitch = 20, QctCountMax = 21, QctNoParent = 22;
    // REWARD_TYPE (RT_*) / REWARD_METHOD (RM_*)
    private const byte RtGold = 1, RtItem = 2, RtSkill = 3, RtSkillUp = 4, RtExp = 6, RtMagicItem = 7;
    private const byte RmDefault = 1, RmSelect = 2, RmProb = 3, RmRandom = 4;
    // TNPC_TYPE TNPC_BOX — the bType of the routing item-list packet (CS_NPCITEMLIST_ACK).
    private const byte TNpcBox = 7;

    /// <summary>The trigger index (C++ <c>m_mapTRIGGER</c>): trigger type → keyed id → the quests that fire.
    /// Child quests land under <c>TT_EXECQUEST</c> keyed by their parent's id.</summary>
    private readonly Dictionary<byte, Dictionary<uint, List<QuestTemplate>>> _questTriggers = new();

    /// <summary>RNG for QCT_PROB / RM_PROB / RM_RANDOM reward rolls. Seedable for tests (not C <c>rand()</c>).</summary>
    public Random QuestRng { get; set; } = new();

    /// <summary>Builds the trigger index from the loaded quest templates (C++ startup wiring). Idempotent.</summary>
    public void InitQuests()
    {
        _questTriggers.Clear();
        foreach (var q in _templates.Quests.Values)
        {
            if (!_questTriggers.TryGetValue(q.TriggerType, out var byId))
                _questTriggers[q.TriggerType] = byId = new();
            if (!byId.TryGetValue(q.TriggerId, out var list))
                byId[q.TriggerId] = list = new();
            list.Add(q);
        }
        if (_templates.Quests.Count > 0)
            _log.LogInformation("Indexed {Quests} quests.", _templates.Quests.Count);
    }

    private List<QuestTemplate> TriggerQuests(byte triggerType, uint triggerId) =>
        _questTriggers.TryGetValue(triggerType, out var byId) && byId.TryGetValue(triggerId, out var list)
            ? list : _emptyQuests;
    private static readonly List<QuestTemplate> _emptyQuests = new();

    /// <summary>Rebuilds a character's saved quest progress into <c>m_mapQUEST</c> (C++ char-enter quest load,
    /// <c>CTBLQuestTable</c>/<c>CTBLQuestTermTable</c>): each row's trigger/complete counts + running-term
    /// counters + the remaining timer. A quest whose template is no longer in the chart is skipped. The load
    /// counterpart of the Phase-25 quest save. Public for DB-free tests.</summary>
    public void LoadQuestProgress(Character ch,
        IReadOnlyList<QuestLoadRow> quests, IReadOnlyList<QuestTermLoadRow> terms)
    {
        foreach (var q in quests)
        {
            // C++ drops a saved row whose template is missing OR is QT_NONE (type 0) — `if(pQuestTemp && m_bType)`
            // (SSHandler.cpp:5014).
            if (_templates.Quest(q.QuestId) is not { Type: not 0 } tmpl) continue;
            var qp = new QuestProgress { Template = tmpl, TriggerCount = q.TriggerCount, CompleteCount = q.CompleteCount };
            if (q.Tick != 0)
            {
                qp.TimerTick = q.Tick; qp.BeginTick = NowMs;   // resume the timer with `Tick` ms left
                qp.Save = true;                                // C++ re-flags m_bSave so the decaying timer re-persists
            }
            ch.Quests[q.QuestId] = qp;

            uint level = GetQuestLevel(tmpl);
            if (level != 0) ch.LevelQuest.TryAdd(level, q.QuestId);   // C++ std::map::insert keeps the first-loaded
        }
        foreach (var t in terms)
            if (ch.Quests.TryGetValue(t.QuestId, out var qp))
                qp.RunningTerms.Add(new RunningTerm { TermId = t.TermId, TermType = t.TermType, Count = t.Count });
    }

    // ---- the universal event hook (C++ CTMapSvrModule::CheckQuest) ----

    /// <summary>C++ <c>CTMapSvrModule::CheckQuest</c> — advance running-quest terms for this event, then fire
    /// any newly-eligible quests indexed under (<paramref name="triggerType"/>, <paramref name="termId"/>).</summary>
    private void CheckQuest(ClientSession s, uint targetId, float x, float y, float z,
                            uint termId, byte termType, byte triggerType, int count)
    {
        if (s.Char is not { } ch) return;
        PlayerCheckQuest(s, ch, termId, termType, targetId, count);

        foreach (var q in TriggerQuests(triggerType, termId).ToList())
            if (CanRunQuest(ch, q, out _) == QctNone)
                ExecQuest(s, ch, q, targetId, x, y, z);
    }

    /// <summary>C++ <c>CTPlayer::CheckQuest</c> — advance the matching running term on every active quest and
    /// push a <c>CS_QUESTUPDATE_ACK</c> when its count changed. Returns a quest id whose term matched (the
    /// NPC-talk objective indicator). The monster-instance (<c>QTT_SPAWNID_DEL</c>) proximity nuance is deferred.</summary>
    private uint PlayerCheckQuest(ClientSession s, Character ch, uint termId, byte termType, uint targetId, int count)
    {
        uint questId = 0;
        foreach (var qp in ch.Quests.Values)
        {
            if (!qp.IsRunning) continue;
            var tmpl = FindTemplateTerm(qp, termId, termType);
            if (tmpl is null) continue;

            var rt = FindRunningTerm(qp, termId, termType);
            questId = qp.Template.QuestId;

            byte prev = rt?.Count ?? 0;
            if (rt is not null)
                rt.Count = (byte)Math.Min(Math.Max(0, prev + count), tmpl.Count);

            if (rt is null || rt.Count != prev)
            {
                byte cur = rt?.Count ?? GetTermCount(ch, termId, termType);
                SendCS_QUESTUPDATE_ACK(s, qp.Template.QuestId, tmpl.TermId, tmpl.TermType, cur,
                    (byte)CheckTermStatus(ch, qp, tmpl));
                qp.Save = true;
            }
        }
        return questId;
    }

    // ---- eligibility (C++ CanRunQuest / CheckQuestCondition / CheckLevelCondition) ----

    /// <summary>C++ <c>CTPlayer::CanRunQuest</c> — QCT_NONE (0) if the quest may fire now, else a QCT_* reason.</summary>
    private byte CanRunQuest(Character ch, QuestTemplate q, out byte level)
    {
        level = 0;
        if (ch.FindQuest(q.QuestId) is { } prog)
        {
            if (q.CountMax != 0) { if (prog.TriggerCount >= q.CountMax) return QctCountMax; }
            else if (prog.TriggerCount > prog.CompleteCount) return QctCountMax;
        }

        if (q.ParentId != 0)
        {
            if (ch.FindQuest(q.ParentId) is not { } parent) return QctNoParent;
            if (q.Type == (byte)QuestType.Complete &&
                (!ch.IsRunningQuest(q.ParentId) ||
                 (parent.Template.CountMax != 0 && parent.CompleteCount >= parent.Template.CountMax)))
                return QctCountMax;
            if (q.Type == (byte)QuestType.Mission &&
                (parent.TriggerCount == 0 || parent.TriggerCount > parent.CompleteCount))
                return QctCountMax;
        }

        byte cond = CheckQuestCondition(ch, q, out level);
        if (cond != QctNone) return cond;

        return ch.IsRunningQuest(q.QuestId) ? QctCountMax : QctNone;
    }

    /// <summary>C++ <c>CheckLevelCondition</c> — the UPPER/LOWER-level gate (returns the required level).</summary>
    private static byte CheckLevelCondition(Character ch, QuestTemplate q, out byte level)
    {
        level = 0;
        byte result = QctNone;
        foreach (var c in q.Conditions)
        {
            result = c.ConditionType;
            switch (c.ConditionType)
            {
                case QctUpperLevel: result = ch.Level >= c.ConditionId ? QctNone : QctUpperLevel; level = (byte)c.ConditionId; break;
                case QctLowerLevel: result = ch.Level <= c.ConditionId ? QctNone : QctLowerLevel; break;
                default: result = QctNone; break;
            }
            if (result != QctNone) return result;
        }
        return result;
    }

    /// <summary>C++ <c>CheckQuestCondition</c> — evaluates each condition (QCT_NONE = pass). With
    /// <c>m_bConditionCheck == 0</c> ALL must pass (first failure returns); with 1 ANY passing condition
    /// suffices. An unmodelled condition type keeps its (non-zero) code — i.e. fails safe (the quest won't
    /// erroneously offer), matching the C++ fall-through.</summary>
    private byte CheckQuestCondition(Character ch, QuestTemplate q, out byte level)
    {
        byte levelCond = CheckLevelCondition(ch, q, out level);
        byte result = QctNone;
        foreach (var c in q.Conditions)
        {
            result = c.ConditionType;
            switch (c.ConditionType)
            {
                case QctUpperLevel:
                case QctLowerLevel: result = QctNone; break;                                       // handled above
                case QctMapId: result = ch.MapId == c.ConditionId ? QctNone : QctMapId; break;
                case QctLeft: result = ch.PosX >= c.ConditionId ? QctNone : QctLeft; break;
                case QctTop: result = ch.PosZ <= c.ConditionId ? QctNone : QctTop; break;
                case QctRight: result = ch.PosX <= c.ConditionId ? QctNone : QctRight; break;
                case QctBottom: result = ch.PosZ >= c.ConditionId ? QctNone : QctBottom; break;
                case QctProb: result = (uint)QuestRng.Next(100) < c.ConditionId ? QctNone : QctProb; break;
                case QctClass: result = (c.ConditionId & (1u << ch.Class)) != 0 ? QctNone : QctClass; break;
                case QctHaveItem: result = HasItem(ch, (ushort)c.ConditionId) ? QctNone : QctHaveItem; break;
                case QctHaveNoItem: result = HasItem(ch, (ushort)c.ConditionId) ? QctHaveNoItem : QctNone; break;
                case QctCountry: result = c.ConditionId == ch.Country ? QctNone : QctCountry; break; // GetWarCountry deferred
                case QctSex: result = c.ConditionId == ch.Sex ? QctNone : QctSex; break;
                case QctMaintainSkill: result = ch.MaintainSkills.Any(m => m.SkillId == c.ConditionId) ? QctNone : QctMaintainSkill; break;
                case QctHaveQuest:
                    {
                        var parent = ch.FindQuest(c.ConditionId);
                        if (parent is { IsRunning: true })
                        {
                            var unmet = CheckComplete(ch, parent);
                            result = q.Type == (byte)QuestType.Complete || (unmet is not null && unmet.TermType != QttTimer)
                                ? QctNone : QctHaveQuest;
                        }
                        // parent missing / not running ⇒ stays QCT_HAVEQUEST (fail)
                    }
                    break;
                case QctAfterQuestComplete:
                    {
                        result = QctNone;
                        var fin = ch.FindQuest(c.ConditionId);
                        if (fin is null || fin.CompleteCount == 0 || fin.CompleteCount < c.Count)
                            result = QctAfterQuestComplete;
                    }
                    break;
                case QctBeforeQuestComplete:
                    {
                        var fin = ch.FindQuest(c.ConditionId);
                        result = fin is null || fin.IsRunning ? QctNone : QctBeforeQuestComplete;
                    }
                    break;
                case QctSameLevel:
                    result = ch.LevelQuest.ContainsKey(c.ConditionId) ? QctSameLevel : QctNone;
                    break;
                case QctSwitch:
                    // C++ TPlayer.cpp:2379 — condition id = switch id, count = expected open state; pass iff the
                    // switch exists on the player's map instance AND its open state matches.
                    result = SwitchFor(ch, c.ConditionId) is { } sw && (sw.Opened ? 1 : 0) == c.Count
                        ? QctNone : QctSwitch;
                    break;
                // QCT_MONID and any other unmodelled type: keep the (non-zero) code = fail-safe.
            }

            if (result != QctNone && q.ConditionCheck == 0) return result;              // AND: first failure
            if (result == QctNone && q.ConditionCheck != 0 && levelCond == QctNone) return QctNone; // OR: first pass
        }
        return result != QctNone ? result : levelCond;
    }

    private static bool HasItem(Character ch, ushort itemId) =>
        ch.Invens.Any(inv => inv.Items.Any(it => it.TemplateId == itemId));

    private static uint GetQuestLevel(QuestTemplate q)
    {
        foreach (var c in q.Conditions)
            if (c.ConditionType == QctSameLevel) return c.ConditionId;
        return 0;
    }

    // ---- term state (C++ CQuest::FindTerm / FindRunningTerm / CheckComplete / GetTermCount) ----

    private static QuestTerm? FindTemplateTerm(QuestProgress qp, uint termId, byte termType)
    {
        foreach (var t in qp.Template.Terms)
            if ((t.TermId == termId && t.TermType == termType) || (termType == QttSpawnIdDel && t.TermType == QttSpawnIdDel))
                return t;
        return null;
    }

    /// <summary>C++ <c>FindRunningTerm</c> — the mutable counter for a term, creating one for the counting
    /// types (HUNT / TALK / USEITEM / SPAWNID_DEL); returns null for count-from-state terms (e.g. GETITEM).</summary>
    private static RunningTerm? FindRunningTerm(QuestProgress qp, uint termId, byte termType)
    {
        foreach (var rt in qp.RunningTerms)
            if ((rt.TermId == termId && rt.TermType == termType) || (termType == QttSpawnIdDel && rt.TermType == QttSpawnIdDel))
                return rt;

        if (termType is QttHunt or QttTalk or QttSpawnIdDel or QttUseItem)
        {
            var rt = new RunningTerm { TermId = termId, TermType = termType, Count = 0 };
            qp.RunningTerms.Add(rt);
            return rt;
        }
        return null;
    }

    /// <summary>C++ <c>CQuest::CheckComplete()</c> — the first term not yet satisfied, or null when all are.</summary>
    private QuestTerm? CheckComplete(Character ch, QuestProgress qp)
    {
        foreach (var t in qp.Template.Terms)
            if (CheckTermStatus(ch, qp, t) != QuestTermStatus.Success)
                return t;
        return null;
    }

    /// <summary>C++ <c>CQuest::CheckComplete(pTERM)</c> — one term's status (RUN / SUCCESS / FAILED).</summary>
    private QuestTermStatus CheckTermStatus(Character ch, QuestProgress qp, QuestTerm term)
    {
        switch (term.TermType)
        {
            case QttGetItem:
                if (GetTermCount(ch, term.TermId, QttGetItem) < term.Count || term.Count == 0)
                    return QuestTermStatus.Run;
                break;
            case QttTalk:
            case QttHunt:
            case QttSpawnIdDel:
            case QttUseItem:
                {
                    var rt = FindRunningTerm(qp, term.TermId, term.TermType);
                    if (rt is null || rt.Count < term.Count) return QuestTermStatus.Run;
                }
                break;
            case QttTimer:
                if (qp.BeginTick != 0 && NowMs - qp.BeginTick > qp.TimerTick) return QuestTermStatus.Failed;
                break;
            case QttLeft: if (ch.PosX < term.TermId) return QuestTermStatus.Run; break;
            case QttTop: if (ch.PosZ > term.TermId) return QuestTermStatus.Run; break;
            case QttRight: if (ch.PosX > term.TermId) return QuestTermStatus.Run; break;
            case QttBottom: if (ch.PosZ < term.TermId) return QuestTermStatus.Run; break;
            case QttQuestCompleted:
                {
                    var comp = ch.FindQuest(term.TermId);
                    if (comp is null || comp.CompleteCount < term.Count) return QuestTermStatus.Run;
                }
                break;
        }
        return QuestTermStatus.Success;
    }

    /// <summary>C++ <c>CTPlayer::GetTermCount</c> — the live count for a count-from-state term.</summary>
    private static byte GetTermCount(Character ch, uint termId, byte termType)
    {
        int count = 0;
        switch (termType)
        {
            case QttGetItem:
                foreach (var inv in ch.Invens)
                    foreach (var it in inv.Items)
                        if (it.TemplateId == termId) count += it.Count;
                break;
            case QttQuestCompleted:
                if (ch.FindQuest(termId) is { } comp) count = comp.CompleteCount;
                break;
        }
        return (byte)Math.Min(count, 0xFF);
    }

    /// <summary>C++ <c>CTPlayer::DropQuest</c> — abandon one trigger of a quest, resetting its counting terms.</summary>
    private static void DropQuest(Character ch, uint questId)
    {
        if (ch.FindQuest(questId) is not { } qp) return;
        uint level = GetQuestLevel(qp.Template);
        if (level != 0) ch.LevelQuest.Remove(level);

        if (qp.TriggerCount > qp.CompleteCount)
        {
            qp.TriggerCount--;
            qp.BeginTick = 0;
            qp.TimerTick = 0;
            qp.Save = true;
            foreach (var rt in qp.RunningTerms)
                if (rt.TermType is QttHunt or QttTalk or QttUseItem) rt.Count = 0;
        }
    }

    // ---- the subtype dispatch (C++ CQuest::CreateQuest → the type's ExecQuest) ----

    private void ExecQuest(ClientSession s, Character ch, QuestTemplate q, uint monId, float x, float y, float z,
                           byte rewardType = 0, uint rewardId = 0)
    {
        switch ((QuestType)q.Type)
        {
            case QuestType.NpcTalk: ExecTalk(s, ch, q, monId); break;
            case QuestType.Mission:
            case QuestType.Guild: ExecMission(s, ch, q, monId, x, y, z); break;
            case QuestType.Complete: ExecComplete(s, ch, q, rewardType, rewardId); break;
            case QuestType.Craft: ExecCraft(s, ch, q, rewardType, rewardId); break;
            case QuestType.GiveItem: ExecGiveItem(s, ch, q, monId, x, y, z); break;
            case QuestType.DeleteItem: ExecDeleteItem(s, ch, q); break;        // Phase 29
            case QuestType.DropQuest: ExecDropQuest(s, ch, q); break;          // Phase 29
            case QuestType.ChapterMsg: ExecChapterMsg(s, ch, q); break;        // Phase 29
            case QuestType.Routing: ExecRouting(s, ch, q); break;              // Phase 29
            case QuestType.Teleport: ExecTeleport(s, ch, q, monId, x, y, z); break; // Phase 29
            case QuestType.DefendSkill: ExecDefendSkill(s, ch, q); break;      // Phase 31
            case QuestType.Switch: ExecSwitch(s, ch, q); break;                // Phase 32
            case QuestType.SpawnMon: ExecSpawnMon(s, ch, q); break;            // Phase 33
            case QuestType.DieMon: ExecDieMon(s, ch, q); break;                // Phase 33
            case QuestType.DropItem: ExecDropItem(s, ch, q, monId); break;     // Phase 33
            case QuestType.Regen: ExecRegen(s, ch, q, monId); break;           // Phase 34
            case QuestType.GiveSkill: ExecGiveSkill(s, ch, q); break;          // Phase 36
            case QuestType.DefTalk: break;                 // no-op
            default: ExecChildren(s, ch, q, monId, x, y, z); break;   // base: recurse children
        }
    }

    /// <summary>C++ <c>CQuestDefendSkill::ExecQuest</c> (QuestDefendSkill.cpp:12) — grant each <c>QTT_SKILLID</c>
    /// term's skill as a maintained buff (level 1, self-cast) via <see cref="ForceMaintain"/>, then recurse
    /// children. The C++ guard <c>if(!CanRunQuest(...))</c> fires when the quest <b>is</b> runnable
    /// (<c>CanRunQuest</c> returns <c>QCT_NONE</c>=0), matching the standard subtype gate. Unblocks the
    /// Phase-29-deferred <c>DefendSkill</c> subtype now that the buff engine exists.</summary>
    private void ExecDefendSkill(ClientSession s, Character ch, QuestTemplate q)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;   // C++ if(!CanRunQuest): CanRunQuest==0 (QCT_NONE) ⇒ runnable
        bool granted = false;
        foreach (var term in q.Terms)
            if (term.TermType == QttSkillId && _templates.Skill((ushort)term.TermId) is not null)
            {
                ForceMaintain(s, ch, (ushort)term.TermId, ch.CharId, OtPc, ch.CharId, OtPc, 0);
                granted = true;
            }
        if (granted) ExecChildren(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ);
    }

    /// <summary>C++ <c>CQuestGiveSkill::ExecQuest</c> (QuestGiveSkill.cpp:20) — learn the <c>QTT_SKILLID</c>
    /// term's skill (id = term id, level = the term count floored at 1) via the add-only
    /// <see cref="UpdateSkill"/>, gated on the skill existing in the chart and its class mask matching the
    /// player (<c>m_dwClassID &amp; BITSHIFTID(m_bClass)</c>). Recurses children <b>only</b> when the skill was
    /// newly granted — the C++ <c>&amp;&amp;</c> short-circuits on <c>UpdateSkill</c> returning TRUE, so an
    /// already-known skill blocks the completion chain. Runnable-gated. Completes the Phase-29-deferred
    /// <c>GiveSkill</c> subtype now that the skill-learn path exists (Phase 36).</summary>
    private void ExecGiveSkill(ClientSession s, Character ch, QuestTemplate q)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        ushort skillId = 0; byte level = 0;
        foreach (var t in q.Terms)
            if (t.TermType == QttSkillId) { skillId = (ushort)t.TermId; level = t.Count; }  // C++ keeps the last QTT_SKILLID
        if (_templates.Skill(skillId) is not { } temp) return;                              // pSKILLTEMP->find miss
        if (!temp.IsClassMatch(ch.Class)) return;                                           // m_dwClassID & BITSHIFTID(m_bClass)
        if (UpdateSkill(s, ch, temp, skillId, (byte)Math.Max(1, (int)level)))               // max(bLevel,1); TRUE ⇒ newly learned
            ExecChildren(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ);
    }

    /// <summary>C++ <c>CQuestSwitch::ExecQuest</c> (QuestSwitch.cpp:11) — flip each <c>QTT_SWITCH</c> term's
    /// switch via the player <see cref="ChangeSwitchPlayer"/> (the term's id is the switch id; it's a toggle,
    /// no target state), and if any actually flipped, recurse children. Gated on runnable (C++ <c>if(!CanRunQuest)</c>
    /// ⇒ <c>QCT_NONE</c>). Unblocks the Phase-29-deferred <c>Switch</c> subtype now that the switch subsystem exists.</summary>
    private void ExecSwitch(ClientSession s, Character ch, QuestTemplate q)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        bool flipped = false;
        foreach (var term in q.Terms)
            if (term.TermType == QttSwitch && ChangeSwitchPlayer(s, ch, term.TermId))
                flipped = true;
        if (flipped) ExecChildren(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ);
    }

    /// <summary>Resolves a switch on the character's current (channel, map) instance — the channel comes from the
    /// player's live session (a <see cref="Character"/> carries no channel).</summary>
    private MapSwitch? SwitchFor(Character ch, uint switchId)
        => _state.FindByChar(ch.CharId) is { } s ? _state.FindSwitch(s.Channel, ch.MapId, switchId) : null;

    /// <summary>C++ <c>CQuestSpawnMon::ExecQuest</c> (QuestSpawnMon.cpp:21) — for each <c>QTT_SPAWNID</c> term
    /// add a time-limited spawn (<see cref="AddTimelimitedMon"/>; the term's count is the REGEN_TYPE), and for
    /// each <c>QTT_SPAWNID_DEL</c> term hard-remove a spawn (<see cref="DelMonSpawn"/>). Recurses children only
    /// if at least one spawn was added (C++ <c>bCanSpawn</c>). Runnable-gated. Unblocks the Phase-29 subtype.</summary>
    private void ExecSpawnMon(ClientSession s, Character ch, QuestTemplate q)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        long nowMs = _tickSeconds * 1000L;
        bool spawned = false;
        foreach (var term in q.Terms)
        {
            if (term.TermType == QttSpawnId)
            {
                if (AddTimelimitedMon((ushort)term.TermId, s.Channel, term.Count, nowMs)) spawned = true;
            }
            else if (term.TermType == QttSpawnIdDel)
            {
                DelMonSpawn((ushort)term.TermId, s.Channel);   // del-only term does not set `spawned`
            }
        }
        if (spawned) ExecChildren(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ);
    }

    /// <summary>C++ <c>CQuestDieMon::ExecQuest</c> (QuestDieMon.cpp:13) — force-kill every live monster of each
    /// <c>QTT_SPAWNID</c> term's spawn: silently for an <c>SE_QUESTDEL</c> spawn (no reward, no re-arm), else a
    /// credited kill (the quester becomes keeper → exp/loot/kill-quest via the normal death path). Always
    /// recurses children. Runnable-gated. Unblocks the Phase-29 subtype.</summary>
    private void ExecDieMon(ClientSession s, Character ch, QuestTemplate q)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        foreach (var term in q.Terms)
            if (term.TermType == QttSpawnId)
                ForceKillSpawn(ch, (ushort)term.TermId, s.Channel);
        ExecChildren(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ);
    }

    /// <summary>C++ <c>CQuestRegen::ExecQuest</c> (QuestRegen.cpp:12) — for each <c>QTT_MONID</c> term mint a
    /// one-shot dynamic spawn of that monster kind at the killed monster's death position
    /// (<see cref="RegenDynamicMonster"/> + <see cref="AddTimelimitedMon"/>), and link it to the killed monster's
    /// slot so its respawn force-removes the temp (C++ <c>m_wRegenDelSpawn</c>). Recurses children only if a
    /// spawn was minted. Runnable-gated. The term count is the roam type (inert here). Phase 34.</summary>
    private void ExecRegen(ClientSession s, Character ch, QuestTemplate q, uint monId)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        // C++ uses the killed monster's death position (m_fPosX/Y/Z); resolve it from the still-registered corpse.
        var origin = _state.FindMonster(monId);
        float x = origin?.PosX ?? ch.PosX, y = origin?.PosY ?? ch.PosY, z = origin?.PosZ ?? ch.PosZ;
        long nowMs = _tickSeconds * 1000L;
        bool regen = false;
        foreach (var term in q.Terms)
        {
            if (term.TermType != QttMonId) continue;
            ushort spawnId = RegenDynamicMonster(ch.MapId, TContryN, (ushort)term.TermId, x, y, z, term.Count);
            if (spawnId == 0) continue;                                   // unknown monster kind / pool exhausted
            if (!AddTimelimitedMon(spawnId, s.Channel, regenType: 0 /*RT_ETERNAL*/, nowMs)) continue;
            LinkRegenDel(monId, s.Channel, spawnId);                      // stash on the killed monster's slot
            regen = true;
        }
        if (regen) ExecChildren(s, ch, q, monId, x, y, z);
    }

    /// <summary>C++ <c>CQuestDropItem::ExecQuest</c> (QuestDropItem.cpp:21) — pick ONE random <c>QTT_ITEMID</c>
    /// term and attach an owner-locked item (owner = the quest holder) to the just-killed monster's corpse.
    /// Triggered on death via the <c>TT_KILLMON</c> hook (so <paramref name="monId"/> is the dead monster). No
    /// runnable gate, no child recursion (C++). Unblocks the Phase-29 subtype.</summary>
    private void ExecDropItem(ClientSession s, Character ch, QuestTemplate q, uint monId)
    {
        var itemTerms = q.Terms.Where(t => t.TermType == QttItemId).ToList();
        if (itemTerms.Count == 0) return;
        var term = itemTerms[QuestRng.Next(itemTerms.Count)];
        if (term.Count == 0) return;                          // C++ !m_bCount ⇒ no drop
        if (_state.FindMonster(monId) is not { } mon) return;
        var tpl = _templates.Item((ushort)term.TermId);
        if (tpl is null && _templates.HasItems) return;       // C++ requires the item template to exist
        AddCorpseItem(mon, (ushort)term.TermId, term.Count, ch.CharId, tpl);
    }

    /// <summary>Base <c>CQuest::ExecQuest</c> — recurse into child quests (indexed under TT_EXECQUEST by this
    /// quest's id) that are now eligible.</summary>
    private void ExecChildren(ClientSession s, Character ch, QuestTemplate q, uint monId, float x, float y, float z)
    {
        foreach (var child in TriggerQuests(TtExecQuest, q.QuestId).ToList())
            if (CanRunQuest(ch, child, out _) == QctNone)
                ExecQuest(s, ch, child, monId, x, y, z);
    }

    /// <summary>C++ <c>CQuestTalk::ExecQuest</c> — offer the quest (NPC-talk dialog): the quest id if runnable,
    /// else 0.</summary>
    private void ExecTalk(ClientSession s, Character ch, QuestTemplate q, uint npcId)
    {
        uint offered = CanRunQuest(ch, q, out _) == QctNone ? q.QuestId : 0;
        // Direct quest execution has no monster/event target; the client matches this ID to its NPC.
        ushort dialogNpc = q.TriggerType == TtTalkNpc ? (ushort)q.TriggerId : (ushort)npcId;
        SendCS_NPCTALK_ACK(s, offered, dialogNpc);
    }

    /// <summary>C++ <c>CQuestMission::ExecQuest</c> — begin the quest: register it, announce
    /// <c>CS_QUESTADD_ACK</c>, bump the trigger count, prime its terms, hand over the fetch item(s), start any
    /// timer, then recurse children.</summary>
    private void ExecMission(ClientSession s, Character ch, QuestTemplate q, uint monId, float x, float y, float z)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;

        if (ch.FindQuest(q.QuestId) is not { } qp)
        {
            qp = new QuestProgress { Template = q };
            ch.Quests[q.QuestId] = qp;
        }

        uint level = GetQuestLevel(q);
        if (level != 0) ch.LevelQuest[level] = q.QuestId;

        SendCS_QUESTADD_ACK(s, q.QuestId, q.Type);

        if (qp.TriggerCount == 0xFF) { qp.TriggerCount = 1; qp.CompleteCount = 1; }
        qp.TriggerCount++;
        qp.Save = true;

        foreach (var t in q.Terms) PlayerCheckQuest(s, ch, t.TermId, t.TermType, 0, 0);

        GiveFetchItems(s, ch, q, x, y, z);

        foreach (var t in q.Terms)
            if (t.TermType == QttTimer)
            {
                qp.TimerTick = t.TermId;
                qp.BeginTick = NowMs;
                SendCS_QUESTSTARTTIMER_ACK(s, q.QuestId, qp.TimerTick);
            }

        ExecChildren(s, ch, q, monId, x, y, z);
    }

    /// <summary>The C++ <c>CQuestMission</c> starter-item hand-over: for a QTT_GETITEM term backed by a
    /// matching QTT_ITEMID term, if the player doesn't already have the item, create and push it (advancing the
    /// GETITEM term); on a full bag, drop the parent quest with QR_INVENTORYFULL.</summary>
    private void GiveFetchItems(ClientSession s, Character ch, QuestTemplate q, float x, float y, float z)
    {
        foreach (var t in q.Terms)
        {
            if (t.TermType != QttGetItem) continue;
            bool marked = q.Terms.Any(t2 => t2.TermType == QttItemId && t2.TermId == t.TermId);
            if (!marked) continue;
            if (HasItem(ch, (ushort)t.TermId)) continue;
            if (t.Count == 0 || _templates.Item((ushort)t.TermId) is not { } tmpl) continue;

            var item = new Item { TemplateId = (ushort)t.TermId, Count = t.Count, Template = tmpl };
            LinkItemAttr(item);
            var one = new[] { item };
            if (CanPush(ch, one))
            {
                PushTItem(s, one);
                CheckQuest(s, 0, x, y, z, t.TermId, QttGetItem, TtGetItem, t.Count);
                SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.Success);
            }
            else
            {
                DropQuest(ch, q.ParentId);
                SendCS_QUESTCOMPLETE_ACK(s, QuestResult.InventoryFull, q.ParentId, 0, 0, q.ParentId);
            }
            break; // C++ breaks after the first matched fetch item
        }
    }

    /// <summary>C++ <c>CQuestGiveItem::ExecQuest</c> — grant the QTT_ITEMID item(s) (or run the referenced
    /// COMPQUEST), then recurse children.</summary>
    private void ExecGiveItem(ClientSession s, Character ch, QuestTemplate q, uint monId, float x, float y, float z)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;

        uint compQuestId = 0;
        var give = new List<Item>();
        foreach (var t in q.Terms)
        {
            if (t.TermType == QttItemId)
            {
                if (t.Count != 0 && _templates.Item((ushort)t.TermId) is { } tmpl)
                {
                    var item = new Item { TemplateId = (ushort)t.TermId, Count = t.Count, Template = tmpl };
                    LinkItemAttr(item);
                    give.Add(item);
                }
            }
            else if (t.TermType == QttCompQuest)
            {
                compQuestId = t.TermId;
                if (ch.FindQuest(compQuestId) is { } comp)
                    ExecQuest(s, ch, comp.Template, monId, x, y, z);
            }
        }

        bool gave = false;
        if (give.Count > 0)
        {
            if (CanPush(ch, give))
            {
                PushTItem(s, give);
                foreach (var it in give) CheckQuest(s, 0, x, y, z, it.TemplateId, QttGetItem, TtGetItem, it.Count);
                SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.Success);
                gave = true;
            }
            else
            {
                DropQuest(ch, q.ParentId);
                SendCS_QUESTCOMPLETE_ACK(s, QuestResult.InventoryFull, q.ParentId, 0, 0, q.ParentId);
            }
        }

        if (compQuestId != 0 || gave) ExecChildren(s, ch, q, monId, x, y, z);
    }

    /// <summary>C++ <c>CQuestDeleteItem::ExecQuest</c> — when runnable, remove every QTT_ITEMID template (all
    /// stacks) from the bags (C++ <c>CTPlayer::DeleteItem(wItemID)</c>, gated on the item existing in the chart).</summary>
    private void ExecDeleteItem(ClientSession s, Character ch, QuestTemplate q)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        foreach (var t in q.Terms)
        {
            if (t.TermType != QttItemId || _templates.Item((ushort)t.TermId) is null) continue;
            int total = 0;
            foreach (var inv in ch.Invens)
                foreach (var it in inv.Items)
                    if (it.TemplateId == (ushort)t.TermId) total += it.Count;
            if (total > 0) UseItemByTemplate(s, ch, (ushort)t.TermId, total);
        }
    }

    /// <summary>C++ <c>CQuestDrop::ExecQuest</c> — abandon each term's quest (<see cref="DropQuest"/>) and report
    /// it dropped (<c>CS_QUESTCOMPLETE_ACK(QR_DROP)</c>). No eligibility gate (matches the C++).</summary>
    private void ExecDropQuest(ClientSession s, Character ch, QuestTemplate q)
    {
        foreach (var t in q.Terms)
        {
            DropQuest(ch, t.TermId);
            SendCS_QUESTCOMPLETE_ACK(s, QuestResult.Drop, t.TermId, 0, 0, t.TermId);
        }
    }

    /// <summary>C++ <c>CQuestChapterMsg::ExecQuest</c> — send the quest's chapter message (<c>CS_CHAPTERMSG_ACK</c>).</summary>
    private void ExecChapterMsg(ClientSession s, Character ch, QuestTemplate q) => SendCS_CHAPTERMSG_ACK(s, q.QuestId);

    /// <summary>C++ <c>CQuestRouting::ExecQuest</c> — when runnable, send the NPC item list (<c>CS_NPCITEMLIST_ACK</c>)
    /// for the trigger NPC, listing every QTT_ITEMID term. (Despite the name it is not cross-map routing.)</summary>
    private void ExecRouting(ClientSession s, Character ch, QuestTemplate q)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        var items = new List<ushort>();
        foreach (var t in q.Terms)
            if (t.TermType == QttItemId) items.Add((ushort)t.TermId);
        SendCS_NPCITEMLIST_ACK(s, (ushort)q.TriggerId, items);
    }

    /// <summary>C++ <c>CQuestTeleport::ExecQuest</c> — when runnable, teleport to the QTT_MAPID/LEFT/HEIGHT/TOP
    /// destination through the ordinary <see cref="Teleport(ClientSession, Character, byte, ushort, float, float, float)"/>
    /// (a short hop is local, anything else goes through the world), then recurse children — the C++ recurses
    /// right after starting the teleport, not after it lands.</summary>
    private void ExecTeleport(ClientSession s, Character ch, QuestTemplate q, uint monId, float x, float y, float z)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        float px = 0, py = 0, pz = 0;
        ushort mapId = 0;
        foreach (var t in q.Terms)
            switch (t.TermType)
            {
                case QttMapId: mapId = (ushort)t.TermId; break;
                case QttLeft: px = t.TermId; break;
                case QttHeight: py = t.TermId; break;
                case QttTop: pz = t.TermId; break;
            }
        Teleport(s, ch, s.Channel, mapId, px, py, pz);
        ExecChildren(s, ch, q, monId, x, y, z);
    }

    /// <summary>C++ <c>CQuestComplete::ExecQuest</c> — turn in the referenced quest (its QTT_COMPQUEST term):
    /// if all its terms are satisfied, grant the reward, bump the complete count, reset counters, and fire the
    /// TT_COMPLETE chain + children; otherwise report the still-unmet term (unless force-run).</summary>
    private void ExecComplete(ClientSession s, Character ch, QuestTemplate q, byte rewardType, uint rewardId)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;

        uint questId = 0;
        foreach (var t in q.Terms)
            if (t.TermType == QttCompQuest) { questId = t.TermId; break; }

        if (!ch.IsRunningQuest(questId)) return;
        var target = ch.FindQuest(questId)!;

        var unmet = CheckComplete(ch, target);
        if (unmet is null)
        {
            var result = OnQuestComplete(s, ch, target.Template, rewardType, rewardId);
            SendCS_QUESTCOMPLETE_ACK(s, result, questId, 0, 0, 0);

            if (result == QuestResult.Success)
            {
                target.CompleteCount++;
                target.BeginTick = 0;
                target.TimerTick = 0;
                if (target.Template.Type != (byte)QuestType.Guild) target.Save = true;
                foreach (var rt in target.RunningTerms) rt.Count = 0;

                CheckQuest(s, 0, ch.PosX, ch.PosY, ch.PosZ, questId, QttQuestCompleted, TtComplete, 1);
                ExecChildren(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ);
            }
        }
        else if (q.ForceRun == 0)
        {
            SendCS_QUESTCOMPLETE_ACK(s, QuestResult.Term, questId, unmet.TermId, unmet.TermType, 0);
        }
    }

    /// <summary>C++ <c>CQuestCraft::ExecQuest</c>: recipes complete directly from inventory, without accepting
    /// a mission first. Failed requirements/capacity checks leave ingredients intact; success fires the
    /// completion trigger and child chain. Craft progress is temporary, so repeatable recipes do not
    /// accumulate a saved mission or overflow byte-sized mission counters.</summary>
    private void ExecCraft(ClientSession s, Character ch, QuestTemplate q, byte rewardType, uint rewardId)
    {
        if (CanRunQuest(ch, q, out _) != QctNone) return;
        var progress = new QuestProgress { Template = q };
        var unmet = CheckComplete(ch, progress);
        if (unmet is not null)
        {
            if (q.ForceRun == 0)
                SendCS_QUESTCOMPLETE_ACK(s, QuestResult.Term, q.QuestId, unmet.TermId, unmet.TermType, 0);
            return;
        }

        var result = OnQuestComplete(s, ch, q, rewardType, rewardId);
        SendCS_QUESTCOMPLETE_ACK(s, result, q.QuestId, 0, 0, 0);
        if (result != QuestResult.Success) return;
        CheckQuest(s, 0, ch.PosX, ch.PosY, ch.PosZ, q.QuestId, QttQuestCompleted, TtComplete, 1);
        ExecChildren(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ);
    }

    /// <summary>C++ <c>CTPlayer::OnQuestComplete</c> — grant the rewards: item rewards (take-method + class
    /// gate → bag push, QR_INVENTORYFULL when they won't fit), then gold / exp, then consume the fetch
    /// (QTT_GETITEM) items. Prebuilt magic items retain their options and lifetime; skill rewards use the
    /// ordinary learn/update packets. Invalid selected rewards fail before any items or progress change.</summary>
    private QuestResult OnQuestComplete(ClientSession s, Character ch, QuestTemplate q, byte reqRewardType, uint reqRewardId)
    {
        var give = new List<Item>();
        int randTake = QuestRng.Next(100);
        int randSum = 0;

        foreach (var rw in q.Rewards)
        {
            if (rw.RewardType != RtItem && rw.RewardType != RtMagicItem) continue;

            bool take = rw.TakeMethod switch
            {
                RmSelect => rw.RewardType == reqRewardType && rw.RewardId == reqRewardId,
                RmProb => QuestRng.Next(100) < rw.TakeData,
                RmRandom => (randSum += rw.TakeData) > randTake,
                RmDefault => true,
                _ => false,
            };
            if (!take) continue;

            Item? prototype;
            if (rw.RewardType == RtMagicItem)
            {
                if (!_templates.QuestItems.TryGetValue(rw.RewardId, out var magic)
                    || ItemFromRow(magic.Item) is not { } built) return QuestResult.Term;
                prototype = built;
                SetEndTime(prototype, magic.UseTime * 86400L);
            }
            else
            {
                ushort itemId = (ushort)rw.RewardId;
                if (_templates.Item(itemId) is not { } tmpl) return QuestResult.Term;
                prototype = new Item { TemplateId = itemId, Template = tmpl };
                LinkItemAttr(prototype);
            }
            if (prototype.Template is { } itemTemplate && (itemTemplate.ClassId & (1u << ch.Class)) != 0)
            {
                int remaining = rw.Count;
                while (remaining > 0)
                {
                    var item = prototype.Clone();
                    item.Count = (byte)Math.Min(remaining, Math.Max(1, (int)itemTemplate.Stack));
                    give.Add(item);
                    remaining -= item.Count;
                }
            }
            if (randSum != 0) break; // RM_RANDOM grants exactly one
        }

        var otherRewards = new List<QuestReward>();
        foreach (var rw in q.Rewards)
        {
            if (rw.RewardType is RtItem or RtMagicItem) continue;
            bool take = rw.TakeMethod switch
            {
                RmSelect => rw.RewardType == reqRewardType && rw.RewardId == reqRewardId,
                RmProb => QuestRng.Next(100) < rw.TakeData,
                RmDefault => true,
                _ => false,
            };
            if (!take) continue;
            if (rw.RewardType is RtSkill or RtSkillUp && _templates.Skill((ushort)rw.RewardId) is null)
                return QuestResult.Term;
            if (rw.RewardType is not (RtGold or RtExp or RtSkill or RtSkillUp)) return QuestResult.Term;
            otherRewards.Add(rw);
        }

        if (!CanPush(ch, give)) return QuestResult.InventoryFull;

        if (give.Count > 0)
        {
            var granted = give.Select(i => (i.TemplateId, i.Count)).ToList();
            PushTItem(s, give);
            foreach (var (id, count) in granted)
                CheckQuest(s, 0, ch.PosX, ch.PosY, ch.PosZ, id, QttGetItem, TtGetItem, count);
        }

        foreach (var rw in otherRewards)
        {
            switch (rw.RewardType)
            {
                case RtGold: ch.EarnMoney(rw.RewardId); SendCS_MONEY_ACK(s, ch); break;
                case RtExp: GainExp(s, ch, rw.RewardId); break;                 // quest-exp buff deferred (0)
                case RtSkill:
                    var template = _templates.Skill((ushort)rw.RewardId)!;
                    if (template.IsClassMatch(ch.Class))
                    {
                        if (ch.Skills.FirstOrDefault(k => k.SkillId == template.Id) is { Level: 0 } placeholder
                            && template.MaxLevel != 0)
                        {
                            placeholder.Level = 1;
                            placeholder.Template = template;
                            SendCS_SKILLBUY_ACK(s, ch, SkillUseResult.Success, template.Id, 1);
                        }
                        else UpdateSkill(s, ch, template, template.Id, 1);
                    }
                    break;
                case RtSkillUp:
                    if (LearnedSkill(ch, (ushort)rw.RewardId) is { Level: < byte.MaxValue } skill)
                    {
                        skill.Level++;
                        SendCS_SKILLBUY_ACK(s, ch, SkillUseResult.Success, skill.SkillId, skill.Level);
                    }
                    break;
            }
        }

        ConsumeFetchItems(s, ch, q);
        GetTitle(s, ch, QuestTitle, q.QuestId, start: true);
        return QuestResult.Success;
    }

    /// <summary>The C++ turn-in item sink: remove each QTT_GETITEM term's required count from the bags
    /// (DELITEM on empty, UPDATEITEM on partial). The equipped-container appearance rebroadcast is deferred
    /// (fetch items live in bags).</summary>
    private void ConsumeFetchItems(ClientSession s, Character ch, QuestTemplate q)
    {
        foreach (var t in q.Terms)
        {
            if (t.TermType != QttGetItem) continue;
            int remain = t.Count;
            foreach (var inv in ch.Invens)
            {
                foreach (var it in inv.Items.Where(i => i.TemplateId == t.TermId).OrderBy(i => i.ItemSlot).ToList())
                {
                    if (remain <= 0) break;
                    if (it.Count > remain) { it.Count -= (byte)remain; remain = 0; SendCS_UPDATEITEM_ACK(s, inv.InvenId, it); }
                    else { remain -= it.Count; inv.Items.Remove(it); SendCS_DELITEM_ACK(s, inv.InvenId, it); }
                }
                if (remain <= 0) break;
            }
        }
    }

    // ---- client handlers ----

    private void OnCS_QUESTEXEC_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;
        uint questId = r.ReadUInt32();
        byte rewardType = r.ReadByte();
        uint rewardId = r.ReadUInt32();

        if (_templates.Quest(questId) is not { } q) return;

        if (q.TriggerType == TtTalkNpc)
        {
            if (_state.FindNpc((ushort)q.TriggerId) is not { } npc) return;
            if (ch.MapId != npc.MapId) return;
            if (!CanTalk(npc, ch)) return;
        }

        ExecQuest(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ, rewardType, rewardId);
    }

    private void OnCS_QUESTDROP_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;
        uint questId = r.ReadUInt32();
        DropQuest(ch, questId);
        SendCS_QUESTCOMPLETE_ACK(s, QuestResult.Drop, questId, 0, 0, questId);
    }

    private void OnCS_QUESTLIST_POSSIBLE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;
        byte count = r.ReadByte();

        var perNpc = new List<(ushort npcId, byte country, List<(uint questId, byte con)> quests)>();
        var arenasShown = new HashSet<ushort>();
        for (int i = 0; i < count; i++)
        {
            ushort npcId = r.ReadUInt16();
            if (_state.FindNpc(npcId) is not { } npc) continue;
            if (npc.Country != 3 && npc.Country != 2 && npc.Country != ch.Country) continue; // TCONTRY_N/_B or own
            SendArenaRosters(s, npc, arenasShown);                                          // an arena NPC's teams (MapService.Arena.cs)

            var runnable = new List<(uint, byte, byte)>();  // (questId, canRun, level)
            byte minLevel = 0xFF;
            foreach (var q in TriggerQuests(TtTalkNpc, npcId))
            {
                byte canRun = CanRunQuest(ch, q, out byte lvl);
                if (canRun == QctNone || canRun == QctUpperLevel)
                {
                    if (canRun == QctUpperLevel && minLevel > lvl) minLevel = lvl;
                    runnable.Add((q.QuestId, canRun, lvl));
                }
            }
            // keep the QCT_NONE quests + only the lowest-required-level UpperLevel quests
            var quests = runnable
                .Where(t => t.Item2 != QctUpperLevel || t.Item3 == minLevel)
                .Select(t => (t.Item1, t.Item2))
                .ToList();
            perNpc.Add((npcId, npc.Country, quests));
        }

        SendCS_QUESTLIST_POSSIBLE_ACK(s, perNpc);
    }

    // ---- senders ----

    /// <summary>C++ <c>SendCS_QUESTADD_ACK</c> — a quest began: dwQuestID, bType.</summary>
    private static void SendCS_QUESTADD_ACK(ClientSession s, uint questId, byte type)
    {
        var w = new PacketWriter(Msg.CS_QUESTADD_ACK, capacity: 8);
        w.WriteUInt32(questId);
        w.WriteByte(type);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_QUESTUPDATE_ACK</c> — dwQuestID, dwTermID, bType, bCount, bStatus.</summary>
    private static void SendCS_QUESTUPDATE_ACK(ClientSession s, uint questId, uint termId, byte termType, byte count, byte status)
    {
        var w = new PacketWriter(Msg.CS_QUESTUPDATE_ACK, capacity: 16);
        w.WriteUInt32(questId);
        w.WriteUInt32(termId);
        w.WriteByte(termType);
        w.WriteByte(count);
        w.WriteByte(status);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_QUESTCOMPLETE_ACK</c> — bResult, dwQuestID, dwTermID, bType, dwDropID.</summary>
    private static void SendCS_QUESTCOMPLETE_ACK(ClientSession s, QuestResult result, uint questId, uint termId, byte termType, uint dropId)
    {
        var w = new PacketWriter(Msg.CS_QUESTCOMPLETE_ACK, capacity: 20);
        w.WriteByte((byte)result);
        w.WriteUInt32(questId);
        w.WriteUInt32(termId);
        w.WriteByte(termType);
        w.WriteUInt32(dropId);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_QUESTSTARTTIMER_ACK</c> — dwQuestID, dwTick.</summary>
    private static void SendCS_QUESTSTARTTIMER_ACK(ClientSession s, uint questId, uint tick)
    {
        var w = new PacketWriter(Msg.CS_QUESTSTARTTIMER_ACK, capacity: 8);
        w.WriteUInt32(questId);
        w.WriteUInt32(tick);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_QUESTLIST_POSSIBLE_ACK</c> — bCount, then per NPC { wNpcID, bCountry, bCount,
    /// { dwQuestID, bQuestCON } }.</summary>
    private static void SendCS_QUESTLIST_POSSIBLE_ACK(ClientSession s,
        List<(ushort npcId, byte country, List<(uint questId, byte con)> quests)> perNpc)
    {
        var w = new PacketWriter(Msg.CS_QUESTLIST_POSSIBLE_ACK, capacity: 64);
        w.WriteByte((byte)perNpc.Count);
        foreach (var (npcId, country, quests) in perNpc)
        {
            w.WriteUInt16(npcId);
            w.WriteByte(country);
            w.WriteByte((byte)quests.Count);
            foreach (var (questId, con) in quests)
            {
                w.WriteUInt32(questId);
                w.WriteByte(con);
            }
        }
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_CHAPTERMSG_ACK</c> (CSSender.cpp:3834) — just <c>dwQuestID</c>.</summary>
    private static void SendCS_CHAPTERMSG_ACK(ClientSession s, uint questId)
    {
        var w = new PacketWriter(Msg.CS_CHAPTERMSG_ACK, capacity: 8);
        w.WriteUInt32(questId);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_NPCITEMLIST_ACK(wID, items)</c> (CSSender.cpp:3218) — wNpcID, <c>TNPC_BOX</c>, the
    /// NPC's discount rate for the player, the count, then each WORD item id.</summary>
    private void SendCS_NPCITEMLIST_ACK(ClientSession s, ushort npcId, List<ushort> items)
    {
        var w = new PacketWriter(Msg.CS_NPCITEMLIST_ACK, capacity: 32);
        w.WriteUInt16(npcId);
        w.WriteByte(TNpcBox);
        w.WriteByte(s.Char is { } ch ? DiscountRate(ch, _state.FindNpc(npcId)) : (byte)0);
        w.WriteByte((byte)items.Count);
        foreach (var id in items) w.WriteUInt16(id);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_QUESTLIST_ACK</c> (CSSender.cpp:1861), sent in the enter <c>MW_CHARINFO</c> step:
    /// the character's in-progress quests (<c>CompleteCount &lt; TriggerCount</c>) so the client rebuilds its
    /// quest log on login. Per quest: dwQuestID, bType, bCountMax, bTermCount, then per template term
    /// { dwTermID, bTermType, bNeedCount, bCurrentCount, bStatus }. Byte layout follows the C++ <b>code</b>
    /// (which sends <c>bCountMax</c> — the header comment omits it). A non-creating running-term lookup is used
    /// (the read must not spawn empty counters).</summary>
    private void SendCS_QUESTLIST_ACK(ClientSession s, Character ch)
    {
        // Ordered by quest id to match the C++ std::map<DWORD> iteration (m_mapQUEST), as the CHARINFO
        // inventory/skill loops already do.
        var running = ch.Quests.Values.Where(qp => qp.IsRunning).OrderBy(qp => qp.Template.QuestId).ToList();
        var w = new PacketWriter(Msg.CS_QUESTLIST_ACK, capacity: 128);
        w.WriteByte((byte)running.Count);
        foreach (var qp in running)
        {
            w.WriteUInt32(qp.Template.QuestId);
            w.WriteByte(qp.Template.Type);
            w.WriteByte(qp.Template.CountMax);
            w.WriteByte((byte)qp.Template.Terms.Count);
            foreach (var t in qp.Template.Terms)
            {
                byte cur = qp.RunningTerms.FirstOrDefault(x => x.TermId == t.TermId && x.TermType == t.TermType)?.Count
                    ?? GetTermCount(ch, t.TermId, t.TermType);
                w.WriteUInt32(t.TermId);
                w.WriteByte(t.TermType);
                w.WriteByte(t.Count);
                w.WriteByte(cur);
                w.WriteByte((byte)CheckTermStatus(ch, qp, t));
            }
        }
        s.Send(w);
    }

    /// <summary>C++ <c>CTPlayer::SendQuestTimer</c> (TPlayer.cpp:3951), sent on enter right after
    /// <c>CS_QUESTLIST_ACK</c>: for every quest with an active timer (<c>m_dwTick != 0</c>) emit
    /// <c>CS_QUESTSTARTTIMER_ACK(dwQuestID, remaining)</c> so the client restores the countdown UI on relog.</summary>
    private void SendQuestTimers(ClientSession s, Character ch)
    {
        foreach (var qp in ch.Quests.Values.Where(q => q.TimerTick != 0).OrderBy(q => q.Template.QuestId))
            SendCS_QUESTSTARTTIMER_ACK(s, qp.Template.QuestId, qp.TimerTick);
    }
}
