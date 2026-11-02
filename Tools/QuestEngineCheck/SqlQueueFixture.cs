using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

internal static class SqlQueueFixture
{
    public static async Task<long> Pending(string connection, string owner)
    {
        await using var db = new SqlConnection(connection); await db.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.TCharacterSaveQueue WHERE OwnerKey=@owner", db);
        cmd.Parameters.AddWithValue("@owner", owner);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    public static async Task ApplyMigration(string connection, string path, bool sqlcmdDefaults = false)
    {
        var b = new SqlConnectionStringBuilder(connection);
        if (!(b.InitialCatalog.StartsWith("4DevQuestTest_", StringComparison.Ordinal) ||
            (b.InitialCatalog == "TGame_gsp" && b.DataSource.StartsWith("4dev-full-save-sql-", StringComparison.Ordinal))))
            throw new InvalidOperationException("Isolated SQL fixture required");
        var sql = await File.ReadAllTextAsync(path);
        sql = sql.Replace("USE [TGame_gsp];", "USE [" + b.InitialCatalog.Replace("]", "]]") + "];");
        await using var db = new SqlConnection(connection); await db.OpenAsync();
        for (int pass = 0; pass < 2; pass++)
        {
            if (sqlcmdDefaults)
            {
                await using var defaults = new SqlCommand("SET QUOTED_IDENTIFIER OFF;", db);
                await defaults.ExecuteNonQueryAsync();
            }
            foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) { await using var cmd = new SqlCommand(batch, db); await cmd.ExecuteNonQueryAsync(); }
        }
    }
}
