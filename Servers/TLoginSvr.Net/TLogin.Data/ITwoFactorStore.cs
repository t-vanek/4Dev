namespace TLogin.Data;

public sealed record TwoFactorLimits(int CodeLifetimeSeconds = 300, int ResendDelaySeconds = 60,
    int MaxAttempts = 5, int LockoutSeconds = 900, int MaxSendsPerHour = 10, int GrantLifetimeSeconds = 60);

public interface ITwoFactorStore
{
    Task<bool> IsRequiredAsync(uint userId, CancellationToken ct = default);
    Task<string?> TryIssueAsync(uint userId, Guid sessionId, Guid challengeId, byte[] codeHash,
        TwoFactorLimits limits, CancellationToken ct = default);
    Task<bool> MarkDeliveredAsync(uint userId, Guid sessionId, Guid challengeId, CancellationToken ct = default);
    Task<bool> VerifyAsync(uint userId, Guid sessionId, Guid challengeId, byte[] codeHash,
        TwoFactorLimits limits, CancellationToken ct = default);
    Task<bool> ConsumeAsync(uint userId, Guid sessionId, Guid challengeId, CancellationToken ct = default);
    Task InvalidateAsync(uint userId, Guid sessionId, Guid challengeId, CancellationToken ct = default);
}
