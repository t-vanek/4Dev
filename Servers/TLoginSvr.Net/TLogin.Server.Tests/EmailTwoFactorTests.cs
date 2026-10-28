using TLogin.Protocol;
using Xunit;
using static TLogin.Server.Tests.EmailTwoFactorHarness;

namespace TLogin.Server.Tests;

public sealed class EmailTwoFactorTests
{
    private static async Task<(TcpTestClient Client, string Token)> AuthenticateAsync(EmailTwoFactorHarness host)
    {
        var (client, _) = await host.ConnectAsync();
        client.Send(LoginPacket());
        Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(client).Id);
        client.Send(Confirm(host.Email.Code));
        Assert.Equal((byte)SecurityCode.Correct, Read(client).ReadByte());
        client.Send(LoginPacket());
        return (client, ReadResumeToken(Read(client)));
    }

    [Fact]
    public async Task ReturnToCharacterSelectAcrossNewConnectionsRotatesTokenWithoutAnotherEmail()
    {
        await using var host = new EmailTwoFactorHarness();
        var (first, token) = await AuthenticateAsync(host);
        first.Dispose(); // The real client closes its login connection before entering the map.
        host.Store.DenyIssue = true;

        for (int i = 0; i < 3; i++)
        {
            var (returning, state) = await host.ConnectAsync();
            returning.Send(LoginPacket(resumeToken: token));
            string nextToken = ReadResumeToken(Read(returning));
            Assert.NotEqual(token, nextToken);
            Assert.True(state.LoggedIn);
            Assert.True(state.Agreement);
            Assert.Null(state.TwoFactor);
            token = nextToken;
            returning.Dispose();
        }

        Assert.Equal(1, host.Email.Calls);
        Assert.Equal(1, host.Store.Consumes);
        Assert.Equal(4, host.Authentication.Logins);
    }

    [Fact]
    public async Task OrdinaryLoginStillRequiresCodeAfterAnAuthenticatedGameSession()
    {
        await using var host = new EmailTwoFactorHarness();
        await AuthenticateAsync(host);
        var (fresh, state) = await host.ConnectAsync();
        fresh.Send(LoginPacket());
        Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(fresh).Id);
        Assert.False(state.LoggedIn);
        Assert.Equal(2, host.Email.Calls);
        Assert.Equal(1, host.Authentication.Logins);
    }

    [Theory]
    [InlineData("other", "password")]
    [InlineData("tester", "new-password")]
    public async Task ReturnTokenCannotAuthenticateAnotherAccountOrChangedPassword(string user, string password)
    {
        await using var host = new EmailTwoFactorHarness();
        var (_, token) = await AuthenticateAsync(host);
        host.Authentication.CurrentPassword = password;
        var (returning, state) = await host.ConnectAsync();
        returning.Send(LoginPacket(user, password, token));
        Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(returning).Id);
        Assert.False(state.LoggedIn);
        Assert.Equal(1, host.Authentication.Logins);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsedOrForgedReturnTokenCannotSkipCode(bool replay)
    {
        await using var host = new EmailTwoFactorHarness();
        var (_, token) = await AuthenticateAsync(host);
        if (replay)
        {
            var (returning, _) = await host.ConnectAsync();
            returning.Send(LoginPacket(resumeToken: token));
            ReadResumeToken(Read(returning));
        }
        else token = new string('0', 64);

        var (invalid, state) = await host.ConnectAsync();
        invalid.Send(LoginPacket(resumeToken: token));
        Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(invalid).Id);
        Assert.False(state.LoggedIn);
        Assert.Equal(replay ? 2 : 1, host.Authentication.Logins);
    }

    [Fact]
    public async Task ReturnToCharacterSelectRetainsDuplicateRecoveryAndAgreementChecks()
    {
        await using var host = new EmailTwoFactorHarness();
        var (_, token) = await AuthenticateAsync(host);
        host.Authentication.DuplicateOnce = true;
        host.Authentication.Logins = 0;
        host.Authentication.Result = LoginResult.NeedAgreement;
        var (returning, state) = await host.ConnectAsync();
        returning.Send(LoginPacket(resumeToken: token));
        ReadResumeToken(Read(returning), LoginResult.NeedAgreement);
        Assert.True(state.LoggedIn);
        Assert.False(state.Agreement);
        Assert.Equal(1, host.Authentication.Releases);
        Assert.Equal(1, host.Email.Calls);
    }

    [Fact]
    public async Task ReturnTokenDoesNotBypassAccountRestrictionsOrSurviveFailedLogin()
    {
        await using var host = new EmailTwoFactorHarness();
        var (_, token) = await AuthenticateAsync(host);
        host.Authentication.Result = LoginResult.Block;
        var (returning, state) = await host.ConnectAsync();
        returning.Send(LoginPacket(resumeToken: token));
        var blocked = Read(returning);
        Assert.Equal(Msg.CS_LOGIN_ACK, blocked.Id);
        Assert.Equal((byte)LoginResult.Block, blocked.ReadByte());
        Assert.Equal(0u, blocked.ReadUInt32());
        Assert.Equal(0u, blocked.ReadUInt32());
        Assert.Equal(0u, blocked.ReadUInt32());
        Assert.False(state.LoggedIn);

        host.Authentication.Result = LoginResult.Success;
        returning.Send(LoginPacket(resumeToken: token));
        Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(returning).Id);
        Assert.False(state.LoggedIn);
    }

    [Fact]
    public async Task ExistingEncryptedClientCompletesEmailChallengeThenReloginWithoutLeakingMapKey()
    {
        await using var host = new EmailTwoFactorHarness();
        var (client, state) = await host.ConnectAsync();
        client.Send(LoginPacket());
        Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(client).Id);
        Assert.Matches("^[0-9]{6}$", host.Email.Code);
        Assert.Equal("owner@example.test", host.Email.Recipient);
        Assert.Equal(0, host.Authentication.Logins);
        Assert.False(state.LoggedIn);
        // Authenticated game operations are unavailable until the full second login completes.
        client.Send(new PacketWriter(Msg.CS_GROUPLIST_REQ));
        client.Send(Confirm("wrong"));
        var incorrect = Read(client);
        Assert.Equal(Msg.CS_SECURITYRESULT_ACK, incorrect.Id);
        Assert.Equal((byte)SecurityCode.Incorrect, incorrect.ReadByte());
        client.Send(Confirm(host.Email.Code));
        Assert.Equal((byte)SecurityCode.Correct, Read(client).ReadByte());
        Assert.False(state.LoggedIn);
        Assert.Equal(0, host.Authentication.Logins);
        client.Send(LoginPacket());
        var login = Read(client);
        Assert.Equal(Msg.CS_LOGIN_ACK, login.Id);
        Assert.Equal((byte)LoginResult.Success, login.ReadByte());
        Assert.Equal(42u, login.ReadUInt32());
        Assert.Equal(123u, login.ReadUInt32());
        Assert.Equal(987654u, login.ReadUInt32());
        Assert.Equal(1, host.Store.Consumes);
        Assert.Equal(1, host.Authentication.Logins);
    }

    [Fact]
    public async Task AChangedButValidPasswordStillRequiresAFreshSecondFactor()
    {
        await using var host = new EmailTwoFactorHarness();
        var (client, state) = await host.ConnectAsync(); client.Send(LoginPacket()); Read(client);
        client.Send(Confirm(host.Email.Code)); Read(client);
        host.Authentication.CurrentPassword = "new-password";
        client.Send(LoginPacket(password: "new-password"));
        Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(client).Id);
        Assert.Equal(0, host.Store.Consumes); Assert.Equal(0, host.Authentication.Logins); Assert.False(state.LoggedIn);
    }

    [Fact]
    public async Task DatabaseFailureDuringVerificationClearsPendingStateAndReturnsFailure()
    {
        await using var host = new EmailTwoFactorHarness();
        var (client, state) = await host.ConnectAsync(); client.Send(LoginPacket()); Read(client);
        host.Store.Unavailable = true;
        client.Send(Confirm(host.Email.Code));
        Assert.Equal((byte)SecurityCode.Incorrect, Read(client).ReadByte());
        Assert.Null(state.TwoFactor); Assert.False(state.LoggedIn); Assert.Equal(0, host.Authentication.Logins);
    }

    [Fact]
    public async Task VerifiedCodeAndGrantAreSingleUse()
    {
        await using var host = new EmailTwoFactorHarness();
        var (client, _) = await host.ConnectAsync();
        client.Send(LoginPacket()); Read(client);
        client.Send(Confirm(host.Email.Code)); Assert.Equal((byte)SecurityCode.Correct, Read(client).ReadByte());
        client.Send(Confirm(host.Email.Code)); Assert.Equal((byte)SecurityCode.Incorrect, Read(client).ReadByte());
        client.Send(LoginPacket()); Assert.Equal((byte)LoginResult.Success, Read(client).ReadByte());
        host.Store.DenyIssue = true;
        client.Send(LoginPacket()); Assert.Equal((byte)LoginResult.Internal, Read(client).ReadByte());
        Assert.Equal(1, host.Authentication.Logins);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GrantIsBoundToAccountAndPassword(bool changeAccount)
    {
        await using var host = new EmailTwoFactorHarness();
        var (client, state) = await host.ConnectAsync();
        client.Send(LoginPacket()); Read(client);
        client.Send(Confirm(host.Email.Code)); Read(client);
        client.Send(changeAccount ? LoginPacket("other") : LoginPacket(password: "different"));
        var result = Read(client);
        if (changeAccount) Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, result.Id);
        else Assert.Equal((byte)LoginResult.InvalidPasswd, result.ReadByte());
        Assert.Equal(0, host.Store.Consumes);
        Assert.False(state.LoggedIn);
    }

    [Fact]
    public async Task AnotherConnectionCannotUseAChallengeOrAnAlreadyVerifiedGrant()
    {
        await using var host = new EmailTwoFactorHarness();
        var (first, _) = await host.ConnectAsync();
        first.Send(LoginPacket()); Read(first); string old = host.Email.Code;
        var (second, state) = await host.ConnectAsync();
        second.Send(Confirm(old)); Assert.Equal((byte)SecurityCode.Incorrect, Read(second).ReadByte());
        second.Send(LoginPacket()); Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(second).Id);
        first.Send(Confirm(old)); Assert.Equal((byte)SecurityCode.Incorrect, Read(first).ReadByte());
        Assert.False(state.LoggedIn);
        Assert.Equal(0, host.Authentication.Logins);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredCodeOrGrantCannotAuthenticate(bool expireGrant)
    {
        await using var host = new EmailTwoFactorHarness();
        var (client, state) = await host.ConnectAsync();
        client.Send(LoginPacket()); Read(client);
        if (expireGrant) { client.Send(Confirm(host.Email.Code)); Read(client); }
        host.Store.Now = host.Store.Now.AddMinutes(10);
        if (expireGrant)
        { client.Send(LoginPacket()); Assert.Equal(Msg.CS_SECURITYCONFIRM_REQ, Read(client).Id); }
        else
        { client.Send(Confirm(host.Email.Code)); Assert.Equal((byte)SecurityCode.Incorrect, Read(client).ReadByte()); }
        Assert.False(state.LoggedIn);
        Assert.Equal(0, host.Authentication.Logins);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task DeliveryFailureDatabaseFailureOrThrottlingFailsClosed(bool smtp, bool database, bool throttle)
    {
        await using var host = new EmailTwoFactorHarness();
        host.Email.Fail = smtp; host.Store.Unavailable = database; host.Store.DenyIssue = throttle;
        var (client, state) = await host.ConnectAsync(); client.Send(LoginPacket());
        var reply = Read(client);
        Assert.Equal(Msg.CS_LOGIN_ACK, reply.Id); Assert.Equal((byte)LoginResult.Internal, reply.ReadByte());
        Assert.Equal(0u, reply.ReadUInt32()); Assert.Equal(0u, reply.ReadUInt32()); Assert.Equal(0u, reply.ReadUInt32());
        Assert.False(state.LoggedIn); Assert.Null(state.TwoFactor); Assert.Equal(0, host.Authentication.Logins);
        if (smtp) { Assert.Equal(1, host.Store.Invalidations); Assert.False(host.Store.Delivered); }
    }

    [Fact]
    public async Task SmtpAcceptanceWithoutDatabaseDeliveryCommitDoesNotCreateAUsableChallenge()
    {
        await using var host = new EmailTwoFactorHarness(); host.Store.FailDeliveryCommit = true;
        var (client, state) = await host.ConnectAsync(); client.Send(LoginPacket());
        Assert.Equal((byte)LoginResult.Internal, Read(client).ReadByte());
        Assert.Null(state.TwoFactor); Assert.Equal(1, host.Store.Invalidations);
    }

    [Fact]
    public async Task DuplicateRecoveryHappensOnlyAfterSecondFactorAndAgreementResultIsPreserved()
    {
        await using var host = new EmailTwoFactorHarness();
        host.Authentication.DuplicateOnce = true; host.Authentication.Result = LoginResult.NeedAgreement;
        var (client, state) = await host.ConnectAsync(); client.Send(LoginPacket()); Read(client);
        Assert.Equal(0, host.Authentication.Releases);
        client.Send(Confirm(host.Email.Code)); Read(client); client.Send(LoginPacket());
        Assert.Equal((byte)LoginResult.NeedAgreement, Read(client).ReadByte());
        Assert.Equal(1, host.Authentication.Releases); Assert.False(state.Agreement);
    }

    [Fact]
    public async Task AccountsWithoutEnrollmentKeepNormalLoginAndLegacySecurityStillCannotBypass()
    {
        await using var host = new EmailTwoFactorHarness(); host.Store.Required = false;
        var (client, _) = await host.ConnectAsync(); client.Send(LoginPacket());
        Assert.Equal((byte)LoginResult.Success, Read(client).ReadByte()); Assert.Equal(0, host.Email.Calls);
        host.Authentication.Result = LoginResult.Security; client.Send(LoginPacket());
        var denied = Read(client); Assert.Equal((byte)LoginResult.Security, denied.ReadByte());
        Assert.Equal(0u, denied.ReadUInt32()); Assert.Equal(0u, denied.ReadUInt32()); Assert.Equal(0u, denied.ReadUInt32());
    }
}
