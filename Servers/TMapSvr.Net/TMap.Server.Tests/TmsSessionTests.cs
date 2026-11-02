using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class TmsSessionTests
{
    [Theory]
    [InlineData(0,0)] [InlineData(0,1)] [InlineData(0,2)]
    [InlineData(1,0)] [InlineData(1,1)] [InlineData(1,2)]
    [InlineData(2,0)] [InlineData(2,1)] [InlineData(2,2)]
    public async Task InactiveOrSecondaryClientsCannotSendInviteOrLeave(int action,int state)
    {
        var h=new MapTestHarness();await h.Service.OnWorldConnectedAsync();var (s,_)=await h.EnterAsync(1,1,11);
        if(state==0)s.State=EnterState.Entering;if(state==1)s.State=EnterState.Granted;if(state==2)s.IsMain=false;
        h.World.Clear();
        var w=new PacketWriter(action==0?Msg.CS_TMSSEND_REQ:action==1?Msg.CS_TMSINVITE_REQ:Msg.CS_TMSOUT_REQ).WriteUInt32(10);
        if(action==0)w.WriteString("hello");if(action==1)w.WriteByte(1).WriteUInt32(2);
        await h.Service.DispatchClientAsync(s,w.ToArray());
        Assert.False(h.World.Has(Msg.MW_TMSSEND_ACK));Assert.False(h.World.Has(Msg.MW_TMSINVITE_ACK));Assert.False(h.World.Has(Msg.MW_TMSOUT_ACK));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ReopenProbeRefusesWrongKeyLoadingOrSecondaryRecipient(int scenario)
    {
        var h=new MapTestHarness();var (s,_)=await h.EnterAsync(2,2,22);
        if(scenario==1)s.State=EnterState.Entering;if(scenario==2)s.State=EnterState.Granted;if(scenario==3)s.IsMain=false;
        var w=new PacketWriter(Msg.MW_TMSINVITEASK_REQ).WriteUInt32(2).WriteUInt32(scenario==0?23u:22u)
            .WriteUInt32(1).WriteUInt32(11).WriteUInt32(10).WriteString("hello");
        h.World.Clear();await h.Service.DispatchWorldAsync(w.ToArray());
        var r=new PacketReader(h.World.Last(Msg.MW_TMSINVITEASK_ACK)!);
        Assert.Equal((1u,11u,2u,scenario==0?23u:22u),(r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32()));
        Assert.Equal(0,r.ReadByte());Assert.Equal(10u,r.ReadUInt32());Assert.Equal("hello",r.ReadString());
    }
}
