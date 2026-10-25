using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TLogin.Protocol;

namespace TLogin.Server.Net;

/// <summary>
/// One client socket: a receive loop that frames + decrypts inbound packets, and a serialized send
/// loop that encrypts + writes outbound packets. Replaces the C++ IOCP <c>CSession</c> with
/// async/await. Per-connection state (the ported <c>CTUser</c> fields) hangs off <see cref="State"/>.
/// </summary>
public sealed class PacketConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly SessionCipher _cipher = new();
    private readonly PacketFramer _framer = new();
    private readonly Channel<byte[]> _sendQueue =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    private Func<PacketConnection, PacketReader, ValueTask>? _onPacket;
    private Task? _sendLoop;
    private int _closed;

    public PacketConnection(Socket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
        RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
    }

    public IPEndPoint? RemoteEndPoint { get; }
    public CancellationToken StoppingToken { get; private set; }

    /// <summary>Arbitrary per-connection state (a <c>LoginSessionState</c>).</summary>
    public object? State { get; set; }

    public bool UseCrypt
    {
        get => _cipher.UseCrypt;
        set => _cipher.UseCrypt = value;
    }

    public void Send(byte[] packet) => _sendQueue.Writer.TryWrite(packet);
    public void Send(PacketWriter writer) => Send(writer.ToArray());

    public async Task RunAsync(Func<PacketConnection, PacketReader, ValueTask> onPacket, CancellationToken outerToken)
    {
        _onPacket = onPacket;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, outerToken);
        StoppingToken = linked.Token;
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
                        await _onPacket(this, new PacketReader(packet));
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
                _cipher.EncryptOutbound(packet, packet.Length);
                int sent = 0;
                while (sent < packet.Length)
                    sent += await _socket.SendAsync(packet.AsMemory(sent), SocketFlags.None, token);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (SocketException) { /* peer gone */ }
        catch (ObjectDisposedException) { /* socket disposed */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        _sendQueue.Writer.TryComplete();
        _cts.Cancel();
        if (_sendLoop is not null)
        {
            try { await _sendLoop; } catch { /* ignore */ }
        }

        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* already closed */ }
        _socket.Dispose();
        _cts.Dispose();
    }
}
