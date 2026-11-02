using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class WorldChatTests
{
    private static byte[] Delivery(ChatGroup group = ChatGroup.World, uint id = 1, uint key = 11,
        uint sender = 99, byte country = 3, byte aid = 3, ChatGroup type = ChatGroup.World)
        => new PacketWriter(Msg.MW_CHAT_REQ).WriteUInt32(id).WriteUInt32(key).WriteByte(7)
            .WriteUInt32(sender).WriteString("Sender").WriteByte(country).WriteByte(aid)
            .WriteByte((byte)type).WriteByte((byte)group).WriteUInt32(123).WriteString("World notice").ToArray();

    private static async Task<(MapTestHarness H, ClientSession A, FakeClientChannel Ca, ClientSession B, FakeClientChannel Cb)> Setup()
    {
        var h = new MapTestHarness();
        var (a, ca) = await h.EnterAsync(1, 1, 11, "A", channel: 1, mapId: 0);
        var (b, cb) = await h.EnterAsync(2, 2, 22, "B", channel: 2, mapId: 1);
        a.Char!.Country = 0; b.Char!.Country = 1;
        a.Char.AidCountry = b.Char.AidCountry = 3;
        ca.Clear(); cb.Clear(); return (h, a, ca, b, cb);
    }

    [Theory]
    [InlineData(ChatGroup.World)] [InlineData(ChatGroup.Map)] [InlineData(ChatGroup.Show)]
    public async Task NeutralBroadcastReachesEachMainAcrossLocalMapsAndChannels(ChatGroup group)
    {
        var x = await Setup();
        await x.H.Service.DispatchWorldAsync(Delivery(group));
        foreach (var c in new[] { x.Ca, x.Cb })
        {
            var r = new PacketReader(c.Last(Msg.CS_CHAT_ACK)!);
            Assert.Equal((byte)ChatGroup.World, r.ReadByte()); Assert.Equal(99u, r.ReadUInt32());
            Assert.Equal("Sender", r.ReadString()); Assert.Equal("World notice", r.ReadString());
            Assert.Equal(0, r.Remaining);
        }
    }

    [Theory]
    [InlineData(0, 3, true, false)] [InlineData(1, 3, false, true)]
    [InlineData(0, 1, false, true)] [InlineData(1, 0, true, false)]
    [InlineData(3, 1, true, true)] [InlineData(4, 3, true, true)]
    public async Task BroadcastUsesNativeCountryAndAidPriority(byte country, byte aid, bool a, bool b)
    {
        var x = await Setup();
        await x.H.Service.DispatchWorldAsync(Delivery(country: country, aid: aid));
        Assert.Equal(a, x.Ca.Has(Msg.CS_CHAT_ACK)); Assert.Equal(b, x.Cb.Has(Msg.CS_CHAT_ACK));
    }

    [Fact]
    public async Task OperatorAnnouncementNeedsNoCharacterRoute_AndUnrelatedBlockOptionsDoNotHideIt()
    {
        var x = await Setup();
        x.A.Char!.Protected[0] = new ProtectedEntry { Name = "Sender", Option = 2 };
        await x.H.Service.DispatchWorldAsync(Delivery(id: 0, key: 0, sender: 0));
        Assert.True(x.Ca.Has(Msg.CS_CHAT_ACK)); Assert.True(x.Cb.Has(Msg.CS_CHAT_ACK));
    }

    [Theory]
    [InlineData(ChatGroup.Party)] [InlineData(ChatGroup.Guild)] [InlineData(ChatGroup.Tactics)] [InlineData(ChatGroup.Force)]
    public async Task GroupDeliveryIsTargetedAndAllowsOpposingFactions(ChatGroup group)
    {
        var x = await Setup();
        await x.H.Service.DispatchWorldAsync(Delivery(group, country: 1, type: group));
        Assert.True(x.Ca.Has(Msg.CS_CHAT_ACK)); Assert.False(x.Cb.Has(Msg.CS_CHAT_ACK));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task TargetedDeliveryRejectsStaleOrInactiveRoutes(int scenario)
    {
        var x = await Setup(); uint id = 1, key = 11;
        if (scenario == 0) key++;
        if (scenario == 1) id = 999;
        if (scenario == 2) x.A.IsMain = false;
        if (scenario == 3) x.A.State = EnterState.Entering;
        if (scenario == 4) x.A.State = EnterState.Granted;
        await x.H.Service.DispatchWorldAsync(Delivery(ChatGroup.Party, id, key));
        Assert.False(x.Ca.Has(Msg.CS_CHAT_ACK)); Assert.False(x.Cb.Has(Msg.CS_CHAT_ACK));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WhisperUsesFactionGateButPeaceRecipientCanReceive(bool peace)
    {
        var x = await Setup(); if (peace) x.A.Char!.Country = 4;
        await x.H.Service.DispatchWorldAsync(Delivery(ChatGroup.Whisper, country: 1));
        Assert.Equal(peace, x.Ca.Has(Msg.CS_CHAT_ACK)); Assert.False(x.Cb.Has(Msg.CS_CHAT_ACK));
    }

    [Fact]
    public async Task SenderEchoUsesGroupWhileRecipientUsesDisplayType()
    {
        var x = await Setup();
        await x.H.Service.DispatchWorldAsync(Delivery(ChatGroup.Party, sender: 1, type: ChatGroup.Whisper));
        Assert.Equal((byte)ChatGroup.Party, new PacketReader(x.Ca.Last(Msg.CS_CHAT_ACK)!).ReadByte());
        x.Ca.Clear();
        await x.H.Service.DispatchWorldAsync(Delivery(ChatGroup.Party, sender: 99, type: ChatGroup.Whisper));
        Assert.Equal((byte)ChatGroup.Whisper, new PacketReader(x.Ca.Last(Msg.CS_CHAT_ACK)!).ReadByte());
    }

    [Theory]
    [InlineData(ChatGroup.World)] [InlineData(ChatGroup.Party)]
    public async Task ProtectedChatAppliesToWorldDelivery(ChatGroup group)
    {
        var x = await Setup(); x.A.Char!.Protected[99] = new ProtectedEntry { Name = "Sender", Option = 1 };
        await x.H.Service.DispatchWorldAsync(Delivery(group));
        Assert.False(x.Ca.Has(Msg.CS_CHAT_ACK));
    }

    [Theory]
    [InlineData(ChatGroup.Near)] [InlineData(ChatGroup.Operator)] [InlineData((ChatGroup)255)]
    public async Task UnsupportedRoutingGroupsAreIgnored(ChatGroup group)
    {
        var x = await Setup(); await x.H.Service.DispatchWorldAsync(Delivery(group));
        Assert.False(x.Ca.Has(Msg.CS_CHAT_ACK)); Assert.False(x.Cb.Has(Msg.CS_CHAT_ACK));
    }

    [Fact]
    public async Task BroadcastSkipsSecondaryAndLoadingRecipients_AndClientCannotInjectIt()
    {
        var x = await Setup(); x.A.IsMain = false; x.B.State = EnterState.Entering;
        await x.H.Service.DispatchWorldAsync(Delivery());
        Assert.False(x.Ca.Has(Msg.CS_CHAT_ACK)); Assert.False(x.Cb.Has(Msg.CS_CHAT_ACK));
        x.A.IsMain = true; x.B.State = EnterState.InGame;
        await x.H.Service.DispatchClientAsync(x.A, Delivery());
        Assert.False(x.Ca.Has(Msg.CS_CHAT_ACK)); Assert.False(x.Cb.Has(Msg.CS_CHAT_ACK));
    }
}
