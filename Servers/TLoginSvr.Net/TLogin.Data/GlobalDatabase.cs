using System.Data;
using Microsoft.Data.SqlClient;

namespace TLogin.Data;

/// <summary>
/// Access to the global database (DSN <c>TGLOBAL_GSP</c> → <c>TGlobal_gsp</c>): accounts, login,
/// routing, server groups/channels, nation, veteran chart and the control-server address. Stored-proc
/// signatures and SELECT text are transcribed from <c>Servers/TLoginSvr/DBAccess.h</c>; procs are
/// called positionally (see <see cref="SqlProc"/>).
/// </summary>
public sealed class GlobalDatabase : ILoginAuthenticationStore
{
    private readonly string _cs;
    public GlobalDatabase(string connectionString) => _cs = connectionString;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>TCheckIP — returns LR_BLOCK (6) if the IP is blocked, else 0. CSPCheckIP.</summary>
    public async Task<int> CheckIpAsync(string ip, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        await SqlProc.ExecAsync(c, "TCheckIP", ret, new[]
        {
            SqlProc.In("@ip", SqlDbType.VarChar, ip, Protocol.Proto.MaxName),
        }, ct);
        return ret.AsInt();
    }

    /// <summary>Deletes the account's TCURRENTUSER login lock. Used to recover from a stale lock when the
    /// same account re-logs in (returning from in-game to character-select, or re-entering) — the .NET port
    /// otherwise leaves the lock behind so TLogin returns Duplicate. Mirrors the C++ TLogout cleanup.</summary>
    public async Task ReleaseCurrentUserAsync(uint userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE FROM TCURRENTUSER WHERE dwUserID = @id", c);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = unchecked((int)userId) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>TLogin — authenticates the account and prepares the session. CSPLogin.</summary>
    public async Task<LoginRow> LoginAsync(string userId, string password, string loginIp, byte ipCheck, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var key = SqlProc.Out("@dwKEY", SqlDbType.Int);
        var charId = SqlProc.Out("@dwCharID", SqlDbType.Int);
        var uid = SqlProc.Out("@dwID", SqlDbType.Int);
        var ip = SqlProc.Out("@szIPAddr", SqlDbType.VarChar, Protocol.Proto.MaxName);
        var port = SqlProc.Out("@wPort", SqlDbType.SmallInt);
        var createCnt = SqlProc.Out("@bCreateCnt", SqlDbType.TinyInt);
        var inPcBang = SqlProc.Out("@bInPcBang", SqlDbType.TinyInt);
        var premium = SqlProc.Out("@dwPremium", SqlDbType.Int);

        await SqlProc.ExecAsync(c, "TLogin", ret, new[]
        {
            SqlProc.In("@szUserID", SqlDbType.VarChar, userId, Protocol.Proto.MaxName),
            SqlProc.In("@szPasswd", SqlDbType.VarChar, password, Protocol.Proto.MaxName),
            SqlProc.In("@szLoginIP", SqlDbType.VarChar, loginIp, Protocol.Proto.MaxName),
            SqlProc.In("@bIPCheck", SqlDbType.TinyInt, ipCheck),
            key, charId, uid, ip, port, createCnt, inPcBang, premium,
        }, ct);

        return new LoginRow(ret.AsInt(), key.AsUInt(), charId.AsUInt(), uid.AsUInt(),
            ip.AsString(), port.AsUShort(), createCnt.AsByte(), inPcBang.AsByte(), premium.AsUInt());
    }

    /// <summary>TLogin with the Japan channeling parameter. CSPLoginJP.</summary>
    public async Task<LoginRow> LoginJpAsync(string userId, string password, string loginIp, byte ipCheck, byte channeling, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var key = SqlProc.Out("@dwKEY", SqlDbType.Int);
        var charId = SqlProc.Out("@dwCharID", SqlDbType.Int);
        var uid = SqlProc.Out("@dwID", SqlDbType.Int);
        var ip = SqlProc.Out("@szIPAddr", SqlDbType.VarChar, Protocol.Proto.MaxName);
        var port = SqlProc.Out("@wPort", SqlDbType.SmallInt);
        var createCnt = SqlProc.Out("@bCreateCnt", SqlDbType.TinyInt);
        var inPcBang = SqlProc.Out("@bInPcBang", SqlDbType.TinyInt);
        var premium = SqlProc.Out("@dwPremium", SqlDbType.Int);

        await SqlProc.ExecAsync(c, "TLogin", ret, new[]
        {
            SqlProc.In("@szUserID", SqlDbType.VarChar, userId, Protocol.Proto.MaxName),
            SqlProc.In("@szPasswd", SqlDbType.VarChar, password, Protocol.Proto.MaxName),
            SqlProc.In("@szLoginIP", SqlDbType.VarChar, loginIp, Protocol.Proto.MaxName),
            SqlProc.In("@bIPCheck", SqlDbType.TinyInt, ipCheck),
            SqlProc.In("@bChanneling", SqlDbType.TinyInt, channeling),
            key, charId, uid, ip, port, createCnt, inPcBang, premium,
        }, ct);

        return new LoginRow(ret.AsInt(), key.AsUInt(), charId.AsUInt(), uid.AsUInt(),
            ip.AsString(), port.AsUShort(), createCnt.AsByte(), inPcBang.AsByte(), premium.AsUInt());
    }

    /// <summary>TCheckPasswd — verifies the account password (used before deleting a char). CSPCheckPasswd.</summary>
    public async Task<int> CheckPasswordAsync(uint userId, string password, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        await SqlProc.ExecAsync(c, "TCheckPasswd", ret, new[]
        {
            SqlProc.In("@dwID", SqlDbType.Int, unchecked((int)userId)),
            SqlProc.In("@szPasswd", SqlDbType.VarChar, password, Protocol.Proto.MaxName),
        }, ct);
        return ret.AsInt();
    }

    /// <summary>TGetNation — server nation/locale. CSPGetNation.</summary>
    public async Task<byte> GetNationAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var nation = SqlProc.Out("@bNation", SqlDbType.TinyInt);
        await SqlProc.ExecAsync(c, "TGetNation", null, new[] { nation }, ct);
        return nation.AsByte();
    }

    /// <summary>TRoute — resolve a map-server address for (group, server, type). CSPRoute.</summary>
    public async Task<RouteRow> RouteAsync(byte groupId, byte serverId, byte type, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var ip = SqlProc.Out("@szIPAddr", SqlDbType.VarChar, Protocol.Proto.MaxName);
        var port = SqlProc.Out("@wPort", SqlDbType.SmallInt);
        await SqlProc.ExecAsync(c, "TRoute", ret, new[]
        {
            SqlProc.In("@bGroupID", SqlDbType.TinyInt, groupId),
            SqlProc.In("@bServerID", SqlDbType.TinyInt, serverId),
            SqlProc.In("@bType", SqlDbType.TinyInt, type),
            ip, port,
        }, ct);
        return new RouteRow(ret.AsInt(), ip.AsString(), port.AsUShort());
    }

    /// <summary>TAgreement — records EULA acceptance. CSPAgreement (no return value).</summary>
    public async Task AgreementAsync(uint userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TAgreement", null, new[]
        {
            SqlProc.In("@dwUserID", SqlDbType.Int, unchecked((int)userId)),
        }, ct);
    }

    /// <summary>TAddMacAddress — whitelists a MAC for the account (2FA). CSPAddNewMACAddress (no return).
    /// Optional: absent in baselines without MAC-whitelist 2FA, in which case it is a no-op.</summary>
    public async Task AddMacAddressAsync(uint userId, string macAddress, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        try
        {
            await SqlProc.ExecAsync(c, "TAddMacAddress", null, new[]
            {
                SqlProc.In("@dwUserID", SqlDbType.Int, unchecked((int)userId)),
                SqlProc.In("@szAddress", SqlDbType.VarChar, macAddress, Protocol.Proto.MaxName),
            }, ct);
        }
        catch (SqlException ex) when (ex.Number == 2812)
        {
            // Proc absent in this baseline; the C++ tolerates a failed query->Call() here, so do nothing.
        }
    }

    /// <summary>TClearLoginCurrentUser — clears the login server's current-user rows. CSPClearLoginUser.</summary>
    public async Task ClearLoginUsersAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TClearLoginCurrentUser", null, Array.Empty<SqlParameter>(), ct);
    }

    /// <summary>TLoadService — looks up a service's IP/port. Used to discover the control-server address.</summary>
    public async Task<(string Ip, ushort Port)> LoadServiceAsync(byte world, byte serviceGroup, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ip = SqlProc.Out("@szIP", SqlDbType.VarChar, Protocol.Proto.MaxName);
        var port = SqlProc.Out("@wPort", SqlDbType.SmallInt);
        await SqlProc.ExecAsync(c, "TLoadService", null, new[]
        {
            SqlProc.In("@bWorld", SqlDbType.TinyInt, world),
            SqlProc.In("@bServiceGroup", SqlDbType.TinyInt, serviceGroup),
            ip, port,
        }, ct);
        return (ip.AsString(), port.AsUShort());
    }

    private const string GroupSql =
        "SELECT bGroupID, bType, szNAME, szDSN, szUserID, szPasswd FROM TGROUP WHERE bGroupID <> 0";

    /// <summary>CTBLGroup — the configured world groups (excludes the global group 0).</summary>
    public async Task<List<GroupConfig>> LoadGroupsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(GroupSql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<GroupConfig>();
        while (await r.ReadAsync(ct))
            list.Add(new GroupConfig(r.GetByteSafe(0), r.GetByteSafe(1), r.GetStringSafe(2),
                r.GetStringSafe(3), r.GetStringSafe(4), r.GetStringSafe(5)));
        return list;
    }

    private const string VeteranSql = "SELECT bID, bLevel FROM TVETERANCHART";

    /// <summary>CTBLVeteranChart — veteran level tiers (bID → option).</summary>
    public async Task<List<VeteranRow>> LoadVeteranChartAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(VeteranSql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<VeteranRow>();
        while (await r.ReadAsync(ct))
            list.Add(new VeteranRow(r.GetByteSafe(0), r.GetByteSafe(1)));
        return list;
    }

    private const string UserCountSql =
        "SELECT bWorldID, COUNT(DISTINCT dwUserID) FROM TALLCHARTABLE GROUP BY bWorldID";

    /// <summary>CTBLUserCount — distinct accounts that own a character in each world.</summary>
    public async Task<Dictionary<byte, uint>> LoadUserCountsAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(UserCountSql, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var map = new Dictionary<byte, uint>();
        while (await r.ReadAsync(ct))
            map[r.GetByteSafe(0)] = r.GetUIntSafe(1);
        return map;
    }

    private const string GroupListSql = @"
SELECT TGROUP.bGroupID, TGROUP.bType, TGROUP.szNAME, TGROUP.bStatus, TGROUP.wFull, TGROUP.wBusy,
       TGROUP.dwMaxUser, COUNT(DISTINCT TCURRENTUSER.dwKEY), COUNT(DISTINCT TALLCHARTABLE.dwCharID)
FROM TGROUP
LEFT OUTER JOIN TCURRENTUSER ON TGROUP.bGroupID = TCURRENTUSER.bGroupID
LEFT OUTER JOIN TALLCHARTABLE ON TGROUP.bGroupID = TALLCHARTABLE.bWorldID
     AND TALLCHARTABLE.dwUserID = @dwUserID AND TALLCHARTABLE.bDelete = 0
WHERE TGROUP.bStatus <> 0
GROUP BY TGROUP.bGroupID, TGROUP.bType, TGROUP.szNAME, TGROUP.bStatus, TGROUP.wFull, TGROUP.wBusy,
         TGROUP.dwMaxUser, TALLCHARTABLE.dwUserID";

    /// <summary>CTBLGroupList — group list with online counts and the user's char count per group.</summary>
    public async Task<List<GroupListRow>> GroupListAsync(uint userId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(GroupListSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwUserID", SqlDbType.Int, unchecked((int)userId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<GroupListRow>();
        while (await r.ReadAsync(ct))
            list.Add(new GroupListRow(r.GetByteSafe(0), r.GetByteSafe(1), r.GetStringSafe(2), r.GetByteSafe(3),
                r.GetUShortSafe(4), r.GetUShortSafe(5), r.GetUIntSafe(6), r.GetUIntSafe(7), r.GetByteSafe(8)));
        return list;
    }

    private const string ChannelSql = @"
SELECT TCHANNEL.bChannel, TCHANNEL.szNAME, TCHANNEL.bStatus, TCHANNEL.wFull, TCHANNEL.wBusy, COUNT(TCURRENTUSER.dwKEY)
FROM TCHANNEL
LEFT OUTER JOIN TCURRENTUSER ON TCHANNEL.bGroupID = TCURRENTUSER.bGroupID AND TCHANNEL.bChannel = TCURRENTUSER.bChannel
WHERE TCHANNEL.bStatus <> 0 AND TCHANNEL.bGroupID = @bGroupID
GROUP BY TCHANNEL.bChannel, TCHANNEL.szNAME, TCHANNEL.bStatus, TCHANNEL.wFull, TCHANNEL.wBusy";

    /// <summary>CTBLChannel — channels for a group with online counts.</summary>
    public async Task<List<ChannelRow>> ChannelListAsync(byte groupId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(ChannelSql, c);
        cmd.Parameters.Add(SqlProc.In("@bGroupID", SqlDbType.TinyInt, groupId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ChannelRow>();
        while (await r.ReadAsync(ct))
            list.Add(new ChannelRow(r.GetByteSafe(0), r.GetStringSafe(1), r.GetByteSafe(2),
                r.GetUShortSafe(3), r.GetUShortSafe(4), r.GetUIntSafe(5)));
        return list;
    }
}
