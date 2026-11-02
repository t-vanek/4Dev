using Microsoft.Extensions.Logging;
using TMap.Data;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    public IQuestPostStore? QuestPostStore { get; set; }
    private sealed record QuestPostJob(ClientSession Session, Character Character, QuestTemplate Quest,
        uint Target, float X, float Y, float Z, List<ItemSaveData> Items);
    private readonly Queue<QuestPostJob> _questPosts = new();
    private readonly HashSet<(Character, uint)> _queuedQuestPosts = new();

    private void ExecSendPost(ClientSession s, Character ch, QuestTemplate q, uint target, float x, float y, float z)
    {
        if (!s.IsMain || ch.TournamentBackup is not null || QuestPostStore is null || CanRunQuest(ch, q, out _) != QctNone
            || _queuedQuestPosts.Contains((ch, q.QuestId))) return;
        if (_capturingQuestPost is { } active && (active.Ancestors.Contains(q.QuestId) || active.QuestId == q.QuestId)) return;
        // SendPost has no selected reward (native QuestSendPost.cpp). It never consumes inventory or gold.
        if (BuildQuestRewardItems(ch, q, 0, 0, out var give) != TMap.Protocol.QuestResult.Success || give.Count == 0) return;
        var rows = give.Select(it => BuildItemSave(0, it) with { Ext = (uint[])it.Ext.Clone() }).ToList();
        _queuedQuestPosts.Add((ch, q.QuestId));
        _questPosts.Enqueue(new(s, ch, q, target, x, y, z, rows));
    }

    // Await on the batch task at the end of each event, including events originating from World and timers.
    // Children run only after every attachment commits, so a failed DB write cannot advance the next mission.
    private async Task FlushQuestPostsAsync()
    {
        try
        {
            while (_questPosts.TryDequeue(out var job))
            {
                try
                {
                    if (_gameDb is not null && job.Character.DbLoaded)
                    {
                        await CommitQuestPostJobsAsync(job);
                        await RecoverQuestPostContinuationsAsync(job.Session);
                        continue;
                    }
                    var result = await QuestPostStore!.ReserveQuestPostAsync(job.Quest.QuestId, job.Character.CharId, job.Items, job.Quest.CountMax);
                    if (result.Result is not (0 or QuestPostResult.AlreadyIssued))
                    {
                        _log.LogWarning("Quest mail {Quest} for {Character} rejected: {Result}.", job.Quest.QuestId, job.Character.CharId, result.Result);
                        continue;
                    }
                    // This state is already committed with the attachments. It must not be re-saved later
                    // by an older asynchronous character snapshot, or written back on an ambiguous failure.
                    job.Character.Quests[job.Quest.QuestId] = new QuestProgress
                    {
                        Template = job.Quest, TriggerCount = result.Count, CompleteCount = result.Count
                    };
                    if (result.Result == QuestPostResult.AlreadyIssued) continue;
                    if (job.Session.IsMain && ReferenceEquals(job.Session.Char, job.Character))
                        ExecChildren(job.Session, job.Character, job.Quest, job.Target, job.X, job.Y, job.Z);
                }
                catch (Exception ex)
                {
                    if (_gameDb is not null)
                    {
                        QuarantinePersistence(job.Session);
                        throw;
                    }
                    // Do not blindly replay after an ambiguous connection/commit failure (would duplicate rewards).
                    _log.LogError(ex, "Quest mail {Quest} for {Character} failed.", job.Quest.QuestId, job.Character.CharId);
                }
            }
        }
        finally { _queuedQuestPosts.Clear(); }
    }
}
