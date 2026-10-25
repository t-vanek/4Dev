namespace TControl.Server.Ops;

/// <summary>Per-machine CPU, physical memory and aggregate network utilization, all in percent.</summary>
public readonly record struct PlatformSample(uint Cpu, uint Mem, float Net);

public interface IPlatformMonitor
{
    PlatformSample? Sample(byte machineId);
}
