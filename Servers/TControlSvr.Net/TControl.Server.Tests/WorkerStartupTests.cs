using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TControl.Data;
using Xunit;

namespace TControl.Server.Tests;

public class WorkerStartupTests
{
    [Fact]
    public async Task MissingPeerTls_DeniesOnlyThatConnector_AndKeepsControlRunning()
    {
        using var worker = new ControlWorker(Options.Create(new ControlServerOptions
        {
            Port = ProcessOperationTests.FreePort(),
            Servers = { new ConfiguredServer { Port = ProcessOperationTests.FreePort(), Type = 3 } },
        }), NullLoggerFactory.Instance, new TestServiceController(), new TestPlatformMonitor(), new TestFileDeploy());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.False(worker.ExecuteTask!.IsCompleted);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ConfiguredDatabaseFailure_FaultsStartup_InsteadOfFallingBackToDbLess()
    {
        using var worker = new ControlWorker(Options.Create(new ControlServerOptions
        {
            Db = new ControlDbOptions { GlobalConnectionString = "invalid connection string" },
        }), NullLoggerFactory.Instance, new TestServiceController(), new TestPlatformMonitor(), new TestFileDeploy());
        await worker.StartAsync(CancellationToken.None);
        Assert.NotNull(worker.ExecuteTask);
        await Assert.ThrowsAsync<ArgumentException>(() => worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
