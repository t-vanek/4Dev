using TWorld.Protocol;
using TWorld.Server.World;
using Xunit;

namespace TWorld.Server.Tests;

public class TmsRecoveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private sealed class Fixture : IAsyncDisposable
    {
        public WorldTestHost H = new(); public List<TcpTestClient> Maps = new();
        public Character A = new() { CharId = 1, Key = 11, Name = "A", MainId = 1 };
        public Character B = new() { CharId = 2, Key = 22, Name = "B", MainId = 2 };
        public Tms T = new() { Id = 10, LastMember = "B" };
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            for (byte id = 1; id <= 2; id++)
            {
                var map = await f.H.ConnectAsync(); f.Maps.Add(map);
                map.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16((ushort)(0x400 | id)).WriteByte(1).WriteByte(1));
            }
            for (int i = 0; i < 200 && f.H.State.Servers.Count < 2; i++) await Task.Delay(5);
            Assert.Equal(2, f.H.State.Servers.Count);
            foreach (var c in new[] { f.A, f.B }) { f.H.State.Characters[c.CharId] = c; f.H.State.CharactersByName[c.Name] = c; }
            f.T.Members[1] = f.A; f.A.TmsIds.Add(10); f.H.State.TmsMap[10] = f.T;
            f.H.State.ServerMessages[(uint)SvrMsg.TmsNoReceiver] = "Nobody available";
            return f;
        }
        public void Send(string text = "hello") => Maps[0].Send(new PacketWriter(Msg.MW_TMSSEND_ACK).WriteUInt32(1).WriteUInt32(11).WriteUInt32(10).WriteString(text));
        public PacketWriter Answer(byte result = 1, string text = "hello", uint key = 22)
            => new PacketWriter(Msg.MW_TMSINVITEASK_ACK).WriteUInt32(1).WriteUInt32(11).WriteUInt32(2).WriteUInt32(key).WriteByte(result).WriteUInt32(10).WriteString(text);
        public void Ask()
        {
            var r = Maps[1].Receive(Timeout); Assert.Equal(Msg.MW_TMSINVITEASK_REQ, r.Id);
            Assert.Equal((2u,22u,1u,11u,10u), (r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32()));
            Assert.Equal("hello",r.ReadString());
        }
        public void Barrier(int source = 1)
        {
            var c = source == 0 ? A : B;
            Maps[source].Send(new PacketWriter(Msg.MW_CHAT_ACK).WriteByte(1).WriteUInt32(c.CharId).WriteUInt32(c.Key)
                .WriteString(c.Name).WriteByte(2).WriteByte(2).WriteUInt32(0).WriteString("").WriteString("barrier"));
            foreach (var m in Maps) Assert.Equal(Msg.MW_CHAT_REQ, m.Receive(Timeout).Id);
        }
        public async ValueTask DisposeAsync() { foreach (var m in Maps) m.Dispose(); await H.DisposeAsync(); }
    }

    private static void Received(TcpTestClient map, uint id, string message)
    {
        var r = map.Receive(Timeout); Assert.Equal(Msg.MW_TMSRECV_REQ,r.Id);
        Assert.Equal(id,r.ReadUInt32()); Assert.Equal(id*11,r.ReadUInt32()); Assert.Equal(10u,r.ReadUInt32());
        Assert.Equal("A",r.ReadString()); Assert.Equal(message,r.ReadString());
    }

    [Fact]
    public async Task ReopenAnnouncesCorrectRosterToBothMains_ThenDeliversMessageOnce()
    {
        await using var f = await Fixture.Create(); f.Send(); f.Ask(); f.Maps[1].Send(f.Answer());
        for (int i=0;i<2;i++)
        {
            var r=f.Maps[i].Receive(Timeout); Assert.Equal(Msg.MW_TMSINVITE_REQ,r.Id);
            Assert.Equal((uint)i+1,r.ReadUInt32()); Assert.Equal(((uint)i+1)*11,r.ReadUInt32());
            Assert.Equal(1u,r.ReadUInt32()); Assert.Equal(10u,r.ReadUInt32()); Assert.Equal(2,r.ReadByte());
            Received(f.Maps[i],(uint)i+1,"hello");
        }
        f.Maps[1].Send(f.Answer()); f.Barrier(); Assert.Equal(2,f.T.Members.Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task UnsolicitedWrongMapStaleKeyAndChangedMessageAnswersCannotJoin(int scenario)
    {
        await using var f = await Fixture.Create();
        if(scenario!=0) { f.Send(); f.Ask(); }
        f.Maps[scenario==1?0:1].Send(f.Answer(text:scenario==3?"forged":"hello",key:scenario==2?23u:22u));
        f.Barrier(scenario==1?0:1); Assert.Single(f.T.Members); Assert.Empty(f.B.TmsIds);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RefusalOrChangedFactionReturnsNativeNoReceiverNotice(bool changedCountry)
    {
        await using var f=await Fixture.Create(); f.Send(); f.Ask();
        if(changedCountry) f.B.Country=1;
        f.Maps[1].Send(f.Answer(result:changedCountry?(byte)1:(byte)0));
        Received(f.Maps[0],1,"00000010Nobody available"); f.Barrier(); Assert.Single(f.T.Members);
    }

    [Fact]
    public async Task PeerWithoutMainServerReturnsNoReceiverInsteadOfLosingMessage()
    {
        await using var f=await Fixture.Create(); f.B.MainId=99; f.Send();
        Received(f.Maps[0],1,"00000010Nobody available"); f.Barrier(0);
    }

    [Fact]
    public async Task RepeatedIdenticalPendingMessagesAreEachDeliveredOnce()
    {
        await using var f=await Fixture.Create(); f.Send(); f.Send(); f.Ask(); f.Ask();
        for(int n=0;n<2;n++)
        {
            f.Maps[1].Send(f.Answer());
            for(int i=0;i<2;i++) { Assert.Equal(Msg.MW_TMSINVITE_REQ,f.Maps[i].Receive(Timeout).Id); Received(f.Maps[i],(uint)i+1,"hello"); }
        }
        f.Maps[1].Send(f.Answer()); f.Barrier();
    }

    [Fact]
    public async Task DuplicateTargetsStillReopenTheExistingOneToOneConversation()
    {
        await using var f=await Fixture.Create(); f.T.Members.Clear(); f.T.Members[2]=f.B; f.T.LastMember="A";
        f.A.TmsIds.Clear(); f.B.TmsIds.Add(10);
        f.Maps[0].Send(new PacketWriter(Msg.MW_TMSINVITE_ACK).WriteUInt32(1).WriteUInt32(11).WriteUInt32(0)
            .WriteByte(2).WriteUInt32(2).WriteUInt32(2));
        var r=f.Maps[0].Receive(Timeout); Assert.Equal(Msg.MW_TMSINVITE_REQ,r.Id);
        r.ReadUInt32();r.ReadUInt32();r.ReadUInt32();Assert.Equal(10u,r.ReadUInt32());
        f.Barrier(0);Assert.Single(f.H.State.TmsMap); Assert.Equal(2,f.T.Members.Count);
    }

    [Fact]
    public async Task LeavingConversationInvalidatesOutstandingReopenAnswer()
    {
        await using var f=await Fixture.Create(); f.Send(); f.Ask();
        f.Maps[0].Send(new PacketWriter(Msg.MW_TMSOUT_ACK).WriteUInt32(1).WriteUInt32(11).WriteUInt32(10));
        Assert.Equal(Msg.MW_TMSOUT_REQ,f.Maps[0].Receive(Timeout).Id);
        f.Maps[1].Send(f.Answer()); f.Barrier(); Assert.Empty(f.H.State.TmsMap); Assert.Empty(f.B.TmsIds);
    }

    [Fact]
    public async Task PromptedPeerDisconnectNotifiesTheWaitingSender()
    {
        await using var f=await Fixture.Create();
        f.B.Connections[2]=new CharConnection { ServerId=2,Ready=true };
        f.Send();f.Ask();f.Maps[1].Dispose();
        Received(f.Maps[0],1,"00000010Nobody available"); Assert.Single(f.T.Members);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OversizeNewOrExistingRosterIsRefusedAtomically(bool existing)
    {
        await using var f=await Fixture.Create(); var ids=new List<uint>();
        for(uint id=3;id<=257;id++)
        {
            var c=new Character { CharId=id,Key=id,Name="P"+id,MainId=2 };
            f.H.State.Characters[id]=c; ids.Add(id);
            if(existing && f.T.Members.Count<255) { f.T.Members[id]=c;c.TmsIds.Add(10); }
        }
        if(!existing) { f.H.State.TmsMap.Clear(); f.A.TmsIds.Clear(); }
        var w=new PacketWriter(Msg.MW_TMSINVITE_ACK).WriteUInt32(1).WriteUInt32(11).WriteUInt32(existing?10u:0u);
        if(existing) w.WriteByte(1).WriteUInt32(2);
        else { w.WriteByte(255);foreach(uint id in ids)w.WriteUInt32(id); }
        f.Maps[0].Send(w);f.Barrier(0);
        Assert.Equal(existing?1:0,f.H.State.TmsMap.Count); Assert.Empty(f.B.TmsIds);
        if(existing)Assert.Equal(255,f.T.Members.Count);else Assert.Empty(f.A.TmsIds);
    }

    [Fact]
    public async Task ExactlyRepresentableRosterIsStillAllowed()
    {
        await using var f=await Fixture.Create();
        for(uint id=3;id<=255;id++)
        {
            var c=new Character { CharId=id,Key=id,Name="P"+id,MainId=99 };
            f.H.State.Characters[id]=c;f.T.Members[id]=c;c.TmsIds.Add(10);
        }
        f.Maps[0].Send(new PacketWriter(Msg.MW_TMSINVITE_ACK).WriteUInt32(1).WriteUInt32(11).WriteUInt32(10).WriteByte(1).WriteUInt32(2));
        foreach(var map in f.Maps)
        {
            var r=map.Receive(Timeout);Assert.Equal(Msg.MW_TMSINVITE_REQ,r.Id);
            r.ReadUInt32();r.ReadUInt32();r.ReadUInt32();r.ReadUInt32();Assert.Equal(255,r.ReadByte());
        }
        f.Barrier(0);Assert.Equal(255,f.T.Members.Count);
    }
}
