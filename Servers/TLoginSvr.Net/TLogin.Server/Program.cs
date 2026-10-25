using Serilog;
using TLogin.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders(); // Serilog is the sole sink (avoids duplicate console lines)
builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.Configure<LoginServerOptions>(builder.Configuration.GetSection("Login"));
builder.Services.AddHostedService<LoginWorker>();
builder.Services.AddHostedService<FourDev.Hosting.ManagedProcessLifetime>();

var host = builder.Build();
host.Run();
