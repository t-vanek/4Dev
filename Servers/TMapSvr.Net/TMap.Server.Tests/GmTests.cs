using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

internal sealed class FakeGmStore : IGmStore
{
    public List<(uint User, uint Char, ushort Gift, uint Gm, ushort Err)> Logs { get; } = new();
    public Task GiftLogAsync(uint userId, uint charId, ushort giftId, uint gmCharId, ushort errId)
    { Logs.Add((userId, charId, giftId, gmCharId, errId)); return Task.CompletedTask; }
}

/// <summary>Batch S7c: the GM tools — in-game operator commands, the world's GM messages, chat bans, help messages, the map level
/// kick-out and the control server's link.</summary>
public class GmTests
{
    private const uint Gm = 1, Bob = 2;
    private const ushort Home = 100, Portal = 7, Dest = 8;

    private sealed record Setup(TemplateStore T, MapTestHarness H, FakeGmStore Db, ClientSession Sg, FakeClientChannel Cg, Character G,
        ClientSession Sb, FakeClientChannel Cb, Character B);

    private static async Task<Setup> Open(Action<TemplateStore>? seed = null)
    {
        var t = new TemplateStore();
        t.Operators.Add(Gm);
        t.SpawnPositions[Home] = new SpawnPosRow(Home, 0, 3000, 0, 3000, 0);
        seed?.Invoke(t);
        var h = new MapTestHarness(t);
        var db = new FakeGmStore();
        h.Service.GmStore = db;
        Character Pc(uint id, string name) => new() { CharId = id, Name = name, Level = 20, Country = 1, MaxHp = 100, Hp = 100 };
        var g = Pc(Gm, "Gina"); var b = Pc(Bob, "Bob");
        var (sg, cg) = await h.EnterAsync(Gm, 11, 21, name: "Gina", x: 100, z: 100, preSeeded: g);
        var (sb, cb) = await h.EnterAsync(Bob, 12, 22, name: "Bob", x: 104, z: 100, preSeeded: b);
        h.World.Clear(); cg.Clear(); cb.Clear();
        return new Setup(t, h, db, sg, cg, g, sb, cb, b);
    }

    private static Task Client(ClientSession s, MapTestHarness h, PacketWriter w) => h.Service.DispatchClientAsync(s, w.ToArray());

    [Fact]
    public async Task OnlyAnOperator_CanBanChat()
    {
        var x = await Open();
        await Client(x.Sb, x.H, new PacketWriter(Msg.CS_CHATBAN_REQ).WriteString("Gina").WriteUInt16(5));
        Assert.Equal(1, new PacketReader(x.Cb.Last(Msg.CS_CHATBAN_ACK)!).ReadByte());       // CHATBAN_NOTOPERATOR
        Assert.False(x.H.World.Has(Msg.MW_CHATBAN_ACK));

        await Client(x.Sg, x.H, new PacketWriter(Msg.CS_CHATBAN_REQ).WriteString("Bob").WriteUInt16(5));
        var r = new PacketReader(x.H.World.Last(Msg.MW_CHATBAN_ACK)!);
        Assert.Equal(("Bob", (ushort)5, Gm, 21u), (r.ReadString(), r.ReadUInt16(), r.ReadUInt32(), r.ReadUInt32()));
    }

    [Fact]
    public async Task ABannedPlayer_CannotChat_AndTheGmIsTold()
    {
        var x = await Open();
        long until = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_CHATBAN_REQ).WriteString("Bob").WriteInt64(until).WriteByte(0)
            .WriteUInt32(Gm).WriteUInt32(21).ToArray());
        Assert.Equal(until, x.B.ChatBanTime);
        Assert.Equal(0, new PacketReader(x.Cg.Last(Msg.CS_CHATBAN_ACK)!).ReadByte());

        await Client(x.Sb, x.H, new PacketWriter(Msg.CS_CHAT_REQ).WriteString("Bob").WriteByte(0).WriteUInt32(0).WriteString("").WriteString("hi"));
        var r = new PacketReader(x.Cb.Last(Msg.CS_SYSTEMMSG_ACK)!);
        Assert.Equal(10, r.ReadByte());                                                       // SM_CHAT_BAN
        Assert.InRange(r.ReadUInt32(), 290u, 300u);
        Assert.False(x.Cg.Has(Msg.CS_CHAT_ACK));
    }

    [Fact]
    public async Task TheMoveCommands_AreForOperators()
    {
        var x = await Open();
        await Client(x.Sb, x.H, new PacketWriter(Msg.CS_CMMOVE_REQ).WriteByte(1).WriteUInt16(3).WriteFloat(500).WriteFloat(0).WriteFloat(500));
        Assert.False(x.H.World.Has(Msg.MW_BEGINTELEPORT_ACK));
        await Client(x.Sg, x.H, new PacketWriter(Msg.CS_CMMOVE_REQ).WriteByte(1).WriteUInt16(3).WriteFloat(500).WriteFloat(0).WriteFloat(500));
        var tp = new PacketReader(x.H.World.Last(Msg.MW_BEGINTELEPORT_ACK)!);
        tp.ReadUInt32(); tp.ReadUInt32(); tp.ReadByte(); tp.ReadByte();
        Assert.Equal((ushort)3, tp.ReadUInt16());

        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_STARTTELEPORT_REQ)
            .WriteUInt32(x.Sg.CharId).WriteUInt32(x.Sg.Key).WriteByte(1).WriteUInt16(3)
            .WriteFloat(500).WriteFloat(0).WriteFloat(500).ToArray());
        await x.H.FinishTeleportAsync(x.Sg);

        await Client(x.Sg, x.H, new PacketWriter(Msg.CS_CMMOVETOUSER_REQ).WriteString("Bob"));
        var r = new PacketReader(x.H.World.Last(Msg.CT_USERPOSITION_ACK)!);
        Assert.Equal(("Bob", "Gina"), (r.ReadString(), r.ReadString()));
    }

    [Fact]
    public async Task APlayersPosition_IsGivenForTheGm()
    {
        var x = await Open();
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_USERPOSITION_REQ).WriteUInt32(Bob).WriteUInt32(22).WriteString("Gina").ToArray());
        var r = new PacketReader(x.H.World.Last(Msg.CT_USERMOVE_ACK)!);
        Assert.Equal(("Gina", (byte)1, (ushort)0, 104f), (r.ReadString(), r.ReadByte(), r.ReadUInt16(), r.ReadFloat()));
    }

    private static byte[] Gift(uint target, byte type, uint value, byte tool = 0, byte count = 1)
        => new PacketWriter(Msg.MW_CMGIFT_REQ).WriteUInt32(target).WriteByte(type).WriteUInt32(value).WriteByte(count).WriteString("For you")
            .WriteString("Enjoy").WriteUInt32(Gm).WriteByte(tool).WriteUInt16(42).WriteUInt16(43).WriteByte(0).ToArray();

    [Fact]
    public async Task AGift_IsGiven_Logged_AndTheGmTold()
    {
        var x = await Open();
        await Client(x.Sg, x.H, new PacketWriter(Msg.CS_CMGIFT_REQ).WriteString("Bob").WriteUInt16(42));
        var ask = new PacketReader(x.H.World.Last(Msg.MW_CMGIFT_ACK)!);
        Assert.Equal(("Bob", (ushort)42, Gm), (ask.ReadString(), ask.ReadUInt16(), ask.ReadUInt32()));

        await x.H.Service.DispatchWorldAsync(Gift(Bob, 4, 25));                                // CGT_MERIT
        await Task.Delay(50);
        Assert.Equal(25u, x.B.PvpUseablePoint);
        Assert.Equal(0, new PacketReader(x.Cg.Last(Msg.CS_CMGIFT_ACK)!).ReadByte());
        Assert.Equal((12u, Bob, (ushort)42, Gm, (ushort)0), Assert.Single(x.Db.Logs));
    }

    [Fact]
    public async Task AGiftForSomeoneAway_OrUnknown_IsAnswered()
    {
        var x = await Open();
        await x.H.Service.DispatchWorldAsync(Gift(99, 4, 25));
        Assert.Equal(1, new PacketReader(x.Cg.Last(Msg.CS_CMGIFT_ACK)!).ReadByte());         // CMGIFT_TARGET (the C++ said nothing)
        await x.H.Service.DispatchWorldAsync(Gift(Bob, 1, 999));
        Assert.Equal(2, new PacketReader(x.Cg.Last(Msg.CS_CMGIFT_ACK)!).ReadByte());         // CMGIFT_ID: no such item
        Assert.Empty(x.Db.Logs);

        await x.H.Service.DispatchWorldAsync(Gift(Bob, 5, 10, tool: 1));                     // from the admin tool: the world is told
        var r = new PacketReader(x.H.World.Last(Msg.MW_CMGIFTRESULT_ACK)!);
        Assert.Equal((0, (byte)1, Gm), (r.ReadByte(), r.ReadByte(), r.ReadUInt32()));
        Assert.Equal(0u, Assert.Single(x.Db.Logs).Gm);
    }

    [Fact]
    public async Task AnOperatorsLine_ReachesThePlayer()
    {
        var x = await Open();
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_CHARMSG_REQ).WriteString("Bob").WriteString("Behave").ToArray());
        var r = new PacketReader(x.Cb.Last(Msg.CS_CHAT_ACK)!);
        Assert.Equal((byte)ChatGroup.Operator, r.ReadByte());
    }

    [Fact]
    public async Task TheHelpMessage_IsShownOnEntering_AndAnEventOneAtOnce()
    {
        var x = await Open(t => t.HelpMessages.Add((2, 0, 0, "Zoom with the wheel")));
        var (_, c) = await x.H.EnterAsync(3, 13, 23, name: "Cid", preSeeded: new Character { CharId = 3, Name = "Cid", Level = 20, MaxHp = 100, Hp = 100 });
        var r = new PacketReader(c.Last(Msg.CS_HELPMESSAGE_ACK)!);
        Assert.Equal((2, "Zoom with the wheel"), (r.ReadByte(), r.ReadString()));

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await x.H.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_HELPMESSAGE_REQ).WriteByte(3).WriteInt64(now - 10).WriteInt64(now + 3600)
            .WriteString("Double exp!").ToArray());
        r = new PacketReader(x.Cb.Last(Msg.CS_HELPMESSAGE_ACK)!);
        Assert.Equal((3, "Double exp!"), (r.ReadByte(), r.ReadString()));
    }

    [Fact]
    public async Task OverAMapsLevelCap_ThePlayerIsSentBack()
    {
        var x = await Open(t =>
        {
            var portal = new PortalRow(Portal, 3, 0, Home, 0);
            portal.Destinations[Dest] = new PortalDestination(Dest, 0, 1, new[] { new PortalCondition(6, 15), default, default });   // PCT_UPLEVEL 15
            t.Portals[Portal] = portal;
        });
        x.B.MapId = 5;
        x.B.Level = 20;                                                                        // over the cap of 15
        x.B.Persist.LastDestination = Portal | ((uint)Dest << 16);
        x.B.Persist.LastSpawnId = Home;
        await Client(x.Sb, x.H, new PacketWriter(Msg.CS_KICKOUTMAP_REQ));
        Assert.True(x.H.World.Has(Msg.MW_BEGINTELEPORT_ACK) || x.Cb.Has(Msg.CS_TELEPORT_ACK));

        x.H.World.Clear(); x.Cb.Clear();
        x.B.Level = 10;                                                                        // under the cap: stays
        await Client(x.Sb, x.H, new PacketWriter(Msg.CS_KICKOUTMAP_REQ));
        Assert.False(x.H.World.Has(Msg.MW_BEGINTELEPORT_ACK) || x.Cb.Has(Msg.CS_TELEPORT_ACK));
    }

    [Fact]
    public async Task TheControlLink_Announces_Kicks_AndAnswers()
    {
        var x = await Open();
        var ctrl = new FakeClientChannel();
        var cs = new ClientSession(ctrl) { IsControl = true };
        x.H.Service.DispatchControl(cs, new PacketReader(new PacketWriter(Msg.CT_ANNOUNCEMENT_ACK).WriteString("Maintenance soon").ToArray()));
        Assert.Equal((byte)ChatGroup.World, new PacketReader(x.Cb.Last(Msg.CS_CHAT_ACK)!).ReadByte());

        x.H.Service.DispatchControl(cs, new PacketReader(new PacketWriter(Msg.CT_SERVICEMONITOR_ACK).WriteUInt32(77).ToArray()));
        var m = new PacketReader(ctrl.Last(Msg.CT_SERVICEMONITOR_REQ)!);
        Assert.Equal((77u, 2u, 2u, 2u), (m.ReadUInt32(), m.ReadUInt32(), m.ReadUInt32(), m.ReadUInt32()));

        x.H.Service.DispatchControl(cs, new PacketReader(new PacketWriter(Msg.CT_CASTLEINFO_REQ).WriteUInt32(5).ToArray()));
        Assert.Equal(5u, new PacketReader(ctrl.Last(Msg.CT_CASTLEINFO_ACK)!).ReadUInt32());

        x.H.Service.DispatchControl(cs, new PacketReader(new PacketWriter(Msg.CT_USERKICKOUT_ACK).WriteString("Bob").ToArray()));
        Assert.True(x.Cb.Closed);
    }

    [Fact]
    public async Task AClient_CannotSendControlMessages()
    {
        var x = await Open();
        await Client(x.Sb, x.H, new PacketWriter(Msg.CT_ANNOUNCEMENT_ACK).WriteString("I am the GM"));
        Assert.False(x.Cg.Has(Msg.CS_CHAT_ACK));
        await Client(x.Sb, x.H, new PacketWriter(Msg.CT_USERKICKOUT_ACK).WriteString("Gina"));
        Assert.False(x.Cg.Closed);
    }
}
