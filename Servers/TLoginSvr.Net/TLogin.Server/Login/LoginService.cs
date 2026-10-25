using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TLogin.Data;
using TLogin.Protocol;
using TLogin.Server.Net;
using TLogin.Server.Security;

namespace TLogin.Server.Login;

/// <summary>
/// The login protocol handlers, ported from <c>Servers/TLoginSvr/CSHandler.cpp</c> and
/// <c>CSSender.cpp</c>. Every read/write order matches the C++ exactly so the wire format is
/// byte-for-byte compatible with this repo's client.
/// </summary>
public sealed class LoginService
{
    private readonly GlobalDatabase _global;
    private readonly IReadOnlyDictionary<byte, (GroupConfig Cfg, GameDatabase Db)> _groups;
    private readonly Nation _nation;
    private readonly IHwidValidator _hwid;
    private readonly ITwoFactorValidator _twoFactor;
    private readonly IEmailSender _email;
    private readonly IReadOnlyList<VeteranRow> _veteran;
    private readonly bool _validateChecksum;
    private readonly ILogger<LoginService> _log;
    private readonly SemaphoreSlim _loginGate;

    // Distinct registered-user count per world, used only to mark a group FULL for new players
    // (mirrors CTLoginSvrModule::m_mapCurrentUser).
    private readonly ConcurrentDictionary<byte, uint> _currentUser;

    public LoginService(
        GlobalDatabase global,
        IReadOnlyDictionary<byte, (GroupConfig, GameDatabase)> groups,
        Nation nation,
        IReadOnlyList<VeteranRow> veteran,
        IReadOnlyDictionary<byte, uint> currentUser,
        IHwidValidator hwid,
        ITwoFactorValidator twoFactor,
        IEmailSender email,
        bool validateChecksum,
        ILogger<LoginService> log,
        int maxConcurrentLogins = 100)
    {
        _global = global;
        _groups = groups;
        _nation = nation;
        _veteran = veteran;
        _currentUser = new ConcurrentDictionary<byte, uint>(currentUser);
        _hwid = hwid;
        _twoFactor = twoFactor;
        _email = email;
        _validateChecksum = validateChecksum;
        _log = log;
        if (maxConcurrentLogins is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxConcurrentLogins));
        _loginGate = new SemaphoreSlim(maxConcurrentLogins, maxConcurrentLogins);
    }

    public ValueTask OnConnectedAsync(PacketConnection conn)
    {
        conn.State = new LoginSessionState();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DispatchAsync(PacketConnection conn, PacketReader r)
    {
        var st = (LoginSessionState)conn.State!;
        try
        {
            switch (r.Id)
            {
                case Msg.CS_LOGIN_REQ: await OnLoginAsync(conn, st, r); break;
                case Msg.CS_AGREEMENT_REQ: await OnAgreementAsync(conn, st, r); break;
                case Msg.CS_GROUPLIST_REQ: await OnGroupListAsync(conn, st, r); break;
                case Msg.CS_CHANNELLIST_REQ: await OnChannelListAsync(conn, st, r); break;
                case Msg.CS_CHARLIST_REQ: await OnCharListAsync(conn, st, r); break;
                case Msg.CS_CREATECHAR_REQ: await OnCreateCharAsync(conn, st, r); break;
                case Msg.CS_DELCHAR_REQ: await OnDelCharAsync(conn, st, r); break;
                case Msg.CS_START_REQ: await OnStartAsync(conn, st, r); break;
                case Msg.CS_TESTVERSION_REQ: OnTestVersion(conn); break;
                case Msg.CS_VETERAN_REQ: OnVeteran(conn); break;
                case Msg.CS_SECURITYCONFIRM_ACK: await OnSecurityConfirmAsync(conn, st, r); break;
                case Msg.CS_TERMINATE_REQ: OnTerminate(r); break;
                case Msg.CS_HOTSEND_REQ: break;     // exec checking disabled (m_hExecFile invalid)
                default:
                    _log.LogDebug("Unhandled message 0x{Id:X4} from {Endpoint}", r.Id, conn.RemoteEndPoint);
                    break;
            }
        }
        catch (OperationCanceledException) when (conn.StoppingToken.IsCancellationRequested) { /* host/connection stopping */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "Handler for 0x{Id:X4} failed (user '{User}').", r.Id, st.UserName);
        }
    }

    private static string ClientIp(PacketConnection conn) => conn.RemoteEndPoint?.Address.ToString() ?? "0.0.0.0";

    // ----- CS_LOGIN_REQ --------------------------------------------------------------------------

    private async Task OnLoginAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        ushort version = r.ReadUInt16();
        if (version != Proto.ClientVersion)
        {
            SendLoginAck(conn, st, LoginResult.Version, default);
            return;
        }

        _ = r.ReadString();                    // Zombie3
        st.Password = r.ReadString();
        _ = r.ReadString();                    // Zombie1
        _ = r.ReadString();                    // Zombie2
        st.UserName = r.ReadString();
        _ = r.ReadInt64();                     // dlCheck (exe checksum; ignored — TClient.exe not loaded)
        long checksumRecv = r.ReadInt64();
        byte channeling = 0;
        if (_nation == Nation.Japan) channeling = r.ReadByte();

        if (st.UserName.Length > Proto.MaxName || st.Password.Length > Proto.MaxName)
        {
            SendLoginAck(conn, st, LoginResult.Internal, default);
            return;
        }

        if (_validateChecksum && Proto.ComputeLoginChecksum(version) != checksumRecv)
        {
            _log.LogWarning("Login '{User}' from {Ip}: client checksum mismatch.", st.UserName, ClientIp(conn));
            return; // C++ returns EC_SESSION_INVALIDCHAR without replying
        }

        string ip = ClientIp(conn);
        string hwid = ""; // HWID/MAC not carried in this build's CS_LOGIN_REQ

        if (!_hwid.IsAllowed(st.UserName, hwid, st.MacAddress, ""))
        {
            SendLoginAck(conn, st, LoginResult.Block, default);
            return;
        }

        LoginRow login;
        var token = conn.StoppingToken;
        await _loginGate.WaitAsync(token);
        try
        {
            int ipCheck = await _global.CheckIpAsync(ip, token);
            if (ipCheck == (int)LoginResult.Block)
            {
                SendLoginAck(conn, st, LoginResult.Block, default);
                return;
            }

            st.CheckKey = unchecked(((long)(uint)Random.Shared.Next() << 32) | (uint)Random.Shared.Next());

            login = _nation == Nation.Japan
                ? await _global.LoginJpAsync(st.UserName, st.Password, ip, (byte)ipCheck, channeling, token)
                : await _global.LoginAsync(st.UserName, st.Password, ip, (byte)ipCheck, token);

            // Re-login recovery: returning from in-game to character-select (and re-entering) reconnects and
            // re-runs TLogin, which still finds this account's TCURRENTUSER lock and returns Duplicate. Since
            // it's the same account reconnecting, kick the stale lock and retry once (single-world semantics).
            if ((LoginResult)login.Ret == LoginResult.Duplicate && login.UserId != 0)
            {
                await _global.ReleaseCurrentUserAsync(login.UserId, token);
                login = _nation == Nation.Japan
                    ? await _global.LoginJpAsync(st.UserName, st.Password, ip, (byte)ipCheck, channeling, token)
                    : await _global.LoginAsync(st.UserName, st.Password, ip, (byte)ipCheck, token);
            }
        }
        finally { _loginGate.Release(); }

        var result = (LoginResult)login.Ret;

        // 2FA is wired but auto-passing: a Security result on a trusted device becomes Success.
        if (result == LoginResult.Security && _twoFactor.IsTrusted(st.UserName, hwid, st.MacAddress, ""))
            result = LoginResult.Success;

        SendLoginAck(conn, st, result, login);

        switch (result)
        {
            case LoginResult.NeedAgreement:
                st.Agreement = false;
                Register(st, login);
                break;
            case LoginResult.Success:
                st.Agreement = true;
                Register(st, login);
                break;
            default:
                _log.LogInformation("Login '{User}' from {Ip} => {Result}", st.UserName, ip, result);
                break;
        }
    }

    private static void Register(LoginSessionState st, LoginRow login)
    {
        st.UserId = login.UserId;
        st.CreateCount = login.CreateCnt;
    }

    private void SendLoginAck(PacketConnection conn, LoginSessionState st, LoginResult result, LoginRow? login)
    {
        var w = new PacketWriter(Msg.CS_LOGIN_ACK);
        w.WriteByte((byte)result);
        w.WriteUInt32(login?.UserId ?? 0);
        w.WriteUInt32(login?.CharId ?? 0);
        w.WriteUInt32(login?.Key ?? 0);
        w.WriteUInt32(login is null ? 0 : Proto.IpToUInt(login.MapIp));
        w.WriteUInt16(login?.Port ?? 0);
        w.WriteByte(login?.CreateCnt ?? 0);
        w.WriteByte(login?.InPcBang ?? 0);
        w.WriteUInt32(login?.Premium ?? 0);
        w.WriteInt64(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // dCurTime (__time64_t)
        w.WriteInt64(st.CheckKey);                               // m_dlCheckKey
        conn.Send(w);
    }

    // ----- CS_AGREEMENT_REQ ----------------------------------------------------------------------

    private async Task OnAgreementAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        st.Agreement = true;
        await _global.AgreementAsync(st.UserId);
    }

    // ----- CS_GROUPLIST_REQ ----------------------------------------------------------------------

    private async Task OnGroupListAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        if (!st.Agreement) return;

        var rows = await _global.GroupListAsync(st.UserId);

        var w = new PacketWriter(Msg.CS_GROUPLIST_ACK);
        w.WriteByte((byte)rows.Count);    // bCount (C++ patches this in; same effect)
        w.WriteUInt32(0);                 // GetCheckFilePoint (0 — exec checking disabled)

        foreach (var g in rows)
        {
            byte status = ComputeStatus(g.Status, g.CurrentUsers, g.Full, g.Busy);
            if (_groups.ContainsKey(g.GroupId) &&
                status != (byte)ServerStatus.Sleep &&
                status != (byte)ServerStatus.Full &&
                g.MaxUser > 0 &&
                g.CharCount == 0 &&
                GetCurrentUser(g.GroupId) >= g.MaxUser)
            {
                status = (byte)ServerStatus.Full;
            }

            w.WriteString(g.Name);
            w.WriteByte(g.GroupId);
            w.WriteByte(g.Type);
            w.WriteByte(status);
            w.WriteByte(g.CharCount);
        }

        conn.Send(w);
    }

    // ----- CS_CHANNELLIST_REQ --------------------------------------------------------------------

    private async Task OnChannelListAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        if (!st.Agreement) return;

        byte groupId = r.ReadByte();
        var rows = await _global.ChannelListAsync(groupId);

        var w = new PacketWriter(Msg.CS_CHANNELLIST_ACK);
        w.WriteByte((byte)rows.Count);    // bCount
        w.WriteUInt32(0);                 // GetCheckFilePoint

        foreach (var ch in rows)
        {
            w.WriteString(ch.Name);
            w.WriteByte(ch.Channel);
            w.WriteByte(ComputeStatus(ch.Status, ch.Count, ch.Full, ch.Busy));
        }

        conn.Send(w);
    }

    // ----- CS_CHARLIST_REQ -----------------------------------------------------------------------

    private async Task OnCharListAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        if (!st.Agreement) return;

        st.GroupId = r.ReadByte();

        var chars = new List<CharRow>();
        uint bowCharId = 0;

        if (_groups.TryGetValue(st.GroupId, out var g))
        {
            var game = g.Db;
            bowCharId = await game.FindBowPlayerAsync(st.UserId);
            if (bowCharId == 0) bowCharId = await game.FindBrPlayerAsync(st.UserId);

            chars = await game.CharListWithAppearanceAsync(st.UserId);
        }

        var w = new PacketWriter(Msg.CS_CHARLIST_ACK);
        w.WriteUInt32(0);                 // GetCheckFilePoint (written first here, then count)
        w.WriteByte((byte)chars.Count);

        foreach (var ch in chars)
        {
            w.WriteUInt32(ch.CharId);
            w.WriteString(ch.Name);
            w.WriteByte(ch.StartAct);
            w.WriteByte(ch.Slot);
            w.WriteByte(ch.Level);
            w.WriteByte(ch.Class);
            w.WriteByte(ch.Race);
            w.WriteByte(ch.Country);
            w.WriteByte(ch.Sex);
            w.WriteByte(ch.Hair);
            w.WriteByte(ch.Face);
            w.WriteByte(ch.Body);
            w.WriteByte(ch.Pants);
            w.WriteByte(ch.Hand);
            w.WriteByte(ch.Foot);
            w.WriteUInt32(ch.Region);
            w.WriteUInt32(ch.Fame);
            w.WriteUInt32(ch.FameColor);
            w.WriteByte(ch.HelmetHide);
            w.WriteByte((byte)ch.Items.Count);
            foreach (var it in ch.Items)
            {
                w.WriteByte(it.ItemId);
                w.WriteUInt16(it.ItemTemplateId);
                w.WriteByte(it.Level);
                w.WriteByte(it.GradeEffect);
                w.WriteUInt16(it.Color);
                w.WriteUInt16(it.CustomTex);
                w.WriteByte(it.RegGuild);
                w.WriteUInt16(it.MoggItemId);
            }
        }

        conn.Send(w);

        if (bowCharId != 0)
        {
            var bow = chars.FirstOrDefault(c => c.CharId == bowCharId);
            if (bow is not null)
            {
                var n = new PacketWriter(Msg.CS_BOWPLAYERNOTIFY_ACK);
                n.WriteByte(bow.Slot);
                conn.Send(n);
            }
        }
    }

    // ----- CS_CREATECHAR_REQ ---------------------------------------------------------------------

    private async Task OnCreateCharAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        if (!st.Agreement) return;

        byte groupId = r.ReadByte();
        string name = r.ReadString();
        byte slot = r.ReadByte();
        byte cls = r.ReadByte();
        byte race = r.ReadByte();
        byte country = r.ReadByte();
        byte sex = r.ReadByte();
        byte hair = r.ReadByte();
        byte face = r.ReadByte();
        byte body = r.ReadByte();
        byte pants = r.ReadByte();
        byte hand = r.ReadByte();
        byte foot = r.ReadByte();
        byte levelOption = r.ReadByte();

        if (groupId != st.GroupId) return; // C++ returns EC_SESSION_INVALIDCHAR silently

        void Ack(CreateResult result, uint charId, byte createCnt, byte level) =>
            SendCreateCharAck(conn, result, charId, name, slot, cls, race, country, sex, hair, face,
                body, pants, hand, foot, createCnt, level);

        if (!_groups.TryGetValue(groupId, out var g))
        {
            Ack(CreateResult.NoGroup, 0, st.CreateCount, 0);
            return;
        }

        if (name.Length is > 16 or < 3)
        {
            Ack(CreateResult.OverChar, 0, st.CreateCount, 0);
            return;
        }

        if (!CheckCharName(name))
        {
            Ack(CreateResult.Protected, 0, st.CreateCount, 0);
            return;
        }

        byte level = 0;
        foreach (var v in _veteran)
            if (v.Option == levelOption) level = v.Level;

        CreateCharRow row;
        try
        {
            row = await g.Db.CreateCharAsync(new CreateCharArgs(name, st.UserId, groupId, slot, cls, race,
                country, sex, hair, face, body, pants, hand, foot, levelOption));
        }
        catch (Exception ex)
        {
            // Always answer: without a CS_CREATECHAR_ACK the client hangs on the create screen.
            _log.LogError(ex, "CreateChar '{Name}' by user {User} failed in TCreateChar.", name, st.UserId);
            Ack(CreateResult.Internal, 0, st.CreateCount, 0);
            return;
        }

        if (row.Ret == 0)
        {
            st.CreateCount = row.CreateCnt;
            if (row.CreateCnt == 1)
                _currentUser.AddOrUpdate(groupId, 1, (_, v) => v + 1);
        }

        Ack((CreateResult)row.Ret, row.CharId, row.CreateCnt, level);
        _log.LogInformation("CreateChar '{Name}' by user {User} => ret={Ret} charId={Char}", name, st.UserId, row.Ret, row.CharId);
    }

    private static void SendCreateCharAck(PacketConnection conn, CreateResult result, uint charId, string name,
        byte slot, byte cls, byte race, byte country, byte sex, byte hair, byte face, byte body, byte pants,
        byte hand, byte foot, byte createCnt, byte level)
    {
        var w = new PacketWriter(Msg.CS_CREATECHAR_ACK);
        w.WriteByte((byte)result);
        w.WriteUInt32(charId);
        w.WriteString(name);
        w.WriteByte(slot);
        w.WriteByte(cls);
        w.WriteByte(race);
        w.WriteByte(country);
        w.WriteByte(sex);
        w.WriteByte(hair);
        w.WriteByte(face);
        w.WriteByte(body);
        w.WriteByte(pants);
        w.WriteByte(hand);
        w.WriteByte(foot);
        w.WriteByte(createCnt);
        w.WriteByte(level);
        conn.Send(w);
    }

    // ----- CS_DELCHAR_REQ ------------------------------------------------------------------------

    private async Task OnDelCharAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        if (st.UserId == 0) return;

        byte groupId = r.ReadByte();
        string password = r.ReadString();
        uint charId = r.ReadUInt32();

        if (groupId != st.GroupId) return;

        if (!_groups.TryGetValue(groupId, out var g))
        {
            SendDelCharAck(conn, DeleteResult.NoGroup, charId);
            return;
        }

        if (password.Length > Proto.MaxName)
        {
            SendDelCharAck(conn, DeleteResult.InvalidPasswd, charId);
            return;
        }

        int pwCheck = await _global.CheckPasswordAsync(st.UserId, password);
        if (pwCheck != 0)
        {
            SendDelCharAck(conn, (DeleteResult)pwCheck, charId);
            return;
        }

        var del = await g.Db.DeleteCharAsync(groupId, st.UserId, charId);
        if (del.Ret != 0)
        {
            SendDelCharAck(conn, DeleteResult.Guild, charId);
            return;
        }

        SendDelCharAck(conn, DeleteResult.Success, charId);
        if (del.CreateCnt == 0 && _currentUser.TryGetValue(groupId, out var cur) && cur > 0)
            _currentUser.AddOrUpdate(groupId, 0, (_, v) => v > 0 ? v - 1 : 0);

        _log.LogInformation("DeleteChar {Char} by user {User} => ret={Ret}", charId, st.UserId, del.Ret);
    }

    private static void SendDelCharAck(PacketConnection conn, DeleteResult result, uint charId)
    {
        var w = new PacketWriter(Msg.CS_DELCHAR_ACK);
        w.WriteByte((byte)result);
        w.WriteUInt32(charId);
        conn.Send(w);
    }

    // ----- CS_START_REQ --------------------------------------------------------------------------

    private async Task OnStartAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        if (!st.Agreement) return;

        byte groupId = r.ReadByte();
        byte channel = r.ReadByte();
        uint charId = r.ReadUInt32();

        if (!_groups.TryGetValue(groupId, out var g))
        {
            SendStartAck(conn, StartResult.NoGroup, 0, 0, 0);
            return;
        }

        var find = await g.Db.FindServerIdAsync(charId, channel);
        if (find.Ret != 0)
        {
            SendStartAck(conn, (StartResult)find.Ret, 0, 0, 0);
            return;
        }

        byte serverId = find.ServerId;

        if (await g.Db.FindBowPlayerAsync(st.UserId) == charId)
            serverId = Proto.BowServerId;
        if (await g.Db.FindBrPlayerAsync(st.UserId) == charId)
            serverId = Proto.BrServerId;

        var route = await _global.RouteAsync(groupId, serverId, Proto.SvrGrpMapSvr);
        if (route.Ret != 0)
        {
            SendStartAck(conn, (StartResult)route.Ret, 0, 0, 0);
            return;
        }

        SendStartAck(conn, StartResult.Success, Proto.IpToUInt(route.Ip), route.Port, serverId);
        _log.LogInformation("Start: user {User} char {Char} => server {Srv} @ {Ip}:{Port}",
            st.UserId, charId, serverId, route.Ip, route.Port);
    }

    private static void SendStartAck(PacketConnection conn, StartResult result, uint mapIp, ushort port, byte serverId)
    {
        var w = new PacketWriter(Msg.CS_START_ACK);
        w.WriteByte((byte)result);
        w.WriteUInt32(mapIp);
        w.WriteUInt16(port);
        w.WriteByte(serverId);
        conn.Send(w);
    }

    // ----- misc ----------------------------------------------------------------------------------

    private void OnTestVersion(PacketConnection conn)
    {
        var w = new PacketWriter(Msg.CS_TESTVERSION_ACK);
        w.WriteUInt16(Proto.ClientVersion);
        conn.Send(w);
    }

    private void OnVeteran(PacketConnection conn)
    {
        byte L(int i) => i < _veteran.Count ? _veteran[i].Level : (byte)0;
        var w = new PacketWriter(Msg.CS_VETERAN_ACK);
        w.WriteByte(3);          // option = 3 (all tiers available)
        w.WriteByte(L(0));
        w.WriteByte(L(1));
        w.WriteByte(L(2));
        conn.Send(w);
    }

    private static void OnTerminate(PacketReader r)
    {
        _ = r.ReadUInt32(); // dwKey; C++ only validates it against a constant and does nothing else
    }

    private async Task OnSecurityConfirmAsync(PacketConnection conn, LoginSessionState st, PacketReader r)
    {
        string code = r.ReadString();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(st.SecurityCode) || st.UserId == 0)
            return;

        if (string.Equals(code, st.SecurityCode, StringComparison.OrdinalIgnoreCase))
        {
            await _global.AddMacAddressAsync(st.UserId, st.MacAddress);
            SendSecurityResult(conn, SecurityCode.Correct);
            st.SecurityCode = "";
        }
        else
        {
            SendSecurityResult(conn, SecurityCode.Incorrect);
        }
    }

    private static void SendSecurityResult(PacketConnection conn, SecurityCode code)
    {
        var w = new PacketWriter(Msg.CS_SECURITYRESULT_ACK);
        w.WriteByte((byte)code);
        conn.Send(w);
    }

    // ----- helpers -------------------------------------------------------------------------------

    private uint GetCurrentUser(byte groupId) => _currentUser.TryGetValue(groupId, out var v) ? v : 0;

    /// <summary>Maps a raw DB status + counts to the TSTATUS value sent to the client.</summary>
    private static byte ComputeStatus(byte dbStatus, uint count, ushort full, ushort busy)
    {
        if (dbStatus == (byte)SvrStatus.Sleep) return (byte)ServerStatus.Sleep;
        if (count > full) return (byte)ServerStatus.Full;
        if (count > busy) return (byte)ServerStatus.Busy;
        return (byte)ServerStatus.Normal;
    }

    /// <summary>Validates a character name per nation, mirroring CTLoginSvrModule::CheckCharName.</summary>
    private bool CheckCharName(string name)
    {
        if (name.Length is < 1 or > 16) return false;

        // Taiwan/Japan/Korea use multibyte rules the C++ accepts permissively here.
        if (_nation is Nation.Taiwan or Nation.Japan or Nation.Korea) return true;

        foreach (char ch in name)
        {
            bool ok = ch is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';
            if (!ok && _nation == Nation.German && "ÜÖÄüöäß".IndexOf(ch) >= 0)
                ok = true; // ÜÖÄüöäß (Latin-1 0xDC,0xD6,0xC4,0xFC,0xF6,0xE4,0xDF)
            if (!ok) return false;
        }
        return true;
    }
}
