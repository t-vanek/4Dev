using TMap.Data;

namespace TMap.Server;

/// <summary>
/// Bound from the <c>Map</c> configuration section (appsettings + environment overrides). Mirrors the
/// C++ <c>Configurations/TMapSvr.ini [TMapConfig]</c> keys read by <c>LoadConfig</c> (TMapSvr.cpp:635).
/// </summary>
public sealed class MapServerOptions
{
    /// <summary>This server's own client-facing TCP listen port. C++ <c>GamePort</c> (default 5816).</summary>
    public int Port { get; set; } = 5816;

    /// <summary>The World server to connect out to. C++ <c>WorldIP</c>/<c>WorldPort</c> (default 127.0.0.1:3816;
    /// the .NET TWorldSvr listens on 3815, so set this to 3815 when interoperating with that port).</summary>
    public string WorldIp { get; set; } = "127.0.0.1";
    public int WorldPort { get; set; } = 3816;
    public Security.PeerTlsClientOptions WorldTls { get; set; } = new();

    /// <summary>This map server's identity in the topology. C++ <c>GroupID</c>/<c>ServerID</c>.</summary>
    public byte GroupId { get; set; } = 1;
    public byte ServerId { get; set; } = 1;

    /// <summary>
    /// The channels this map server serves (announced to the world in MW_CONNECT_ACK). C++ default 1.
    /// <para>
    /// Defaults to <b>empty</b>, not <c>{ 1 }</c>, deliberately: the .NET configuration binder
    /// <i>appends</i> bound array elements to whatever the property already holds rather than replacing
    /// them. With a <c>{ 1 }</c> default, the <c>"Channels": [ 1 ]</c> in appsettings.json (or a
    /// <c>Map__Channels__0</c> env override) bound to <c>{ 1, 1 }</c> — which announced a channel count of
    /// 2 in <c>MW_CONNECT_ACK</c> and, worse, made <see cref="Map.MapService.InitMonsterSpawns"/> build
    /// every SE_DEFAULT spawn point once per duplicate, doubling the monsters on the map. It also meant
    /// configuring a single non-default channel (<c>Map__Channels__0=2</c>) yielded <c>{ 1, 2 }</c>.
    /// <see cref="Normalize"/> supplies the C++ default instead, after binding.
    /// </para>
    /// </summary>
    public byte[] Channels { get; set; } = Array.Empty<byte>();

    /// <summary>UDP log-sink endpoint. C++ <c>LogIP</c>/<c>LogPort</c> (default 127.0.0.1:7000).</summary>
    public string LogIp { get; set; } = "127.0.0.1";

    /// <summary>Legacy configuration compatibility only. Source IP no longer grants Control privileges.</summary>
    public string ControlIp { get; set; } = "";
    /// <summary>Dedicated mutual-TLS control listener; 0 disables direct Control access.</summary>
    public int ControlPort { get; set; }
    public Security.PeerTlsServerOptions ControlTls { get; set; } = new();
    public int LogPort { get; set; } = 7000;

    /// <summary>
    /// When true the client plane is accepted in plaintext (no RC4/XOR cipher). The deployed C++ map runs
    /// crypto ON, so this defaults to false. Set true only to interoperate with a plaintext client/bot.
    /// </summary>
    public bool NoCrypt { get; set; }

    /// <summary>The auction house (MapService.Auction.cs). On, as in the C++ 5.0 map server, unless set false.</summary>
    public bool AuctionEnabled { get; set; } = true;

    public MapDbOptions Db { get; set; } = new();

    /// <summary>
    /// Applies the defaults the configuration binder cannot express. Call once at startup, before anything
    /// reads <see cref="Channels"/>: an unbound channel list becomes the C++ default <c>{ 1 }</c>, and
    /// duplicates are collapsed (a repeated channel double-builds the per-channel spawn points).
    /// </summary>
    public void Normalize()
    {
        Channels = Channels is { Length: > 0 } ? Channels.Distinct().ToArray() : new byte[] { 1 };
    }
}
