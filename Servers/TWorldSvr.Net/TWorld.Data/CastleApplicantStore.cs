namespace TWorld.Data;

public interface ICastleApplicantStore
{
    Task SaveCastleApplicantAsync(ushort castle, uint charId, byte camp, CancellationToken ct = default);
}

public sealed partial class GameDatabase : ICastleApplicantStore { }
