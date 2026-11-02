using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

internal static class GorEggPickupCheck
{
    // SQL is read-only. No live actor, saved quest, inventory or reward row is touched.
    public static async Task RunAsync(string planPath)
    {
        string connection=Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var t=await new GameDatabase(connection).LoadTemplatesAsync();using var proof=JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
        var state=new MapState();var channel=new Channel();var map=new MapService(new MapServerOptions{Channels=new byte[]{1}},state,new World(),null,t,NullLogger<MapService>.Instance);
        map.InitNpcs();map.InitQuests();map.ApplyItemIdSeed(4_100_000_000L);
        var ch=new Character{CharId=1,Class=0,Country=4,AidCountry=3,Level=13,Hp=100,MaxHp=100};ch.Invens.Add(new(){InvenId=255});
        ch.Quests[7906]=new QuestProgress{Template=t.Quests[7906],TriggerCount=1};
        var session=new ClientSession(channel){CharId=1,UserId=1,Key=11,IsMain=true,State=EnterState.InGame,Channel=1,Char=ch};state.Register(session);
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static byte[] Exec(uint id)=>new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(id).WriteByte(0).WriteUInt32(0).ToArray();
        int Count(ushort item)=>ch.Invens.SelectMany(b=>b.Items).Where(i=>i.TemplateId==item).Sum(i=>i.Count);
        var passed=new List<string>();
        foreach(var change in proof.RootElement.GetProperty("changes").EnumerateArray())
        {
            if(change.GetProperty("route_id").ValueKind==JsonValueKind.Null)continue;
            uint route=change.GetProperty("route_id").GetUInt32();var target=change.GetProperty("after");ushort npc=target.GetProperty("npc_id").GetUInt16(),item=target.GetProperty("term_id").GetUInt16();
            var n=t.Npcs[npc];ch.MapId=n.MapId;ch.PosX=n.PosX;ch.PosY=n.PosY;ch.PosZ=n.PosZ;
            Require(n.MapId==target.GetProperty("map_id").GetUInt16() && n.PosX==target.GetProperty("x").GetSingle() && n.PosY==target.GetProperty("y").GetSingle() && n.PosZ==target.GetProperty("z").GetSingle(),"SQL pickup object differs from repaired navigation");
            channel.Packets.Clear();await map.DispatchClientAsync(session,new PacketWriter(Msg.CS_QUESTLIST_POSSIBLE_REQ).WriteByte(1).WriteUInt16(npc).ToArray());
            var possible=new PacketReader(channel.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_QUESTLIST_POSSIBLE_ACK));bool offered=false;int groups=possible.ReadByte();
            for(int i=0;i<groups;i++){possible.ReadUInt16();possible.ReadByte();int count=possible.ReadByte();for(int j=0;j<count;j++){uint id=possible.ReadUInt32();byte type=possible.ReadByte();if(id==route&&type==0)offered=true;}}
            Require(offered,"Nest does not advertise the expected routing action");
            await map.DispatchClientAsync(session,new PacketWriter(Msg.CS_NPCTALK_REQ).WriteUInt16(npc).ToArray());
            var talk=new PacketReader(channel.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_NPCTALK_ACK));Require(talk.ReadUInt32()==0 && talk.ReadUInt16()==npc,"Nest click reply points to another object");
            await map.DispatchClientAsync(session,Exec(route));
            var list=new PacketReader(channel.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_NPCITEMLIST_ACK));Require(list.ReadUInt16()==npc&&list.ReadByte()==7,"Nest did not open its pickup box");list.ReadByte();Require(list.ReadByte()==1&&list.ReadUInt16()==item,"Nest offered a different egg");
            var buy=new PacketWriter(Msg.CS_ITEMBUY_REQ).WriteUInt16(npc).WriteUInt32(route).WriteUInt16(item).WriteByte(1).WriteByte(252).WriteByte(255).ToArray();
            await map.DispatchClientAsync(session,buy);Require(Count(item)==1,"Egg pickup did not reach inventory");
            Require(channel.Packets.Any(p=>{var r=new PacketReader(p);return r.Id==Msg.CS_QUESTUPDATE_ACK&&r.ReadUInt32()==7906&&r.ReadUInt32()==item&&r.ReadByte()==2&&r.ReadByte()==1;}),"Egg pickup did not update the mission objective");
            await map.DispatchClientAsync(session,buy);Require(Count(item)==1,"Egg pickup duplicated");passed.Add($"{route}: nest {npc}, item {item}, advertisement, click, box, pickup, objective and replay guard");
        }
        var giver=t.Npcs[26041];ch.MapId=giver.MapId;ch.PosX=giver.PosX;ch.PosY=giver.PosY;ch.PosZ=giver.PosZ;
        await map.DispatchClientAsync(session,Exec(7907));Require(ch.Quests[7906].CompleteCount==1&&!ch.IsRunningQuest(7906),"Egg turn-in failed");
        for(ushort item=9644;item<=9648;item++)Require(Count(item)==0,"Turn-in did not consume all five eggs");
        long money=ch.MoneyTotal;await map.DispatchClientAsync(session,Exec(7907));Require(ch.MoneyTotal==money,"Repeated turn-in granted another reward");
        passed.Add("Ennahi turn-in consumes five distinct eggs, completes mission and rejects repeat rewards");
        Console.WriteLine(JsonSerializer.Serialize(new{Passed=passed,ReadOnlySql=true,PlayerRowsModified=0,RealClientNavigationVerified=false}));
    }
    private sealed class Channel:IClientChannel
    {public List<byte[]> Packets{get;}=new();public IPEndPoint? RemoteEndPoint=>null;public void Send(byte[] p)=>Packets.Add(p);public void Send(PacketWriter p)=>Packets.Add(p.ToArray());public void Close(){}}
    private sealed class World:IWorldSink{public void Send(byte[] p){}public void Send(PacketWriter p){}}
}
