using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// PvP ranking and titles: the month's points and tallies a kill moves, the ladder report to the world
/// (CheckMonthRank), the ladder the world sends and the client windows, the month reset, the PvP record window, and the
/// titles (GetTitle — honour, gold, month kills; the chosen title through the world).
/// </summary>
public class RankTitleTests
{
    private const uint Killer = 1, Victim = 2;
    private const byte VictimLevel = 19, Ranger = 1;
    private const ushort Honour10 = 56, Honour60 = 57, Gold5 = 90, Kills1 = 30, Hero1st = 1, Hero2nd = 4;

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Classes[Ranger] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                       // FTYPE_AL: a hit on a PC connects
        t.LevelPvPoint[VictimLevel] = 14;
        t.PvPointKill[(1, 2)] = (80, 20);                                 // an even kill: +11 / −2
        foreach (var r in new[]
        {
            new TitleRow(Hero1st, 1, 1, 0), new TitleRow(2, 1, 1, 0), new TitleRow(3, 1, 1, 0), new TitleRow(Hero2nd, 1, 1, 0),
            new TitleRow(Kills1, 2, 4, 0),                                // DEFEATS_MONTH: more than 0 kills this month
            new TitleRow(Honour10, 3, 7, 10), new TitleRow(Honour60, 3, 7, 60),
            new TitleRow(Gold5, 4, 8, 5),                                 // GOLD: more than 5 gold
        })
            t.Titles[r.Id] = r;
        return t;
    }

    private static Character Pc(uint id, string name, byte cls = 0)
        => new() { CharId = id, Name = name, Level = VictimLevel, Class = cls, MaxHp = 100, Hp = 100, MaxMp = 50, Mp = 50 };

    private sealed record Two(MapTestHarness H, ClientSession Sk, FakeClientChannel Ck, Character K,
        ClientSession Sv, FakeClientChannel Cv, Character V);

    private static async Task<Two> Setup()
    {
        var h = new MapTestHarness(Store());
        var k = Pc(Killer, "Killer"); var v = Pc(Victim, "Victim", Ranger);
        var (sk, ck) = await h.EnterAsync(Killer, 1, 1, x: 100, z: 100, name: "Killer", preSeeded: k);
        var (sv, cv) = await h.EnterAsync(Victim, 2, 2, x: 102, z: 100, name: "Victim", preSeeded: v);
        k.Country = 0; v.Country = 1; k.Level = v.Level = VictimLevel; v.Class = Ranger;
        k.PvpTotalPoint = 5; v.PvpTotalPoint = 50;
        h.Service.CombatRng = new Random(1);
        ck.Clear(); cv.Clear(); h.World.Clear();
        return new Two(h, sk, ck, k, sv, cv, v);
    }

    private static async Task Kill(Two x)
    {
        x.V.Hp = 5;
        await x.H.CastAndHitAsync(x.Sk, MapTestHarness.DefendReq(Killer, Victim, attackType: 1, targetType: 1, hostId: Killer));
    }

    private static List<(ushort Id, bool Selected)> Titles(PacketReader r)
    {
        var l = new List<(ushort, bool)>();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++) l.Add((r.ReadUInt16(), r.ReadByte() != 0));
        return l;
    }

    // ================================ the month's tally ================================

    [Fact]
    public async Task AKill_CountsInTheMonth_AndInTheClassRecord()
    {
        var x = await Setup();

        await Kill(x);

        Assert.Equal((11u, (ushort)1), (x.K.MonthPvPoint, x.K.MonthWin));
        Assert.Equal((ushort)1, x.V.MonthLose);
        Assert.Equal(1u, x.K.PvpRecord[Ranger * 2 + 1]);                   // one win against a ranger
        Assert.Equal(1u, x.V.PvpRecord[0 * 2 + 0]);                        // one loss to a warrior
        var r = new PacketReader(x.Ck.Last(Msg.CS_PVPPOINT_ACK)!);
        r.ReadUInt32(); r.ReadUInt32(); r.ReadByte();
        Assert.Equal(11u, r.ReadUInt32());                                 // the month's points shown
    }

    [Fact]
    public async Task AKill_IsReportedToTheLadder()
    {
        var x = await Setup();

        await Kill(x);

        var r = new PacketReader(x.H.World.Last(Msg.MW_MONTHRANKUPDATE_ACK)!);
        r.ReadByte();                                                      // bMonth
        Assert.Equal((byte)0, r.ReadByte());                               // the killer's country
        var me = new MonthRanker(); me.Read(r);
        Assert.Equal((Killer, "Killer", 16u, 11u, (ushort)1), (me.CharId, me.Name, me.TotalPoint, me.MonthPoint, me.MonthWin));
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task ThePvpRecordWindow_ShowsTheClassRecord_AndTheLastKills()
    {
        var x = await Setup();
        await Kill(x);
        x.Ck.Clear();

        await x.H.Service.DispatchClientAsync(x.Sk, new PacketWriter(Msg.CS_PVPRECORD_REQ).WriteByte(0).ToArray());

        var r = new PacketReader(x.Ck.Last(Msg.CS_PVPRECORD_ACK)!);
        Assert.Equal((byte)0, r.ReadByte());
        r.ReadUInt32(); r.ReadByte();                                      // rank order / percent
        var record = Enumerable.Range(0, 6).Select(_ => (r.ReadUInt32(), r.ReadUInt32())).ToList();
        Assert.Equal((1u, 0u), record[Ranger]);                            // (win, lose) against rangers
        Assert.Equal((byte)1, r.ReadByte());                               // one recent result
        Assert.Equal(("Victim", (byte)1, Ranger, VictimLevel, 11u), (r.ReadString(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadUInt32()));
        r.ReadInt64();
        r.ReadUInt32(); r.ReadByte();
        Assert.Equal(((ushort)1, (ushort)0), (r.ReadUInt16(), r.ReadUInt16()));
        Assert.Equal(0, r.Remaining);
    }

    // ================================ the ladder from the world ================================

    private static byte[] RankList(byte month, uint warlordOfC)
    {
        var w = new PacketWriter(Msg.MW_MONTHRANKLIST_REQ);
        w.WriteByte(month); w.WriteByte(33);
        for (int c = 0; c < 3; c++)
            for (int j = 0; j < 33; j++)
                new MonthRanker { CharId = c == 1 && j == 0 ? warlordOfC : 0, Name = c == 1 && j == 0 ? "Lord" : "", TotalPoint = c == 1 && j == 0 ? 9999u : 0 }.Write(w);
        return w.ToArray();
    }

    [Fact]
    public async Task TheLadderFromTheWorld_FillsTheMonthRankWindow()
    {
        var x = await Setup();
        await x.H.Service.DispatchWorldAsync(RankList(5, 77));

        await x.H.Service.DispatchClientAsync(x.Sk, new PacketWriter(Msg.CS_MONTHRANKLIST_REQ).ToArray());

        var r = new PacketReader(x.Ck.Last(Msg.CS_MONTHRANKLIST_ACK)!);
        Assert.Equal(((byte)5, (byte)17, (byte)17), (r.ReadByte(), r.ReadByte(), r.ReadByte()));
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal((byte)c, r.ReadByte());
            for (int j = 0; j < 17; j++)
            {
                var e = new MonthRanker(); e.Read(r);
                if (c == 1 && j == 0) Assert.Equal((77u, "Lord"), (e.CharId, e.Name));
            }
        }
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task NoOneToBeat_NoReport()
    {
        var x = await Setup();
        // Country 0's whole ladder already has more points than the killer will reach.
        var w = new PacketWriter(Msg.MW_MONTHRANKLIST_REQ);
        w.WriteByte(5); w.WriteByte(33);
        for (int c = 0; c < 3; c++)
            for (int j = 0; j < 33; j++) new MonthRanker { CharId = 1000u + (uint)j, MonthPoint = 500, TotalPoint = 5000 }.Write(w);
        await x.H.Service.DispatchWorldAsync(w.ToArray());

        await Kill(x);

        Assert.False(x.H.World.Has(Msg.MW_MONTHRANKUPDATE_ACK));
    }

    [Fact]
    public async Task TheMonthReset_StartsTheMonthOver_AndCrownsItsHeroes()
    {
        var x = await Setup();
        await Kill(x);                                                     // killer: 1 kill ⇒ the DEFEATS_MONTH title
        Assert.True(x.K.Titles.ContainsKey(Kills1));
        x.Ck.Clear();

        var w = new PacketWriter(Msg.MW_MONTHRANKRESET_REQ);
        w.WriteByte(4); w.WriteByte(9);                                   // April's heroes: the killer came first
        for (int i = 0; i < 9; i++) new MonthRanker { CharId = i == 0 ? Killer : 0 }.Write(w);
        await x.H.Service.DispatchWorldAsync(w.ToArray());

        Assert.Equal((0u, (ushort)0, (ushort)0), (x.K.MonthPvPoint, x.K.MonthWin, x.K.MonthLose));
        Assert.False(x.K.Titles.ContainsKey(Kills1));                      // the month's titles are gone
        Assert.True(x.Ck.Has(Msg.CS_TITLERESET_ACK));
        Assert.True(x.K.Titles.ContainsKey(Hero1st));                      // and the hero's title is earned
        Assert.True(x.Ck.Has(Msg.CS_UPDATEFAMERANKLIST_ACK));
        Assert.Equal(11u, x.K.PvpTotalPoint - 5);                         // all-time points stay
    }

    // ================================ titles ================================

    [Fact]
    public async Task PassingAnHonourRequirement_EarnsTheTitle()
    {
        var x = await Setup();

        await Kill(x);                                                     // total 5 → 16: more than 10

        Assert.True(x.K.Titles.ContainsKey(Honour10));
        Assert.False(x.K.Titles.ContainsKey(Honour60));
        Assert.Contains(x.Ck.WithId(Msg.CS_TITLEGAIN_ACK), p =>
        {
            var r = new PacketReader(p);
            var owned = Titles(r);
            return owned.Contains((Honour10, false)) && r.ReadUInt16() == Honour10 && r.ReadByte() == 1 && r.Remaining == 0;
        });
    }

    [Fact]
    public async Task AGoldChange_ChecksTheGoldTitles()
    {
        var x = await Setup();

        x.K.EarnMoney(6L * 1000 * 1000);                                   // 6 gold: more than 5

        Assert.True(x.K.Titles.ContainsKey(Gold5));
        Assert.True(x.Ck.Has(Msg.CS_TITLEGAIN_ACK));
    }

    private static byte[] CharInfo(uint charId, uint key, ushort title = 0)
    {
        var w = new PacketWriter(Msg.MW_CHARINFO_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key);
        w.WriteUInt32(0); w.WriteByte(0); w.WriteString(""); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteString("");
        w.WriteByte(0); w.WriteByte(0); w.WriteUInt16(0); w.WriteByte(0); w.WriteUInt16(0); w.WriteByte(1); w.WriteUInt32(0);
        w.WriteUInt16(title); w.WriteUInt32(0); w.WriteByte(0);
        return w.ToArray();
    }

    [Fact]
    public async Task AtLogin_TitlesNoLongerEarnedGo_AndTheShownOneComesFromTheWorld()
    {
        var x = await Setup();
        x.K.Titles[Honour60] = true;
        x.K.PvpTotalPoint = 70;                                            // passes 10 and 60: both, the shown one stays shown

        await x.H.Service.DispatchWorldAsync(CharInfo(Killer, 1, title: Honour60));

        Assert.Equal(Honour60, x.K.TitleId);
        Assert.Equal(new ushort[] { Honour10, Honour60 }, x.K.Titles.Keys.Where(k => k is Honour10 or Honour60).ToArray());
        Assert.True(x.K.Titles[Honour60]);                                 // still the shown one
    }

    [Fact]
    public async Task ChoosingAnOwnedTitle_GoesThroughTheWorld_ThenEveryoneAroundSeesIt()
    {
        var x = await Setup();
        x.K.Titles[Honour10] = false;

        await x.H.Service.DispatchClientAsync(x.Sk, new PacketWriter(Msg.CS_CHANGETITLE_REQ).WriteUInt16(Honour10).ToArray());
        var up = new PacketReader(x.H.World.Last(Msg.MW_CHANGECHARBASE_ACK)!);
        Assert.Equal((Killer, 1u, (byte)103, (byte)0, Honour10), (up.ReadUInt32(), up.ReadUInt32(), up.ReadByte(), up.ReadByte(), up.ReadUInt16()));

        var back = new PacketWriter(Msg.MW_CHANGECHARBASE_REQ);
        back.WriteUInt32(Killer); back.WriteUInt32(1); back.WriteByte(103); back.WriteByte(0); back.WriteUInt16(Honour10); back.WriteString("Killer");
        await x.H.Service.DispatchWorldAsync(back.ToArray());

        Assert.Equal(Honour10, x.K.TitleId);
        Assert.True(x.K.Titles[Honour10]);
        foreach (var c in new[] { x.Ck, x.Cv })
        {
            var r = new PacketReader(c.Last(Msg.CS_CHANGECHARBASE_ACK)!);
            Assert.Equal(((byte)0, Killer, (byte)103), (r.ReadByte(), r.ReadUInt32(), r.ReadByte()));
        }
    }

    [Fact]
    public async Task ATitleNotOwned_CannotBeChosen()
    {
        var x = await Setup();

        await x.H.Service.DispatchClientAsync(x.Sk, new PacketWriter(Msg.CS_CHANGETITLE_REQ).WriteUInt16(Honour60).ToArray());

        Assert.False(x.H.World.Has(Msg.MW_CHANGECHARBASE_ACK));
    }

    [Fact]
    public async Task TheTitleList_ListsTheOwnedTitles()
    {
        var x = await Setup();
        x.K.Titles[Honour10] = true; x.K.Titles[Gold5] = false;

        await x.H.Service.DispatchClientAsync(x.Sk, new PacketWriter(Msg.CS_TITLELIST_REQ).ToArray());

        var r = new PacketReader(x.Ck.Last(Msg.CS_TITLELIST_ACK)!);
        Assert.Equal(new List<(ushort, bool)> { (Honour10, true), (Gold5, false) }, Titles(r));
        Assert.Equal(0, r.Remaining);
    }
}
