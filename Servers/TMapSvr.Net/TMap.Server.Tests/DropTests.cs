using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Monster drops: ranged picks by group, drop level, id range and country; prebuilt magic items; sealed gamble items;
/// random durability; magic options from the row's odds; and the 25-level anti-farm for a party.</summary>
public class DropTests
{
    private const ushort Fixed = 300, Ranged = 410, HighLevel = 420, OutOfRange = 600, ForeignOnly = 411, Sealed = 13, Magic = 7;

    private static ItemTemplate Tpl(ushort id, byte group = 4, byte dropLevel = 5, byte country = 3, byte type = 1, byte canMagic = 0,
        uint dura = 0) => new(id, 0, new[] { 1f, 0f, 0f, 0f }, Type: type, Kind: 1, CanMagic: canMagic, DuraMax: dura, ItemCountry: country,
        GroupId: group, DropLevel: dropLevel, InitState: 1, Weight: 1);

    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        foreach (var tpl in new[] { Tpl(Fixed, dropLevel: 0, dura: 1000), Tpl(Ranged), Tpl(HighLevel, dropLevel: 20), Tpl(OutOfRange),
                     Tpl(ForeignOnly, group: 6, country: 0), Tpl(Sealed, dropLevel: 0, type: 13), Tpl(Magic, dropLevel: 0, canMagic: 1) })
        {
            t.Items[tpl.ItemId] = tpl;
            if (tpl.DropLevel == 0) continue;
            if (!t.DropItems.TryGetValue(tpl.GroupId, out var byLevel)) t.DropItems[tpl.GroupId] = byLevel = new();
            if (!byLevel.TryGetValue(tpl.DropLevel, out var list)) byLevel[tpl.DropLevel] = list = new();
            list.Add(tpl);
        }
        var opt = new MagicTemplate(9, 0, 100, Kind: 1, IsMagic: 1, IsRare: 1, RareBound: 100, MaxBound: 100);
        t.Magics[9] = opt;
        t.MagicsByKind[1] = new List<MagicTemplate> { opt };
        t.QuestItems[75] = new QuestItemRow(75, new FullItemRow(0, 0, 0, Fixed, 0, 1, 0, 0, 0, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0, 0),
            0, 0, DropLevel: 5);
        return t;
    }

    private static async Task<(MapTestHarness H, Monster Mob)> Kill(MonItemRow row, Action<Character>? setup = null)
    {
        var h = new MapTestHarness(Store());
        var ch = new Character { CharId = 1, Name = "Looter", Level = 10, Country = 1, MaxHp = 100, Hp = 100 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        setup?.Invoke(ch);
        var mob = new Monster { Id = 0x60001, ChartId = 500, Level = 5, MaxHp = 10, Hp = 10, MaxMp = 50, Mp = 50, DefendPower = 100,
            PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0, ItemProb = 100, DropCount = 1, MaxWeight = row.Weight, DropRows = new[] { row } };
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        h.Service.LootRng = new Random(1);
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mob.Id));
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, mob.Id));
        return (h, mob);
    }

    private static MonItemRow Row(ushort item, byte chart, ushort min = 0, ushort max = 0, byte lvMin = 0, byte lvMax = 0, byte magic = 0, byte magicOpt = 0)
        => new(item, 1, chart, 100, 100, 100, 100, min, max, lvMin, lvMax, magic, 0, magicOpt, 0);

    [Fact]
    public async Task RangedRow_PicksAnItemOfItsGroupLevelsAndIds()
    {
        var (_, mob) = await Kill(Row(0, 4, min: 400, max: 499, lvMin: 1, lvMax: 10));
        Assert.Equal(Ranged, Assert.Single(mob.CorpseInven.Items).TemplateId);
    }

    [Fact]
    public async Task RangedRow_SkipsAnotherCountrysItems()
    {
        var (_, mob) = await Kill(Row(0, 6, min: 400, max: 499, lvMin: 1, lvMax: 10));
        Assert.Empty(mob.CorpseInven.Items);
    }

    [Fact]
    public async Task MagicRow_DropsAPrebuiltMagicItem()
    {
        var (_, mob) = await Kill(Row(0, 0, min: 70, max: 80, lvMin: 1, lvMax: 10));
        Assert.Equal(Fixed, Assert.Single(mob.CorpseInven.Items).TemplateId);
    }

    [Fact]
    public async Task FixedItem_GetsARandomDurability()
    {
        var (_, mob) = await Kill(Row(Fixed, 1));
        var item = Assert.Single(mob.CorpseInven.Items);
        Assert.InRange(item.DuraMax, 110u, 1000u);
        Assert.Equal(item.DuraMax, item.DuraCur);
    }

    [Fact]
    public async Task GambleItem_DropsSealedAtTheMonstersLevel()
    {
        var (_, mob) = await Kill(Row(Sealed, 1, magic: 150, magicOpt: 2));
        var item = Assert.Single(mob.CorpseInven.Items);
        Assert.Equal(5, item.GLevel);
        Assert.Empty(item.Magic);
    }

    [Fact]
    public async Task SureMagicRow_RollsAnOption()
    {
        var (_, mob) = await Kill(Row(Magic, 1, magic: 150, magicOpt: 1));
        Assert.Equal(9, Assert.Single(Assert.Single(mob.CorpseInven.Items).Magic).Id);
    }

    [Fact]
    public async Task Party25LevelsAbove_GetsNothing()
    {
        var (_, mob) = await Kill(Row(Fixed, 1), ch => { ch.Level = 40; ch.PartyId = 5; });
        Assert.Equal(2, mob.KeeperType);
        Assert.Empty(mob.CorpseInven.Items);
    }
}
