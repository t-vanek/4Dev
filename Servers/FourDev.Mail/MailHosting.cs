using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.SqlServer;

namespace FourDev.Mail;

public static class MailHosting
{
    public static void AddGameMail(this HostApplicationBuilder builder, string? gameConnection,
        string? globalConnection, bool world, bool provision = false, bool listenForDelivery = true, Action<WolverineOptions>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(gameConnection)) return;
        builder.Services.AddSingleton(new MailSqlBackend(gameConnection, globalConnection));
        builder.Services.AddSingleton<MailNotificationInbox>();
        builder.Services.AddSingleton<WolverineMailStore>();
        // Registered before Wolverine's hosted service: missing gameplay receipt schema must fail before consuming anything.
        builder.Services.AddHostedService<MailPrerequisites>();
        builder.UseWolverine(opts => { Configure(opts, gameConnection, world, provision, listenForDelivery); configure?.Invoke(opts); });
    }

    public static void Configure(WolverineOptions opts, string gameConnection, bool world, bool provision, bool listenForDelivery = true)
    {
        opts.ServiceName = "4dev-game-mail";
        opts.AutoBuildMessageStorageOnStartup = provision ? AutoCreate.CreateOrUpdate : AutoCreate.None;
        var sql = opts.UseSqlServerPersistenceAndTransport(gameConnection, "fourdev_mail", transportSchema: "fourdev_mail_transport");
        if (provision) sql.AutoProvision();
        opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MailHandlers));
        // Map nodes must never recover a World actor notification into an inbox they cannot drain.
        if (world) opts.Discovery.IncludeType(typeof(MailNotificationHandler));
        opts.PublishMessage<DeliverServerMail>().ToSqlServerQueue("mail-delivery");
        opts.PublishMessage<MailDelivered>().ToSqlServerQueue("mail-notifications");
        if (listenForDelivery) opts.ListenToSqlServerQueue("mail-delivery").Sequential();
        if (world) opts.ListenToSqlServerQueue("mail-notifications").Sequential();
    }

    private sealed class MailPrerequisites(MailSqlBackend backend) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => backend.Map.VerifyOperatorPostSchemaAsync();
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
