using TWorld.Data;

namespace TWorld.Server;

/// <summary>Bound from the <c>World</c> configuration section (appsettings + environment overrides).</summary>
public sealed class WorldServerOptions
{
    /// <summary>TCP listen port for inbound server peers. Default 3815 (DEF_WORLDPORT).</summary>
    public int Port { get; set; } = 3815;

    public byte GroupId { get; set; } = 1;
    public byte ServerId { get; set; } = 1;
    public Security.PeerTlsServerOptions PeerTls { get; set; } = new();

    /// <summary>Optional nation override; when null the value is read from TGetNation.</summary>
    public byte? Nation { get; set; }

    /// <summary>The map servers (main ids) whose players count in the kingdom balance (WorldService.Nation.cs). The C++ counted its own
    /// deployment's field servers, 4, 5, 6, 7 and 10; null or empty counts every map server.</summary>
    public byte[]? CountryBalanceServers { get; set; }

    public WorldDbOptions Db { get; set; } = new();
}
