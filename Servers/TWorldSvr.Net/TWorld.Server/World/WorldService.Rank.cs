using Microsoft.Extensions.Logging;
using TWorld.Data;
using TWorld.Protocol;

namespace TWorld.Server.World;

/// <summary>
/// Phase 3 — PvP rankings + the monthly rollover, ported from <c>SSHandler.cpp</c>
/// (OnMW_FAMERANKUPDATE/MONTHRANKUPDATE/WARLORDSAY/MONTHRANKRESETCHAR, OnSM/DM_MONTHRANKSAVE) +
/// <c>SSSender.cpp</c> (SendMW_MONTHRANK*/FAMERANK*/WARLORDSAY/FIRSTGRADEGROUP) + the OnTimer rollover
/// check. The C++ DB-thread save round-trip is folded into one best-effort <see cref="OnTimerAsync"/> pass.
/// </summary>
public sealed partial class WorldService
{
    private bool DispatchRank(ServerSession session, PacketReader r)
    {
        switch (r.Id)
        {
            case Msg.MW_FAMERANKUPDATE_ACK: OnFameRankUpdate(r); return true;
            case Msg.MW_MONTHRANKUPDATE_ACK: OnMonthRankUpdate(r); return true;
            case Msg.MW_WARLORDSAY_ACK: OnWarlordSay(r); return true;
            case Msg.MW_MONTHRANKRESETCHAR_ACK: OnMonthRankResetChar(session, r); return true;
            default: return false;
        }
    }

    // ===== handlers =====

    /// <summary>Relay a fame-rank update blob to every map (the body is opaque; only the id changes).</summary>
    private void OnFameRankUpdate(PacketReader r)
    {
        var body = r.ReadRemaining().ToArray();
        var w = new PacketWriter(Msg.MW_FAMERANKUPDATE_REQ);
        w.WriteRaw(body);
        var packet = w.ToArray();
        foreach (var s in _state.Servers.Values) s.Send(packet);
    }

    private void OnWarlordSay(PacketReader r)
    {
        byte type = r.ReadByte();
        byte rankMonth = r.ReadByte();
        uint charId = r.ReadUInt32();
        string say = r.ReadString();
        var w = new PacketWriter(Msg.MW_WARLORDSAY_REQ);
        w.WriteByte(type); w.WriteByte(rankMonth); w.WriteUInt32(charId); w.WriteString(say);
        var packet = w.ToArray();
        foreach (var s in _state.Servers.Values) s.Send(packet);
    }

    private void OnMonthRankResetChar(ServerSession session, PacketReader r)
    {
        uint charId = r.ReadUInt32();
        if (!_state.Characters.TryGetValue(charId, out var ch)) return;
        var packet = BuildMonthRankResetChar(charId);
        foreach (var serverId in ch.Connections.Keys)
        {
            var map = _state.FindMapSvr(serverId);
            if (map is not null && map != session) map.Send(packet);
        }
    }

    /// <summary>
    /// Place an updated ranker into its country ladder, re-pick the warlord, and tell every map which
    /// slot range changed. Transcribed from OnMW_MONTHRANKUPDATE_ACK (the most intricate sender).
    /// </summary>
    private void OnMonthRankUpdate(PacketReader r)
    {
        byte month = r.ReadByte();
        byte country = r.ReadByte();
        if (month != _state.RankMonth || country >= Proto.CountryCount) return;

        var st = new MonthRanker();
        st.WrapOut(r);
        var arr = _state.MonthRank[country];
        const int N = Proto.MonthRankCount;
        bool newWarlord = false;

        RepickWarlord(arr, st, ref newWarlord);

        int oldRank = 0;
        for (int b = 1; b < N; b++)
            if (arr[b].CharId == st.CharId) { oldRank = b; break; }

        int newRank = 0;
        for (int i = 1; i < N; i++)
        {
            if (arr[i].MonthPoint <= st.MonthPoint)
            {
                if (arr[i].MonthPoint < st.MonthPoint) newRank = i;
                else if (arr[i].MonthWin < st.MonthWin) newRank = i;
                else if (arr[i].MonthWin > st.MonthWin) newRank = i + 1;
                else if (arr[i].MonthLose < st.MonthLose) newRank = i;
                else if (arr[i].MonthLose > st.MonthLose) newRank = i + 1;
                else newRank = arr[i].CharId < st.CharId ? i : i + 1;
                break;
            }
        }

        if (oldRank != 0 || newRank != 0)
        {
            if (oldRank == 0) oldRank = N - 1;
            else if (newRank == 0) newRank = N;
        }
        else return;

        byte startRank, endRank;
        if (oldRank == newRank)
        {
            arr[newRank] = st.Clone();
            startRank = endRank = (byte)newRank;
        }
        else if (oldRank < newRank)
        {
            for (int j = oldRank; j < newRank - 1; j++) arr[j] = arr[j + 1].Clone();
            arr[newRank - 1] = st.Clone();
            startRank = (byte)oldRank; endRank = (byte)(newRank - 1);
        }
        else
        {
            for (int j = oldRank; j > newRank; j--) arr[j] = arr[j - 1].Clone();
            arr[newRank] = st.Clone();
            startRank = (byte)newRank; endRank = (byte)oldRank;
        }

        RepickWarlord(arr, st, ref newWarlord);

        var packet = BuildMonthRankUpdate(month, country, startRank, endRank, newWarlord);
        foreach (var s in _state.Servers.Values) s.Send(packet);

        static void RepickWarlord(MonthRanker[] arr, MonthRanker st, ref bool newWarlord)
        {
            if (arr[0].TotalPoint >= st.TotalPoint && arr[0].CharId != st.CharId) return;
            arr[0] = st.Clone();
            newWarlord = true;
            for (int w = 1; w < Proto.MonthRankCount; w++)
                if (arr[w].TotalPoint > arr[0].TotalPoint) arr[0] = arr[w].Clone();
        }
    }

    // ===== monthly rollover (OnTimer + the SM/DM_MONTHRANKSAVE sequence, folded inline) =====

    /// <summary>Called once per second on the batch task. Fires the rollover when the calendar month
    /// passes the loaded rank month (mirrors the C++ OnTimer guard).</summary>
    public async Task OnTimerAsync()
    {
        byte curMonth = (byte)DateTime.UtcNow.Month;
        if (_state.RankMonth != 0 && curMonth != _state.RankMonth && !_state.FameRankSave)
            await MonthRankSaveAsync();

        // Guild auto-extinction: disband guilds whose 7-day disband grace has elapsed (C++ CheckTGuildExtinction).
        await CheckGuildExtinctionAsync();

        // Event subsystem ticks: lucky-event quarter schedule + timed-expiry queue (C++ CheckEventQuarter /
        // CheckEventExpired).
        CheckEventQuarter();
        CheckEventExpired();

        // The kingdom balance, once a minute (C++ OnTimer's per-minute block, WorldService.Nation.cs).
        int minute = DateTime.UtcNow.Minute;
        if (minute != _state.BalanceMinute) { _state.BalanceMinute = minute; RecalcCountryBalance(); }
        await RunReservedPostsAsync(DateTime.UtcNow);

        // Phase 4: drive the BoW + BR phase machines and the battle-time machine off the same 1-second tick.
        uint nowSec = (uint)DateTime.UtcNow.TimeOfDay.TotalSeconds;
        if (_state.Bow is not null) await BowOnTimerAsync(nowSec);
        if (_state.Br is not null) await BrOnTimerAsync(nowSec);
        if (_state.Battles is not null)
            BattleOnTimer(nowSec, (byte)((int)DateTime.UtcNow.DayOfWeek + 1),
                (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Proto.DayOne));
        if (_state.Tournament is not null)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!_state.Tournament.ScheduleInitialized)
            {
                _state.Tournament.ScheduleInitialized = true;
                if (_state.Battles is not null) SetTournamentTime(_state.Battles[BattleType.Tournament], now, monthBase: false);
            }
            TournamentOnTimer(now);
        }
    }

    private async Task MonthRankSaveAsync()
    {
        _state.FameRankSave = true;
        _log.LogInformation("Monthly PvP rank rollover: month {Old} -> {New}.", _state.RankMonth, DateTime.UtcNow.Month);

        // 1) zero each guild's monthly PvP contribution
        foreach (var g in _state.Guilds.Values) { g.PvPMonthPoint = 0; g.RankMonth = 0; }

        // 2) build the cross-country fame podium from the first-grade ranks
        var pool = new List<MonthRanker>();
        for (int m = 0; m < Proto.CountryCount; m++)
            for (int n = 1; n < Proto.FirstGradeGroupCount; n++)
                pool.Add(_state.MonthRank[m][n].Clone());
        pool.Sort(MonthRankDesc);

        var total = new MonthRanker[Proto.TotalMonthRankCount];
        for (int i = 0; i < total.Length; i++) total[i] = new MonthRanker();
        for (int t = 0; t < pool.Count && t + 1 < total.Length; t++) total[t + 1] = pool[t];

        int top = 0;
        for (int i = 0; i < Proto.CountryCount; i++)
            if (_state.MonthRank[top][0].TotalPoint < _state.MonthRank[i][0].TotalPoint) top = i;
        for (int i = 0; i < Proto.CountryCount; i++)
            total[i == top ? 0 : Proto.TotalMonthRankCount - i - 1] = _state.MonthRank[i][0].Clone();

        for (int t = 0; t < Proto.FameRankCount; t++) _state.LastFameRank[t] = total[t].Clone();

        // 3) best-effort persistence (TInitMonthRank + TSaveMonthRank per slot)
        if (_rankDb is not null)
        {
            try
            {
                await _rankDb.InitMonthRankAsync(_state.RankMonth);
                int k = 0;
                for (int i = 0; i < Proto.CountryCount; i++)
                    for (int j = 0; j < Proto.FirstGradeGroupCount; j++)
                    {
                        var rk = _state.MonthRank[i][j];
                        if (rk.CharId == 0 || (j != 0 && rk.MonthPoint == 0)) continue;
                        byte monthRank = (byte)Proto.TotalMonthRankCount;
                        for (int t = k; t < Proto.TotalMonthRankCount; t++)
                            if (rk.CharId == total[t].CharId) { monthRank = (byte)t; break; }
                        if (monthRank == 0) k = 1;
                        await _rankDb.SaveMonthRankAsync(_state.RankMonth, (byte)j, monthRank, ToRow(rk));
                    }
                byte next = (byte)(_state.RankMonth + 1); if (next > 12) next -= 12;
                await _rankDb.InitMonthRankAsync(next);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Month-rank persistence failed (tables/procs absent?)."); }
        }

        // 4) freeze the first-grade group, broadcast reset + group, then reset the live monthly ladder
        for (int i = 0; i < Proto.CountryCount; i++)
            for (int j = 0; j < Proto.FirstGradeGroupCount; j++)
                _state.FirstGradeGroup[i][j] = _state.MonthRank[i][j].Clone();

        foreach (var s in _state.Servers.Values)
        {
            s.Send(BuildMonthRankReset());
            s.Send(BuildFirstGradeGroup());
        }

        for (int i = 0; i < Proto.CountryCount; i++)
            for (int j = 1; j < Proto.MonthRankCount; j++)
                _state.MonthRank[i][j].Reset();

        byte inc = (byte)(_state.RankMonth + 1); if (inc > 12) inc -= 12;
        _state.RankMonth = inc;
        _state.FameRankSave = false;
    }

    /// <summary>Sort predicate matching C++ MonthRankDesc (month point, then wins, then losses).</summary>
    private static int MonthRankDesc(MonthRanker a, MonthRanker b)
    {
        if (a.MonthPoint != b.MonthPoint) return b.MonthPoint.CompareTo(a.MonthPoint);
        if (a.MonthWin != b.MonthWin) return b.MonthWin.CompareTo(a.MonthWin);
        return b.MonthLose.CompareTo(a.MonthLose);
    }

    private static MonthRankRow ToRow(MonthRanker r) => new(
        (byte)r.MonthRank, r.CharId, r.Name, r.TotalPoint, r.MonthPoint, r.MonthWin, r.MonthLose,
        r.TotalWin, r.TotalLose, r.Country, r.Level, r.Class, r.Race, r.Sex, r.Hair, r.Face, r.Say, r.Guild);

    // ===== senders =====

    /// <summary>SendMW_MONTHRANKLIST_REQ: the whole ladder for every country.</summary>
    private byte[] BuildMonthRankList()
    {
        var w = new PacketWriter(Msg.MW_MONTHRANKLIST_REQ);
        w.WriteByte(_state.RankMonth);
        w.WriteByte((byte)Proto.MonthRankCount);
        for (int i = 0; i < Proto.CountryCount; i++)
            for (int j = 0; j < Proto.MonthRankCount; j++)
                _state.MonthRank[i][j].WrapIn(w);
        return w.ToArray();
    }

    private byte[] BuildMonthRankUpdate(byte month, byte country, byte startRank, byte endRank, bool newWarlord)
    {
        var w = new PacketWriter(Msg.MW_MONTHRANKUPDATE_REQ);
        w.WriteByte(month);
        w.WriteByte(country);
        w.WriteByte(startRank);
        w.WriteByte(endRank);
        if (startRank != 0 && endRank != 0)
            for (int i = startRank; i <= endRank; i++)
                _state.MonthRank[country][i].WrapIn(w);
        w.WriteBool(newWarlord);
        if (newWarlord) _state.MonthRank[country][0].WrapIn(w);
        return w.ToArray();
    }

    private byte[] BuildMonthRankReset()
    {
        var w = new PacketWriter(Msg.MW_MONTHRANKRESET_REQ);
        w.WriteByte(_state.RankMonth);
        w.WriteByte((byte)Proto.FameRankCount);
        for (int i = 0; i < Proto.FameRankCount; i++) _state.LastFameRank[i].WrapIn(w);
        return w.ToArray();
    }

    private byte[] BuildFirstGradeGroup()
    {
        var w = new PacketWriter(Msg.MW_FIRSTGRADEGROUP_REQ);
        w.WriteByte(_state.RankMonth);
        w.WriteByte((byte)Proto.FirstGradeGroupCount);
        for (int i = 0; i < Proto.CountryCount; i++)
            for (int j = 0; j < Proto.FirstGradeGroupCount; j++)
                _state.FirstGradeGroup[i][j].WrapIn(w);
        return w.ToArray();
    }

    private static byte[] BuildMonthRankResetChar(uint charId)
    { var w = new PacketWriter(Msg.MW_MONTHRANKRESETCHAR_REQ); w.WriteUInt32(charId); return w.ToArray(); }
}
