using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class CloakTests
{
    private sealed class Store : ICloakStore
    {
        public uint? Result = 500;
        public int Changes;
        public Task<List<CustomCloakRow>> LoadCustomCloaksAsync(uint u)
            => Task.FromResult(new List<CustomCloakRow> { new(7, 123), new(2, 456) });
        public Task<uint?> ChangeCustomCloakAsync(uint u, uint c, long item, ushort id)
        { Changes++; return Task.FromResult(Result); }
        public Task<uint?> RedeemMedalItemAsync(uint c, long item, byte count, uint medals) => Task.FromResult(Result);
    }
    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C, Item Cloak, Store Db)> Setup()
    {
        var t = new TemplateStore(); t.Items[100] = new(100, 0, new float[4], Type: 2, Kind: 19);
        var h = new MapTestHarness(t); var db = new Store(); h.Service.CloakStore = db;
        var ch = new Character { CharId = 1, Medals = 4500, GuildId = 1, MaxHp = 100, Hp = 100 };
        var cloak = new Item { TemplateId = 100, Template = t.Items[100], ItemSlot = 4, Count = 1, DlId = 111 };
        var equip = new Inven { InvenId = Proto.InvenEquip }; equip.Items.Add(cloak); ch.Invens.Add(equip);
        var (s, c) = await h.EnterAsync(1, 9, 10, preSeeded: ch); ch.GuildId = 1; c.Clear();
        return (h, s, c, cloak, db);
    }

    [Fact]
    public async Task List_UsesWordIdsAnd64BitDates_InIdOrder()
    {
        var x = await Setup();
        await x.H.Service.DispatchClientAsync(x.S, new PacketWriter(Msg.CS_CUSTOMCLOAK_LIST_REQ).ToArray());
        var r = new PacketReader(x.C.Last(Msg.CS_CUSTOMCLOAK_LIST_ACK)!);
        Assert.Equal(2, r.ReadUInt16());
        Assert.Equal(((ushort)2, 456L, (ushort)7, 123L), (r.ReadUInt16(), r.ReadInt64(), r.ReadUInt16(), r.ReadInt64()));
    }

    [Fact]
    public async Task Change_UpdatesTextureBalanceAndViewers_RefusalChargesNothing()
    {
        var x = await Setup();
        var (_, viewer) = await x.H.EnterAsync(2, 10, 11);
        await x.H.Service.DispatchClientAsync(x.S, new PacketWriter(Msg.CS_CUSTOMCLOAK_CHANGE_REQ).WriteUInt16(7).ToArray());
        Assert.Equal(7u, x.Cloak.Ext[Item.IevCustomTex]); Assert.Equal(500u, x.S.Char!.Medals);
        Assert.True(viewer.Has(Msg.CS_CUSTOMCLOAK_CHANGE_ACK));
        x.S.Char.Medals = 4500; x.Db.Result = null;
        await x.H.Service.DispatchClientAsync(x.S, new PacketWriter(Msg.CS_CUSTOMCLOAK_CHANGE_REQ).WriteUInt16(8).ToArray());
        Assert.Equal(7u, x.Cloak.Ext[Item.IevCustomTex]); Assert.Equal(4500u, x.S.Char.Medals);
        var r = new PacketReader(x.C.Last(Msg.CS_CUSTOMCLOAK_CHANGE_ACK)!); r.ReadUInt32(); r.ReadUInt16(); Assert.Equal(0, r.ReadByte());
    }

    [Fact]
    public async Task GuildRegistration_RequiresGuildAndCloak_AndCanBeRemoved()
    {
        var x = await Setup();
        async Task Register(byte value) => await x.H.Service.DispatchClientAsync(x.S,
            new PacketWriter(Msg.CS_REGGUILDCLOAK_REQ).WriteByte(Proto.InvenEquip).WriteByte(4).WriteByte(value).ToArray());
        x.S.Char!.GuildId = 0; await Register(1); Assert.Equal(0u, x.Cloak.Ext[Item.IevGuild]);
        x.S.Char.GuildId = 8; await Register(1); Assert.Equal(1u, x.Cloak.Ext[Item.IevGuild]);
        await Register(0); Assert.Equal(0u, x.Cloak.Ext[Item.IevGuild]);
        Assert.True(x.C.Has(Msg.CS_REGGUILDCLOAK_ACK));
    }
}
