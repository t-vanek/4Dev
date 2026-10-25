using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TMap.Protocol;

namespace TMap.Server.Net;

/// <summary>
/// One game-client socket (the CS_MAP plane). A receive loop frames + decrypts inbound packets and hands
/// each decrypted packet to <c>onPacket</c> (which enqueues it onto the map's single serialized batch
/// task); a send loop encrypts + writes outbound packets. Mirrors the C++ IOCP <c>CTMapSession</c>
/// (a client session <i>is</i> a <c>CTPlayer</c>). The client plane is encrypted by default — the C++
/// TMapSvr sets <c>m_bUseCrypt = TRUE</c> at accept (TMapSvr.cpp).
/// </summary>
public sealed class ClientConnection : IClientChannel, IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly SessionCipher _cipher = new();
    private readonly PacketFramer _framer = new();
    private readonly PacketSendQueue _sendQueue = new();
    private readonly CancellationTokenSource _cts = new();

    private Func<ClientConnection, byte[], ValueTask>? _onPacket;
    private Task? _sendLoop;
    private int _closed;

    public ClientConnection(Socket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
        RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
    }

    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>Per-connection game state (a <c>ClientSession</c>).</summary>
    public object? State { get; set; }

    public bool UseCrypt
    {
        get => _cipher.UseCrypt;
        set => _cipher.UseCrypt = value;
    }

    /// <summary>Queues a copy: the send loop encrypts in place with this connection's own sequence number, and a
    /// broadcast hands the same array to every viewer — encrypting it once per viewer corrupted every copy but one
    /// (the C++ builds a <c>CPacket</c> per session).</summary>
    public void Send(byte[] packet) => Enqueue(packet);
    public void Send(PacketWriter writer) => Enqueue(writer.AsSpan());

    public long SendCalls => _sendQueue.SendCalls;
    public long SentPackets => _sendQueue.SentPackets;

    private void Enqueue(ReadOnlySpan<byte> packet)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (!_sendQueue.TryEnqueue(packet))
        {
            _logger.LogWarning("Dropping slow client {Endpoint}: outbound queue limit exceeded.", RemoteEndPoint);
            _cts.Cancel();
        }
    }

    /// <summary>Requests an orderly close of this connection (drains the send queue first).</summary>
    public void Close() => _ = DisposeAsync();

    public async Task RunAsync(Func<ClientConnection, byte[], ValueTask> onPacket, CancellationToken outerToken)
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
                n = await _socket.ReceiveAsync(buffer, SocketFlags.None, token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            if (n == 0) break; // peer closed

            _framer.Append(buffer.AsSpan(0, n));

            try
            {
                while (_framer.TryReadPacket(out var packet))
                {
                    if (!_cipher.DecryptInbound(packet))
                    {
                        _logger.LogWarning("Dropping {Endpoint}: packet failed decrypt/checksum/sequence.", RemoteEndPoint);
                        return;
                    }

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
            await _sendQueue.RunAsync(_socket, _cipher, token);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (SocketException) { /* peer gone */ }
        catch (ObjectDisposedException) { /* socket disposed */ }
        catch (IOException) { /* peer closed */ }
        finally { _cts.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        _sendQueue.Complete();
        if (_sendLoop is not null)
        {
            try { await _sendLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { _cts.Cancel(); }
            catch { /* peer gone */ }
        }
        _cts.Cancel();
        if (_sendLoop is not null) { try { await _sendLoop; } catch { /* peer gone */ } }
        else _sendQueue.Drain();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* already closed */ }
        _socket.Dispose();
        _cts.Dispose();
    }
}
