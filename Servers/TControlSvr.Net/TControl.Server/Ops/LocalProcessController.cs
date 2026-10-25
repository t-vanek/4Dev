using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TControl.Protocol;
using TControl.Server.Control;

namespace TControl.Server.Ops;

/// <summary>Supervises registered local processes across Control restarts. Stop requires authenticated IPC.</summary>
public sealed class LocalProcessController : IServiceController, IAsyncDisposable
{
    private sealed class Child(Process process, OwnedProcessRecord record)
    {
        public Process Process { get; } = process;
        public OwnedProcessRecord Record { get; set; } = record;
        public bool Stopping { get; set; }
        public long RestartVersion { get; set; }
        public TaskCompletionSource? StopCompletion { get; set; }
    }

    private readonly IHostEnvironment _environment;
    private readonly ILogger<LocalProcessController> _log;
    private readonly OwnedProcessStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Child> _children = new();
    private bool _disposed;

    public LocalProcessController(IHostEnvironment environment, ILogger<LocalProcessController> log,
        IOptions<ControlServerOptions>? options = null)
    {
        _environment = environment;
        _log = log;
        _store = new OwnedProcessStore(Path.GetFullPath(options?.Value.ProcessStateDirectory ?? "process-state", environment.ContentRootPath));
    }

    private static OwnedProcessRecord Stopped(OwnedProcessRecord record)
        => record with { ProcessId = 0, StartedUtc = 0, PipeName = "", Token = "" };

    // Called under _gate. Recover only a recorded PID with the original creation time and executable.
    private Child? FindChild(ServiceInstance service)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_children.TryGetValue(service.Id, out var child)) return child;
        var record = _store.Read(service.Id);
        if (record is null || record.ProcessId == 0)
        {
            if (record is { PipeName.Length: > 0 }) throw new IOException("An incomplete process launch needs operator recovery.");
            return null;
        }
        Process process;
        try { process = Process.GetProcessById(record.ProcessId); }
        catch (ArgumentException) { _store.Write(Stopped(record)); return null; }
        try
        {
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != record.StartedUtc)
            {
                process.Dispose();
                _store.Write(Stopped(record));
                return null;
            }
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(process.MainModule?.FileName, record.Executable, comparison))
                throw new IOException("Recorded executable does not match the running process.");
            child = new Child(process, record);
            _children.Add(service.Id, child);
            _log.LogInformation("Recovered registered process {Name}, PID {Pid}.", service.Name, process.Id);
            return child;
        }
        catch { process.Dispose(); throw; }
    }

    public uint? GetStatus(ServiceInstance service)
    {
        if (service.Process is null) return null;
        lock (_gate)
        {
            try
            {
                var child = FindChild(service);
                if (child is null) return service.Conn is null ? Proto.SvcStopped : Proto.SvcCannotControl;
                // Stop owns the process handle until its asynchronous wait has finished.
                if (child.Stopping) return Proto.SvcStopPending;
                if (!child.Process.HasExited) return Proto.SvcRunning;
                _log.LogInformation("Process {Name} exited with code {Code}.", service.Name, child.Process.ExitCode);
                _store.Write(Stopped(child.Record));
                child.Process.Dispose();
                _children.Remove(service.Id);
                return Proto.SvcStopped;
            }
            catch (Exception ex) when (IsOperationalError(ex))
            {
                _log.LogWarning(ex, "Cannot verify the owned process for {Name}.", service.Name);
                return Proto.SvcCannotControl;
            }
        }
    }

    public bool? GetRestartIntent(ServiceInstance service)
    {
        if (service.Process is null) return null;
        lock (_gate) return _children.TryGetValue(service.Id, out var child) ? child.Record.Restart : _store.Read(service.Id)?.Restart;
    }

    public void SetRestartIntent(ServiceInstance service, bool enabled)
    {
        if (service.Process is null) return;
        lock (_gate)
        {
            var child = FindChild(service);
            var record = child?.Record ?? _store.Read(service.Id) ?? new OwnedProcessRecord(service.Id, 0, 0,
                Executable(service.Process), "", "", enabled);
            record = record with { Restart = enabled };
            _store.Write(record);
            if (child is not null) { child.Record = record; child.RestartVersion++; }
        }
    }

    private string DirectoryFor(LocalProcessOptions configuration) => string.IsNullOrWhiteSpace(configuration.WorkingDirectory)
        ? _environment.ContentRootPath : Path.GetFullPath(configuration.WorkingDirectory, _environment.ContentRootPath);
    private string Executable(LocalProcessOptions configuration) => Path.GetFullPath(configuration.FileName, DirectoryFor(configuration));

    public bool Start(ServiceInstance service)
    {
        if (service.Process is not { } configuration) return false;
        lock (_gate)
        {
            try
            {
                if (GetStatus(service) != Proto.SvcStopped) return false;
                string file = Executable(configuration);
                if (!File.Exists(file) || configuration.StopTimeoutSeconds is < 1 or > 600)
                    throw new InvalidOperationException("A local executable and shutdown timeout of 1–600 seconds are required.");
                string executable = File.ResolveLinkTarget(file, returnFinalTarget: true)?.FullName ?? file;
                var record = new OwnedProcessRecord(service.Id, 0, 0, executable, $"4dev-{Guid.NewGuid():N}",
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), true);
                // Check that state can be persisted before launching a process.
                _store.Write(record);
                var start = new ProcessStartInfo(file)
                {
                    WorkingDirectory = DirectoryFor(configuration), UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (string argument in configuration.Arguments) start.ArgumentList.Add(argument);
                foreach (var (name, value) in configuration.Environment) start.Environment[name] = value;
                start.Environment["FOURDEV_CONTROLLED_PROCESS"] = "1";
                start.Environment["FOURDEV_CONTROL_PIPE"] = record.PipeName;
                start.Environment["FOURDEV_CONTROL_TOKEN"] = record.Token;
                Process? process = null;
                try
                {
                    process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
                    record = record with { ProcessId = process.Id, StartedUtc = process.StartTime.ToUniversalTime().Ticks };
                    _children.Add(service.Id, new Child(process, record));
                    _store.Write(record);
                }
                catch
                {
                    if (process is null || process.HasExited) { _store.Write(Stopped(record)); process?.Dispose(); }
                    throw;
                }
                _log.LogInformation("Started {Name}, PID {Pid}.", service.Name, process.Id);
                return true;
            }
            catch (Exception ex) when (IsOperationalError(ex))
            {
                _log.LogError(ex, "Could not start configured process {Name}.", service.Name);
                return false;
            }
        }
    }

    public async Task<bool> StopAsync(ServiceInstance service, CancellationToken cancellationToken = default)
    {
        if (service.Process is not { } configuration) return false;
        Child child;
        bool previousRestart;
        long intentVersion;
        lock (_gate)
        {
            try
            {
                var found = FindChild(service);
                if (found is null || found.Stopping) return false;
                child = found;
                previousRestart = child.Record.Restart;
                _store.Write(child.Record with { Restart = false });
                child.Record = child.Record with { Restart = false };
                intentVersion = ++child.RestartVersion;
                child.Stopping = true;
                child.StopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            catch (Exception ex) when (IsOperationalError(ex))
            { _log.LogWarning(ex, "Cannot begin stop for {Name}.", service.Name); return false; }
        }
        bool success = false;
        bool shutdownAccepted = false;
        try
        {
            if (!child.Process.HasExited)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(configuration.StopTimeoutSeconds));
                using var pipe = new NamedPipeClientStream(".", child.Record.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(timeout.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(("AUTH " + child.Record.Token).AsMemory(), timeout.Token);
                string expected = $"READY {child.Record.ProcessId.ToString(CultureInfo.InvariantCulture)} {child.Record.StartedUtc.ToString(CultureInfo.InvariantCulture)}";
                if (await reader.ReadLineAsync(timeout.Token) != expected) throw new IOException("Process-control identity verification failed.");
                await writer.WriteLineAsync("SHUTDOWN".AsMemory(), timeout.Token);
                if (await reader.ReadLineAsync(timeout.Token) != "STOPPING") throw new IOException("Shutdown was not acknowledged.");
                shutdownAccepted = true;
                // Force termination is allowed only after this exact process authenticated and accepted shutdown.
                try { await child.Process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && configuration.ForceKillOnTimeout)
                {
                    _log.LogWarning("Graceful shutdown timed out for {Name}; terminating its authenticated process tree.", service.Name);
                    child.Process.Kill(entireProcessTree: true);
                    await child.Process.WaitForExitAsync(cancellationToken);
                }
            }
            success = true;
        }
        catch (Exception ex) when (IsOperationalError(ex))
        {
            success = child.Process.HasExited;
            if (!success && cancellationToken.IsCancellationRequested && shutdownAccepted)
                _log.LogInformation("Stop wait interrupted for {Name}; the accepted shutdown intent is retained.", service.Name);
            else if (!success) _log.LogWarning(ex, "Could not stop {Name}; its prior restart intent is retained.", service.Name);
        }
        finally
        {
            lock (_gate)
            {
                if (success)
                {
                    child.Record = Stopped(child.Record);
                    child.Process.Dispose();
                    _children.Remove(service.Id);
                }
                else if (child.RestartVersion == intentVersion && !(cancellationToken.IsCancellationRequested && shutdownAccepted))
                    child.Record = child.Record with { Restart = previousRestart };
                child.Stopping = false;
                try { _store.Write(child.Record); }
                catch (Exception ex) when (IsOperationalError(ex)) { _log.LogError(ex, "Could not save process state for {Name}.", service.Name); }
                finally { child.StopCompletion?.TrySetResult(); }
            }
        }
        return success;
    }

    private static bool IsOperationalError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException;

    public async ValueTask DisposeAsync()
    {
        Task[] stops;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            stops = _children.Values.Where(c => c.Stopping).Select(c => c.StopCompletion!.Task).ToArray();
        }
        await Task.WhenAll(stops);
        lock (_gate)
        {
            // Detach only. Games and their authenticated pipe endpoints survive Control maintenance.
            foreach (var child in _children.Values) child.Process.Dispose();
            _children.Clear();
            _store.Dispose();
        }
    }
}
