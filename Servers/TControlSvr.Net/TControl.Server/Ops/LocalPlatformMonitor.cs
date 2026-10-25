using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TControl.Server.Ops;

public sealed class LocalPlatformMonitor(IOptions<ControlServerOptions> options, ILogger<LocalPlatformMonitor> log)
    : IPlatformMonitor
{
    private (ulong Idle, ulong Total)? _cpu;
    private long _networkBytes, _networkTick;

    public PlatformSample? Sample(byte machineId)
    {
        if (machineId != options.Value.LocalMachineId) return null;
        try
        {
            var current = ReadCpu();
            uint cpu = 0;
            if (_cpu is { } previous && current.Total > previous.Total && current.Idle >= previous.Idle)
                cpu = (uint)Math.Clamp(100d * (1 - (double)(current.Idle - previous.Idle) / (current.Total - previous.Total)), 0, 100);
            _cpu = current;

            long bytes = 0, speed = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                bytes += stats.BytesSent + stats.BytesReceived;
                speed += Math.Max(0, nic.Speed);
            }
            long tick = Stopwatch.GetTimestamp();
            double seconds = (double)(tick - _networkTick) / Stopwatch.Frequency;
            float net = _networkTick != 0 && bytes >= _networkBytes && speed > 0 && seconds > 0
                ? (float)Math.Clamp((bytes - _networkBytes) * 800d / (seconds * speed), 0, 100) : 0;
            _networkBytes = bytes;
            _networkTick = tick;
            return new PlatformSample(cpu, ReadMemoryPercent(), net);
        }
        catch (Exception ex) when (ex is IOException or NetworkInformationException or System.ComponentModel.Win32Exception
                                   or PlatformNotSupportedException or UnauthorizedAccessException or FormatException)
        {
            log.LogWarning(ex, "Local platform sample is unavailable.");
            return null;
        }
    }

    private static (ulong Idle, ulong Total) ReadCpu()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) throw new System.ComponentModel.Win32Exception();
            return (idle, kernel + user);
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var values = fields.Skip(1).Take(8).Select(s => ulong.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return (values[3] + values[4], values.Aggregate(0UL, (sum, value) => sum + value));
    }

    private static uint ReadMemoryPercent()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref status)) throw new System.ComponentModel.Win32Exception();
            return status.Load;
        }
        var fields = File.ReadLines("/proc/meminfo").Select(l => l.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(p => p[0], p => ulong.Parse(p[1], CultureInfo.InvariantCulture));
        return (uint)Math.Clamp(100d * (1 - (double)fields["MemAvailable"] / fields["MemTotal"]), 0, 100);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
