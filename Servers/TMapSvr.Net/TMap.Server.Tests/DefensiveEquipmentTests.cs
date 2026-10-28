using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Actual monster/player/summon hit packets, defender gear, reactive effects and the World return route.</summary>
public sealed class DefensiveEquipmentTests
{
    private const ushort Strike = 700, Reaction = 901;
    private sealed record Context(TemplateStore T, MapTestHarness H, ClientSession A, FakeClientChannel Ca, Character Attacker,
        ClientSession B, FakeClientChannel Cb, Character Defender, Monster Mon, SkillTemplate Strike);

    private sealed class Rolls(params int[] rolls) : Random
    {
        private readonly Queue<int> _rolls = new(rolls);
        public override int Next(int maxValue)
        {
            Assert.NotEmpty(_rolls); int value = _rolls.Dequeue(); Assert.InRange(value, 0, maxValue - 1); return value;
        }
        public void Done() => Assert.Empty(_rolls);
    }

    private static SkillTemplate Template(ushort id, params SkillDataRow[] rows)
    {
        var t = new SkillTemplate(id, 0, 0, 0, 0, 0, 1, 10, 1, 0, 0, 0, 0, 0, 0, 0xFFFF, Duration: 10000);
        t.Data.AddRange(rows); return t;
    }
    private static SkillDataRow Drain(byte exec = 14, byte attr = 1) => new(0, 1, attr, exec, 2, 20, 0, 0);

    private static async Task<Context> Setup(params SkillDataRow[] rows)
    {
        var t = new TemplateStore();
        t.Classes[0] = new(1, 1, 1, 1, 1, 1); t.Races[0] = new(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new(40, 0, 0); t.Formulas[8] = new(200, 0, 0); t.Formulas[19] = new(100, 0, 0);
        var tpl = Template(Strike, rows.Length == 0 ? new[] { Drain() } : rows);
        t.Skills[Strike] = tpl; t.MonsterTemplates[500] = new(500, 5, 0, Skill1: Strike);
        var h = new MapTestHarness(t);
        Character Pc(uint id, string name) => new() { CharId = id, Name = name, Level = 10,
            MaxHp = 200, Hp = 200, MaxMp = 100, Mp = 100, Invens = { new Inven { InvenId = 254 } } };
        var a = Pc(1, "Attacker"); var b = Pc(2, "Defender");
        a.Skills.Add(new Skill { SkillId = Strike, Level = 3, Template = tpl });
        var (sa, ca) = await h.EnterAsync(1, 1, 111, x: 100, z: 100, preSeeded: a);
        var (sb, cb) = await h.EnterAsync(2, 2, 222, x: 101, z: 100, preSeeded: b);
        a.Country = 0; b.Country = 1; a.AidCountry = b.AidCountry = 3;
        a.Hp = b.Hp = 200; a.Mp = b.Mp = 100;
        var mon = new Monster { Id = 0x50001, ChartId = 500, Level = 5, MaxHp = 200, Hp = 200, MaxMp = 100, Mp = 100,
            HostId = 2, TargetId = 2, Channel = 1, MapId = 0, PosX = 100, PosZ = 100,
            AtkMin = 40, AtkMax = 40, AttackLevel = 40, Country = 3, AidCountry = 3,
            Attr = new MonAttrRow(1, 5, 200, 100, 0, MagicAp: 40, MagicAtkLevel: 40) };
        h.Service.SpawnMonster(mon); h.Service.CombatRng = new FixedRandom(0); h.Service.NowMs = 10000;
        a.DbLoaded = b.DbLoaded = true; h.Service.ApplyItemIdSeed(1000);
        ca.Clear(); cb.Clear(); h.World.Clear();
        return new(t, h, sa, ca, a, sb, cb, b, mon, tpl);
    }

    private static Item Gear(Context x, byte slot = 3, uint current = 10, byte repair = 1, bool shield = false, bool magicShield = false)
    {
        ushort id = (ushort)(500 + slot);
        var t = new ItemTemplate(id, 0, new float[4], Type: shield ? (byte)6 : (byte)7,
            Kind: shield ? magicShield ? (byte)11 : (byte)12 : (byte)13, CanRepair: repair);
        var item = new Item { TemplateId = id, Template = t, ItemSlot = slot, Count = 1, DuraMax = 10, DuraCur = current,
            DlId = 200 + slot, Attr = new ItemAttr(id, 0, 0, 0, 0, shield ? (ushort)10 : (ushort)0,
                0, 0, shield ? (ushort)10 : (ushort)0, shield ? (byte)100 : (byte)0) };
        x.T.Items[id] = t; x.Defender.Equipped!.Items.Add(item); return item;
    }

    private static Task MonsterHit(Context x) => x.H.Service.DispatchClientAsync(x.B, MapTestHarness.MonsterHitReq(x.Mon.Id, 2));
    private static Task PlayerHit(Context x, bool legacy = false)
        => x.H.CastAndHitAsync(x.A, legacy ? MapTestHarness.DefendReq(1, 2, targetType: 1, skillId: Strike)
            : Finish(1, 1, 1, Strike, 2, 1));
    private static byte[] Finish(uint host, uint obj, byte actorType, ushort skill, uint target, byte targetType)
        => new PacketWriter(Msg.CS_FINISHSKILL_ACK).WriteUInt32(host).WriteUInt32(obj).WriteByte(actorType)
            .WriteFloat(100).WriteFloat(0).WriteFloat(100).WriteUInt16(skill).WriteUInt32(0).WriteUInt32(0)
            .WriteUInt16(0).WriteByte(1).WriteUInt32(target).WriteByte(targetType).ToArray();
    private static byte[] Body(Context x) => x.Cb.Last(Msg.CS_DEFEND_ACK)![PacketHeader.Size..];
    private static ushort BackSkill(Context x) => BitConverter.ToUInt16(Body(x), 56);
    private static void Proc(Context x, Item item, params SkillDataRow[] rows)
    {
        x.T.Skills[Reaction] = Template(Reaction, rows);
        var magic = new MagicTemplate(1, 0, 100, AutoSkill: Reaction); x.T.Magics[1] = magic;
        item.Magic.Add(new MagicOption(1, 100, magic));
    }
    private static void NoWear(Context x)
    {
        Assert.False(x.Cb.Has(Msg.CS_DURATIONDEC_ACK)); Assert.False(x.Cb.Has(Msg.CS_DURATIONEND_ACK));
        Assert.Empty(x.H.Service.PendingItemUpserts); Assert.Empty(x.H.Service.PendingItemDeletes);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 5)]
    [InlineData(2, 6)]
    [InlineData(3, 8)]
    [InlineData(4, 7)]
    [InlineData(5, 4)]
    public async Task HpDamageWearsOneNativeArmorSlotAndQueuesItsSnapshot(int pick, byte selected)
    {
        var x = await Setup(); var gear = new byte[] { 3, 4, 5, 6, 7, 8 }.Select(s => Gear(x, s)).ToArray();
        var rng = new Rolls(pick, 2); x.H.Service.EquipmentRng = rng;
        await MonsterHit(x); rng.Done(); Assert.Equal(180u, x.Defender.Hp);
        Assert.All(gear, item => Assert.Equal(item.ItemSlot == selected ? 8u : 10u, item.DuraCur));
        Assert.Equal(8u, x.H.Service.PendingItemUpserts[200 + selected].data.DuraCur);
        var ack = new PacketReader(x.Cb.Last(Msg.CS_DURATIONDEC_ACK)!);
        Assert.Equal((byte)254, ack.ReadByte()); Assert.Equal(selected, ack.ReadByte()); Assert.Equal(8u, ack.ReadUInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockWearsOnlyTheSecondaryShield_PhysicalOrMagic(bool magic)
    {
        var x = await Setup(new SkillDataRow(0, 1, magic ? (byte)3 : (byte)1, 30, 1, 0, 0, 0));
        var armor = Gear(x); var shield = Gear(x, 1, shield: true, magicShield: magic);
        var rng = new Rolls(1); x.H.Service.EquipmentRng = rng;
        await MonsterHit(x); rng.Done(); Assert.Equal(3, Body(x)[29]); // HT_BLOCK
        Assert.Equal(9u, shield.DuraCur); Assert.Equal(10u, armor.DuraCur);
        Assert.Single(x.Cb.WithId(Msg.CS_DURATIONDEC_ACK));
    }

    [Theory]
    [InlineData(0)] // miss
    [InlineData(1)] // MP loss only
    [InlineData(2)] // HP heal
    [InlineData(3)] // an attribute-zero ability is not a damage skill
    [InlineData(4)] // pure status skill
    [InlineData(5)] // physical immunity
    public async Task MissesHealsAndNonHpDamageDoNotWearArmor(int reason)
    {
        SkillDataRow row = reason switch
        {
            1 => Drain(22), 2 => new(0, 1, 1, 14, 1, 20, 0, 0), 3 => Drain(attr: 0),
            4 => new(0, 6, 0, 3, 0, 0, 0, 0), _ => Drain(),
        };
        var x = await Setup(row); var armor = Gear(x); var rng = new Rolls(); x.H.Service.EquipmentRng = rng;
        if (reason == 0) x.Mon.AttackLevel = 0;
        if (reason == 5)
        {
            var immune = Template(902, new SkillDataRow(3, 6, 0, 39, 0, 0, 0, 0));
            x.Defender.MaintainSkills.Add(new MaintainSkill { SkillId = 902, Template = immune });
        }
        await MonsterHit(x); rng.Done(); Assert.Equal(10u, armor.DuraCur); NoWear(x);
    }

    [Fact]
    public async Task AnEmptySelectedSlotDoesNotWearAnotherArmorPiece()
    {
        var x = await Setup(); var body = Gear(x, 5); var rng = new Rolls(0); x.H.Service.EquipmentRng = rng;
        await MonsterHit(x); rng.Done(); Assert.Equal(10u, body.DuraCur); NoWear(x);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIndestructibleOrAlreadyBrokenArmorPieceIsNotLost(bool broken)
    {
        var x = await Setup(); var armor = Gear(x, current: 0, repair: 0); if (!broken) armor.DuraMax = 0;
        var rng = new Rolls(0); x.H.Service.EquipmentRng = rng;
        await MonsterHit(x); rng.Done(); Assert.Contains(armor, x.Defender.Equipped!.Items); NoWear(x);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LethalDamageStillWearsAndBreaksGearBeforeTheDeathNotification(bool repairable)
    {
        var x = await Setup(); x.Defender.Hp = 10;
        var armor = Gear(x, current: 1, repair: repairable ? (byte)1 : (byte)0);
        x.H.Service.EquipmentRng = new Rolls(0, 2); await MonsterHit(x);
        Assert.Equal(0u, armor.DuraCur); Assert.Equal(0u, x.Defender.Hp); Assert.True(x.Cb.Has(Msg.CS_DIE_ACK));
        Assert.Equal(repairable, x.Defender.Equipped!.Items.Contains(armor));
        if (repairable) Assert.Equal(0u, x.H.Service.PendingItemUpserts[armor.DlId].data.DuraCur);
        else Assert.Contains(armor.DlId, x.H.Service.PendingItemDeletes);
        var ack = new PacketReader(x.Cb.Last(Msg.CS_DURATIONEND_ACK)!);
        Assert.Equal((byte)254, ack.ReadByte()); Assert.Equal((byte)3, ack.ReadByte()); Assert.Equal(repairable ? 0 : 1, ack.ReadByte());
    }

    [Fact]
    public async Task DeleteTargetEffectExhaustsTheSelectedGear_EvenOnAZeroWearRoll()
    {
        var x = await Setup(Drain(), new SkillDataRow(0, 8, 0, 2, 0, 0, 0, 0));
        var armor = Gear(x); x.H.Service.EquipmentRng = new Rolls(0, 0);
        await MonsterHit(x); Assert.Equal(0u, armor.DuraCur); Assert.True(x.Cb.Has(Msg.CS_DURATIONEND_ACK));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayerFinishAndLegacyDefendShareOneWearOperation_WithNoReplay(bool legacy)
    {
        var x = await Setup(); var armor = Gear(x); var rng = new Rolls(0, 1); x.H.Service.EquipmentRng = rng;
        await PlayerHit(x, legacy); rng.Done(); Assert.Equal(9u, armor.DuraCur); Assert.Equal(180u, x.Defender.Hp);
        await x.H.Service.DispatchClientAsync(x.A, MapTestHarness.DefendReq(1, 2, targetType: 1, skillId: Strike));
        Assert.Equal(9u, armor.DuraCur); Assert.Equal(180u, x.Defender.Hp);
        Assert.Single(x.Cb.WithId(Msg.CS_DURATIONDEC_ACK));
    }

    [Fact]
    public async Task ARecallHitAlsoWearsThePlayerDefendersGear()
    {
        var x = await Setup(); var armor = Gear(x); x.H.Service.EquipmentRng = new Rolls(0, 1);
        var recall = new RecallMon { Id = 800, OwnerId = 1, RecallType = 1, Country = 0, AidCountry = 3,
            Level = 10, Hp = 100, MaxHp = 100, Channel = 1, MapId = 0, PosX = 100, PosZ = 100,
            Attr = new MonAttrRow(1, 10, 100, 100, 0, AttackLevel: 40, Ap: 40) };
        recall.Skills.Add(new Skill { SkillId = Strike, Level = 1, Template = x.Strike });
        x.Attacker.Recalls[recall.Id] = recall; x.H.State.AddRecall(recall);
        await x.H.Service.DispatchClientAsync(x.A, Finish(1, recall.Id, 7, Strike, 2, 1));
        Assert.Equal(9u, armor.DuraCur); Assert.Equal(180u, x.Defender.Hp);
    }

    [Fact]
    public async Task AReactiveDamageModifierUsesSequentialDamageThenHpScalingBeforeLoss()
    {
        var x = await Setup(); var armor = Gear(x);
        Proc(x, armor, new SkillDataRow(0, 1, 1, 30, 5, 50, 0, 0), new SkillDataRow(0, 1, 1, 14, 5, 50, 0, 0));
        x.H.Service.EquipmentRng = new Rolls(0, 99, 1); await MonsterHit(x);
        Assert.Equal(195u, x.Defender.Hp); // 20 -> 10 -> 5
        Assert.Equal((ushort)0, BackSkill(x)); Assert.False(x.H.World.Has(Msg.MW_GETBLOOD_ACK));
        Assert.Equal((byte)1, Body(x)[83]); Assert.Equal((byte)14, Body(x)[84]);
        Assert.Equal(20u, BitConverter.ToUInt32(Body(x), 85)); // inherited pre-reaction damage display
    }

    [Fact]
    public async Task ReactiveScalingUsesTheIncomingSkillLevel()
    {
        var x = await Setup(); var armor = Gear(x);
        Proc(x, armor, new SkillDataRow(0, 1, 1, 30, 5, 50, 10, 1));
        x.H.Service.EquipmentRng = new Rolls(0, 99, 1); await PlayerHit(x);
        Assert.Equal(186u, x.Defender.Hp); // incoming level 3 -> 70% of 20
    }

    [Theory]
    [InlineData(0)] // same side
    [InlineData(1)] // defender is peaceful
    [InlineData(2)] // attacker is peaceful
    public async Task FriendlyOrPeacefulHitsStillWearGear_ButDoNotTriggerAReaction(int reason)
    {
        var x = await Setup(); var armor = Gear(x); Proc(x, armor, new SkillDataRow(0, 1, 1, 30, 5, 50, 0, 0));
        if (reason == 0) x.Attacker.Country = x.Defender.Country;
        if (reason == 1) x.Defender.Country = 4;
        if (reason == 2) x.Attacker.Country = 4;
        x.H.Service.EquipmentRng = new Rolls(0, 0, 1); await PlayerHit(x);
        Assert.Equal(180u, x.Defender.Hp); Assert.Equal(9u, armor.DuraCur); Assert.Equal((ushort)0, BackSkill(x));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MirrorPreservesDefenderHp_AndRoutesTheDamageBackThroughWorld(bool pc)
    {
        var x = await Setup(); var armor = Gear(x); Proc(x, armor, new SkillDataRow(0, 6, 0, 17, 0, 0, 0, 0));
        x.H.Service.EquipmentRng = new Rolls(0, 99, 1);
        if (pc) await PlayerHit(x); else await MonsterHit(x);
        Assert.Equal(200u, x.Defender.Hp); Assert.Equal((ushort)0, BackSkill(x));
        var r = new PacketReader(x.H.World.Last(Msg.MW_GETBLOOD_ACK)!);
        uint id = r.ReadUInt32(); byte type = r.ReadByte(); uint host = r.ReadUInt32();
        Assert.Equal(pc ? 1u : x.Mon.Id, id); Assert.Equal(pc ? (byte)1 : (byte)2, type); Assert.Equal(pc ? 1u : 2u, host);
        Assert.Equal((byte)30, r.ReadByte()); Assert.Equal(20u, r.ReadUInt32());
        x.H.World.Clear();
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_GETBLOOD_REQ).WriteUInt32(host)
            .WriteUInt32(pc ? 111u : 222u).WriteUInt32(id).WriteByte(type).WriteByte(30).WriteUInt32(20).ToArray());
        Assert.Equal(180u, pc ? x.Attacker.Hp : x.Mon.Hp);
        Assert.False(x.H.World.Has(Msg.MW_GETBLOOD_ACK)); // reflection cannot reflect again
    }

    [Fact]
    public async Task ACounterSkillIsAnnouncedForTheDefender_AndOnlyItsAttackerIsAuthorized()
    {
        var x = await Setup(); var armor = Gear(x); Proc(x, armor, new SkillDataRow(3, 6, 0, 3, 0, 0, 0, 0));
        x.H.Service.EquipmentRng = new Rolls(0, 99, 1); await MonsterHit(x);
        Assert.Equal(Reaction, BackSkill(x)); Assert.DoesNotContain(x.Defender.Skills, k => k.SkillId == Reaction);
        x.Cb.Clear();
        await x.H.Service.DispatchClientAsync(x.B, Finish(2, 2, 1, Reaction, 1, 1)); // unrelated player
        Assert.False(x.Cb.Has(Msg.CS_DEFEND_ACK));
        await x.H.Service.DispatchClientAsync(x.B, Finish(2, 2, 1, Reaction, x.Mon.Id, 2));
        Assert.Contains(x.Mon.MaintainSkills, m => m.SkillId == Reaction && m.Level == 1);
        Assert.Single(x.Cb.WithId(Msg.CS_DEFEND_ACK));
        await x.H.Service.DispatchClientAsync(x.B, MapTestHarness.DefendReq(2, x.Mon.Id, skillId: Reaction));
        Assert.Single(x.Cb.WithId(Msg.CS_DEFEND_ACK)); Assert.Equal(100u, x.Defender.Mp);
    }

    [Fact]
    public async Task AFailedDefensiveProcDoesNotAuthorizeACounterSkill()
    {
        var x = await Setup(); var armor = Gear(x); Proc(x, armor, new SkillDataRow(3, 6, 0, 3, 0, 0, 0, 0));
        armor.Magic[0] = armor.Magic[0] with { Value = 50 };
        x.H.Service.EquipmentRng = new Rolls(0, 50, 1); await MonsterHit(x); Assert.Equal((ushort)0, BackSkill(x));
        x.Cb.Clear(); await x.H.Service.DispatchClientAsync(x.B, Finish(2, 2, 1, Reaction, x.Mon.Id, 2));
        Assert.Empty(x.Mon.MaintainSkills); Assert.False(x.Cb.Has(Msg.CS_DEFEND_ACK));
    }
}
