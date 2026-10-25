using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace TControl.Server.Net;

/// <summary>
/// Outbound connector to a game server (the C++ <c>CTServer::Connect</c> path, triggered in the original
/// by SCM detecting a service RUNNING). This port instead <b>dials</b> each configured server and treats a
/// live connection as "running". <see cref="RunConnectionAsync"/> connects once and runs the connection to
/// completion; the caller (the connector loop) retries after a delay when it returns.
/// </summary>
public static class PacketClient
{
    /// <summary>Connect to <paramref name="host"/>:<paramref name="port"/> and run the connection until it
    /// drops. Returns true if the connection was established (regardless of how it ended), false if the
    /// connect attempt itself failed. Callbacks run on this task.</summary>
    public static async Task<bool> RunConnectionAsync(
        string host, int port, ILogger logger,
        Func<PacketConnection, byte[], ValueTask> onPacket,
        Func<PacketConnection, ValueTask>? onConnected,
        Func<PacketConnection, ValueTask>? onDisconnected,
        CancellationToken token, Security.PeerTlsClientOptions? tlsOptions = null)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, token);
        }
        catch (OperationCanceledException) { socket.Dispose(); return false; }
        catch (SocketException) { socket.Dispose(); return false; }

        Stream? stream = null;
        if (tlsOptions is not null)
        {
            try { stream = await Security.PeerTls.ConnectAsync(socket, tlsOptions, token); }
            catch (Exception ex)
            {
                logger.LogWarning("Peer TLS to {Host}:{Port} failed: {Type}", host, port, ex.GetType().Name);
                socket.Dispose();
                return false;
            }
        }
        var conn = new PacketConnection(socket, logger, stream);
        logger.LogInformation("Connected to game server {Host}:{Port}", host, port);
        try
        {
            if (onConnected is not null) await onConnected(conn);
            await conn.RunAsync(onPacket, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbound connection {Host}:{Port} faulted.", host, port);
        }
        finally
        {
            if (onDisconnected is not null)
            {
                try { await onDisconnected(conn); } catch { }
            }
            await conn.DisposeAsync();
            logger.LogInformation("Disconnected from game server {Host}:{Port}", host, port);
        }
        return true;
    }
}
