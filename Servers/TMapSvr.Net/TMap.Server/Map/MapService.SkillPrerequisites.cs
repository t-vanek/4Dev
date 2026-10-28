namespace TMap.Server.Map;

public sealed partial class MapService
{
    // CheckPrevAct checks maintained effects, not the learned-skill list. The map's maintain timer
    // removes expired effects; apply this gate again when the cast or impact actually arrives.
    private bool HasActiveSkill(IEnumerable<MaintainSkill> effects, ushort required)
        => required == 0 || effects.Any(m => m.SkillId == required && !m.IsEnd(NowMs));

    private bool TargetHasActiveSkill(byte type, uint id, ushort required)
    {
        if (required == 0) return true;
        IEnumerable<MaintainSkill>? effects = type switch
        {
            OtPc => _state.FindByChar(id)?.Char?.MaintainSkills,
            Monster.OtMon => _state.FindMonster(id)?.MaintainSkills,
            RecallMon.OtRecall or RecallMon.OtSelf or RecallMon.OtCompanion => _state.FindRecall(type, id)?.MaintainSkills,
            _ => null,
        };
        return effects is not null && HasActiveSkill(effects, required);
    }
}
