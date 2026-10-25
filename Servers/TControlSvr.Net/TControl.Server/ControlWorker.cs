using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TControl.Data;
using TControl.Protocol;
using TControl.Server.Control;
using TControl.Server.Net;
using TControl.Server.Ops;

namespace TControl.Server;

internal enum BatchKind { Packet, ManagerConnected, ManagerDisconnected, ServerConnected, ServerDisconnected, Tick, StopCompleted }

/// <summary>One unit of work for the single batch task.</summary>
internal readonly record struct BatchItem(BatchKind Kind, PacketConnection? Conn = null, byte[]? Packet = null,
    ServiceInstance? Service = null, ProcessStopResult? StopResult = null);

/// <summary>
/// Hosted service that boots the control server: load config → open DB (optional) → load topology + events
/// → run the manager accept loop, the outbound connector (dials each configured game server), a 1-second
/// timer, and the single batch task that serializes all handling (mirroring the C++ batch thread + lock).
/// </summary>
public sealed class ControlWorker : BackgroundService
{
    private readonly ControlServerOptions _opt;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ControlWorker> _log;
    private readonly IServiceController _svcCtl;
    private readonly IPlatformMonitor _platform;
    private readonly IFileDeploy _fileDeploy;
    private readonly IOperatorAuthenticator? _authenticator;

    public ControlWorker(IOptions<ControlServerOptions> opt, ILoggerFactory loggerFactory,
        IServiceController svcCtl, IPlatformMonitor platform, IFileDeploy fileDeploy, IOperatorAuthenticator? authenticator = null)
    {
        _opt = opt.Value;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<ControlWorker>();
        _svcCtl = svcCtl;
        _platform = platform;
        _fileDeploy = fileDeploy;
        _authenticator = authenticator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_opt.Port is < 1 or > 65535 || _opt.RestartDelaySeconds is < 1 or > 3600 || _opt.MaxUploadBytes <= 0)
            throw new InvalidOperationException("Invalid Control port, restart delay or upload limit.");
        var state = new ControlState { AutoStart = _opt.AutoStart };

        ControlDatabase? db = null;
        if (!string.IsNullOrWhiteSpace(_opt.Db.GlobalConnectionString))
        {
            db = new ControlDatabase(_opt.Db.GlobalConnectionString);
            try
            {
                await db.PingAsync(stoppingToken);
                await LoadTopologyAsync(db, state, stoppingToken);
                await LoadEventsAsync(db, state, stoppingToken);
                try { state.CashItems.AddRange(await db.LoadCashShopItemsAsync(stoppingToken)); }
                catch (Exception ex) { _log.LogWarning(ex, "TCASHSHOPITEMCHART load failed."); }
                try { state.MyAddr = (await db.LoadServiceAsync(Proto.SVRGRP_NULL, Proto.SVRGRP_CTLSVR, stoppingToken)).Ip; }
                catch (Exception ex) { _log.LogWarning(ex, "TLoadService(CTLSVR) failed."); }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Configured global DB is unavailable; refusing to start the control listener.");
                throw;
            }
        }

        // Apply the configured dial list: used alone in DB-less mode, or to override/augment the DB
        // topology (an entry with the same group/type/serverId redirects that service's dial address —
        // handy when the DB TIPADDR points at an address not reachable from this host/container).
        if (_opt.Servers.Count > 0)
            BuildServicesFromConfig(state);

        _log.LogInformation("Control server: {Svc} service(s), {Grp} group(s), {Ev} event(s), myAddr='{Addr}', autoStart={Auto}.",
            state.Services.Count, state.Groups.Count, state.Events.Count, state.MyAddr, state.AutoStart);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var token = lifetime.Token;
        var batch = Channel.CreateUnbounded<BatchItem>(new UnboundedChannelOptions { SingleReader = true });
        var service = new ControlService(state, db, _loggerFactory.CreateLogger<ControlService>(), _svcCtl, _platform, _fileDeploy,
            _authenticator, _opt, token,
            result => batch.Writer.TryWrite(new BatchItem(BatchKind.StopCompleted, StopResult: result)));
        var batchTask = RunBatchAsync(batch.Reader, service, token);
        var timerTask = RunTimerAsync(batch.Writer, token);
        var connectors = state.Services.Values
            .Select(svc => RunConnectorAsync(svc, batch.Writer, token))
            .ToArray();

        var server = new PacketServer(_loggerFactory.CreateLogger<PacketServer>());
        var listener = server.ListenAsync(
            _opt.Port,
            onPacket: (conn, data) => { batch.Writer.TryWrite(new BatchItem(BatchKind.Packet, conn, data)); return ValueTask.CompletedTask; },
            onConnected: conn => { batch.Writer.TryWrite(new BatchItem(BatchKind.ManagerConnected, conn)); return ValueTask.CompletedTask; },
            onDisconnected: conn => { batch.Writer.TryWrite(new BatchItem(BatchKind.ManagerDisconnected, conn)); return ValueTask.CompletedTask; },
            token: token);
        try { await await Task.WhenAny(new[] { listener, batchTask, timerTask }.Concat(connectors)); }
        finally
        {
            lifetime.Cancel();
            batch.Writer.TryComplete();
            try { await Task.WhenAll(new[] { listener, batchTask, timerTask }.Concat(connectors)); }
            catch (OperationCanceledException) { }
            finally { await service.StopOperationsAsync(); service.CloseSessions(); }
        }
    }

    private async Task RunBatchAsync(ChannelReader<BatchItem> reader, ControlService service, CancellationToken token)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(token))
            {
                switch (item.Kind)
                {
                    case BatchKind.Packet: await service.DispatchAsync(item.Conn!, item.Packet!); break;
                    case BatchKind.ManagerConnected: service.OnManagerConnected(item.Conn!); break;
                    case BatchKind.ManagerDisconnected: service.OnManagerDisconnected(item.Conn!); break;
                    case BatchKind.ServerConnected: service.OnServerConnected(item.Conn!, item.Service!); break;
                    case BatchKind.ServerDisconnected: service.OnServerDisconnected(item.Conn!, item.Service!); break;
                    case BatchKind.Tick: await service.OnTickAsync(); break;
                    case BatchKind.StopCompleted: service.CompleteStop(item.StopResult!); break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunTimerAsync(ChannelWriter<BatchItem> writer, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                writer.TryWrite(new BatchItem(BatchKind.Tick));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Per-service dial loop: connect, run, and re-dial after the configured interval when it drops.</summary>
    private async Task RunConnectorAsync(ServiceInstance svc, ChannelWriter<BatchItem> writer, CancellationToken token)
    {
        var logger = _loggerFactory.CreateLogger<PacketConnection>();
        var delay = TimeSpan.FromSeconds(Math.Max(1, _opt.ConnectIntervalSeconds));
        while (!token.IsCancellationRequested)
        {
            if ((svc.IsWorld || svc.IsMap) && svc.PeerTls is null)
            {
                logger.LogWarning("Server {Service} requires configured PeerTls; connection denied.", svc.Id);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return;
            }
            await PacketClient.RunConnectionAsync(
                svc.DialHost, svc.Port, logger,
                onPacket: (conn, data) => { writer.TryWrite(new BatchItem(BatchKind.Packet, conn, data)); return ValueTask.CompletedTask; },
                onConnected: conn => { writer.TryWrite(new BatchItem(BatchKind.ServerConnected, conn, Service: svc)); return ValueTask.CompletedTask; },
                onDisconnected: conn => { writer.TryWrite(new BatchItem(BatchKind.ServerDisconnected, conn, Service: svc)); return ValueTask.CompletedTask; },
                token, svc.PeerTls);
            try { await Task.Delay(delay, token); } catch (OperationCanceledException) { break; }
        }
    }

    // ===== startup loads =====

    private async Task LoadTopologyAsync(ControlDatabase db, ControlState state, CancellationToken ct)
    {
        foreach (var m in await db.LoadMachinesAsync(ct)) state.Machines[m.MachineId] = m;
        foreach (var g in await db.LoadGroupsAsync(ct)) state.Groups[g.GroupId] = g;
        foreach (var t in await db.LoadSvrTypesAsync(ct)) state.SvrTypes[t.Type] = t;

        foreach (var row in await db.LoadServersAsync(ct))
        {
            if (!state.Groups.TryGetValue(row.GroupId, out var group)) continue;
            if (!state.SvrTypes.TryGetValue(row.Type, out var type)) continue;
            if (!state.Machines.TryGetValue(row.MachineId, out var machine)) continue;
            var svc = new ServiceInstance
            {
                Id = Proto.MakeSvrId(row.GroupId, row.Type, row.ServerId),
                ServerId = row.ServerId, Port = row.Port, Name = row.Name,
                Group = group, SvrType = type, Machine = machine,
            };
            state.Services[svc.Id] = svc;
        }
    }

    private async Task LoadEventsAsync(ControlDatabase db, ControlState state, CancellationToken ct)
    {
        try
        {
            foreach (var row in await db.LoadEventsAsync(ct))
            {
                var e = new EventInfo
                {
                    Index = row.Index, Id = row.Id, GroupId = row.GroupId, SvrType = row.SvrType, SvrId = row.SvrId,
                    StartDate = new DateTimeOffset(DateTime.SpecifyKind(row.StartDate, DateTimeKind.Local)).ToUnixTimeSeconds(),
                    EndDate = new DateTimeOffset(DateTime.SpecifyKind(row.EndDate, DateTimeKind.Local)).ToUnixTimeSeconds(),
                    Value = row.Value, MapId = row.MapId, StartAlarm = row.StartAlarm, EndAlarm = row.EndAlarm,
                    StartMsg = row.StartMsg, EndMsg = row.EndMsg, Title = row.Title, PartTime = row.PartTime,
                };
                e.ParseStrValue(row.SzValue);
                state.Events[e.Index] = e;
                if (state.EventIndex < e.Index) state.EventIndex = e.Index;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "TEVENTCHART load failed; no scheduled events."); }
    }

    private void BuildServicesFromConfig(ControlState state)
    {
        foreach (var cfg in _opt.Servers)
        {
            if (cfg.Port is < 1 or > 65535) throw new InvalidOperationException("Configured server port must be 1–65535.");
            if (cfg.Process is not null && cfg.Host != "localhost"
                && (!System.Net.IPAddress.TryParse(cfg.Host, out var host) || !System.Net.IPAddress.IsLoopback(host)))
                throw new InvalidOperationException("A locally managed process must have a loopback dial address.");
            var group = state.Groups.TryGetValue(cfg.Group, out var g) ? g : new TGroup { GroupId = cfg.Group, Name = $"G{cfg.Group}" };
            var type = state.SvrTypes.TryGetValue(cfg.Type, out var t) ? t : new TSvrType { Type = cfg.Type, Name = $"T{cfg.Type}" };
            var machine = new TMachine { MachineId = _opt.LocalMachineId, Name = cfg.Host };
            machine.PriAddr.Add(cfg.Host);
            machine.IpAddr.Add(cfg.Host);
            state.Groups.TryAdd(group.GroupId, group);
            state.SvrTypes.TryAdd(type.Type, type);
            state.Machines[machine.MachineId] = machine;
            var svc = new ServiceInstance
            {
                Id = Proto.MakeSvrId(cfg.Group, cfg.Type, cfg.ServerId),
                ServerId = cfg.ServerId, Port = (ushort)cfg.Port, Name = string.IsNullOrEmpty(cfg.Name) ? machine.Name : cfg.Name,
                Group = group, SvrType = type, Machine = machine,
                Process = cfg.Process, PeerTls = cfg.PeerTls, ManagerControl = cfg.Process is not null,
            };
            state.Services[svc.Id] = svc;
            svc.ManagerControl = _svcCtl.GetRestartIntent(svc) ?? svc.ManagerControl;
        }
        // Recover a deliberately stopped world's group even if Control restarted during its stop.
        foreach (var world in state.Services.Values.Where(s => s.Process is not null && s.IsWorld && !s.ManagerControl))
            foreach (var member in state.Services.Values.Where(s => s.Group.GroupId == world.Group.GroupId))
            {
                member.ManagerControl = false;
                _svcCtl.SetRestartIntent(member, false);
            }
    }
}
