using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class OtherSelfTests
{
    // CSProtocol.h / client CSSender.cpp: the request follows ADDRECALLMON with template 0
    // when the summon is visible but its owner is outside the player's view.
    private const ushort RequestId = Msg.CS_OTHERSELF_REQ, ReplyId = Msg.CS_OTHERSELF_ACK;
    private const uint OwnerId = 1, ViewerId = 2, Key = 41, MonId = 900;

    private static byte[] Request(uint owner = OwnerId, uint mon = MonId)
        => new PacketWriter(RequestId).WriteUInt32(owner).WriteUInt32(mon).ToArray();

    private static byte[] Create(ushort chart = 0)
    {
        var w = new PacketWriter(Msg.MW_CREATERECALLMON_REQ);
        w.WriteUInt32(OwnerId); w.WriteUInt32(Key); w.WriteUInt32(MonId);
        w.WriteUInt16(chart); w.WriteUInt32(1001 | (10u << 16));
        w.WriteUInt16(0); w.WriteByte(0); w.WriteString("");
        w.WriteByte(10); w.WriteByte(5); w.WriteByte(2);
        w.WriteByte(0); w.WriteByte(1); w.WriteByte(0);
        w.WriteUInt32(500); w.WriteUInt32(100); w.WriteUInt32(500); w.WriteUInt32(100);
        w.WriteByte(100); w.WriteByte(1);
        w.WriteFloat(110); w.WriteFloat(0); w.WriteFloat(100);
        w.WriteUInt16(0); w.WriteUInt32(5000);
        w.WriteByte(0); w.WriteUInt32(0); w.WriteByte(0); w.WriteByte(0);
        return w.ToArray();
    }

    private static async Task<(MapTestHarness h, ClientSession ownerSession, Character owner,
        ClientSession viewer, FakeClientChannel client, RecallMon mon)> Setup(ushort chart = 0)
    {
        var templates = new TemplateStore();
        templates.MonsterTemplates[chart] = new MonsterTemplate(chart, 10, 0, RecallType: 1, SummonAttr: 1001);
        templates.MonAttrs[TemplateStore.MonAttrKey(1001, 10)] = new MonAttrRow(1001, 10, 500, 100, 0);
        var h = new MapTestHarness(templates);
        var owner = new Character { CharId = OwnerId, Hp = 500, MaxHp = 500 };
        var (ownerSession, _) = await h.EnterAsync(OwnerId, 10, Key, name: "Summoner", x: 400, z: 100, preSeeded: owner);
        var (viewer, client) = await h.EnterAsync(ViewerId, 20, 42, x: 100, z: 100);
        await h.Service.DispatchWorldAsync(Create(chart));
        Assert.True(client.Has(Msg.CS_ADDRECALLMON_ACK));
        Assert.DoesNotContain(ownerSession, h.State.InView(viewer));
        var mon = owner.Recalls[MonId];
        client.Clear(); h.World.Clear();
        return (h, ownerSession, owner, viewer, client, mon);
    }

    [Fact]
    public async Task VisibleDoppelgangerWithDistantOwnerGetsCompleteNativeAppearanceAndEquippedItems()
    {
        var (h, _, owner, viewer, client, mon) = await Setup();
        owner.GuildId = 101; owner.Fame = 102; owner.FameColor = 103; owner.GuildName = "Guild";
        owner.GuildPeer = 4; owner.TacticsId = 105; owner.TacticsName = "Tactics";
        owner.Class = 5; owner.Race = 2; owner.Country = 1; owner.AidCountry = 3;
        owner.Sex = 1; owner.Hair = 2; owner.Face = 3; owner.Body = 4;
        owner.Pants = 5; owner.Hand = 6; owner.Foot = 7; owner.HelmetHide = 1; owner.Level = 10;
        owner.PartyChiefId = 200; owner.PartyId = 201; owner.CommanderId = 202;
        viewer.Char!.CompanionSlot = 2;
        var equip = new Inven { InvenId = Proto.InvenEquip };
        var weapon = new Item { ItemSlot = 7, TemplateId = 301, Count = 1, DuraMax = 80, DuraCur = 75 };
        weapon.Ext[Item.IevGuild] = OwnerId;
        equip.Items.Add(weapon);
        equip.Items.Add(new Item { ItemSlot = 1, TemplateId = 302, Count = 1 });
        owner.Invens.Add(equip);
        var bag = new Inven { InvenId = 0 };
        bag.Items.Add(new Item { TemplateId = 999, Count = 1 }); owner.Invens.Add(bag);

        await h.Service.DispatchClientAsync(viewer, Request());

        var r = new PacketReader(Assert.Single(client.WithId(ReplyId)));
        Assert.Equal(MonId, r.ReadUInt32()); Assert.Equal(OwnerId, r.ReadUInt32());
        Assert.Equal("Summoner", r.ReadString());
        Assert.Equal(101u, r.ReadUInt32()); Assert.Equal(102u, r.ReadUInt32()); Assert.Equal(103u, r.ReadUInt32());
        Assert.Equal("Guild", r.ReadString()); Assert.Equal(4, r.ReadByte());
        Assert.Equal(105u, r.ReadUInt32()); Assert.Equal("Tactics", r.ReadString());
        foreach (var field in new byte[] { 5, 2, 1, 3, 1, 2, 3, 4, 5, 6, 7, 1, 10 }) Assert.Equal(field, r.ReadByte());
        Assert.Equal(200u, r.ReadUInt32()); Assert.Equal(201, r.ReadUInt16()); Assert.Equal(202, r.ReadUInt16());
        Assert.Equal(2, r.ReadByte());
        var expectedItems = new PacketWriter(ReplyId);
        foreach (var item in equip.Items.OrderBy(i => i.ItemSlot)) item.WrapPacketClient(expectedItems, OwnerId);
        Assert.Equal(expectedItems.ToArray()[PacketHeader.Size..], r.ReadRemaining().ToArray());
        Assert.Equal(2, new PacketReader(Assert.Single(client.WithId(Msg.CS_UPDATESPAWNEDCOMPANION_REQ))).ReadByte());
        Assert.Same(mon, owner.Recalls[MonId]); Assert.Empty(h.World.Sent);
    }

    [Fact]
    public async Task UnequippedOwnerWritesAnEmptyEquipmentList()
    {
        var (h, _, _, viewer, client, _) = await Setup();
        await h.Service.DispatchClientAsync(viewer, Request());
        Assert.Equal(0, Assert.Single(client.WithId(ReplyId))[^1]);
    }

    [Fact]
    public async Task SecondaryOwnerCopyCanSupplyAppearanceForItsLocallyVisibleSummon()
    {
        var (h, ownerSession, _, viewer, client, _) = await Setup();
        ownerSession.IsMain = false;
        await h.Service.DispatchClientAsync(viewer, Request());
        Assert.Single(client.WithId(ReplyId));
    }

    [Theory]
    [InlineData("secondary viewer")]
    [InlineData("loading viewer")]
    [InlineData("teleporting viewer")]
    [InlineData("loading owner")]
    [InlineData("missing owner")]
    [InlineData("wrong owner")]
    [InlineData("wrong summon")]
    [InlineData("wrong ownership")]
    [InlineData("removed summon")]
    [InlineData("replaced summon")]
    [InlineData("self object")]
    [InlineData("companion")]
    [InlineData("distant summon")]
    [InlineData("different map")]
    [InlineData("different channel")]
    [InlineData("pending deletion")]
    [InlineData("expired")]
    public async Task UnavailableOrUnrelatedSummonsDoNotExposeOwnerAppearance(string scenario)
    {
        var (h, ownerSession, owner, viewer, client, mon) = await Setup();
        switch (scenario)
        {
            case "secondary viewer": viewer.IsMain = false; break;
            case "loading viewer": viewer.State = EnterState.Entering; break;
            case "teleporting viewer":
                h.Options.Channels = new byte[] { 1, 2 };
                await h.Service.OnWorldConnectedAsync();
                await h.Service.DispatchClientAsync(viewer, new PacketWriter(Msg.CS_CHGCHANNEL_REQ).WriteByte(2).ToArray());
                Assert.True(viewer.TeleportPending);
                client.Clear(); break;
            case "loading owner": ownerSession.State = EnterState.Entering; break;
            case "missing owner": h.State.Remove(ownerSession); break;
            case "wrong ownership": mon.OwnerId = ViewerId; break;
            case "removed summon": h.State.RemoveRecall(mon); break;
            case "replaced summon": owner.Recalls[MonId] = new RecallMon { Id = MonId, OwnerId = OwnerId }; break;
            case "self object": h.State.RemoveRecall(mon); mon.ObjType = RecallMon.OtSelf; h.State.AddRecall(mon); break;
            case "companion": h.State.RemoveRecall(mon); mon.ObjType = RecallMon.OtCompanion; h.State.AddRecall(mon); break;
            case "distant summon": h.State.MoveRecall(mon, 900, 900); break;
            case "different map": h.State.RemoveRecall(mon); mon.MapId = 7; h.State.AddRecall(mon); break;
            case "different channel": h.State.RemoveRecall(mon); mon.Channel = 2; h.State.AddRecall(mon); break;
            case "pending deletion": mon.DeleteAsked = true; break;
            case "expired": h.Service.NowMs = 5000; break;
        }
        await h.Service.DispatchClientAsync(viewer, Request(scenario == "wrong owner" ? ViewerId : OwnerId,
            scenario == "wrong summon" ? MonId + 1 : MonId));
        Assert.False(client.Has(ReplyId));
        Assert.False(client.Has(Msg.CS_UPDATESPAWNEDCOMPANION_REQ));
    }

    [Fact]
    public async Task OrdinarySummonDoesNotUseTheDoppelgangerAppearanceFlow()
    {
        var (h, _, _, viewer, client, _) = await Setup(chart: 500);
        await h.Service.DispatchClientAsync(viewer, Request());
        Assert.False(client.Has(ReplyId));
    }

    [Fact]
    public async Task ClientRequestOnWorldPlaneCannotProduceAnAppearanceReply()
    {
        var (h, _, _, _, client, _) = await Setup();
        await h.Service.DispatchWorldAsync(Request());
        Assert.False(client.Has(ReplyId));
    }
}
