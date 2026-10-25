using TMap.Protocol;
using TMap.Server.Net;

namespace TMap.Server.Map;

/// <summary>Where a client session is in the map-enter handshake.</summary>
public enum EnterState
{
    /// <summary>Fresh socket, before CS_CONNECT_REQ.</summary>
    Connected = 0,

    /// <summary>CS_CONNECT_REQ accepted; MW_ADDCHAR_ACK sent; the map↔world handshake is in flight.</summary>
    Entering,

    /// <summary>MW_CONRESULT_REQ(CN_SUCCESS) received; CS_CHARINFO_ACK sent; awaiting CS_CONREADY_REQ.</summary>
    Granted,

    /// <summary>CS_CONREADY_REQ received; the player is live in a cell and visible to neighbours.</summary>
    InGame,
}

/// <summary>
/// Per-connection game state — the ported <c>CTMapSession</c> + the session-scoped parts of
/// <c>CTPlayer</c>. Stored on <see cref="ClientConnection.State"/>. Only ever touched on the batch thread.
/// </summary>
public sealed class ClientSession
{
    public ClientSession(IClientChannel conn) => Conn = conn;

    public IClientChannel Conn { get; }

    public EnterState State { get; set; } = EnterState.Connected;

    // Identity from CS_CONNECT_REQ / the world handshake.
    public ushort Version { get; set; }
    public byte Channel { get; set; }
    public uint UserId { get; set; }
    public uint CharId { get; set; }
    public uint Key { get; set; }
    public uint ClientIp { get; set; }
    public ushort ClientPort { get; set; }

    /// <summary>True once this map is the char's "main" server for the CHECKMAIN handshake.</summary>
    public bool IsMain { get; set; }
    public bool EntryValidated { get; set; }

    /// <summary>Unlock applies only to this connection; the code and failed attempts are account-wide.</summary>
    public bool SecurityUnlocked { get; set; }
    public bool SecurityStateFailed { get; set; }

    /// <summary>The control server's link (C++ <c>SESSION_SERVER</c> from <c>m_addrCtrl</c>; MapService.Gm.cs).</summary>
    public bool IsControl { get; set; }

    /// <summary>The loaded character (null until char data is resolved during the handshake).</summary>
    public Character? Char { get; set; }

    /// <summary>The player-to-player deal (trade) state (C++ <c>CTPlayer::m_dealItem</c>) — a transient
    /// two-party state machine; idle until an invite is accepted. Phase 39.</summary>
    public Deal Deal { get; } = new();

    /// <summary>The player's personal store / vendor (C++ <c>CTPlayer::m_bStore</c> + <c>m_mapStoreItem</c>) —
    /// transient; closed until the player opens one. Phase 40.</summary>
    public Store Store { get; } = new();

    /// <summary>The (channel, map) grid this player is live in, or null before entry / after leaving
    /// (C++ <c>CTPlayer::m_pMAP</c>). Set by <see cref="MapState.EnterWorld"/>.</summary>
    public MapGrid? Grid { get; set; }

    /// <summary>The packed key of the cell this player is currently bucketed in (<c>MAKELONG(cellX, cellZ)</c>).
    /// Maintained by <see cref="MapGrid"/> so membership never desyncs from position.</summary>
    public uint CellKey { get; set; }

    public void Send(PacketWriter w) => Conn.Send(w);
    public void Send(byte[] packet) => Conn.Send(packet);
}
