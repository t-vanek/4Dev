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

internal static class GorPactLifecycleCheck
{
    public static async Task RunAsync()
    {
        var connection = Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        if (!Regex.IsMatch(new SqlConnectionStringBuilder(connection).InitialCatalog, "^4DevQuestTest_pakt_[a-f0-9]{32}$"))
            throw new InvalidOperationException("A generated pact fixture database is required");
        var templates = new TemplateStore();
        await using (var db = new SqlConnection(connection))
        {
            await db.OpenAsync();
            await using (var cmd = new SqlCommand("SELECT dwQuestID,dwParentID,bType,bTriggerType,dwTriggerID,bCountMax,bConditionCheck,bForceRun FROM TQUESTCHART WHERE dwQuestID IN(547,548)", db))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    templates.Quests[Convert.ToUInt32(r[0])] = new() { QuestId=Convert.ToUInt32(r[0]),ParentId=Convert.ToUInt32(r[1]),Type=Convert.ToByte(r[2]),
                        TriggerType=Convert.ToByte(r[3]),TriggerId=Convert.ToUInt32(r[4]),CountMax=Convert.ToByte(r[5]),ConditionCheck=Convert.ToByte(r[6]),ForceRun=Convert.ToByte(r[7]) };
            foreach (string table in new[] { "TQUESTTERMCHART", "TQCONDITIONCHART", "TQREWARDCHART" })
            {
                string columns = table switch { "TQUESTTERMCHART" => "dwQuestID,dwTermID,bTermType,bCount", "TQCONDITIONCHART" => "dwQuestID,dwConditionID,bConditionType,bCount", _ => "dwQuestID,dwRewardID,bRewardType,bTakeMethod,bTakeData,bCount" };
                await using var cmd = new SqlCommand($"SELECT {columns} FROM {table} WHERE dwQuestID IN(547,548) ORDER BY dwID", db);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var q = templates.Quests[Convert.ToUInt32(r[0])]; uint id=Convert.ToUInt32(r[1]); byte kind=Convert.ToByte(r[2]);
                    if (table == "TQUESTTERMCHART") q.Terms.Add(new(id,kind,Convert.ToByte(r[3])));
                    else if (table == "TQCONDITIONCHART") q.Conditions.Add(new(id,kind,Convert.ToByte(r[3])));
                    else q.Rewards.Add(new(id,kind,Convert.ToByte(r[3]),Convert.ToByte(r[4]),Convert.ToByte(r[5])));
                }
            }
        }
        templates.LevelExp[89]=0; templates.LevelExp[90]=1_000_000;
        async Task<(MapService Map,ClientSession S,Character Ch)> Enter()
        {
            var state=new MapState(); var map=new MapService(new MapServerOptions(),state,new World(),null,templates,NullLogger<MapService>.Instance);
            map.InitQuests();map.AddNpc(new(){Id=26200,Country=3,MapId=0});map.AddNpc(new(){Id=26436,Country=3,MapId=0});
            var ch=new Character {CharId=1,Level=90,Country=1,Hp=100,MaxHp=100};
            var s=new ClientSession(new Channel()){CharId=1,UserId=1,Key=11,IsMain=true,State=EnterState.InGame,Channel=1,Char=ch};
            state.Register(s);
            var rows=await new GameDatabase(connection).LoadQuestsAsync(1);
            map.LoadQuestProgress(ch,rows.quests,rows.terms);
            return(map,s,ch);
        }
        static byte[] Execute(uint id)=>new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(id).WriteByte(0).WriteUInt32(0).ToArray();
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        var first=await Enter();
        await first.Map.DispatchClientAsync(first.S,Execute(547));
        await new GameDatabase(connection).SaveQuestsAsync(1,MapService.BuildQuestSaves(first.Ch,0),requireProcedures:true);
        var second=await Enter();
        Require(second.Ch.IsRunningQuest(547),"Pact acceptance did not reload through native SQL procedures");
        await second.Map.DispatchClientAsync(second.S,Execute(548));
        Require(second.Ch.Quests[547].CompleteCount==1 && second.Ch.MoneyTotal==1782 && second.Ch.Exp==855 && second.Ch.SoulLotExp==1022,"SQL-loaded pact did not grant its exact ordinary and soul rewards");
        await new GameDatabase(connection).SaveQuestsAsync(1,MapService.BuildQuestSaves(second.Ch,0),requireProcedures:true);
        var third=await Enter();
        Require(third.Ch.Quests[547].CompleteCount==1 && third.Ch.SoulLotExp==0,"Completion did not persist independently of the ephemeral soul bar");
        await third.Map.DispatchClientAsync(third.S,Execute(548));
        await third.Map.DispatchClientAsync(third.S,Execute(547));
        Require(!third.Ch.IsRunningQuest(547) && third.Ch.MoneyTotal==0 && third.Ch.Exp==0 && third.Ch.SoulLotExp==0,"Reloaded completion issued duplicate rewards");
        Console.WriteLine(JsonSerializer.Serialize(new { Passed=new[]{"SQL-loaded templates execute exact gold EXP and soul rewards","Acceptance survives native quest-store reload","Completion survives native quest-store reload","Fresh Map instance cannot repeat completed pact and starts with zero soul"},NativeQuestStoreVerified=true,GoldAndExpDatabaseRoundTripVerified=false,FullMapWorkerRestartVerified=false }));
    }
    private sealed class Channel:IClientChannel
    {
        public IPEndPoint? RemoteEndPoint=>null;
        public void Send(byte[] packet){}
        public void Send(PacketWriter packet){}
        public void Close(){}
    }
    private sealed class World:IWorldSink
    {
        public void Send(byte[] packet){}
        public void Send(PacketWriter packet){}
    }
}
