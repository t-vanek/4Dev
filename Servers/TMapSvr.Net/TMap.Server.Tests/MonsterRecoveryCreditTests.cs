using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Recovery must update loot ownership as well as bars, preserving party and fort damage buckets.</summary>
public sealed class MonsterRecoveryCreditTests
{
    private const ushort Strike = 700;
    private sealed record Context(MapTestHarness H, TemplateStore T, ClientSession A, FakeClientChannel Ca, Character Owner,
        ClientSession B, FakeClientChannel Cb, Character Other, Monster Mon, byte InitialA, byte InitialB);

    private static byte AddedColor(FakeClientChannel c)
    {
        var r = new PacketReader(c.Last(Msg.CS_ADDMON_ACK)!);
        r.ReadUInt32(); r.ReadUInt16(); r.ReadByte();
        for (int i = 0; i < 4; i++) r.ReadUInt32();
        for (int i = 0; i < 3; i++) r.ReadFloat();
        r.ReadUInt16(); r.ReadUInt16();
        for (int i = 0; i < 6; i++) r.ReadByte(); // mouse/key/action/mode/new-member/country
        return r.ReadByte();
    }
    private static byte ChangedColor(FakeClientChannel c, Monster mon)
    {
        var r = new PacketReader(c.Last(Msg.CS_CHANGECOLOR_ACK)!);
        Assert.Equal(Monster.OtMon, r.ReadByte()); Assert.Equal(mon.Id, r.ReadUInt32());
        byte color = r.ReadByte(); Assert.Equal(mon.Country, r.ReadByte()); return color;
    }

    private static async Task<Context> Setup(uint hp = 50, uint maxHp = 100, ushort party = 0, bool claimed = true)
    {
        var t = new TemplateStore();
        t.Classes[0] = new(1, 1, 1, 1, 1, 1); t.Races[0] = new(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new(20, 0, 0); t.Formulas[8] = new(100, 0, 0); t.Formulas[19] = new(100, 0, 0);
        t.LevelExp[5] = 1000000;
        var strike = new SkillTemplate(Strike, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0xFFFF);
        strike.Data.Add(new SkillDataRow(0, 1, 1, 14, 2, 20, 0, 0)); t.Skills[Strike] = strike;
        var h = new MapTestHarness(t);
        Character Hero(uint id) => new() { CharId = id, Name = "P"+id, Level = 5, MaxHp = 100, Hp = 100,
            MaxMp = 100, Mp = 100, Invens = { new Inven { InvenId = 255 } } };
        var a = Hero(1); var b = Hero(2);
        foreach (var pc in new[] { a, b }) pc.Skills.Add(new Skill { SkillId = Strike, Level = 1, Template = strike });
        var (sa, ca) = await h.EnterAsync(1, 1, 11, x: 100, z: 100, preSeeded: a);
        var (sb, cb) = await h.EnterAsync(2, 2, 22, x: 101, z: 100, preSeeded: b);
        a.Level = b.Level = 5; a.Country = 0; b.Country = 1; a.AidCountry = b.AidCountry = 3;
        a.PartyId = b.PartyId = party;
        var mon = new Monster { Id = 0x50001, ChartId = 500, Level = 5, MaxHp = maxHp, Hp = hp,
            MaxMp = 100, Mp = 50, Channel = 1, MapId = 0, PosX = 100, PosZ = 100,
            Country = 3, AidCountry = 3, Exp = 1000, MaxWeight = 1, MoneyProb = 100, MinMoney = 10, MaxMoney = 11 };
        if (claimed) mon.AddDamage(1, party, 31);
        h.Service.SpawnMonster(mon);
        byte colorA = AddedColor(ca), colorB = AddedColor(cb);
        h.Service.CombatRng = new FixedRandom(0); h.Service.LootRng = new FixedRandom(0);
        ca.Clear(); cb.Clear(); h.World.Clear();
        return new(h, t, sa, ca, a, sb, cb, b, mon, colorA, colorB);
    }

    [Fact]
    public async Task PartialRecoveryScalesAndTruncatesEveryPrivatePartyAndGuildBucket()
    {
        var x = await Setup();
        x.Mon.AddDamage(2, 42, 15); x.Mon.AddDamage(3, 0, 1);
        x.Mon.GuildDamage[10] = 31; x.Mon.GuildDamage[20] = 15; x.Mon.GuildDamage[30] = 1;
        x.H.Service.RunRecover(3000); // deficit 50, actual heal 25 -> each bucket retains 50%
        Assert.Equal(75u, x.Mon.Hp);
        Assert.Equal(15u, x.Mon.DamageCredits[1]); Assert.Equal(7u, x.Mon.DamageCredits[42L << 32]);
        Assert.False(x.Mon.DamageCredits.ContainsKey(3)); Assert.Equal(2, x.Mon.DamageCredits.Count);
        Assert.Equal(15u, x.Mon.GuildDamage[10]); Assert.Equal(7u, x.Mon.GuildDamage[20]);
        Assert.False(x.Mon.GuildDamage.ContainsKey(30)); Assert.Equal(2, x.Mon.GuildDamage.Count);
        Assert.Equal(1u, x.Mon.KeeperId); Assert.False(x.Ca.Has(Msg.CS_CHANGECOLOR_ACK));
    }

    [Fact]
    public async Task ClampedFullHealingClearsCreditsAndNotifiesEachViewerAfterHpMp()
    {
        var x = await Setup(hp: 80); x.Mon.GuildDamage[10] = 31;
        x.H.Service.RunRecover(3000); // heal 20, not 25; the entire deficit is gone
        Assert.Equal(100u, x.Mon.Hp); Assert.Empty(x.Mon.DamageCredits); Assert.Empty(x.Mon.GuildDamage);
        Assert.Equal((0u, (byte)0), (x.Mon.KeeperId, x.Mon.KeeperType));
        foreach (var c in new[] { x.Ca, x.Cb })
        {
            Assert.Equal((byte)2, ChangedColor(c, x.Mon));
            int hp = c.Sent.FindIndex(p => PacketHeader.ReadId(p) == Msg.CS_HPMP_ACK);
            int color = c.Sent.FindIndex(p => PacketHeader.ReadId(p) == Msg.CS_CHANGECOLOR_ACK);
            Assert.True(hp >= 0 && hp < color);
        }
    }

    [Theory]
    [InlineData(100u, 64u, false)] // 89, below 90
    [InlineData(100u, 65u, true)]  // exactly 90
    [InlineData(103u, 67u, false)] // 92, below max - floor(max*10/100) = 93
    [InlineData(103u, 68u, true)]
    public async Task KeeperReleaseUsesTheNativeIntegerThreshold(uint maxHp, uint hp, bool released)
    {
        var x = await Setup(hp, maxHp);
        x.H.Service.RunRecover(3000);
        Assert.Equal(released ? 0u : 1u, x.Mon.KeeperId);
        Assert.Equal(released, x.Ca.Has(Msg.CS_CHANGECOLOR_ACK));
    }

    [Fact]
    public async Task ANewAttackerCanClaimTheRecoveredMonsterAndReceiveItsExpAndMoney()
    {
        var x = await Setup(hp: 65);
        Assert.Equal((byte)3, x.InitialA); Assert.Equal((byte)1, x.InitialB);
        x.H.Service.RunRecover(3000); Assert.Equal(0u, x.Mon.KeeperId);
        Assert.Equal((byte)2, ChangedColor(x.Cb, x.Mon));
        await x.H.CastAndHitAsync(x.B, MapTestHarness.DefendReq(2, x.Mon.Id, skillId: Strike));
        Assert.Equal(2u, x.Mon.KeeperId);
        Assert.Equal((byte)1, ChangedColor(x.Ca, x.Mon)); Assert.Equal((byte)3, ChangedColor(x.Cb, x.Mon));
        while (x.Mon.Hp != 0)
            await x.H.CastAndHitAsync(x.B, MapTestHarness.DefendReq(2, x.Mon.Id, skillId: Strike));
        Assert.Equal(0u, x.Owner.Exp); Assert.Equal(630u, x.Other.Exp);
        Assert.Equal(10u, x.Mon.CorpseMoney);
        await x.H.Service.DispatchClientAsync(x.A, MapTestHarness.MonMoneyTakeReq(x.Mon.Id));
        Assert.Equal(0, x.Owner.MoneyTotal); Assert.Equal(10u, x.Mon.CorpseMoney);
        await x.H.Service.DispatchClientAsync(x.B, MapTestHarness.MonMoneyTakeReq(x.Mon.Id));
        Assert.Equal(10, x.Other.MoneyTotal); Assert.Equal(0u, x.Mon.CorpseMoney);
    }

    [Fact]
    public async Task PartyKeeperAndPrivateKeeperUseDifferentBuckets_AndSharedViewerColors()
    {
        var x = await Setup(hp: 65, party: 42);
        Assert.Equal((byte)3, x.InitialA); Assert.Equal((byte)3, x.InitialB);
        x.Mon.AddDamage(42, 0, 7); // char ID 42 must not collide with party ID 42
        x.H.Service.RunRecover(3000);
        Assert.Equal(0u, x.Mon.KeeperId); Assert.Equal((byte)0, x.Mon.KeeperType);
        Assert.True(x.Mon.DamageCredits.ContainsKey(42L << 32)); Assert.True(x.Mon.DamageCredits.ContainsKey(42));
        Assert.Equal((byte)2, ChangedColor(x.Ca, x.Mon)); Assert.Equal((byte)2, ChangedColor(x.Cb, x.Mon));
    }

    [Theory]
    [InlineData(0)] // battle: MP can recover but HP and credit cannot
    [InlineData(1)] // dead
    [InlineData(2)] // STOPRECOVER
    [InlineData(3)] // attack-blocking status
    [InlineData(4)] // HP timer not due; MP timer is due
    public async Task NonHpRecoveryPassesDoNotChangeCreditOrKeeper(int scenario)
    {
        var x = await Setup(); x.Mon.GuildDamage[10] = 31;
        if (scenario == 0) x.Mon.Mode = 1;
        if (scenario == 1) x.Mon.Dead = true;
        if (scenario is 2 or 3)
        {
            var t = new SkillTemplate(900, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 0xFFFF);
            t.Data.Add(new SkillDataRow(3, 6, 0, scenario == 2 ? (byte)48 : (byte)3, 0, 0, 0, 0));
            x.Mon.MaintainSkills.Add(new MaintainSkill { SkillId = 900, Template = t });
        }
        if (scenario == 4) x.Mon.RecoverHpTick = 1;
        x.H.Service.RunRecover(3000);
        Assert.Equal(50u, x.Mon.Hp); Assert.Equal(31u, x.Mon.DamageCredits[1]); Assert.Equal(31u, x.Mon.GuildDamage[10]);
        Assert.Equal(1u, x.Mon.KeeperId); Assert.False(x.Ca.Has(Msg.CS_CHANGECOLOR_ACK));
    }

    [Fact]
    public async Task MpRecoveryAloneDoesNotReleaseAnAlreadyHighHpKeeper()
    {
        var x = await Setup(hp: 95); x.Mon.RecoverHpTick = 10000;
        x.H.Service.RunRecover(3000);
        Assert.Equal(75u, x.Mon.Mp); Assert.Equal(31u, x.Mon.DamageCredits[1]); Assert.Equal(1u, x.Mon.KeeperId);
        Assert.False(x.Ca.Has(Msg.CS_CHANGECOLOR_ACK));
    }

    [Theory]
    [InlineData(50u, false)]
    [InlineData(95u, true)]
    public async Task ZeroRecoveryModifierKeepsCredit_ButEligibleHpPassStillChecksTheNativeKeeperThreshold(uint hp, bool released)
    {
        var x = await Setup(hp);
        var t = new SkillTemplate(900, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 0xFFFF);
        t.Data.Add(new SkillDataRow(3, 1, 0, 32, 2, 1000, 0, 0)); // HPR reduction floors recovery to zero
        x.Mon.MaintainSkills.Add(new MaintainSkill { SkillId = 900, Template = t });
        x.H.Service.RunRecover(3000);
        Assert.Equal(hp, x.Mon.Hp); Assert.Equal(31u, x.Mon.DamageCredits[1]);
        Assert.Equal(released ? 0u : 1u, x.Mon.KeeperId);
    }

    [Fact]
    public async Task FullRecoveryDoesNotClearAggroHostOrScheduledAiIdentity()
    {
        var x = await Setup(hp: 80); x.Mon.AddAggro(1, 1, 1, 0, 100);
        x.Mon.HostId = 1; x.Mon.TargetId = 1; x.Mon.TargetType = 1; x.Mon.HostKey = 77;
        var entry = x.Mon.AggroTable.Values.Single();
        x.H.Service.RunRecover(3000);
        Assert.Same(entry, x.Mon.AggroTable.Values.Single()); Assert.Equal(100u, entry.Aggro);
        Assert.Equal((1u, 1u, (byte)1, 77u), (x.Mon.HostId, x.Mon.TargetId, x.Mon.TargetType, x.Mon.HostKey));
        Assert.Empty(x.Mon.DamageCredits);
    }

    [Fact]
    public async Task ModeChangesSendEachViewersColorAfterTheMode()
    {
        var x = await Setup(claimed: false);
        x.Mon.Ai = new AiScript(2); // passive script: normal = peaceful, battle = enemy, home = peaceful
        x.Mon.Ai.Bind((byte)AiTrigger.Defend, 0,
            new AiBinding(new AiCommandTemplate(1, AiCommandKind.ChgMode), 0, false));
        x.Mon.HostId = 1;
        foreach (byte expectedMode in new byte[] { 1, 2, 0 })
        {
            x.Ca.Clear(); x.Cb.Clear();
            x.H.Service.OnAiEvent(x.Mon, AiTrigger.Defend, 0, 1, 1, 1);
            Assert.Equal(expectedMode, x.Mon.Mode);
            foreach (var c in new[] { x.Ca, x.Cb })
            {
                var r = new PacketReader(c.Last(Msg.CS_CHGMODE_ACK)!);
                Assert.Equal(x.Mon.Id, r.ReadUInt32()); Assert.Equal(Monster.OtMon, r.ReadByte());
                Assert.Equal(expectedMode, r.ReadByte());
                Assert.Equal(expectedMode == 1 ? (byte)3 : (byte)2, ChangedColor(c, x.Mon));
                int mode = c.Sent.FindIndex(p => PacketHeader.ReadId(p) == Msg.CS_CHGMODE_ACK);
                int color = c.Sent.FindIndex(p => PacketHeader.ReadId(p) == Msg.CS_CHANGECOLOR_ACK);
                Assert.True(mode >= 0 && mode < color);
            }
        }
    }

    [Fact]
    public async Task SubsequentHealingDoesNotRepeatTheAlreadyReleasedKeeperNotification()
    {
        var x = await Setup(hp: 65); x.H.Service.RunRecover(3000);
        Assert.True(x.Ca.Has(Msg.CS_CHANGECOLOR_ACK)); x.Ca.Clear();
        x.H.Service.RunRecover(6000);
        Assert.Equal(100u, x.Mon.Hp); Assert.False(x.Ca.Has(Msg.CS_CHANGECOLOR_ACK)); Assert.True(x.Ca.Has(Msg.CS_HPMP_ACK));
    }

    [Theory]
    [InlineData(0, 2)] [InlineData(1, 0)] [InlineData(2, 3)] [InlineData(3, 3)] [InlineData(4, 0)]
    [InlineData(5, 2)] [InlineData(6, 3)] [InlineData(7, 1)] [InlineData(8, 3)] [InlineData(9, 1)]
    [InlineData(10, 1)] [InlineData(11, 3)] [InlineData(12, 3)] [InlineData(13, 2)] [InlineData(14, 3)]
    public void MonsterColorPreservesNativeFactionKeeperAndModePriority(int scenario, byte expected)
    {
        var m = new Monster { MaxHp = 100, Country = 3, AidCountry = 3 };
        byte country = 0; ushort party = 0;
        switch (scenario)
        {
            case 1: m.Country = 0; break;
            case 2: m.Country = 1; break;
            case 3: m.Country = 0; m.AidCountry = 1; break;
            case 4: m.Country = 0; m.AidCountry = 1; country = 1; break;
            case 5: m.Mode = 2; m.AddDamage(8, 0, 20); break;
            case 6: m.AddDamage(7, 0, 20); break;
            case 7: m.AddDamage(8, 0, 20); break;
            case 8: m.AddDamage(1, 7, 20); party = 7; break;
            case 9: m.AddDamage(1, 7, 20); break;
            case 10: m.AddDamage(7, 0, 20); party = 7; break;
            case 11: m.Mode = 1; break;
            case 12: m.Ai = new AiScript(1); break;
            case 13: m.Ai = new AiScript(2); m.Aggressive = true; break; // loaded source AI flag controls colour only
            case 14: m.Aggressive = true; break; // legacy script-less fallback
        }
        Assert.Equal(expected, m.ColorFor(7, party, country));
    }
}
