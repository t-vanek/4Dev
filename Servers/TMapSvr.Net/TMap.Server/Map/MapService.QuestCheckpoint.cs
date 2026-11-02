using Microsoft.Extensions.Logging;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    private readonly Dictionary<Character, ClientSession> _questCheckpoints = new();

    private void ScheduleQuestCheckpoint(Character ch)
    {
        if (_gameDb is null || !ch.DbLoaded || ch.TournamentBackup is not null) return;
        if (_state.FindByChar(ch.CharId) is { IsMain: true } s && ReferenceEquals(s.Char, ch))
            _questCheckpoints[ch] = s;
    }

    // Capture after the whole logical event and mail/child execution, not midway through rewards.
    private void FlushQuestCheckpoints(Character? except = null)
    {
        foreach (var (ch, s) in _questCheckpoints.ToArray())
        {
            if (ReferenceEquals(ch, except)) continue;
            if (!s.IsMain || !ReferenceEquals(s.Char, ch) || ch.TournamentBackup is not null)
            {
                _questCheckpoints.Remove(ch);
                continue;
            }
            try { _ = QueueCharacterSave(s); }
            catch (Exception ex)
            {
                // Retain dirty state/checkpoint. A character cannot keep playing without durable storage.
                _log.LogError(ex, "Quest checkpoint for {Character} could not be committed to the SQL queue.", ch.CharId);
                QuarantinePersistence(s); // never leak an unconfirmed quest result through logout or item saves
                throw;
            }
        }
    }
}
