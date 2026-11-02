using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

internal static class GorPortalLifecycleCheck
{
    // Loads SQL charts read-only; actors, World relay and all movement exist only in this fixture process.
    public static async Task RunAsync(string planPath)
    {
        string connection=Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var templates=await new GameDatabase(connection).LoadTemplatesAsync();using var document=JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
        var passed=new List<string>();
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        foreach(var route in document.RootElement.GetProperty("routes").EnumerateArray())
        {
            ushort npcId=route.GetProperty("npc_id").GetUInt16(),portal=route.GetProperty("portal_id").GetUInt16();
            var expected=route.GetProperty("expected");ushort mapId=expected.GetProperty("map_id").GetUInt16();float x=expected.GetProperty("x").GetSingle(),y=expected.GetProperty("y").GetSingle(),z=expected.GetProperty("z").GetSingle();
            var state=new MapState();var world=new Sink();var client=new Sink();var service=new MapService(new MapServerOptions{Channels=new byte[]{1}},state,world,null,templates,NullLogger<MapService>.Instance);service.InitNpcs();
            var npc=state.FindNpc(npcId) ?? throw new InvalidOperationException("Source NPC missing");
            var ch=new Character{CharId=1,Country=4,Hp=73,MaxHp=100,MapId=npc.MapId,PosX=npc.PosX,PosY=npc.PosY,PosZ=npc.PosZ};
            var session=new ClientSession(client){CharId=1,UserId=1,Key=11,IsMain=true,Channel=1,State=EnterState.InGame,Char=ch};state.Register(session);
            await service.DispatchClientAsync(session,new PacketWriter(Msg.CS_NPCITEMLIST_REQ).WriteUInt16(npcId).ToArray());
            var list=new PacketReader(client.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_NPCITEMLIST_ACK));Require(list.ReadUInt16()==npcId && list.ReadByte()==8,"Wrong portal list");list.ReadByte();int count=list.ReadByte();bool offered=false;
            for(int i=0;i<count;i++){ushort id=list.ReadUInt16();uint price=list.ReadUInt32();if(id==portal){Require(price==0,"Captured free destination gained a fee");offered=true;}}
            Require(offered,$"Captured destination {portal} not offered by NPC {npcId} (bound to {npc.PortalId})");
            await service.DispatchClientAsync(session,new PacketWriter(Msg.CS_TELEPORT_REQ).WriteUInt16(npcId).WriteUInt16(portal).ToArray());
            var begin=new PacketReader(world.Packets.Last(p=>new PacketReader(p).Id==Msg.MW_BEGINTELEPORT_ACK));Require(begin.ReadUInt32()==1 && begin.ReadUInt32()==11,"Wrong teleport actor");begin.ReadByte();byte channel=begin.ReadByte();
            Require(begin.ReadUInt16()==mapId && begin.ReadFloat()==x && begin.ReadFloat()==y && begin.ReadFloat()==z,"SQL/World destination differs from the official capture");
            await service.DispatchWorldAsync(new PacketWriter(Msg.MW_STARTTELEPORT_REQ).WriteUInt32(1).WriteUInt32(11).WriteByte(channel).WriteUInt16(mapId).WriteFloat(x).WriteFloat(y).WriteFloat(z).ToArray());
            Require(ch.MapId==mapId && ch.PosX==x && ch.PosY==y && ch.PosZ==z && ch.Hp==73,"World transfer changed the destination or health");
            await service.DispatchWorldAsync(new PacketWriter(Msg.MW_TELEPORT_REQ).WriteUInt32(1).WriteUInt32(11).WriteByte(channel).WriteUInt16(mapId).WriteFloat(x).WriteFloat(y).WriteFloat(z).WriteByte(0).ToArray());
            var ack=new PacketReader(client.Packets.Last(p=>new PacketReader(p).Id==Msg.CS_TELEPORT_ACK));Require(ack.ReadByte()==0 && ack.ReadUInt32()==1,"Teleport verdict failed");ack.ReadByte();ack.ReadUInt32();
            Require(ack.ReadUInt16()==mapId && ack.ReadFloat()==x && ack.ReadFloat()==y && ack.ReadFloat()==z,"Client received different coordinates");
            Require(ch.MoneyTotal==0,"Free captured teleport charged money");
            passed.Add($"NPC {npcId} -> portal {portal}: list, request, World handoff, actor position and client verdict match capture");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{Passed=passed,ReadOnlySql=true,PlayerRowsModified=0,RealClientVerified=false}));
    }
    private sealed class Sink:IWorldSink,IClientChannel
    {public List<byte[]> Packets{get;}=new();public IPEndPoint? RemoteEndPoint=>null;public void Send(byte[] p)=>Packets.Add(p);public void Send(PacketWriter p)=>Packets.Add(p.ToArray());public void Close(){}}
}
