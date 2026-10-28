using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Dice that land on 0, 1, 2, … in turn.</summary>
internal sealed class CyclingRandom : Random
{
    private int _n;
    public override int Next(int maxValue) => maxValue <= 0 ? 0 : _n++ % maxValue;
}

internal sealed class FakeCompanionStore : ICompanionStore
{
    public List<(uint CharId, byte Slot)> Deleted { get; } = new();
    public Task<CompanionLoad> LoadCompanionsAsync(uint charId) => Task.FromResult(new CompanionLoad(new(), 0xFF, 0));
    public Task SaveCompanionAsync(uint charId, CompanionRow c) => Task.CompletedTask;
    public Task DeleteCompanionAsync(uint charId, byte slot) { Deleted.Add((charId, slot)); return Task.CompletedTask; }
    public Task SaveLastCompanionAsync(uint charId, byte slot) => Task.CompletedTask;
    public Task SaveMedalsAsync(uint charId, uint medals) => Task.CompletedTask;
}

/// <summary>
/// Companions (C++ CSHandler.cpp:18514-19610; TPlayer.cpp:1054, 5665-5723, 7086): the owned record, the creature it
/// becomes through the world, its growth, items and stamina, and the bonuses it gives its owner.
/// </summary>
public class CompanionTests
{
    private const ushort Rune = 19001, Species = 31125, SummonAttr = 1001, Food = 19100, ExpPot = 19101, Powder = 19102,
        CompItemA = 19200, CompItemB = 19201, CompItemA2 = 19202, NpcId = 500;
    private const byte Level = 20;
    private const uint Key = 9;
    private const long Now = 2_000_000_000;

    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        t.MonsterTemplates[Species] = new MonsterTemplate(Species, 1, 0, SummonAttr: SummonAttr, Race: 51, Class: 2);
        t.MonAttrs[TemplateStore.MonAttrKey(SummonAttr, Level)] = new MonAttrRow(SummonAttr, Level, 500, 200, 5);
        t.MonAttrs[TemplateStore.MonAttrKey(SummonAttr, 1)] = new MonAttrRow(SummonAttr, 1, 50, 20, 1);
        t.Items[Rune] = new ItemTemplate(Rune, 0, new float[4], Type: 22, Kind: 0, UseValue: 31124);
        t.Items[Food] = new ItemTemplate(Food, 0, new float[4], Type: 7, Kind: 109, UseValue: 5000);
        t.Items[ExpPot] = new ItemTemplate(ExpPot, 0, new float[4], Type: 7, Kind: 110, UseValue: 1000);
        t.Items[Powder] = new ItemTemplate(Powder, 0, new float[4], Type: 7, Kind: 116);
        t.Items[CompItemA] = new ItemTemplate(CompItemA, 0, new float[4], Type: 24, Kind: 111);
        t.Items[CompItemA2] = new ItemTemplate(CompItemA2, 0, new float[4], Type: 24, Kind: 111);
        t.Items[CompItemB] = new ItemTemplate(CompItemB, 0, new float[4], Type: 24, Kind: 114);
        t.Items[18084] = new ItemTemplate(18084, 0, new float[4], Type: 24, Kind: 112);
        t.CompanionRunes[Rune] = Species;
        foreach (var (id, b, m) in new (byte, float, float)[] { (11, 1, 2), (12, 1, 2), (13, 1, .5f), (20, 1, .5f), (21, 1, .5f),
                     (51, 20, 20), (86, 1, 2), (87, 1, 2), (88, 10, .5f) })
            t.CompanionBonuses[id] = new CompanionBonus(id, b, m);
        return t;
    }

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch, FakeCompanionStore db)> Setup(
        Action<Character>? seed = null)
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var db = new FakeCompanionStore();
        h.Service.CompanionStore = db;
        h.Service.UnixNow = () => Now;
        h.Service.CompanionRng = new CyclingRandom();
        var ch = new Character { CharId = 1, Name = "Owner", MaxHp = 100, Hp = 100 };
        var bag = new Inven { InvenId = 0 };
        foreach (var (slot, id) in new (byte, ushort)[] { (1, Rune), (2, Food), (3, ExpPot), (4, Powder), (5, CompItemA), (6, CompItemB), (7, CompItemA2) })
            bag.Items.Add(new Item { ItemSlot = slot, TemplateId = id, Template = t.Items[id], Count = 3 });
        bag.Items[0].Ext[Item.IevCompanion] = Species;
        ch.Invens.Add(bag);
        seed?.Invoke(ch);
        var (s, c) = await h.EnterAsync(1, 1, Key, name: "Owner", preSeeded: ch);
        ch.Level = Level;
        c.Clear(); h.World.Clear();
        return (h, s, c, ch, db);
    }

    private static Companion Comp(byte slot, byte bonus = 11, byte level = 1, uint life = 11000) => new()
    {
        Slot = slot, MonId = Species, Name = $"C{slot}", BonusId = bonus, Level = level, Life = life,
        NextExp = Companion.NextExpFor(level),
    };

    private static byte[] Req(ushort id, Action<PacketWriter> body) { var w = new PacketWriter(id); body(w); return w.ToArray(); }

    private static byte[] Create(string name = "Buddy") => Req(Msg.CS_CREATECOMPANION_REQ, w => { w.WriteByte(0); w.WriteByte(1); w.WriteString(name); });

    // ================================ create / list ================================

    [Fact]
    public async Task ARune_CreatesACompanion_InTheFirstFreeSlot_AndCallsItOut()
    {
        var (h, s, c, ch, _) = await Setup(ch => ch.Companions[0] = Comp(0, bonus: 11));

        await h.Service.DispatchClientAsync(s, Create());

        var created = ch.Companions[1];
        Assert.Equal(Species, created.MonId);
        Assert.Equal((byte)1, created.Level);
        Assert.Equal(11000u, created.Life);
        Assert.Equal((byte)1, created.StatPoints);
        Assert.Equal((ushort)18084, created.ItemIds[0]);
        Assert.Equal(Now + 3600, created.EndTimes[0]);
        Assert.NotEqual((byte)11, created.BonusId);                 // never a bonus another companion has
        Assert.Equal((byte)1, ch.CompanionSlot);
        var ack = new PacketReader(c.Last(Msg.CS_CREATECOMPANION_ACK)!);
        Assert.Equal(0, ack.ReadByte()); Assert.Equal(1, ack.ReadByte());
        Assert.Equal(2, ch.FindInven(0)!.Items.First(i => i.ItemSlot == 1).Count);   // the rune is used

        var mw = new PacketReader(h.World.Last(Msg.MW_CREATESPOLECNIKMON_ACK)!);
        Assert.Equal(1u, mw.ReadUInt32()); Assert.Equal(Key, mw.ReadUInt32()); Assert.Equal(0u, mw.ReadUInt32());
        Assert.Equal(Species, mw.ReadUInt16());
        Assert.Equal(SummonAttr | ((uint)Level << 16), mw.ReadUInt32());
    }

    [Fact]
    public async Task ABadName_OrAFullRoster_IsRefused()
    {
        var (h, s, c, ch, _) = await Setup();
        await h.Service.DispatchClientAsync(s, Create(""));
        Assert.Equal(1, new PacketReader(c.Last(Msg.CS_CREATECOMPANION_ACK)!).ReadByte());

        for (byte i = 0; i < 5; i++) ch.Companions[i] = Comp(i, bonus: new byte[] { 11, 12, 13, 20, 21 }[i]);
        await h.Service.DispatchClientAsync(s, Create());
        var r = new PacketReader(c.Last(Msg.CS_CREATECOMPANION_ACK)!);
        Assert.Equal(3, r.ReadByte()); Assert.Equal(0xFF, r.ReadByte());
    }

    [Fact]
    public async Task TheList_CarriesEveryField()
    {
        var (h, s, c, ch, _) = await Setup(ch =>
        {
            var x = Comp(0, bonus: 13, level: 5);
            x.Exp = 99; x.StatPoints = 4; x.Effect = 7; x.Stats[2] = 9; x.ItemIds[1] = CompItemB; x.EndTimes[1] = 555;
            ch.Companions[0] = x;
        });
        await h.Service.DispatchClientAsync(s, Create());                                 // sends the list

        var r = new PacketReader(c.WithId(Msg.CS_COMPANIONLIST_ACK).First());
        Assert.Equal(2, r.ReadByte());
        Assert.Equal(0, r.ReadByte()); Assert.Equal((uint)Species, r.ReadUInt32()); Assert.Equal("C0", r.ReadString());
        Assert.Equal(99u, r.ReadUInt32()); Assert.Equal(25u * 3600, r.ReadUInt32()); Assert.Equal(5, r.ReadByte());
        Assert.Equal(11000u, r.ReadUInt32()); Assert.Equal(4, r.ReadByte()); Assert.Equal(7, r.ReadByte());
        Assert.Equal(new byte[] { 0, 0, 9, 0, 0, 0 }, new[] { r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte() });
        Assert.Equal(0, r.ReadUInt16()); Assert.Equal(0, r.ReadInt64());
        Assert.Equal(CompItemB, r.ReadUInt16()); Assert.Equal(555, r.ReadInt64());
        Assert.Equal(0u, r.ReadUInt32()); Assert.Equal(13, r.ReadByte());
        Assert.Equal(1f + 0.5f * 4, r.ReadFloat());                      // base + mult·(level-1)
    }

    // ================================ delete ================================

    [Fact]
    public async Task Deleting_ClosesTheGap_AndDeletesTheVacatedLastSlot()
    {
        var (h, s, c, ch, db) = await Setup(ch =>
        {
            for (byte i = 0; i < 3; i++) ch.Companions[i] = Comp(i);
            ch.CompanionSlot = 2;
        });

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_DELETECOMPANION_REQ, w => w.WriteByte(0)));

        Assert.Equal(new byte[] { 0, 1 }, ch.Companions.Keys.ToArray());
        Assert.Equal("C1", ch.Companions[0].Name);
        Assert.Equal("C2", ch.Companions[1].Name);
        Assert.Equal((byte)1, ch.CompanionSlot);                         // followed its companion down
        Assert.Equal(new[] { (1u, (byte)0), (1u, (byte)2) }, db.Deleted); // the deleted slot, then the vacated one
    }

    [Fact]
    public async Task Deleting_WithNoneSummoned_KeepsNone()
    {
        var (h, s, _, ch, _) = await Setup(ch => { ch.Companions[0] = Comp(0); ch.Companions[1] = Comp(1); });
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_DELETECOMPANION_REQ, w => w.WriteByte(0)));
        Assert.Equal((byte)0xFF, ch.CompanionSlot);                      // the C++ made it 0xFE
    }

    // ================================ summon ================================

    [Fact]
    public async Task Summoning_UsesTheRecordsOwnSpecies_AndAgain_DismissesIt()
    {
        var (h, s, c, ch, _) = await Setup(ch => ch.Companions[0] = Comp(0, level: 3));

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_COMPANIONRECALL_REQ, w => { w.WriteUInt32(Species); w.WriteByte(0); }));
        Assert.Equal((byte)0, ch.CompanionSlot);
        Assert.Equal(0, new PacketReader(c.Last(Msg.CS_UPDATESPAWNEDCOMPANION_REQ)!).ReadByte());
        var mw = h.World.Last(Msg.MW_CREATESPOLECNIKMON_ACK)!;

        // The world gives it an id: the creature appears.
        var raw = (byte[])mw.Clone();
        PacketHeader.WriteId(raw, Msg.MW_CREATESPOLECNIKMON_REQ);
        BitConverter.GetBytes(4242u).CopyTo(raw, PacketHeader.Size + 8);
        c.Clear();
        await h.Service.DispatchWorldAsync(raw);
        var obj = ch.CompanionObjs[4242];
        Assert.True(obj.IsCompanion);
        var add = new PacketReader(c.Last(Msg.CS_ADDSPOLECNIKMON_ACK)!);
        Assert.Equal(1u, add.ReadUInt32()); Assert.Equal(4242u, add.ReadUInt32()); Assert.Equal(Species, add.ReadUInt16());
        Assert.True(c.Has(Msg.CS_CHARSTATINFO_ACK));                     // the viewer's own sheet goes first

        h.World.Clear();
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_COMPANIONRECALL_REQ, w => { w.WriteUInt32(Species); w.WriteByte(0); }));
        Assert.Equal((byte)0xFF, ch.CompanionSlot);
        var del = new PacketReader(h.World.Last(Msg.MW_SPOLECNIKMONDEL_ACK)!);
        del.ReadUInt32(); del.ReadUInt32(); Assert.Equal(4242u, del.ReadUInt32());

        var gone = Req(Msg.MW_SPOLECNIKMONDEL_REQ, w => { w.WriteUInt32(1); w.WriteUInt32(Key); w.WriteUInt32(4242); w.WriteByte(1); });
        c.Clear();
        await h.Service.DispatchWorldAsync(gone);
        Assert.Empty(ch.CompanionObjs);
        var d = new PacketReader(c.Last(Msg.CS_DELSPOLECNIKMON_ACK)!);
        Assert.Equal(1u, d.ReadUInt32()); Assert.Equal(4242u, d.ReadUInt32()); Assert.Equal(1, d.ReadByte()); Assert.Equal(0, d.Remaining);
    }

    // ================================ growth ================================

    [Fact]
    public async Task LevelUp_NeedsFullExp_AndGrantsPointsByTheOldLevel()
    {
        var (h, s, c, ch, _) = await Setup(ch => ch.Companions[0] = Comp(0, bonus: 12, level: 17));
        var comp = ch.Companions[0];
        byte[] lup = Req(Msg.CS_COMPANIONLUP_REQ, w => w.WriteByte(0));

        await h.Service.DispatchClientAsync(s, lup);
        Assert.Equal((byte)17, comp.Level);                               // not enough exp

        comp.Exp = comp.NextExp;
        await h.Service.DispatchClientAsync(s, lup);
        Assert.Equal((byte)18, comp.Level);
        Assert.Equal((byte)5, comp.StatPoints);                          // levels 15-19 give 5
        Assert.All(comp.Stats, st => Assert.Equal((byte)5, st));        // reaching 18: +5 to every stat
        Assert.Equal(18u * 18 * 3600, comp.NextExp);
        var r = new PacketReader(c.Last(Msg.CS_COMPANIONLUPDATE_REQ)!);
        Assert.Equal(0, r.ReadByte()); Assert.Equal(18, r.ReadByte()); Assert.Equal(0u, r.ReadUInt32()); Assert.Equal(5, r.ReadByte());
        Assert.Equal(18u * 18 * 3600, r.ReadUInt32()); Assert.Equal(1f + 2f * 17, r.ReadFloat());
    }

    [Fact]
    public async Task LevelNineteen_StillGrantsFivePoints()
    {
        var (h, s, _, ch, _) = await Setup(ch => ch.Companions[0] = Comp(0, bonus: 12, level: 19));
        ch.Companions[0].Exp = ch.Companions[0].NextExp;
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_COMPANIONLUP_REQ, w => w.WriteByte(0)));
        Assert.Equal((byte)20, ch.Companions[0].Level);
        Assert.Equal((byte)5, ch.Companions[0].StatPoints);
    }

    [Fact]
    public async Task Upgrade_SpendsAPoint_UpToThirty()
    {
        var (h, s, c, ch, _) = await Setup(ch => { var x = Comp(0); x.StatPoints = 2; x.Stats[1] = 29; ch.Companions[0] = x; });
        byte[] up = Req(Msg.CS_COMPANIONUPGRADE_REQ, w => { w.WriteByte(1); w.WriteByte(0); });

        await h.Service.DispatchClientAsync(s, up);
        await h.Service.DispatchClientAsync(s, up);

        Assert.Equal((byte)30, ch.Companions[0].Stats[1]);
        Assert.Equal((byte)1, ch.Companions[0].StatPoints);             // the second one hit the cap
    }

    [Fact]
    public async Task FoodAndExpPotions_AreCapped_AndTold()
    {
        var (h, s, c, ch, _) = await Setup(ch => ch.Companions[0] = Comp(0, life: 298000));
        byte[] Use(byte itemSlot) => Req(Msg.CS_USEPETITEM_REQ, w => { w.WriteUInt32((uint)(0 | (itemSlot << 16))); w.WriteByte(0); });

        await h.Service.DispatchClientAsync(s, Use(2));
        Assert.Equal(300000u, ch.Companions[0].Life);
        await h.Service.DispatchClientAsync(s, Use(3));
        Assert.Equal(1000u, ch.Companions[0].Exp);
        var r = new PacketReader(c.Last(Msg.CS_UPDATECOMPANIONBYITEM_REQ)!);
        Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(0, r.ReadByte()); Assert.Equal(300000u, r.ReadUInt32()); Assert.Equal(1000u, r.ReadUInt32());

        await h.Service.DispatchClientAsync(s, Use(2));                   // already full: not used
        Assert.Equal(2, ch.FindInven(0)!.Items.First(i => i.ItemSlot == 2).Count);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(255)]
    public async Task Gameforge5_CompanionAtOrAboveCap_CannotLevelFurther(byte level)
    {
        var (h, s, c, ch, _) = await Setup(ch =>
        {
            var companion = Comp(0, level: level);
            companion.Exp = companion.NextExp;
            companion.StatPoints = 4;
            ch.Companions[0] = companion;
        });

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_COMPANIONLUP_REQ, w => w.WriteByte(0)));

        Assert.Equal(level, ch.Companions[0].Level);
        Assert.Equal(4, ch.Companions[0].StatPoints);
        Assert.Equal(ch.Companions[0].NextExp, ch.Companions[0].Exp);
        Assert.False(c.Has(Msg.CS_COMPANIONLUPDATE_REQ));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    public async Task Gameforge5_ExpPotionAtCompanionCap_IsNotConsumed(byte level)
    {
        var (h, s, c, ch, _) = await Setup(ch => ch.Companions[0] = Comp(0, level: level));
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_USEPETITEM_REQ, w =>
        {
            w.WriteUInt32(3u << 16); // inventory 0, EXP potion slot 3
            w.WriteByte(0);
        }));

        Assert.Equal(0u, ch.Companions[0].Exp);
        Assert.Equal(3, ch.FindInven(0)!.FindItem(3)!.Count);
        Assert.False(c.Has(Msg.CS_UPDATECOMPANIONBYITEM_REQ));
    }

    [Fact]
    public async Task Gameforge5_LevelNineteen_CanReachTwenty_ThenStops()
    {
        var (h, s, c, ch, _) = await Setup(ch =>
        {
            var companion = Comp(0, level: 19);
            companion.Exp = companion.NextExp;
            ch.Companions[0] = companion;
        });
        var request = Req(Msg.CS_COMPANIONLUP_REQ, w => w.WriteByte(0));

        await h.Service.DispatchClientAsync(s, request);
        Assert.Equal(20, ch.Companions[0].Level);
        Assert.True(c.Has(Msg.CS_COMPANIONLUPDATE_REQ));

        ch.Companions[0].Exp = ch.Companions[0].NextExp;
        byte points = ch.Companions[0].StatPoints;
        c.Clear();
        await h.Service.DispatchClientAsync(s, request);
        Assert.Equal(20, ch.Companions[0].Level);
        Assert.Equal(points, ch.Companions[0].StatPoints);
        Assert.False(c.Has(Msg.CS_COMPANIONLUPDATE_REQ));
    }

    [Fact]
    public async Task EquippingAnItemOfTheSameKind_ReplacesIt_AndGivesTheOldOneBack()
    {
        var (h, s, c, ch, _) = await Setup(ch => ch.Companions[0] = Comp(0));
        byte[] Equip(byte itemSlot) => Req(Msg.CS_USECOMPANIONITEM_REQ, w => { w.WriteByte(0); w.WriteByte(itemSlot); w.WriteByte(0); });

        await h.Service.DispatchClientAsync(s, Equip(5));               // kind 111 → sub-slot 0
        await h.Service.DispatchClientAsync(s, Equip(6));               // kind 114 → sub-slot 1
        await h.Service.DispatchClientAsync(s, Equip(7));               // kind 111 again → replaces sub-slot 0

        Assert.Equal(new[] { CompItemA2, CompItemB }, ch.Companions[0].ItemIds);
        Assert.Equal(3, ch.FindInven(0)!.Items.Where(i => i.TemplateId == CompItemA).Sum(i => i.Count));   // 3 - 1 + 1 back
    }

    [Fact]
    public async Task ASameKindItemInTheSecondSlot_IsReplaced_NotDoubled()
    {
        var (h, s, _, ch, _) = await Setup(ch => { var x = Comp(0); x.ItemIds[1] = CompItemA; ch.Companions[0] = x; });
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_USECOMPANIONITEM_REQ, w => { w.WriteByte(0); w.WriteByte(7); w.WriteByte(0); }));
        Assert.Equal(new ushort[] { 0, CompItemA2 }, ch.Companions[0].ItemIds);
    }

    [Fact]
    public async Task Powder_RerollsTheBonus_ToOneNoCompanionHas()
    {
        var (h, s, c, ch, _) = await Setup(ch => { ch.Companions[0] = Comp(0, bonus: 11); ch.Companions[1] = Comp(1, bonus: 12); });
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_USECOMPANIONPOWDER_REQ, w => { w.WriteByte(0); w.WriteByte(4); w.WriteByte(0); }));
        Assert.DoesNotContain(ch.Companions[0].BonusId, new byte[] { 11, 12 });
        Assert.True(c.Has(Msg.CS_UPDATECOMPANIONBONUS_REQ));
    }

    // ================================ stamina timer ================================

    [Fact]
    public async Task EveryMinute_TheSummonedCompanionTires_AndGainsExp_AndIsSentAwayWhenExhausted()
    {
        var (h, s, c, ch, _) = await Setup(ch => { ch.Companions[0] = Comp(0, life: 2401); ch.CompanionSlot = 0; });

        for (int i = 0; i < 60; i++) await h.Service.OnTimerAsync();

        var comp = ch.Companions[0];
        Assert.Equal(2400u, comp.Life);                                   // -1 above 2400
        Assert.Equal(30u, comp.Exp);                                      // +30 below 10000 stamina
        var r = new PacketReader(c.Last(Msg.CS_UPDATECOMPANIONBYSYSTEM_REQ)!);
        Assert.Equal(0, r.ReadByte()); Assert.Equal(2400u, r.ReadUInt32()); Assert.Equal(30u, r.ReadUInt32());
        Assert.Equal((byte)0, ch.CompanionSlot);

        for (int i = 0; i < 60; i++) await h.Service.OnTimerAsync();
        Assert.Equal(2395u, comp.Life);                                   // -5 at or below 2400
        Assert.Equal((byte)0xFF, ch.CompanionSlot);                      // under 2400: sent away
    }

    // ================================ owner bonuses ================================

    [Fact]
    public void TheOwnerStat_IsTheSummonedStat_PlusOnePerCompanion_PlusFiveForStrongOnes()
    {
        var ch = new Character();
        var a = Comp(0, level: 12); a.Stats[0] = 7;                      // strong: level 11+, stamina ≥ 2400
        var b = Comp(1, level: 3);
        ch.Companions[0] = a; ch.Companions[1] = b;
        Assert.Equal(0f, MapService.CompanionStat(ch, 1));               // nothing summoned
        ch.CompanionSlot = 0;
        Assert.Equal(7f + (1 + 5) + 1, MapService.CompanionStat(ch, 1));
    }

    [Fact]
    public void TheOwnerBonus_ComesFromTheFirstEligibleCompanion()
    {
        var t = Store();
        var ch = new Character();
        ch.Companions[0] = Comp(0, bonus: 11, level: 5);                  // level 5, not summoned: not eligible
        ch.Companions[1] = Comp(1, bonus: 12, level: 5);
        ch.CompanionSlot = 1;
        Assert.Equal(0f, MapService.CompanionBonusValue(ch, 11, t));
        Assert.Equal(1f + 2f * 4, MapService.CompanionBonusValue(ch, 12, t));   // the summoned one counts at any level
        ch.Companions[1].Life = 2399;
        Assert.Equal(0f, MapService.CompanionBonusValue(ch, 12, t));      // too tired
    }

    // ================================ rune stamping ================================

    [Fact]
    public async Task ABoughtRune_TakesItsSpeciesFromTheRuneChart()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Buyer", MaxHp = 100, Hp = 100, Country = 1 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        var (s, _) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        s.Char!.Country = 1; s.Char.AidCountry = 0;
        s.Char.SetMoneyTotal(1_000_000);
        var npc = new Npc { Id = NpcId, Type = 2, Country = 3, MapId = 0 };
        npc.Items[Rune] = t.Item(Rune)!;
        h.Service.AddNpc(npc);

        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(NpcId, Rune, count: 1));

        var bought = ch.FindInven(0xFF)!.Items.Single(i => i.TemplateId == Rune);
        Assert.Equal((uint)Species, bought.Ext[Item.IevCompanion]);
    }
}
