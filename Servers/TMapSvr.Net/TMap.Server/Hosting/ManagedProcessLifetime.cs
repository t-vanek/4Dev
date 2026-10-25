using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FourDev.Hosting;

/// <summary>Authenticated local shutdown endpoint; independent of the Control process lifetime.</summary>
public sealed class ManagedProcessLifetime(IHostApplicationLifetime lifetime, ILogger<ManagedProcessLifetime> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("FOURDEV_CONTROLLED_PROCESS") != "1") return;
        string? name = Environment.GetEnvironmentVariable("FOURDEV_CONTROL_PIPE");
        string? token = Environment.GetEnvironmentVariable("FOURDEV_CONTROL_TOKEN");
        if (name is null || token is not { Length: 64 }) throw new InvalidOperationException("Managed process IPC is not configured.");
        Environment.SetEnvironmentVariable("FOURDEV_CONTROL_TOKEN", null);
        // A local supervisor's Ctrl+C must not propagate to a managed Linux game's process group.
        if (OperatingSystem.IsLinux() && setsid() < 0 && getsid(0) != Environment.ProcessId)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot detach the managed process session.");
        using var process = Process.GetCurrentProcess();
        string identity = $"READY {process.Id.ToString(CultureInfo.InvariantCulture)} {process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}";
        byte[] expected = Encoding.ASCII.GetBytes(token);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                string? authentication = await ReadCommand(reader, timeout.Token);
                if (authentication is not { Length: 69 } || !authentication.StartsWith("AUTH ", StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(authentication[5..]), expected))
                {
                    await writer.WriteLineAsync("DENIED".AsMemory(), timeout.Token);
                    continue;
                }
                await writer.WriteLineAsync(identity.AsMemory(), timeout.Token);
                if (await ReadCommand(reader, timeout.Token) == "SHUTDOWN")
                {
                    await writer.WriteLineAsync("STOPPING".AsMemory(), timeout.Token);
                    lifetime.StopApplication();
                    return;
                }
                // EOF or a probe only closes this connection; the game continues serving players.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                log.LogWarning("Local control connection interrupted ({Error}).", ex.GetType().Name);
                try { await Task.Delay(100, stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task<string?> ReadCommand(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var character = new char[1];
        while (text.Length < 128)
        {
            if (await reader.ReadAsync(character.AsMemory(), token) == 0) return null;
            if (character[0] == '\n') return text.ToString().TrimEnd('\r');
            text.Append(character[0]);
        }
        throw new IOException("Local control command is too long.");
    }

    [DllImport("libc", SetLastError = true)] private static extern int setsid();
    [DllImport("libc", SetLastError = true)] private static extern int getsid(int processId);
}
