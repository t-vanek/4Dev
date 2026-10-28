using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Where monster damage comes from. Seen live: players took hits at distance 10 from a skill with range 2, even
/// while running away, because the port dealt the damage when the swing was <i>announced</i>. In the C++ the
/// announce (<c>CS_MONATTACK_ACK</c>) deals nothing; the host client animates the swing and reports it with
/// <c>CS_DEFEND_REQ</c> (attacker = the monster) when it lands, and only that report hurts.
/// </summary>
public class MonsterHitReportTests
{
    private static Character Victim(uint hp = 100)
        => new() { CharId = 1, Name = "Victim", MaxHp = 100, Hp = hp, MaxMp = 50, Mp = 50 };

    private static Monster Attacker(uint hp = 100) => new()
    {
        Id = 0x50001, ChartId = 500, Level = 5, MaxHp = 100, Hp = hp, PosX = 100, PosZ = 100,
        StartX = 100, StartZ = 100, Mode = 1, TargetId = 1, HostId = 1, AtkMin = 20, AtkMax = 20,
        AtkSpeed = 2000, AttackLevel = 10, CritProb = 0, Region = 7, Channel = 1, MapId = 0,
    };

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch, Monster mon)> Setup(
        Monster? mon = null, Character? ch = null)
    {
        var h = new MapTestHarness(MapTestHarness.WithMonsterMelee());
        ch ??= Victim();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 120, z: 100, preSeeded: ch);
        mon ??= Attacker();
        h.Service.SpawnMonster(mon);
        h.Service.CombatRng = new Random(1);
        c.Clear();
        return (h, s, c, ch, mon);
    }

    [Fact]
    public async Task TheSwingAnnounce_DealsNoDamage()
    {
        var (h, _, c, ch, _) = await Setup();

        h.Service.RunMonsterAI(1_000);

        Assert.True(c.Has(Msg.CS_MONATTACK_ACK));   // the swing is announced to the host...
        Assert.Equal(100u, ch.Hp);                  // ...and nothing is dealt until it lands
        Assert.False(c.Has(Msg.CS_DEFEND_ACK));
    }

    [Fact]
    public async Task TheHostsReport_DealsTheMonstersDamage_NotTheClients()
    {
        var (h, s, _, ch, mon) = await Setup();

        // The report carries 0 for every power field: the 20 comes from the monster's own AtkMin/AtkMax.
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(mon.Id, 1));

        Assert.Equal(80u, ch.Hp);
    }

    [Fact]
    public async Task TheAck_DerivesMagicPowerAndEchoesSelectAndAidCountry()
    {
        var (h, s, c, _, mon) = await Setup();
        var req = MapTestHarness.MonsterHitReq(mon.Id, 1);
        // CS_DEFEND_REQ body offsets: dwMgMinPower 36, dwMgMaxPower 40, bCanSelect 50, bAttackAidCountry 52.
        const int body = PacketHeader.Size;
        BitConverter.GetBytes(33u).CopyTo(req, body + 36);
        BitConverter.GetBytes(44u).CopyTo(req, body + 40);
        req[body + 50] = 0;
        req[body + 52] = 2;

        await h.Service.DispatchClientAsync(s, req);

        var r = new PacketReader(c.Last(Msg.CS_DEFEND_ACK)!);
        r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); r.ReadByte(); r.ReadUInt32(); r.ReadByte();   // ids, host
        r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); r.ReadUInt32(); r.ReadByte(); r.ReadByte();   // act/ani, maintain, hit
        r.ReadUInt16(); r.ReadByte(); r.ReadUInt32(); r.ReadUInt32();                               // AL, level, pys power
        Assert.Equal(0u, r.ReadUInt32());    // dwMgMinPower comes from the monster
        Assert.Equal(0u, r.ReadUInt32());    // dwMgMaxPower comes from the monster
        Assert.Equal(0, r.ReadByte());       // bCanSelect
        r.ReadByte(); r.ReadByte();          // bCancelCharge, bAttackCountry
        Assert.Equal(2, r.ReadByte());       // bAttackAidCountry
    }

    [Fact]
    public async Task AMiss_SendsNoHpMp_AndAKillShowsNoMp()
    {
        var (h, s, c, ch, mon) = await Setup();
        mon.AttackLevel = 0;                                        // C++ !wAL ⇒ HT_MISS
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(mon.Id, 1));
        Assert.Equal(100u, ch.Hp);
        Assert.True(c.Has(Msg.CS_DEFEND_ACK));
        Assert.False(c.Has(Msg.CS_HPMP_ACK));                       // nothing changed, nothing sent (bHPMP)

        c.Clear();
        ch.Hp = 5;
        mon.AttackLevel = 10;
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(mon.Id, 1));

        Assert.Equal(0u, ch.Hp);
        var r = new PacketReader(c.Last(Msg.CS_HPMP_ACK)!);
        r.ReadUInt32(); r.ReadByte(); r.ReadUInt32();
        Assert.Equal(0u, r.ReadUInt32());    // HP
        r.ReadUInt32();
        Assert.Equal(0u, r.ReadUInt32());    // MP shown as 0 on the killing blow (C++ m_dwHP ? m_dwMP : 0)
    }

    [Fact]
    public async Task ASkillTheMonsterDoesNotOwn_IsRefused()
    {
        var store = MapTestHarness.WithMonsterMelee();
        store.Skills[701] = store.Skills[MapTestHarness.MonsterMelee] with { Id = 701 };   // exists, but not chart 500's
        var h = new MapTestHarness(store);
        var ch = Victim();
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 120, z: 100, preSeeded: ch);
        var mon = Attacker();
        h.Service.SpawnMonster(mon);

        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(mon.Id, 1, skillId: 701));

        Assert.Equal(100u, ch.Hp);
    }

    [Fact]
    public async Task ADeadMonster_LandsNothing()
    {
        var (h, s, _, ch, mon) = await Setup(Attacker(hp: 0));

        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(mon.Id, 1));

        Assert.Equal(100u, ch.Hp);
    }

    [Fact]
    public async Task AnUnknownMonster_LandsNothing()
    {
        var (h, s, _, ch, _) = await Setup();

        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(0x59999, 1));

        Assert.Equal(100u, ch.Hp);
    }

    [Fact]
    public async Task TheHitAck_NamesTheReporterAsHost_AndEchoesTheAnimation()
    {
        var (h, s, c, _, mon) = await Setup();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(mon.Id, 1, attackType: 2, targetType: 1,
            skillId: MapTestHarness.MonsterMelee, actId: 0x11, aniId: 0x22));

        // CS_DEFEND_ACK: attackId(4) targetId(4) attackType(1) targetType(1) hostId(4) hostType(1) actId(4) aniId(4)
        var r = new PacketReader(c.Last(Msg.CS_DEFEND_ACK)!);
        Assert.Equal(mon.Id, r.ReadUInt32());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(Monster.OtMon, r.ReadByte());
        Assert.Equal(1, r.ReadByte());              // OT_PC
        Assert.Equal(1u, r.ReadUInt32());           // dwHostID = the reporting client (C++ dwHostID = pPlayer->m_dwID)
        Assert.Equal(1, r.ReadByte());              // bHostType OT_PC
        Assert.Equal(0x11u, r.ReadUInt32());
        Assert.Equal(0x22u, r.ReadUInt32());
    }

    [Fact]
    public async Task ADeadPlayer_CannotBeHitAgain()
    {
        var (h, s, c, ch, mon) = await Setup(ch: Victim(hp: 0));

        await h.Service.DispatchClientAsync(s, MapTestHarness.MonsterHitReq(mon.Id, 1));

        Assert.False(c.Has(Msg.CS_DEFEND_ACK));
    }
}
