using TLogin.Data;

namespace TLogin.Server.Security;

public sealed class EmailTwoFactorOptions
{
    public int CodeLifetimeSeconds { get; set; } = 300;
    public int ResendDelaySeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
    public int LockoutSeconds { get; set; } = 900;
    public int MaxSendsPerHour { get; set; } = 10;
    public int GrantLifetimeSeconds { get; set; } = 60;
    public SmtpOptions Smtp { get; set; } = new();

    public TwoFactorLimits Limits()
    {
        if (CodeLifetimeSeconds is < 60 or > 600 || ResendDelaySeconds is < 30 or > 3600
            || MaxAttempts is < 1 or > 10 || LockoutSeconds is < 60 or > 86400
            || MaxSendsPerHour is < 1 or > 100 || GrantLifetimeSeconds is < 10 or > 120
            || Smtp.TimeoutSeconds >= CodeLifetimeSeconds)
            throw new InvalidOperationException("Invalid Login email two-factor limits.");
        return new(CodeLifetimeSeconds, ResendDelaySeconds, MaxAttempts, LockoutSeconds,
            MaxSendsPerHour, GrantLifetimeSeconds);
    }
}

public enum SmtpEncryption { StartTls = 0, SslOnConnect = 1 }

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public SmtpEncryption Encryption { get; set; } = SmtpEncryption.StartTls;
    public string From { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 20;
}
