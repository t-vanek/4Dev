using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TMap.Protocol;
using TMap.Server.Security;

namespace TMap.Server.Net;

/// <summary>
/// The single outbound connection from this map server to the World server (the MW/SM/DM planes).
/// Server↔server traffic uses mutual TLS. Mirrors the C++ <c>m_world</c>
/// (a <c>CTMapSession</c>) that <c>InitNetwork</c> connects to <c>WorldIP:WorldPort</c> and immediately
/// sends <c>MW_CONNECT_ACK</c> on. Reconnects with backoff if the link drops.
/// </summary>
public sealed class WorldLink : IWorldSink
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly PeerTlsClientOptions _tlsOptions;
    private volatile PacketSendQueue? _sendQueue;

    private volatile Socket? _socket;

    public WorldLink(string host, int port, ILogger logger, PeerTlsClientOptions tlsOptions)
    {
        _host = host;
        _port = port;
        _logger = logger;
        _tlsOptions = tlsOptions;
    }

    public bool IsConnected => _socket is { Connected: true };

    public void Send(byte[] packet) => Enqueue(packet);
    public void Send(PacketWriter writer) => Enqueue(writer.AsSpan());

    private void Enqueue(ReadOnlySpan<byte> packet)
    {
        if (_sendQueue is { } queue && !queue.TryEnqueue(packet) && ReferenceEquals(queue, _sendQueue))
        {
            _logger.LogWarning("World link outbound queue limit exceeded; reconnecting.");
            try { _socket?.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Runs the connect/receive/reconnect loop until cancellation. <paramref name="onConnected"/> fires
    /// after each (re)connect (send MW_CONNECT_ACK there); <paramref name="onPacket"/> receives each framed
    /// decoded application packet; <paramref name="onDisconnected"/> fires when a live link drops.
    /// </summary>
    public async Task RunAsync(
        Func<ValueTask> onConnected,
        Func<byte[], ValueTask> onPacket,
        Func<ValueTask> onDisconnected,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket? socket = null;
            Stream? stream = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(_host, _port, token);
                stream = await PeerTls.ConnectAsync(socket, _tlsOptions, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { socket?.Dispose(); break; }
            catch (Exception ex)
            {
                socket?.Dispose();
                _logger.LogWarning("World link connect to {Host}:{Port} failed ({Msg}); retrying in 3s.", _host, _port, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(3), token); } catch { break; }
                continue;
            }

            var queue = new PacketSendQueue();
            _sendQueue = queue;
            _socket = socket;
            _logger.LogInformation("World link connected to {Host}:{Port}.", _host, _port);

            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var sendLoop = SendLoopAsync(socket, stream, queue, sendCts.Token);

            try
            {
                await onConnected();
                await ReceiveLoopAsync(stream, onPacket, sendCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "World link receive faulted.");
            }
            finally
            {
                _socket = null;
                _sendQueue = null;
                queue.Complete();
                sendCts.Cancel();
                try { await sendLoop; } catch { /* ignore */ }
                try { socket.Shutdown(SocketShutdown.Both); } catch { /* already closed */ }
                await stream.DisposeAsync();
                socket.Dispose();
                try { await onDisconnected(); } catch { /* ignore */ }
            }

            if (token.IsCancellationRequested) break;
            _logger.LogWarning("World link dropped; reconnecting in 3s.");
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); } catch { break; }
        }
    }

    private async Task ReceiveLoopAsync(Stream stream, Func<byte[], ValueTask> onPacket, CancellationToken token)
    {
        var framer = new PacketFramer();
        var buffer = new byte[64 * 1024];
        while (!token.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer, token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }

            if (n == 0) break; // world closed

            framer.Append(buffer.AsSpan(0, n));
            try
            {
                while (framer.TryReadPacket(out var packet))
                    await onPacket(packet);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "World link: malformed framing; dropping link.");
                return;
            }
        }
    }

    private static async Task SendLoopAsync(Socket socket, Stream stream, PacketSendQueue queue, CancellationToken token)
    {
        try
        {
            await queue.RunAsync(socket, null, token, stream);
        }
        catch (OperationCanceledException) { /* reconnecting / shutting down */ }
        catch (SocketException) { /* world gone */ }
        catch (ObjectDisposedException) { /* socket disposed */ }
        catch (IOException) { /* peer closed */ }
        finally
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
