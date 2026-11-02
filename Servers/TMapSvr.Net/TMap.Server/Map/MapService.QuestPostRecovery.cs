using System.Security.Cryptography;
using System.Text.Json;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    private readonly Dictionary<Character, List<QuestPostIntent>> _questPostRequests = new();
    private readonly Dictionary<Character, List<Guid>> _questPostReceipts = new();
    private QuestPostIntent? _capturingQuestPost;
    private Character? _unpublishedQuestPost;
    private string QuestPostChartHash() => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(_templates.Quests.OrderBy(p => p.Key).Select(p => p.Value))));

    private QuestPostIntent PostIntent(QuestPostJob job)
    {
        var ancestors = _capturingQuestPost is { } parent ? parent.Ancestors.Append(parent.QuestId).ToList() : new List<uint>();
        var intent = new QuestPostIntent(Guid.NewGuid(), job.Quest.QuestId, job.Character.CharId, job.Quest.CountMax,
            job.Target, job.Character.MapId, job.Session.Channel, job.X, job.Y, job.Z, QuestPostChartHash(), ancestors, job.Items);
        intent.Validate();
        return intent;
    }

    private void StagePostJob(QuestPostJob job)
    {
        if (!_questPostRequests.TryGetValue(job.Character, out var requests))
            _questPostRequests[job.Character] = requests = new();
        requests.Add(PostIntent(job));
    }

    private async Task CommitQuestPostJobsAsync(QuestPostJob job)
    {
        if (QuestPostStore is not IQuestPostOperationStore) throw new InvalidOperationException("Migration 042/durable quest post store required.");
        _unpublishedQuestPost = job.Character;
        StagePostJob(job);
        // Sibling mail branches already queued by this same event belong to the same character
        // checkpoint. Capturing only the first would strand siblings if it committed before a crash.
        var others = new List<QuestPostJob>();
        while (_questPosts.TryDequeue(out var next))
        {
            if (ReferenceEquals(next.Character, job.Character)) StagePostJob(next);
            else others.Add(next);
        }
        foreach (var other in others) _questPosts.Enqueue(other);
        // Freeze the issuance identity, rolled rewards and triggering character state BEFORE any SQL mail write.
        await QueueCharacterSave(job.Session);
        await RecoverPendingSavesAsync();
        var saved = await _gameDb!.LoadQuestsAsync(job.Character.CharId);
        foreach (var row in saved.quests)
            if (_templates.Quest(row.QuestId) is { Type: (byte)QuestType.SendPost } template)
                job.Character.Quests[row.QuestId] = new() { Template = template, TriggerCount = row.TriggerCount, CompleteCount = row.CompleteCount };
    }

    private void QuarantinePersistence(ClientSession s)
    {
        if (s.Char is { } ch)
        {
            foreach (var id in _pendingItemUpserts.Where(p => p.Value.charId == ch.CharId).Select(p => p.Key).ToArray()) _pendingItemUpserts.Remove(id);
            foreach (var id in _pendingItemDeletes.Where(p => p.Value == ch.CharId).Select(p => p.Key).ToArray()) _pendingItemDeletes.Remove(id);
            if (ReferenceEquals(_unpublishedQuestPost, ch)) _unpublishedQuestPost = null;
            // Unpublished child changes must never leak into the disconnect/periodic/item save paths.
            ch.DbLoaded = false;
            _questCheckpoints.Remove(ch); _questPostRequests.Remove(ch); _questPostReceipts.Remove(ch);
        }
        var otherJobs = _questPosts.Where(j => !ReferenceEquals(j.Character, s.Char)).ToArray();
        _questPosts.Clear();
        foreach (var job in otherJobs) _questPosts.Enqueue(job);
        s.QuestPostRecoveryFailed = true;
        s.EntryValidated = false; s.State = EnterState.Entering; s.Conn.Close();
    }

    /// <summary>After the ordinary save-recovery/load barrier, before admitting the main to gameplay.
    /// Durable character effects and nested mail intents are published with the receipt in one SQL queue record.
    /// A crash before publication reloads the pre-event state; after publication the SQL queue finishes and acks.
    /// This API also lets the isolated process fixture exercise the production recovery path.</summary>
    public async Task RecoverQuestPostContinuationsAsync(ClientSession s)
    {
        if (_gameDb is null || !s.IsMain || s.Char is not { DbLoaded: true, TournamentBackup: null } ch) return;
        if (QuestPostStore is not IQuestPostOperationStore posts) throw new InvalidOperationException("Durable quest post store required.");
        using var responses = s.DeferResponses();
        var waitingPosts = _questPosts.ToArray();
        _questPosts.Clear();
        try
        {
            if (!_itemIdReady) throw new InvalidOperationException("Quest post recovery requires the item-ID seed.");
            await RecoverPendingSavesAsync();
            int processed = 0;
            while (true)
            {
                var pending = await posts.LoadQuestPostOperationsAsync(ch.CharId);
                if (pending.Count == 0) return;
                foreach (var operation in pending)
                {
                    var intent = operation.Intent;
                    if (++processed > 1024) throw new InvalidDataException("Quest post continuation limit exceeded.");
                    if (intent.ChartHash != QuestPostChartHash() || _templates.Quest(intent.QuestId) is not { Type: (byte)QuestType.SendPost } parent)
                        throw new InvalidDataException("Pending quest post requires its original quest chart.");
                    FlushItemDirect();
                    await RecoverPendingSavesAsync();
                    ch.Quests[parent.QuestId] = new() { Template = parent, TriggerCount = operation.Count, CompleteCount = operation.Count };
                    _capturingQuestPost = intent;
                    _unpublishedQuestPost = ch;
                    try
                    {
                        ExecChildren(s, ch, parent, intent.Target, intent.X, intent.Y, intent.Z);
                        // Children may enqueue more mail. Persist their stable identities before acknowledging
                        // this parent; replay reserves nested mail idempotently before the parent receipt.
                        while (_questPosts.TryDequeue(out var child))
                        {
                            if (!ReferenceEquals(child.Character, ch)) throw new InvalidOperationException("Cross-character quest post continuation.");
                            StagePostJob(child);
                        }
                    }
                    finally { _capturingQuestPost = null; }
                    _questPostReceipts[ch] = [intent.OperationId];
                    await QueueCharacterSave(s);
                    await RecoverPendingSavesAsync();
                }
            }
        }
        catch
        {
            responses.Discard();
            QuarantinePersistence(s);
            throw;
        }
        finally { foreach (var job in waitingPosts) _questPosts.Enqueue(job); }
    }
}
