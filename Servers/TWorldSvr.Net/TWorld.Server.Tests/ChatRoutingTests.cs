using TWorld.Protocol;
using TWorld.Server.World;
using Xunit;

namespace TWorld.Server.Tests;

public class ChatRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private sealed class Fixture : IAsyncDisposable
    {
        public WorldTestHost H = new();
        public List<TcpTestClient> Maps = new();
        public Character A = new() { CharId = 1, Key = 11, Name = "A", MainId = 1, Country = 0, Channel = 1 };
        public Character B = new() { CharId = 2, Key = 22, Name = "B", MainId = 2, Country = 0, Channel = 2 };
        public Character C = new() { CharId = 3, Key = 33, Name = "C", MainId = 3, Country = 0, Channel = 1 };
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            for (byte id = 1; id <= 3; id++)
            {
                var map = await f.H.ConnectAsync(); f.Maps.Add(map);
                map.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16((ushort)(0x400 | id)).WriteByte(1).WriteByte(1));
            }
            for (int i = 0; i < 200 && f.H.State.Servers.Count != 3; i++) await Task.Delay(5);
            Assert.Equal(3, f.H.State.Servers.Count);
            foreach (var c in new[] { f.A, f.B, f.C }) { f.H.State.Characters[c.CharId] = c; f.H.State.CharactersByName[c.Name] = c; }
            return f;
        }
        public PacketWriter Chat(byte group, uint target = 999, string name = "B", string text = "hello", uint key = 11, string sender = "A")
            => new PacketWriter(Msg.MW_CHAT_ACK).WriteByte(99).WriteUInt32(1).WriteUInt32(key).WriteString(sender)
                .WriteByte(7).WriteByte(group).WriteUInt32(target).WriteString(name).WriteString(text);
        public async ValueTask DisposeAsync() { foreach (var m in Maps) m.Dispose(); await H.DisposeAsync(); }
    }
    private static (uint Id, uint Key, uint Sender, string Name, byte Type, byte Group, uint Target, string Talk) Read(TcpTestClient c)
    {
        var r = c.Receive(Timeout); Assert.Equal(Msg.MW_CHAT_REQ, r.Id);
        uint id = r.ReadUInt32(), key = r.ReadUInt32(); Assert.Equal(1, r.ReadByte());
        uint sender = r.ReadUInt32(); string name = r.ReadString();
        Assert.Equal(0, r.ReadByte()); Assert.Equal(3, r.ReadByte());
        byte type = r.ReadByte(), group = r.ReadByte(); uint target = r.ReadUInt32(); string talk = r.ReadString();
        Assert.Equal(0, r.Remaining); return (id, key, sender, name, type, group, target, talk);
    }
    private static void Barrier(Fixture f)
    {
        f.Maps[0].Send(f.Chat(2, text: "barrier"));
        foreach (var map in f.Maps) Assert.Equal("barrier", Read(map).Talk);
    }

    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(8)]
    public async Task MembershipSelectsOnlyItsOwnMembersAndUsesTheirMainKeys(byte group)
    {
        await using var f = await Fixture.Create();
        var party = new Party { Id = 42 }; party.AddMember(f.A); party.AddMember(f.B);
        if (group == 6)
        {
            party.DelMember(2); var second = new Party { Id = 43 }; second.AddMember(f.B);
            var corps = new Corps { Id = 7 }; corps.Enter(party); corps.Enter(second); f.H.State.CorpsMap[7] = corps;
        }
        var guild = new Guild { Id = 50 }; f.A.Guild = guild;
        guild.Members[1] = new GuildMember { CharId = 1, OnlineChar = f.A };
        if (group == 8) guild.Tactics[2] = new TacticsMember { CharId = 2, OnlineChar = f.B };
        else guild.Members[2] = new GuildMember { CharId = 2, OnlineChar = f.B };
        f.H.State.Guilds[50] = guild;
        f.Maps[0].Send(f.Chat(group));
        var a = Read(f.Maps[0]); var b = Read(f.Maps[1]);
        Assert.Equal((1u, 11u), (a.Id, a.Key)); Assert.Equal((2u, 22u), (b.Id, b.Key));
        Assert.Equal(group, a.Type); Assert.Equal(group, b.Group); Assert.Equal("A", b.Name);
        Assert.Equal(group == 4 ? 42u : group is 5 or 8 ? 50u : 999u, b.Target);
        Barrier(f); // outsider C's first packet must be the barrier
    }

    [Fact]
    public async Task TacticsIncludesGuildAndMercenariesWithoutDuplicates_OrdinaryGuildExcludesMercenaries()
    {
        await using var f = await Fixture.Create(); var g = new Guild { Id = 50 }; f.H.State.Guilds[50] = g;
        g.Members[2] = new GuildMember { CharId = 2, OnlineChar = f.B };
        g.Tactics[1] = new TacticsMember { CharId = 1, OnlineChar = f.A };
        g.Tactics[2] = new TacticsMember { CharId = 2, OnlineChar = f.B };
        f.H.State.CharTactics[1] = 50;
        f.Maps[0].Send(f.Chat(8)); Assert.Equal(1u, Read(f.Maps[0]).Id); Assert.Equal(2u, Read(f.Maps[1]).Id); Barrier(f);
        f.Maps[0].Send(f.Chat(5)); Barrier(f); // mercenary has no ordinary guild
    }

    [Theory]
    [InlineData(2u, "wrong")] [InlineData(0u, "b")]
    public async Task WhisperFindsByIdOrNameAndEchoesRecipientName(uint target, string name)
    {
        await using var f = await Fixture.Create(); f.Maps[0].Send(f.Chat(0, target, name));
        var to = Read(f.Maps[1]); var echo = Read(f.Maps[0]);
        Assert.Equal((2u, 22u, "A"), (to.Id, to.Key, to.Name));
        Assert.Equal((1u, 11u, "B"), (echo.Id, echo.Key, echo.Name)); Barrier(f);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task InvalidSenderOrUnavailableWhisperDoesNotLeak(int scenario)
    {
        await using var f = await Fixture.Create();
        if (scenario == 2) f.A.ChatBanTime = long.MaxValue;
        if (scenario == 3) f.B.Country = 1;
        if (scenario == 4) f.B.Logout = true;
        if (scenario == 5) f.A.MainId = 2;
        if (scenario == 6) f.H.State.ChatBans["A"] = long.MaxValue;
        f.H.State.Characters[4] = new Character { CharId = 4, Key = 44, Name = "D", MainId = 1, Country = 0 };
        f.Maps[0].Send(f.Chat(0, 2, key: scenario == 0 ? 12u : 11u, sender: scenario == 1 ? "spoof" : "A"));
        // Same TCP connection establishes an ordering barrier without changing the rejected sender's state.
        f.Maps[0].Send(new PacketWriter(Msg.MW_CHAT_ACK).WriteByte(1).WriteUInt32(4).WriteUInt32(44).WriteString("D")
            .WriteByte(2).WriteByte(2).WriteUInt32(0).WriteString("").WriteString("barrier"));
        foreach (var map in f.Maps)
        {
            var r = map.Receive(Timeout); Assert.Equal(Msg.MW_CHAT_REQ, r.Id);
            r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); Assert.Equal(4u, r.ReadUInt32());
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OperatorWhisperRoutesToConfiguredSameCountryOperatorsOrReturnsOfflineNotice(bool online)
    {
        await using var f = await Fixture.Create(); f.H.State.ServerMessages[(uint)SvrMsg.NameOperator] = "GM";
        f.H.State.ServerMessages[(uint)SvrMsg.CharLogout] = "offline";
        if (online) f.H.State.Operators.Add(2);
        f.Maps[0].Send(f.Chat(0, name: "GM"));
        if (online) Assert.Equal("A", Read(f.Maps[1]).Name);
        var echo = Read(f.Maps[0]); Assert.Equal("GM", echo.Name);
        Assert.Equal(online ? 1u : 0u, echo.Sender); Assert.Equal(online ? (byte)0 : (byte)7, echo.Type);
        Assert.Equal(online ? "hello" : "00000007offline", echo.Talk); Barrier(f);
    }

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(7)] [InlineData(255)]
    public async Task ClientOriginatedReservedOrUnknownGroupsAreNotBroadcast(byte group)
    {
        await using var f = await Fixture.Create(); f.Maps[0].Send(f.Chat(group)); Barrier(f);
    }

    [Fact]
    public async Task OperatorDisplayAndLengthCapAreServerDerived()
    {
        await using var f = await Fixture.Create(); f.H.State.Operators.Add(1);
        f.Maps[0].Send(f.Chat(2, text: new string('x', 1100)));
        foreach (var map in f.Maps) { var message = Read(map); Assert.Equal(7, message.Type); Assert.Equal(1024, message.Talk.Length); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RemoteWhisperHonorsAidCountryAndPeaceExceptions(bool peace)
    {
        await using var f = await Fixture.Create(); f.B.Country = 1;
        if (peace) f.B.Country = 4; else f.A.AidCountry = 1;
        f.Maps[0].Send(f.Chat(0, 2));
        Assert.Equal(2u, f.Maps[1].Receive(Timeout).ReadUInt32());
        Assert.Equal(1u, f.Maps[0].Receive(Timeout).ReadUInt32());
        f.A.AidCountry = 3; Barrier(f);
    }
}
