using System.Data;
using Microsoft.Data.SqlClient;

namespace TMap.Data;

public sealed record MapEntry(uint UserId, uint CharId, uint Key, byte GroupId, byte Channel,
    string Ip, ushort Port);

public interface IMapEntryStore
{
    Task<bool> ValidateAsync(MapEntry entry);
    Task<bool> EnterAsync(MapEntry entry);
}

public sealed partial class GameDatabase : IMapEntryStore
{
    // Check both databases before loading or publishing any character data. The game client supplies
    // all three identifiers; the checksum proves neither account ownership nor possession of a login.
    public async Task<bool> ValidateAsync(MapEntry entry)
    {
        if (entry.UserId == 0 || entry.CharId == 0 || entry.Key == 0 || string.IsNullOrWhiteSpace(_globalCs))
            return false;
        await using var global = new SqlConnection(_globalCs);
        await global.OpenAsync();
        await using var login = new SqlCommand(
            "SELECT 1 FROM TCURRENTUSER WHERE dwUserID = @user AND dwKEY = @key", global);
        login.Parameters.Add(new SqlParameter("@user", SqlDbType.Int) { Value = unchecked((int)entry.UserId) });
        login.Parameters.Add(new SqlParameter("@key", SqlDbType.Int) { Value = unchecked((int)entry.Key) });
        if (await login.ExecuteScalarAsync() is null) return false;

        await using var game = await OpenAsync(default);
        await using var owner = new SqlCommand(
            "SELECT 1 FROM TCHARTABLE WHERE dwCharID = @char AND dwUserID = @user AND bDelete = 0", game);
        owner.Parameters.Add(new SqlParameter("@char", SqlDbType.Int) { Value = unchecked((int)entry.CharId) });
        owner.Parameters.Add(new SqlParameter("@user", SqlDbType.Int) { Value = unchecked((int)entry.UserId) });
        return await owner.ExecuteScalarAsync() is not null;
    }

    public async Task<bool> EnterAsync(MapEntry entry)
    {
        if (!await ValidateAsync(entry)) return false;
        await using var c = await OpenAsync(default);
        var ret = SqlProc.Ret();
        // Positional signature from CSPEnterServer: key, user, char, group, channel, IP, port.
        await SqlProc.ExecAsync(c, "TEnterServer", ret, new[]
        {
            SqlProc.In("@key", SqlDbType.Int, unchecked((int)entry.Key)),
            SqlProc.In("@user", SqlDbType.Int, unchecked((int)entry.UserId)),
            SqlProc.In("@char", SqlDbType.Int, unchecked((int)entry.CharId)),
            SqlProc.In("@group", SqlDbType.TinyInt, entry.GroupId),
            SqlProc.In("@channel", SqlDbType.TinyInt, entry.Channel),
            SqlProc.In("@ip", SqlDbType.VarChar, entry.Ip, 50),
            SqlProc.In("@port", SqlDbType.SmallInt, unchecked((short)entry.Port)),
        }, default);
        return ret.AsInt() == 0;
    }
}
