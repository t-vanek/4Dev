using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TControl.Protocol;
using TControl.Server.Ops;
using Xunit;

namespace TControl.Server.Tests;

public class ProcessPersistenceTests
{
    private static LocalProcessController Controller(TestDirectory directory)
        => new(new TestEnvironment(directory.Path), NullLogger<LocalProcessController>.Instance);

    [Fact]
    public async Task ManuallyStoppedService_RemainsStoppedAfterSupervisorRestart()
    {
        using var directory = new TestDirectory();
        var service = ProcessAndMonitorTests.Child(directory);
        await using (var first = Controller(directory))
        {
            try
            {
                Assert.True(first.Start(service));
                await ProcessAndMonitorTests.WaitFor(() => File.Exists(Path.Combine(directory.Path, "child marker.txt")));
                Assert.True(await first.StopAsync(service));
            }
            finally { await first.StopAsync(service); }
        }
        await using var replacement = Controller(directory);
        Assert.False(replacement.GetRestartIntent(service));
        using var worker = new ControlWorker(Options.Create(new ControlServerOptions
        {
            Port = ProcessOperationTests.FreePort(), AutoStart = true,
            Servers = { new ConfiguredServer { Port = ProcessOperationTests.FreePort(), Group = 1, Type = 4, ServerId = 1, Process = service.Process } },
        }), NullLoggerFactory.Instance, replacement, new TestPlatformMonitor(), new TestFileDeploy());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(1200);
            Assert.Equal(Proto.SvcStopped, replacement.GetStatus(service));
            Assert.Equal("started|stopped", File.ReadAllText(Path.Combine(directory.Path, "child marker.txt")));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("Token")] [InlineData("StartedUtc")]
    public async Task InvalidRecordedIdentity_CannotStopOrForceKillRunningChild(string field)
    {
        using var directory = new TestDirectory();
        var service = ProcessAndMonitorTests.Child(directory);
        service.Process!.ForceKillOnTimeout = true;
        service.Process.StopTimeoutSeconds = 1;
        var first = Controller(directory);
        string? original = null, file = null;
        try
        {
            Assert.True(first.Start(service));
            await ProcessAndMonitorTests.WaitFor(() => File.Exists(Path.Combine(directory.Path, "child marker.txt")));
            file = Directory.GetFiles(Path.Combine(directory.Path, "process-state"), "*.json").Single();
            original = File.ReadAllText(file);
            var record = JsonNode.Parse(original)!;
            using var process = Process.GetProcessById(record["ProcessId"]!.GetValue<int>());
            await first.DisposeAsync();
            if (field == "Token") record[field] = new string('0', 64);
            else record[field] = record[field]!.GetValue<long>() + 1;
            File.WriteAllText(file, record.ToJsonString());
            await using (var other = Controller(directory))
            {
                Assert.False(await other.StopAsync(service));
                Assert.False(process.HasExited);
                Assert.Equal("started", File.ReadAllText(Path.Combine(directory.Path, "child marker.txt")));
            }
        }
        finally
        {
            await first.DisposeAsync();
            if (original is not null) File.WriteAllText(file!, original);
            await using var cleanup = Controller(directory);
            await cleanup.StopAsync(service);
        }
    }

    [Fact]
    public async Task ProcessStateDirectory_HasOnlyOneSupervisorOwner()
    {
        using var directory = new TestDirectory();
        await using var first = Controller(directory);
        Assert.Throws<IOException>(() => Controller(directory));
    }

    [Fact]
    public async Task SupervisorRestartDuringAcceptedStop_PreservesStopIntent()
    {
        using var directory = new TestDirectory();
        var service = ProcessAndMonitorTests.Child(directory);
        service.Process!.Arguments.AddRange(new[] { "--ShutdownDelay", "1500" });
        var first = Controller(directory);
        try
        {
            Assert.True(first.Start(service));
            string marker = Path.Combine(directory.Path, "child marker.txt");
            await ProcessAndMonitorTests.WaitFor(() => File.Exists(marker));
            using var cancellation = new CancellationTokenSource();
            var stop = first.StopAsync(service, cancellation.Token);
            await ProcessAndMonitorTests.WaitFor(() => File.ReadAllText(marker).Contains("|stopping", StringComparison.Ordinal));
            cancellation.Cancel();
            Assert.False(await stop);
            Assert.False(first.GetRestartIntent(service));
            await first.DisposeAsync();
            await ProcessAndMonitorTests.WaitFor(() => File.ReadAllText(marker).EndsWith("|stopped", StringComparison.Ordinal));
            await using var replacement = Controller(directory);
            Assert.False(replacement.GetRestartIntent(service));
            Assert.Equal(Proto.SvcStopped, replacement.GetStatus(service));
        }
        finally
        {
            await first.DisposeAsync();
            await using var cleanup = Controller(directory);
            await cleanup.StopAsync(service);
        }
    }

    [Fact]
    public async Task AuthenticatedProbeDisconnect_DoesNotStopTheGame()
    {
        using var directory = new TestDirectory();
        var service = ProcessAndMonitorTests.Child(directory);
        await using var controller = Controller(directory);
        try
        {
            Assert.True(controller.Start(service));
            string marker = Path.Combine(directory.Path, "child marker.txt");
            await ProcessAndMonitorTests.WaitFor(() => File.Exists(marker));
            string file = Directory.GetFiles(Path.Combine(directory.Path, "process-state"), "*.json").Single();
            var record = JsonNode.Parse(File.ReadAllText(file))!;
            using (var pipe = new NamedPipeClientStream(".", record["PipeName"]!.GetValue<string>(), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                using var timeout = new CancellationTokenSource(ControlHarness.Timeout);
                await pipe.ConnectAsync(timeout.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("AUTH " + record["Token"]!.GetValue<string>());
                Assert.StartsWith("READY ", await reader.ReadLineAsync(timeout.Token));
            }
            Assert.Equal(Proto.SvcRunning, controller.GetStatus(service));
            Assert.Equal("started", File.ReadAllText(marker));
            Assert.True(await controller.StopAsync(service));
        }
        finally { await controller.StopAsync(service); }
    }
}
