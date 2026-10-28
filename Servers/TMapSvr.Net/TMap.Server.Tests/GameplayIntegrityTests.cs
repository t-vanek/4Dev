using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Packet-level regressions for the gameplay audit. These intentionally use the raw dispatcher:
/// a missing or rejected cast must never be supplied by a test helper.</summary>
public sealed class GameplayIntegrityTests
{
    private static SkillTemplate Attack(ushort id = 200, byte repeats = 0, byte range = 0)
    {
        var t = new SkillTemplate(id, 0, 10, 1, 0, 0, 1, 10, 0, 10000, 0, 0, 0, 0, 0, 0xFFFF,
            ClassId: uint.MaxValue, Rate1stX: 100, RepeatCount: repeats, TargetRange: range);
        t.Data.Add(new SkillDataRow(0, 1, 1, 14, 2, 20, 0, 0));
        return t;
    }

    private static TemplateStore Charts()
    {
        var t = new TemplateStore();
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0, 0);
        t.Formulas[8] = new FormulaRow(100, 0, 0);
        t.Formulas[19] = new FormulaRow(100, 0, 0);
        t.Skills[200] = Attack();
        t.Items[300] = new ItemTemplate(300, 0, new float[4], Stack: 99, IsSell: 1, ClassId: uint.MaxValue);
        t.Items[301] = new ItemTemplate(301, 0, new float[4], Stack: 99, IsSell: 0, ClassId: uint.MaxValue);
        return t;
    }

    private static Character Hero(uint id, TemplateStore t, bool learned = true)
    {
        var ch = new Character { CharId = id, Name = "Hero" + id, Country = 1, AidCountry = 3, Level = 10,
            MaxHp = 100, Hp = 100, MaxMp = 100, Mp = 100 };
        ch.Invens.Add(new Inven { InvenId = 0xFF, SlotCount = 30 });
        if (learned) ch.Skills.Add(new Skill { SkillId = 200, Level = 1, Template = t.Skills[200] });
        return ch;
    }

    private static Monster Mob() => new() { Id = 0x30001, ChartId = 500, Level = 5, MaxHp = 100, Hp = 100,
        MaxMp = 100, Mp = 100, PosX = 100, PosZ = 100, Channel = 1, MapId = 0, Country = 3 };

    private static byte[] Finish(ushort skill, params (uint Id, byte Type)[] targets)
    {
        var w = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        w.WriteUInt32(1); w.WriteUInt32(1); w.WriteByte(1);
        w.WriteFloat(100); w.WriteFloat(0); w.WriteFloat(100); w.WriteUInt16(skill);
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt16(0); w.WriteByte((byte)targets.Length);
        foreach (var (id, type) in targets) { w.WriteUInt32(id); w.WriteByte(type); }
        return w.ToArray();
    }

    private static byte[] Loop(uint caster, ushort skill, uint target)
    {
        var w = new PacketWriter(Msg.CS_LOOPSKILL_REQ);
        w.WriteUInt32(caster); w.WriteByte(1); w.WriteByte(1); w.WriteUInt16(0); w.WriteUInt16(skill);
        w.WriteFloat(100); w.WriteFloat(0); w.WriteFloat(100);
        w.WriteByte(1); w.WriteUInt32(target); w.WriteByte(2); w.WriteByte(1);
        return w.ToArray();
    }

    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, Character Ch, Monster Mon)> Enter(
        TemplateStore? t = null, bool learned = true)
    {
        t ??= Charts();
        var h = new MapTestHarness(t); var ch = Hero(1, t, learned);
        var (s, c) = await h.EnterAsync(1, 1, 1, name: ch.Name, x: 100, z: 100, preSeeded: ch);
        var mon = Mob(); h.Service.SpawnMonster(mon); h.Service.NowMs = 100000; h.Service.CombatRng = new Random(1);
        c.Clear();
        return (h, s, c, ch, mon);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignPlayerCannotSpendAnotherPlayersManaOrCooldown(bool loop)
    {
        var t = Charts(); var (h, s, _, ch, mon) = await Enter(t);
        var victim = Hero(2, t);
        await h.EnterAsync(2, 2, 2, x: 101, z: 100, preSeeded: victim);
        await h.Service.DispatchClientAsync(s, loop ? Loop(2, 200, mon.Id) : MapTestHarness.SkillUseReq(2, 200));
        Assert.Equal(100u, victim.Mp);
        Assert.Equal(0u, victim.Skills[0].GetReuseRemainTick(h.Service.NowMs));
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200, targets: new[] { (mon.Id, (byte)2) }));
        Assert.Equal(90u, ch.Mp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImpactRequiresAPaidCastAndCannotBeReplayedThroughEitherPacket(bool legacy)
    {
        var (h, s, _, ch, mon) = await Enter();
        byte[] Hit(bool old) => old ? MapTestHarness.DefendReq(1, mon.Id, skillId: 200) : Finish(200, (mon.Id, 2));
        await h.Service.DispatchClientAsync(s, Hit(legacy));
        Assert.Equal(100u, mon.Hp);
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200, targets: new[] { (mon.Id, (byte)2) }));
        await h.Service.DispatchClientAsync(s, Hit(legacy));
        Assert.Equal(80u, mon.Hp);
        Assert.Equal(90u, ch.Mp);
        Assert.True(ch.Skills[0].GetReuseRemainTick(h.Service.NowMs) > 0);
        await h.Service.DispatchClientAsync(s, Hit(!legacy));
        await h.Service.DispatchClientAsync(s, Hit(legacy));
        Assert.Equal(80u, mon.Hp);
        Assert.Equal(90u, ch.Mp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnlearnedSkillsCannotAuthorizeAnImpact(bool zeroEntry)
    {
        var t = Charts(); var (h, s, c, ch, mon) = await Enter(t, learned: false);
        if (zeroEntry) ch.Skills.Add(new Skill { SkillId = 200, Level = 0, Template = t.Skills[200] });
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200, targets: new[] { (mon.Id, (byte)2) }));
        Assert.Equal((byte)SkillUseResult.NotFound, new PacketReader(c.Last(Msg.CS_SKILLUSE_ACK)!).ReadByte());
        await h.Service.DispatchClientAsync(s, Finish(200, (mon.Id, 2)));
        Assert.Equal(100u, mon.Hp);
    }

    [Fact]
    public async Task DeclaredRepeatsWorkButAdditionalImpactsDoNot()
    {
        var t = Charts(); t.Skills[200] = Attack(repeats: 1);
        var (h, s, _, _, mon) = await Enter(t);
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200, targets: new[] { (mon.Id, (byte)2) }));
        for (int i = 0; i < 3; i++) await h.Service.DispatchClientAsync(s, Finish(200, (mon.Id, 2)));
        Assert.Equal(60u, mon.Hp);
    }

    [Fact]
    public async Task MultipleMissilesHaveSeparateAuthorizedImpacts()
    {
        var t = Charts(); t.Skills[200].Data.Add(new SkillDataRow(0, 1, 0, 36, 1, 3, 0, 0));
        var (h, s, _, _, mon) = await Enter(t);
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200, targets: new[] { (mon.Id, (byte)2) }));
        for (int i = 0; i < 4; i++) await h.Service.DispatchClientAsync(s, Finish(200, (mon.Id, 2)));
        Assert.Equal(40u, mon.Hp);
    }

    [Fact]
    public async Task GroundAreaCastCanHitTargetsFoundAtImpactWithoutReplayingThem()
    {
        var t = Charts(); t.Skills[200] = Attack(range: 1);
        var (h, s, _, _, mon) = await Enter(t);
        var other = Mob(); other.Id++; h.Service.SpawnMonster(other);
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200));
        var impact = Finish(200, (mon.Id, 2), (other.Id, 2));
        await h.Service.DispatchClientAsync(s, impact);
        await h.Service.DispatchClientAsync(s, impact);
        Assert.Equal(80u, mon.Hp); Assert.Equal(80u, other.Hp);
    }

    [Fact]
    public async Task LinkedImpactCanPrecedeParentButCannotRunWithoutItsCast()
    {
        var t = Charts(); t.Skills[201] = Attack(201) with { TriggerId = 200 };
        var (h, s, _, _, mon) = await Enter(t);
        await h.Service.DispatchClientAsync(s, Finish(201, (mon.Id, 2)));
        Assert.Equal(100u, mon.Hp);
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200, targets: new[] { (mon.Id, (byte)2) }));
        await h.Service.DispatchClientAsync(s, Finish(201, (mon.Id, 2)));
        await h.Service.DispatchClientAsync(s, Finish(200, (mon.Id, 2)));
        await h.Service.DispatchClientAsync(s, Finish(201, (mon.Id, 2)));
        Assert.Equal(60u, mon.Hp);
    }

    [Fact]
    public async Task ExpiredCastAndUnannouncedDirectTargetAreRejected()
    {
        var (h, s, _, _, mon) = await Enter();
        var announced = Mob(); announced.Id = 42; h.Service.SpawnMonster(announced);
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200, targets: new[] { (42u, (byte)2) }));
        await h.Service.DispatchClientAsync(s, Finish(200, (mon.Id, 2)));
        Assert.Equal(100u, mon.Hp);
        h.Service.NowMs += 30001;
        await h.Service.DispatchClientAsync(s, Finish(200, (42u, 2)));
        Assert.False(s.Conn is FakeClientChannel { Closed: true });
        Assert.Equal(100u, announced.Hp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopImpactsRequireALoopSkillAndRespectItsDelay(bool looping)
    {
        var t = Charts(); t.Skills[200] = Attack() with { LoopDelay = looping ? 3000u : 0u };
        var (h, s, _, ch, mon) = await Enter(t);
        await h.Service.DispatchClientAsync(s, Loop(1, 200, mon.Id));
        await h.Service.DispatchClientAsync(s, Finish(200, (mon.Id, 2)));
        await h.Service.DispatchClientAsync(s, Loop(1, 200, mon.Id));
        await h.Service.DispatchClientAsync(s, Finish(200, (mon.Id, 2)));
        Assert.Equal(looping ? 80u : 100u, mon.Hp);
        Assert.Equal(looping ? 90u : 100u, ch.Mp);
        if (looping)
        {
            h.Service.NowMs += 3000;
            await h.Service.DispatchClientAsync(s, Loop(1, 200, mon.Id));
            await h.Service.DispatchClientAsync(s, Finish(200, (mon.Id, 2)));
            Assert.Equal(60u, mon.Hp); Assert.Equal(80u, ch.Mp);
        }
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)8)]
    [InlineData((byte)53)]
    public async Task AttackBlockingStatusStopsPlayerCastAndMonsterAttack(byte status)
    {
        var t = MapTestHarness.WithMonsterMelee(Charts());
        var hold = Attack(201); hold.Data.Clear(); hold.Data.Add(new SkillDataRow(3, 6, 0, status, 1, 1, 0, 0));
        var (h, s, c, ch, mon) = await Enter(t);
        ch.MaintainSkills.Add(new MaintainSkill { SkillId = 201, Template = hold, StartTick = 100000, MaintainTick = 10000 });
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 200));
        Assert.Equal((byte)SkillUseResult.SpeedyUse, new PacketReader(c.Last(Msg.CS_SKILLUSE_ACK)!).ReadByte());
        Assert.Equal(100u, ch.Mp);
        mon.MaintainSkills.Add(new MaintainSkill { SkillId = 201, Template = hold, StartTick = 100000, MaintainTick = 10000 });
        mon.HostId = 1; mon.TargetId = 1; mon.TargetType = 1; mon.Mode = 1;
        mon.Ai = new AiScript(1);
        mon.Ai.Bind((byte)AiTrigger.Defend, 0, new AiBinding(new AiCommandTemplate(1, AiCommandKind.BeginAtk), 0, false));
        c.Clear(); h.Service.OnAiEvent(mon, AiTrigger.Defend, eventHost: 1, rhId: 1, rhType: 1);
        Assert.False(c.Has(Msg.CS_MONATTACK_ACK));
        mon.MaintainSkills.Clear(); h.Service.OnAiEvent(mon, AiTrigger.Defend, eventHost: 1, rhId: 1, rhType: 1);
        Assert.True(c.Has(Msg.CS_MONATTACK_ACK));
    }

    [Fact]
    public async Task MonsterMagicSkillDrainsMpAndAppliesItsDebuffWithoutPhysicalHpDamage()
    {
        var t = Charts(); var drain = Attack(202) with { Duration = 10000 };
        drain.Data.Clear(); drain.Data.Add(new SkillDataRow(0, 1, 3, 22, 2, 20, 0, 0));
        drain.Data.Add(new SkillDataRow(3, 6, 0, 8, 1, 1, 0, 0));
        t.Skills[202] = drain; t.MonsterTemplates[500] = new MonsterTemplate(500, 5, 0, Skill1: 202);
        var (h, s, c, ch, mon) = await Enter(t);
        mon.HostId = 1; mon.AtkMin = 50; mon.AtkMax = 51; mon.AttackLevel = 10;
        mon.Attr = new MonAttrRow(0, 5, 100, 100, 0, MagicAp: 30, MagicAtkLevel: 10);
        await h.Service.DispatchClientAsync(s, MapTestHarness.DefendReq(mon.Id, 1, attackType: 2, targetType: 1, skillId: 202));
        Assert.Equal(100u, ch.Hp); Assert.Equal(80u, ch.Mp);
        Assert.Contains(ch.MaintainSkills, m => m.SkillId == 202);
        Assert.True(c.Has(Msg.CS_HPMP_ACK));
    }

    private static void AddItem(Character ch, TemplateStore t, ushort id, byte count, byte slot = 0) =>
        ch.FindInven(0xFF)!.Items.Add(new Item { ItemSlot = slot, TemplateId = id, Count = count, Template = t.Items[id] });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreRejectsAChangedStackOrUntradableSlotReplacement(bool replace)
    {
        var t = Charts(); var (h, s, _, seller, _) = await Enter(t); var buyer = Hero(2, t);
        buyer.SetMoneyTotal(100); AddItem(seller, t, 300, 5);
        if (replace) AddItem(seller, t, 301, 1, 1);
        var (b, c) = await h.EnterAsync(2, 2, 2, name: buyer.Name, x: 101, z: 100, preSeeded: buyer);
        await h.Service.DispatchClientAsync(s, MapTestHarness.StoreOpenReq("Shop", (0u, 0u, 1u, 0u, (byte)0xFF, (byte)0, (byte)5)));
        await h.Service.DispatchClientAsync(s, replace ? MapTestHarness.MoveItemReq(0xFF, 1, 0xFF, 0, 1)
            : MapTestHarness.MoveItemReq(0xFF, 0, 0xFF, 1, 4));
        await h.Service.DispatchClientAsync(b, MapTestHarness.StoreItemBuyReq(seller.Name, 0, 5));
        Assert.Equal((byte)StoreResult.ItemNoItem, new PacketReader(c.Last(Msg.CS_STOREITEMBUY_ACK)!).ReadByte());
        Assert.Empty(buyer.FindInven(0xFF)!.Items); Assert.Equal(100, buyer.MoneyTotal);
        Assert.Equal(5, seller.FindInven(0xFF)!.Items.Where(i => i.TemplateId == 300).Sum(i => i.Count));
        Assert.False(s.Store.IsOpen);
    }

    [Theory]
    [InlineData((byte)10, (byte)10, 50, 50)]
    [InlineData((byte)10, (byte)20, 34, 66)]
    public async Task PartyMoneyIsLevelWeightedAndRemainderStaysWithCollector(byte aLevel, byte bLevel, int aMoney, int bMoney)
    {
        var t = Charts(); var (h, s, _, a, mon) = await Enter(t); var b = Hero(2, t);
        await h.EnterAsync(2, 2, 2, x: 101, z: 100, preSeeded: b);
        a.Level = aLevel; b.Level = bLevel; a.PartyId = b.PartyId = 42;
        mon.Hp = 0; mon.Dead = true; mon.AddDamage(1, 42, 100); mon.CorpseMoney = 100;
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonMoneyTakeReq(mon.Id));
        Assert.Equal(aMoney, a.MoneyTotal); Assert.Equal(bMoney, b.MoneyTotal);
        Assert.Equal(0u, mon.CorpseMoney);
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonMoneyTakeReq(mon.Id));
        Assert.Equal(100, a.MoneyTotal + b.MoneyTotal);
    }

    [Fact]
    public async Task HunterModeDeniesDirectTakeButAllowsTheActualHunter()
    {
        var t = Charts(); var (h, s, _, a, mon) = await Enter(t); var hunter = Hero(2, t);
        var (hs, _) = await h.EnterAsync(2, 2, 2, x: 101, z: 100, preSeeded: hunter);
        a.PartyId = hunter.PartyId = 42; a.PartyType = hunter.PartyType = 2;
        mon.Hp = 0; mon.Dead = true; mon.AddDamage(2, 42, 100); mon.AddAggro(2, 2, 1, 1, 100);
        mon.CorpseInven.Items.Add(new Item { ItemSlot = 0, TemplateId = 300, Count = 1, Template = t.Items[300] });
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonItemTakeReq(mon.Id, 0));
        Assert.Empty(a.FindInven(0xFF)!.Items); Assert.Single(mon.CorpseInven.Items);
        await h.Service.DispatchClientAsync(hs, MapTestHarness.MonItemTakeReq(mon.Id, 0));
        Assert.Single(hunter.FindInven(0xFF)!.Items); Assert.Empty(mon.CorpseInven.Items);
    }

    [Fact]
    public async Task TakeAllUsesTheSamePartyMoneySplit_AndStillGrantsTheItemOnce()
    {
        var t = Charts(); var (h, s, _, a, mon) = await Enter(t); var b = Hero(2, t);
        await h.EnterAsync(2, 2, 2, x: 101, z: 100, preSeeded: b);
        a.Level = 10; b.Level = 20; a.PartyId = b.PartyId = 42;
        mon.Hp = 0; mon.Dead = true; mon.AddDamage(1, 42, 100); mon.CorpseMoney = 100;
        mon.CorpseInven.Items.Add(new Item { ItemSlot = 0, TemplateId = 300, Count = 1, Template = t.Items[300] });
        byte[] request = new PacketWriter(Msg.CS_MONITEMTAKEALL_REQ).WriteUInt32(mon.Id).ToArray();

        await h.Service.DispatchClientAsync(s, request);

        Assert.Equal(34, a.MoneyTotal); Assert.Equal(66, b.MoneyTotal);
        Assert.Single(a.FindInven(0xFF)!.Items); Assert.Empty(mon.CorpseInven.Items); Assert.Equal(0u, mon.CorpseMoney);
        await h.Service.DispatchClientAsync(s, request);
        Assert.Equal(34, a.MoneyTotal); Assert.Equal(66, b.MoneyTotal); Assert.Single(a.FindInven(0xFF)!.Items);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    [InlineData(4, 0)] [InlineData(4, 1)] [InlineData(4, 2)]
    [InlineData(5, 0)] [InlineData(5, 1)] [InlineData(5, 2)]
    public async Task LootEntryPointsPreserveHunterLockTradeAndMapGuards(int denial, int path)
    {
        var t = Charts(); var (h, s, _, ch, mon) = await Enter(t);
        ch.PartyId = 42; mon.AddDamage(1, 42, 100); mon.Hp = 0; mon.Dead = true; mon.CorpseMoney = 100;
        mon.CorpseInven.Items.Add(new Item { ItemSlot = 0, TemplateId = 300, Count = 1, Template = t.Items[300] });
        switch (denial)
        {
            case 0: ch.PartyType = 2; mon.AddAggro(2, 2, 1, 1, 100); break; // another hunter
            case 1: mon.InvenLock = 2; break;
            case 2: ch.MapId = 1; break;
            case 3: mon.Channel = 2; break;
            case 4: s.Deal.SetTarget("Trading partner"); break;
            case 5: s.IsMain = false; break;
        }
        byte[] request = path switch
        {
            0 => MapTestHarness.MonMoneyTakeReq(mon.Id),
            1 => MapTestHarness.MonItemTakeReq(mon.Id, 0),
            _ => new PacketWriter(Msg.CS_MONITEMTAKEALL_REQ).WriteUInt32(mon.Id).ToArray(),
        };
        await h.Service.DispatchClientAsync(s, request);
        Assert.Equal(0, ch.MoneyTotal); Assert.Equal(100u, mon.CorpseMoney);
        Assert.Empty(ch.FindInven(0xFF)!.Items); Assert.Single(mon.CorpseInven.Items);
    }

    [Theory]
    [InlineData((byte)7, false)]
    [InlineData((byte)3, false)]
    [InlineData((byte)3, true)]
    public async Task QuestGrantsMagicItemOrSkillBeforeConsumingIngredients(byte rewardType, bool levelZero)
    {
        var t = Charts();
        t.QuestItems[301] = new QuestItemRow(301,
            new FullItemRow(0, 0, 0, 301, 2, 1, 0, 0, 0, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0, 0), 1, 0);
        var q = new QuestTemplate { QuestId = 6000, Type = (byte)QuestType.Craft, TriggerType = 3, TriggerId = 500 };
        q.Terms.Add(new QuestTerm(300, 2, 1));
        q.Rewards.Add(new QuestReward(rewardType == 7 ? 301u : 200u, rewardType, 1, 0, 1)); t.Quests[q.QuestId] = q;
        var (h, s, c, ch, _) = await Enter(t, learned: false); AddItem(ch, t, 300, 1);
        if (levelZero) ch.Skills.Add(new Skill { SkillId = 200, Level = 0, Template = t.Skills[200] });
        h.Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 }); h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(q.QuestId));
        Assert.Equal((byte)QuestResult.Success, new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.DoesNotContain(ch.FindInven(0xFF)!.Items, i => i.TemplateId == 300);
        if (rewardType == 7)
        {
            var item = Assert.Single(ch.FindInven(0xFF)!.Items);
            Assert.Equal((ushort)301, item.TemplateId); Assert.Equal((byte)2, item.Level);
            Assert.True(item.EndTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        else Assert.Contains(ch.Skills, k => k.SkillId == 200 && k.Level == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrUnreceivableRewardPreservesIngredientsMoneyAndSkills(bool missingTemplate)
    {
        var t = Charts();
        if (!missingTemplate)
            t.QuestItems[301] = new QuestItemRow(301,
                new FullItemRow(0, 0, 0, 301, 2, 1, 0, 0, 0, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0, 0), 0, 0);
        var q = new QuestTemplate { QuestId = 6000, Type = (byte)QuestType.Craft, TriggerType = 3, TriggerId = 500 };
        q.Terms.Add(new QuestTerm(300, 2, 1));
        q.Rewards.Add(new QuestReward(301, 7, 1, 0, 1));
        q.Rewards.Add(new QuestReward(200, 3, 1, 0, 1));
        q.Rewards.Add(new QuestReward(100, 1, 1, 0, 1));
        t.Quests[q.QuestId] = q;
        var (h, s, c, ch, _) = await Enter(t, learned: false); AddItem(ch, t, 300, 1);
        for (byte slot = 1; slot < 30; slot++) AddItem(ch, t, 301, 99, slot);
        h.Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 }); h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(6000));
        Assert.Equal((byte)(missingTemplate ? QuestResult.Term : QuestResult.InventoryFull),
            new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Contains(ch.FindInven(0xFF)!.Items, i => i.TemplateId == 300 && i.Count == 1);
        Assert.Empty(ch.Skills); Assert.Equal(0, ch.MoneyTotal);
    }
}
