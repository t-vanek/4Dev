namespace TLogin.Server.Security;

/// <summary>HWID gate (anti-cheat). The C++ flow is wired but effectively disabled; default auto-passes.</summary>
public interface IHwidValidator
{
    bool IsAllowed(string userId, string hwid, string macAddress, string pcName);
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
