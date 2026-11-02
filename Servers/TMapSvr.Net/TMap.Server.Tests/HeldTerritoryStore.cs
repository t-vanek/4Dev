using TMap.Data;

namespace TMap.Server.Tests;

internal sealed class HeldTerritoryStore(int result) : ITerritoryOccupationStore
{
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public byte Country = 1;
    public uint Guild = 17;
    public int Calls;
    public ushort Territory;
    public byte Type;
    private async Task<int> Save(ushort id, byte type)
    {
        Calls++; Territory = id; Type = type; Entered.TrySetResult();
        await Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (result < 0) throw new IOException("Injected SQL failure");
        return result;
    }
    public Task<int> SaveMissionOccupyAsync(ushort id, byte type, uint ch, byte country, CancellationToken ct = default) => Save(id, type);
    public Task<int> SaveSkyGardenOccupyAsync(byte country, ushort id, byte type, CancellationToken ct = default) => Save(id, type);
    public async Task<(int Ret, byte Country, uint Guild)> SaveLocalOccupyAsync(ushort id, byte type, uint guild, uint ch, CancellationToken ct = default)
        => (await Save(id, type), Country, Guild);
    public async Task<(int Ret, byte Country)> SaveCastleOccupyAsync(ushort id, byte type, uint guild, CancellationToken ct = default)
        => (await Save(id, type), Country);
}
