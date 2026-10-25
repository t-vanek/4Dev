using Serilog;
using TWorld.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders(); // Serilog is the sole sink (avoids duplicate console lines)
builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.Configure<WorldServerOptions>(builder.Configuration.GetSection("World"));
builder.Services.AddHostedService<WorldWorker>();
builder.Services.AddHostedService<FourDev.Hosting.ManagedProcessLifetime>();

var host = builder.Build();
host.Run();
