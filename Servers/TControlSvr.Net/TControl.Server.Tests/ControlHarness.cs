using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using TControl.Protocol;
using TControl.Server.Control;
using TControl.Server.Net;
using TControl.Server.Ops;

namespace TControl.Server.Tests;

internal sealed class TestAuthenticator(int authority = (int)ManagerClass.All, bool fail = false) : IOperatorAuthenticator
{
    public Task<int> AuthenticateAsync(string id, string password, CancellationToken cancellationToken)
        => fail ? throw new IOException("Database unavailable") : Task.FromResult(password == "correct" ? authority : 0);
}

internal sealed class TestServiceController : IServiceController
{
    public int Starts, Stops;
    public uint? Status;
    public bool Start(ServiceInstance service) { Starts++; Status = Proto.SvcRunning; return true; }
    public Task<bool> StopAsync(ServiceInstance service, CancellationToken cancellationToken = default)
    { Stops++; Status = Proto.SvcStopped; return Task.FromResult(true); }
    public uint? GetStatus(ServiceInstance service) => Status;
}

internal sealed class TestPlatformMonitor : IPlatformMonitor
{
    public PlatformSample? Sample(byte machineId) => new PlatformSample(12, 34, 5.5f);
}

internal sealed class TestFileDeploy : IFileDeploy
{
    public int Begins, Writes, Ends;
    public byte Begin(byte machineId, string fileName) { Begins++; return 0; }
    public byte Write(ReadOnlySpan<byte> data) { Writes++; return 0; }
    public byte End(bool cancel) { Ends++; return 0; }
}

internal sealed class TestEnvironment(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "TControl.Tests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "4dev-control-tests", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

/// <summary>Real framed TCP sockets with the production dispatcher serialized on one batch task.</summary>
internal sealed class ControlHarness : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Func<Task>> _batch = Channel.CreateUnbounded<Func<Task>>();
    private readonly List<Task> _connections = new();
    private readonly Task _batchTask;
    public ControlService Service { get; }
    public TestServiceController Controller { get; } = new();
    public TestFileDeploy Files { get; } = new();

    public ControlHarness(IOperatorAuthenticator? auth = null, IFileDeploy? files = null, ControlServerOptions? options = null,
        bool useDatabaseAuthenticator = false, IServiceController? controller = null)
    {
        Service = new ControlService(new ControlState(), null, NullLogger<ControlService>.Instance, controller ?? Controller,
            new TestPlatformMonitor(), files ?? Files,
            useDatabaseAuthenticator ? null : auth ?? new TestAuthenticator(), options,
            stopCompletion: result => _batch.Writer.TryWrite(() => { Service!.CompleteStop(result); return Task.CompletedTask; }));
        _batchTask = Task.Run(async () =>
        {
            await foreach (var action in _batch.Reader.ReadAllAsync(_cts.Token)) await action();
        });
    }

    public async Task<WireTestClient> ConnectAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new WireTestClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            var connection = new PacketConnection(await listener.AcceptSocketAsync(), NullLogger.Instance);
            Service.OnManagerConnected(connection);
            _connections.Add(connection.RunAsync((c, data) => { _batch.Writer.TryWrite(() => Service.DispatchAsync(c, data)); return ValueTask.CompletedTask; }, _cts.Token)
                .ContinueWith(_ => _batch.Writer.TryWrite(() => { Service.OnManagerDisconnected(connection); return Task.CompletedTask; }), TaskScheduler.Default));
            return client;
        }
        finally { listener.Stop(); }
    }

    public static PacketReader Login(WireTestClient client, bool readOnly = false, string password = "correct")
    {
        client.Send(new PacketWriter(readOnly ? Msg.CT_STLOGIN_REQ : Msg.CT_OPLOGIN_REQ).WriteString(Guid.NewGuid().ToString("N")).WriteString(password));
        return new PacketReader(client.ReceiveUntil(readOnly ? Msg.CT_STLOGIN_ACK : Msg.CT_OPLOGIN_ACK, Timeout));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Task.WhenAll(_connections).WaitAsync(Timeout);
        _batch.Writer.TryComplete();
        try { await _batchTask; } catch (OperationCanceledException) { }
        await Service.StopOperationsAsync();
        Service.CloseSessions();
        _cts.Dispose();
    }
}
