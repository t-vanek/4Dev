using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

internal static class MorayCombatCheck
{
    // Only chart SELECTs reach SQL. Synthetic actors and hit reports stay in this process.
    public static async Task RunAsync(string evidencePath, bool before)
    {
        var connection = Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var templates = await new GameDatabase(connection).LoadTemplatesAsync();
        using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var state = new MapState(); var world = new Sink(); var client = new Sink();
        var service = new MapService(new MapServerOptions { Channels = new byte[] { 1 } }, state, world, null, templates, NullLogger<MapService>.Instance);
        service.SpawnRng = new Random(1); service.CombatRng = new Random(1);
        service.InitMonsterSpawns(); service.RunMonsterRegen(60000);
        var mon = state.FindMonster(Monster.MakeId(32404, 1, 0)) ?? throw new InvalidOperationException("Training Moray did not spawn from SQL");
        Require(mon.ChartId == 801 && mon.Level == 10 && mon.MaxHp == 711, "Moray identity/HP drifted");
        var skill = templates.Skill(700) ?? throw new InvalidOperationException("Owned skill missing");
        var power = AttackerPower.Of(mon, skill);
        if (before)
            Require(power.PysMin == 350 && power.PysMax == 350 && power.AttackLevel == 250, "Legacy defect no longer reproduced");
        else
        {
            foreach (var observation in proof.RootElement.GetProperty("observations").EnumerateArray())
            {
                var expected = observation.GetProperty("decoded");
                Require(power.PysMin == expected.GetProperty("physical_min").GetUInt32() && power.PysMax == expected.GetProperty("physical_max").GetUInt32()
                    && power.MgMin == expected.GetProperty("magic_min").GetUInt32() && power.MgMax == expected.GetProperty("magic_max").GetUInt32()
                    && power.AttackLevel == expected.GetProperty("attack_level").GetUInt16() && power.Crit == expected.GetProperty("critical_probability").GetByte(), "SQL-spawned power differs from original wire capture");
            }
            Require(mon.MaxMp == 485, "MP differs from original appearance");
        }
        var ch = new Character { CharId = 1, Name = "MorayFixture", Level = 10, Class = 3, Country = 4, Hp = 404, MaxHp = 404, Mp = 593, MaxMp = 593,
            MapId = mon.MapId, PosX = mon.PosX, PosY = mon.PosY, PosZ = mon.PosZ };
        var session = new ClientSession(client) { CharId = 1, UserId = 1, Key = 11, IsMain = true, Channel = 1, State = EnterState.InGame, Char = ch };
        state.Register(session); state.EnterWorld(session);
        var damages = new List<uint>();
        for (int i = 0; i < 32; i++)
        {
            ch.Hp = 404; client.Packets.Clear();
            await service.DispatchClientAsync(session, Hit(mon, ch));
            var packet = client.Packets.LastOrDefault(p => new PacketReader(p).Id == Msg.CS_DEFEND_ACK) ?? throw new InvalidOperationException("Native monster hit report was rejected");
            var r = new PacketReader(packet);
            Require(r.ReadUInt32() == mon.Id && r.ReadUInt32() == ch.CharId, "Wrong hit actors");
            r.ReadByte(); r.ReadByte(); r.ReadUInt32(); r.ReadByte(); r.ReadUInt32(); r.ReadUInt32(); r.ReadByte(); r.ReadUInt32();
            byte crit = r.ReadByte(); r.ReadByte(); ushort al = r.ReadUInt16(); byte level = r.ReadByte();
            Require(crit == power.Crit && al == power.AttackLevel && level == 10 && r.ReadUInt32() == power.PysMin && r.ReadUInt32() == power.PysMax
                && r.ReadUInt32() == power.MgMin && r.ReadUInt32() == power.MgMax, "Hit ACK did not use authoritative SQL power");
            for (int j = 0; j < 4; j++) r.ReadByte();
            Require(r.ReadUInt16() == 700 && r.ReadByte() == 1, "Owned skill or level changed");
            r.ReadUInt16(); r.ReadByte(); for (int j = 0; j < 6; j++) r.ReadFloat();
            int count = r.ReadByte(); uint damage = 0;
            for (int j = 0; j < count; j++) { byte exec = r.ReadByte(); uint value = r.ReadUInt32(); if (exec == 30) damage += value; }
            Require(ch.Hp == (damage >= 404 ? 0 : 404 - damage), "HP and reported damage diverged");
            damages.Add(damage);
        }
        Require(damages.Any(d => d > 0), "No hits actually landed");
        Console.WriteLine(JsonSerializer.Serialize(new { ReadOnlySql = true, PlayerRowsModified = 0, BeforeRepair = before,
            Physical = new[] { power.PysMin, power.PysMax }, Magic = new[] { power.MgMin, power.MgMax }, power.AttackLevel,
            Hits = damages.Count, FixtureDamageMin = damages.Min(), FixtureDamageMax = damages.Max(), RealClientVerified = false }));
    }

    private static byte[] Hit(Monster m, Character ch)
    {
        var w = new PacketWriter(Msg.CS_DEFEND_REQ);
        w.WriteUInt32(1); w.WriteUInt32(m.Id); w.WriteUInt32(ch.CharId); w.WriteByte(2); w.WriteByte(1); w.WriteUInt16(0);
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteByte(1); w.WriteUInt16(ch.MapId); w.WriteByte(10);
        // Deliberately forged strength: the server must derive the SQL monster's power.
        for (int i = 0; i < 4; i++) w.WriteUInt32(999999);
        w.WriteUInt16(0); w.WriteUInt16(0); w.WriteByte(0); w.WriteByte(0); w.WriteByte(1); w.WriteByte(3); w.WriteByte(3);
        w.WriteUInt16(65535); w.WriteByte(100); w.WriteUInt16(700); w.WriteByte(99);
        w.WriteFloat(m.PosX); w.WriteFloat(m.PosY); w.WriteFloat(m.PosZ); w.WriteFloat(ch.PosX); w.WriteFloat(ch.PosY); w.WriteFloat(ch.PosZ); w.WriteUInt32(0);
        return w.ToArray();
    }
    private sealed class Sink : IWorldSink, IClientChannel
    { public List<byte[]> Packets { get; } = new(); public IPEndPoint? RemoteEndPoint => null; public void Send(byte[] p) => Packets.Add(p); public void Send(PacketWriter p) => Packets.Add(p.ToArray()); public void Close() { } }
}
