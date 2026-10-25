using FourDev.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ManagedProcessLifetime>();
var host = builder.Build();
string marker = builder.Configuration["Marker"]!;
int shutdownDelay = int.TryParse(builder.Configuration["ShutdownDelay"], out int delay) ? delay : 100;
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => File.WriteAllText(marker, "started"));
await host.RunAsync();
// Simulates a server's final persistence flush on graceful shutdown.
if (shutdownDelay != 100) File.AppendAllText(marker, "|stopping");
await Task.Delay(shutdownDelay);
File.AppendAllText(marker, "|stopped");
