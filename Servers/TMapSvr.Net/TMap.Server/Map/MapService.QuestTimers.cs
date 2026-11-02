using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    private void RunQuestTimers()
    {
        foreach (var s in _state.AllInGame().Where(s => s.IsMain))
            foreach (var quest in s.Char!.Quests.Values)
                ExpireQuestTimer(s, quest);
    }

    private void ExpireQuestTimer(ClientSession s, QuestProgress quest)
    {
        if (!quest.IsRunning || !quest.HasTimer || quest.TimerExpired
            || unchecked(NowMs - quest.BeginTick) <= quest.TimerTick) return;
        var terms = quest.Template.Terms.Where(t => t.TermType == QttTimer).ToList();
        if (terms.Count == 0) return;
        // The native client notification cleared BeginTick, which accidentally disabled CheckComplete.
        // Retain a failed state and persist zero remaining; neither silence nor replay may extend the quest.
        quest.TimerExpired = true;
        quest.TimerActive = true;
        quest.BeginTick = 0;
        quest.TimerTick = 0;
        quest.Save = true;
        if (s.Char is { } ch) ScheduleQuestCheckpoint(ch);
        foreach (var term in terms)
            SendCS_QUESTUPDATE_ACK(s, quest.Template.QuestId, term.TermId, term.TermType, 0, (byte)QuestTermStatus.Failed);
    }
}
