using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class QuestLootEventTests
{
    private const ushort Herb = 200, Other = 201;
    private sealed class LastJoinerRandom : Random { public override int Next(int maxValue) => maxValue - 1; }

    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, Monster Mon, TemplateStore T)> Setup(byte count = 3, ushort keeperParty = 0)
    {
        var t = new TemplateStore();
        t.Items[Herb] = new ItemTemplate(Herb, 0, new float[4], Stack: 10);
        t.Items[Other] = new ItemTemplate(Other, 0, new float[4], Stack: 1);
        var q = new QuestTemplate { QuestId = 100, Type = (byte)QuestType.Mission, TriggerType = 3, TriggerId = 500 };
        q.Terms.Add(new(Herb, 2, 3)); q.Rewards.Add(new(50, 1, 1, 0, 0)); t.Quests[100] = q;
        var complete = new QuestTemplate { QuestId = 101, Type = (byte)QuestType.Complete, TriggerType = 3, TriggerId = 500 };
        complete.Terms.Add(new(100, 1, 0)); t.Quests[101] = complete;
        t.Quests[102] = new QuestTemplate { QuestId = 102, Type = (byte)QuestType.ChapterMsg, TriggerType = 4, TriggerId = Herb };
        var h = new MapTestHarness(t);
        var ch = Hero(1);
        var (s, c) = await h.EnterAsync(1, 1, 11, x: 100, z: 100, preSeeded: ch);
        h.Service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = 0 }); h.Service.InitQuests();
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(100));
        var mon = new Monster { Id = 0x70001, ChartId = 700, MapId = 0, Channel = 1, Hp = 0, MaxHp = 100, PosX = 100, PosZ = 100 };
        h.Service.SpawnMonster(mon); mon.AddDamage(1, keeperParty, 100);
        mon.CorpseInven.Items.Add(new Item { TemplateId = Herb, Template = t.Items[Herb], Count = count, ItemSlot = 0 });
        c.Clear(); return (h, s, c, mon, t);
    }

    private static Character Hero(uint id)
    {
        var ch = new Character { CharId = id, Name = $"Hero{id}", Country = 1, AidCountry = 3, Level = 5, Hp = 100, MaxHp = 100 };
        ch.Invens.Add(new Inven { InvenId = 255 }); return ch;
    }
    private static byte[] Take(uint mon, bool all) => all
        ? new PacketWriter(Msg.CS_MONITEMTAKEALL_REQ).WriteUInt32(mon).ToArray() : MapTestHarness.MonItemTakeReq(mon, 0);

    private static void Update(FakeClientChannel c, byte count, QuestTermStatus status)
    {
        var r = new PacketReader(c.Last(Msg.CS_QUESTUPDATE_ACK)!);
        Assert.Equal((100u, (uint)Herb, (byte)2, count, (byte)status),
            (r.ReadUInt32(), r.ReadUInt32(), r.ReadByte(), r.ReadByte(), r.ReadByte())); Assert.Equal(0, r.Remaining);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LootAdvancesFetchObjectivesFiresAcquisitionAndTurnsInOnlyOnce(bool all)
    {
        var x = await Setup(); await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, all));
        Update(x.C, 3, QuestTermStatus.Success); Assert.True(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
        Assert.True(x.S.Char!.Quests[100].Save); Assert.Empty(x.Mon.CorpseInven.Items);
        x.C.Clear(); await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, all));
        Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK)); Assert.False(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.QuestExecReq(101));
        Assert.Equal(50L, x.S.Char.MoneyTotal); Assert.False(x.S.Char.IsRunningQuest(100));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MergedAndSplitStacksReportTheLiveInventoryCount(bool all)
    {
        var x = await Setup(5);
        x.S.Char!.FindInven(255)!.Items.Add(new Item { TemplateId = Herb, Template = x.T.Items[Herb], ItemSlot = 0, Count = 8 });
        await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, all));
        Update(x.C, 13, QuestTermStatus.Success);
        Assert.Equal(13, x.S.Char.FindInven(255)!.Items.Sum(i => i.Count));
        Assert.Single(x.C.WithId(Msg.CS_CHAPTERMSG_ACK));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)] [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)] [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    public async Task RejectedLootCannotAdvanceOrTriggerAQuest(bool all, int scenario)
    {
        var x = await Setup();
        if (scenario == 0) x.S.Char!.FindInven(255)!.SlotCount = 0;
        if (scenario == 1) x.S.IsMain = false;
        if (scenario == 2) x.Mon.Channel = 2;
        if (scenario == 3) x.Mon.CorpseInven.Items[0].OwnerId = 999;
        if (scenario == 4) x.S.State = EnterState.Granted;
        await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, all));
        Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK)); Assert.False(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
        Assert.Single(x.Mon.CorpseInven.Items); Assert.Empty(x.S.Char!.FindInven(255)!.Items);
    }

    [Fact]
    public async Task TakeAllOnlyAdvancesForItemsThatActuallyFit()
    {
        var x = await Setup(2); x.S.Char!.FindInven(255)!.SlotCount = 1;
        x.Mon.CorpseInven.Items.Add(new Item { TemplateId = Other, Template = x.T.Items[Other], ItemSlot = 1, Count = 1 });
        await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, true));
        Update(x.C, 2, QuestTermStatus.Run); Assert.Single(x.C.WithId(Msg.CS_CHAPTERMSG_ACK));
        Assert.Equal(Other, Assert.Single(x.Mon.CorpseInven.Items).TemplateId);
        Assert.Equal((byte)MonItemTakeResult.FullInven, new PacketReader(x.C.Last(Msg.CS_MONITEMTAKE_ACK)!).ReadByte());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LotteryOnlyAdvancesTheWinnerAndTakeAllCannotBypassAPendingDraw(bool all)
    {
        var x = await Setup(keeperParty: 42); var (b, cb) = await x.H.EnterAsync(2, 2, 22, x: 100, z: 100, preSeeded: Hero(2));
        foreach (var ch in new[] { x.S.Char!, b.Char! }) { ch.PartyId = 42; ch.PartyType = 3; }
        await x.H.Service.DispatchClientAsync(b, MapTestHarness.QuestExecReq(100));
        x.H.Service.CraftRng = new LastJoinerRandom(); x.C.Clear(); cb.Clear();
        await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, all));
        Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK)); Assert.False(cb.Has(Msg.CS_QUESTUPDATE_ACK));
        await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, true));
        Assert.Single(x.Mon.CorpseInven.Items); Assert.Empty(x.S.Char!.FindInven(255)!.Items);
        await x.H.Service.DispatchClientAsync(b, new PacketWriter(Msg.CS_MONITEMLOTTERY_REQ).WriteByte(1)
            .WriteUInt32(x.Mon.Id).WriteUInt16(Herb).ToArray());
        Update(cb, 3, QuestTermStatus.Success); Assert.Single(cb.WithId(Msg.CS_CHAPTERMSG_ACK));
        Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK)); Assert.False(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
        Assert.Empty(x.Mon.CorpseInven.Items); Assert.True(x.C.Has(Msg.CS_PARTYITEMTAKE_ACK));
    }

    [Fact]
    public async Task ALeavingLotteryWinnerCannotReceiveItemsOrQuestProgress()
    {
        var x = await Setup(keeperParty: 42); var (b, cb) = await x.H.EnterAsync(2, 2, 22, x: 100, z: 100, preSeeded: Hero(2));
        foreach (var ch in new[] { x.S.Char!, b.Char! }) { ch.PartyId = 42; ch.PartyType = 3; }
        await x.H.Service.DispatchClientAsync(b, MapTestHarness.QuestExecReq(100)); x.C.Clear(); cb.Clear();
        await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, false)); x.S.State = EnterState.Granted;
        await x.H.Service.DispatchClientAsync(b, new PacketWriter(Msg.CS_MONITEMLOTTERY_REQ).WriteByte(0)
            .WriteUInt32(x.Mon.Id).WriteUInt16(Herb).ToArray());
        Assert.Single(x.Mon.CorpseInven.Items); Assert.Empty(x.S.Char!.FindInven(255)!.Items);
        Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK)); Assert.False(cb.Has(Msg.CS_QUESTUPDATE_ACK));
        Assert.False(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
    }

    [Fact]
    public async Task ALoadingParticipantCannotAnswerTheLottery()
    {
        var x = await Setup(keeperParty: 42); var (b, cb) = await x.H.EnterAsync(2, 2, 22, x: 100, z: 100, preSeeded: Hero(2));
        foreach (var ch in new[] { x.S.Char!, b.Char! }) { ch.PartyId = 42; ch.PartyType = 3; }
        await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, false)); cb.Clear(); b.State = EnterState.Granted;
        await x.H.Service.DispatchClientAsync(b, new PacketWriter(Msg.CS_MONITEMLOTTERY_REQ).WriteByte(1)
            .WriteUInt32(x.Mon.Id).WriteUInt16(Herb).ToArray());
        Assert.Equal(2, x.Mon.CorpseInven.Items.Single().MaxRouting);
        Assert.Empty(b.Char!.FindInven(255)!.Items); Assert.False(cb.Has(Msg.CS_QUESTUPDATE_ACK));
    }

    [Fact]
    public async Task ALeavingPlayerCannotTakeCorpseMoneyThroughTheSeparateRequest()
    {
        var x = await Setup(); x.Mon.CorpseMoney = 100; x.S.State = EnterState.Granted;
        long before = x.S.Char!.MoneyTotal;
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.MonMoneyTakeReq(x.Mon.Id));
        Assert.Equal(100u, x.Mon.CorpseMoney); Assert.Equal(before, x.S.Char.MoneyTotal);
    }

    [Theory]
    [InlineData((byte)1, (byte)2)] [InlineData((byte)3, (byte)0)]
    public async Task DestroyingItemsRefreshesTheObjectiveWithoutFiringAnAcquisition(byte removed, byte left)
    {
        var x = await Setup(); await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, false)); x.C.Clear();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.MoveItemReq(255, 0, Proto.InvenNull, 0, removed));
        Update(x.C, left, QuestTermStatus.Run); Assert.False(x.C.Has(Msg.CS_CHAPTERMSG_ACK));
    }

    [Fact]
    public async Task MovingAnOwnedStackDoesNotFireAnAcquisition()
    {
        var x = await Setup(); await x.H.Service.DispatchClientAsync(x.S, Take(x.Mon.Id, false)); x.C.Clear();
        await x.H.Service.DispatchClientAsync(x.S, MapTestHarness.MoveItemReq(255, 0, 255, 1, 3));
        Assert.False(x.C.Has(Msg.CS_CHAPTERMSG_ACK)); Assert.False(x.C.Has(Msg.CS_QUESTUPDATE_ACK));
        Assert.Equal(3, x.S.Char!.FindInven(255)!.Items.Sum(i => i.Count));
    }
}
