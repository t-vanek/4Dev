using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TLogin.Data;

namespace TLogin.Server.Security;

public sealed record EmailChallenge(uint UserId, Guid SessionId, Guid ChallengeId);

public sealed class PendingEmailVerification(EmailChallenge challenge, string password)
{
    public EmailChallenge Challenge { get; } = challenge;
    private readonly byte[] _passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    public bool Verified { get; set; }
    public bool Matches(uint userId, Guid sessionId, string password) =>
        Challenge.UserId == userId && Challenge.SessionId == sessionId
        && CryptographicOperations.FixedTimeEquals(_passwordHash, SHA256.HashData(Encoding.UTF8.GetBytes(password)));
}

public sealed class EmailTwoFactorService(ITwoFactorStore store, IEmailSender sender,
    EmailTwoFactorOptions options, ILogger<EmailTwoFactorService> log)
{
    private readonly TwoFactorLimits _limits = options.Limits();

    public Task<bool> IsRequiredAsync(uint userId, CancellationToken ct) => store.IsRequiredAsync(userId, ct);

    public async Task<EmailChallenge?> BeginAsync(uint userId, Guid sessionId, CancellationToken ct)
    {
        var challenge = new EmailChallenge(userId, sessionId, Guid.NewGuid());
        string code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var email = await store.TryIssueAsync(userId, sessionId, challenge.ChallengeId, Hash(challenge, code), _limits, ct);
        if (email is null) return null;
        try
        {
            await sender.SendSecurityCodeAsync(email, code, ct);
            if (await store.MarkDeliveredAsync(userId, sessionId, challenge.ChallengeId, ct)) return challenge;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // SMTP exceptions can include recipients or message data. Never log their text or OTPs.
            log.LogWarning("Two-factor delivery failed for account {User}: {Type}", userId, ex.GetType().Name);
        }
        await store.InvalidateAsync(userId, sessionId, challenge.ChallengeId, ct);
        return null;
    }

    public Task<bool> VerifyAsync(EmailChallenge challenge, string code, CancellationToken ct)
    {
        // Malformed input consumes an attempt too. It cannot accidentally normalize to a valid code.
        byte[] hash = code.Length == 6 && code.All(c => c is >= '0' and <= '9') ? Hash(challenge, code) : new byte[32];
        return store.VerifyAsync(challenge.UserId, challenge.SessionId, challenge.ChallengeId, hash, _limits, ct);
    }

    public Task<bool> ConsumeAsync(EmailChallenge challenge, CancellationToken ct) =>
        store.ConsumeAsync(challenge.UserId, challenge.SessionId, challenge.ChallengeId, ct);

    private static byte[] Hash(EmailChallenge challenge, string code) => SHA256.HashData(
        Encoding.ASCII.GetBytes(challenge.SessionId.ToString("N") + challenge.ChallengeId.ToString("N") + code));
}
