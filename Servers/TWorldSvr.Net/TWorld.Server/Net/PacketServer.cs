using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TWorld.Server.Security;

namespace TWorld.Server.Net;

/// <summary>
/// Async TCP acceptor for inbound server peers (map/control/relay). Replaces the C++ IOCP accept thread.
/// Each accepted socket becomes a <see cref="PacketConnection"/> handled on its own task; framed packets
/// are handed to <c>onPacket</c> for enqueue onto the world's batch task.
/// </summary>
public sealed class PacketServer
{
    private readonly ILogger _logger;
    private readonly IPeerAuthenticator _authenticator;
    private readonly ConcurrentDictionary<PacketConnection, byte> _connections = new();

    public PacketServer(ILogger logger, IPeerAuthenticator authenticator)
    { _logger = logger; _authenticator = authenticator; }

    public int ConnectionCount => _connections.Count;

    public async Task ListenAsync(
        int port,
        Func<PacketConnection, byte[], ValueTask> onPacket,
        Func<PacketConnection, ValueTask>? onConnected,
        Func<PacketConnection, ValueTask>? onDisconnected,
        CancellationToken token)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(backlog: 64);
        _logger.LogInformation("World server listening on port {Port}.", port);

        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Accept failed.");
                continue;
            }

            _ = HandleConnectionAsync(socket, onPacket, onConnected, onDisconnected, token);
        }
    }

    private async Task HandleConnectionAsync(
        Socket socket,
        Func<PacketConnection, byte[], ValueTask> onPacket,
        Func<PacketConnection, ValueTask>? onConnected,
        Func<PacketConnection, ValueTask>? onDisconnected,
        CancellationToken token)
    {
        AuthenticatedPeer peer;
        try { peer = await _authenticator.AuthenticateAsync(socket, token); }
        catch (Exception ex)
        {
            _logger.LogWarning("Peer TLS authentication rejected from {Endpoint}: {Type}", socket.RemoteEndPoint, ex.GetType().Name);
            socket.Dispose();
            return;
        }
        var conn = new PacketConnection(socket, _logger, peer.Stream, peer.Identity);
        _connections.TryAdd(conn, 0);
        _logger.LogInformation("Peer connected: {Endpoint}", conn.RemoteEndPoint);

        try
        {
            if (onConnected is not null) await onConnected(conn);
            await conn.RunAsync(onPacket, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection {Endpoint} faulted.", conn.RemoteEndPoint);
        }
        finally
        {
            _connections.TryRemove(conn, out _);
            if (onDisconnected is not null)
            {
                try { await onDisconnected(conn); } catch { }
            }
            await conn.DisposeAsync();
            _logger.LogInformation("Peer disconnected: {Endpoint}", conn.RemoteEndPoint);
        }
    }
}
