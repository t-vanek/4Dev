using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TControl.Protocol;
using TControl.Server.Control;
using TControl.Server.Ops;
using Xunit;

namespace TControl.Server.Tests;

public class ProcessAndMonitorTests
{
    internal static string ChildDll([CallerFilePath] string source = "")
    {
        string configuration = typeof(ProcessAndMonitorTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        return Path.GetFullPath($"../TControl.TestProcess/bin/{configuration}/net10.0/TControl.TestProcess.dll", Path.GetDirectoryName(source)!);
    }

    internal static ServiceInstance Child(TestDirectory directory)
    {
        string host = Path.GetFullPath(OperatingSystem.IsWindows() ? "../../../dotnet.exe" : "../../../dotnet", RuntimeEnvironment.GetRuntimeDirectory());
        return new ServiceInstance
        {
            Id = Proto.MakeSvrId(1, Proto.SVRGRP_MAPSVR, 1), Name = "test-child",
            Process = new LocalProcessOptions
            {
                FileName = host, StopTimeoutSeconds = 5,
                Arguments = { ChildDll(), "--Marker", Path.Combine(directory.Path, "child marker.txt") },
            },
        };
    }

    [Fact]
    public async Task StartsConfiguredChild_RejectsDuplicate_AndFlushesOnGracefulStop()
    {
        using var directory = new TestDirectory();
        await using var controller = new LocalProcessController(new TestEnvironment(directory.Path), NullLogger<LocalProcessController>.Instance);
        var service = Child(directory);
        try
        {
            Assert.True(controller.Start(service));
            Assert.False(controller.Start(service));
            await WaitFor(() => File.Exists(Path.Combine(directory.Path, "child marker.txt")));
            Assert.Equal(Proto.SvcRunning, controller.GetStatus(service));
            Assert.True(await controller.StopAsync(service));
            Assert.Equal(Proto.SvcStopped, controller.GetStatus(service));
            Assert.Equal("started|stopped", File.ReadAllText(Path.Combine(directory.Path, "child marker.txt")));
        }
        finally { await controller.StopAsync(service); }
    }

    [Fact]
    public async Task DisposingController_DetachesAndReplacementRecoversSameChild()
    {
        using var directory = new TestDirectory();
        var controller = new LocalProcessController(new TestEnvironment(directory.Path), NullLogger<LocalProcessController>.Instance);
        var service = Child(directory);
        try
        {
            Assert.True(controller.Start(service));
            await WaitFor(() => File.Exists(Path.Combine(directory.Path, "child marker.txt")));
            await controller.DisposeAsync();
            Assert.Equal("started", File.ReadAllText(Path.Combine(directory.Path, "child marker.txt")));
            await using var replacement = new LocalProcessController(new TestEnvironment(directory.Path), NullLogger<LocalProcessController>.Instance);
            Assert.Equal(Proto.SvcRunning, replacement.GetStatus(service));
            Assert.False(replacement.Start(service));
            Assert.True(await replacement.StopAsync(service));
            Assert.Equal("started|stopped", File.ReadAllText(Path.Combine(directory.Path, "child marker.txt")));
        }
        finally
        {
            await controller.DisposeAsync();
            await using var cleanup = new LocalProcessController(new TestEnvironment(directory.Path), NullLogger<LocalProcessController>.Instance);
            await cleanup.StopAsync(service);
        }
    }

    [Fact]
    public async Task Tick_AutoStartsManagedService_ButRespectsManualStop()
    {
        await using var host = new ControlHarness();
        var state = host.Service.State;
        var service = new ServiceInstance { Id = 1, ManagerControl = true };
        state.Services.Add(1, service);
        state.AutoStart = true;
        host.Controller.Status = Proto.SvcStopped;
        await host.Service.OnTickAsync();
        Assert.Equal(1, host.Controller.Starts);
        Assert.Equal(Proto.SvcRunning, service.Status);
        using var client = await host.ConnectAsync();
        ControlHarness.Login(client);
        client.Send(new PacketWriter(Msg.CT_SERVICECONTROL_REQ).WriteUInt32(1).WriteByte(0));
        Assert.Equal(1, new PacketReader(client.ReceiveUntil(Msg.CT_SERVICECONTROL_ACK, ControlHarness.Timeout)).ReadByte());
        service.RestartAfterTick = 0;
        await host.Service.OnTickAsync();
        Assert.Equal(1, host.Controller.Starts);
        Assert.Equal(1, host.Controller.Stops);
        Assert.False(service.ManagerControl);
    }

    [Fact]
    public async Task UnexpectedExit_RestartWaitsForConfiguredDelay()
    {
        await using var host = new ControlHarness(options: new ControlServerOptions { RestartDelaySeconds = 1 });
        var service = new ServiceInstance { Id = 1, Status = Proto.SvcRunning, ManagerControl = true };
        host.Service.State.Services.Add(1, service);
        host.Service.State.AutoStart = true;
        host.Controller.Status = Proto.SvcStopped;
        await host.Service.OnTickAsync();
        Assert.Equal(0, host.Controller.Starts);
        Assert.Equal(1u, service.StopCount);
        await Task.Delay(1100);
        await host.Service.OnTickAsync();
        Assert.Equal(1, host.Controller.Starts);
    }

    [Fact]
    public async Task Tick_PublishesActualSamplerValues_WithOriginalWireLayout()
    {
        await using var host = new ControlHarness();
        using var client = await host.ConnectAsync();
        ControlHarness.Login(client);
        await host.Service.OnTickAsync();
        var sample = new PacketReader(client.ReceiveUntil(Msg.CT_PLATFORM_ACK, ControlHarness.Timeout));
        Assert.Equal(0, sample.ReadByte());
        Assert.Equal(12u, sample.ReadUInt32());
        Assert.Equal(34u, sample.ReadUInt32());
        Assert.Equal(5.5f, sample.ReadFloat());
        Assert.Equal(0, sample.Remaining);
    }

    [Fact]
    public void LocalPlatformSampler_ReadsHostMemory_AndDoesNotInventRemoteMetrics()
    {
        var monitor = new LocalPlatformMonitor(Options.Create(new ControlServerOptions { LocalMachineId = 3 }), NullLogger<LocalPlatformMonitor>.Instance);
        var sample = monitor.Sample(3);
        Assert.NotNull(sample);
        Assert.InRange(sample.Value.Cpu, 0u, 100u);
        Assert.InRange(sample.Value.Mem, 1u, 100u);
        Assert.True(float.IsFinite(sample.Value.Net));
        Assert.Null(monitor.Sample(4));
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
