using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class TeleportSkillTests
{
    private const byte OtPc = 1, StatusTeleport = 16, StatusWarp = 24, StatusReturn = 31, StatusReturnPos = 56;

    private static SkillTemplate Template(ushort id, byte exec, ushort value = 0, ushort valueInc = 0,
        byte calc = 0, byte positive = 1)
    {
        var tpl = new SkillTemplate(id, Kind: 0, UseMp: 10, UseMpType: 1, UseHp: 0, UseHpType: 0,
            StartLevel: 1, MaxLevel: 10, NextLevel: 1, ReuseDelay: 10000, ReuseDelayInc: 0, LoopDelay: 0,
            KindDelay: 0, SpeedApply: 0, Positive: positive, MapId: 0xFFFF);
        tpl.Data.Add(new SkillDataRow(0, 6, 0, exec, 1, value, valueInc, calc));
        return tpl;
    }

    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, Character Ch)> Setup(
        SkillTemplate tpl, byte level = 1)
    {
        var store = new TemplateStore();
        store.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        store.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        store.Formulas[5] = new FormulaRow(10, 0, 0); // Player-target hits require an attack level above 1.
        store.Formulas[8] = new FormulaRow(100, 0, 0);
        store.Formulas[19] = new FormulaRow(100, 0, 0);
        store.Skills[tpl.Id] = tpl;
        store.SpawnPositions[20] = new SpawnPosRow(20, 3, 900, 5, 700, 0);
        store.SpawnPositions[8000] = new SpawnPosRow(8000, 0, 110, 2, 105, 0);
        var ch = new Character { CharId = 1, Level = 50, MaxHp = 100, Hp = 100, MaxMp = 100, Mp = 100 };
        ch.Skills.Add(new Skill { SkillId = tpl.Id, Level = level, Template = tpl });
        var h = new MapTestHarness(store);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        ch.Persist.SpawnId = 20;
        h.Service.NowMs = 100000;
        c.Clear(); h.World.Clear();
        return (h, s, c, ch);
    }

    private static byte[] Finish(ushort skill, uint target = 1, byte targetType = OtPc,
        float x = 110, float y = 2, float z = 105)
    {
        var w = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        w.WriteUInt32(1); w.WriteUInt32(1); w.WriteByte(OtPc);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        w.WriteUInt16(skill); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt16(0);
        w.WriteByte(1); w.WriteUInt32(target); w.WriteByte(targetType);
        return w.ToArray();
    }

    [Fact]
    public async Task CompletedReturnToVillageStartsTeleportAfterSuccessfulCastAndCooldown()
    {
        var (h, s, c, ch) = await Setup(Template(35, StatusReturn));
        await h.Service.DispatchClientAsync(s, MapTestHarness.SkillUseReq(1, 35, targets: new[] { (1u, OtPc) }));
        Assert.Equal((byte)SkillUseResult.Success, new PacketReader(c.Last(Msg.CS_SKILLUSE_ACK)!).ReadByte());
        Assert.True(ch.Skills[0].GetReuseRemainTick(h.Service.NowMs) > 0);

        await h.Service.DispatchClientAsync(s, Finish(35));

        Assert.True(h.World.Has(Msg.MW_BEGINTELEPORT_ACK));
        Assert.True(c.Has(Msg.CS_BEGINTELEPORT_ACK));
        var r = new PacketReader(h.World.Last(Msg.MW_BEGINTELEPORT_ACK)!);
        Assert.Equal(1u, r.ReadUInt32()); r.ReadUInt32(); r.ReadByte();
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(3, r.ReadUInt16());
        Assert.Equal(900f, r.ReadFloat()); Assert.Equal(5f, r.ReadFloat()); Assert.Equal(700f, r.ReadFloat());
        Assert.Equal(100u, ch.Hp);
    }

    [Theory]
    [InlineData(StatusTeleport, 0u)]
    [InlineData(StatusWarp, 20u)]
    public async Task CompletedBlinkSendsTheGroundPointAndScaledRangeOnlyToItsOwner(byte exec, uint range)
    {
        var (h, s, c, ch) = await Setup(Template(437, exec, 10, 5, calc: 1), level: 3);
        var (_, spectator) = await h.EnterAsync(2, 2, 2, x: 101, z: 100);
        c.Clear(); spectator.Clear();
        ch.Riding = 123;

        await h.Service.DispatchClientAsync(s, Finish(437));

        Assert.True(c.Has(Msg.CS_WARP_ACK));
        Assert.False(spectator.Has(Msg.CS_WARP_ACK));
        var r = new PacketReader(c.Last(Msg.CS_WARP_ACK)!);
        Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(OtPc, r.ReadByte()); Assert.Equal(range, r.ReadUInt32());
        Assert.Equal(110f, r.ReadFloat()); Assert.Equal(2f, r.ReadFloat()); Assert.Equal(105f, r.ReadFloat());
        Assert.Equal(0, r.Remaining);
        Assert.Equal(0u, ch.Riding);
        Assert.False(h.World.Has(Msg.MW_BEGINTELEPORT_ACK));
        Assert.Equal(100f, ch.PosX); // Client checks the landing and synchronizes through CS_MOVE_REQ.
    }

    [Fact]
    public async Task ReturnToExplicitPositionUsesTheRowSpawnInsteadOfTheBoundVillage()
    {
        var (h, s, c, _) = await Setup(Template(907, StatusReturnPos, 8000));
        await h.Service.DispatchClientAsync(s, Finish(907));
        Assert.True(c.Has(Msg.CS_TELEPORT_ACK));
        var r = new PacketReader(c.Last(Msg.CS_TELEPORT_ACK)!);
        Assert.Equal(0, r.ReadByte()); r.ReadUInt32(); r.ReadByte(); r.ReadUInt32();
        Assert.Equal(0, r.ReadUInt16());
        Assert.Equal(110f, r.ReadFloat()); Assert.Equal(2f, r.ReadFloat()); Assert.Equal(105f, r.ReadFloat());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeadOrUnlearnedCasterCannotBlink(bool dead)
    {
        var (h, s, c, ch) = await Setup(Template(437, StatusWarp), dead ? (byte)1 : (byte)0);
        if (dead) ch.Hp = 0;
        await h.Service.DispatchClientAsync(s, Finish(437));
        Assert.False(c.Has(Msg.CS_WARP_ACK));
        Assert.False(c.Has(Msg.CS_BEGINTELEPORT_ACK));
    }

    [Theory]
    [InlineData(20000)]
    [InlineData(20001)]
    public async Task OrdinaryReturnDoesNotEscapeThroughABattleOfWorldsSpawn(ushort spawn)
    {
        var (h, s, c, ch) = await Setup(Template(35, StatusReturn));
        ch.Persist.SpawnId = spawn;
        await h.Service.DispatchClientAsync(s, Finish(35));
        Assert.False(h.World.Has(Msg.MW_BEGINTELEPORT_ACK));
        Assert.False(c.Has(Msg.CS_TELEPORT_ACK));
    }

    [Fact]
    public async Task NonFiniteBlinkPositionIsIgnored()
    {
        var (h, s, c, _) = await Setup(Template(437, StatusWarp));
        await h.Service.DispatchClientAsync(s, Finish(437, x: float.NaN));
        Assert.False(c.Has(Msg.CS_WARP_ACK));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedTeleportMovesOnlyALivePlayerOnTheSameMap(bool otherMap)
    {
        var (h, s, c, _) = await Setup(Template(436, StatusWarp, positive: 0));
        var target = new Character { CharId = 2, Level = 50, MaxHp = 100, Hp = 100 };
        var (_, targetClient) = await h.EnterAsync(2, 2, 2, x: 101, z: 100,
            mapId: otherMap ? (ushort)3 : (ushort)0, preSeeded: target);
        c.Clear(); targetClient.Clear();
        await h.Service.DispatchClientAsync(s, Finish(436, target: 2));
        Assert.Equal(!otherMap, targetClient.Has(Msg.CS_WARP_ACK));
        Assert.False(c.Has(Msg.CS_WARP_ACK));
        Assert.Equal(100u, target.Hp); // The movement skill has no damage rows.
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(30756, false)]
    [InlineData(30768, false)]
    public async Task ForcedTeleportOfAMonsterGoesToItsHostAndPreservesProtectedTemplates(ushort chartId, bool allowed)
    {
        var (h, s, c, ch) = await Setup(Template(436, StatusWarp, positive: 0));
        ch.Country = 0; ch.AidCountry = 3;
        var monster = new Monster { Id = 0x30001, ChartId = chartId, Level = 1, MaxHp = 100, Hp = 100,
            Country = 3, Channel = 1, MapId = 0, PosX = 101, PosZ = 100, HostId = 1 };
        h.Service.SpawnMonster(monster);
        c.Clear();
        await h.Service.DispatchClientAsync(s, Finish(436, monster.Id, Monster.OtMon));
        Assert.Equal(allowed, c.Has(Msg.CS_WARP_ACK));
        Assert.Equal(100u, monster.Hp);
        if (allowed)
        {
            var r = new PacketReader(c.Last(Msg.CS_WARP_ACK)!);
            Assert.Equal(monster.Id, r.ReadUInt32());
            Assert.Equal(Monster.OtMon, r.ReadByte());
        }
    }
}
