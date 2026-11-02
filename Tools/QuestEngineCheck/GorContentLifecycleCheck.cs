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

internal static class GorContentLifecycleCheck
{
    public static async Task ReadOnlyAsync(string path)
    {
        var connection=Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var t=await new GameDatabase(connection).LoadTemplatesAsync();using var plan=JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var after=plan.RootElement.GetProperty("after");
        foreach(var h in after.GetProperty("headers").EnumerateArray())
        {
            uint id=h.GetProperty("dwQuestID").GetUInt32();var q=t.Quests[id];
            if(q.Type!=h.GetProperty("bType").GetByte() || q.ParentId!=h.GetProperty("dwParentID").GetUInt32() || q.TriggerId!=h.GetProperty("dwTriggerID").GetUInt32()
                || q.CountMax!=h.GetProperty("bCountMax").GetByte())throw new InvalidOperationException($"Production template header differs: {id}");
            var terms=after.GetProperty("terms").EnumerateArray().Where(r=>r.GetProperty("dwQuestID").GetUInt32()==id)
                .Select(r=>(r.GetProperty("dwTermID").GetUInt32(),r.GetProperty("bTermType").GetByte(),r.GetProperty("bCount").GetByte())).Order();
            if(!q.Terms.Select(r=>(r.TermId,r.TermType,r.Count)).Order().SequenceEqual(terms))throw new InvalidOperationException($"Production objective differs: {id}");
            var rewards=after.GetProperty("rewards").EnumerateArray().Where(r=>r.GetProperty("dwQuestID").GetUInt32()==id)
                .Select(r=>(r.GetProperty("dwRewardID").GetUInt32(),r.GetProperty("bRewardType").GetByte(),r.GetProperty("bTakeMethod").GetByte(),r.GetProperty("bTakeData").GetByte(),r.GetProperty("bCount").GetByte())).Order();
            if(!q.Rewards.Select(r=>(r.RewardId,r.RewardType,r.TakeMethod,r.TakeData,r.Count)).Order().SequenceEqual(rewards))throw new InvalidOperationException($"Production reward differs: {id}");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{ReadOnly=true,SqlWrites=0,VerifiedTemplates=after.GetProperty("headers").GetArrayLength(),RealClientVerified=false}));
    }
    public static async Task RunAsync(string planPath)
    {
        var connection=Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        if(!Regex.IsMatch(new SqlConnectionStringBuilder(connection).InitialCatalog,"^4DevQuestTest_content_[a-f0-9]{32}$"))throw new InvalidOperationException("Generated content fixture required");
        using var plan=JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
        var extra=plan.RootElement.TryGetProperty("extra_root_ids",out var extraRoots)?extraRoots.EnumerateArray().Select(x=>x.GetUInt32()):new uint[]{5917,5919,10634};
        var ids=plan.RootElement.GetProperty("recipe_ids").EnumerateArray().Select(x=>x.GetUInt32())
            .Concat(plan.RootElement.GetProperty("aligned_mission_ids").EnumerateArray().Select(x=>x.GetUInt32())).Concat(extra).ToArray();
        var t=new TemplateStore();var npcs=new Dictionary<ushort,Npc>();
        await using(var c=new SqlConnection(connection))
        {
            await c.OpenAsync();
            await using(var cmd=new SqlCommand("SELECT dwQuestID,dwParentID,bType,bTriggerType,dwTriggerID,bCountMax,bConditionCheck,bForceRun FROM TQUESTCHART",c))
            await using(var r=await cmd.ExecuteReaderAsync())while(await r.ReadAsync())
                t.Quests[Convert.ToUInt32(r[0])]=new(){QuestId=Convert.ToUInt32(r[0]),ParentId=Convert.ToUInt32(r[1]),Type=Convert.ToByte(r[2]),TriggerType=Convert.ToByte(r[3]),TriggerId=Convert.ToUInt32(r[4]),CountMax=Convert.ToByte(r[5]),ConditionCheck=Convert.ToByte(r[6]),ForceRun=Convert.ToByte(r[7])};
            foreach(string table in new[]{"TQUESTTERMCHART","TQCONDITIONCHART","TQREWARDCHART"})
            {
                var cols=table switch{"TQUESTTERMCHART"=>"dwQuestID,dwTermID,bTermType,bCount","TQCONDITIONCHART"=>"dwQuestID,dwConditionID,bConditionType,bCount",_=>"dwQuestID,dwRewardID,bRewardType,bTakeMethod,bTakeData,bCount"};
                await using var cmd=new SqlCommand($"SELECT {cols} FROM {table} ORDER BY dwID",c);await using var r=await cmd.ExecuteReaderAsync();
                while(await r.ReadAsync())
                {
                    if(!t.Quests.TryGetValue(Convert.ToUInt32(r[0]),out var q))continue;
                    uint id=Convert.ToUInt32(r[1]);byte type=Convert.ToByte(r[2]);
                    if(table=="TQUESTTERMCHART")q.Terms.Add(new(id,type,Convert.ToByte(r[3])));
                    else if(table=="TQCONDITIONCHART")q.Conditions.Add(new(id,type,Convert.ToByte(r[3])));
                    else q.Rewards.Add(new(id,type,Convert.ToByte(r[3]),Convert.ToByte(r[4]),Convert.ToByte(r[5])));
                }
            }
            await using(var cmd=new SqlCommand("SELECT wItemID,bType,bStack,dwClassID FROM TITEMCHART",c))
            await using(var r=await cmd.ExecuteReaderAsync())while(await r.ReadAsync())
            {ushort id=unchecked((ushort)Convert.ToInt32(r[0]));t.Items[id]=new(id,Convert.ToByte(r[1]),new float[4],Stack:Convert.ToByte(r[2]),ClassId:unchecked((uint)Convert.ToInt64(r[3])));}
            await using(var cmd=new SqlCommand("SELECT wID,bCountryID,wMapID FROM TNPCCHART",c))
            await using(var r=await cmd.ExecuteReaderAsync())while(await r.ReadAsync())
            {ushort id=unchecked((ushort)Convert.ToInt32(r[0]));npcs[id]=new(){Id=id,Country=Convert.ToByte(r[1]),MapId=unchecked((ushort)Convert.ToInt32(r[2]))};}
            await using(var cmd=new SqlCommand("SELECT dwSwitchID,wMapID,wPosX,wPosY,wPosZ,bStart,bLockOnOpen,bLockOnClose,dwDuration FROM TSWITCHCHART WHERE dwSwitchID=363",c))
            await using(var r=await cmd.ExecuteReaderAsync())while(await r.ReadAsync())
                t.Switches.Add(new(Convert.ToUInt32(r[0]),Convert.ToUInt16(r[1]),Convert.ToUInt16(r[2]),Convert.ToUInt16(r[3]),Convert.ToUInt16(r[4]),Convert.ToByte(r[5]),Convert.ToByte(r[6]),Convert.ToByte(r[7]),Convert.ToUInt32(r[8])));
        }
        var db=new GameDatabase(connection);var passed=new List<string>();long fixtureItemSeed=3_000_000_000L;
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static byte[] Exec(uint id)=>new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(id).WriteByte(0).WriteUInt32(0).ToArray();
        async Task<(MapService Map,Character Ch,ClientSession S,Channel Channel,MapState State)> Enter(uint id,QuestTemplate q)
        {
            uint mask=63;
            foreach(var cond in q.Conditions.Where(x=>x.ConditionType==5))mask&=cond.ConditionId;
            foreach(var reward in q.Rewards.Where(x=>x.RewardType==2))mask&=t.Items[(ushort)reward.RewardId].ClassId;
            Require(mask!=0,$"No compatible class for quest {q.QuestId}");
            byte cls=(byte)Enumerable.Range(0,6).First(x=>(mask&(1u<<x))!=0);
            var state=new MapState();var map=new MapService(new MapServerOptions{Channels=new byte[]{1}},state,new World(),null,t,NullLogger<MapService>.Instance);
            map.InitQuests();map.InitSwitches();map.ApplyItemIdSeed(fixtureItemSeed+=1000);
            foreach(var npc in npcs.Values)map.AddNpc(new(){Id=npc.Id,Country=npc.Country,MapId=npc.MapId});
            var ch=new Character{CharId=id,Class=cls,Country=4,Level=90,Hp=100,MaxHp=100};ch.Invens.Add(new(){InvenId=255});
            if(q.TriggerType==3)ch.MapId=npcs[(ushort)q.TriggerId].MapId;
            foreach(var item in await db.LoadItemsAsync(id))ch.Invens[0].Items.Add(new(){DlId=item.DlId,TemplateId=item.TemplateId,Template=t.Items[item.TemplateId],Count=item.Count,ItemSlot=item.ItemSlot});
            var rows=await db.LoadQuestsAsync(id);map.LoadQuestProgress(ch,rows.quests,rows.terms);
            if(q.ParentId!=0 && !ch.Quests.ContainsKey(q.ParentId))
                ch.Quests[q.ParentId]=new(){Template=t.Quests[q.ParentId],CompleteCount=1,TriggerCount=1};
            foreach(var cond in q.Conditions.Where(x=>x.ConditionType==15))if(!ch.Quests.ContainsKey(cond.ConditionId))
                ch.Quests[cond.ConditionId]=new(){Template=t.Quests[cond.ConditionId],CompleteCount=1,TriggerCount=1};
            var channel=new Channel();var s=new ClientSession(channel){CharId=id,UserId=id,Key=id+10,IsMain=true,State=EnterState.InGame,Channel=1,Char=ch};state.Register(s);
            return(map,ch,s,channel,state);
        }
        async Task Save(MapService map,Character ch)
        {await db.SaveQuestsAsync(ch.CharId,MapService.BuildQuestSaves(ch,0),requireProcedures:true);var b=map.BuildInvenSaves(ch);await db.SaveInventoryAsync(ch.CharId,b.invens,b.items,requireProcedures:true);}
        foreach(uint id in ids)
        {
            var q=t.Quests[id];var a=await Enter(id,q);bool craft=q.Type==18;
            var end=craft?q:t.Quests.Values.Single(x=>x.Type==7 && x.ParentId==id && x.Terms.Any(y=>y.TermType==1&&y.TermId==id));
            if(craft)
            {
                await a.Map.DispatchClientAsync(a.S,Exec(id));Require(a.Ch.Invens.All(i=>i.Items.Count==0),$"Empty craft {id} granted an item");
            }
            // Derive ingredients from the migrated SQL definition, not a hand-written duplicate recipe.
            foreach(var term in q.Terms.Where(x=>x.TermType==2))
                a.Ch.Invens[0].Items.Add(new(){TemplateId=(ushort)term.TermId,Template=t.Items[(ushort)term.TermId],Count=term.Count,ItemSlot=(byte)a.Ch.Invens[0].Items.Count});
            if(id==7307)
            {
                var parent=a.Ch.Quests[q.ParentId];a.Ch.Quests.Remove(q.ParentId);
                await a.Map.DispatchClientAsync(a.S,Exec(id));Require(!a.Ch.IsRunningQuest(id),"Wake-up bypassed required parent");a.Ch.Quests[q.ParentId]=parent;
            }
            if(!craft)await a.Map.DispatchClientAsync(a.S,Exec(id));
            if(id==7307)
            {
                Require(a.Ch.IsRunningQuest(id),"Wake-up giver did not accept in map 2010");await Save(a.Map,a.Ch);a=await Enter(id,q);
                Require(a.Ch.IsRunningQuest(id),"Wake-up acceptance did not survive SQL reload");
                await a.Map.DispatchClientAsync(a.S,Exec(end.QuestId));Require(a.Ch.IsRunningQuest(id),"Wake-up turned in without leaving the trial map");
                a.Ch.MapId=npcs[(ushort)end.TriggerId].MapId;
            }
            a.Channel.Packets.Clear();await a.Map.DispatchClientAsync(a.S,Exec(end.QuestId));
            var ack=a.Channel.Packets.LastOrDefault(p=>new PacketReader(p).Id==Msg.CS_QUESTCOMPLETE_ACK);
            Require(ack is not null && new PacketReader(ack).ReadByte()==0,$"Quest/craft {id} did not complete");
            var expected=q.Rewards.Where(x=>x.RewardType==2).GroupBy(x=>x.RewardId).ToDictionary(g=>(ushort)g.Key,g=>g.Sum(x=>x.Count));
            var actual=a.Ch.Invens.SelectMany(i=>i.Items).GroupBy(x=>x.TemplateId).ToDictionary(g=>g.Key,g=>g.Sum(x=>x.Count));
            Require(expected.Count==actual.Count && expected.All(x=>actual.GetValueOrDefault(x.Key)==x.Value),$"Ingredients/rewards differ for {id}");
            await Save(a.Map,a.Ch);var reloaded=await Enter(id,q);
            Require(reloaded.Ch.Invens.SelectMany(i=>i.Items).Sum(i=>i.Count)==expected.Values.Sum(),$"Inventory did not reload for {id}");
            if(!craft)
            {
                Require(reloaded.Ch.Quests[id].CompleteCount==1,$"Completion did not persist for {id}");
                await reloaded.Map.DispatchClientAsync(reloaded.S,Exec(end.QuestId));
                Require(reloaded.Ch.Invens.SelectMany(i=>i.Items).Sum(i=>i.Count)==expected.Values.Sum(),$"Completion replay paid again for {id}");
                if(id is 5917 or 5919)
                {
                    foreach(var term in q.Terms.Where(x=>x.TermType==2))
                        reloaded.Ch.Invens[0].Items.Add(new(){TemplateId=(ushort)term.TermId,Template=t.Items[(ushort)term.TermId],Count=term.Count,ItemSlot=(byte)reloaded.Ch.Invens[0].Items.Count});
                    await reloaded.Map.DispatchClientAsync(reloaded.S,Exec(id));await reloaded.Map.DispatchClientAsync(reloaded.S,Exec(end.QuestId));
                    Require(reloaded.Ch.Quests[id].CompleteCount==2 && reloaded.Ch.Invens.SelectMany(i=>i.Items).Sum(i=>i.Count)==expected.Values.Sum()*2,$"Corneus repeat {id} differs");
                    await Save(reloaded.Map,reloaded.Ch);var twice=await Enter(id,q);
                    Require(twice.Ch.Quests[id].CompleteCount==2 && twice.Ch.Invens.SelectMany(i=>i.Items).Sum(i=>i.Count)==expected.Values.Sum()*2,$"Second Corneus exchange did not persist for {id}");
                }
            }
            passed.Add($"{id}: migrated SQL definition executes exact ingredients/rewards and native inventory/quest reload");
        }
        if(plan.RootElement.TryGetProperty("legacy_switch_internal_id",out var legacyId))
        {
            uint id=legacyId.GetUInt32();var q=t.Quests[id];var a=await Enter(id,q);var sw=t.Switches.Single(x=>x.SwitchId==363);a.Ch.MapId=sw.MapId;
            await a.Map.DispatchWorldAsync(new PacketWriter(Msg.MW_MONSTERDIE_REQ).WriteUInt32(a.Ch.CharId).WriteUInt32(a.S.Key).WriteUInt32(0).WriteUInt32(1)
                .WriteFloat(0).WriteFloat(0).WriteFloat(0).WriteUInt16(7125).WriteByte(18).WriteByte(9).WriteByte(1).ToArray());
            Require(a.State.FindSwitch(1,sw.MapId,363)!.Opened,"Relocated legacy completion action no longer opens switch 363");
            passed.Add("Relocated legacy switch still responds to completion 7125 and opens switch 363");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{Passed=passed,NativeQuestAndInventoryVerified=true,RealClientVerified=false}));
    }
    private sealed class Channel:IClientChannel
    {public List<byte[]> Packets{get;}=new();public IPEndPoint? RemoteEndPoint=>null;public void Send(byte[] p)=>Packets.Add(p);public void Send(PacketWriter p)=>Packets.Add(p.ToArray());public void Close(){}}
    private sealed class World:IWorldSink{public void Send(byte[] p){}public void Send(PacketWriter p){}}
}
