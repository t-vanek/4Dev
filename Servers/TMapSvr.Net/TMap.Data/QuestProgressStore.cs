namespace TMap.Data;

/// <summary>Required saved quest state for character admission. A failed read is not an empty quest log.</summary>
public interface IQuestProgressStore
{
    Task<(List<QuestLoadRow> quests, List<QuestTermLoadRow> terms)> LoadQuestsAsync(
        uint charId, CancellationToken ct = default);
}

public sealed partial class GameDatabase : IQuestProgressStore { }
