using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

internal static class MarkaSpawnCheck
{
    // SQL is read-only. Actors, forced-death fixture and quest counters exist only in this process.
    public static async Task RunAsync(string evidencePath, bool before)
    {
        var connection = Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var templates = await new GameDatabase(connection).LoadTemplatesAsync();
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
        var expected = evidence.RootElement.GetProperty(before ? "spawn_before" : "spawn_after");
        var spawn = templates.MonsterSpawns.Single(s => s.Spawn.Id == 5024);
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        Require(spawn.Spawn.PosX == expected.GetProperty("fPosX").GetSingle()
            && spawn.Spawn.PosY == expected.GetProperty("fPosY").GetSingle()
            && spawn.Spawn.PosZ == expected.GetProperty("fPosZ").GetSingle(), "SQL Marka placement differs");
        // Test the real loader and real spawn pipeline, isolating just this population in memory.
        templates.MonsterSpawns.RemoveAll(s => s.Spawn.Id != 5024);
        var state = new MapState(); var sink = new Sink();
        var service = new MapService(new MapServerOptions { Channels = new byte[] { 1 } }, state, sink, null, templates, NullLogger<MapService>.Instance);
        service.SpawnRng = new Random(1); service.LootRng = new Random(1);
        var node = evidence.RootElement.GetProperty("original_navigation").GetProperty("node");
        var ch = new Character { CharId = 1, Name = "MarkaFixture", Level = 8, Country = 4, Class = 3, Hp = 1000, MaxHp = 1000,
            MapId = 0, PosX = node.GetProperty("x").GetSingle(), PosY = node.GetProperty("y").GetSingle(), PosZ = node.GetProperty("z").GetSingle() };
        var session = new ClientSession(sink) { CharId = 1, UserId = 1, Key = 11, IsMain = true, Channel = 1, State = EnterState.InGame, Char = ch };
        state.Register(session); state.EnterWorld(session);
        var mission = templates.Quests[11027];
        var progress = new QuestProgress { Template = mission, TriggerCount = 1 };
        progress.RunningTerms.Add(new RunningTerm { TermId = 800, TermType = 3 }); ch.Quests[11027] = progress;
        // A test-only action exercises the normal credited death and respawn path.
        var kill = new QuestTemplate { QuestId = 4_000_000_000, Type = (byte)QuestType.DieMon, TriggerType = 1 };
        kill.Terms.Add(new QuestTerm(5024, 16, 0)); templates.Quests[kill.QuestId] = kill;
        service.InitQuests(); service.InitMonsterSpawns(); service.RunMonsterRegen(999);
        Require(!state.AllMonsters().Any(), "Existing initial delay was bypassed");
        service.RunMonsterRegen(1000);
        var mon = state.AllMonsters().Single();
        Require(mon.Id == Monster.MakeId(5024, 1, 0) && mon.ChartId == 800 && mon.Level == 10 && mon.Hp == mon.MaxHp, "Spawn identity/vitals differ");
        float Distance(Monster m) => MathF.Sqrt(MathF.Pow(m.PosX - ch.PosX, 2) + MathF.Pow(m.PosZ - ch.PosZ, 2));
        float distance = Distance(mon);
        if (before) Require(distance > 85 && mon.PosY - ch.PosY > 10, "Old misplaced spawn no longer reproduced");
        else
        {
            Require(distance <= spawn.Spawn.Range && mon.PosY == ch.PosY, "Marka is absent from pinned hunt location");
            Require(sink.Packets.Any(p => { var r = new PacketReader(p); return r.Id == Msg.CS_ADDMON_ACK && r.ReadUInt32() == mon.Id; }), "Marka not announced to client at hunt marker");
            await service.DispatchClientAsync(session, new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(kill.QuestId).WriteByte(0).WriteUInt32(0).ToArray());
            Require(progress.RunningTerms.Single().Count == 1, "Killing SQL-spawned Marka did not credit mission 11027");
            service.RunCorpseExpiry(30000); service.RunMonsterRegen(30999);
            Require(!state.AllMonsters().Any(), "Existing respawn delay was bypassed");
            service.RunMonsterRegen(31000); var next = state.AllMonsters().Single();
            Require(!next.Dead && next.Hp == next.MaxHp && Distance(next) <= spawn.Spawn.Range && next.PosY == ch.PosY, "Respawn lost repaired position");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ReadOnlySql = true, PlayerRowsModified = 0, BeforeRepair = before,
            SpawnId = 5024, MonsterId = 800, DistanceFromHuntMarker = distance, QuestCreditAndRespawnVerified = !before, RealClientVerified = false }));
    }
    private sealed class Sink : IWorldSink, IClientChannel
    { public List<byte[]> Packets { get; } = new(); public IPEndPoint? RemoteEndPoint => null; public void Send(byte[] p) => Packets.Add(p); public void Send(PacketWriter p) => Packets.Add(p.ToArray()); public void Close() { } }
}
