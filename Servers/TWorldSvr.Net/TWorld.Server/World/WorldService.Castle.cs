using TWorld.Data;
using TWorld.Protocol;
using TWorld.Server.Net;

namespace TWorld.Server.World;

/// <summary>
/// Phase 5c — castle / territory-war <em>ownership broadcasts</em>, ported from <c>SSHandler.cpp</c>
/// (OnMW_CASTLEOCCUPY/LOCALOCCUPY/MISSIONOCCUPY/SKYGARDENOCCUPY/ENDWAR) + <c>SSSender.cpp</c>. When a map
/// reports that a territory changed hands, the world awards the capturing guild its stat-exp and fans the
/// new ownership out to every connected map so all clients render it. The scoreboard, scheduler and
/// PvP-point paths live in the adjacent partials. Applicant changes wait for SQL before publishing success.
/// Guild-stat saves and distributed result/reward replay still need a durable settlement boundary.
/// </summary>
public sealed partial class WorldService
{
    public ICastleApplicantStore? CastleApplicantStore { get; set; }

    private async Task<bool> DispatchCastleAsync(ServerSession session, PacketReader r, byte[] packet)
    {
        switch (r.Id)
        {
            case Msg.MW_CASTLEAPPLY_ACK: await OnMW_CASTLEAPPLY_ACK(session, r); return true;
            case Msg.MW_CASTLEWARINFO_ACK: OnMW_CASTLEWARINFO_ACK(session, r); return true;
            case Msg.MW_CASTLEOCCUPY_ACK: await OnMW_CASTLEOCCUPY_ACK(r); return true;
            case Msg.MW_LOCALOCCUPY_ACK: await OnMW_LOCALOCCUPY_ACK(r); return true;
            case Msg.MW_MISSIONOCCUPY_ACK: OnMW_MISSIONOCCUPY_ACK(r); return true;
            case Msg.MW_SKYGARDENOCCUPY_ACK: OnMW_SKYGARDENOCCUPY_ACK(r); return true;
            case Msg.MW_ENDWAR_ACK: OnMW_ENDWAR_ACK(r); return true;
        }
        return false;
    }

    /// <summary>A castle was captured: award the winner stat-exp and broadcast the new ownership. C++ OnMW_CASTLEOCCUPY_ACK.</summary>
    private async Task OnMW_CASTLEOCCUPY_ACK(PacketReader r)
    {
        byte type = r.ReadByte();
        ushort castleId = r.ReadUInt16();
        uint guildId = r.ReadUInt32();
        byte country = r.ReadByte();
        uint loseGuildId = r.ReadUInt32();

        var guild = _state.FindGuild(guildId);
        if (guild is not null)
        {
            ResetCastleApply(guild, castleId);
            guild.StatExp += Proto.CastleOccupyStatExp;
            await SaveGuildStats(guild);
        }
        if (_state.FindGuild(loseGuildId) is { } lose) ResetCastleApply(lose, castleId);

        var w = new PacketWriter(Msg.MW_CASTLEOCCUPY_REQ);
        w.WriteByte(type); w.WriteUInt16(castleId); w.WriteUInt32(guildId); w.WriteByte(country);
        w.WriteString(guild?.Name ?? "");
        BroadcastServers(w.ToArray());
    }

    /// <summary>A local territory was captured. Broa (the neutral/raid nation) doesn't hold locals as a guild,
    /// so its capture flips the displayed country and clears the guild id. C++ OnMW_LOCALOCCUPY_ACK.</summary>
    private async Task OnMW_LOCALOCCUPY_ACK(PacketReader r)
    {
        byte type = r.ReadByte();
        ushort localId = r.ReadUInt16();
        byte country = r.ReadByte();
        uint guildId = r.ReadUInt32();
        byte curCountry = r.ReadByte();

        var guild = _state.FindGuild(guildId);
        if (guild is not null)
        {
            guild.StatExp += Proto.LocalOccupyStatExp;
            await SaveGuildStats(guild);
        }

        if (guild is not null && guild.Country == (byte)Contry.Broa)
        {
            if (curCountry != (byte)Contry.None) country = (byte)(country == 0 ? 1 : 0);
            guildId = 0;
        }

        var w = new PacketWriter(Msg.MW_LOCALOCCUPY_REQ);
        w.WriteByte(type); w.WriteUInt16(localId); w.WriteByte(country); w.WriteUInt32(guildId);
        w.WriteString(guild?.Name ?? "");
        BroadcastServers(w.ToArray());
    }

    /// <summary>A mission territory changed hands: broadcast (no guild ownership). C++ OnMW_MISSIONOCCUPY_ACK.</summary>
    private void OnMW_MISSIONOCCUPY_ACK(PacketReader r)
    {
        byte type = r.ReadByte();
        ushort localId = r.ReadUInt16();
        byte country = r.ReadByte();

        var w = new PacketWriter(Msg.MW_MISSIONOCCUPY_REQ);
        w.WriteByte(type); w.WriteUInt16(localId); w.WriteByte(country);
        BroadcastServers(w.ToArray());
    }

    /// <summary>A sky-garden changed hands: broadcast. C++ OnMW_SKYGARDENOCCUPY_ACK (#ifdef SKYGARDEN).</summary>
    private void OnMW_SKYGARDENOCCUPY_ACK(PacketReader r)
    {
        byte type = r.ReadByte();
        ushort id = r.ReadUInt16();
        byte country = r.ReadByte();

        var w = new PacketWriter(Msg.MW_SKYGARDENOCCUPY_REQ);
        w.WriteByte(type); w.WriteUInt16(id); w.WriteByte(country);
        BroadcastServers(w.ToArray());
    }

    /// <summary>A castle war ended: tell every map. C++ OnMW_ENDWAR_ACK.</summary>
    private void OnMW_ENDWAR_ACK(PacketReader r)
    {
        ushort castleId = r.ReadUInt16();
        var w = new PacketWriter(Msg.MW_ENDWAR_REQ);
        w.WriteUInt16(castleId);
        BroadcastServers(w.ToArray());
    }

    /// <summary>A guild chief assigns one of its members (or a tactics mercenary) to a castle slot with a
    /// camp — or clears it by re-applying to the same castle. Validates the per-guild slot cap, echoes the
    /// result to the chief and (if different) the assigned char, and broadcasts the new applicant count.
    /// C++ OnMW_CASTLEAPPLY_ACK.</summary>
    private async Task OnMW_CASTLEAPPLY_ACK(ServerSession main, PacketReader r)
    {
        uint charId = r.ReadUInt32();
        uint key = r.ReadUInt32();
        ushort castle = r.ReadUInt16();
        uint target = r.ReadUInt32();
        byte camp = r.ReadByte();

        var ch = _state.FindChar(charId, key);
        if (ch?.Guild is null || ch.Guild.Chief != charId) return;   // only the chief may assign
        var guild = ch.Guild;

        Character? charTarget;
        ushort prevCastle; byte prevCamp;
        var targetMem = guild.FindMember(target);
        TacticsMember? targetTac = null;
        if (targetMem is null)
        {
            targetTac = guild.FindTactics(target);
            if (targetTac is null) return;
            charTarget = targetTac.OnlineChar; prevCastle = targetTac.Castle; prevCamp = targetTac.Camp;
        }
        else
        {
            if (targetMem.Tactics != 0) return;       // a member loaned out as a tactic can't be assigned here
            charTarget = targetMem.OnlineChar; prevCastle = targetMem.Castle; prevCamp = targetMem.Camp;
        }
        _ = prevCamp;

        if (prevCastle == castle) { castle = 0; camp = 0; }   // re-applying to the same castle clears it

        if (castle != 0 && !guild.CanApplyWar(castle))
        {
            main.Send(BuildCastleApplyReq(charId, key, CastleApplyResult.Full, castle, target, camp));
            return;
        }

        if (prevCastle != 0 || castle != 0)
        {
            // Previously the success packet and in-memory slot preceded an unobserved SQL Task.
            // Keep the old slot if SQL rejects or disconnects; DispatchAsync logs the failure.
            if (CastleApplicantStore is { } store) await store.SaveCastleApplicantAsync(castle, target, camp);
            if (targetMem is not null) { targetMem.Castle = castle; targetMem.Camp = camp; }
            else { targetTac!.Castle = castle; targetTac.Camp = camp; }

            main.Send(BuildCastleApplyReq(charId, key, CastleApplyResult.Success, castle, target, camp));

            if (charId != target && charTarget is not null)
                _state.FindMapSvr(charTarget.MainId)?.Send(
                    BuildCastleApplyReq(target, charTarget.Key, CastleApplyResult.Success, castle, target, camp));

            if (prevCastle != 0) NotifyCastleApply(prevCastle, guild);
            if (castle != 0) NotifyCastleApply(castle, guild);
        }
    }

    /// <summary>Clear every member/tactics slot applied to <paramref name="castle"/> and tell each online one.
    /// C++ ResetCastleApply (used on capture and when a member transfers out).</summary>
    private void ResetCastleApply(Guild guild, ushort castle)
    {
        foreach (var m in guild.Members.Values)
        {
            if (m.Castle != castle) continue;
            m.Castle = 0; m.Camp = 0;
            if (m.OnlineChar is { } c)
                _state.FindMapSvr(c.MainId)?.Send(BuildCastleApplyReq(c.CharId, c.Key, CastleApplyResult.Success, 0, c.CharId, 0));
        }
        foreach (var t in guild.Tactics.Values)
        {
            if (t.Castle != castle) continue;
            t.Castle = 0; t.Camp = 0;
            if (t.OnlineChar is { } c)
                _state.FindMapSvr(c.MainId)?.Send(BuildCastleApplyReq(c.CharId, c.Key, CastleApplyResult.Success, 0, c.CharId, 0));
        }
    }

    /// <summary>Broadcast a guild's current applicant count for a castle to every map. C++ NotifyCastleApply.</summary>
    private void NotifyCastleApply(ushort castle, Guild guild)
    {
        var (count, camp) = guild.GetCastleApplicantCount(castle);
        var w = new PacketWriter(Msg.MW_CASTLEAPPLICANTCOUNT_REQ);
        w.WriteUInt16(castle); w.WriteUInt32(guild.Id); w.WriteByte(camp); w.WriteByte(count);
        BroadcastServers(w.ToArray());
    }

    private static byte[] BuildCastleApplyReq(uint charId, uint key, CastleApplyResult result, ushort castle, uint target, byte camp)
    {
        var w = new PacketWriter(Msg.MW_CASTLEAPPLY_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key); w.WriteByte((byte)result);
        w.WriteUInt16(castle); w.WriteUInt32(target); w.WriteByte(camp);
        return w.ToArray();
    }

    /// <summary>C++ SaveGuildStats — roll over to the next stat level when exp crosses the threshold, then
    /// persist (TSaveGuildStats). No-op on persistence in DB-free tests. A failed save is logged and the caller goes on, as
    /// the C++ DB thread's would (a capture must still be relayed to the maps).</summary>
    private async Task SaveGuildStats(Guild guild)
    {
        if (guild.StatExp >= (uint)guild.StatLevel * Proto.GuildStatExpPerLevel)
        {
            guild.StatLevel++;
            guild.StatPoint++;
            guild.StatExp = 0;
        }
        if (_guildDb is null) return;
        try { await _guildDb.SaveGuildStatsAsync(guild.Id, guild.StatPoint, guild.StatLevel, guild.StatExp); }
        catch (Exception ex) { _log.LogWarning(ex, "TSaveGuildStats failed for guild {Guild}.", guild.Id); }
    }

    private void BroadcastServers(byte[] packet)
    {
        foreach (var s in _state.Servers.Values) s.Send(packet);
    }
}
