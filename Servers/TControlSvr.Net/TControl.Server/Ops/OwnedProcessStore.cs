using System.Text.Json;

namespace TControl.Server.Ops;

internal sealed record OwnedProcessRecord(uint ServiceId, int ProcessId, long StartedUtc,
    string Executable, string PipeName, string Token, bool Restart);

/// <summary>Private local state, never sent to a manager. One supervisor owns the directory at a time.</summary>
internal sealed class OwnedProcessStore : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _lease;

    public OwnedProcessStore(string directory)
    {
        _directory = directory;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The process-state directory cannot be a symbolic link or junction.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory)
            & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
               | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("The process-state directory must be private to its owner (mode 0700).");
        _lease = new FileStream(Path.Combine(directory, "supervisor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string FileName(uint id) => Path.Combine(_directory, $"{id:X8}.json");

    public OwnedProcessRecord? Read(uint id)
    {
        string path = FileName(id);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid process-state file.");
        var record = JsonSerializer.Deserialize<OwnedProcessRecord>(File.ReadAllBytes(path));
        if (record is null || record.ServiceId != id || record.ProcessId < 0 || string.IsNullOrWhiteSpace(record.Executable)
            || (record.ProcessId > 0 && (record.StartedUtc <= 0 || record.Token is not { Length: 64 }
                || !record.Token.All(Uri.IsHexDigit) || record.PipeName is not { Length: 37 }
                || !record.PipeName.StartsWith("4dev-", StringComparison.Ordinal))))
            throw new IOException("Invalid owned-process identity.");
        return record;
    }

    public void Write(OwnedProcessRecord record)
    {
        string temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(file, record);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, FileName(record.ServiceId), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => _lease.Dispose();
}
