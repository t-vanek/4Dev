using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

internal static class GorNavigationJourneyCheck
{
    // Native SQL charts, synthetic active missions/actors. No character or gameplay SQL writes.
    public static async Task RunAsync(string planPath)
    {
        var connection=Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var templates=await new GameDatabase(connection).LoadTemplatesAsync();using var document=JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
        var kinds=new HashSet<ushort>{793,796,797,798,800};templates.MonsterSpawns.RemoveAll(s=>!s.Types.Any(m=>kinds.Contains(m.MonId)));
        var passed=new List<object>();long seed=4_200_000_000;
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static byte[] Exec(uint id)=>new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(id).WriteByte(0).WriteUInt32(0).ToArray();
        foreach(var plan in document.RootElement.GetProperty("plans").EnumerateArray())
        {
            uint id=plan.GetProperty("quest_id").GetUInt32(),completion=plan.GetProperty("completion").GetProperty("dwQuestID").GetUInt32();ushort npc=plan.GetProperty("completion").GetProperty("dwTriggerID").GetUInt16();
            var state=new MapState();var client=new Sink();var service=new MapService(new MapServerOptions{Channels=new byte[]{1}},state,new World(),null,templates,NullLogger<MapService>.Instance);
            service.SpawnRng=new Random(1);service.QuestRng=new Random(1);service.InitNpcs();service.InitQuests();service.ApplyItemIdSeed(seed+=10000);service.InitMonsterSpawns();service.RunMonsterRegen(60000);
            var ch=new Character{CharId=1,Country=4,AidCountry=3,Class=0,Level=13,Hp=100,MaxHp=100};ch.Invens.Add(new(){InvenId=255});
            ch.Quests[id]=new QuestProgress{Template=templates.Quests[id],TriggerCount=1};
            var session=new ClientSession(client){CharId=1,UserId=1,Key=11,IsMain=true,State=EnterState.InGame,Channel=1,Char=ch};state.Register(session);
            void At(ushort target){var n=templates.Npcs[target];ch.MapId=n.MapId;ch.PosX=n.PosX;ch.PosY=n.PosY;ch.PosZ=n.PosZ;}
            int Count(ushort item)=>ch.Invens.SelectMany(b=>b.Items).Where(i=>i.TemplateId==item).Sum(i=>i.Count);
            async Task<Monster> Kill(ushort kind)
            {
                var mon=state.AllMonsters().FirstOrDefault(m=>m.ChartId==kind&&m.Hp>0) ?? throw new InvalidOperationException($"No enabled SQL spawn for monster {kind}");
                ch.MapId=mon.MapId;ch.PosX=mon.PosX;ch.PosY=mon.PosY;ch.PosZ=mon.PosZ;mon.AddDamage(1,0,mon.Hp);mon.Hp=0;mon.Status=3;
                await service.DispatchWorldAsync(new PacketWriter(Msg.MW_MONSTERDIE_REQ).WriteUInt32(1).WriteUInt32(11).WriteUInt32(0).WriteUInt32(mon.Id)
                    .WriteFloat(mon.PosX).WriteFloat(mon.PosY).WriteFloat(mon.PosZ).WriteUInt16(kind).WriteByte(3).WriteByte(5).WriteByte(1).ToArray());
                return mon;
            }
            At(npc);bool hasObjectives=plan.GetProperty("objectives").GetArrayLength()>0;
            if(hasObjectives){await service.DispatchClientAsync(session,Exec(completion));Require(ch.IsRunningQuest(id)&&ch.Quests[id].CompleteCount==0,$"Mission {id} completed before objectives");}
            Monster? marka=null;int kills=0;
            foreach(var term in plan.GetProperty("objectives").EnumerateArray())
            {
                if(term.GetProperty("type").GetByte()!=3)continue;
                ushort kind=term.GetProperty("id").GetUInt16();int needed=term.GetProperty("count").GetInt32();
                for(int i=0;i<needed;i++)
                {
                    var killed=await Kill(kind);if(kind==800)marka=killed;kills++;
                    Require(ch.Quests[id].RunningTerms.Single(t=>t.TermType==3&&t.TermId==kind).Count==i+1,$"Mission {id} hunt counter did not advance");
                    if(i+1<needed){At(npc);await service.DispatchClientAsync(session,Exec(completion));Require(ch.IsRunningQuest(id),$"Mission {id} completed with too few kills");}
                }
            }
            foreach(var term in plan.GetProperty("objectives").EnumerateArray())
            {
                if(term.GetProperty("type").GetByte()!=2)continue;ushort item=term.GetProperty("id").GetUInt16();
                if(item==9651)
                {
                    Require(marka is not null&&marka.CorpseInven.Items.Any(i=>i.TemplateId==9651),"Marka did not produce the quest mace on its native death relay");
                    Require(Count(item)==0,"Mace granted without being looted");At(npc);await service.DispatchClientAsync(session,Exec(completion));Require(ch.IsRunningQuest(id),"Marka kill bypassed missing mace");
                    ch.MapId=marka!.MapId;ch.PosX=marka.PosX;ch.PosY=marka.PosY;ch.PosZ=marka.PosZ;
                    var drop=marka.CorpseInven.Items.Single(i=>i.TemplateId==item);var take=new PacketWriter(Msg.CS_MONITEMTAKE_REQ).WriteUInt32(marka.Id).WriteByte(drop.ItemSlot).WriteByte(255).WriteByte(255).ToArray();
                    await service.DispatchClientAsync(session,take);await service.DispatchClientAsync(session,take);Require(Count(item)==1,"Mace pickup missing or duplicated");
                }
                else
                {
                    Require(item==9649,"Unexpected fetch item");At(26067);await service.DispatchClientAsync(session,Exec(7925));
                    var list=new PacketReader(client.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_NPCITEMLIST_ACK));Require(list.ReadUInt16()==26067&&list.ReadByte()==7,"Peijo stash did not open its pickup dialog");list.ReadByte();Require(list.ReadByte()==1&&list.ReadUInt16()==9649,"Peijo stash lists another item");
                    var buy=new PacketWriter(Msg.CS_ITEMBUY_REQ).WriteUInt16(26067).WriteUInt32(7925).WriteUInt16(item).WriteByte(1).WriteByte(252).WriteByte(255).ToArray();await service.DispatchClientAsync(session,buy);await service.DispatchClientAsync(session,buy);Require(Count(item)==1,"Peijo weapon missing or duplicated");
                }
            }
            At(npc);client.Packets.Clear();await service.DispatchClientAsync(session,new PacketWriter(Msg.CS_QUESTLIST_POSSIBLE_REQ).WriteByte(1).WriteUInt16(npc).ToArray());
            var offered=new PacketReader(client.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_QUESTLIST_POSSIBLE_ACK));bool found=false;int groups=offered.ReadByte();
            for(int i=0;i<groups;i++){offered.ReadUInt16();offered.ReadByte();int count=offered.ReadByte();for(int j=0;j<count;j++){uint q=offered.ReadUInt32();byte condition=offered.ReadByte();if(q==completion&&condition==0)found=true;}}
            Require(found,$"Mission {id} completion is not offered by NPC {npc}");
            await service.DispatchClientAsync(session,new PacketWriter(Msg.CS_NPCTALK_REQ).WriteUInt16(npc).ToArray());var talk=new PacketReader(client.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_NPCTALK_ACK));talk.ReadUInt32();Require(talk.ReadUInt16()==npc,"Turn-in dialog points to another NPC");
            await service.DispatchClientAsync(session,Exec(completion));Require(ch.Quests[id].CompleteCount==1&&!ch.IsRunningQuest(id),$"Mission {id} did not complete");
            foreach(var term in plan.GetProperty("objectives").EnumerateArray())if(term.GetProperty("type").GetByte()==2)Require(Count(term.GetProperty("id").GetUInt16())==0,"Turn-in did not consume the required item");
            long money=ch.MoneyTotal;uint exp=ch.Exp;await service.DispatchClientAsync(session,Exec(completion));Require(ch.MoneyTotal==money&&ch.Exp==exp,"Repeated turn-in granted rewards again");
            passed.Add(new{Quest=id,Kills=kills,TurnInNpc=npc,CorrectNpcOffersCompletion=true,RepeatedRewardRejected=true});
        }
        Console.WriteLine(JsonSerializer.Serialize(new{Passed=passed,ReadOnlySql=true,PlayerRowsModified=0,Scope="Synthetic already-active missions: native existing spawns, kill relay, quest item pickups and completion; no real-client or full character persistence claim"}));
    }
    private sealed class Sink:IClientChannel{public List<byte[]> Packets{get;}=new();public IPEndPoint? RemoteEndPoint=>null;public void Send(byte[] p)=>Packets.Add(p);public void Send(PacketWriter p)=>Packets.Add(p.ToArray());public void Close(){}}
    private sealed class World:IWorldSink{public void Send(byte[] p){}public void Send(PacketWriter p){}}
}
