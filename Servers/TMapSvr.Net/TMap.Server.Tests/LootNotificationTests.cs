using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class LootNotificationTests
{
    // Screenshot reproduction: [corpse slot 0, template 17211 = 3B 43] was decoded as
    // template 15104 (Soath's seal), grade 67. GETITEM has no slot; ADD/UPDATE still do.
    private const ushort Herb = 17211;

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] // individual pickup
    [InlineData(1, false)] [InlineData(1, true)] // take all
    [InlineData(2, false)] [InlineData(2, true)] // asynchronous party lottery
    public async Task PickupAnnouncementMatchesTheItemActuallyAddedOrMerged(int mode, bool merge)
    {
        var templates = new TemplateStore();
        templates.Items[Herb] = new ItemTemplate(Herb, 0, new float[4], Stack: 10);
        var h = new MapTestHarness(templates);
        var ch = Hero(1, mode == 2);
        if (merge) ch.FindInven(255)!.Items.Add(new Item
            { TemplateId = Herb, Template = templates.Items[Herb], ItemSlot = 7, Count = 3 });
        var (s, c) = await h.EnterAsync(1, 1, 11, x: 100, z: 100, preSeeded: ch);
        ClientSession? mate = null;
        if (mode == 2)
            (mate, _) = await h.EnterAsync(2, 2, 22, x: 100, z: 100, preSeeded: Hero(2, true));
        if (mate is not null)
            foreach (var member in new[] { ch, mate.Char! }) { member.PartyId = 42; member.PartyType = 3; }
        var mon = new Monster { Id = 0x70001, MapId = 0, Channel = 1, Hp = 0, MaxHp = 100, PosX = 100, PosZ = 100 };
        h.Service.SpawnMonster(mon);
        mon.AddDamage(1, mode == 2 ? (ushort)42 : (ushort)0, 100);
        mon.CorpseInven.Items.Add(new Item { TemplateId = Herb, Template = templates.Items[Herb], ItemSlot = 0, Count = 1 });
        c.Clear();

        await h.Service.DispatchClientAsync(s, mode == 1
            ? new PacketWriter(Msg.CS_MONITEMTAKEALL_REQ).WriteUInt32(mon.Id).ToArray()
            : MapTestHarness.MonItemTakeReq(mon.Id, 0));
        if (mate is not null)
        {
            Assert.False(c.Has(Msg.CS_GETITEM_ACK));
            await h.Service.DispatchClientAsync(mate, new PacketWriter(Msg.CS_MONITEMLOTTERY_REQ)
                .WriteByte(0).WriteUInt32(mon.Id).WriteUInt16(Herb).ToArray());
        }

        var stored = Assert.Single(ch.FindInven(255)!.Items);
        Assert.Equal(Herb, stored.TemplateId);
        Assert.Equal(merge ? 4 : 1, stored.Count);
        Assert.Empty(mon.CorpseInven.Items);
        var mutation = new PacketReader(c.Last(merge ? Msg.CS_UPDATEITEM_ACK : Msg.CS_ADDITEM_ACK)!);
        Assert.Equal(255, mutation.ReadByte());
        Assert.Equal(merge ? 7 : 0, mutation.ReadByte());
        AssertItemData(mutation, merge ? (byte)4 : (byte)1);
        // OnCS_GETITEM_ACK calls SetItemData directly, without SetItemSlot.
        AssertItemData(new PacketReader(c.Last(Msg.CS_GETITEM_ACK)!), 1);
        Assert.Equal((byte)MonItemTakeResult.Success, new PacketReader(c.Last(Msg.CS_MONITEMTAKE_ACK)!).ReadByte());
    }

    private static Character Hero(uint id, bool lottery)
    {
        var ch = new Character { CharId = id, Hp = 100, MaxHp = 100,
            PartyId = lottery ? (ushort)42 : (ushort)0, PartyType = lottery ? (byte)3 : (byte)0 };
        ch.Invens.Add(new Inven { InvenId = 255, SlotCount = 16 });
        return ch;
    }

    // Read the native SetItemData + SetItemMagic field order independently of WrapPacketClient.
    private static void AssertItemData(PacketReader r, byte count)
    {
        Assert.Equal(Herb, r.ReadUInt16());
        Assert.Equal(0, r.ReadByte()); // grade
        Assert.Equal(0, r.ReadByte()); // gem
        Assert.Equal(0, r.ReadUInt16()); // mogg
        Assert.Equal(0, r.ReadUInt16()); // companion
        Assert.Equal(count, r.ReadByte());
        Assert.Equal(0u, r.ReadUInt32()); // max durability
        Assert.Equal(0u, r.ReadUInt32()); // current durability
        Assert.Equal(0, r.ReadByte()); // max refine
        Assert.Equal(0, r.ReadByte()); // current refine
        Assert.Equal(0, r.ReadByte()); // gamble
        Assert.Equal(0L, r.ReadInt64()); // expiry
        Assert.Equal(0, r.ReadByte()); // grade effect
        Assert.Equal(0, r.ReadByte()); // ELD
        Assert.Equal(0, r.ReadByte()); // wrap
        Assert.Equal(0, r.ReadUInt16()); // colour
        Assert.Equal(0, r.ReadUInt16()); // custom texture
        Assert.Equal(0, r.ReadByte()); // guild registration
        Assert.Equal(0, r.ReadByte()); // magic count
        Assert.Equal(0, r.Remaining);
    }
}
