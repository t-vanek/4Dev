using Microsoft.Data.SqlClient;
using TMap.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;
using TMap.Protocol;

if (args.Length > 0 && args[0] == "--gor-navigation-journeys-readonly")
{
    await GorNavigationJourneyCheck.RunAsync(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "--gor-eggs-readonly")
{
    await GorEggPickupCheck.RunAsync(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "--marka-spawn-readonly")
{
    await MarkaSpawnCheck.RunAsync(args[1], args.Contains("--before"));
    return;
}

if (args.Length > 0 && args[0] == "--moray-defense-readonly")
{
    await MorayDefenseCheck.RunAsync(args[1], args.Contains("--before"));
    return;
}

if (args.Length > 0 && args[0] == "--moray-combat-readonly")
{
    await MorayCombatCheck.RunAsync(args[1], args.Contains("--before"));
    return;
}

if (args.Length > 0 && args[0] == "--gor-portals-lifecycle")
{
    await GorPortalLifecycleCheck.RunAsync(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "--gor-training-lifecycle")
{
    await GorTrainingLifecycleCheck.RunAsync(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "--gor-content-readonly")
{
    await GorContentLifecycleCheck.ReadOnlyAsync(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "--gor-content-lifecycle")
{
    await GorContentLifecycleCheck.RunAsync(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "--gor-reconstruction-readonly")
{
    await GorReconstructionLifecycleCheck.ReadOnlyProbeAsync();
    return;
}

if (args.Length > 0 && args[0] == "--gor-reconstruction-lifecycle")
{
    await GorReconstructionLifecycleCheck.RunAsync();
    return;
}

if (args.Length > 0 && args[0] == "--gor-pact-lifecycle")
{
    await GorPactLifecycleCheck.RunAsync();
    return;
}

if (args.Length > 0 && args[0].StartsWith("--full-", StringComparison.Ordinal))
{
    await FullSaveCheck.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0].StartsWith("--save-", StringComparison.Ordinal))
{
    await SaveRecoveryCheck.RunAsync(args);
    return;
}

// Deliberately refuse ordinary game databases: this check writes synthetic rows and fault constraints.
var connection = Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
var builder = new SqlConnectionStringBuilder(connection);
if (!builder.InitialCatalog.StartsWith("4DevQuestTest_", StringComparison.Ordinal))
    throw new InvalidOperationException("An isolated 4DevQuestTest_* database is required");
var map = new TMap.Data.GameDatabase(connection);
var world = new TWorld.Data.GameDatabase(connection);
await using var db = new SqlConnection(connection); await db.OpenAsync();
var passed = new List<string>();
async Task Exec(string sql) { await using var cmd = new SqlCommand(sql, db); await cmd.ExecuteNonQueryAsync(); }
async Task<long> Number(string sql) { await using var cmd = new SqlCommand(sql, db); return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
async Task MustFail(Func<Task> operation)
{
    try { await operation(); } catch (SqlException) { return; }
    throw new InvalidOperationException("Expected injected SQL failure");
}

await Exec("INSERT TCHARTABLE(dwCharID,szName) VALUES(1,'QuestFixture'); INSERT TQTITLECHART VALUES(100,'Fixture title','Fixture body','Fixture NPC');");
var item = new ItemSaveData(0, 2, 0, 0, 200, 7, 1, 2, 400, 350, 3, 0, 4,
    new byte[] { 1, 2, 0, 0, 0, 0 }, new ushort[] { 123, 456, 0, 0, 0, 0 }, new uint[] { 7, 8, 0, 0, 0, 0 }, 5, 201);
Require((await map.ReserveQuestPostAsync(100, 999, new[] { item })).Result == 1, "Missing recipient accepted");
Require((await map.ReserveQuestPostAsync(999, 1, new[] { item })).Result == 2, "Missing chart text accepted");
Require(await Number("SELECT COUNT(*) FROM TRESERVEDPOST") == 0, "Invalid reservation wrote rows");
passed.Add("invalid recipient and missing chart text reject without writes");

await Exec("ALTER TABLE TRESERVEDPOST ADD CONSTRAINT TestStack CHECK (bCount<=10)");
await MustFail(async () => { await map.ReserveQuestPostAsync(100, 1, new[] { item, item with { Count = 11 } }); });
Require(await Number("SELECT COUNT(*) FROM TRESERVEDPOST") == 0, "Partial reward survived rollback");
await Exec("ALTER TABLE TRESERVEDPOST DROP CONSTRAINT TestStack");
passed.Add("multi-attachment reservation rolls back after second-stack failure");

Require((await map.ReserveQuestPostAsync(100, 1, new[] { item, item with { Count = 2 } })).Result == 0, "Reservation rejected");
Require(await Number("SELECT COUNT(*) FROM TRESERVEDPOST WHERE bSend=0 AND bGradeEffect=4 AND bGem=5 AND wMoggItemID=201") == 2, "Item attributes lost");
passed.Add("normal and magic attachment fields retained in durable reservations");

// Exercise independent World instances competing for the same durable queue.
int delivered = 0;
for (int attempt = 0; attempt < 10 && delivered < 2; attempt++)
{
    var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => new TWorld.Data.GameDatabase(connection).DeliverReservedPostAsync()));
    delivered += results.Count(r => r.Result == 0);
}
Require(delivered == 2 && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == 2, "Duplicate/missing mail under concurrency");
Require(await Number("SELECT COUNT(DISTINCT dlID) FROM TITEMTABLE") == 2, "Item IDs collided");
Require(await Number("SELECT COUNT(*) FROM TITEMTABLE WHERE bStorageType=2 AND dwOwnerID=1 AND bGradeEffect=4 AND bMagic2=2 AND wValue2=456 AND dwTime2=8 AND bGem=5 AND wMoggItemID=201") == 2, "Delivered attributes lost");
Require(await Number("SELECT COUNT(*) FROM TPOSTTABLE WHERE bContain=1 AND szTitle='0000000dFixture title' AND szMessage='0000000cFixture body'") == 2, "Mail text framing/attachment flag lost");
Require((await world.DeliverReservedPostAsync()).Result == 1, "Delivered reservation replayed");
passed.Add("concurrent World consumers deliver exactly one mail per reserved stack, including offline recipient");

Require((await map.ReserveQuestPostAsync(100, 1, new[] { item with { Count = 3 } })).Result == 0, "Reservation failed");
await Exec("ALTER TABLE TITEMTABLE ADD CONSTRAINT TestAttachment CHECK (bCount<3)");
await MustFail(async () => { await world.DeliverReservedPostAsync(); });
Require(await Number("SELECT COUNT(*) FROM TPOSTTABLE") == 2, "Orphan header committed");
Require(await Number("SELECT COUNT(*) FROM TRESERVEDPOST WHERE bSend=0") == 1, "Failed attachment marked delivered");
await Exec("ALTER TABLE TITEMTABLE DROP CONSTRAINT TestAttachment");
Require((await world.DeliverReservedPostAsync()).Result == 0, "Rolled-back reservation cannot retry");
passed.Add("failed attachment rolls mail/header/queue state back and can retry");

var saved = new QuestSaveRow(1000, 15000, 0, 1, new[] { new QuestTermSaveRow(500, 3, 2), new QuestTermSaveRow(501, 13, 1) });
await map.SaveQuestsAsync(1, new[] { saved });
var loaded = await new TMap.Data.GameDatabase(connection).LoadQuestsAsync(1);
Require(loaded.quests.Single(q => q.QuestId == 1000) == new QuestLoadRow(1000, 15000, 0, 1), "Quest header roundtrip failed");
Require(loaded.terms.Count == 2 && loaded.terms.Single(t => t.TermId == 500).Count == 2, "Term roundtrip failed");
passed.Add("new connection reloads mission timer, counts and objectives");

await Exec("ALTER TABLE TQUESTTERMTABLE ADD CONSTRAINT TestProgress CHECK (bCount<3)");
await MustFail(() => map.SaveQuestsAsync(1, new[] { saved with { CompleteCount = 1, Tick = 0,
    Terms = new[] { new QuestTermSaveRow(500, 3, 3) } } }));
loaded = await map.LoadQuestsAsync(1);
Require(loaded.quests.Single(q => q.QuestId == 1000).CompleteCount == 0 && loaded.quests.Single(q => q.QuestId == 1000).Tick == 15000, "Header advanced without terms");
Require(loaded.terms.Single(t => t.TermId == 500).Count == 2, "Term did not roll back");
await Exec("ALTER TABLE TQUESTTERMTABLE DROP CONSTRAINT TestProgress");
passed.Add("quest header and terms are one transaction under injected failure");

await map.SaveQuestsAsync(1, new[] { saved with { CompleteCount = 1, Tick = 0,
    Terms = new[] { new QuestTermSaveRow(500, 3, 0), new QuestTermSaveRow(501, 13, 0) } } });
loaded = await map.LoadQuestsAsync(1);
Require(loaded.quests.Single(q => q.QuestId == 1000).CompleteCount == 1 && loaded.terms.All(t => t.Count == 0), "Completed mission failed reload");
passed.Add("completed mission and reset objectives survive a second database roundtrip");

await Exec("INSERT TQTITLECHART VALUES(200,'Once title','Once body','Fixture NPC'),(201,'Twice title','Twice body','Fixture NPC'),(202,'Rollback title','Rollback body','Fixture NPC'),(203,'Repeat title','Repeat body','Fixture NPC')");
long before = await Number("SELECT COUNT(*) FROM TRESERVEDPOST");
var issued = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new TMap.Data.GameDatabase(connection)
    .ReserveQuestPostAsync(200, 1, new[] { item }, countMax: 1)));
Require(issued.Count(r => r.Result == 0) == 1 && issued.Count(r => r.Result == QuestPostResult.AlreadyIssued) == 3,
    "Concurrent Map instances issued a capped reward twice");
Require(await Number("SELECT COUNT(*) FROM TRESERVEDPOST") == before + 1, "Duplicate reservation survived cap");
loaded = await new TMap.Data.GameDatabase(connection).LoadQuestsAsync(1);
Require(loaded.quests.Single(q => q.QuestId == 200) == new QuestLoadRow(200, 0, 1, 1), "Issuance progress did not reload");
Require((await new TMap.Data.GameDatabase(connection).ReserveQuestPostAsync(200, 1, new[] { item }, 1)).Result == QuestPostResult.AlreadyIssued,
    "A new data-store instance replayed a completed mail quest");
passed.Add("CountMax=1 survives reload and concurrent Map issuers without duplicate reservation");

var first = await map.ReserveQuestPostAsync(201, 1, new[] { item }, 2);
var second = await map.ReserveQuestPostAsync(201, 1, new[] { item }, 2);
var third = await map.ReserveQuestPostAsync(201, 1, new[] { item }, 2);
Require(first == new QuestPostResult(0, 1) && second == new QuestPostResult(0, 2)
    && third == new QuestPostResult(QuestPostResult.AlreadyIssued, 2), "CountMax>1 not honored");
passed.Add("chart repetition cap allows exactly its configured number of issuances");

before = await Number("SELECT COUNT(*) FROM TRESERVEDPOST");
await Exec("ALTER TABLE TQUESTTABLE ADD CONSTRAINT TestIssuance CHECK (dwQuestID<>202)");
await MustFail(async () => { await map.ReserveQuestPostAsync(202, 1, new[] { item }, 1); });
Require(await Number("SELECT COUNT(*) FROM TRESERVEDPOST") == before, "Mail committed without its replay counter");
Require(await Number("SELECT COUNT(*) FROM TQUESTTABLE WHERE dwQuestID=202") == 0, "Failed counter persisted");
await Exec("ALTER TABLE TQUESTTABLE DROP CONSTRAINT TestIssuance");
Require((await map.ReserveQuestPostAsync(202, 1, new[] { item }, 1)) == new QuestPostResult(0, 1), "Failed issuance cannot retry");
passed.Add("progress-write failure rolls attachments back and preserves retry eligibility");

await Exec("INSERT TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(1,203,0,255,255)");
Require((await map.ReserveQuestPostAsync(203, 1, new[] { item })) == new QuestPostResult(0, 1), "Repeatable mail counter overflowed");
Require((await map.ReserveQuestPostAsync(203, 1, new[] { item })) == new QuestPostResult(0, 2), "Uncapped mail became blocked");
passed.Add("uncapped quests remain repeatable across byte-counter wrap");

// Exercise runtime snapshot/load as well as SQL: repeated saves must keep a decaying countdown,
// and an expired row must load failed in a fresh service, including at map clock zero.
var timerTemplate = new QuestTemplate { QuestId = 1001, Type = (byte)QuestType.Mission };
timerTemplate.Terms.Add(new QuestTerm(1000, 6, 0));
MapService TimerService()
{
    var templates = new TemplateStore(); templates.Quests[1001] = timerTemplate;
    return new MapService(new MapServerOptions(), new MapState(), new DiscardWorld(), null, templates,
        NullLogger<MapService>.Instance);
}
var firstTimerService = TimerService(); var timed = new Character { CharId = 1 };
firstTimerService.LoadQuestProgress(timed, new[] { new QuestLoadRow(1001, 1000, 0, 1) }, Array.Empty<QuestTermLoadRow>());
await map.SaveQuestsAsync(1, MapService.BuildQuestSaves(timed, 300));
var timerRows = await map.LoadQuestsAsync(1);
Require(timerRows.quests.Single(q => q.QuestId == 1001).Tick == 700, "Timer's first save lost remaining time");
await map.SaveQuestsAsync(1, MapService.BuildQuestSaves(timed, 700)); // no new objective/dirty flag
timerRows = await new TMap.Data.GameDatabase(connection).LoadQuestsAsync(1);
Require(timerRows.quests.Single(q => q.QuestId == 1001).Tick == 300, "Second save reused stale timer state");
await map.SaveQuestsAsync(1, MapService.BuildQuestSaves(timed, 1001));
timerRows = await new TMap.Data.GameDatabase(connection).LoadQuestsAsync(1);
var freshTimerService = TimerService(); var reloaded = new Character { CharId = 1 };
freshTimerService.LoadQuestProgress(reloaded, timerRows.quests, timerRows.terms);
Require(reloaded.Quests[1001].TimerExpired && reloaded.Quests[1001].RemainingTimer(0) == 0,
    "Expired persisted timer was revived in a fresh Map service");
passed.Add("runtime timer snapshots decay on successive SQL saves and reload expired in a fresh Map service");
await QuestAdmissionCheck.RunAsync(map, connection, passed);
await CharacterStateCheck.RunAsync(map, connection, passed);
Console.WriteLine(JsonSerializer.Serialize(new { database = builder.InitialCatalog, passed }, new JsonSerializerOptions { WriteIndented = true }));

sealed class DiscardWorld : IWorldSink
{
    public void Send(byte[] packet) { }
    public void Send(PacketWriter writer) { }
}
