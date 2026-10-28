using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Mini-games, batch S5b — the soul lottery and the event lottery's message (C++ CSHandler.cpp:17724–17830, TPlayer.cpp:1117–1160,
/// SSHandler.cpp:16049, CSSender.cpp:1357, 5045, 6227–6255).
/// <list type="bullet">
/// <item><b>The soul bar</b> fills with the exp gained (memory only); full, it is shown around (<c>CS_SOULUP_ACK</c>) and goes in the
/// fourth field of <c>CS_EXP_ACK</c>. A level-up keeps its share of the bar.</item>
/// <item><b>The soul lottery</b>: with a full bar, four different numbers of 1–30 and a prize (the level's base money times 1–101)
/// are drawn (<c>CS_SOULLOTTERY_REQ</c>); then one number is drawn (<c>CS_SOULLOTTERY_TRIGGER_REQ</c>) — one of the four wins the
/// prize, else the level in silver; either way a 15-minute buff (skill 820) follows.</item>
/// <item><b>The event lottery</b>'s winners, sent by the world (<c>MW_EVENTMSGLOTTERY_REQ</c>), are shown to everyone
/// (<c>CS_SYSTEMMSG_ACK</c> <c>SM_EVENT_LOTTERY</c>).</item>
/// </list>
/// <para>The bar fills with the bonus exp too (the C++ left the bonus out; this port adds it before <see cref="GainExp"/>).</para>
/// </summary>
public sealed partial class MapService
{
    private const ushort SoulLotterySkill = 820;
    private const uint SoulLotteryBuffMs = 15 * 60 * 1000;
    private const byte SmEventLottery = 27;

    private uint SoulBarWidth(Character ch)
    {
        uint ceiling = _templates.LevelExpOf(ch.Level) ?? 0, floor = _templates.LevelExpOf(ch.Level - 1) ?? 0;
        return ceiling > floor ? ceiling - floor : 0;
    }

    /// <summary>C++ <c>CTPlayer::GainExp</c>'s soul part: the bar fills; full the first time, it is shown around.</summary>
    private void FillSoulBar(ClientSession s, Character ch, uint gain)
    {
        uint width = SoulBarWidth(ch);
        if (width == 0) return;
        uint filled = (uint)Math.Min((ulong)ch.SoulLotExp + gain, uint.MaxValue);
        if (width <= filled)
        {
            if (ch.UseLot) return;
            ch.UseLot = true;
            ch.SoulLotExp = width;
            var up = new PacketWriter(Msg.CS_SOULUP_ACK).WriteUInt32(ch.CharId).ToArray();
            foreach (var p in _state.InView(s)) p.Send(up);
        }
        else ch.SoulLotExp = filled;
    }

    /// <summary>C++ <c>OnCS_SOULLOTTERY_REQ</c> (CSHandler.cpp:17724).</summary>
    private void OnCS_SOULLOTTERY_REQ(ClientSession s)
    {
        if (!s.IsMain || s.Char is not { } ch || SoulBarWidth(ch) == 0 || ch.SoulLotExp != SoulBarWidth(ch)) return;
        uint Draw() => (uint)(Rand() % 30 + 1);
        uint n1 = Draw(), n2 = Draw(), n3 = Draw(), n4 = Draw();
        while (n2 == n1) n2 = Draw();
        while (n3 == n2 || n3 == n1) n3 = Draw();
        while (n4 == n2 || n4 == n1 || n4 == n3) n4 = Draw();
        uint basis = _templates.LevelMoneyOf(ch.Level) ?? 0;
        if (basis == 0) return;
        ch.SoulLotExp = 0;
        uint range = basis * (uint)(Rand() % 100 + 1);
        uint money = (uint)Rand() % range + basis;
        s.Send(new PacketWriter(Msg.CS_SOULLOTTERY_ACK).WriteUInt32(n1).WriteUInt32(n2).WriteUInt32(n3).WriteUInt32(n4).WriteUInt32(money));
        ch.LotNumbers = new[] { n1, n2, n3, n4 };
        ch.LotMoney = money;
    }

    /// <summary>C++ <c>OnCS_SOULLOTTERY_TRIGGER_REQ</c> (CSHandler.cpp:17778).</summary>
    private void OnCS_SOULLOTTERY_TRIGGER_REQ(ClientSession s)
    {
        if (!s.IsMain || s.Char is not { } ch || ch.LotNumbers is not { } numbers || ch.LotMoney == 0) return;
        uint draw = (uint)(Rand() % 30 + 1);
        bool won = numbers.Contains(draw);
        if (!won) ch.LotMoney = ch.Level * Character.MoneyMultiply;                              // CalcMoney(0, level, 0)
        ch.EarnMoney(ch.LotMoney);
        SendCS_MONEY_ACK(s, ch);
        s.Send(new PacketWriter(Msg.CS_SOULLOTTERY_TRIGGER_ACK).WriteUInt32(draw).WriteUInt32(ch.LotMoney).WriteByte(won ? (byte)1 : (byte)0));
        ch.LotNumbers = null;
        ch.LotMoney = 0;
        ch.UseLot = false;
        ForceMaintain(s, ch, SoulLotterySkill, ch.CharId, OtPc, ch.CharId, OtPc, SoulLotteryBuffMs);
    }

    /// <summary>C++ <c>OnMW_EVENTMSGLOTTERY_REQ</c> (SSHandler.cpp:16049): the winners' list, to every player.</summary>
    private void OnMW_EVENTMSGLOTTERY_REQ(PacketReader r)
    {
        byte[] body = r.ReadBytes(r.Remaining);
        var msg = new PacketWriter(Msg.CS_SYSTEMMSG_ACK).WriteByte(SmEventLottery).WriteRaw(body).ToArray();
        foreach (var p in _state.AllInGame()) if (p.IsMain) p.Send(msg);
    }
}
