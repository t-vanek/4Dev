using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TControl.Protocol;
using TControl.Server.Control;
using TControl.Server.Ops;
using Xunit;

namespace TControl.Server.Tests;

internal sealed class GatedServiceController : IServiceController
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public uint Status = Proto.SvcRunning;
    public Dictionary<uint, uint> StatusById { get; } = new();
    public int Starts;
    public bool Start(ServiceInstance service) { Starts++; StatusById[service.Id] = Proto.SvcRunning; return true; }
    public uint? GetStatus(ServiceInstance service) => StatusById.GetValueOrDefault(service.Id, Status);
    public async Task<bool> StopAsync(ServiceInstance service, CancellationToken token = default)
    {
        Entered.TrySetResult();
        bool result = await Finish.Task.WaitAsync(token);
        if (result) Status = Proto.SvcStopped;
        return result;
    }
}

public class ProcessOperationTests
{
    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<WireTestClient> Connect(int port)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new WireTestClient("127.0.0.1", port); }
            catch (SocketException) when (attempt < 100) { await Task.Delay(20); }
        }
    }

    private sealed class CountingMonitor : IPlatformMonitor
    {
        public int Samples;
        public PlatformSample? Sample(byte machineId) { Interlocked.Increment(ref Samples); return new(1, 2, 3); }
    }

    [Fact]
    public async Task SlowStop_DoesNotBlockAnotherManagerOrPeriodicMonitoring()
    {
        int port = FreePort();
        var controller = new GatedServiceController();
        var monitor = new CountingMonitor();
        using var worker = new ControlWorker(Options.Create(new ControlServerOptions
        {
            Port = port, ConnectIntervalSeconds = 1,
            Servers = { new ConfiguredServer { Host = "127.0.0.1", Port = FreePort(), Group = 1, Type = 3, ServerId = 1 } },
        }), NullLoggerFactory.Instance, controller, monitor, new TestFileDeploy(), new TestAuthenticator());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var first = await Connect(port);
            using var second = await Connect(port);
            Assert.Equal(0, ControlHarness.Login(first).ReadByte());
            Assert.Equal(0, ControlHarness.Login(second).ReadByte());
            first.ReceiveUntil(Msg.CT_SERVICEAUTOSTART_ACK, ControlHarness.Timeout);
            second.ReceiveUntil(Msg.CT_SERVICEAUTOSTART_ACK, ControlHarness.Timeout);
            first.Send(new PacketWriter(Msg.CT_SERVICECONTROL_REQ).WriteUInt32(Proto.MakeSvrId(1, 3, 1)).WriteByte(0));
            await controller.Entered.Task.WaitAsync(ControlHarness.Timeout);
            second.Send(new PacketWriter(Msg.CT_SERVICESTAT_REQ));
            second.ReceiveUntil(Msg.CT_SERVICESTAT_ACK, ControlHarness.Timeout);
            Assert.False(controller.Finish.Task.IsCompleted);
            int samples = Volatile.Read(ref monitor.Samples);
            await Task.Delay(1200);
            Assert.True(Volatile.Read(ref monitor.Samples) > samples);
            second.Send(new PacketWriter(Msg.CT_SERVICECONTROL_REQ).WriteUInt32(Proto.MakeSvrId(1, 3, 1)).WriteByte(0));
            Assert.Equal(0, new PacketReader(second.ReceiveUntil(Msg.CT_SERVICECONTROL_ACK, ControlHarness.Timeout)).ReadByte());
            controller.Finish.TrySetResult(true);
            Assert.Equal(1, new PacketReader(first.ReceiveUntil(Msg.CT_SERVICECONTROL_ACK, ControlHarness.Timeout)).ReadByte());
        }
        finally
        {
            controller.Finish.TrySetResult(true);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WorldStop_CommitsGroupRestartSuppressionOnlyOnSuccess(bool success)
    {
        var controller = new GatedServiceController();
        await using var host = new ControlHarness(controller: controller);
        host.Service.State.AutoStart = true;
        var world = new ServiceInstance { Id = 1, Status = Proto.SvcRunning, ManagerControl = true,
            Group = new TGroup { GroupId = 1 }, SvrType = new TSvrType { Type = Proto.SVRGRP_WORLDSVR } };
        var map = new ServiceInstance { Id = 2, Status = Proto.SvcStopped, ManagerControl = true, Group = new TGroup { GroupId = 1 } };
        controller.StatusById[2] = Proto.SvcStopped;
        host.Service.State.Services.Add(1, world);
        host.Service.State.Services.Add(2, map);
        using var manager = await host.ConnectAsync();
        ControlHarness.Login(manager);
        manager.Send(new PacketWriter(Msg.CT_SERVICECONTROL_REQ).WriteUInt32(1).WriteByte(0));
        await controller.Entered.Task.WaitAsync(ControlHarness.Timeout);
        Assert.True(world.ManagerControl);
        Assert.True(map.ManagerControl);
        await host.Service.OnTickAsync();
        Assert.Equal(0, controller.Starts);
        controller.Finish.SetResult(success);
        Assert.Equal(success ? 1 : 0, new PacketReader(manager.ReceiveUntil(Msg.CT_SERVICECONTROL_ACK, ControlHarness.Timeout)).ReadByte());
        Assert.Equal(!success, world.ManagerControl);
        Assert.Equal(!success, map.ManagerControl);
        Assert.Equal(success ? Proto.SvcStopped : Proto.SvcRunning, world.Status);
        await host.Service.OnTickAsync();
        Assert.Equal(success ? 0 : 1, controller.Starts);
    }

    [Fact]
    public async Task StartOfAlreadyRunningService_ReenablesItsRestartIntent()
    {
        await using var host = new ControlHarness();
        host.Controller.Status = Proto.SvcRunning;
        var service = new ServiceInstance { Id = 1, Status = Proto.SvcRunning, ManagerControl = false };
        host.Service.State.Services.Add(1, service);
        using var manager = await host.ConnectAsync();
        ControlHarness.Login(manager);
        manager.Send(new PacketWriter(Msg.CT_SERVICECONTROL_REQ).WriteUInt32(1).WriteByte(1));
        Assert.Equal(1, new PacketReader(manager.ReceiveUntil(Msg.CT_SERVICECONTROL_ACK, ControlHarness.Timeout)).ReadByte());
        Assert.True(service.ManagerControl);
        Assert.Equal(0, host.Controller.Starts);
    }
}
