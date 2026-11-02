using TWorld.Protocol;
using TWorld.Server.World;
using Xunit;

namespace TWorld.Server.Tests;

/// <summary>Social, batch S1: the world's friend and messenger handlers, per the C++ (SSHandler.cpp OnMW_PROTECTEDCHECK_ACK,
/// OnMW_TMSINVITEASK_ACK; TWorldSvr.cpp LeaveTMS).</summary>
public class SocialAuditTests
{
    private const uint Ann = 5001, Bob = 5002;
    private static uint Key(uint id) => 0xBEE00000 + id;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static async Task<(WorldTestHost Host, TcpTestClient Client)> Open()
    {
        var host = new WorldTestHost();
        var client = await host.ConnectAsync();
        GuildTests.Connect(client);
        foreach (var (id, name) in new[] { (Ann, "Ann"), (Bob, "Bob") })
        {
            GuildTests.EnterChar(client, id, Key(id), level: 20, country: 1);
            GuildTests.ConfirmEnter(client, id, Key(id));
        }
        await Task.Delay(100);
        foreach (var (id, name) in new[] { (Ann, "Ann"), (Bob, "Bob") })
        {
            var ch = host.State.Characters[id];
            ch.Name = name;
            host.State.CharactersByName[name] = ch;
        }
        return (host, client);
    }

    private static PacketReader Next(TcpTestClient c, ushort id)
    {
        for (int i = 0; i < 20; i++)
        {
            var p = c.Receive(Wait);
            if (p.Id == id) return p;
        }
        throw new InvalidOperationException($"no 0x{id:X}");
    }

    private static Tms Conversation(WorldTestHost host, string lastMember, params uint[] members)
    {
        var tms = new Tms { Id = 0x700, LastMember = lastMember };
        foreach (uint m in members) { tms.Members[m] = host.State.Characters[m]; host.State.Characters[m].TmsIds.Add(tms.Id); }
        host.State.TmsMap[tms.Id] = tms;
        return tms;
    }

    [Fact]
    public async Task BlockingAFriend_BreaksTheFriendship()
    {
        var (host, client) = await Open();
        await using var _ = host;
        using var __ = client;
        host.State.Characters[Ann].Friends[Bob] = new Friend { Id = Bob, Name = "Bob", Type = FriendType.Friend, Connected = true };
        host.State.Characters[Bob].Friends[Ann] = new Friend { Id = Ann, Name = "Ann", Type = FriendType.Target, Connected = true };

        var w = new PacketWriter(Msg.MW_PROTECTEDCHECK_ACK);
        w.WriteUInt32(Ann); w.WriteUInt32(Key(Ann)); w.WriteByte((byte)FriendConnState.Disconnection); w.WriteString("Bob");
        client.Send(w);

        var erase = Next(client, Msg.MW_FRIENDERASE_REQ);
        Assert.Equal((Ann, Key(Ann), (byte)FriendResult.Success, Bob), (erase.ReadUInt32(), erase.ReadUInt32(), erase.ReadByte(), erase.ReadUInt32()));
        Assert.False(host.State.Characters[Ann].Friends.ContainsKey(Bob));
    }

    [Fact]
    public async Task ALeaverBroughtBack_EveryMemberSeesTheNewRoster()
    {
        var (host, client) = await Open();
        await using var _ = host;
        using var __ = client;
        var tms = Conversation(host, "Bob", Ann);

        // Complete the native request/answer sequence; an unsolicited ACK is no longer accepted.
        client.Send(new PacketWriter(Msg.MW_TMSSEND_ACK).WriteUInt32(Ann).WriteUInt32(Key(Ann))
            .WriteUInt32(tms.Id).WriteString("back?"));
        Assert.Equal(Msg.MW_TMSINVITEASK_REQ, Next(client, Msg.MW_TMSINVITEASK_REQ).Id);

        var w = new PacketWriter(Msg.MW_TMSINVITEASK_ACK);
        w.WriteUInt32(Ann); w.WriteUInt32(Key(Ann)); w.WriteUInt32(Bob); w.WriteUInt32(Key(Bob));
        w.WriteByte(1); w.WriteUInt32(tms.Id); w.WriteString("back?");
        client.Send(w);

        var seen = new List<(uint To, uint Key, uint Inviter)>();
        for (int i = 0; i < 2; i++)
        {
            var inv = Next(client, Msg.MW_TMSINVITE_REQ);
            seen.Add((inv.ReadUInt32(), inv.ReadUInt32(), inv.ReadUInt32()));
            Assert.Equal(tms.Id, inv.ReadUInt32());
            Assert.Equal(2, inv.ReadByte());
        }
        Assert.Equal(new[] { (Ann, Key(Ann), Ann), (Bob, Key(Bob), Ann) }, seen.OrderBy(x => x.To));
    }

    [Fact]
    public async Task LoggingOut_TellsTheOthersInEachConversation()
    {
        var (host, client) = await Open();
        await using var _ = host;
        using var __ = client;
        var tms = Conversation(host, "", Ann, Bob);

        var w = new PacketWriter(Msg.MW_CLOSECHAR_ACK);
        w.WriteUInt32(Ann); w.WriteUInt32(Key(Ann));
        client.Send(w);

        var outp = Next(client, Msg.MW_TMSOUT_REQ);
        Assert.Equal((Bob, Key(Bob), tms.Id, "Ann"), (outp.ReadUInt32(), outp.ReadUInt32(), outp.ReadUInt32(), outp.ReadString()));
        Assert.Equal("Ann", tms.LastMember);
        Assert.DoesNotContain(Ann, tms.Members.Keys);
    }
}
