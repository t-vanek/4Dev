using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TMap.Server.Security;

namespace TMap.Server.Net;

/// <summary>
/// Async TCP acceptor for game clients (the CS_MAP plane). Replaces the C++ AcceptEx/IOCP control thread.
/// Each accepted socket becomes a <see cref="ClientConnection"/> handled on its own task; framed+decrypted
/// packets are handed to <c>onPacket</c> for enqueue onto the map's batch task.
/// </summary>
public sealed class ClientListener
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ClientConnection, byte> _connections = new();

    public ClientListener(ILogger logger) => _logger = logger;

    /// <summary>When true, accept clients in plaintext (no cipher). Mirrors the <c>NoCrypt</c> option.</summary>
    public bool NoCrypt { get; set; }
    public IPeerAuthenticator? ControlAuthenticator { get; init; }

    public int ConnectionCount => _connections.Count;

    public async Task ListenAsync(
        int port,
        Func<ClientConnection, byte[], ValueTask> onPacket,
        Func<ClientConnection, ValueTask>? onConnected,
        Func<ClientConnection, ValueTask>? onDisconnected,
        CancellationToken token)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(backlog: 128);
        _logger.LogInformation("Map server listening for clients on port {Port} (crypt={Crypt}).", port, !NoCrypt);

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
        Func<ClientConnection, byte[], ValueTask> onPacket,
        Func<ClientConnection, ValueTask>? onConnected,
        Func<ClientConnection, ValueTask>? onDisconnected,
        CancellationToken token)
    {
        AuthenticatedPeer? peer = null;
        if (ControlAuthenticator is not null)
        {
            try
            {
                peer = await ControlAuthenticator.AuthenticateAsync(socket, token);
                if (peer.Identity.Role != PeerRole.Control)
                    throw new System.Security.Authentication.AuthenticationException("Control identity required.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Control TLS rejected: {Type}", ex.GetType().Name);
                if (peer is not null) await peer.Stream.DisposeAsync();
                socket.Dispose();
                return;
            }
        }
        var conn = new ClientConnection(socket, _logger, peer?.Stream, authenticatedControl: peer is not null);
        conn.UseCrypt = peer is null && !NoCrypt;
        _connections.TryAdd(conn, 0);
        _logger.LogDebug("Client connected: {Endpoint}", conn.RemoteEndPoint);

        try
        {
            if (onConnected is not null) await onConnected(conn);
            await conn.RunAsync(onPacket, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Client {Endpoint} faulted.", conn.RemoteEndPoint);
        }
        finally
        {
            _connections.TryRemove(conn, out _);
            if (onDisconnected is not null)
            {
                try { await onDisconnected(conn); } catch { /* ignore */ }
            }
            await conn.DisposeAsync();
            _logger.LogDebug("Client disconnected: {Endpoint}", conn.RemoteEndPoint);
        }
    }
}
