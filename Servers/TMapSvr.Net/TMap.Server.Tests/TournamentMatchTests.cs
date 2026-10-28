using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Batch S6e-2: the tournament's matches — the bracket on its maps, the tournament character, kills, hiding, the result, the
/// drawn match, leaving mid-match, the world's results and the final's reward, and what a tournament character can't do.</summary>
public class TournamentMatchTests
{
    private const ushort Map = 501, Sword = 300, ClassSkill = 900, HideSkill = 901, Home = 100;
    private const byte StepEnter = 5, StepQFinal = 6, StepQFEnd = 7, StepFinal = 12;

    private sealed record Fighter(ClientSession S, FakeClientChannel C, Character Ch);
    private sealed record Setup(TemplateStore T, MapTestHarness H, FakeHeroStore Db, Fighter A, Fighter B);

    private static SkillTemplate Skill(ushort id, byte canLearn, bool hide = false) => new(id, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0,
        StartLevel: 1, MaxLevel: 5, NextLevel: 0, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0, SpeedApply: 0, Positive: 1,
        MapId: 0xFFFF, ClassId: 0xFFFF, IsHideSkill: hide, CanLearn: canLearn);

    private static byte[] Info(byte step)
    {
        var w = new PacketWriter(Msg.MW_TOURNAMENTINFO_REQ);
        w.WriteByte(17); w.WriteByte(1); w.WriteByte(step); w.WriteByte(1);
        w.WriteByte(1); w.WriteByte(1); w.WriteString("Solo"); w.WriteByte(1); w.WriteUInt32(0xFFFF); w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteUInt16(0); w.WriteByte(0); w.WriteByte(1); w.WriteByte(99); w.WriteByte(0);
        return w.ToArray();
    }

    /// <summary>Ann (slot 0) against Bob (slot 1) in entry 1: their quarter-final is map 501.</summary>
    private static byte[] Bracket()
    {
        var w = new PacketWriter(Msg.MW_TOURNAMENTMATCH_REQ);
        w.WriteByte(2);
        foreach (var (slot, id, name) in new[] { ((byte)0, 1u, "Ann"), ((byte)1, 2u, "Bob") })
        {
            w.WriteByte(1); w.WriteByte(slot); w.WriteUInt32(id); w.WriteByte(1); w.WriteString(name); w.WriteByte(30); w.WriteByte(2);
            w.WriteUInt32(id); w.WriteByte(0); w.WriteByte(0); w.WriteByte(0);
        }
        return w.ToArray();
    }

    private static byte[] Enable(byte step, uint period = 300)
    {
        var w = new PacketWriter(Msg.MW_TOURNAMENTENABLE_REQ);
        w.WriteByte(1); w.WriteByte(step); w.WriteUInt32(period); w.WriteInt64(0);
        return w.ToArray();
    }

    private static async Task<Setup> Open(bool bobOnMap = true)
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[2] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                                             // FTYPE_AL: a hit on a PC connects
        t.Items[Sword] = new ItemTemplate(Sword, 0, new[] { 1f, 0f, 0f, 0f }, Type: 1, Kind: 1);
        t.Skills[ClassSkill] = Skill(ClassSkill, 1);
        t.Skills[HideSkill] = Skill(HideSkill, 0, hide: true);
        t.LimitedLevel = 80;
        t.TnmtInvens.Add((Proto.InvenDefault, 0, 0));
        t.TnmtInvens.Add((Proto.InvenEquip, 0, 0));
        t.TnmtItems[2] = new() { (Proto.InvenDefault, new FullItemRow(0, 0, 4, Sword, 0, 1, 0, 100, 100, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0, 0)) };
        t.SpawnPositions[Home] = new SpawnPosRow(Home, 0, 50, 0, 50, 0);
        t.SpawnPositions[101] = new SpawnPosRow(101, Map, 10, 0, 10, 0);                      // this map server holds map 501
        var h = new MapTestHarness(t);
        var db = new FakeHeroStore();
        h.Service.HeroStore = db;
        h.Service.CombatRng = new Random(1);
        await h.Service.DispatchWorldAsync(Info(StepEnter));
        await h.Service.DispatchWorldAsync(Bracket());
        async Task<Fighter> Pc(uint id, string name, ushort map)
        {
            var ch = new Character { CharId = id, Name = name, Level = 30, Class = 2, Country = 1, MaxHp = 100, Hp = 100 };
            ch.Invens.Add(new Inven { InvenId = Proto.InvenDefault });
            ch.FindInven(Proto.InvenDefault)!.Items.Add(new Item { ItemSlot = 9, TemplateId = Sword, Count = 1, Template = t.Items[Sword] });
            ch.Skills.Add(new Skill { SkillId = 777, Level = 3 });
            ch.Persist.LastSpawnId = Home;
            var (s, c) = await h.EnterAsync(id, id, 100 + id, name: name, mapId: map, x: 100, y: 0, z: 100, preSeeded: ch);
            return new Fighter(s, c, ch);
        }
        var a = await Pc(1, "Ann", Map);
        var b = await Pc(2, "Bob", bobOnMap ? Map : (ushort)0);
        h.World.Clear();
        return new Setup(t, h, db, a, b);
    }

    private static (byte Step, bool Won, uint Winner, uint Loser) Result(MapTestHarness h)
    {
        var r = new PacketReader(h.World.Last(Msg.MW_TOURNAMENTRESULT_ACK)!);
        return (r.ReadByte(), r.ReadByte() != 0, r.ReadUInt32(), r.ReadUInt32());
    }

    [Fact]
    public async Task AFighterOnItsMap_BecomesTheTournamentCharacter()
    {
        var x = await Open();
        var a = x.A.Ch;
        Assert.NotNull(a.TournamentBackup);
        Assert.Equal(80, a.Level);
        Assert.Equal(ClassSkill, Assert.Single(a.Skills).SkillId);
        Assert.Equal(5, a.Skills[0].Level);
        Assert.Equal(4, Assert.Single(a.FindInven(Proto.InvenDefault)!.Items).ItemSlot);   // the chart's sword, not its own
        Assert.True(x.A.C.Has(Msg.CS_TOURNAMENTENTER_ACK));
        Assert.True(x.A.C.Has(Msg.CS_DELITEM_ACK));
    }

    [Fact]
    public async Task LeavingTheMap_GivesTheCharacterBack()
    {
        var x = await Open();
        byte level = x.A.Ch.TournamentBackup!.Level;
        var w = new PacketWriter(Msg.MW_STARTTELEPORT_REQ);
        w.WriteUInt32(1); w.WriteUInt32(101); w.WriteByte(1); w.WriteUInt16(0); w.WriteFloat(50); w.WriteFloat(0); w.WriteFloat(50);
        await x.H.Service.DispatchWorldAsync(w.ToArray());
        var a = x.A.Ch;
        Assert.Null(a.TournamentBackup);
        Assert.Equal(level, a.Level);
        Assert.NotEqual(80, a.Level);
        Assert.Equal(777, Assert.Single(a.Skills).SkillId);
        Assert.Equal(9, Assert.Single(a.FindInven(Proto.InvenDefault)!.Items).ItemSlot);
        Assert.True(x.A.C.Has(Msg.CS_EXITTOURNAMETMAINTAIN_ACK));
    }

    [Fact]
    public async Task Kills_DecideTheMatch_AtItsEnd()
    {
        var x = await Open();
        await x.H.Service.DispatchWorldAsync(Enable(StepQFinal));
        Assert.False(x.H.World.Has(Msg.MW_TOURNAMENTRESULT_ACK));                               // both sides are there
        x.B.Ch.Hp = 1;
        await x.H.CastAndHitAsync(x.A.S, MapTestHarness.DefendReq(1, 2, attackType: 1, targetType: 1, hostId: 1));
        Assert.Equal(0, (int)x.B.Ch.Hp);
        await x.H.Service.DispatchWorldAsync(Enable(StepQFEnd));
        Assert.Equal((StepQFinal, true, 1u, 2u), Result(x.H));                                  // Ann's side scored
    }

    [Fact]
    public async Task AMissingSide_LosesAtOnce()
    {
        var x = await Open(bobOnMap: false);
        await x.H.Service.DispatchWorldAsync(Enable(StepQFinal));
        Assert.Equal((StepQFinal, true, 1u, 2u), Result(x.H));
    }

    [Fact]
    public async Task AFullTie_IsDrawnByDice_AndSettledTenSecondsLater()
    {
        var x = await Open();
        await x.H.Service.DispatchWorldAsync(Enable(StepQFinal));
        await x.H.Service.DispatchWorldAsync(Enable(StepQFEnd));
        Assert.True(x.A.C.Has(Msg.CS_TOURNAMENTSELECTWINNER_ACK));
        Assert.False(x.H.World.Has(Msg.MW_TOURNAMENTRESULT_ACK));
        for (int i = 0; i < 11; i++) await x.H.Service.OnTimerAsync();
        var (step, won, winner, loser) = Result(x.H);
        Assert.Equal((StepQFinal, true), (step, won));
        Assert.Equal(new[] { 1u, 2u }, new[] { winner, loser }.OrderBy(v => v));
    }

    [Fact]
    public async Task WorldResults_AreShown_AndTheFinalRewarded()
    {
        var x = await Open();
        x.Db.Reward = new TournamentRewardRow(0, 0, 1, 1, 10, 20, 1, 0, 3, 1, 1, 30, 2, 0, 0, 0, 0, "Ann", "");
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_MONTHRANKLIST_REQ).WriteByte(5).WriteByte(0).ToArray());
        var w = new PacketWriter(Msg.MW_TOURNAMENTRESULT_REQ);
        w.WriteUInt16(1); w.WriteByte(StepFinal); w.WriteByte(1); w.WriteUInt32(1); w.WriteUInt32(2); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteByte(0);
        await x.H.Service.DispatchWorldAsync(w.ToArray());
        await Task.Delay(50);
        var r = new PacketReader(x.A.C.Last(Msg.CS_TOURNAMENTRESULT_ACK)!);
        Assert.Equal((StepFinal, 1, 1u, 2u), (r.ReadByte(), r.ReadByte(), r.ReadUInt32(), r.ReadUInt32()));
        Assert.Equal(((byte)4, (ushort)1, (byte)1, 1u, 1u), (x.Db.Rewards[0].Month, x.Db.Rewards[0].Tournament, x.Db.Rewards[0].Entry, x.Db.Rewards[0].Chief, x.Db.Rewards[0].Winner));
        Assert.True(x.H.World.Has(Msg.MW_FAMERANKUPDATE_ACK));
    }

    [Fact]
    public async Task ATournamentCharacter_CannotTradeOrMail()
    {
        var x = await Open();
        x.A.C.Clear();
        await x.H.Service.DispatchClientAsync(x.A.S, new PacketWriter(Msg.CS_STOREOPEN_REQ).WriteString("shop").WriteByte(0).ToArray());
        await x.H.Service.DispatchClientAsync(x.A.S, new PacketWriter(Msg.CS_POSTVIEW_REQ).WriteUInt32(1).ToArray());
        Assert.Empty(x.A.C.Sent);
        await x.H.Service.DispatchClientAsync(x.A.S, new PacketWriter(Msg.CS_ITEMSELL_REQ).WriteByte(0xFF).WriteByte(4).WriteByte(1).WriteUInt16(0).ToArray());
        Assert.True(x.A.C.Has(Msg.CS_ITEMSELL_ACK));
    }
}
