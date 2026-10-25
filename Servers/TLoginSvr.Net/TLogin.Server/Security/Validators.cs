namespace TLogin.Server.Security;

/// <summary>HWID gate (anti-cheat). The C++ flow is wired but effectively disabled; default auto-passes.</summary>
public interface IHwidValidator
{
    bool IsAllowed(string userId, string hwid, string macAddress, string pcName);
}

/// <summary>2FA trusted-device gate. An empty device identity cannot establish trust.</summary>
public interface ITwoFactorValidator
{
    bool IsTrusted(string userId, string hwid, string macAddress, string pcName);
}

/// <summary>Security-code delivery. The unconfigured sender fails rather than claiming delivery.</summary>
public interface IEmailSender
{
    Task SendSecurityCodeAsync(string email, string code, CancellationToken ct = default);
}

public sealed class AutoPassHwidValidator : IHwidValidator
{
    public bool IsAllowed(string userId, string hwid, string macAddress, string pcName) => true;
}

public sealed class UntrustedDeviceValidator : ITwoFactorValidator
{
    public bool IsTrusted(string userId, string hwid, string macAddress, string pcName) => false;
}

public sealed class UnavailableEmailSender : IEmailSender
{
    public Task SendSecurityCodeAsync(string email, string code, CancellationToken ct = default) =>
        throw new InvalidOperationException("Security-code delivery is not configured.");
}
