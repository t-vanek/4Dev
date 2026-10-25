using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TMap.Data;
using TMap.Server.Map;
using TMap.Server.Net;

namespace TMap.Server;

/// <summary>
/// The hosted background service. Boots the map (loads any DB-backed data best-effort), then runs three
/// cooperating pieces on a single serialized batch task — exactly like TWorldSvr.Net:
/// <list type="bullet">
/// <item>the client acceptor (CS_MAP plane, encrypted),</item>
/// <item>the outbound world link (MW/SM/DM planes, plaintext),</item>
/// <item>a 1-second timer.</item>
/// </list>
/// Every client packet, world packet, disconnect and tick is funneled into one <see cref="Channel{T}"/>
/// so all <see cref="MapState"/> mutation happens on one thread — no locks (replaces the C++ batch thread
/// + global lock).
/// </summary>
public sealed class MapWorker : BackgroundService
{
    private readonly MapServerOptions _opt;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MapWorker> _log;

    public MapWorker(IOptions<MapServerOptions> opt, ILoggerFactory loggerFactory)
    {
        _opt = opt.Value;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<MapWorker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Must run before anything reads Channels: the config binder appends array elements to the
        // property's default, so the bound list can carry duplicates. See MapServerOptions.Normalize.
        _opt.Normalize();

        _log.LogInformation("TMapSvr.Net starting (group {Group}, server {Server}, port {Port}).",
            _opt.GroupId, _opt.ServerId, _opt.Port);

        // --- DB (best-effort; the map runs DB-free when unconfigured/unreachable) ---
        GameDatabase? gameDb = null;
        if (!string.IsNullOrWhiteSpace(_opt.Db.GameConnectionString))
        {
            try
            {
                gameDb = new GameDatabase(_opt.Db.GameConnectionString, _opt.Db.GlobalConnectionString);
                await gameDb.PingAsync(stoppingToken);
                _log.LogInformation("Game DB connected.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Game DB unavailable at startup; continuing DB-free.");
                gameDb = null;
            }
        }
        else
        {
            _log.LogInformation("No game connection string configured; running DB-free.");
        }

        // --- template charts (item + magic), loaded once; empty ⇒ raw values / no drop-unknown-template ---
        var templates = new TemplateStore();
        if (gameDb is not null)
        {
            try
            {
                templates = await gameDb.LoadTemplatesAsync(stoppingToken);
                _log.LogInformation(
                    "Loaded {Items} item templates, {Magics} magic templates, {Formulas} formulas, {Classes} classes, {Races} races.",
                    templates.Items.Count, templates.Magics.Count, templates.Formulas.Count,
                    templates.Classes.Count, templates.Races.Count);

                // The AI-script summary gets its own line — whether TAICHART actually has rows is
                // the difference between monsters running their real scripted behaviour and silently falling
                // back to the built-in roam/chase sweep, and that is otherwise invisible at runtime.
                if (templates.AiScripts.Count > 0)
                    _log.LogInformation(
                        "Loaded {Scripts} AI scripts ({Bindings} trigger bindings); {Aggressive} engage on sight (AT_ENTERLB → ChgHost/ChgMode).",
                        templates.AiScripts.Count,
                        templates.AiScripts.Values.Sum(s => s.BindingCount),
                        templates.AiScripts.Values.Count(s => s.IsAggressive));
                else
                    _log.LogWarning(
                        "No TAICHART rows loaded — monsters fall back to the built-in roam/chase sweep and none auto-aggro.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Template chart load failed; item RefineMax/magic values fall back to raw stored values.");
            }
        }

        var state = new MapState();
        var worldLink = new WorldLink(_opt.WorldIp, _opt.WorldPort, _loggerFactory.CreateLogger<WorldLink>());
        var service = new MapService(_opt, state, worldLink, gameDb, templates, _loggerFactory.CreateLogger<MapService>());
        // A configured DB failure must not silently turn off account protection via the DB-free game fallback.
        if (gameDb is null && (!string.IsNullOrWhiteSpace(_opt.Db.GameConnectionString) || !string.IsNullOrWhiteSpace(_opt.Db.GlobalConnectionString)))
            service.SecurityCodeStore = new GameDatabase(
                string.IsNullOrWhiteSpace(_opt.Db.GameConnectionString) ? _opt.Db.GlobalConnectionString : _opt.Db.GameConnectionString,
                _opt.Db.GlobalConnectionString);
        service.InitMonsterSpawns(); // build the SE_DEFAULT spawn points (empty when the charts aren't loaded)
        service.InitNpcs();          // build the NPC registry + shop stock (empty when the charts aren't loaded)
        service.InitTerritories();   // the forts, castles, missions and sky garden (MapService.Territory.cs)
        service.InitSwitches();      // build the per-channel map switches + gates (empty when the charts aren't loaded)
        service.InitQuests();        // build the quest trigger index (empty when the charts aren't loaded)
        await service.InitItemIdSeedAsync(); // seed the per-server item-id counter (enables inventory save; no-op DB-free)
        service.AuctionEnabled = _opt.AuctionEnabled;
        await service.LoadAuctionsAsync();   // the auction house's lots (MapService.Auction.cs; no-op DB-free)

        // --- single serialized batch task ---
        var batch = Channel.CreateUnbounded<BatchItem>(new UnboundedChannelOptions { SingleReader = true });
        var batchTask = RunBatchAsync(batch.Reader, service, stoppingToken);
        var timerTask = RunTimerAsync(batch.Writer, stoppingToken);

        // --- outbound world link ---
        var worldTask = worldLink.RunAsync(
            onConnected: () => { batch.Writer.TryWrite(BatchItem.WorldConnected()); return ValueTask.CompletedTask; },
            onPacket: p => { batch.Writer.TryWrite(BatchItem.WorldPacket(p)); return ValueTask.CompletedTask; },
            onDisconnected: () => { batch.Writer.TryWrite(BatchItem.WorldDisconnected()); return ValueTask.CompletedTask; },
            token: stoppingToken);

        // --- client acceptor ---
        var listener = new ClientListener(_loggerFactory.CreateLogger<ClientListener>()) { NoCrypt = _opt.NoCrypt };
        var listenTask = listener.ListenAsync(
            _opt.Port,
            onPacket: (conn, p) => { batch.Writer.TryWrite(BatchItem.ClientPacket(conn, p)); return ValueTask.CompletedTask; },
            onConnected: service.OnClientConnectedAsync,
            onDisconnected: conn => { batch.Writer.TryWrite(BatchItem.ClientDisconnected(conn)); return ValueTask.CompletedTask; },
            token: stoppingToken);

        await Task.WhenAll(listenTask, worldTask, timerTask);
        batch.Writer.TryComplete();
        await batchTask;
        await service.SaveAllCharDataAsync(); // final flush on shutdown (C++ SaveAllCharData); no-op DB-free
        _log.LogInformation("TMapSvr.Net stopped.");
    }

    private async Task RunBatchAsync(ChannelReader<BatchItem> reader, MapService service, CancellationToken token)
    {
        await foreach (var item in reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                switch (item.Kind)
                {
                    case BatchKind.Tick: await service.OnTimerAsync(); break;
                    case BatchKind.ClientPacket:
                        if (item.Client!.State is Map.ClientSession cs) await service.DispatchClientAsync(cs, item.Packet!);
                        break;
                    case BatchKind.ClientDisconnected:
                        if (item.Client!.State is Map.ClientSession ds) service.OnClientDisconnect(ds);
                        break;
                    case BatchKind.WorldConnected: await service.OnWorldConnectedAsync(); break;
                    case BatchKind.WorldPacket: await service.DispatchWorldAsync(item.Packet!); break;
                    case BatchKind.WorldDisconnected: service.OnWorldDisconnect(); break;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Batch item {Kind} failed.", item.Kind);
            }
            // Stop once the queue is drained (an unbounded single-reader channel has no Count — TryPeek instead).
            if (token.IsCancellationRequested && !reader.TryPeek(out _)) break;
        }
    }

    private static async Task RunTimerAsync(ChannelWriter<BatchItem> writer, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                writer.TryWrite(BatchItem.Timer());
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

internal enum BatchKind { Tick, ClientPacket, ClientDisconnected, WorldConnected, WorldPacket, WorldDisconnected }

/// <summary>A single unit of serialized work on the batch task.</summary>
internal readonly record struct BatchItem(BatchKind Kind, ClientConnection? Client, byte[]? Packet)
{
    public static BatchItem Timer() => new(BatchKind.Tick, null, null);
    public static BatchItem ClientPacket(ClientConnection c, byte[] p) => new(BatchKind.ClientPacket, c, p);
    public static BatchItem ClientDisconnected(ClientConnection c) => new(BatchKind.ClientDisconnected, c, null);
    public static BatchItem WorldConnected() => new(BatchKind.WorldConnected, null, null);
    public static BatchItem WorldPacket(byte[] p) => new(BatchKind.WorldPacket, null, p);
    public static BatchItem WorldDisconnected() => new(BatchKind.WorldDisconnected, null, null);
}
