using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class GameforgeProgressionTests
{
    // Independent publisher vectors: Gameforge/progression-140/publisher-exp.json,
    // forum.4story.gameforge.com/forum/thread/546-levely/ (CZ team, 26 Nov 2018).
    private static readonly uint[] ExpToReach =
    {
        264_808_925, 273_311_573, 281_971_691, 290_790_914,
        299_770_887, 308_913_253, 318_219_659, 327_691_759,
        337_331_208, 347_139_665, 357_118_797, 367_270_272,
        377_595_757, 388_096_933, 398_775_475, 409_633_067
    }; // target levels 125..140

    public static IEnumerable<object[]> Boundaries()
        => Enumerable.Range(126, 15).Select(level => new object[] { (byte)level, ExpToReach[level - 125] });

    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        for (int i = 0; i < ExpToReach.Length; i++) t.LevelExp[124 + i] = ExpToReach[i];
        t.LevelExp[140] = 0;
        t.LevelMoney[140] = 100;
        return t;
    }

    private static Task Award(MapTestHarness h, uint gain)
        => h.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_CMGIFT_REQ)
            .WriteUInt32(1).WriteByte(5).WriteUInt32(gain).WriteByte(1)
            .WriteString("EXP probe").WriteString("").WriteUInt32(0).WriteByte(1)
            .WriteUInt16(0).WriteUInt16(0).WriteByte(0).ToArray());

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task PublisherBoundary_IsReachedExactly_WithoutSkippingALevel(byte target, uint threshold)
    {
        var h = new MapTestHarness(Store());
        var ch = new Character { CharId = 1, Name = "Progression", MaxHp = 100, Hp = 100 };
        var (s, client) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        ch.Level = (byte)(target - 1);
        ch.Exp = threshold - 2;
        client.Clear();

        await Award(h, 1);
        Assert.Equal(target - 1, ch.Level);
        Assert.Equal(threshold - 1, ch.Exp);
        Assert.False(client.Has(Msg.CS_LEVEL_ACK));

        await Award(h, 1);
        Assert.Equal(target, ch.Level);
        Assert.Equal(threshold, ch.Exp);
        Assert.True(client.Has(Msg.CS_LEVEL_ACK));
        Assert.True(client.Has(Msg.CS_EXP_ACK));
        Assert.InRange(ch.SoulLotExp, 0u, 11_000_000u);
        Assert.Equal(0u, ch.SkillPoint); // No unverified high-level skill grants introduced.
    }

    [Fact]
    public async Task LargeReward_UsesWideArithmetic_AndStopsAt140()
    {
        var h = new MapTestHarness(Store());
        var ch = new Character { CharId = 1, Name = "LargeReward", MaxHp = 100, Hp = 100 };
        await h.EnterAsync(1, 1, 1, preSeeded: ch);
        ch.Level = 125;
        ch.Exp = ExpToReach[0];
        await Award(h, uint.MaxValue);
        Assert.Equal(140, ch.Level);
        Assert.Equal(409_633_067u, ch.Exp);
        Assert.Equal(0u, ch.SoulLotExp);
    }

    [Fact]
    public async Task MaximumLevel_CannotGainExpOrStartAZeroWidthLottery()
    {
        var h = new MapTestHarness(Store());
        var ch = new Character { CharId = 1, Name = "Maximum", MaxHp = 100, Hp = 100 };
        var (s, client) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        ch.Level = 140;
        ch.Exp = 409_633_067;
        client.Clear();
        await Award(h, uint.MaxValue);
        await h.Service.DispatchClientAsync(s, new PacketWriter(Msg.CS_SOULLOTTERY_REQ).ToArray());
        Assert.Equal(140, ch.Level);
        Assert.Equal(409_633_067u, ch.Exp);
        Assert.Equal(0u, ch.SoulLotExp);
        Assert.False(client.Has(Msg.CS_SOULUP_ACK));
        Assert.False(client.Has(Msg.CS_SOULLOTTERY_ACK));
    }

    [Theory]
    [InlineData(126, 273_311_573u, 281_971_691u)]
    [InlineData(140, 409_633_067u, 0u)]
    public async Task LoginPacket_ContainsLoadedExpBounds_BeforeAnyReward(byte level, uint floor, uint ceiling)
    {
        var h = new MapTestHarness(Store());
        var ch = new Character { CharId = 1, Name = "Reconnect", Level = level, Exp = floor, MaxHp = 100, Hp = 100 };
        var (_, client) = await h.EnterAsync(1, 1, 1, preSeeded: ch);
        var r = new PacketReader(client.Last(Msg.CS_CHARINFO_ACK)!);
        r.ReadUInt32(); // character id
        for (int i = 0; i < 3; i++) r.ReadByte(); // security flags
        r.ReadUInt16(); r.ReadString(); // title and name
        for (int i = 0; i < 14; i++) r.ReadByte(); // appearance, country, level
        r.ReadUInt16(); // party
        for (int i = 0; i < 3; i++) r.ReadUInt32(); // guild, fame, colour
        r.ReadByte(); r.ReadByte(); r.ReadString(); // guild duty, peer, name
        r.ReadUInt32(); r.ReadString(); // tactics
        for (int i = 0; i < 3; i++) r.ReadUInt32(); // money
        Assert.Equal(floor, r.ReadUInt32());
        Assert.Equal(ceiling, r.ReadUInt32());
        Assert.Equal(floor, r.ReadUInt32());
    }
}
