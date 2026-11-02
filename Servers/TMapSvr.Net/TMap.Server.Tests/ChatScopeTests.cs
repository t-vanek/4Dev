using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class ChatScopeTests
{
    private static async Task<(MapTestHarness H, ClientSession A, FakeClientChannel Ca, ClientSession B, FakeClientChannel Cb)> Setup(bool op = false, ushort map = 0, float x = 100)
    {
        var t = new TemplateStore(); if (op) t.Operators.Add(1);
        var h = new MapTestHarness(t); await h.Service.OnWorldConnectedAsync();
        var (a, ca) = await h.EnterAsync(1, 1, 11, "A", mapId: map, x: x, z: 100);
        var (b, cb) = await h.EnterAsync(2, 2, 22, "B", mapId: map, x: x + 1, z: 100);
        a.Char!.Country = b.Char!.Country = 0; a.Char.AidCountry = b.Char.AidCountry = 3;
        ca.Clear(); cb.Clear(); h.World.Clear(); return (h, a, ca, b, cb);
    }

    [Theory]
    [InlineData(ChatGroup.Party, 42u)] [InlineData(ChatGroup.Guild, 50u)] [InlineData(ChatGroup.Tactics, 60u)]
    public async Task GroupTargetComesFromServerMembership(ChatGroup group, uint expected)
    {
        var x = await Setup(); x.A.Char!.PartyId = 42; x.A.Char.GuildId = 50; x.A.Char.TacticsId = 60;
        await x.H.Service.DispatchClientAsync(x.A, MapTestHarness.ChatReq("A", group, 999, "", "hello"));
        var r = new PacketReader(x.H.World.Last(Msg.MW_CHAT_ACK)!);
        r.ReadByte(); r.ReadUInt32(); r.ReadUInt32(); r.ReadString(); r.ReadByte(); r.ReadByte();
        Assert.Equal(expected, r.ReadUInt32());
    }

    [Theory]
    [InlineData(0u, "b")] [InlineData(2u, "wrong")]
    public async Task LocalWhisperResolvesByIdOrCaseInsensitiveNameAndEchoesRecipient(uint id, string name)
    {
        var x = await Setup(op: true);
        await x.H.Service.DispatchClientAsync(x.A, MapTestHarness.ChatReq("A", ChatGroup.Whisper, id, name, "hello"));
        var to = new PacketReader(x.Cb.Last(Msg.CS_CHAT_ACK)!); Assert.Equal((byte)ChatGroup.Operator, to.ReadByte());
        Assert.Equal(1u, to.ReadUInt32()); Assert.Equal("A", to.ReadString());
        var echo = new PacketReader(x.Ca.Last(Msg.CS_CHAT_ACK)!); Assert.Equal((byte)ChatGroup.Whisper, echo.ReadByte());
        Assert.Equal(1u, echo.ReadUInt32()); Assert.Equal("B", echo.ReadString());
        Assert.False(x.H.World.Has(Msg.MW_CHAT_ACK));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task NearRespectsFactionAidMeetingAndBroaExceptions(int scenario)
    {
        var x = await Setup(map: scenario == 3 ? (ushort)1100 : (ushort)0,
            x: scenario == 4 ? 3072 : scenario == 5 ? 4096 : 100); x.B.Char!.Country = 1;
        if (scenario == 1) x.A.Char!.AidCountry = 1;
        if (scenario == 2) x.B.Char.Country = 4;
        if (scenario >= 4)
        {
            x.A.Char!.Country = 2;
        }
        await x.H.Service.DispatchClientAsync(x.A, MapTestHarness.ChatReq("A", ChatGroup.Near, 0, "", "hello"));
        Assert.Equal(scenario is 1 or 2 or 3 or 4, x.Cb.Has(Msg.CS_CHAT_ACK));
        Assert.True(x.Ca.Has(Msg.CS_CHAT_ACK));
    }

    [Fact]
    public async Task LocalWhisperFallsBackToNameWhenIdCannotTalk()
    {
        var x = await Setup();
        var (c, _) = await x.H.EnterAsync(3, 3, 33, "C"); c.Char!.Country = 1; c.Char.AidCountry = 3;
        await x.H.Service.DispatchClientAsync(x.A, MapTestHarness.ChatReq("A", ChatGroup.Whisper, 3, "B", "hello"));
        Assert.True(x.Cb.Has(Msg.CS_CHAT_ACK));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WhisperCannotCrossFactionsButHonorsAidCountry(bool aid)
    {
        var x = await Setup(); x.B.Char!.Country = 1; if (aid) x.A.Char!.AidCountry = 1;
        await x.H.Service.DispatchClientAsync(x.A, MapTestHarness.ChatReq("A", ChatGroup.Whisper, 2, "B", "hello"));
        Assert.Equal(aid, x.Cb.Has(Msg.CS_CHAT_ACK));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task InvalidShowUnknownGroupAndSecondaryOriginAreRejected(int scenario)
    {
        var x = await Setup(); if (scenario == 2) x.A.IsMain = false;
        await x.H.Service.DispatchClientAsync(x.A, MapTestHarness.ChatReq("A",
            scenario == 1 ? (ChatGroup)255 : ChatGroup.Show, 999, "", "hello"));
        Assert.False(x.H.World.Has(Msg.MW_CHAT_ACK)); Assert.False(x.Cb.Has(Msg.CS_CHAT_ACK));
    }
}
