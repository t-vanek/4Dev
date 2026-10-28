using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class MeetingRoomTests
{
    private static async Task<(MapTestHarness H, ClientSession S, FakeClientChannel C)> Setup()
    {
        var t = new TemplateStore();
        t.SpawnPositions[1] = new(1, 0, 100, 0, 100, 0);
        t.SpawnPositions[2] = new(2, 1101, 10, 0, 10, 0);
        t.SpawnPositions[15500] = new(15500, 0, 200, 0, 200, 0);
        t.Portals[1] = new(1, 3, 0, 1, 0); t.Portals[2] = new(2, 3, 0, 2, 0);
        t.Portals[1].Destinations[2] = new(2, 100, 1, new[] { new PortalCondition(12, 0) });
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Country = 3, MaxHp = 100, Hp = 100, Cooper = 1000 };
        var (s, c) = await h.EnterAsync(1, 1, 1, name: "Host", x: 100, z: 100, preSeeded: ch);
        h.Service.AddNpc(new Npc { Id = 70, Country = 3, PortalId = 1, Type = 8 });
        c.Clear(); h.World.Clear();
        return (h, s, c);
    }
    private static Task Rent(MapTestHarness h, ClientSession s, ushort npc = 70)
        => h.Service.DispatchClientAsync(s, new PacketWriter(Msg.CS_TELEPORT_REQ).WriteUInt16(npc).WriteUInt16(2).ToArray());
    private static async Task Arrive(MapTestHarness h, ClientSession s)
    {
        await h.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_STARTTELEPORT_REQ).WriteUInt32(s.CharId).WriteUInt32(s.Key)
            .WriteByte(1).WriteUInt16(1101).WriteFloat(10).WriteFloat(0).WriteFloat(10).ToArray());
        await h.FinishTeleportAsync(s);
    }

    [Fact]
    public async Task Reservation_IsExclusive_CannotBeBypassed_AndDisconnectReleasesIt()
    {
        var (h, owner, c) = await Setup();
        var (other, cc) = await h.EnterAsync(2, 2, 2); other.Char!.Country = 3; other.Char.Cooper = 1000;
        await Rent(h, owner); Assert.Equal(900, owner.Char!.MoneyTotal);
        await Rent(h, other); Assert.Equal(1000, other.Char.MoneyTotal);
        Assert.Equal(7, new PacketReader(cc.Last(Msg.CS_TELEPORT_ACK)!).ReadByte());
        await Rent(h, other, npc: 0); Assert.Equal(7, new PacketReader(cc.Last(Msg.CS_TELEPORT_ACK)!).ReadByte());
        h.Service.OnClientDisconnect(owner); cc.Clear();
        await Rent(h, other); Assert.True(cc.Has(Msg.CS_BEGINTELEPORT_ACK)); Assert.Equal(900, other.Char.MoneyTotal);
    }

    [Fact]
    public async Task OnlyOwnerInvites_BusyGuestsRefuse_AndUninvitedRepliesAreIgnored()
    {
        var (h, owner, c) = await Setup();
        await Rent(h, owner); await Arrive(h, owner); h.World.Clear();
        await h.Service.DispatchClientAsync(owner, new PacketWriter(Msg.CS_MEETINGROOM_REQ).WriteByte(0).WriteByte(0).WriteString("Guest").ToArray());
        Assert.True(h.World.Has(Msg.MW_MEETINGROOM_ACK));
        var (guest, cc) = await h.EnterAsync(2, 2, 2, name: "Guest"); h.World.Clear();
        await h.Service.DispatchClientAsync(guest, new PacketWriter(Msg.CS_MEETINGROOM_REQ).WriteByte(0).WriteByte(0).WriteString("Host").ToArray());
        var denied = new PacketReader(cc.Last(Msg.CS_MEETINGROOM_ACK)!); Assert.Equal((0, 4), (denied.ReadByte(), denied.ReadByte()));
        await h.Service.DispatchClientAsync(guest, new PacketWriter(Msg.CS_MEETINGROOM_REQ).WriteByte(1).WriteByte(0).WriteString("Host").ToArray());
        Assert.False(h.World.Has(Msg.MW_MEETINGROOM_ACK));
        guest.Char!.DuelId = 99;
        await h.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_MEETINGROOM_REQ).WriteUInt32(2).WriteUInt32(2)
            .WriteByte(0).WriteByte(0).WriteString("Host").ToArray());
        var busy = new PacketReader(h.World.Last(Msg.MW_MEETINGROOM_ACK)!); busy.ReadUInt32(); busy.ReadUInt32();
        Assert.Equal((1, 2), (busy.ReadByte(), busy.ReadByte()));
    }

    [Fact]
    public async Task OwnerLeaving_AndOneHourExpiry_EjectOccupants()
    {
        var (h, owner, _) = await Setup();
        await Rent(h, owner); await Arrive(h, owner);
        var (guest, cc) = await h.EnterAsync(2, 2, 2, mapId: 1101, x: 12, z: 10);
        cc.Clear(); h.World.Clear();
        h.Service.OnClientDisconnect(owner);
        Assert.True(cc.Has(Msg.CS_BEGINTELEPORT_ACK));

        var (hh, host, client) = await Setup();
        await Rent(hh, host); await Arrive(hh, host); client.Clear();
        for (int i = 0; i < 3600; i++) await hh.Service.OnTimerAsync();
        Assert.True(client.Has(Msg.CS_BEGINTELEPORT_ACK));
    }

    [Fact]
    public async Task OccupiedRooms_AreHiddenFromNpcList_AndStaleSavedEntriesReturnOutside()
    {
        var (h, owner, _) = await Setup(); await Rent(h, owner);
        var (other, client) = await h.EnterAsync(2, 2, 2); other.Char!.Country = 3;
        await h.Service.DispatchClientAsync(other, new PacketWriter(Msg.CS_NPCITEMLIST_REQ).WriteUInt16(70).ToArray());
        var list = new PacketReader(client.Last(Msg.CS_NPCITEMLIST_ACK)!);
        list.ReadUInt16(); list.ReadByte(); list.ReadByte(); Assert.Equal(0, list.ReadByte());
        var stale = new Character { CharId = 3, MapId = 1101, PosX = 10, PosZ = 10 };
        var session = new ClientSession(new FakeClientChannel()) { Char = stale };
        await h.Service.DispatchClientAsync(session, MapTestHarness.ConnectReq(3, 3, 3));
        Assert.Equal(((ushort)0, 200f, 200f), (stale.MapId, stale.PosX, stale.PosZ));
    }
}
