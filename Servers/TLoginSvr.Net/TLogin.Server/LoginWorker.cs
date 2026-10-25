using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TLogin.Data;
using TLogin.Protocol;
using TLogin.Server.Login;
using TLogin.Server.Net;
using TLogin.Server.Security;

namespace TLogin.Server;

/// <summary>
/// Hosted service that boots the login server: loads world groups + reference data, clears stale
/// login rows (mirroring the C++ startup and OnExit), then runs the TCP acceptor until shutdown.
/// </summary>
public sealed class LoginWorker : BackgroundService
{
    private readonly LoginServerOptions _opt;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LoginWorker> _log;
    private GlobalDatabase? _global;

    public LoginWorker(IOptions<LoginServerOptions> opt, ILoggerFactory loggerFactory)
    {
        _opt = opt.Value;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<LoginWorker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_opt.Db.GlobalConnectionString))
            throw new InvalidOperationException("Login:Db:GlobalConnectionString is not configured.");

        var global = new GlobalDatabase(_opt.Db.GlobalConnectionString);
        var twoFactorStore = new SqlTwoFactorStore(_opt.Db.GlobalConnectionString);
        await twoFactorStore.ValidateSchemaAsync(stoppingToken);
        using var emailSender = new SmtpEmailSender(_opt.TwoFactor.Smtp);
        var twoFactor = new EmailTwoFactorService(twoFactorStore, emailSender, _opt.TwoFactor,
            _loggerFactory.CreateLogger<EmailTwoFactorService>());

        var groupConfigs = await global.LoadGroupsAsync(stoppingToken);
        var groups = new Dictionary<byte, (GroupConfig, GameDatabase)>();
        foreach (var g in groupConfigs)
        {
            string cs = _opt.Db.GameConnectionFor(g);
            groups[g.GroupId] = (g, new GameDatabase(cs));
            _log.LogInformation("Group {Id} '{Name}' -> DSN {Dsn}", g.GroupId, g.Name, g.Dsn);
        }

        var veteran = await global.LoadVeteranChartAsync(stoppingToken);
        var userCounts = await global.LoadUserCountsAsync(stoppingToken);
        await global.ClearLoginUsersAsync(stoppingToken);

        var nation = _opt.Nation ?? (Nation)await global.GetNationAsync(stoppingToken);
        _log.LogInformation("Nation = {Nation}, {GroupCount} group(s), {VetCount} veteran tier(s)",
            nation, groups.Count, veteran.Count);

        var service = new LoginService(
            global, groups, nation, veteran, userCounts,
            new AutoPassHwidValidator(),
            _opt.ValidateClientChecksum,
            _loggerFactory.CreateLogger<LoginService>(), _opt.MaxConcurrentLogins, twoFactor: twoFactor);

        var server = new PacketServer(_loggerFactory.CreateLogger<PacketServer>());
        if (!string.IsNullOrWhiteSpace(_opt.ControlServerIp) &&
            IPAddress.TryParse(_opt.ControlServerIp, out var ctrl))
        {
            server.PlaintextPeers.Add(ctrl);
            _log.LogInformation("Control server {Ip} will be treated as a plaintext peer.", ctrl);
        }

        _global = global; // Startup validation must not run shutdown cleanup against a failed configuration.
        await server.ListenAsync(_opt.Port, service.DispatchAsync, service.OnConnectedAsync, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting and drain in-flight verification/login work before clearing login rows.
        await base.StopAsync(cancellationToken);
        if (_global is not null)
        {
            try
            {
                await _global.ClearLoginUsersAsync(cancellationToken);
                _log.LogInformation("Cleared login current-user rows on shutdown.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to clear login current-user rows on shutdown.");
            }
        }
    }
}
