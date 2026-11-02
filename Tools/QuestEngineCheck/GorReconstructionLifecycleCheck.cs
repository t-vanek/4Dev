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

// Native SQL projection/reload checks. Arena combat itself is covered by ReconstructedArenaTests;
// process-crash recovery of the full snapshot/point balance is covered by QuestLifecycleCheck.
internal static class GorReconstructionLifecycleCheck
{
    private sealed record Actor(MapService Map,ClientSession S,Character Ch);
    public static async Task ReadOnlyProbeAsync()
    {
        var connection=Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var t=await new GameDatabase(connection).LoadTemplatesAsync();
        var expected=new Dictionary<ushort,ushort>{{32398,3504},{32399,3502},{32400,3512},{32401,3510},{32402,3521},{32403,3519}};
        t.MonsterSpawns.RemoveAll(s=>!expected.ContainsKey(s.Spawn.Id));
        Require(t.MonsterSpawns.Count==6,"Six live populations did not load");
        var state=new MapState();var map=new MapService(new MapServerOptions{Channels=new byte[]{1}},state,new World(),null,t,NullLogger<MapService>.Instance);
        map.InitMonsterSpawns();map.RunMonsterRegen(60000);
        foreach(var pair in expected)
        {
            var spawn=t.MonsterSpawns.Single(s=>s.Spawn.Id==pair.Key).Spawn;
            var mon=state.FindMonster(Monster.MakeId(pair.Key,1,0));
            Require(mon is not null && mon.ChartId==pair.Value && mon.Hp>0 && mon.PosX==spawn.PosX && mon.PosZ==spawn.PosZ,"Live monster failed to initialize at its approved anchor");
        }
        Require(state.AllMonsters().Count()==6,"Live spawn slot count differs");
        Require(t.Arenas.Count(a=>a.QuestMatch && (a.Id==1 && a.Type==3 || a.Id==2 && a.Type==5))==2,"Live reconstructed arenas did not load");
        Require(new uint[]{1082,1084,1203,1208,3209,3210}.All(id=>t.Quests[id].Type==8),"Live missions did not load");
        Console.WriteLine(JsonSerializer.Serialize(new{ReadOnly=true,SqlWrites=0,LoadedMissions=6,ReconstructedArenas=2,InitializedApprovedMonsters=6,RealClientVerified=false}));
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    }
    public static async Task RunAsync()
    {
        var connection=Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        if(!Regex.IsMatch(new SqlConnectionStringBuilder(connection).InitialCatalog,"^4DevQuestTest_gor_[a-f0-9]{32}$"))
            throw new InvalidOperationException("A generated Gor fixture is required");
        var templates=new TemplateStore();const string ids="1082,1084,1203,1208,3209,3210,3211,9716,10552,10553,10596,10597";
        await using(var db=new SqlConnection(connection))
        {
            await db.OpenAsync();
            await using(var cmd=new SqlCommand($"SELECT dwQuestID,dwParentID,bType,bTriggerType,dwTriggerID,bCountMax,bConditionCheck,bForceRun FROM TQUESTCHART WHERE dwQuestID IN({ids})",db))
            await using(var r=await cmd.ExecuteReaderAsync())
                while(await r.ReadAsync()) templates.Quests[Convert.ToUInt32(r[0])]=new(){QuestId=Convert.ToUInt32(r[0]),ParentId=Convert.ToUInt32(r[1]),Type=Convert.ToByte(r[2]),
                    TriggerType=Convert.ToByte(r[3]),TriggerId=Convert.ToUInt32(r[4]),CountMax=Convert.ToByte(r[5]),ConditionCheck=Convert.ToByte(r[6]),ForceRun=Convert.ToByte(r[7])};
            foreach(string table in new[]{"TQUESTTERMCHART","TQCONDITIONCHART","TQREWARDCHART"})
            {
                string cols=table switch {"TQUESTTERMCHART"=>"dwQuestID,dwTermID,bTermType,bCount","TQCONDITIONCHART"=>"dwQuestID,dwConditionID,bConditionType,bCount",_=>"dwQuestID,dwRewardID,bRewardType,bTakeMethod,bTakeData,bCount"};
                await using var cmd=new SqlCommand($"SELECT {cols} FROM {table} WHERE dwQuestID IN({ids}) ORDER BY dwID",db);
                await using var r=await cmd.ExecuteReaderAsync();
                while(await r.ReadAsync())
                {
                    var q=templates.Quests[Convert.ToUInt32(r[0])];uint id=Convert.ToUInt32(r[1]);byte type=Convert.ToByte(r[2]);
                    if(table=="TQUESTTERMCHART") q.Terms.Add(new(id,type,Convert.ToByte(r[3])));
                    else if(table=="TQCONDITIONCHART") q.Conditions.Add(new(id,type,Convert.ToByte(r[3])));
                    else q.Rewards.Add(new(id,type,Convert.ToByte(r[3]),Convert.ToByte(r[4]),Convert.ToByte(r[5])));
                }
            }
            await using var item=new SqlCommand("SELECT bStack FROM TITEMCHART WHERE wItemID=9737",db);
            templates.Items[9737]=new(9737,0,new float[4],Stack:Convert.ToByte(await item.ExecuteScalarAsync()),ClassId:uint.MaxValue);
        }
        templates.LevelExp[95]=10_000_000;
        var store=new GameDatabase(connection);
        async Task<Actor> Enter(uint id)
        {
            var state=new MapState();var map=new MapService(new MapServerOptions(),state,new World(),null,templates,NullLogger<MapService>.Instance);
            map.InitQuests();map.ApplyItemIdSeed(2_000_000_000L+id*1000);
            foreach(ushort npc in new ushort[]{26424,26425,26427,26428,26081,26474}) map.AddNpc(new(){Id=npc,Country=npc==26081?(byte)2:(byte)3,MapId=0});
            var ch=new Character{CharId=id,Level=95,Country=2,Hp=100,MaxHp=100};
            ch.Invens.Add(new(){InvenId=255});
            foreach(var it in await store.LoadItemsAsync(id))
                ch.Invens[0].Items.Add(new(){DlId=it.DlId,TemplateId=it.TemplateId,Template=templates.Items[it.TemplateId],Count=it.Count,ItemSlot=it.ItemSlot});
            var rows=await store.LoadQuestsAsync(id);map.LoadQuestProgress(ch,rows.quests,rows.terms);
            var s=new ClientSession(new Channel()){CharId=id,UserId=id,Key=id+10,IsMain=true,State=EnterState.InGame,Channel=1,Char=ch};state.Register(s);
            return new(map,s,ch);
        }
        async Task Save(Actor a)
        {
            await store.SaveQuestsAsync(a.Ch.CharId,MapService.BuildQuestSaves(a.Ch,0),requireProcedures:true);
            var inventory=a.Map.BuildInvenSaves(a.Ch);
            await store.SaveInventoryAsync(a.Ch.CharId,inventory.invens,inventory.items,requireProcedures:true);
        }
        static byte[] Exec(uint id)=>new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(id).WriteByte(0).WriteUInt32(0).ToArray();
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static int Seals(Actor a)=>a.Ch.Invens.SelectMany(i=>i.Items).Where(i=>i.TemplateId==9737).Sum(i=>i.Count);
        async Task Hunt(Actor a,QuestTerm term,int count)
        {
            for(int i=0;i<count;i++) await a.Map.DispatchWorldAsync(new PacketWriter(Msg.MW_MONSTERDIE_REQ).WriteUInt32(a.Ch.CharId).WriteUInt32(a.S.Key)
                .WriteUInt32(0).WriteUInt32((uint)(0x70000+i)).WriteFloat(0).WriteFloat(0).WriteFloat(0).WriteUInt16((ushort)term.TermId).WriteByte(3).WriteByte(5).WriteByte(1).ToArray());
        }
        var a=await Enter(1);a.Ch.Level=93;
        await a.Map.DispatchClientAsync(a.S,Exec(3209));Require(!a.Ch.IsRunningQuest(3209),"Ragoen mission ignored minimum level");a.Ch.Level=99;
        await a.Map.DispatchClientAsync(a.S,Exec(3210));Require(!a.Ch.IsRunningQuest(3210),"Producer ignored maximum level");a.Ch.Level=95;
        await a.Map.DispatchClientAsync(a.S,Exec(3209));await Save(a);a=await Enter(1);
        Require(a.Ch.IsRunningQuest(3209),"Ragoen acceptance did not persist");
        for(int run=0;run<5;run++)
        {
            await a.Map.DispatchClientAsync(a.S,Exec(3210));
            var first=templates.Quests[3210].Terms[0];await Hunt(a,first,2);await Save(a);a=await Enter(1);
            Require(a.Ch.Quests[3210].RunningTerms.Single(t=>t.TermId==first.TermId).Count==2,"Partial hunt count did not persist");
            await a.Map.DispatchClientAsync(a.S,Exec(3211));Require(Seals(a)==run,"Incomplete producer paid a seal");
            foreach(var term in templates.Quests[3210].Terms) await Hunt(a,term,term.Count-(term.TermId==first.TermId?2:0));
            await a.Map.DispatchClientAsync(a.S,Exec(3211));
            Require(Seals(a)==run+1 && a.Ch.MoneyTotal==93110 && a.Ch.Exp==44253,"Producer rewards or repeatability differ");
            await a.Map.DispatchClientAsync(a.S,Exec(3211));Require(Seals(a)==run+1,"Producer paid duplicate reward");
            await Save(a);a=await Enter(1);Require(Seals(a)==run+1,"Earned seals did not survive inventory reload");
        }
        await a.Map.DispatchClientAsync(a.S,Exec(9716));
        Require(Seals(a)==0 && a.Ch.MoneyTotal==279329 && a.Ch.Exp==132759 && a.Ch.Quests[3209].CompleteCount==1,"Five-seal turn-in differs");
        await Save(a);a=await Enter(1);await a.Map.DispatchClientAsync(a.S,Exec(9716));await a.Map.DispatchClientAsync(a.S,Exec(3209));
        Require(!a.Ch.IsRunningQuest(3209) && Seals(a)==0 && a.Ch.MoneyTotal==0,"Reloaded Ragoen completion paid twice or seals reappeared");
        foreach(var pair in new[]{(1082u,10552u),(1084u,10553u),(1203u,10596u),(1208u,10597u)})
        {
            var p=await Enter(pair.Item1);await p.Map.DispatchClientAsync(p.S,Exec(pair.Item1));await p.Map.DispatchClientAsync(p.S,Exec(pair.Item2));
            Require(p.Ch.PvpUseablePoint==0,"Arena quest completed without victory");
            // Inject the result counter only: real two-round win production is exercised by actor combat tests.
            p.Ch.Quests[pair.Item1].RunningTerms.Single(t=>t.TermType==28).Count=1;await Save(p);p=await Enter(pair.Item1);
            Require(p.Ch.Quests[pair.Item1].RunningTerms.Single(t=>t.TermType==28).Count==1,"Type-28 victory did not survive native SQL reload");
            await p.Map.DispatchClientAsync(p.S,Exec(pair.Item2));uint expected=pair.Item1 is 1082 or 1203?100u:150u;
            Require(p.Ch.PvpUseablePoint==expected,"SQL-loaded arena quest reward differs");await Save(p);p=await Enter(pair.Item1);
            await p.Map.DispatchClientAsync(p.S,Exec(pair.Item2));Require(p.Ch.PvpUseablePoint==0,"Reloaded arena completion paid twice");
        }
        Console.WriteLine(JsonSerializer.Serialize(new {Passed=new[]{"SQL-loaded Ragoen level bounds and turn-in NPCs","Acceptance and partial six-kind hunt counts persist","Five repeated producer cycles issue five native SQL-persisted seals","Turn-in consumes five seals and cannot replay after reload","All four type-28 objectives survive native quest-store reload and pay once"},NativeQuestAndInventoryStoreVerified=true,RealClientVerified=false}));
    }
    private sealed class Channel:IClientChannel
    {public IPEndPoint? RemoteEndPoint=>null;public void Send(byte[] p){}public void Send(PacketWriter p){}public void Close(){}}
    private sealed class World:IWorldSink
    {public void Send(byte[] p){}public void Send(PacketWriter p){}}
}
