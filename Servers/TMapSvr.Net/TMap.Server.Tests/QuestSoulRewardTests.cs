using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class QuestSoulRewardTests
{
    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, Character Ch)> Open(
        uint amount, byte method = 1, uint initial = 0, bool thresholds = true)
    {
        var t = new TemplateStore();
        if (thresholds) { t.LevelExp[4] = 1000; t.LevelExp[5] = 2000; }
        var q = new QuestTemplate { QuestId = 100, Type = 8, TriggerType = 3, TriggerId = 500, CountMax = 1 };
        q.Rewards.Add(new(amount, 9, method, 0, 0));
        var end = new QuestTemplate { QuestId = 101, Type = 7, ParentId = 100, TriggerType = 3, TriggerId = 500 };
        end.Terms.Add(new(100, 1, 0));
        t.Quests[100] = q; t.Quests[101] = end;
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Level = 5, Exp = 1000, Hp = 100, MaxHp = 100 };
        var (s, c) = await h.EnterAsync(1, 1, 11, preSeeded: ch);
        ch.Level = 5; ch.Exp = 1000; ch.SoulLotExp = initial;
        h.Service.AddNpc(new() { Id = 500, Country = 3, MapId = 0 });
        h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(100));
        c.Clear();
        return (h, s, c, ch);
    }

    [Theory]
    [InlineData(167u, 100u, 267u, false)]
    [InlineData(uint.MaxValue, 100u, 1000u, true)]
    [InlineData(0u, 100u, 100u, false)]
    public async Task SoulRewardUsesItsOwnAmountWithoutChangingExperienceAndCannotReplay(
        uint amount, uint initial, uint expected, bool full)
    {
        var x = await Open(amount, initial: initial);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Success, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(expected, x.Ch.SoulLotExp);
        Assert.Equal(1000u, x.Ch.Exp);
        Assert.Equal(full, x.Ch.UseLot);
        if (amount != 0)
        {
            var ack = new PacketReader(x.C.Last(Msg.CS_EXP_ACK)!);
            Assert.Equal(1000u, ack.ReadUInt32()); ack.ReadUInt32(); ack.ReadUInt32();
            Assert.Equal(expected, ack.ReadUInt32()); Assert.Equal(0, ack.Remaining);
        }
        Assert.Equal(full, x.C.Has(Msg.CS_SOULUP_ACK));
        x.C.Clear();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal(expected, x.Ch.SoulLotExp);
        Assert.False(x.C.Has(Msg.CS_EXP_ACK));
        Assert.Equal(1, x.Ch.Quests[100].CompleteCount);
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public async Task UndocumentedNondefaultSoulMethodsRemainRejected(byte method)
    {
        var x = await Open(167, method: method);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Term, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(0u, x.Ch.SoulLotExp);
        Assert.Equal(0, x.Ch.Quests[100].CompleteCount);
    }

    [Fact]
    public async Task MissingThresholdsDoNotCreateAnOverflowingSoulBar()
    {
        var x = await Open(uint.MaxValue, thresholds: false);
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal(0u, x.Ch.SoulLotExp);
        Assert.False(x.Ch.UseLot);
        Assert.Equal(1, x.Ch.Quests[100].CompleteCount);
    }

    [Fact]
    public async Task AnotherUnsupportedRewardStillRejectsBeforeSoulItemsOrProgressChange()
    {
        var x = await Open(167, initial: 100);
        x.Ch.Quests[100].Template.Rewards.Add(new(1, 99, 1, 0, 0));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal((byte)QuestResult.Term, new PacketReader(x.C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
        Assert.Equal(100u, x.Ch.SoulLotExp);
        Assert.Equal(0, x.Ch.Quests[100].CompleteCount);
        Assert.True(x.Ch.IsRunningQuest(100));
        Assert.False(x.C.Has(Msg.CS_EXP_ACK));
    }
}
