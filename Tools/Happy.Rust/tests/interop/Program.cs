// Test-only loopback host. Never reads production settings or connects to the game/database.
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TControl.Protocol;
using TControl.Server;
using TControl.Server.Control;
using TControl.Server.Net;
using TControl.Server.Ops;

string root = Path.GetFullPath(args.Single());
Directory.CreateDirectory(root);
var options = new ControlServerOptions { LocalMachineId = 1, UploadRoot = Path.Combine(root, "deploy") };
using var files = new LocalFileDeploy(Options.Create(options), new FixtureEnvironment(root), NullLogger<LocalFileDeploy>.Instance);
var state = new ControlState();
var group = new TGroup { GroupId = 1, Name = "Fixture world" };
var machine = new TMachine { MachineId = 1, Name = "Fixture machine" };
var type = new TSvrType { Type = 3, Name = "World" };
state.Groups.Add(1, group); state.Machines.Add(1, machine); state.SvrTypes.Add(3, type);
uint id = Proto.MakeSvrId(1, 3, 1);
state.Services.Add(id, new ServiceInstance { Id = id, ServerId = 1, Name = "Fixture World", Group = group, Machine = machine, SvrType = type, Status = Proto.SvcCannotControl });
var example = new EventInfo {
    Index = 42, Id = 14, GroupId = 1, SvrType = 4, SvrId = 2, StartDate = 1_900_000_000,
    EndDate = 1_900_003_600, Value = 150, MapId = 255, StartAlarm = 60, EndAlarm = 30,
    StartMsg = "Start", EndMsg = "End", Title = "Lottery", PartTime = 1, LotMsg = "Prize",
};
example.CashItems.Add(new() { Id = 101, SaleValue = 25 });
example.Mon.StartAction = 2; example.Mon.EndAction = 3; example.Mon.SpawnIds.Add(501);
example.MonRegens.Add(new() { MonId = 12, Delay = 1000, MapId = 20, PosX = -1.5f, PosY = 2.25f, PosZ = 3.5f });
example.Lotteries.Add(new() { ItemId = 200, Num = 3, Winner = 10 });
state.Events.Add(42, example); state.EventIndex = 42;
// A golden body produced by the production .NET EventInfo writer, consumed and re-encoded in Rust.
var golden = new PacketWriter(Msg.CT_EVENTLIST_ACK).WriteUInt16(1); example.Write(golden);
File.WriteAllBytes(Path.Combine(root, "event-packet.bin"), golden.ToArray());

var service = new ControlService(state, null, NullLogger<ControlService>.Instance,
    new FixtureProcesses(), new FixtureMonitor(), files, new FixtureAuth(), options);
var batch = Channel.CreateUnbounded<Func<Task>>();
using var stop = new CancellationTokenSource();
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
Console.Out.Flush();
var connections = new List<Task>();
var accept = Task.Run(async () => {
    try {
        while (!stop.IsCancellationRequested) {
            var conn = new PacketConnection(await listener.AcceptSocketAsync(stop.Token), NullLogger.Instance);
            batch.Writer.TryWrite(() => { service.OnManagerConnected(conn); return Task.CompletedTask; });
            connections.Add(conn.RunAsync((c, p) => { batch.Writer.TryWrite(() => service.DispatchAsync(c, p)); return ValueTask.CompletedTask; }, stop.Token)
                .ContinueWith(_ => batch.Writer.TryWrite(() => { service.OnManagerDisconnected(conn); return Task.CompletedTask; })));
        }
    } catch (OperationCanceledException) { }
});
var tick = Task.Run(async () => {
    try {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stop.Token)) batch.Writer.TryWrite(service.OnTickAsync);
    } catch (OperationCanceledException) { }
});
var processing = Task.Run(async () => { await foreach (var action in batch.Reader.ReadAllAsync()) await action(); });
await Console.In.ReadLineAsync();
stop.Cancel(); listener.Stop(); await accept; await tick;
await Task.WhenAll(connections); batch.Writer.TryComplete(); await processing;
await service.StopOperationsAsync(); service.CloseSessions();

sealed class FixtureAuth : IOperatorAuthenticator {
    public Task<int> AuthenticateAsync(string id, string password, CancellationToken token)
        => Task.FromResult(password == "interop-only" ? id switch { "root" => 1, "gm" => 7, _ => 0 } : 0);
}
sealed class FixtureProcesses : IServiceController {
    public bool Start(ServiceInstance service) => false;
    public Task<bool> StopAsync(ServiceInstance service, CancellationToken token = default) => Task.FromResult(false);
    public uint? GetStatus(ServiceInstance service) => null;
}
sealed class FixtureMonitor : IPlatformMonitor { public PlatformSample? Sample(byte machine) => new(12, 34, 5.5f); }
sealed class FixtureEnvironment(string root) : IHostEnvironment {
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Happy.Interop";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
