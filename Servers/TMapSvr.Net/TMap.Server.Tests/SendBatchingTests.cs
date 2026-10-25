using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Protocol;
using TMap.Protocol.Crypto;
using TMap.Server.Net;
using Xunit;

namespace TMap.Server.Tests;

public class SendBatchingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadyPackets_AreCoalesced_InOrder_WithIndependentEncryptionAndEnqueueSnapshots(bool encrypted)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        await using var connection = new ClientConnection(await listener.AcceptSocketAsync(), NullLogger.Instance) { UseCrypt = encrypted };
        const int count = 200, packetSize = PacketHeader.Size + 4;
        for (uint i = 0; i < count; i++)
        {
            byte[] p = new PacketWriter(Msg.CS_CHARSTATINFO_ACK).WriteUInt32(i).ToArray();
            connection.Send(p); Array.Fill(p, (byte)0); // caller mutation after Send must not change the queued snapshot
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = connection.RunAsync((_, _) => ValueTask.CompletedTask, timeout.Token);
        var bytes = new byte[count * packetSize];
        await peer.GetStream().ReadExactlyAsync(bytes, timeout.Token);
        for (uint i = 0; i < count; i++)
        {
            var packet = bytes.AsSpan((int)i * packetSize, packetSize).ToArray();
            if (encrypted)
            {
                long key = ProtocolKeys.XorKeys[(i + 1) % ProtocolKeys.KeyCount];
                PacketCrypto.DecryptHeader(packet, key);
                Assert.True(PacketCrypto.DecryptBody(packet, packet.Length, key));
                Assert.Equal(i + 1, PacketHeader.ReadNumber(packet));
            }
            Assert.Equal(i, new PacketReader(packet).ReadUInt32());
        }
        await connection.DisposeAsync(); await running;
        Assert.Equal(count, connection.SentPackets);
        Assert.InRange(connection.SendCalls, 2, 4); // 128-packet bound; formerly 200 socket sends
    }

    [Fact]
    public async Task Close_DrainsTheLastReply_EvenWhenItWasQueuedBeforeRun()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        await using var connection = new ClientConnection(await listener.AcceptSocketAsync(), NullLogger.Instance) { UseCrypt = false };
        byte[] reply = new PacketWriter(Msg.CS_CHARSTATINFO_ACK).WriteUInt32(42).ToArray(); connection.Send(reply);
        var running = connection.RunAsync((_, _) => ValueTask.CompletedTask, CancellationToken.None);
        await connection.DisposeAsync();
        byte[] got = new byte[reply.Length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await peer.GetStream().ReadExactlyAsync(got, timeout.Token);
        Assert.Equal(reply, got); await running;
    }
}
