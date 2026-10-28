using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Phase 19 — monster aggro + chase: being hit targets the attacker + enters battle; RunMonsterAI
/// chases the target (CS_MONACTION_ACK TA_FOLLOW toward the target) and drops aggro (→ NORMAL) when the
/// target is gone or beyond the leash.</summary>
public class MonsterAggroTests
{
    private static Monster Mob(uint id, uint hp = 100, byte mode = 0, uint targetId = 0,
        float startX = 100, float startZ = 100)
        => new() { Id = id, ChartId = 500, Level = 5, MaxHp = 100, Hp = hp, MaxMp = 50, Mp = 50,
            DefendPower = 100, PosX = startX, PosZ = startZ, StartX = startX, StartY = 0, StartZ = startZ,
            Mode = mode, TargetId = targetId, Region = 7, Channel = 1, MapId = 0, RoamNextMs = 0,
            Country = 3 /* TCONTRY_N — a neutral field mob (Phase 44: aggro needs attacker country ≠ mob country) */ };

    [Fact]
    public async Task Hit_AggrosAttacker_AndEntersBattle()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = Mob(0x40001);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x40001));

        Assert.Equal(1u, mob.TargetId);      // aggroed the attacker
        Assert.Equal((byte)1, mob.Mode);     // MT_BATTLE
    }

    [Fact]
    public async Task Chase_BroadcastsFollow_TowardTarget()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 150, z: 150); // target (and viewer) near the monster
        var mob = Mob(0x40002, mode: 1, targetId: 1, startX: 100, startZ: 100);
        h.Service.SpawnMonster(mob);
        c.Clear();

        h.Service.RunMonsterAI(1_000);

        var r = new PacketReader(c.Last(Msg.CS_MONACTION_ACK)!);
        Assert.Equal(0x40002u, r.ReadUInt32());  // dwMonID
        Assert.Equal((byte)9, r.ReadByte());     // bAction TA_FOLLOW
        Assert.Equal(150f, r.ReadFloat());       // toward the target's X
        r.ReadFloat();                           // Y
        Assert.Equal(150f, r.ReadFloat());       // target Z
        Assert.Equal(1u, r.ReadUInt32());        // dwTargetID
        Assert.Equal((byte)1, r.ReadByte());     // bTargetType OT_PC
    }

    [Fact]
    public async Task Chase_TargetBeyondLeash_DropsAggro()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 2000, z: 2000); // target far from the anchor (100,100)
        var mob = Mob(0x40003, mode: 1, targetId: 1, startX: 100, startZ: 100);
        h.Service.SpawnMonster(mob);
        c.Clear();

        h.Service.RunMonsterAI(1_000);

        Assert.Equal((byte)0, mob.Mode);         // MT_NORMAL
        Assert.Equal(0u, mob.TargetId);          // aggro dropped
        Assert.False(c.Has(Msg.CS_MONACTION_ACK));
    }

    [Fact]
    public async Task Chase_TargetGone_DropsAggro()
    {
        var h = new MapTestHarness();
        await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = Mob(0x40004, mode: 1, targetId: 999); // no such player
        h.Service.SpawnMonster(mob);

        h.Service.RunMonsterAI(1_000);

        Assert.Equal((byte)0, mob.Mode);
        Assert.Equal(0u, mob.TargetId);
    }

    [Fact]
    public async Task Chase_RespectsCadence()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 150, z: 150);
        var mob = Mob(0x40005, mode: 1, targetId: 1, startX: 100, startZ: 100);
        h.Service.SpawnMonster(mob);
        c.Clear();

        h.Service.RunMonsterAI(1_000);           // chases (arms cadence to 2000)
        Assert.True(c.Has(Msg.CS_MONACTION_ACK));
        c.Clear();

        h.Service.RunMonsterAI(1_500);           // within the chase interval ⇒ no re-broadcast
        Assert.False(c.Has(Msg.CS_MONACTION_ACK));

        h.Service.RunMonsterAI(2_000);           // due again
        Assert.True(c.Has(Msg.CS_MONACTION_ACK));
    }
}
