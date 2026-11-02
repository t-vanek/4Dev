using TMap.Data;

namespace TMap.Server.Map;

/// <summary>A per-character running objective counter (C++ <c>CQuest::m_vTERM</c> entry): the mutable
/// progress toward one term. Created on demand by the term engine for the counting term types
/// (HUNT / TALK / USEITEM); the count-from-state terms (GETITEM, QUESTCOMPLETED) are computed live instead.</summary>
public sealed class RunningTerm
{
    public uint TermId;
    public byte TermType;
    public byte Count;
}

/// <summary>
/// A character's progress on one quest — the C# counterpart of the live <c>CQuest</c> instance stored in
/// <c>CTPlayer::m_mapQUEST</c>. It links the immutable <see cref="Template"/> to the mutable running state:
/// the term counters, the trigger/complete tallies (a quest is "running" while
/// <see cref="CompleteCount"/> &lt; <see cref="TriggerCount"/>), the timer anchors, and the reward choice the
/// client sent on accept. <see cref="Save"/> mirrors the C++ <c>m_bSave</c> dirty flag for persistence.
/// </summary>
public sealed class QuestProgress
{
    public required QuestTemplate Template { get; init; }
    public List<RunningTerm> RunningTerms { get; } = new();   // m_vTERM
    public byte TriggerCount;
    public byte CompleteCount;
    public uint BeginTick;
    public uint TimerTick;
    public bool TimerActive; // tick zero is a valid start and zero remaining means expired, not untimed
    public bool TimerExpired;
    public bool HasTimer => TimerActive || BeginTick != 0;
    public uint RemainingTimer(uint nowMs) => !HasTimer || TimerExpired ? 0
        : TimerTick - Math.Min(TimerTick, unchecked(nowMs - BeginTick));
    public bool Save;
    public byte RewardType;      // from CS_QUESTEXEC (RM_SELECT reward pick)
    public uint RewardId;

    /// <summary>C++ <c>IsRunningQuest</c>: accepted but not yet turned in this cycle.</summary>
    public bool IsRunning => CompleteCount < TriggerCount;
}
