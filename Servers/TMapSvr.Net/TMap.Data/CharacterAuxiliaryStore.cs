namespace TMap.Data;

/// <summary>Saved character state that must be available before admission and later full saves.</summary>
public interface ICharacterAuxiliaryStore
{
    Task<List<HotkeyLoadRow>> LoadHotkeysAsync(uint charId, CancellationToken ct = default);
    Task<(List<RecallSaveRow> Recalls, List<RecallMaintainRow> Buffs)> LoadRecallsAsync(uint charId, CancellationToken ct = default);
    Task<(byte OriCountry, byte AidCountry, long AidDate)> LoadAidAsync(uint charId, CancellationToken ct = default);
    Task<DuringLoad> LoadDuringItemsAsync(uint charId, uint userId, int playDate, CancellationToken ct = default);
}

public sealed partial class GameDatabase : ICharacterAuxiliaryStore { }
