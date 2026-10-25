using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TControl.Protocol;

namespace TControl.Server.Net;

/// <summary>
/// One socket — either an inbound manager (GM tool) peer or an outbound game-server peer. All control
/// traffic is <b>plaintext</b> (no cipher). The receive loop frames packets and hands each to
/// <c>onPacket</c> (which enqueues to the single batch task); a serialized send loop writes outbound
/// packets. Per-connection state (a <c>ManagerSession</c> or <c>ServerSession</c>) hangs off
/// <see cref="State"/>.
/// </summary>
public sealed class PacketConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly Stream? _stream;
    private readonly ILogger _logger;
    private readonly PacketFramer _framer = new();
    private readonly Channel<byte[]> _sendQueue =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    private Func<PacketConnection, byte[], ValueTask>? _onPacket;
    private Task? _sendLoop;
    private int _closed;

    public PacketConnection(Socket socket, ILogger logger, Stream? stream = null)
    {
        _socket = socket;
        _stream = stream;
        _logger = logger;
        RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
    }

    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>Per-connection peer state (a <c>ManagerSession</c> or <c>ServerSession</c>).</summary>
    public object? State { get; set; }

    public void Send(byte[] packet) => _sendQueue.Writer.TryWrite(packet);
    public void Send(PacketWriter writer) => Send(writer.ToArray());

    public void Close()
    {
        // Disconnect notifications may already be queued when shutdown closes every session.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task RunAsync(Func<PacketConnection, byte[], ValueTask> onPacket, CancellationToken outerToken)
    {
        _onPacket = onPacket;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, outerToken);
        _sendLoop = SendLoopAsync(linked.Token);
        try
        {
            await ReceiveLoopAsync(linked.Token);
        }
        finally
        {
            await DisposeAsync();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (!token.IsCancellationRequested)
        {
            int n;
            try
            {
                n = _stream is null ? await _socket.ReceiveAsync(buffer, SocketFlags.None, token)
                    : await _stream.ReadAsync(buffer, token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }

            if (n == 0) break; // peer closed

            _framer.Append(buffer.AsSpan(0, n));

            try
            {
                while (_framer.TryReadPacket(out var packet))
                {
                    if (_onPacket is not null)
                        await _onPacket(this, packet);
                }
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Dropping {Endpoint}: malformed framing.", RemoteEndPoint);
                return;
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var packet in _sendQueue.Reader.ReadAllAsync(token))
            {
                int sent = 0;
                if (_stream is not null) { await _stream.WriteAsync(packet, token); sent = packet.Length; }
                while (sent < packet.Length)
                    sent += await _socket.SendAsync(packet.AsMemory(sent), SocketFlags.None, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { Close(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        _sendQueue.Writer.TryComplete();
        _cts.Cancel();
        if (_sendLoop is not null)
        {
            try { await _sendLoop; } catch { }
        }

        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        if (_stream is not null) await _stream.DisposeAsync();
        _socket.Dispose();
        _cts.Dispose();
    }
}
