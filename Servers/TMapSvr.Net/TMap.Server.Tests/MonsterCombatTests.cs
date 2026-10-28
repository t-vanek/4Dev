using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Phase 13 — CS_DEFEND: a player's physical hit on a monster → CalcDamage (AP−DP roll) → HP drop
/// + CS_DEFEND_ACK/CS_HPMP_ACK broadcast → on 0 HP: CS_DIE_ACK + CS_DELMON_ACK + spawn-slot respawn.</summary>
public class MonsterCombatTests
{
    // DP 100 ≥ any naked char's AP ⇒ the AP−DP roll floors to a=5,b=7 ⇒ each hit deals 5 or 6 (deterministic band).
    private static Monster Mob(uint id, float x, float z, uint hp = 100, uint dp = 100)
        => new() { Id = id, ChartId = 500, Level = 5, MaxHp = hp, Hp = hp, MaxMp = 50, Mp = 50, DefendPower = dp,
            PosX = x, PosY = 0, PosZ = z, Dir = 0, Mode = 0, Country = 0, Region = 7, Channel = 1, MapId = 0 };

    // A one-slot spawn (id 7) of a MaxHP-10 / DP-100 monster at (100,100), respawn delay 0.
    private static TemplateStore SpawnStore(uint maxHp = 10, uint dp = 100, uint delay = 0)
    {
        const ushort monId = 500, attrId = 900; const byte level = 5;
        var t = new TemplateStore();
        t.MonsterTemplates[monId] = new MonsterTemplate(monId, level, attrId);
        t.MonAttrs[TemplateStore.MonAttrKey(attrId, level)] = new MonAttrRow(attrId, level, maxHp, 50, dp);
        t.MonsterSpawns.Add(new MonsterSpawnDef(
            new MonSpawnRow(Id: 7, MapId: 0, PosX: 100, PosY: 0, PosZ: 100, Dir: 0, Country: 0,
                Count: 1, Range: 0, Prob: 100, Region: 7, Delay: delay, Event: 0),
            new List<MapMonRow> { new(7, monId, 0, 0, 100) }));
        return t;
    }

    [Fact]
    public async Task Hit_ReducesMonsterHp_AndBroadcasts()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = Mob(0x20001, 100, 100, hp: 100);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(attackerId: 1, targetId: 0x20001));

        Assert.InRange(mob.Hp, 94u, 95u);           // 100 − (5..6)
        Assert.True(c.Has(Msg.CS_DEFEND_ACK));
        Assert.True(c.Has(Msg.CS_HPMP_ACK));
    }

    [Fact]
    public async Task DefendAck_And_HpMp_CarryTheHit()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = Mob(0x20002, 100, 100, hp: 100);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x20002));

        var r = new PacketReader(c.Last(Msg.CS_DEFEND_ACK)!);
        r.ReadUInt32();                              // dwAttackID
        Assert.Equal(0x20002u, r.ReadUInt32());      // dwTargetID
        Assert.Equal((byte)1, r.ReadByte());         // bAttackType OT_PC
        Assert.Equal((byte)2, r.ReadByte());         // bTargetType OT_MON
        r.ReadUInt32(); r.ReadByte();                // dwHostID, bHostType
        r.ReadUInt32(); r.ReadUInt32();              // actId, aniId
        r.ReadByte(); r.ReadUInt32();                // bIsMaintain, dwMaintainTick
        Assert.Equal((byte)0, r.ReadByte());         // bHit (== bCP, deferred 0)
        Assert.Equal((byte)1, r.ReadByte());         // bAtkHit == HT_NORMAL (not a kill)
        r.ReadUInt16(); r.ReadByte();                // wAttackLevel, bAttackerLevel
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); // pys/mg power
        r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();         // canSelect, cancelCharge, country, aid
        r.ReadUInt16(); r.ReadByte(); r.ReadUInt16(); r.ReadByte();     // skillId, skillLevel, backSkill, perform
        for (int i = 0; i < 6; i++) r.ReadFloat();   // atk/def positions
        Assert.Equal((byte)1, r.ReadByte());         // damage-map count
        Assert.Equal((byte)30, r.ReadByte());        // key == MTYPE_DAMAGE
        uint dmg = r.ReadUInt32();
        Assert.InRange(dmg, 5u, 6u);
        Assert.Equal(100u - dmg, mob.Hp);

        var hp = new PacketReader(c.Last(Msg.CS_HPMP_ACK)!);
        Assert.Equal(0x20002u, hp.ReadUInt32());     // monster id
        Assert.Equal((byte)2, hp.ReadByte());        // OT_MON
        Assert.Equal(100u, hp.ReadUInt32());         // maxHp
        Assert.Equal(mob.Hp, hp.ReadUInt32());       // current hp
    }

    [Fact]
    public async Task DefendAck_ServerDerivesHostCountryAndPerform()
    {
        // Audit fix (Phase 13): the server echoes the client dwHostID, re-derives country from the attacker,
        // and sends bPerform = 1 (PERFORM_SUCCESS==0 ⇒ TRUE on the wire) — not the client's country / a 0 perform.
        var ch = new Character { CharId = 9, Name = "A", Class = 2, Level = 10 };
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(9, 1, 1, x: 100, z: 100, preSeeded: ch);
        ch.Country = 3; ch.AidCountry = 4;   // set after the enter handshake (which overlays country from MW_ENTERCHAR)
        h.Service.SpawnMonster(Mob(0x20009, 100, 100, hp: 100));
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(9, 0x20009, hostId: 0xABCD));

        var r = new PacketReader(c.Last(Msg.CS_DEFEND_ACK)!);
        r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); r.ReadByte(); // attackId, targetId, attackType, targetType
        Assert.Equal(9u, r.ReadUInt32());           // dwHostID is the authenticated attacker
        r.ReadByte(); r.ReadUInt32(); r.ReadUInt32(); // bHostType, actId, aniId
        r.ReadByte(); r.ReadUInt32();                 // bIsMaintain, dwMaintainTick
        r.ReadByte(); r.ReadByte();                   // bHit, bAtkHit
        r.ReadUInt16(); r.ReadByte();                 // wAttackLevel, bAttackerLevel
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); // pys/mg power
        r.ReadByte();                                 // bCanSelect
        r.ReadByte();                                 // bCancelCharge
        Assert.Equal((byte)3, r.ReadByte());          // bAttackCountry == ch.Country
        Assert.Equal((byte)4, r.ReadByte());          // bAttackAidCountry == ch.AidCountry
        r.ReadUInt16(); r.ReadByte(); r.ReadUInt16(); // skillId, skillLevel, backSkill
        Assert.Equal((byte)1, r.ReadByte());          // bPerform == 1 (successful hit)
    }

    [Fact]
    public async Task Kill_SendsDieAndDelmon_AndRespawns()
    {
        var h = new MapTestHarness(SpawnStore(maxHp: 10, delay: 0));
        h.Service.InitMonsterSpawns();
        h.Service.RunMonsterRegen(0);
        uint monId = Monster.MakeId(7, 1, 0);
        Assert.NotNull(h.State.FindMonster(monId));

        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        h.Service.CombatRng = new Random(1);
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, monId)); // 5..6
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, monId)); // ≥10 total ⇒ dead

        Assert.True(c.Has(Msg.CS_DIE_ACK));
        Assert.True(c.Has(Msg.CS_DELMON_ACK));
        Assert.Null(h.State.FindMonster(monId));      // corpse removed

        h.Service.RunMonsterRegen(0);                 // slot re-armed (delay 0) ⇒ respawns
        var re = h.State.FindMonster(monId);
        Assert.NotNull(re);
        Assert.Equal(10u, re!.Hp);                    // fresh, full HP, same id
    }

    [Fact]
    public async Task AttackingRemovedMonster_IsNoOp()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        var mob = Mob(0x20003, 100, 100, hp: 5);
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x20003)); // ≥5 dmg ⇒ dead (hp 5)
        Assert.Null(h.State.FindMonster(0x20003));
        c.Clear();

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x20003)); // target gone
        Assert.False(c.Has(Msg.CS_DEFEND_ACK));
    }

    [Fact]
    public async Task NonMonsterTarget_IsIgnored()
    {
        var h = new MapTestHarness();
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        h.Service.SpawnMonster(Mob(0x20004, 100, 100));
        c.Clear();

        // targetType OT_PC (1) — PvP is deferred this phase.
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, 0x20004, targetType: 1));
        Assert.False(c.Has(Msg.CS_DEFEND_ACK));
        Assert.Equal(100u, h.State.FindMonster(0x20004)!.Hp); // untouched
    }
}
