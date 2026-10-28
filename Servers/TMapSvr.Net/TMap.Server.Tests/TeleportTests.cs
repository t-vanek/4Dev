using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// NPC portals (C++ OnCS_TELEPORT_REQ, CSHandler.cpp:5904), the move (Teleport, TMapSvr.cpp:7189) and the
/// single-server world round trip that relocates the player without dropping its socket.
/// </summary>
public class TeleportTests
{
    private const ushort Here = 1001, There = 1002, Far = 4001;   // portal ids (the live chart's first rows)
    private const ushort Toll = 5001;                              // an item a destination demands

    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        t.SpawnPositions[10] = new SpawnPosRow(10, 0, 100, 0, 100, 0);    // right here
        t.SpawnPositions[20] = new SpawnPosRow(20, 0, 110, 0, 105, 0);    // under a cell away
        t.SpawnPositions[30] = new SpawnPosRow(30, 3, 900, 5, 700, 0);    // another map
        var here = new PortalRow(Here, 3, 0, 10, 0);
        t.Portals[Here] = here;
        t.Portals[There] = new PortalRow(There, 3, 0, 20, 0);
        t.Portals[Far] = new PortalRow(Far, 3, 0, 30, 0);
        var none = new[] { new PortalCondition(0, 0), new PortalCondition(0, 0), new PortalCondition(0, 0) };
        here.Destinations[There] = new PortalDestination(There, 0, 1, none);
        here.Destinations[Far] = new PortalDestination(Far, 346, 1,
            new[] { new PortalCondition(2 /* PCT_HAVEITEM */, Toll), new PortalCondition(0, 0), new PortalCondition(0, 0) });
        t.Items[Toll] = new ItemTemplate(Toll, 0, new float[4]);
        return t;
    }

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch)> Setup(long money = 1000, bool withToll = true)
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Hero", MaxHp = 100, Hp = 100, Country = 3 };
        var bag = new Inven { InvenId = 0 };
        if (withToll) bag.Items.Add(new Item { ItemSlot = 0, TemplateId = Toll, Template = t.Items[Toll], Count = 2 });
        ch.Invens.Add(bag);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: ch);
        ch.Gold = 0; ch.Silver = 0; ch.Cooper = (uint)money;
        h.Service.AddNpc(new Npc { Id = 70, Type = 8 /* TNPC_PORTAL */, Country = 3, PortalId = Here });
        c.Clear(); h.World.Clear();
        return (h, s, c, ch);
    }

    private static byte[] Req(ushort npc, ushort portal)
    {
        var w = new PacketWriter(Msg.CS_TELEPORT_REQ);
        w.WriteUInt16(npc); w.WriteUInt16(portal);
        return w.ToArray();
    }

    private static byte Result(FakeClientChannel c) => new PacketReader(c.Last(Msg.CS_TELEPORT_ACK)!).ReadByte();

    // ================= the portal gates =================

    [Fact]
    public async Task AShortHop_IsAnsweredLocally()
    {
        var (h, s, c, _) = await Setup();

        await h.Service.DispatchClientAsync(s, Req(70, There));

        var r = new PacketReader(c.Last(Msg.CS_TELEPORT_ACK)!);
        Assert.Equal(0, r.ReadByte());                          // TPR_SUCCESS
        Assert.Equal(1u, r.ReadUInt32());                       // dwID
        Assert.Equal(1, r.ReadByte());                          // OT_PC
        Assert.Equal(0u, r.ReadUInt32());                       // dwRange
        Assert.Equal(0, r.ReadUInt16());
        Assert.Equal(110f, r.ReadFloat());
        Assert.False(h.World.Has(Msg.MW_BEGINTELEPORT_ACK));
    }

    [Fact]
    public async Task AFarTeleport_GoesThroughTheWorld_ChargesAndConsumes()
    {
        var (h, s, c, ch) = await Setup();

        await h.Service.DispatchClientAsync(s, Req(70, Far));

        var r = new PacketReader(h.World.Last(Msg.MW_BEGINTELEPORT_ACK)!);
        Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(s.Key, r.ReadUInt32());
        Assert.Equal(0, r.ReadByte());                          // bSameChannel
        r.ReadByte();
        Assert.Equal(3, r.ReadUInt16());                        // the destination map
        Assert.True(c.Has(Msg.CS_BEGINTELEPORT_ACK));
        Assert.Equal(1000 - 346, ch.MoneyTotal);
        Assert.Equal(1, ch.Invens[0].Items.Single().Count);     // one toll item used
    }

    [Fact]
    public async Task NotEnoughMoney_Refuses()
    {
        var (h, s, c, ch) = await Setup(money: 100);

        await h.Service.DispatchClientAsync(s, Req(70, Far));

        Assert.Equal(4, Result(c));                             // TPR_NEEDMONEY
        Assert.Equal(100, ch.MoneyTotal);
    }

    [Fact]
    public async Task AMissingRequiredItem_Refuses()
    {
        var (h, s, c, _) = await Setup(withToll: false);

        await h.Service.DispatchClientAsync(s, Req(70, Far));

        Assert.Equal(5, Result(c));                             // TPR_NOITEM
        Assert.False(h.World.Has(Msg.MW_BEGINTELEPORT_ACK));
    }

    [Fact]
    public async Task ADestinationThePortalDoesNotOffer_Refuses()
    {
        var (h, s, c, _) = await Setup();

        await h.Service.DispatchClientAsync(s, Req(70, Here));  // Here is not one of Here's destinations

        Assert.Equal(3, Result(c));                             // TPR_NODESTINATION
    }

    [Fact]
    public async Task ADeadPlayer_CannotTeleport()
    {
        var (h, s, c, ch) = await Setup();
        ch.Hp = 0;

        await h.Service.DispatchClientAsync(s, Req(70, There));

        Assert.Equal(6, Result(c));                             // TPR_INVALID
    }

    // ================= the world round trip =================

    [Fact]
    public async Task TheWorldRoundTrip_ReplacesThePlayerAtTheDestination()
    {
        var (h, s, c, ch) = await Setup();
        ch.Hp = 73; // Teleporting preserves current health; it neither kills nor refills the player.
        var (other, otherClient) = await h.EnterAsync(2, 2, 2, name: "Other", x: 105, z: 100);
        c.Clear(); otherClient.Clear(); h.World.Clear();

        // MW_STARTTELEPORT_REQ: leave the map, take the destination, report the owning server.
        var start = new PacketWriter(Msg.MW_STARTTELEPORT_REQ);
        start.WriteUInt32(1); start.WriteUInt32(s.Key); start.WriteByte(s.Channel); start.WriteUInt16(3);
        start.WriteFloat(900); start.WriteFloat(5); start.WriteFloat(700);
        await h.Service.DispatchWorldAsync(start.ToArray());

        Assert.Equal(EnterState.Entering, s.State);
        Assert.Equal(3, ch.MapId);
        Assert.Equal(900f, ch.PosX);
        Assert.True(otherClient.Has(Msg.CS_LEAVE_ACK));        // the neighbour saw it leave
        var tp = new PacketReader(h.World.Last(Msg.MW_TELEPORT_ACK)!);
        tp.ReadUInt32(); tp.ReadUInt32();
        Assert.Equal(h.Options.ServerId, tp.ReadByte());

        // MW_TELEPORT_REQ: the verdict goes to the client, which loads the map.
        var verdict = new PacketWriter(Msg.MW_TELEPORT_REQ);
        verdict.WriteUInt32(1); verdict.WriteUInt32(s.Key); verdict.WriteByte(s.Channel); verdict.WriteUInt16(3);
        verdict.WriteFloat(900); verdict.WriteFloat(5); verdict.WriteFloat(700); verdict.WriteByte(0);
        await h.Service.DispatchWorldAsync(verdict.ToArray());
        Assert.Equal(0, Result(c));

        // MW_CONLIST_REQ: this server is the one the destination needs.
        var conlist = new PacketWriter(Msg.MW_CONLIST_REQ);
        conlist.WriteUInt32(1); conlist.WriteUInt32(s.Key); conlist.WriteByte(s.Channel); conlist.WriteUInt16(3);
        conlist.WriteFloat(900); conlist.WriteFloat(5); conlist.WriteFloat(700);
        await h.Service.DispatchWorldAsync(conlist.ToArray());
        var cl = new PacketReader(h.World.Last(Msg.MW_CONLIST_ACK)!);
        cl.ReadUInt32(); cl.ReadUInt32();
        Assert.Equal(1, cl.ReadByte());
        Assert.Equal(h.Options.ServerId, cl.ReadByte());

        // The ordinary tail: CHECKMAIN → CONRESULT → CS_CONNECT_ACK, then the client's CONREADY re-places it.
        await h.Service.DispatchWorldAsync(MapTestHarness.CheckMainReq(1, s.Key, s.Channel, 3, 900, 5, 700));
        await h.Service.DispatchWorldAsync(MapTestHarness.ConResultReq(1, s.Key, ConnectResult.Success));
        Assert.True(c.Has(Msg.CS_CONNECT_ACK));
        await h.Service.DispatchClientAsync(s, MapTestHarness.ConReady());

        Assert.Equal(EnterState.InGame, s.State);
        Assert.Equal(73u, ch.Hp);
        Assert.False(c.Has(Msg.CS_DIE_ACK));
        Assert.NotNull(s.Grid);
        Assert.DoesNotContain(h.State.Neighbors(s), n => n == other);   // placed on the other map, away from it
    }

    [Fact]
    public async Task ATeleportEndsATradeInProgress()
    {
        var (h, s, c, ch) = await Setup();
        s.Deal.SetTarget("Nobody");
        s.Deal.Status = 1;                                      // DEAL_START

        var start = new PacketWriter(Msg.MW_STARTTELEPORT_REQ);
        start.WriteUInt32(1); start.WriteUInt32(s.Key); start.WriteByte(s.Channel); start.WriteUInt16(3);
        start.WriteFloat(900); start.WriteFloat(5); start.WriteFloat(700);
        await h.Service.DispatchWorldAsync(start.ToArray());

        Assert.True(c.Has(Msg.CS_DEALITEMEND_ACK));
        Assert.False(s.Deal.InProgress);
    }
}
