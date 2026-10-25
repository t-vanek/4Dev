using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TWorld.Protocol;
using TWorld.Server.Security;

namespace TWorld.Server.Net;

/// <summary>
/// One authenticated map/control peer over mutual TLS. The receive loop frames packets and hands each to <c>onPacket</c>
/// (which enqueues to the world's single batch task); a serialized send loop writes outbound packets.
/// </summary>
public sealed class PacketConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly PacketFramer _framer = new();
    private readonly Channel<byte[]> _sendQueue =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    private Func<PacketConnection, byte[], ValueTask>? _onPacket;
    private Task? _sendLoop;
    private int _closed;

    public PacketConnection(Socket socket, ILogger logger, Stream stream, PeerIdentity identity)
    {
        _socket = socket;
        _stream = stream;
        Identity = identity;
        _logger = logger;
        RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
    }

    public IPEndPoint? RemoteEndPoint { get; }
    public PeerIdentity Identity { get; }
    public void Close() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }

    /// <summary>Per-connection peer state (a <c>ServerSession</c>).</summary>
    public object? State { get; set; }

    public void Send(byte[] packet) => _sendQueue.Writer.TryWrite(packet);
    public void Send(PacketWriter writer) => Send(writer.ToArray());

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
                n = await _stream.ReadAsync(buffer, token);
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
                await _stream.WriteAsync(packet, token);
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
        await _stream.DisposeAsync();
        _socket.Dispose();
        _cts.Dispose();
    }
}
