using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class MonsterRewardRelayTests
{
    private static TemplateStore Templates()
    {
        var t=new TemplateStore();t.LevelExp[5]=1000000;
        t.Classes[0]=new(1,1,1,1,1,1);t.Races[0]=new(1,1,1,1,1,1);
        t.Formulas[8]=new(200,0,0);t.Formulas[19]=new(100,0,0);return t;
    }
    private static void Hunt(Character ch,ushort kind=500)
    {
        var q=new QuestTemplate { QuestId=100,Type=(byte)QuestType.Mission };
        q.Terms.Add(new QuestTerm(kind,3,5));ch.Quests[100]=new QuestProgress { Template=q,TriggerCount=1 };
    }
    private static int Count(Character ch)=>ch.Quests[100].RunningTerms.FirstOrDefault()?.Count??0;
    private static void ExpBuff(Character ch)
    {
        var t=new SkillTemplate(900,0,0,0,0,0,1,1,0,0,0,0,0,0,1,0xFFFF);
        t.Data.Add(new SkillDataRow(3,6,0,69,0,100,0,0));ch.MaintainSkills.Add(new MaintainSkill { SkillId=900,Template=t });
    }
    private static byte[] Reward(uint exp=322,uint id=2,uint key=22,ushort term=500,byte count=1)
        =>new PacketWriter(Msg.MW_MONSTERDIE_REQ).WriteUInt32(id).WriteUInt32(key).WriteUInt32(exp).WriteUInt32(0x70001)
            .WriteFloat(100).WriteFloat(7).WriteFloat(102).WriteUInt16(term).WriteByte(3).WriteByte(5).WriteByte(count).ToArray();
    private static async Task<(MapTestHarness H,TemplateStore T,ClientSession S,FakeClientChannel C)> Receiver()
    {
        var t=Templates();var h=new MapTestHarness(t);
        var ch=new Character { CharId=2,Name="B",Hp=100,MaxHp=200,Mp=50,MaxMp=100,Level=5 };
        var (s,c)=await h.EnterAsync(2,2,22,preSeeded:ch);ch.Level=5;Hunt(ch);c.Clear();return(h,t,s,c);
    }

    [Theory]
    [InlineData(null,false)] [InlineData((ushort)600,false)] [InlineData((ushort)0,false)] [InlineData(null,true)]
    public async Task SecondaryPartyShareIsRelayedAndNeverAwardedToTheShadow(ushort? huntKind,bool disconnected)
    {
        var templates=Templates();
        if(huntKind is {} kind)templates.MonsterTemplates[500]=new MonsterTemplate(500,5,0,HuntKind:kind);
        var h=new MapTestHarness(templates);await h.Service.OnWorldConnectedAsync();
        var (a,ca)=await h.EnterAsync(1,1,11,"A",x:100,z:100);
        var (b,cb)=await h.EnterAsync(2,2,22,"B",x:100,z:100);
        foreach(var pc in new[]{a.Char!,b.Char!}) {pc.Level=5;pc.PartyId=42;Hunt(pc,huntKind??500);}
        b.IsMain=false;ExpBuff(b.Char!);ca.Clear();cb.Clear();h.World.Clear();
        if(disconnected)h.Service.OnWorldDisconnect();
        var mon=new Monster { Id=0x70001,ChartId=500,Level=5,Hp=10,MaxHp=10,Exp=1000,Country=3,AidCountry=3,
            Channel=1,MapId=0,PosX=100,PosY=7,PosZ=100 };
        h.Service.SpawnMonster(mon);h.Service.CombatRng=new Random(1);
        for(int i=0;i<10&&mon.Hp>0;i++)await h.CastAndHitAsync(a,MapTestHarness.DefendReq(1,mon.Id));
        Assert.Equal(0u,mon.Hp);Assert.Equal(322u,a.Char!.Exp);Assert.Equal(0u,b.Char!.Exp);
        Assert.Equal(1,Count(a.Char));Assert.Equal(0,Count(b.Char));Assert.False(cb.Has(Msg.CS_EXP_ACK));
        if(disconnected) {Assert.False(h.World.Has(Msg.MW_MONSTERDIE_ACK));return;}
        byte[] packet=h.World.Last(Msg.MW_MONSTERDIE_ACK)!;var r=new PacketReader(packet);
        Assert.Equal((2u,22u,322u,mon.Id),(r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32()));
        Assert.Equal((100f,7f,100f),(r.ReadFloat(),r.ReadFloat(),r.ReadFloat()));
        Assert.Equal((huntKind??500,(byte)3,(byte)5,(byte)1),(r.ReadUInt16(),r.ReadByte(),r.ReadByte(),r.ReadByte()));Assert.Equal(0,r.Remaining);
        var to=await Receiver();to.S.Char!.Quests[100].Template.Terms[0]=new QuestTerm(huntKind??500,3,5);
        ExpBuff(to.S.Char);var delivered=(byte[])packet.Clone();PacketHeader.WriteId(delivered,Msg.MW_MONSTERDIE_REQ);
        await to.H.Service.DispatchWorldAsync(delivered);
        Assert.Equal(322u,to.S.Char!.Exp);Assert.Equal(1,Count(to.S.Char));
    }

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(322u)]
    public async Task MainReceivesTheAlreadyScaledExpAndHuntEvent(uint exp)
    {
        var x=await Receiver();await x.H.Service.DispatchWorldAsync(Reward(exp));
        Assert.Equal(exp,x.S.Char!.Exp);Assert.Equal(1,Count(x.S.Char));
        Assert.Equal(exp!=0,x.C.Has(Msg.CS_EXP_ACK));Assert.True(x.C.Has(Msg.CS_QUESTUPDATE_ACK));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task InvalidRoutesCannotChangeExperienceOrQuest(int scenario)
    {
        var x=await Receiver();uint id=2,key=22;
        if(scenario==0)key++;if(scenario==1)id=999;if(scenario==2)x.S.IsMain=false;
        if(scenario==3)x.S.State=EnterState.Entering;if(scenario==4)x.S.State=EnterState.Granted;
        await x.H.Service.DispatchWorldAsync(Reward(id:id,key:key));
        Assert.Equal(0u,x.S.Char!.Exp);Assert.Equal(0,Count(x.S.Char));Assert.False(x.C.Has(Msg.CS_EXP_ACK));
    }

    [Fact]
    public async Task GameClientCannotInjectWorldRewards()
    {
        var x=await Receiver();await x.H.Service.DispatchClientAsync(x.S,Reward());
        Assert.Equal(0u,x.S.Char!.Exp);Assert.Equal(0,Count(x.S.Char));
    }

    [Fact]
    public async Task ReceiverPreservesLevelUpHealingAndDoesNotApplyBonusAgain()
    {
        var x=await Receiver();x.T.LevelExp[5]=100;x.S.Char!.Hp=1;x.S.Char.Mp=1;ExpBuff(x.S.Char);
        await x.H.Service.DispatchWorldAsync(Reward(120));
        Assert.Equal(120u,x.S.Char.Exp);Assert.Equal(6,x.S.Char.Level);
        Assert.Equal((200u,100u),(x.S.Char.Hp,x.S.Char.Mp));Assert.True(x.C.Has(Msg.CS_LEVEL_ACK));
    }

    [Fact]
    public async Task ExistingCapAndOverflowProtectionApplyToRoutedExp()
    {
        var x=await Receiver();x.T.LevelExp[139]=1000;x.S.Char!.Exp=900;
        await x.H.Service.DispatchWorldAsync(Reward(uint.MaxValue));Assert.Equal(1000u,x.S.Char.Exp);
        x.S.Char.Level=140;x.C.Clear();await x.H.Service.DispatchWorldAsync(Reward(100));
        Assert.Equal(1000u,x.S.Char.Exp);Assert.False(x.C.Has(Msg.CS_EXP_ACK));
    }

    [Fact]
    public async Task DeadRecipientCannotGainExperienceOrRevive()
    {
        var x=await Receiver();x.S.Char!.Hp=0;await x.H.Service.DispatchWorldAsync(Reward());
        Assert.Equal(0u,x.S.Char.Exp);Assert.Equal(0u,x.S.Char.Hp);Assert.False(x.C.Has(Msg.CS_EXP_ACK));
    }

    [Fact]
    public async Task QuestUsesRoutedTermAndCountInsteadOfAssumingOneLocalKill()
    {
        var x=await Receiver();await x.H.Service.DispatchWorldAsync(Reward(0,term:501,count:3));Assert.Equal(0,Count(x.S.Char!));
        await x.H.Service.DispatchWorldAsync(Reward(0,count:3));Assert.Equal(3,Count(x.S.Char!));
        await x.H.Service.DispatchWorldAsync(Reward(0,count:3));Assert.Equal(5,Count(x.S.Char!));
    }
}
