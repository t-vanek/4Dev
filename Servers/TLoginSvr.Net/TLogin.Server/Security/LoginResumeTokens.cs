using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TLogin.Server.Security;

/// <summary>One-use proof of a completed second factor across the client's map/login connections.</summary>
public sealed class LoginResumeTokens(TimeProvider? timeProvider = null)
{
    private sealed record Grant(byte[] TokenHash, byte[] PasswordHash, string Ip, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<uint, Grant> _grants = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public string Issue(uint userId, string password, string ip)
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in _grants)
            if (entry.Value.Expires <= now) _grants.TryRemove(entry);

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // Only the latest successful login/return for this account may resume it again.
        _grants[userId] = new Grant(Hash(token), Hash(password), ip, now + Lifetime);
        return token;
    }

    public bool TryConsume(uint userId, string password, string ip, string token)
    {
        if (token.Length != 64 || !_grants.TryGetValue(userId, out var grant)) return false;
        if (grant.Expires <= _clock.GetUtcNow())
        {
            _grants.TryRemove(new KeyValuePair<uint, Grant>(userId, grant));
            return false;
        }

        return grant.Ip == ip
            && CryptographicOperations.FixedTimeEquals(grant.PasswordHash, Hash(password))
            && CryptographicOperations.FixedTimeEquals(grant.TokenHash, Hash(token))
            && _grants.TryRemove(new KeyValuePair<uint, Grant>(userId, grant));
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
