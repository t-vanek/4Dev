using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>CS_SC_* wire-compatible account security code. Attempts and the 24-hour lock survive relog/restart;
/// unlocking is session-local. The C++ save calls were commented out.</summary>
public sealed partial class MapService
{
    public ISecurityCodeStore? SecurityCodeStore { get; set; }
    private readonly Dictionary<uint, SecurityCodeRow?> _securityCodes = new();

    private SecurityCodeRow? CodeFor(ClientSession s) => _securityCodes.GetValueOrDefault(s.UserId);

    private async Task<bool> LoadSecurityCode(ClientSession s)
    {
        if (_securityCodes.ContainsKey(s.UserId)) return true;
        try
        {
            _securityCodes[s.UserId] = SecurityCodeStore is { } db ? await db.LoadSecurityCodeAsync(s.UserId) : null;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Security state load failed for account {User}; refusing entry.", s.UserId);
            s.SecurityStateFailed = true;
            s.Conn.Close();
            return false;
        }
    }

    private async Task<bool> SaveCode(ClientSession s, SecurityCodeRow row)
    {
        if (SecurityCodeStore is not { } db) return false;
        try
        {
            await db.SaveSecurityCodeAsync(s.UserId, row);
            _securityCodes[s.UserId] = row;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Security state save failed for account {User}; refusing further actions.", s.UserId);
            s.SecurityUnlocked = false;
            s.SecurityStateFailed = true;
            s.IsMain = false;
            s.Conn.Close();
            return false;
        }
    }

    // The client accepts 8–12 characters, at least one digit and one special character.
    private static bool ValidSecurityCode(string code) => code.Length is >= 8 and <= 12
        && code.Any(char.IsAsciiDigit)
        && code.Any(c => !char.IsAsciiLetterOrDigit(c) && "ÜÖÄüöäß".IndexOf(c) < 0)
        && code.All(c => c >= ' ' && c <= '\u00ff');

    private static void SecurityAck(ClientSession s, ushort id, byte result)
        => s.Send(new PacketWriter(id).WriteByte(result));

    private async Task OnSecurityCode(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is null) return;
        if (!await LoadSecurityCode(s)) return;
        ushort ack = (ushort)(r.Id + 1);
        var row = CodeFor(s);
        long now = UnixNow();
        if (row is { LockedUntil: > 0 } && row.LockedUntil <= now)
        {
            row = row with { Tries = 0, LockedUntil = 0 };
            if (!await SaveCode(s, row)) return;
        }

        if (r.Id == Msg.CS_SC_GETLOCKTIME_REQ)
        {
            if (row is { LockedUntil: > 0 })
            {
                long minutes = Math.Max(0, row.LockedUntil - now) / 60;
                s.Send(new PacketWriter(ack).WriteByte((byte)(minutes / 60)).WriteByte((byte)(minutes % 60)));
            }
            else SecurityAck(s, Msg.CS_SC_UNLOCK_ACK, 0);
            return;
        }
        if (r.Id == Msg.CS_SC_CREATECODE_REQ)
        {
            string code = r.ReadString();
            if (row is not null) { SecurityAck(s, ack, 1); return; }
            if (!ValidSecurityCode(code)) { SecurityAck(s, ack, 3); return; }
            var created = new SecurityCodeRow(SecurityCodeHash.Create(code), true, 0, 0);
            if (!await SaveCode(s, created)) { SecurityAck(s, ack, 3); return; }
            s.SecurityUnlocked = true;
            SecurityAck(s, ack, 2);
            return;
        }
        if (row is null) { SecurityAck(s, Msg.CS_SC_UNLOCK_ACK, 0); return; }
        if (row.Tries >= 10) { SecurityAck(s, Msg.CS_SC_UNLOCK_ACK, 0); return; }
        if (r.Id == Msg.CS_SC_ENABLECODE_REQ)
        {
            if (await SaveCode(s, row with { Enabled = true })) SecurityAck(s, ack, 4);
            return;
        }

        string supplied = r.ReadString();
        string? replacement = r.Id == Msg.CS_SC_CHANGECODE_REQ ? r.ReadString() : null;
        bool correct = supplied.Length <= 12 && SecurityCodeHash.Verify(supplied, row.Hash);
        if (!correct)
        {
            byte tries = (byte)(row.Tries + 1);
            if (await SaveCode(s, row with { Tries = tries, LockedUntil = tries >= 10 ? now + 86400 : 0 }))
                SecurityAck(s, ack, 7);
            return;
        }
        if (replacement is not null && !ValidSecurityCode(replacement)) { SecurityAck(s, ack, 3); return; }
        var next = row with { Tries = 0, LockedUntil = 0 };
        byte result = 9;
        if (replacement is not null) { next = next with { Hash = SecurityCodeHash.Create(replacement) }; result = 5; }
        if (r.Id == Msg.CS_SC_DISABLECODE_REQ) { next = next with { Enabled = false }; result = 6; }
        if (r.Id == Msg.CS_SC_UNLOCK_REQ) result = 8;
        if (!await SaveCode(s, next)) return;
        if (r.Id != Msg.CS_SC_CHECKCODE_REQ) s.SecurityUnlocked = true;
        SecurityAck(s, ack, result);
    }

    private bool SecurityRefuses(ClientSession s, ushort id)
    {
        if (s.SecurityUnlocked || CodeFor(s) is not { Enabled: true } || !SecurityProtectedMessages.Contains(id)) return false;
        SecurityAck(s, Msg.CS_SC_UNLOCK_ACK, 0);
        if (id == Msg.CS_MOVEITEM_REQ) SendCS_MOVEITEM_ACK(s, MoveItemResult.SamePos);
        return true;
    }

    private static readonly HashSet<ushort> SecurityProtectedMessages = new()
    {
        Msg.CS_AUCTIONBID_REQ, Msg.CS_AUCTIONBUYDIRECT_REQ, Msg.CS_AUCTIONREG_REQ, Msg.CS_CABINETPUTIN_REQ,
        Msg.CS_CABINETTAKEOUT_REQ, Msg.CS_CASHCABINETBUY_REQ, Msg.CS_CASHITEMBUY_REQ, Msg.CS_CASHITEMGET_REQ,
        Msg.CS_CASHITEMPRESENT_REQ, Msg.CS_CASHITEMPUTIN_REQ, Msg.CS_CUSTOMCLOAK_CHANGE_REQ, Msg.CS_DEALITEMADD_REQ,
        Msg.CS_DEALITEMASK_REQ, Msg.CS_DEALITEMRLY_REQ, Msg.CS_DEALITEM_REQ, Msg.CS_DURATIONREP_REQ,
        Msg.CS_FRIENDERASE_REQ, Msg.CS_FRIENDGROUPDELETE_REQ, Msg.CS_GUILDARTICLEADD_REQ, Msg.CS_GUILDARTICLEDEL_REQ,
        Msg.CS_GUILDARTICLEUPDATE_REQ, Msg.CS_GUILDCABINETTAKEOUT_REQ, Msg.CS_GUILDCONTRIBUTION_REQ, Msg.CS_GUILDKICKOUT_REQ,
        Msg.CS_GUILDLEAVE_REQ, Msg.CS_GUILDPOINTREWARD_REQ, Msg.CS_GUILDTACTICSKICKOUT_REQ, Msg.CS_INVENDEL_REQ,
        Msg.CS_INVENMOVE_REQ, Msg.CS_ITEMBUY_REQ, Msg.CS_ITEMCHANGE_REQ, Msg.CS_ITEMSELL_REQ,
        Msg.CS_ITEMUPGRADE_REQ, Msg.CS_ITEMUSE_REQ, Msg.CS_MAGICITEMBUY_REQ, Msg.CS_MOVEITEM_REQ,
        Msg.CS_PETEFFECTCHANGE_REQ, Msg.CS_POSTDEL_REQ, Msg.CS_POSTGETITEM_REQ, Msg.CS_POSTRETURN_REQ,
        Msg.CS_POSTSEND_REQ, Msg.CS_REFINE_REQ, Msg.CS_REGGUILDCLOAK_REQ, Msg.CS_SKILLBUY_REQ,
        Msg.CS_SOULMATEREG_REQ, Msg.CS_STOREITEMBUY_REQ, Msg.CS_STOREOPEN_REQ, Msg.CS_USEMEDALITEM_REQ,
        Msg.CS_WARLORDSAY_REQ,
    };
}
