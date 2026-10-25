using System.Net.Sockets;
using TWorld.Protocol;
using TWorld.Server.Security;

namespace TWorld.Server.Tests;

/// <summary>
/// A minimal plaintext TCP client that speaks the server↔server wire format (no crypto), used to
/// impersonate a map server in end-to-end tests.
/// </summary>
public sealed class TcpTestClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly Stream _stream;

    public TcpTestClient(string host, int port, PeerTlsClientOptions? tls = null)
    {
        _tcp = new TcpClient();
        _tcp.Connect(host, port);
        _stream = tls is null ? _tcp.GetStream()
            : PeerTls.ConnectAsync(_tcp.Client, tls, default).GetAwaiter().GetResult();
    }

    public void Send(PacketWriter w)
    {
        byte[] buf = w.ToArray();
        _stream.Write(buf, 0, buf.Length);
        _stream.Flush();
    }

    /// <summary>Reads the next framed packet (by wSize) and returns a reader over it.</summary>
    public PacketReader Receive(TimeSpan timeout)
    {
        _tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        byte[] header = ReadExactly(PacketHeader.Size);
        int size = PacketHeader.ReadSize(header);
        var packet = new byte[size];
        Array.Copy(header, packet, PacketHeader.Size);
        if (size > PacketHeader.Size)
            Array.Copy(ReadExactly(size - PacketHeader.Size), 0, packet, PacketHeader.Size, size - PacketHeader.Size);
        return new PacketReader(packet);
    }

    private byte[] ReadExactly(int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _stream.Read(buf, read, count - read);
            if (n == 0) throw new IOException("Connection closed by server.");
            read += n;
        }
        return buf;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}
