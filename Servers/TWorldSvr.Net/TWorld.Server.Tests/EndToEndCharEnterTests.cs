using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using TWorld.Protocol;
using TWorld.Server.Net;
using TWorld.Server.World;
using Xunit;

namespace TWorld.Server.Tests;

/// <summary>
/// End-to-end test: boots the real <see cref="PacketServer"/> + <see cref="WorldService"/> + the single
/// batch task, then drives the full map-server-connect + character-enter + chat flow as a simulated map
/// server. No database is required (the core flow is in-memory coordination).
/// </summary>
public class EndToEndCharEnterTests
{
    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task MapConnect_CharEnter_Chat_Flow()
    {
        int port = FreeTcpPort();
        var state = new WorldState();
        var service = new WorldService(state, null, NullLogger<WorldService>.Instance, authorizePeer: (_, _) => true);
        var server = new PacketServer(NullLogger<PacketServer>.Instance, new TestPeerAuthenticator());
        var batch = Channel.CreateUnbounded<(PacketConnection Conn, byte[]? Packet)>(new UnboundedChannelOptions { SingleReader = true });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var batchTask = Task.Run(async () =>
        {
            await foreach (var item in batch.Reader.ReadAllAsync(cts.Token))
            {
                if (item.Packet is null) service.OnDisconnect(item.Conn);
                else await service.DispatchAsync(item.Conn, item.Packet);
            }
        });

        var listen = server.ListenAsync(
            port,
            onPacket: (conn, data) => { batch.Writer.TryWrite((conn, data)); return ValueTask.CompletedTask; },
            onConnected: service.OnConnectedAsync,
            onDisconnected: conn => { batch.Writer.TryWrite((conn, null)); return ValueTask.CompletedTask; },
            token: cts.Token);

        using var client = await ConnectWithRetryAsync("127.0.0.1", port, cts.Token);

        const uint charId = 1001;
        const uint key = 0xABCD1234;
        const byte serverId = 1, serverType = 4;
        ushort wId = (ushort)((serverType << 8) | serverId);

        // 1) Map server registers (MW_CONNECT_ACK: wServerID, bCount, channels)
        var connect = new PacketWriter(Msg.MW_CONNECT_ACK);
        connect.WriteUInt16(wId);
        connect.WriteByte(1);   // one channel
        connect.WriteByte(0);   // channel 0
        client.Send(connect);

        // 2) Character login start (MW_ADDCHAR_ACK: charId, key, ip, port, userId)
        var addChar = new PacketWriter(Msg.MW_ADDCHAR_ACK);
        addChar.WriteUInt32(charId);
        addChar.WriteUInt32(key);
        addChar.WriteUInt32(0x0100007F); // 127.0.0.1
        addChar.WriteUInt16(5816);
        addChar.WriteUInt32(2007);       // userId
        client.Send(addChar);

        // 3) Expect MW_ENTERSVR_REQ (bDBLoad, charId, key)
        var enterSvr = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_ENTERSVR_REQ, enterSvr.Id);
        Assert.True(enterSvr.ReadBool());                 // bDBLoad
        Assert.Equal(charId, enterSvr.ReadUInt32());
        Assert.Equal(key, enterSvr.ReadUInt32());

        // 4) Map returns char data (MW_CHARDATA_ACK: charId,key,startAct,level,maxHP,HP,maxMP,MP,country,mode, + recall blob)
        var charData = new PacketWriter(Msg.MW_CHARDATA_ACK);
        charData.WriteUInt32(charId);
        charData.WriteUInt32(key);
        charData.WriteByte(0);     // bStartAct
        charData.WriteByte(10);    // bLevel
        charData.WriteUInt32(500); // maxHP
        charData.WriteUInt32(500); // HP
        charData.WriteUInt32(200); // maxMP
        charData.WriteUInt32(200); // MP
        charData.WriteByte(4);     // bCountry
        charData.WriteByte(0);     // bMode
        charData.WriteByte(0);     // bRecallCnt = 0 (trailing blob)
        client.Send(charData);

        // 5) Expect MW_ENTERCHAR_REQ (starts with charId, key)
        var enterChar = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_ENTERCHAR_REQ, enterChar.Id);
        Assert.Equal(charId, enterChar.ReadUInt32());
        Assert.Equal(key, enterChar.ReadUInt32());

        // 6) Confirm entry (MW_ENTERCHAR_ACK: charId, key)
        var enterAck = new PacketWriter(Msg.MW_ENTERCHAR_ACK);
        enterAck.WriteUInt32(charId);
        enterAck.WriteUInt32(key);
        client.Send(enterAck);

        // 6b) World confirms the main server (CHECKMAIN_REQ); ack it so the connection is granted.
        var checkMain = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_CHECKMAIN_REQ, checkMain.Id);
        var checkAck = new PacketWriter(Msg.MW_CHECKMAIN_ACK);
        checkAck.WriteUInt32(charId); checkAck.WriteUInt32(key);
        client.Send(checkAck);
        Assert.Equal(Msg.MW_CONRESULT_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);

        // 7) Chat is relayed back to connected servers (this one included)
        var chat = new PacketWriter(Msg.MW_CHAT_ACK);
        chat.WriteByte(0);          // bChannel
        chat.WriteUInt32(charId);   // dwSender
        chat.WriteUInt32(key);      // dwSenderKEY
        chat.WriteString("Hero");   // strSenderName
        chat.WriteByte(0);          // bType
        chat.WriteByte(0);          // bGroup (all)
        chat.WriteUInt32(0);        // dwTarget
        chat.WriteString("");       // strName
        chat.WriteString("hello world"); // strTalk
        client.Send(chat);

        var echo = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_CHAT_ACK, echo.Id);

        cts.Cancel();
        try { await listen; } catch (OperationCanceledException) { }
    }

    /// <summary>The real C++ map handshake: after ENTERSVR_REQ the map replies ROUTE_ACK (not CHARDATA_ACK);
    /// the world must answer CHARDATA_REQ. Regression guard for the "stuck after ENTERSVR_REQ" bug.</summary>
    [Fact]
    public async Task RouteAck_DrivesCharDataReq_ThenEnter()
    {
        const uint charId = 2, key = 0xCAFE, userId = 1;
        const byte serverId = 1, serverType = 4;
        ushort wId = (ushort)((serverType << 8) | serverId);

        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();

        var connect = new PacketWriter(Msg.MW_CONNECT_ACK);
        connect.WriteUInt16(wId); connect.WriteByte(1); connect.WriteByte(0);
        client.Send(connect);

        var addChar = new PacketWriter(Msg.MW_ADDCHAR_ACK);
        addChar.WriteUInt32(charId); addChar.WriteUInt32(key); addChar.WriteUInt32(0x0100007F); addChar.WriteUInt16(5816); addChar.WriteUInt32(userId);
        client.Send(addChar);
        Assert.Equal(Msg.MW_ENTERSVR_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);

        // Map replies ROUTE_ACK with no extra connections -> world must send CHARDATA_REQ.
        var route = new PacketWriter(Msg.MW_ROUTE_ACK);
        route.WriteUInt32(charId); route.WriteUInt32(key); route.WriteByte(0);
        client.Send(route);

        var dataReq = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_CHARDATA_REQ, dataReq.Id);
        Assert.Equal(charId, dataReq.ReadUInt32());
        Assert.Equal(key, dataReq.ReadUInt32());

        var charData = new PacketWriter(Msg.MW_CHARDATA_ACK);
        charData.WriteUInt32(charId); charData.WriteUInt32(key); charData.WriteByte(0); charData.WriteByte(10);
        charData.WriteUInt32(500); charData.WriteUInt32(500); charData.WriteUInt32(200); charData.WriteUInt32(200);
        charData.WriteByte(4); charData.WriteByte(0); charData.WriteByte(0);
        client.Send(charData);

        var enterChar = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_ENTERCHAR_REQ, enterChar.Id);
        Assert.Equal(charId, enterChar.ReadUInt32());

        var enterAck = new PacketWriter(Msg.MW_ENTERCHAR_ACK);
        enterAck.WriteUInt32(charId); enterAck.WriteUInt32(key);
        client.Send(enterAck);

        await Task.Delay(80);
        Assert.True(host.State.Characters.ContainsKey(charId));
    }

    /// <summary>The actual live map handshake (verified against a real C++ TMapSvr): after ENTERSVR_REQ
    /// the map replies <c>MW_ENTERSVR_ACK</c> with the full 75-byte character record; the world must then
    /// send CHARINFO_REQ + ROUTE_REQ. Regression guard for the "Unhandled message 0x9009 → stuck after
    /// ENTERSVR_REQ" bug. Mirrors C++ <c>OnMW_ENTERSVR_ACK</c> (SSHandler.cpp:1181).</summary>
    [Fact]
    public async Task EnterSvrAck_DrivesCharInfoAndRoute_ThenEnter()
    {
        const uint charId = 2, key = 0xCAFE, userId = 1;
        const byte serverId = 1, serverType = 4;
        ushort wId = (ushort)((serverType << 8) | serverId);

        await using var host = new WorldTestHost();
        using var client = await host.ConnectAsync();

        var connect = new PacketWriter(Msg.MW_CONNECT_ACK);
        connect.WriteUInt16(wId); connect.WriteByte(1); connect.WriteByte(0);
        client.Send(connect);

        var addChar = new PacketWriter(Msg.MW_ADDCHAR_ACK);
        addChar.WriteUInt32(charId); addChar.WriteUInt32(key); addChar.WriteUInt32(0x0100007F); addChar.WriteUInt16(5816); addChar.WriteUInt32(userId);
        client.Send(addChar);
        Assert.Equal(Msg.MW_ENTERSVR_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);

        // Map loaded the char and returns the full record (same field order as OnMW_ENTERSVR_ACK).
        var ack = new PacketWriter(Msg.MW_ENTERSVR_ACK);
        ack.WriteUInt32(charId); ack.WriteUInt32(key);
        ack.WriteString("Pittt");
        ack.WriteByte(19);   // level
        ack.WriteByte(1);    // realSex
        ack.WriteByte(2);    // class
        ack.WriteByte(0);    // race
        ack.WriteByte(1);    // sex
        ack.WriteByte(3);    // face
        ack.WriteByte(4);    // hair
        ack.WriteByte(0);    // helmetHide
        ack.WriteByte(1);    // country
        ack.WriteByte(0);    // aidCountry
        ack.WriteUInt32(0);  // region
        ack.WriteByte(0);    // channel
        ack.WriteUInt16(0);  // mapId (in-game zone; 0 is valid)
        ack.WriteFloat(100f); ack.WriteFloat(0f); ack.WriteFloat(200f);
        ack.WriteByte(0);    // logout
        ack.WriteByte(0);    // save
        ack.WriteByte(0);    // result (0 = OK)
        ack.WriteUInt16(0);  // titleId
        ack.WriteUInt32(0);  // rankPoint
        ack.WriteUInt32(0x0100007F); // userIP
        client.Send(ack);

        // World must respond with CHARINFO_REQ then ROUTE_REQ.
        Assert.Equal(Msg.MW_CHARINFO_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);
        Assert.Equal(Msg.MW_ROUTE_REQ, client.Receive(TimeSpan.FromSeconds(5)).Id);

        // Routing done with no extra connections -> world asks for char data.
        var route = new PacketWriter(Msg.MW_ROUTE_ACK);
        route.WriteUInt32(charId); route.WriteUInt32(key); route.WriteByte(0);
        client.Send(route);
        var dataReq = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_CHARDATA_REQ, dataReq.Id);

        var charData = new PacketWriter(Msg.MW_CHARDATA_ACK);
        charData.WriteUInt32(charId); charData.WriteUInt32(key); charData.WriteByte(0); charData.WriteByte(19);
        charData.WriteUInt32(500); charData.WriteUInt32(500); charData.WriteUInt32(200); charData.WriteUInt32(200);
        charData.WriteByte(1); charData.WriteByte(0); charData.WriteByte(0);
        client.Send(charData);

        var enterChar = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_ENTERCHAR_REQ, enterChar.Id);

        var enterAck = new PacketWriter(Msg.MW_ENTERCHAR_ACK);
        enterAck.WriteUInt32(charId); enterAck.WriteUInt32(key);
        client.Send(enterAck);

        // World confirms the main server (CHECKMAIN_REQ); map confirms back.
        var checkMain = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_CHECKMAIN_REQ, checkMain.Id);
        Assert.Equal(charId, checkMain.ReadUInt32());

        var checkAck = new PacketWriter(Msg.MW_CHECKMAIN_ACK);
        checkAck.WriteUInt32(charId); checkAck.WriteUInt32(key);
        client.Send(checkAck);

        // The "enter granted" signal: CONRESULT_REQ with CN_SUCCESS (=0) and the connection list.
        var conResult = client.Receive(TimeSpan.FromSeconds(5));
        Assert.Equal(Msg.MW_CONRESULT_REQ, conResult.Id);
        Assert.Equal(charId, conResult.ReadUInt32());
        Assert.Equal(key, conResult.ReadUInt32());
        Assert.Equal(0, conResult.ReadByte());     // CN_SUCCESS
        Assert.Equal(1, conResult.ReadByte());     // one connection
        Assert.Equal(serverId, conResult.ReadByte());

        await Task.Delay(80);
        Assert.True(host.State.Characters.TryGetValue(charId, out var ch));
        Assert.Equal("Pittt", ch!.Name);
        Assert.Equal(19, ch.Level);
    }

    private static async Task<TcpTestClient> ConnectWithRetryAsync(string host, int port, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try { return new TcpTestClient(host, port); }
            catch (SocketException) { await Task.Delay(50, ct); }
        }
        throw new TimeoutException("Server did not start listening.");
    }
}
