using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

internal static class GorTrainingLifecycleCheck
{
    public static async Task RunAsync(string planPath)
    {
        string connection=Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        if(!Regex.IsMatch(new SqlConnectionStringBuilder(connection).InitialCatalog,"^4DevQuestTest_training_[a-f0-9]{32}$"))throw new InvalidOperationException("Isolated generated training fixture required");
        using var plan=JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
        var db=new GameDatabase(connection);var t=await db.LoadTemplatesAsync();
        var order=new uint[]{11012,11013,11014,11015,11016,11017,11018,11019,11021,11083,11025,11085,11027,11037};
        var passed=new List<string>();long seed=4_000_000_000L;uint monsterId=0x70001;
        Character ch=new(){CharId=1,Country=4,Class=3,Level=1,Hp=100,MaxHp=100};
        ch.Invens.Add(new(){InvenId=255});
        MapService map=null!;ClientSession session=null!;MapState state=null!;Channel channel=null!;
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static byte[] Exec(uint id)=>new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(id).WriteByte(0).WriteUInt32(0).ToArray();
        void Enter()
        {
            state=new();channel=new();map=new(new MapServerOptions{Channels=new byte[]{1}},state,new World(),null,t,NullLogger<MapService>.Instance);
            map.InitQuests();map.ApplyItemIdSeed(seed+=1000);
            foreach(var n in t.Npcs.Values)map.AddNpc(new(){Id=n.Id,Country=n.Country,MapId=n.MapId,Type=n.Type});
            session=new(channel){CharId=1,UserId=1,Key=11,IsMain=true,State=EnterState.InGame,Channel=1,Char=ch};state.Register(session);
        }
        async Task Reload()
        {
            await db.SaveQuestsAsync(1,MapService.BuildQuestSaves(ch,0),requireProcedures:true);
            var inventory=map.BuildInvenSaves(ch);await db.SaveInventoryAsync(1,inventory.invens,inventory.items,requireProcedures:true);
            // The native quest/inventory tables are reloaded through a new service and SQL calls.
            // Character balances stay in memory here; full character snapshot persistence has its separate fixture.
            var next=new Character{CharId=1,Country=ch.Country,Class=ch.Class,Level=ch.Level,Exp=ch.Exp,Hp=100,MaxHp=100};next.SetMoneyTotal(ch.MoneyTotal);next.Invens.Add(new(){InvenId=255});ch=next;
            foreach(var item in await db.LoadItemsAsync(1))ch.Invens[0].Items.Add(new(){DlId=item.DlId,TemplateId=item.TemplateId,Template=t.Item(item.TemplateId),Count=item.Count,ItemSlot=item.ItemSlot});
            Enter();var progress=await db.LoadQuestsAsync(1);map.LoadQuestProgress(ch,progress.quests,progress.terms);
        }
        int Count(ushort id)=>ch.Invens.SelectMany(i=>i.Items).Where(i=>i.TemplateId==id).Sum(i=>i.Count);
        async Task<Monster> Kill(ushort kind)
        {
            var mon=new Monster{Id=monsterId++,ChartId=kind,MapId=ch.MapId,Channel=1,Hp=0,MaxHp=100,Status=3};state.AddMonster(mon);
            await map.DispatchWorldAsync(new PacketWriter(Msg.MW_MONSTERDIE_REQ).WriteUInt32(1).WriteUInt32(11).WriteUInt32(0).WriteUInt32(mon.Id)
                .WriteFloat(0).WriteFloat(0).WriteFloat(0).WriteUInt16(kind).WriteByte(3).WriteByte(5).WriteByte(1).ToArray());
            return mon;
        }
        async Task Take(Monster mon)
        {
            foreach(var item in mon.CorpseInven.Items.ToArray())await map.DispatchClientAsync(session,new PacketWriter(Msg.CS_MONITEMTAKE_REQ).WriteUInt32(mon.Id).WriteByte(item.ItemSlot).WriteByte(255).WriteByte(255).ToArray());
        }
        Enter();
        await map.DispatchClientAsync(session,Exec(11013));Require(!ch.IsRunningQuest(11013),"Child bypassed its predecessor");
        Require((await Kill(773)).CorpseInven.Items.Count==0,"Eye dropped without active mission");
        foreach(uint id in order)
        {
            var q=t.Quests[id];ch.MapId=0;
            await map.DispatchClientAsync(session,Exec(id));Require(ch.IsRunningQuest(id),$"Mission {id} did not start");
            if(id==11014)Require(Count(9589)==1,"Soup not handed over once on acceptance");
            await Reload();Require(ch.IsRunningQuest(id),$"Acceptance {id} did not survive reload");
            var end=t.Quests.Values.Single(x=>x.Type==7 && x.ParentId==id && x.Terms.Any(y=>y.TermType==1&&y.TermId==id));
            if(q.Terms.Any(x=>x.TermType==3 || x.TermType==2 && Count((ushort)x.TermId)<x.Count))
            {await map.DispatchClientAsync(session,Exec(end.QuestId));Require(ch.IsRunningQuest(id),$"Unmet mission {id} paid out");}
            foreach(var term in q.Terms.Where(x=>x.TermType==3))for(int n=0;n<term.Count;n++)await Kill((ushort)term.TermId);
            if(id is 11015 or 11025)
            {
                ushort kind=id==11015?(ushort)773:(ushort)801,item=id==11015?(ushort)9586:(ushort)9587;
                var mon=await Kill(kind);Require(mon.CorpseInven.Items.Single().TemplateId==item,"Quest drop missing from corpse");
                await Take(mon);Require(Count(item)==1,"Quest loot could not be collected");
                Require((await Kill(kind)).CorpseInven.Items.Count==0,"Duplicate quest item dropped while already owned");
            }
            if(id==11016)
            {
                for(int n=0;n<3;n++)
                {
                    uint route=(uint)(40007930+n);ushort item=(ushort)(9629+n),npc=(ushort)(26055+n);
                    channel.Packets.Clear();
                    await map.DispatchClientAsync(session,new PacketWriter(Msg.CS_QUESTLIST_POSSIBLE_REQ).WriteByte(1).WriteUInt16(npc).ToArray());
                    var possible=new PacketReader(channel.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_QUESTLIST_POSSIBLE_ACK));
                    Require(possible.ReadByte()==1 && possible.ReadUInt16()==npc,"Pickup NPC omitted from possible quest list");
                    possible.ReadByte();int offers=possible.ReadByte();bool advertised=false;
                    for(int k=0;k<offers;k++){uint offered=possible.ReadUInt32();byte condition=possible.ReadByte();if(offered==route && condition==0)advertised=true;}
                    Require(advertised,"Client was not offered the runnable pickup action");
                    await map.DispatchClientAsync(session,new PacketWriter(Msg.CS_NPCTALK_REQ).WriteUInt16(npc).ToArray());
                    var talk=new PacketReader(channel.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_NPCTALK_ACK));talk.ReadUInt32();Require(talk.ReadUInt16()==npc,"NPC click reply used a different target");
                    await map.DispatchClientAsync(session,Exec(route));
                    var list=new PacketReader(channel.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_NPCITEMLIST_ACK));
                    Require(list.ReadUInt16()==npc && list.ReadByte()==7,"Pickup did not open the box dialog");list.ReadByte();Require(list.ReadByte()==1 && list.ReadUInt16()==item,"Wrong sword offered in box dialog");
                    var buy=new PacketWriter(Msg.CS_ITEMBUY_REQ).WriteUInt16(npc).WriteUInt32(route).WriteUInt16(item).WriteByte(1).WriteByte(252).WriteByte(255).ToArray();
                    await map.DispatchClientAsync(session,buy);await map.DispatchClientAsync(session,buy);Require(Count(item)==1,"Routing pickup missing or duplicated");
                }
            }
            await Reload();long before=ch.MoneyTotal;uint expBefore=ch.Exp;
            await map.DispatchClientAsync(session,Exec(end.QuestId));Require(!ch.IsRunningQuest(id) && ch.Quests[id].CompleteCount==1,$"Turn-in {id} failed");
            Require(ch.MoneyTotal-before==q.Rewards.Where(r=>r.RewardType==1).Sum(r=>(long)r.RewardId),$"Money differs for {id}");
            foreach(var term in q.Terms.Where(x=>x.TermType==2))Require(Count((ushort)term.TermId)==0,$"Turn-in did not consume {term.TermId}");
            if(id==11017)Require(Count(1596)==1,"Class-specific Shondoleon weapon missing");
            if(id==11027)Require(Count(14)==1,"Gallau bag missing");
            await Reload();Require(ch.Quests[id].CompleteCount==1,$"Completion {id} did not survive reload");
            before=ch.MoneyTotal;await map.DispatchClientAsync(session,Exec(end.QuestId));await map.DispatchClientAsync(session,Exec(id));Require(ch.MoneyTotal==before && !ch.IsRunningQuest(id),$"Mission {id} replayed");
            passed.Add($"{id}: ordered acceptance, objectives, turn-in, native SQL quest/inventory reload, replay guard");
        }
        Require((await Kill(801)).CorpseInven.Items.Count==0,"Certificate dropped after completion");
        // The approved fixed population must load from SQL and respect its configured delay.
        t.MonsterSpawns.RemoveAll(x=>x.Spawn.Id!=32404);state=new();map=new(new MapServerOptions{Channels=new byte[]{1}},state,new World(),null,t,NullLogger<MapService>.Instance);map.InitMonsterSpawns();map.RunMonsterRegen(60000);
        Require(state.AllMonsters().Single().ChartId==801,"Approved Moray did not initialize");
        Console.WriteLine(JsonSerializer.Serialize(new{Passed=passed,SequentialMissions=14,NativeQuestAndInventoryReload=true,RealClientVerified=false}));
    }
    private sealed class Channel:IClientChannel
    {public List<byte[]> Packets{get;}=new();public IPEndPoint? RemoteEndPoint=>null;public void Send(byte[] p)=>Packets.Add(p);public void Send(PacketWriter p)=>Packets.Add(p.ToArray());public void Close(){}}
    private sealed class World:IWorldSink{public void Send(byte[] p){}public void Send(PacketWriter p){}}
}
