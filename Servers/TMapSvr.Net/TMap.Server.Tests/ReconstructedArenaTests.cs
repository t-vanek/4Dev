using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class ReconstructedArenaTests
{
    private sealed record Player(ClientSession S, FakeClientChannel C, Character Ch);
    private sealed record Fixture(MapTestHarness H, FakePostStore Post, Player[] A, Player[] B, uint Quest, uint Complete, byte Size);

    private static async Task<Fixture> Open(byte size, uint quest, uint complete)
    {
        var t=new TemplateStore();t.Arenas.Add(new(1,size,50_000,17000,17001,QuestMatch:true));
        t.SpawnPositions[17000]=new(17000,0,100,0,100,0);t.SpawnPositions[17001]=new(17001,0,130,0,100,0);
        var npc=new NpcDef(26156,24,3,0,0,0,0,0,0,105,0,105);npc.ItemIds.Add(1);t.Npcs[26156]=npc;
        var q=new QuestTemplate {QuestId=quest,Type=8,TriggerType=3,TriggerId=26424,CountMax=1};
        q.Terms.Add(new(size==3?1u:2u,28,1));q.Rewards.Add(new(size==3?100u:150u,10,1,0,0));
        var end=new QuestTemplate {QuestId=complete,Type=7,ParentId=quest,TriggerType=3,TriggerId=26427};end.Terms.Add(new(quest,1,0));
        t.Quests[quest]=q;t.Quests[complete]=end;
        var other=new QuestTemplate {QuestId=50000,Type=8,TriggerType=3,TriggerId=26424,CountMax=1};
        other.Terms.Add(new(size==3?2u:1u,28,1));t.Quests[50000]=other;
        var h=new MapTestHarness(t);h.Service.InitNpcs();h.Service.InitQuests();
        var post=new FakePostStore();h.Service.PostStore=post;
        h.Service.AddNpc(new(){Id=26424,Country=3,MapId=0});h.Service.AddNpc(new(){Id=26427,Country=3,MapId=0});
        async Task<Player[]> Party(ushort party,uint first)
        {
            var result=new List<Player>();
            for(uint i=0;i<size;i++)
            {
                uint id=first+i;var ch=new Character {CharId=id,Level=90,Hp=100,MaxHp=100,Gold=1};
                var(s,c)=await h.EnterAsync(id,id,id+10,name:"Player"+id,preSeeded:ch);
                ch.PartyId=party;ch.PartyChiefId=first;ch.Level=90;ch.Gold=1;
                await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(quest));
                await h.Service.DispatchClientAsync(s,MapTestHarness.QuestExecReq(50000));
                result.Add(new(s,c,ch));
            }
            return result.ToArray();
        }
        return new(h,post,await Party(1,1),await Party(2,101),quest,complete,size);
    }
    private static byte[] Join()=>new PacketWriter(Msg.CS_ARENA_REQ).WriteByte(0).WriteUInt16(26156).WriteUInt16(0).ToArray();
    private static async Task Ticks(Fixture f,int n){for(int i=0;i<n;i++)await f.H.Service.OnTimerAsync();}
    private static byte Count(Player p,uint id)=>p.Ch.Quests[id].RunningTerms.Single(x=>x.TermType==28).Count;
    private static async Task Round(Fixture f,Player[] losers)
    {
        await Ticks(f,60);foreach(var p in losers)p.Ch.Hp=0;await Ticks(f,240);
    }

    [Theory]
    [InlineData((byte)3,1082u,10552u)]
    [InlineData((byte)5,1084u,10553u)]
    [InlineData((byte)3,1203u,10596u)]
    [InlineData((byte)5,1208u,10597u)]
    public async Task TwoRoundWinsCreditOnlyWinnersAndTurnInPaysOnce(byte size,uint quest,uint complete)
    {
        var f=await Open(size,quest,complete);
        await f.H.Service.DispatchClientAsync(f.A[0].S,Join());await f.H.Service.DispatchClientAsync(f.B[0].S,Join());
        await Round(f,f.B);
        Assert.All(f.A.Concat(f.B),p=>Assert.Equal(0,Count(p,quest)));Assert.Empty(f.Post.Mails);
        Assert.All(f.B,p=>Assert.Equal(1,p.Ch.ArenaId));
        await Round(f,f.B);
        Assert.All(f.A,p=>Assert.Equal(1,Count(p,quest)));Assert.All(f.B,p=>Assert.Equal(0,Count(p,quest)));
        Assert.All(f.A.Concat(f.B),p=>Assert.Equal(0,Count(p,50000)));
        Assert.Single(f.Post.Mails);
        foreach(var p in f.A)
        {
            await f.H.Service.DispatchClientAsync(p.S,MapTestHarness.QuestExecReq(complete));
            Assert.Equal(size==3?100u:150u,p.Ch.PvpUseablePoint);
            await f.H.Service.DispatchClientAsync(p.S,MapTestHarness.QuestExecReq(complete));
            Assert.Equal(size==3?100u:150u,p.Ch.PvpUseablePoint);
        }
        await f.H.Service.DispatchClientAsync(f.B[0].S,MapTestHarness.QuestExecReq(complete));
        Assert.Equal((byte)QuestResult.Term,new PacketReader(f.B[0].C.Last(Msg.CS_QUESTCOMPLETE_ACK)!).ReadByte());
    }

    [Fact]
    public async Task PartialPartyCannotJoinAndAForfeitDoesNotProduceAVictory()
    {
        var f=await Open(3,1082,10552);f.A[2].Ch.PartyId=9;
        await f.H.Service.DispatchClientAsync(f.A[0].S,Join());Assert.Equal(0,f.A[0].Ch.ArenaId);
        f.A[2].Ch.PartyId=1;await f.H.Service.DispatchClientAsync(f.A[0].S,Join());
        await f.H.Service.DispatchClientAsync(f.B[0].S,Join());
        await f.H.Service.OnClientDisconnectAsync(f.B[0].S);
        Assert.All(f.A,p=>Assert.Equal(0,Count(p,1082)));
    }

    [Fact]
    public async Task OneRoundEachNeedsAThirdAndAbandoningClearsVictoryCredit()
    {
        var f=await Open(3,1082,10552);
        await f.H.Service.DispatchClientAsync(f.A[0].S,Join());await f.H.Service.DispatchClientAsync(f.B[0].S,Join());
        await Round(f,f.B);await Round(f,f.A);
        Assert.All(f.A.Concat(f.B),p=>Assert.Equal(0,Count(p,1082)));
        await Round(f,f.B);Assert.Equal(1,Count(f.A[0],1082));
        await f.H.Service.DispatchClientAsync(f.A[0].S,MapTestHarness.QuestDropReq(1082));
        await f.H.Service.DispatchClientAsync(f.A[0].S,MapTestHarness.QuestExecReq(1082));
        Assert.Equal(0,Count(f.A[0],1082));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueueRevalidatesPaymentAndEntersAllMembersOutsideViewCells(bool unfunded)
    {
        var f=await Open(3,1082,10552);
        var queued=new List<Player>();
        for(uint id=201;id<204;id++)
        {
            var ch=new Character{CharId=id,Level=90,Hp=100,MaxHp=100,Gold=1};
            var(s,c)=await f.H.EnterAsync(id,id,id+10,x:5000,z:5000,preSeeded:ch);ch.PartyId=3;ch.PartyChiefId=201;ch.Gold=1;
            queued.Add(new(s,c,ch));
        }
        await f.H.Service.DispatchClientAsync(f.A[0].S,Join());
        await f.H.Service.DispatchClientAsync(queued[0].S,new PacketWriter(Msg.CS_ARENA_REQ).WriteByte(1).WriteUInt16(26156).WriteUInt16(1).ToArray());
        Assert.All(f.A,p=>Assert.Equal(1,p.Ch.ArenaId));Assert.Empty(f.Post.Mails);
        await f.H.Service.DispatchClientAsync(f.B[0].S,Join());
        await f.H.Service.DispatchClientAsync(queued[0].S,Join());if(unfunded)queued[0].Ch.Gold=0;
        await Round(f,f.B);await Round(f,f.B);
        Assert.All(queued,p=>Assert.Equal(unfunded?0:1,p.Ch.ArenaId));Assert.Equal(unfunded?0u:950_000u,queued[0].Ch.MoneyTotal);
        Assert.Single(f.Post.Mails);
    }
}
