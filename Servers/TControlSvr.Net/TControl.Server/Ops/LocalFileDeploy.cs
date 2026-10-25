using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TControl.Server.Ops;

/// <summary>Bounded uploads to a configured local root; commit atomically, discard on failure/cancel.</summary>
public sealed class LocalFileDeploy(IOptions<ControlServerOptions> options, IHostEnvironment environment,
    ILogger<LocalFileDeploy> log) : IFileDeploy, IDisposable
{
    private FileStream? _stream;
    private string? _temporary, _target;

    public byte Begin(byte machineId, string fileName)
    {
        if (_stream is not null) return 1;
        var cfg = options.Value;
        if (machineId != cfg.LocalMachineId || string.IsNullOrWhiteSpace(cfg.UploadRoot)) return 2;
        try
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) || fileName.Contains(':') || fileName.Contains('\\')
                || fileName.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
                return 3;
            string root = Path.GetFullPath(cfg.UploadRoot, environment.ContentRootPath);
            string target = Path.GetFullPath(fileName, root);
            string relative = Path.GetRelativePath(root, target);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return 3;
            Directory.CreateDirectory(root);
            RejectLink(root);
            string directory = root;
            foreach (string component in relative.Split(Path.DirectorySeparatorChar).SkipLast(1))
            {
                directory = Path.Combine(directory, component);
                if (Directory.Exists(directory)) RejectLink(directory);
                else Directory.CreateDirectory(directory);
            }
            if (File.Exists(target)) RejectLink(target);
            _temporary = Path.Combine(directory, $".control-upload-{Guid.NewGuid():N}.tmp");
            _stream = new FileStream(_temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _target = target;
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.LogWarning(ex, "Could not begin upload to the configured deployment directory.");
            End(cancel: true);
            return 3;
        }
    }

    public byte Write(ReadOnlySpan<byte> data)
    {
        if (_stream is null || data.Length > options.Value.MaxUploadBytes - _stream.Length) return 4;
        try { _stream.Write(data); return 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Upload write failed.");
            return 4;
        }
    }

    public byte End(bool cancel)
    {
        if (_stream is null) return 4;
        try
        {
            if (!cancel) _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _stream = null;
            if (!cancel)
            {
                RejectLink(Path.GetDirectoryName(_target!)!);
                if (File.Exists(_target)) RejectLink(_target!);
                if (!OperatingSystem.IsWindows() && File.Exists(_target))
                    File.SetUnixFileMode(_temporary!, File.GetUnixFileMode(_target!));
                File.Move(_temporary!, _target!, overwrite: true);
                log.LogInformation("Committed uploaded file {File}.", _target);
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not commit uploaded file.");
            return 4;
        }
        finally
        {
            _stream?.Dispose();
            _stream = null;
            if (_temporary is not null)
                try { File.Delete(_temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _temporary = _target = null;
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Deployment through symbolic links or junctions is not allowed.");
    }

    public void Dispose() { if (_stream is not null) End(cancel: true); }
}
