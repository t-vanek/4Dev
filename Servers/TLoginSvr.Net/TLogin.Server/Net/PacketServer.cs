using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TLogin.Protocol;

namespace TLogin.Server.Net;

/// <summary>
/// Async TCP acceptor. Replaces the C++ AcceptEx/IOCP control thread. Each accepted socket becomes a
/// <see cref="PacketConnection"/> handled on its own task. Peers in <see cref="PlaintextPeers"/>
/// (e.g. the control server) are handled without the per-packet cipher.
/// </summary>
public sealed class PacketServer
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<PacketConnection, byte> _connections = new();
    private readonly ConcurrentDictionary<long, Task> _connectionTasks = new();
    private long _connectionId;

    public PacketServer(ILogger logger) => _logger = logger;

    public HashSet<IPAddress> PlaintextPeers { get; } = new();

    public int ConnectionCount => _connections.Count;

    public async Task ListenAsync(
        int port,
        Func<PacketConnection, PacketReader, ValueTask> onPacket,
        Func<PacketConnection, ValueTask>? onConnected,
        CancellationToken token)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(backlog: 128);
        _logger.LogInformation("Login server listening on port {Port}.", port);

        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await listener.AcceptAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex)
                {
                    _logger.LogWarning(ex, "Accept failed.");
                    continue;
                }
                long id = Interlocked.Increment(ref _connectionId);
                var task = HandleConnectionAsync(socket, onPacket, onConnected, token);
                _connectionTasks[id] = task;
                _ = task.ContinueWith(completed => _connectionTasks.TryRemove(id, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            // Finish cancellation/OTP cleanup before the worker disposes its SMTP admission gate.
            await Task.WhenAll(_connectionTasks.Values.ToArray());
        }
    }

    private async Task HandleConnectionAsync(
        Socket socket,
        Func<PacketConnection, PacketReader, ValueTask> onPacket,
        Func<PacketConnection, ValueTask>? onConnected,
        CancellationToken token)
    {
        var conn = new PacketConnection(socket, _logger);
        bool plaintext = conn.RemoteEndPoint is not null && PlaintextPeers.Contains(conn.RemoteEndPoint.Address);
        conn.UseCrypt = !plaintext;
        _connections.TryAdd(conn, 0);

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
            await conn.DisposeAsync();
        }
    }
}
