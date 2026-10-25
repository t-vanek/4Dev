namespace TControl.Server.Ops;

/// <summary>
/// Transactional local file deployment. Returns the original protocol's result codes:
/// 0 success, 1 busy, 2 unavailable machine/root, 3 invalid destination, 4 write/commit failure.
/// </summary>
public interface IFileDeploy
{
    /// <summary>Begin an upload; returns 0 on success, or a non-zero failure code.</summary>
    byte Begin(byte machineId, string fileName);
    /// <summary>Write a chunk; returns 0 on success, non-zero on failure.</summary>
    byte Write(ReadOnlySpan<byte> data);
    /// <summary>Finish (cancel=false) or abort (cancel=true) the upload; returns 0 on success.</summary>
    byte End(bool cancel);
}
