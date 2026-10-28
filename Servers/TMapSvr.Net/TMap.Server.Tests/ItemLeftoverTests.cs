using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

internal sealed class FakeCashStore : ICashStore
{
    public int? Ret = 0;
    public List<(uint CharId, ushort ShopItemId)> Calls { get; } = new();
    public uint Cash = 1000, Bonus = 50;
    public CashCabinet Cabinet = new(0, 0, 0, new());
    public (int Ret, FullItemRow Item)? Get;
    public CashBuyResult? Buy = new(0, 900, 50, 0, 0, "");
    public (int Ret, long Time, uint Cash, uint Bonus)? CabinetBuy = (0, 2_000_000_000, 800, 50);
    public (int Ret, uint NewId)? Putin = (0, 77);
    public int? Gain = 0;
    public List<string> Log { get; } = new();

    public Task<int?> StopTheClockAsync(uint charId, ushort shopItemId) { Calls.Add((charId, shopItemId)); return Task.FromResult(Ret); }
    public Task<(uint Cash, uint Bonus)> GetCashAsync(uint userId) { Log.Add($"get {userId}"); return Task.FromResult((Cash, Bonus)); }
    public Task<CashCabinet> CabinetAsync(uint charId, uint userId) { Log.Add($"cabinet {charId} {userId}"); return Task.FromResult(Cabinet); }
    public Task<(int Ret, FullItemRow Item)?> CashItemGetAsync(uint userId, uint charId, uint cashItemId, byte inven, byte slot)
    { Log.Add($"take {cashItemId} {inven} {slot}"); return Task.FromResult(Get); }
    public Task<CashBuyResult?> CashItemBuyAsync(uint userId, uint charId, ushort cashItemId, byte buyType, string target)
    { Log.Add($"buy {cashItemId} {buyType} {target}"); return Task.FromResult(Buy); }
    public Task<(int Ret, long Time, uint Cash, uint Bonus)?> CashCabinetBuyAsync(uint charId, ushort shopItemId)
    { Log.Add($"cabinetbuy {shopItemId}"); return Task.FromResult(CabinetBuy); }
    public Task<(int Ret, uint NewId)?> CashItemPutinAsync(uint charId, byte inven, byte slot) { Log.Add($"putin {inven} {slot}"); return Task.FromResult(Putin); }
    public Task<int?> GainCashBonusAsync(uint charId, byte inven, byte slot, uint bonus) { Log.Add($"bonus {inven} {slot} {bonus}"); return Task.FromResult(Gain); }
}

/// <summary>Batch S7a: fall damage, act items, medal items, the rewards window, stop the clock and waste refine.</summary>
public class ItemLeftoverTests
{
    private const uint A = 1, B = 2;
    private const ushort Firework = 1300, FireworkItem = 18006, Medal = 500, Stone = 600, Ore = 601, Sword = 700, ShopItem = 33;
    private const byte Bag = 0;

    private sealed record Setup(TemplateStore T, MapTestHarness H, FakeCashStore Cash, ClientSession Sa, FakeClientChannel Ca, Character A,
        ClientSession Sb, FakeClientChannel Cb, Character B);

    private static async Task<Setup> Open()
    {
        var t = new TemplateStore();
        t.Skills[Firework] = new SkillTemplate(Firework, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0, UseHpType: 0, StartLevel: 1, MaxLevel: 1,
            NextLevel: 0, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0, KindDelay: 0, SpeedApply: 0, Positive: 1, MapId: 0xFFFF,
            UseItem: FireworkItem);
        t.Items[FireworkItem] = new ItemTemplate(FireworkItem, 0, new[] { 1f, 0f, 0f, 0f }, Type: 25, Kind: 0, Delay: 5000, DelayGroup: 77);
        t.Items[Medal] = new ItemTemplate(Medal, 0, new[] { 1f, 0f, 0f, 0f }, Type: 15, Kind: 118, UseValue: 5);
        t.Items[Stone] = new ItemTemplate(Stone, 0, new[] { 1f, 0f, 0f, 0f }, Type: 14, Kind: 0, UseValue: Ore);
        t.Items[Ore] = new ItemTemplate(Ore, 0, new[] { 1f, 0f, 0f, 0f }, Type: 7, Kind: 0);
        t.Items[Sword] = new ItemTemplate(Sword, 0, new[] { 1f, 0f, 0f, 0f }, Type: 1, Kind: 1);
        t.CashItems[ShopItem] = new CashItem { Id = ShopItem, UseTime = 30 };
        var row = new FullItemRow(0, 0, 0, Sword, 0, 1, 0, 100, 100, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0, 0);
        t.SpecialBoxes[9] = new() { new SpecialBoxRow(9, 0, 2, row), new SpecialBoxRow(9, 0, 3, row), new SpecialBoxRow(9, 0, 6, row) };
        var h = new MapTestHarness(t);
        var cash = new FakeCashStore();
        h.Service.CashStore = cash;
        Character Pc(uint id, string name)
        {
            var ch = new Character { CharId = id, Name = name, Level = 20, Class = 2, MaxHp = 100, Hp = 100 };
            ch.Invens.Add(new Inven { InvenId = Bag });
            return ch;
        }
        var a = Pc(A, "Ann"); var b = Pc(B, "Bob");
        var (sa, ca) = await h.EnterAsync(A, 1, 11, name: "Ann", x: 100, z: 100, preSeeded: a);
        var (sb, cb) = await h.EnterAsync(B, 2, 12, name: "Bob", x: 105, z: 100, preSeeded: b);
        a.Hp = b.Hp = 100;
        ca.Clear(); cb.Clear();
        return new Setup(t, h, cash, sa, ca, a, sb, cb, b);
    }

    private static Item Give(Setup x, ushort id, byte slot, byte count = 1, long endTime = 0)
    {
        var item = new Item { TemplateId = id, ItemSlot = slot, Count = count, EndTime = endTime, Template = x.T.Items[id] };
        x.A.FindInven(Bag)!.Items.Add(item);
        return item;
    }

    private static byte[] Fall(uint target, byte type, uint damage, byte channel = 1, ushort mapId = 0)
        => new PacketWriter(Msg.CS_DROPDAMAGE_REQ).WriteUInt32(target).WriteByte(type).WriteUInt32(damage).WriteByte(channel).WriteUInt16(mapId).ToArray();

    private static byte[] Act(uint attacker, ushort skill = Firework)
    {
        var w = new PacketWriter(Msg.CS_ACTITEMSUSE_REQ);
        w.WriteUInt32(attacker); w.WriteByte(1); w.WriteByte(1); w.WriteUInt16(0); w.WriteUInt16(skill); w.WriteByte(1);
        w.WriteUInt32(5); w.WriteUInt32(6); w.WriteFloat(100); w.WriteFloat(0); w.WriteFloat(100); w.WriteUInt32(0); w.WriteByte(0);
        return w.ToArray();
    }

    private static byte SkillResult(FakeClientChannel c) => new PacketReader(c.Last(Msg.CS_SKILLUSE_ACK)!).ReadByte();

    [Fact]
    public async Task AFall_HurtsOneself_ForEveryoneAround_AndCanKill()
    {
        var x = await Open();
        await x.H.Service.DispatchClientAsync(x.Sa, Fall(A, 1, 30));
        Assert.Equal(70u, x.A.Hp);
        var r = new PacketReader(x.Cb.Last(Msg.CS_DROPDAMAGE_ACK)!);
        Assert.Equal((A, (byte)1, 30u), (r.ReadUInt32(), r.ReadByte(), r.ReadUInt32()));
        Assert.True(x.Ca.Has(Msg.CS_HPMP_ACK));

        await x.H.Service.DispatchClientAsync(x.Sa, Fall(A, 1, 500));
        Assert.Equal(0u, x.A.Hp);
        Assert.True(x.Cb.Has(Msg.CS_DIE_ACK));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 3)]
    public async Task AFallFromThePreviousMapOrChannel_DoesNotKillAtTheDestination(byte channel, ushort mapId)
    {
        var x = await Open();
        x.Ca.Clear(); x.Cb.Clear();

        await x.H.Service.DispatchClientAsync(x.Sa, Fall(A, 1, 500, channel, mapId));

        Assert.Equal(100u, x.A.Hp);
        Assert.False(x.Ca.Has(Msg.CS_DROPDAMAGE_ACK));
        Assert.False(x.Ca.Has(Msg.CS_HPMP_ACK));
        Assert.False(x.Ca.Has(Msg.CS_DIE_ACK));
        Assert.False(x.Cb.Has(Msg.CS_DIE_ACK));
    }

    [Fact]
    public async Task AFallWhileLoadingTheDestination_DoesNotKill()
    {
        var x = await Open();
        x.Sa.State = EnterState.Granted;
        x.Ca.Clear();

        await x.H.Service.DispatchClientAsync(x.Sa, Fall(A, 1, 500));

        Assert.Equal(100u, x.A.Hp);
        Assert.False(x.Ca.Has(Msg.CS_DIE_ACK));
    }

    [Fact]
    public async Task SomeoneElsesFall_IsRefused_WithTheOperatorsWord()
    {
        var x = await Open();
        await x.H.Service.DispatchClientAsync(x.Sa, Fall(B, 1, 30));
        Assert.Equal(100u, x.B.Hp);
        Assert.True(x.Ca.Has(Msg.CS_CHAT_ACK));
        Assert.False(x.Cb.Has(Msg.CS_DROPDAMAGE_ACK));
    }

    [Fact]
    public async Task AnActItem_IsSpent_UnderItsDelay_AndShownAround()
    {
        var x = await Open();
        Give(x, FireworkItem, 3, count: 2);
        await x.H.Service.DispatchClientAsync(x.Sa, Act(A));
        Assert.Equal(0, SkillResult(x.Cb));
        Assert.Equal(1, x.A.FindInven(Bag)!.FindItem(3)!.Count);
        Assert.True(x.Ca.Has(Msg.CS_ITEMUSE_ACK));

        x.Ca.Clear();
        await x.H.Service.DispatchClientAsync(x.Sa, Act(A));                                     // still under the item's delay
        Assert.Equal((byte)SkillUseResult.SpeedyUse, SkillResult(x.Ca));
        Assert.Equal(1, x.A.FindInven(Bag)!.FindItem(3)!.Count);
    }

    [Fact]
    public async Task AnActItem_NeedsItsItem_AndIsTheSendersOwn()
    {
        var x = await Open();
        await x.H.Service.DispatchClientAsync(x.Sa, Act(A));
        Assert.Equal((byte)SkillUseResult.NeedItem, SkillResult(x.Ca));
        Assert.Empty(x.A.ItemCoolTime);                                                        // no delay for nothing (C++ armed it)

        Give(x, FireworkItem, 3);
        x.Ca.Clear();
        await x.H.Service.DispatchClientAsync(x.Sb, Act(A));                                     // B can't spend A's firework
        Assert.Equal(1, x.A.FindInven(Bag)!.FindItem(3)!.Count);
        Assert.False(x.Ca.Has(Msg.CS_SKILLUSE_ACK));
    }

    [Fact]
    public async Task MedalItems_AddTheirMedals()
    {
        var x = await Open();
        Give(x, Medal, 4, count: 3);
        await x.H.Service.DispatchClientAsync(x.Sa, new PacketWriter(Msg.CS_USEMEDALITEM_REQ).WriteByte(Bag).WriteByte(4).WriteByte(2).ToArray());
        Assert.Equal(10u, x.A.Medals);
        Assert.Equal(10u, new PacketReader(x.Ca.Last(Msg.CS_UPDATEMEDALS_REQ)!).ReadUInt32());
        Assert.Equal(1, x.A.FindInven(Bag)!.FindItem(4)!.Count);

        await x.H.Service.DispatchClientAsync(x.Sa, new PacketWriter(Msg.CS_USEMEDALITEM_REQ).WriteByte(Bag).WriteByte(4).WriteByte(2).ToArray());
        Assert.Equal(10u, x.A.Medals);                                                          // only one left
    }

    [Fact]
    public async Task TheRewardsWindow_ShowsASpecialBoxForOnesClass()
    {
        var x = await Open();
        await x.H.Service.DispatchClientAsync(x.Sa, new PacketWriter(Msg.CS_GETREWARDS_REQ).WriteByte(115).WriteUInt16(9).ToArray());
        Assert.Equal(2, new PacketReader(x.Ca.Last(Msg.CS_GETREWARDS_ACK)!).ReadUInt16());       // class 2 and every class (6)
    }

    [Fact]
    public async Task StopTheClock_BuysTheCashItemsDays()
    {
        var x = await Open();
        var item = Give(x, Sword, 5, endTime: 1_000_000);
        await x.H.Service.DispatchClientAsync(x.Sa, new PacketWriter(Msg.CS_STOPTHECLOCK_REQ).WriteByte(Bag).WriteByte(5).WriteUInt16(ShopItem).ToArray());
        Assert.Equal((A, ShopItem), Assert.Single(x.Cash.Calls));
        Assert.Equal(1_000_000 + 30 * 86400L, item.EndTime);
        var r = new PacketReader(x.Ca.Last(Msg.CS_STOPTHECLOCK_ACK)!);
        Assert.Equal((0, Bag, (byte)5, item.EndTime), (r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadInt64()));

        x.Cash.Ret = 1;                                                                         // not enough cash
        await x.H.Service.DispatchClientAsync(x.Sa, new PacketWriter(Msg.CS_STOPTHECLOCK_REQ).WriteByte(Bag).WriteByte(5).WriteUInt16(ShopItem).ToArray());
        Assert.Equal(1, new PacketReader(x.Ca.Last(Msg.CS_STOPTHECLOCK_ACK)!).ReadByte());
        Assert.Equal(1_000_000 + 30 * 86400L, item.EndTime);
    }

    [Fact]
    public async Task StopTheClock_IsOnlyForTimedThings()
    {
        var x = await Open();
        Give(x, Sword, 5);
        await x.H.Service.DispatchClientAsync(x.Sa, new PacketWriter(Msg.CS_STOPTHECLOCK_REQ).WriteByte(Bag).WriteByte(5).WriteUInt16(ShopItem).ToArray());
        Assert.Empty(x.Cash.Calls);
        Assert.False(x.Ca.Has(Msg.CS_STOPTHECLOCK_ACK));
    }

    [Fact]
    public async Task WasteRefine_TurnsAStoneIntoItsItem()
    {
        var x = await Open();
        Give(x, Stone, 1);
        Give(x, Sword, 2);
        byte Refine(byte slot)
        {
            x.H.Service.DispatchClientAsync(x.Sa, new PacketWriter(Msg.CS_WASTEREFINE_REQ).WriteByte(Bag).WriteByte(slot).ToArray()).GetAwaiter().GetResult();
            return new PacketReader(x.Ca.Last(Msg.CS_WASTEREFINE_ACK)!).ReadByte();
        }
        Assert.Equal(2, Refine(2));                                                              // a sword is no refine stone
        Assert.Equal(1, Refine(9));                                                              // nothing there
        Assert.Equal(0, Refine(1));
        var bag = x.A.FindInven(Bag)!;
        Assert.Null(bag.FindItem(1));
        Assert.Contains(bag.Items, i => i.TemplateId == Ore);
    }

    [Fact]
    public async Task WasteRefine_NeedsRoom()
    {
        var x = await Open();
        x.A.FindInven(Bag)!.SlotCount = 1;
        Give(x, Stone, 0);
        await x.H.Service.DispatchClientAsync(x.Sa, new PacketWriter(Msg.CS_WASTEREFINE_REQ).WriteByte(Bag).WriteByte(0).ToArray());
        Assert.Equal(3, new PacketReader(x.Ca.Last(Msg.CS_WASTEREFINE_ACK)!).ReadByte());
        Assert.NotNull(x.A.FindInven(Bag)!.FindItem(0));
    }
}
