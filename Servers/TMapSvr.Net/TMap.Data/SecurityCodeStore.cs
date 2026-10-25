using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace TMap.Data;

public sealed record SecurityCodeRow(string Hash, bool Enabled, byte Tries, long LockedUntil);

public interface ISecurityCodeStore
{
    Task<SecurityCodeRow?> LoadSecurityCodeAsync(uint userId);
    Task SaveSecurityCodeAsync(uint userId, SecurityCodeRow code);
}

/// <summary>Only salted derivations are written; the original plaintext column is cleared on the next save.</summary>
public static class SecurityCodeHash
{
    private const int Iterations = 100_000;
    public static string Create(string code)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(code, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string code, string encoded)
    {
        string[] parts = encoded.Split(':');
        if (parts.Length != 3 || parts[0] != "1") return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]), expected = Convert.FromBase64String(parts[2]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(code, salt, Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

public sealed partial class GameDatabase : ISecurityCodeStore
{
    private async Task<SqlConnection> OpenSecurityDatabase()
    {
        string cs = string.IsNullOrWhiteSpace(_globalCs)
            ? new SqlConnectionStringBuilder(_cs) { InitialCatalog = "TGlobal_gsp" }.ConnectionString : _globalCs;
        var connection = new SqlConnection(cs);
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<SecurityCodeRow?> LoadSecurityCodeAsync(uint userId)
    {
        await using var c = await OpenSecurityDatabase();
        await using var cmd = new SqlCommand(@"SELECT strCodeHash, strSecurityCode, bEnabled, bTries, iLockUntilUnix
            FROM dbo.TSECURECODE WHERE dwUserID = @u", c);
        cmd.Parameters.Add(SqlProc.In("@u", SqlDbType.Int, unchecked((int)userId)));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        string hash = r.IsDBNull(0) ? "" : r.GetString(0);
        if (hash.Length == 0)
        {
            string legacy = r.GetStringSafe(1);
            if (legacy.Length == 0) throw new InvalidDataException("Security code has no credential.");
            hash = SecurityCodeHash.Create(legacy);
        }
        return new SecurityCodeRow(hash, r.GetInt32(2) != 0, (byte)Math.Clamp(r.GetInt32(3), 0, 10), r.GetInt64(4));
    }

    public async Task SaveSecurityCodeAsync(uint userId, SecurityCodeRow code)
    {
        await using var c = await OpenSecurityDatabase();
        await using var cmd = new SqlCommand(@"SET XACT_ABORT ON;
            BEGIN TRAN;
            UPDATE dbo.TSECURECODE WITH (UPDLOCK, SERIALIZABLE)
              SET strSecurityCode = '', strCodeHash = @hash, bEnabled = @enabled, bTries = @tries,
                  iLockTick = 0, iLockUntilUnix = @until WHERE dwUserID = @u;
            IF @@ROWCOUNT = 0 INSERT dbo.TSECURECODE
              (dwUserID, strSecurityCode, strCodeHash, bEnabled, bTries, iLockTick, iLockUntilUnix)
              VALUES (@u, '', @hash, @enabled, @tries, 0, @until);
            COMMIT;", c);
        cmd.Parameters.AddRange(new[] {
            SqlProc.In("@u", SqlDbType.Int, unchecked((int)userId)),
            SqlProc.In("@hash", SqlDbType.VarChar, code.Hash, 100),
            SqlProc.In("@enabled", SqlDbType.Int, code.Enabled ? 1 : 0),
            SqlProc.In("@tries", SqlDbType.Int, (int)code.Tries),
            SqlProc.In("@until", SqlDbType.BigInt, code.LockedUntil),
        });
        await cmd.ExecuteNonQueryAsync();
    }
}
