using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Phase 17 — loot/exp on monster death: the keeper gains attacker-level-scaled exp (level-up ⇒
/// full heal + skill points + CS_LEVEL_ACK/CS_EXP_ACK), and money drops onto a lootable corpse taken via
/// CS_MONMONEYTAKE. Anti-farm: 0 exp / no drops when far above the monster level.</summary>
public class LootExpTests
{
    // MaxHP 200 / MaxMP 100 (for the full-heal-on-level-up check).
    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Formulas[8] = new FormulaRow(200, 0f, 0f);
        t.Formulas[19] = new FormulaRow(100, 0f, 0f);
        t.Classes[1] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[1] = new StatSeed(1, 1, 1, 1, 1, 1);
        return t;
    }

    private static Character Hero(uint hp = 200, uint mp = 100)
        => new() { CharId = 1, Name = "Hero", Class = 1, Race = 1, Hp = hp, Mp = mp };

    // A MaxHP-10 monster (2 naked hits kill it; each ≥10% ⇒ the attacker becomes keeper). A non-empty drop
    // table (MaxWeight > 0, DropCount 0) is required for money to drop (C++ AddItem gates on m_dwMaxWeight)
    // while dropping no items.
    private static Monster Mob(uint exp = 0, byte moneyProb = 0, uint minMoney = 0, uint maxMoney = 0)
        => new() { Id = 0x20001, ChartId = 500, Level = 10, MaxHp = 10, Hp = 10, MaxMp = 50, Mp = 50,
            DefendPower = 100, PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0,
            Exp = exp, MoneyProb = moneyProb, MinMoney = minMoney, MaxMoney = maxMoney,
            MaxWeight = 1, DropCount = 0, DropRows = new[] { new MonItemRow(0, 1, 0, 0, 0, 0, 0) } };

    private static async Task Kill(MapTestHarness h, ClientSession s, uint monId)
    {
        // 2 hits (5..6 each with seed 1) ⇒ ≥10 total ⇒ dead.
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, monId));
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, monId));
    }

    [Fact]
    public async Task Kill_AwardsScaledExp_ToKeeper()
    {
        var t = Store();
        t.LevelExp[10] = 100_000; // no level-up
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: Hero());
        var mob = Mob(exp: 1000);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await Kill(h, s, mob.Id);

        // GetExp = 1000 * MonExpRate(10)=62 / 100 = 620; LevelRate(10,10)=1.0 ⇒ gain 620.
        Assert.Equal(620u, h.State.ByChar[1].Char!.Exp);
        Assert.Equal((byte)10, h.State.ByChar[1].Char!.Level); // no level-up
        Assert.True(c.Has(Msg.CS_EXP_ACK));
        Assert.False(c.Has(Msg.CS_LEVEL_ACK));
    }

    [Fact]
    public async Task Kill_LevelUp_FullHealsAndGrantsSkillPoints()
    {
        var t = Store();
        t.LevelExp[10] = 500; t.LevelSkillPoint[10] = 3; t.LevelExp[11] = 100_000;
        var ch = Hero(hp: 50, mp: 50);
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        var mob = Mob(exp: 1000); // gain 620 ≥ 500 ⇒ level 11
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await Kill(h, s, mob.Id);

        Assert.Equal((byte)11, ch.Level);
        Assert.Equal(200u, ch.Hp);          // full heal on level-up
        Assert.Equal(100u, ch.Mp);
        Assert.Equal(3u, ch.SkillPoint);    // TLEVELCHART[10].bSkillPoint
        Assert.True(c.Has(Msg.CS_LEVEL_ACK));
        Assert.True(c.Has(Msg.CS_EXP_ACK));
    }

    [Fact]
    public async Task Kill_TooHighLevel_NoExp()
    {
        var t = Store();
        var ch = Hero();
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        ch.Level = 25;                       // 15 levels above the level-10 monster ⇒ LevelRate 0
        var mob = Mob(exp: 1000);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await Kill(h, s, mob.Id);

        Assert.Equal(0u, ch.Exp);            // 10+ levels above ⇒ 0 exp
        Assert.False(c.Has(Msg.CS_EXP_ACK)); // zero gain ⇒ no packet
    }

    [Fact]
    public async Task Kill_DropsMoney_CorpsePersists_ThenPickup()
    {
        var t = Store();
        var ch = Hero();
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        var mob = Mob(moneyProb: 100, minMoney: 5, maxMoney: 10);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        h.Service.LootRng = new Random(1);
        c.Clear();

        await Kill(h, s, mob.Id);

        Assert.True(c.Has(Msg.CS_DIE_ACK));
        Assert.False(c.Has(Msg.CS_DELMON_ACK));          // corpse persists (has loot)
        Assert.NotNull(h.State.FindMonster(mob.Id));
        uint loot = mob.CorpseMoney;
        Assert.InRange(loot, 5u, 9u);                    // 5 + rand()%5

        c.Clear();
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonMoneyTakeReq(mob.Id));

        Assert.Equal((long)loot, ch.MoneyTotal);         // credited to the keeper
        Assert.Equal(0u, mob.CorpseMoney);               // corpse emptied
        Assert.True(c.Has(Msg.CS_MONEY_ACK));
    }

    [Fact]
    public async Task Kill_FarAbove_NoMoney_CorpseDespawnsImmediately()
    {
        var t = Store();
        var ch = Hero();
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        ch.Level = 40;                                   // 30 levels above ⇒ no drops
        var mob = Mob(moneyProb: 100, minMoney: 5, maxMoney: 10);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await Kill(h, s, mob.Id);

        Assert.Equal(0u, mob.CorpseMoney);               // suppressed
        Assert.Null(h.State.FindMonster(mob.Id));        // no loot ⇒ despawned + rearmed
        Assert.True(c.Has(Msg.CS_DELMON_ACK));
    }

    [Fact]
    public async Task MonItemList_ReportsCorpseMoney()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: Hero());
        var mob = Mob(moneyProb: 100, minMoney: 1500, maxMoney: 1501); // exactly 1500 ⇒ 1 silver 500 copper
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        h.Service.LootRng = new Random(1);
        c.Clear();

        await Kill(h, s, mob.Id);
        c.Clear();
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonItemListReq(mob.Id));

        var r = new PacketReader(c.Last(Msg.CS_MONITEMLIST_ACK)!);
        Assert.Equal((byte)0, r.ReadByte());   // bRet OK
        r.ReadByte();                          // bUpdate
        Assert.Equal(mob.Id, r.ReadUInt32());  // dwMonID
        Assert.Equal(0u, r.ReadUInt32());      // gold
        Assert.Equal(1u, r.ReadUInt32());      // silver (1500 / 1000)
        Assert.Equal(500u, r.ReadUInt32());    // cooper (1500 % 1000)
        Assert.Equal((byte)0, r.ReadByte());   // item count (deferred)
    }
}
