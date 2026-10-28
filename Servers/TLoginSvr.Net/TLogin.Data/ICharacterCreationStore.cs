namespace TLogin.Data;

/// <summary>The character-creation database operation selected after validating the world group.</summary>
public interface ICharacterCreationStore
{
    Task<CreateCharRow> CreateCharAsync(CreateCharArgs args, CancellationToken ct = default);
}
