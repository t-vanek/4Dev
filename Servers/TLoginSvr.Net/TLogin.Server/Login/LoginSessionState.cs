namespace TLogin.Server.Login;

/// <summary>
/// Per-connection login state, the ported subset of the C++ <c>CTUser</c> fields used by the handlers.
/// </summary>
public sealed class LoginSessionState
{
    public uint UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Mirrors <c>CTUser::m_bAgreement</c>, which defaults to TRUE; only LR_NEEDAGREEMENT clears it.</summary>
    public bool Agreement { get; set; } = true;

    public byte GroupId { get; set; }
    public byte CreateCount { get; set; }

    /// <summary>Random per-login nonce echoed in CS_LOGIN_ACK (<c>m_dlCheckKey</c>).</summary>
    public long CheckKey { get; set; }

    public string MacAddress { get; set; } = "";
    public string SecurityCode { get; set; } = "";

    public bool LoggedIn => UserId != 0;

    public void ResetAuthentication()
    {
        UserId = 0;
        UserName = Password = MacAddress = SecurityCode = "";
        Agreement = false;
        GroupId = CreateCount = 0;
        CheckKey = 0;
    }
}
