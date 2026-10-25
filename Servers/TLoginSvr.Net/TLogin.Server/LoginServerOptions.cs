using TLogin.Data;
using TLogin.Protocol;

namespace TLogin.Server;

/// <summary>Bound from the <c>Login</c> configuration section (appsettings + environment overrides).</summary>
public sealed class LoginServerOptions
{
    /// <summary>TCP listen port. Default 4816 (the C++ default).</summary>
    public int Port { get; set; } = 4816;

    /// <summary>Concurrent authentication DB sequences; excess logins wait without consuming a pooled SQL connection.</summary>
    public int MaxConcurrentLogins { get; set; } = 100;

    /// <summary>
    /// Optional control-server IP. Connections from this address skip the per-packet cipher
    /// (the C++ treats the control server as a plaintext SESSION_SERVER peer).
    /// If empty, it is discovered from TLoadService at startup.
    /// </summary>
    public string? ControlServerIp { get; set; }

    /// <summary>Optional nation override; when null the value is read from TGetNation.</summary>
    public Nation? Nation { get; set; }

    /// <summary>When true (default), enforces the client version checksum that OnCS_LOGIN_REQ validates.</summary>
    public bool ValidateClientChecksum { get; set; } = true;

    public LoginDbOptions Db { get; set; } = new();
}
