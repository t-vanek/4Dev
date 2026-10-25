using Serilog;
using TControl.Server;
using TControl.Server.Ops;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders(); // Serilog is the sole sink (avoids duplicate console lines)
builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.Configure<ControlServerOptions>(builder.Configuration.GetSection("Control"));

builder.Services.AddSingleton<IServiceController, LocalProcessController>();
builder.Services.AddSingleton<IPlatformMonitor, LocalPlatformMonitor>();
builder.Services.AddSingleton<IFileDeploy, LocalFileDeploy>();
builder.Services.AddHostedService<FourDev.Hosting.ManagedProcessLifetime>();

builder.Services.AddHostedService<ControlWorker>();

var host = builder.Build();
host.Run();
