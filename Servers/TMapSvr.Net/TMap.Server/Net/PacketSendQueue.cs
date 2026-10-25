using System.Buffers;
using System.Net.Sockets;
using System.Threading.Channels;
using TMap.Protocol;

namespace TMap.Server.Net;

/// <summary>Copies at enqueue, pools the copies, and coalesces ready packets without delaying a lone packet.
/// Encryption stays per packet and per connection. A slow peer may retain at most 4 MiB plus one batch.</summary>
internal sealed class PacketSendQueue
{
    private const int BatchBytes = 64 * 1024, BatchPackets = 128, MaxQueuedBytes = 4 * 1024 * 1024;
    private readonly Channel<Packet> _queue = Channel.CreateUnbounded<Packet>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private int _queuedBytes;
    private long _sendCalls, _packets;
    public long SendCalls => Interlocked.Read(ref _sendCalls);
    public long SentPackets => Interlocked.Read(ref _packets);

    public bool TryEnqueue(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < PacketHeader.Size or > PacketHeader.MaxPacketSize)
            throw new InvalidDataException("Invalid outbound packet length.");
        if (Interlocked.Add(ref _queuedBytes, bytes.Length) > MaxQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -bytes.Length);
            return false;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(bytes.Length);
        bytes.CopyTo(buffer);
        var packet = new Packet(buffer, bytes.Length);
        if (_queue.Writer.TryWrite(packet)) return true;
        Return(packet);
        return false;
    }

    public void Complete() => _queue.Writer.TryComplete();

    public void Drain()
    {
        while (_queue.Reader.TryRead(out var packet)) Return(packet);
    }

    private void Return(Packet packet)
    {
        ArrayPool<byte>.Shared.Return(packet.Buffer, clearArray: true);
        Interlocked.Add(ref _queuedBytes, -packet.Length);
    }

    public async Task RunAsync(Socket socket, SessionCipher? cipher, CancellationToken token, Stream? stream = null)
    {
        var batch = ArrayPool<byte>.Shared.Rent(BatchBytes);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(token))
            {
                int length = 0, count = 0;
                while (count < BatchPackets && _queue.Reader.TryPeek(out var next)
                    && length + next.Length <= BatchBytes && _queue.Reader.TryRead(out var packet))
                {
                    try
                    {
                        cipher?.EncryptOutbound(packet.Buffer, packet.Length);
                        packet.Buffer.AsSpan(0, packet.Length).CopyTo(batch.AsSpan(length));
                        length += packet.Length;
                        count++;
                    }
                    finally { Return(packet); }
                }
                int sent = 0;
                if (stream is not null)
                {
                    await stream.WriteAsync(batch.AsMemory(0, length), token);
                    Interlocked.Increment(ref _sendCalls);
                    sent = length;
                }
                while (sent < length)
                {
                    int n = await socket.SendAsync(batch.AsMemory(sent, length - sent), SocketFlags.None, token);
                    Interlocked.Increment(ref _sendCalls);
                    if (n == 0) throw new IOException("Peer closed during send.");
                    sent += n;
                }
                Interlocked.Add(ref _packets, count);
            }
        }
        finally
        {
            Complete();
            Drain();
            ArrayPool<byte>.Shared.Return(batch, clearArray: true);
        }
    }

    private readonly record struct Packet(byte[] Buffer, int Length);
}
