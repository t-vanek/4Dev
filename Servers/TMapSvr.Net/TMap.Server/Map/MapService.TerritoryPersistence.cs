using Microsoft.Extensions.Logging;
using TMap.Data;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    public ITerritoryOccupationStore? TerritoryOccupationStore { get; set; }
    private readonly Queue<(Territory Territory, Func<Task> Write)> _territoryWrites = new();
    private readonly HashSet<Territory> _pendingTerritoryWrites = new();

    private void QueueTerritoryWrite(Territory territory, Func<Task> write)
    {
        if (_pendingTerritoryWrites.Add(territory)) _territoryWrites.Enqueue((territory, write));
    }

    // Preserve the single DB lane and apply native acknowledgements on the actor, before the next
    // event can change the war or its participant set. Do not publish ownership/rewards on failure.
    private async Task FlushTerritoryWritesAsync()
    {
        while (_territoryWrites.TryDequeue(out var job))
        {
            try { await job.Write(); }
            catch (Exception ex) { _log.LogError(ex, "Territory result for {Territory} was not confirmed; no success acknowledgement was applied.", job.Territory.Id); }
            finally { _pendingTerritoryWrites.Remove(job.Territory); }
        }
    }

    private async Task<T> SaveTerritoryResultAsync<T>(Func<Task<T>> write)
    {
        T? result = default;
        bool completed = false;
        Exception? error = null;
        await EnqueueDbWrite(async () =>
        {
            try { result = await write(); completed = true; }
            catch (Exception ex) { error = ex; }
        });
        if (error is not null) throw new IOException("Native territory save failed.", error);
        if (!completed) throw new IOException("Earlier SQL recovery blocked the territory save.");
        return result!;
    }
}
