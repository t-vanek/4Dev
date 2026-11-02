using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace TMap.Data;

public sealed record StoredCharacterSave(long Sequence, string Checksum, string Payload);

public interface ICharacterSaveQueueStore : IDisposable
{
    void Open(string identity);
    void AssertLease();
    long Boundary { get; }
    int Count { get; }
    long Append(Guid operationId, string checksum, string payload);
    IReadOnlyList<StoredCharacterSave> Read(long boundary);
    void Acknowledge(long sequence);
}

/// <summary>SQL is the only durable storage. One session-owned SQL application lock protects this
/// Map save domain. No automatic reconnect/reacquisition after losing the lease is permitted.</summary>
public sealed class CharacterSaveQueueStore : ICharacterSaveQueueStore
{
    private readonly SqlConnection _connection;
    private readonly string _owner;
    private readonly string _resource;
    private readonly object _gate = new();
    private bool _opened;
    public CharacterSaveQueueStore(string connection, string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 200) throw new ArgumentException("Invalid SQL save queue owner.");
        _owner = owner.Trim();
        _resource = "4dev-character-save:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_owner)));
        _connection = new(new SqlConnectionStringBuilder(connection) { Pooling = false, ConnectRetryCount = 0, ApplicationName = _resource }.ConnectionString);
    }
    private SqlCommand Command(string sql)
    {
        var cmd = new SqlCommand(sql, _connection);
        cmd.Parameters.Add(SqlProc.In("@owner", SqlDbType.NVarChar, _owner, 200));
        cmd.Parameters.Add(SqlProc.In("@resource", SqlDbType.NVarChar, _resource, 255));
        return cmd;
    }
    public void Open(string identity)
    {
        lock (_gate)
        {
            if (_opened) throw new InvalidOperationException("SQL save queue already opened.");
            _connection.Open();
            using var cmd = Command(@"
DECLARE @ret int;
EXEC @ret=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0;
IF @ret<0 THROW 51043,'SQL save queue is already owned by another Map process',1;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveOwner WHERE OwnerKey=@owner AND IdentityHash<>@identity)
    THROW 51043,'SQL save queue belongs to another database configuration',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TCharacterSaveOwner WHERE OwnerKey=@owner)
    INSERT dbo.TCharacterSaveOwner(OwnerKey,IdentityHash) VALUES(@owner,@identity);
SELECT COUNT(*) FROM dbo.TCharacterSaveQueue WHERE OwnerKey=@owner;");
            cmd.Parameters.Add(SqlProc.In("@identity", SqlDbType.Char, identity, 64));
            cmd.ExecuteScalar(); // Required schema errors must fail startup before admission.
            _opened = true;
        }
    }
    private void CheckLease()
    {
        if (!_opened || _connection.State != ConnectionState.Open) throw new IOException("SQL save queue lease is unavailable.");
        using var cmd = Command("SELECT APPLOCK_MODE('public',@resource,'Session')");
        if (Convert.ToString(cmd.ExecuteScalar()) != "Exclusive") throw new IOException("SQL save queue lease was lost.");
    }
    public void AssertLease() { lock (_gate) CheckLease(); }
    public long Boundary
    {
        get { lock (_gate) { CheckLease(); using var cmd = Command("SELECT COALESCE(MAX(Sequence),0) FROM dbo.TCharacterSaveQueue WHERE OwnerKey=@owner"); return Convert.ToInt64(cmd.ExecuteScalar()); } }
    }
    public int Count
    {
        get { lock (_gate) { CheckLease(); using var cmd = Command("SELECT COUNT(*) FROM dbo.TCharacterSaveQueue WHERE OwnerKey=@owner"); return Convert.ToInt32(cmd.ExecuteScalar()); } }
    }
    public long Append(Guid operationId, string checksum, string payload)
    {
        lock (_gate)
        {
            CheckLease();
            using var cmd = Command(@"
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK) WHERE OwnerKey=@owner AND OperationId=@id)
BEGIN
    IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WHERE OwnerKey=@owner AND OperationId=@id AND (Checksum<>@hash OR Payload<>@payload))
        THROW 51043,'SQL save operation identity reused with different payload',1;
END
ELSE INSERT dbo.TCharacterSaveQueue(OwnerKey,OperationId,Checksum,Payload) VALUES(@owner,@id,@hash,@payload);
SELECT Sequence FROM dbo.TCharacterSaveQueue WHERE OwnerKey=@owner AND OperationId=@id;
COMMIT;");
            cmd.Parameters.Add(SqlProc.In("@id", SqlDbType.UniqueIdentifier, operationId));
            cmd.Parameters.Add(SqlProc.In("@hash", SqlDbType.Char, checksum, 64));
            cmd.Parameters.Add(SqlProc.In("@payload", SqlDbType.NVarChar, payload, -1));
            // A synchronous commit is the actor's durability boundary, replacing the old synchronous
            // file flush. Native gameplay table writes still run on the existing single DB lane.
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }
    public IReadOnlyList<StoredCharacterSave> Read(long boundary)
    {
        lock (_gate)
        {
            CheckLease();
            using var cmd = Command("SELECT Sequence,Checksum,Payload FROM dbo.TCharacterSaveQueue WHERE OwnerKey=@owner AND Sequence<=@boundary ORDER BY Sequence");
            cmd.Parameters.Add(SqlProc.In("@boundary", SqlDbType.BigInt, boundary));
            using var r = cmd.ExecuteReader();
            var rows = new List<StoredCharacterSave>();
            while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return rows;
        }
    }
    public void Acknowledge(long sequence)
    {
        lock (_gate)
        {
            CheckLease();
            using var cmd = Command("DELETE dbo.TCharacterSaveQueue WHERE OwnerKey=@owner AND Sequence=@sequence; IF @@ROWCOUNT<>1 THROW 51043,'SQL save acknowledgement missing',1;");
            cmd.Parameters.Add(SqlProc.In("@sequence", SqlDbType.BigInt, sequence));
            cmd.ExecuteNonQuery();
        }
    }
    public void Dispose() { lock (_gate) { _opened = false; _connection.Dispose(); } }
}
