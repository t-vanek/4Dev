using TLogin.Server.Security;
using Xunit;

namespace TLogin.Server.Tests;

public sealed class LoginResumeTokensTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ConcurrentReturnsCanOnlyConsumeTheTokenOnce()
    {
        var tokens = new LoginResumeTokens();
        string token = tokens.Issue(42, "password", "127.0.0.1");
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => tokens.TryConsume(42, "password", "127.0.0.1", token))));
        Assert.Single(results, accepted => accepted);
    }

    [Theory]
    [InlineData(43u, "password", "127.0.0.1")]
    [InlineData(42u, "other-password", "127.0.0.1")]
    [InlineData(42u, "password", "127.0.0.2")]
    public void WrongAccountPasswordOrIpDoesNotConsumeTheOwnersToken(uint user, string password, string ip)
    {
        var tokens = new LoginResumeTokens();
        string token = tokens.Issue(42, "password", "127.0.0.1");
        Assert.False(tokens.TryConsume(user, password, ip, token));
        Assert.True(tokens.TryConsume(42, "password", "127.0.0.1", token));
    }

    [Fact]
    public void LatestVerificationReplacesPreviousTokenAndRestartInvalidatesIt()
    {
        var tokens = new LoginResumeTokens();
        string old = tokens.Issue(42, "password", "127.0.0.1");
        string current = tokens.Issue(42, "password", "127.0.0.1");
        Assert.False(tokens.TryConsume(42, "password", "127.0.0.1", old));
        Assert.False(new LoginResumeTokens().TryConsume(42, "password", "127.0.0.1", current));
        Assert.True(tokens.TryConsume(42, "password", "127.0.0.1", current));
    }

    [Fact]
    public void TokenExpiresAfter24Hours()
    {
        var clock = new Clock();
        var tokens = new LoginResumeTokens(clock);
        string token = tokens.Issue(42, "password", "127.0.0.1");
        clock.Now = clock.Now.AddHours(24);
        Assert.False(tokens.TryConsume(42, "password", "127.0.0.1", token));
    }
}
