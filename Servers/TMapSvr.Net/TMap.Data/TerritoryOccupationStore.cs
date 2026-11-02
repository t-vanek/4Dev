namespace TMap.Data;

public interface ITerritoryOccupationStore
{
    Task<int> SaveMissionOccupyAsync(ushort localId, byte type, uint charId, byte country, CancellationToken ct = default);
    Task<int> SaveSkyGardenOccupyAsync(byte country, ushort id, byte type, CancellationToken ct = default);
    Task<(int Ret, byte Country, uint Guild)> SaveLocalOccupyAsync(ushort localId, byte type, uint guildId, uint charId, CancellationToken ct = default);
    Task<(int Ret, byte Country)> SaveCastleOccupyAsync(ushort castle, byte type, uint guildId, CancellationToken ct = default);
}

public sealed partial class GameDatabase : ITerritoryOccupationStore { }
