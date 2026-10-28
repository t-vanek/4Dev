using TMap.Data;

namespace TMap.Server.Map;

/// <summary>Transient, connection-local authorization for the landed half of a paid player cast.</summary>
internal sealed class AuthorizedPlayerCast
{
    public required Skill Skill;
    public byte Level;
    public bool RequireLearned = true;
    public uint Started, Lifetime;
    public ushort Map;
    public byte Channel;
    public required (uint Id, byte Type)[] Targets;
    public Dictionary<ushort, AuthorizedHitStage> Stages { get; } = new();
}

internal sealed class AuthorizedHitStage
{
    public required SkillTemplate Template;
    public byte Level;
    public required Dictionary<(uint Id, byte Type), int> Targets;
    public int Remaining, PerTarget, EmptyFinishes;
    public bool Area;
}

public sealed partial class MapService
{
    private void AuthorizePlayerCast(ClientSession s, Skill skill, (uint Id, byte Type)[] targets, bool requireLearned = true,
        ushort backSkill = 0)
    {
        if (skill.Template is not { } tpl || s.Char is not { } ch) return;
        s.PendingCasts.RemoveAll(c => unchecked(NowMs - c.Started) >= c.Lifetime
            || c.Map != ch.MapId || c.Channel != s.Channel);
        if (s.PendingCasts.Count >= 256) s.PendingCasts.RemoveAt(0);
        var cast = new AuthorizedPlayerCast
        {
            Skill = skill, Level = skill.Level, RequireLearned = requireLearned, Started = NowMs,
            Map = ch.MapId, Channel = s.Channel, Targets = targets,
            Lifetime = (uint)Math.Clamp((long)tpl.ActionTime + tpl.Duration
                + (long)tpl.DurationInc * Math.Max(0, skill.Level - 1) + 10000, 30000, 600000),
        };
        cast.Stages[tpl.Id] = NewHitStage(tpl, skill.Level, targets);
        // The real client launches the server-announced item skill at level 1. Authorize exactly
        // that rolled skill, with the same bounded target budget, even when it is not learned.
        if (backSkill != 0 && backSkill != tpl.Id && _templates.Skill(backSkill) is { } proc)
            cast.Stages[backSkill] = NewHitStage(proc, 1, targets);
        s.PendingCasts.Add(cast);
    }

    private static AuthorizedHitStage NewHitStage(SkillTemplate tpl, byte level, (uint Id, byte Type)[] targets)
    {
        int repeats = tpl.RepeatCount + 1;
        var budget = targets.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count() * repeats);
        return new AuthorizedHitStage
        {
            Template = tpl, Level = level, Targets = budget, Area = tpl.TargetRange != 0,
            Remaining = tpl.TargetRange != 0 ? MaxTarget * repeats : budget.Values.Sum(),
            PerTarget = repeats, EmptyFinishes = repeats,
        };
    }

    /// <summary>Both FINISHSKILL and legacy DEFEND spend the same target budget. Failed/replayed packets
    /// cannot manufacture a cast; repeats, missiles and linked effects are bounded by the server charts.</summary>
    private Skill? ConsumePlayerCast(ClientSession s, ushort skillId, (uint Id, byte Type)[] targets)
    {
        if (!s.IsMain || s.Char is not { } ch || targets.Length > MaxTarget) return null;
        s.PendingCasts.RemoveAll(c => unchecked(NowMs - c.Started) >= c.Lifetime
            || c.Map != ch.MapId || c.Channel != s.Channel);
        foreach (var cast in s.PendingCasts)
        {
            if (cast.RequireLearned && (!IsLearned(cast.Skill) || !ch.Skills.Contains(cast.Skill))) continue;
            if (!cast.Stages.TryGetValue(skillId, out var stage))
            {
                if (_templates.Skill(skillId) is not { } linked) continue;
                // Linked projectiles may arrive before the parent impact. The paid root cast, rather
                // than impact order, authorizes only the chart's declared descendants.
                var parent = cast.Stages.Values.FirstOrDefault(parent =>
                    (linked.TriggerId != 0 && linked.TriggerId == parent.Template.Id
                        || linked.Id == DeadlyPoisonDot && parent.Template.Id == DeadlyPoison
                        || parent.Template.Data.Any(d => d.Type == SkillTemplate.SdtStatus && d.Exec == 29
                            && d.Inc == 0 && parent.Template.GetValue(d, parent.Level) == skillId)));
                if (parent is null || cast.Stages.Count >= 32) continue;
                stage = NewHitStage(linked, parent.Level, cast.Targets);
                cast.Stages.Add(skillId, stage);
            }

            var requested = targets.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            if (targets.Length == 0)
            {
                if (stage.EmptyFinishes == 0) continue;
                stage.EmptyFinishes--;
            }
            else
            {
                if (stage.Remaining < targets.Length || requested.Any(pair => pair.Value >
                    (stage.Targets.TryGetValue(pair.Key, out int left) ? left : stage.Area ? stage.PerTarget : 0))) continue;
                foreach (var (target, count) in requested)
                    stage.Targets[target] = (stage.Targets.TryGetValue(target, out int left) ? left : stage.PerTarget) - count;
                stage.Remaining -= targets.Length;
            }
            return new Skill { SkillId = skillId, Level = stage.Level, Template = stage.Template };
        }
        return null;
    }
}
