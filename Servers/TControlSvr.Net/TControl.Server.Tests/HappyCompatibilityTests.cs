using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TControl.Protocol;
using TControl.Server.Control;
using TControl.Server.Net;
using Xunit;

namespace TControl.Server.Tests;

public class HappyCompatibilityTests
{
    [Fact]
    public async Task ReadOnlyLoginIncludesTopologyWithoutGrantingMutationRights()
    {
        await using var host = new ControlHarness();
        host.Service.State.Groups.Add(1, new TGroup { GroupId = 1, Name = "World" });
        using var client = await host.ConnectAsync();
        Assert.Equal(0, ControlHarness.Login(client, readOnly: true).ReadByte());
        var groups = new PacketReader(client.ReceiveUntil(Msg.CT_GROUPLIST_ACK, ControlHarness.Timeout));
        Assert.Equal(1u, groups.ReadUInt32()); Assert.Equal(1, groups.ReadByte()); Assert.Equal("World", groups.ReadString());
        Assert.Equal(Msg.CT_MACHINELIST_ACK, PacketHeader.ReadId(client.Receive(ControlHarness.Timeout)));
        Assert.Equal(Msg.CT_SVRTYPELIST_ACK, PacketHeader.ReadId(client.Receive(ControlHarness.Timeout)));
        Assert.Equal(Msg.CT_SERVICEAUTOSTART_ACK, PacketHeader.ReadId(client.Receive(ControlHarness.Timeout)));
        client.Send(new PacketWriter(Msg.CT_SERVICEAUTOSTART_REQ).WriteByte(1));
        Assert.Equal(Msg.CT_AUTHORITY_ACK, PacketHeader.ReadId(client.Receive(ControlHarness.Timeout)));
        Assert.False(host.Service.State.AutoStart);
    }

    [Fact]
    public async Task RelayFreeDeploymentDeliversAnnouncementsAndCharacterMessages()
    {
        await using var host = new ControlHarness();
        await using var map1 = await Peer.Connect(host, 1, Proto.SVRGRP_MAPSVR);
        await using var map2 = await Peer.Connect(host, 2, Proto.SVRGRP_MAPSVR);
        await using var world = await Peer.Connect(host, 1, Proto.SVRGRP_WORLDSVR);
        using var client = await host.ConnectAsync(); ControlHarness.Login(client);
        client.Send(new PacketWriter(Msg.CT_ANNOUNCEMENT_REQ).WriteUInt32(0).WriteString("All groups"));
        Assert.Equal("All groups", map1.Read(Msg.CT_ANNOUNCEMENT_ACK).ReadString());
        Assert.Equal("All groups", map2.Read(Msg.CT_ANNOUNCEMENT_ACK).ReadString());
        client.Send(new PacketWriter(Msg.CT_CHARMSG_REQ).WriteString("Player").WriteString("Hello"));
        var message = world.Read(Msg.CT_CHARMSG_ACK);
        Assert.Equal("Player", message.ReadString()); Assert.Equal("Hello", message.ReadString());
    }

    [Fact]
    public async Task ConnectedRelaySuppressesFallbackOnlyForItsOwnGroup()
    {
        await using var host = new ControlHarness();
        await using var relay = await Peer.Connect(host, 1, Proto.SVRGRP_RLYSVR);
        await using var map1 = await Peer.Connect(host, 1, Proto.SVRGRP_MAPSVR);
        await using var map2 = await Peer.Connect(host, 2, Proto.SVRGRP_MAPSVR);
        await using var world1 = await Peer.Connect(host, 1, Proto.SVRGRP_WORLDSVR);
        await using var world2 = await Peer.Connect(host, 2, Proto.SVRGRP_WORLDSVR);
        using var client = await host.ConnectAsync(); ControlHarness.Login(client);
        client.Send(new PacketWriter(Msg.CT_ANNOUNCEMENT_REQ).WriteUInt32(0).WriteString("Notice"));
        Assert.Equal("Notice", relay.Read(Msg.CT_ANNOUNCEMENT_ACK).ReadString());
        Assert.Equal("Notice", map2.Read(Msg.CT_ANNOUNCEMENT_ACK).ReadString());
        Assert.Throws<IOException>(() => map1.Client.Receive(TimeSpan.FromMilliseconds(150)));
        client.Send(new PacketWriter(Msg.CT_CHARMSG_REQ).WriteString("Player").WriteString("Hello"));
        Assert.Equal("Player", relay.Read(Msg.CT_CHARMSG_ACK).ReadString());
        Assert.Equal("Player", world2.Read(Msg.CT_CHARMSG_ACK).ReadString());
        Assert.Throws<IOException>(() => world1.Client.Receive(TimeSpan.FromMilliseconds(150)));
        client.Send(new PacketWriter(Msg.CT_ANNOUNCEMENT_REQ).WriteUInt32(2).WriteString("Only group 2"));
        Assert.Equal("Only group 2", map2.Read(Msg.CT_ANNOUNCEMENT_ACK).ReadString());
        Assert.Throws<IOException>(() => relay.Client.Receive(TimeSpan.FromMilliseconds(150)));
    }

    private sealed class Peer(WireTestClient client, PacketConnection connection, Task loop) : IAsyncDisposable
    {
        public WireTestClient Client => client;
        public PacketReader Read(ushort id) => new(client.ReceiveUntil(id, ControlHarness.Timeout));
        public static async Task<Peer> Connect(ControlHarness host, byte group, byte type)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var client = new WireTestClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
                var connection = new PacketConnection(await listener.AcceptSocketAsync(), NullLogger.Instance);
                var service = new ServiceInstance { Id = Proto.MakeSvrId(group, type, 1), ServerId = 1,
                    Group = new TGroup { GroupId = group }, SvrType = new TSvrType { Type = type } };
                host.Service.State.Services.Add(service.Id, service);
                host.Service.OnServerConnected(connection, service);
                var peer = new Peer(client, connection, connection.RunAsync((_, _) => ValueTask.CompletedTask, CancellationToken.None));
                peer.Read(Msg.CT_CTRLSVR_REQ);
                return peer;
            }
            finally { listener.Stop(); }
        }
        public async ValueTask DisposeAsync() { connection.Close(); client.Dispose(); await loop.WaitAsync(ControlHarness.Timeout); }
    }
}
