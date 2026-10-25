using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using TWorld.Server.Net;
using TWorld.Server.World;
using TWorld.Server.Security;

namespace TWorld.Server.Tests;

/// <summary>
/// Spins up a real <see cref="PacketServer"/> + <see cref="WorldService"/> + batch task on an ephemeral
/// port for end-to-end tests, with an optional hook to seed <see cref="WorldState"/> first. No DB.
/// </summary>
public sealed class WorldTestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
    private readonly Task _listen;
    private readonly Task _batch;

    public WorldState State { get; }
    public WorldService Service { get; }
    public int Port { get; }

    public WorldTestHost(Action<WorldState>? seed = null, IPeerAuthenticator? authenticator = null,
        bool enforceAuthorization = false)
    {
        State = new WorldState();
        seed?.Invoke(State);

        var service = new WorldService(State, null, NullLogger<WorldService>.Instance,
            authorizePeer: enforceAuthorization ? null : (_, _) => true);
        Service = service;
        var server = new PacketServer(NullLogger<PacketServer>.Instance, authenticator ?? new TestPeerAuthenticator());
        var batch = Channel.CreateUnbounded<(PacketConnection Conn, byte[]? Packet)>(new UnboundedChannelOptions { SingleReader = true });

        Port = FreeTcpPort();

        _batch = Task.Run(async () =>
        {
            await foreach (var item in batch.Reader.ReadAllAsync(_cts.Token))
            {
                if (item.Packet is null) service.OnDisconnect(item.Conn);
                else await service.DispatchAsync(item.Conn, item.Packet);
            }
        });

        _listen = server.ListenAsync(
            Port,
            onPacket: (conn, data) => { batch.Writer.TryWrite((conn, data)); return ValueTask.CompletedTask; },
            onConnected: service.OnConnectedAsync,
            onDisconnected: conn => { batch.Writer.TryWrite((conn, null)); return ValueTask.CompletedTask; },
            token: _cts.Token);
    }

    public async Task<TcpTestClient> ConnectAsync(PeerTlsClientOptions? tls = null)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try { return new TcpTestClient("127.0.0.1", Port, tls); }
            catch (SocketException) { await Task.Delay(50, _cts.Token); }
        }
        throw new TimeoutException("Server did not start listening.");
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_listen, _batch); } catch { }
        _cts.Dispose();
    }
}
