// Real SQL template loader and production packet handlers, with ephemeral characters and no persistence store.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using TMap.Server.Tests;

if (args.Length != 4 || !Regex.IsMatch(args[0], @"^TGame_(?:gsp|ContentStage_[0-9_]+)$"))
    throw new ArgumentException("Expected explicit database, EXP source, region plan and output path.");
var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("Map__Db__GameConnectionString")
    ?? throw new ArgumentException("Run in the local server container with its configured SQL connection."))
    { InitialCatalog = args[0], ConnectTimeout = 5 };
var templates = await new GameDatabase(connection.ConnectionString).LoadTemplatesAsync();
using var expDocument = JsonDocument.Parse(File.ReadAllBytes(args[1]));
using var regionDocument = JsonDocument.Parse(File.ReadAllBytes(args[2]));
var monsterRestores = new List<object>();
if (regionDocument.RootElement.TryGetProperty("monster_restores", out var restores))
    foreach (var row in restores.EnumerateArray())
    {
        ushort id = row.GetProperty("id").GetUInt16();
        var monster = templates.MonsterTemplates[id];
        var attr = templates.MonAttr(monster.MonAttr, monster.Level)
            ?? throw new InvalidDataException($"Missing monster attribute {id}.");
        if (monster.Level != row.GetProperty("level").GetByte() || monster.MonAttr != row.GetProperty("new_attr").GetUInt16()
            || attr.MaxHp != row.GetProperty("hp").GetUInt32())
            throw new InvalidDataException($"Loaded monster level/HP differs from the import plan: {id}.");
        monsterRestores.Add(new { id, monster.Level, attr.MaxHp, passed = true });
    }
var monsterFields = new List<object>();
if (regionDocument.RootElement.TryGetProperty("changes", out var changes))
    foreach (var row in changes.EnumerateArray())
    {
        ushort id = row.GetProperty("key").GetUInt16();
        var monster = templates.MonsterTemplates[id];
        string field = row.GetProperty("column").GetString()!;
        // fSize is consumed by the client; it is checked by the complete SQL-row comparison.
        uint? actual = field switch { "wSkill1" => monster.Skill1, "wSkill2" => monster.Skill2,
            "wSkill3" => monster.Skill3, "wSkill4" => monster.Skill4, "bCanSelect" => monster.CanSelect,
            "bCanAttack" => monster.CanAttack, "bTame" => monster.Tame, _ => null };
        if (actual is not null && actual != row.GetProperty("after").GetUInt32())
            throw new InvalidDataException($"Loaded monster field differs: {id}.{field}.");
        monsterFields.Add(new { id, field, loadedByServer = actual is not null, passed = true });
    }
var published = expDocument.RootElement.GetProperty("rows").EnumerateArray()
    .ToDictionary(r => r[0].GetInt32(), r => r[1].GetUInt32());
foreach (var (level, exp) in published)
    if (templates.LevelExpOf(level - 1) != exp) throw new InvalidDataException($"Wrong EXP to reach {level}.");
if (templates.LevelExpOf(140) != 0) throw new InvalidDataException("Missing maximum-level sentinel.");

Character Hero(string name) { var ch = new Character { CharId = 1, Name = name, MaxHp = 100, Hp = 100 }; ch.Invens.Add(new Inven { InvenId = 0xFF }); return ch; }
Task Award(MapTestHarness h, uint gain) => h.Service.DispatchWorldAsync(new PacketWriter(Msg.MW_CMGIFT_REQ)
    .WriteUInt32(1).WriteByte(5).WriteUInt32(gain).WriteByte(1).WriteString("EXP probe").WriteString("")
    .WriteUInt32(0).WriteByte(1).WriteUInt16(0).WriteUInt16(0).WriteByte(0).ToArray());
var boundaries = new List<object>();
var loginBounds = new List<object>();
foreach (byte level in new byte[] { 126, 140 })
{
    var hero = Hero("LoginProbe"); hero.Level = level; hero.Exp = published[level];
    var h = new MapTestHarness(templates);
    var (_, client) = await h.EnterAsync(1, 1, 1, preSeeded: hero);
    var r = new PacketReader(client.Last(Msg.CS_CHARINFO_ACK)!);
    r.ReadUInt32(); for (int i = 0; i < 3; i++) r.ReadByte(); r.ReadUInt16(); r.ReadString();
    for (int i = 0; i < 14; i++) r.ReadByte();
    r.ReadUInt16(); for (int i = 0; i < 3; i++) r.ReadUInt32();
    r.ReadByte(); r.ReadByte(); r.ReadString(); r.ReadUInt32(); r.ReadString();
    for (int i = 0; i < 3; i++) r.ReadUInt32();
    uint floor = r.ReadUInt32(), ceiling = r.ReadUInt32(), current = r.ReadUInt32();
    if (floor != published[level] || ceiling != (templates.LevelExpOf(level) ?? 0) || current != published[level])
        throw new InvalidDataException("Login EXP bounds differ from the loaded publisher curve.");
    loginBounds.Add(new { level, floor, ceiling, current, passed = true });
}
for (byte target = 126; target <= 140; target++)
{
    var hero = Hero("BoundaryProbe");
    var harness = new MapTestHarness(templates);
    await harness.EnterAsync(1, 1, 1, preSeeded: hero);
    hero.Level = (byte)(target - 1); hero.Exp = published[target] - 2;
    await Award(harness, 1);
    if (hero.Level != target - 1 || hero.Exp != published[target] - 1) throw new InvalidDataException("Early level-up.");
    await Award(harness, 1);
    if (hero.Level != target || hero.Exp != published[target] || hero.SoulLotExp > 11_000_000)
        throw new InvalidDataException("Wrong level boundary or underflowed soul bar.");
    boundaries.Add(new { target, threshold = hero.Exp, passed = true });
}
var maxHero = Hero("OverflowProbe"); var maxHarness = new MapTestHarness(templates);
var (maxSession, maxClient) = await maxHarness.EnterAsync(1, 1, 1, preSeeded: maxHero);
maxHero.Level = 125; maxHero.Exp = published[125];
await Award(maxHarness, uint.MaxValue);
if (maxHero.Level != 140 || maxHero.Exp != published[140] || maxHero.SoulLotExp != 0) throw new InvalidDataException("EXP overflow/cap failed.");
await Award(maxHarness, uint.MaxValue); maxClient.Clear();
await maxHarness.Service.DispatchClientAsync(maxSession, new PacketWriter(Msg.CS_SOULLOTTERY_REQ).ToArray());
if (maxHero.Level != 140 || maxHero.Exp != published[140] || maxClient.Has(Msg.CS_SOULLOTTERY_ACK)) throw new InvalidDataException("Zero-width maximum-level lottery.");

var deliveries = new List<object>();
foreach (var record in regionDocument.RootElement.GetProperty("supported_deliveries").EnumerateArray())
{
    uint id = record.GetProperty("mission").GetProperty("dwQuestID").GetUInt32();
    uint completeId = record.GetProperty("complete").GetProperty("dwQuestID").GetUInt32();
    var mission = templates.Quests[id]; var completion = templates.Quests[completeId];
    var giver = templates.Npcs[(ushort)record.GetProperty("giver").GetUInt32()];
    var turnin = templates.Npcs[(ushort)record.GetProperty("turnin").GetUInt32()];
    byte required = (byte)mission.Conditions.Single(c => c.ConditionType == 1).ConditionId;
    var hero = Hero("DeliveryProbe"); var h = new MapTestHarness(templates);
    var (s, c) = await h.EnterAsync(1, 1, 1, mapId: giver.MapId, x: giver.PosX, y: giver.PosY, z: giver.PosZ, preSeeded: hero);
    hero.Country = giver.Country <= 2 ? giver.Country : (byte)1; hero.AidCountry = hero.Country;
    hero.Level = required; hero.Exp = templates.LevelExpOf(required - 1) ?? 0;
    h.Service.InitNpcs(); h.Service.InitQuests(); c.Clear();
    if (mission.ParentId != 0)
    {
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(id));
        if (hero.IsRunningQuest(id)) throw new InvalidDataException("Prerequisite bypass.");
        hero.Quests[mission.ParentId] = new QuestProgress { Template = templates.Quests[mission.ParentId], TriggerCount = 1, CompleteCount = 1 };
    }
    hero.Level = (byte)(required - 1);
    await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(id));
    if (hero.IsRunningQuest(id)) throw new InvalidDataException("Minimum level bypass.");
    hero.Level = required;
    await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(id));
    if (!hero.IsRunningQuest(id) || !c.Has(Msg.CS_QUESTADD_ACK)) throw new InvalidDataException($"Delivery {id} cannot be accepted.");
    hero.MapId = turnin.MapId; hero.PosX = turnin.PosX; hero.PosY = turnin.PosY; hero.PosZ = turnin.PosZ;
    uint beforeExp = hero.Exp; long beforeMoney = hero.MoneyTotal;
    uint rewardExp = checked((uint)mission.Rewards.Where(r => r.RewardType == 6).Sum(r => (long)r.RewardId));
    long rewardMoney = mission.Rewards.Where(r => r.RewardType == 1).Sum(r => (long)r.RewardId);
    c.Clear();
    await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(completeId));
    var ack = new PacketReader(c.Last(Msg.CS_QUESTCOMPLETE_ACK) ?? throw new InvalidDataException("No completion packet."));
    if (ack.ReadByte() != (byte)QuestResult.Success || ack.ReadUInt32() != id || hero.FindQuest(id)!.CompleteCount != 1
        || hero.Exp != beforeExp + rewardExp || hero.MoneyTotal != beforeMoney + rewardMoney)
        throw new InvalidDataException($"Delivery {id} reward/completion failed.");
    c.Clear();
    await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(completeId));
    await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(id));
    if (hero.Exp != beforeExp + rewardExp || hero.MoneyTotal != beforeMoney + rewardMoney || hero.IsRunningQuest(id))
        throw new InvalidDataException("Repeated story reward.");
    deliveries.Add(new { id, completeId, required, rewardExp, rewardMoney, prerequisiteChecked = true, repeatBlocked = true });
}

var purchases = new List<object>();
foreach (var shop in regionDocument.RootElement.GetProperty("shops_imported").EnumerateArray())
{
    ushort npcId = shop.GetProperty("npc_id").GetUInt16(); var npc = templates.Npcs[npcId];
    foreach (var stock in shop.GetProperty("stock").EnumerateArray())
    {
        ushort itemId = stock.GetProperty("item_id").GetUInt16(); long price = stock.GetProperty("price").GetInt64();
        var hero = Hero("ShopProbe"); var h = new MapTestHarness(templates);
        var (s, c) = await h.EnterAsync(1, 1, 1, mapId: npc.MapId, x: npc.PosX, y: npc.PosY, z: npc.PosZ, preSeeded: hero);
        hero.Level = 140; hero.Country = 1; hero.AidCountry = 1; hero.SetMoneyTotal(price);
        h.Service.InitNpcs(); c.Clear();
        await h.Service.DispatchClientAsync(s, MapTestHarness.ItemBuyReq(npcId, itemId, 1));
        if (hero.MoneyTotal != 0 || hero.Invens.Sum(b => b.Items.Where(i => i.TemplateId == itemId).Sum(i => i.Count)) != 1)
            throw new InvalidDataException($"Shop {npcId} price/stock failed for {itemId}.");
        purchases.Add(new { npcId, itemId, price, passed = true });
    }
}
var report = new { database = args[0], passed = true, boundaries, loginBounds, largeRewardAndCapPassed = true,
    deliveries, purchases, monsterRestores, monsterFields, writesToSql = 0, graphicalPlaythrough = false,
    expSourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))).ToLowerInvariant(),
    regionPlanSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))).ToLowerInvariant(),
    serverDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MapService).Assembly.Location))).ToLowerInvariant() };
File.WriteAllText(args[3], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { report.database, report.passed, boundaries = boundaries.Count,
    loginBounds = loginBounds.Count, deliveries = deliveries.Count, purchases = purchases.Count, report.writesToSql }));
