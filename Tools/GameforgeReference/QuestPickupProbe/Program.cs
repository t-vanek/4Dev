using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using TMap.Server.Tests;

if (args.Length != 4 || args[3] is not ("current" or "legacy"))
    throw new ArgumentException("Usage: QuestPickupProbe REPO CASES.json NEW-OUTPUT.json current|legacy");
if (File.Exists(args[2])) throw new IOException("Choose a new output file to preserve earlier evidence.");
bool legacy = args[3] == "legacy";
var cases = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement.EnumerateArray()
    .ToDictionary(x => x.GetProperty("quest").GetUInt32(), x => x.Clone());
string secret = File.ReadLines(Path.Combine(args[0], ".env")).Single(x => x.StartsWith("SA_PASSWORD="));
var connection = new SqlConnectionStringBuilder { DataSource = "127.0.0.1,11433", InitialCatalog = "TGame_gsp", UserID = "sa",
    Password = secret["SA_PASSWORD=".Length..].Trim().Trim('\'', '"'), TrustServerCertificate = true, Encrypt = false };
var templates = await new GameDatabase(connection.ConnectionString).LoadTemplatesAsync();
var routes = templates.Quests.Values.Where(q => q.Type == (byte)QuestType.Routing).OrderBy(q => q.QuestId).ToList();
if (!routes.Select(q => q.QuestId).SequenceEqual(cases.Keys.Order())) throw new InvalidOperationException("Routing snapshot changed.");
var h = new MapTestHarness(templates);
var hero = new Character { CharId = 1, Class = 3, Race = 0, Level = 13, Country = 0, AidCountry = 3,
    MaxHp = 1000, Hp = 1000, MaxMp = 1000, Mp = 1000 };
hero.Invens.Add(new Inven { InvenId = 0xFF });
var (session, client) = await h.EnterAsync(1, 1, 1, preSeeded: hero);
h.Service.InitNpcs(); h.Service.InitQuests();
var results = new List<PickupResult>();
foreach (var quest in routes)
{
    var metadata = cases[quest.QuestId];
    var row = new PickupResult { Quest = quest.QuestId, Parent = quest.ParentId, Npc = (ushort)quest.TriggerId,
        Category = metadata.GetProperty("category").GetString()! };
    results.Add(row);
    var term = quest.Terms.SingleOrDefault(t => t.TermType == 5);
    row.Item = term?.TermId ?? 0;
    if (term is null || term.TermId == 0 || !templates.Items.ContainsKey((ushort)term.TermId))
    { row.Status = "placeholder_or_missing_item"; continue; }
    if (quest.TriggerType != 3 || !templates.Npcs.TryGetValue(row.Npc, out var npc))
    { row.Status = "missing_npc_or_trigger"; continue; }
    ushort item = (ushort)term.TermId;
    Reset(quest, npc);
    client.Clear(); await h.Service.DispatchClientAsync(session, MapTestHarness.QuestListPossibleReq(npc.Id));
    row.Offered = Offers().Contains((quest.QuestId, (byte)0));
    if (!row.Offered) { row.Status = "eligibility_blocked_in_fixture"; continue; }
    client.Clear(); await h.Service.DispatchClientAsync(session, MapTestHarness.QuestExecReq(quest.QuestId));
    if (client.Last(Msg.CS_NPCITEMLIST_ACK) is not { } packet) { row.Status = "pickup_list_missing"; continue; }
    var list = new PacketReader(packet);
    row.ListCorrect = list.ReadUInt16() == npc.Id && list.ReadByte() == 7;
    list.ReadByte(); byte count = list.ReadByte(); var listed = new List<ushort>();
    row.ListCorrect &= list.Remaining == count * 2;
    for (int i = 0; i < count; i++) listed.Add(list.ReadUInt16()); // TNPC_BOX rows contain only WORD item IDs.
    row.ListCorrect &= listed.SequenceEqual(quest.Terms.Where(t => t.TermType == 5).Select(t => (ushort)t.TermId));
    client.Clear(); await h.Service.DispatchClientAsync(session, MapTestHarness.ItemBuyReq(npc.Id, item, 1, questId: quest.QuestId));
    row.PickupResultCode = ResultCode(item);
    row.ItemCount = hero.Invens.SelectMany(b => b.Items).Where(i => i.TemplateId == item).Sum(i => i.Count);
    if (legacy)
    {
        row.Status = row.PickupResultCode == (byte)ItemBuyResult.NotFound && row.ItemCount == 0
            ? "legacy_missing_item_reproduced" : "legacy_result_differs";
        continue;
    }
    if (row.PickupResultCode != (byte)ItemBuyResult.Success || row.ItemCount != 1 || hero.MoneyTotal != 0 || !row.ListCorrect)
    { row.Status = "pickup_failed"; continue; }
    if (templates.Quests.TryGetValue(quest.ParentId, out var parent) && parent.Terms.Any(t => t.TermType == 2 && t.TermId == item))
        row.ObjectiveUpdated = client.WithId(Msg.CS_QUESTUPDATE_ACK).Any(p =>
        {
            var r = new PacketReader(p);
            return r.ReadUInt32() == parent.QuestId && r.ReadUInt32() == item && r.ReadByte() == 2 && r.ReadByte() == 1;
        });
    client.Clear(); await h.Service.DispatchClientAsync(session, MapTestHarness.ItemBuyReq(npc.Id, item, 1, questId: quest.QuestId));
    row.RepeatBlocked = ResultCode(item) == (byte)ItemBuyResult.NotFound
        && hero.Invens.SelectMany(b => b.Items).Where(i => i.TemplateId == item).Sum(i => i.Count) == 1;
    Reset(quest, npc); hero.Quests.Clear();
    client.Clear(); await h.Service.DispatchClientAsync(session, MapTestHarness.ItemBuyReq(npc.Id, item, 1, questId: quest.QuestId));
    row.MissingPrerequisiteBlocked = ResultCode(item) == (byte)ItemBuyResult.NotFound && Empty();
    Reset(quest, npc); hero.MapId = npc.MapId == 0 ? (ushort)1 : (ushort)0;
    client.Clear(); await h.Service.DispatchClientAsync(session, MapTestHarness.ItemBuyReq(npc.Id, item, 1, questId: quest.QuestId));
    row.WrongMapBlocked = ResultCode(item) == (byte)ItemBuyResult.NotFound && Empty();
    Reset(quest, npc);
    var otherNpc = templates.Npcs.Values.First(n => n.Id != npc.Id && n.Country == 3 && n.LocalId == 0);
    client.Clear(); await h.Service.DispatchClientAsync(session, MapTestHarness.ItemBuyReq(otherNpc.Id, item, 1, questId: quest.QuestId));
    row.WrongNpcBlocked = ResultCode(item) == (byte)ItemBuyResult.NotFound && Empty();
    row.Status = row.RepeatBlocked && row.MissingPrerequisiteBlocked && row.WrongMapBlocked && row.WrongNpcBlocked
        && row.ObjectiveUpdated != false ? "passed" : "guard_or_objective_failed";
}
var summary = results.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
var report = new { Mode = args[3], DatabaseWrites = 0, Templates = routes.Count, Summary = summary, Results = results,
    MapServerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MapService).Assembly.Location))),
    Limits = "Actual SQL templates and server DLL; prerequisites seeded in memory, including disabled legacy parent records. Validates pickup protocol and guards, not real-world reachability or complete quest playthroughs." };
File.WriteAllText(args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { Mode = args[3], Templates = routes.Count, Summary = summary }));
if (results.Any(r => r.Status is not ("passed" or "legacy_missing_item_reproduced" or "placeholder_or_missing_item" or "eligibility_blocked_in_fixture")))
    throw new InvalidOperationException("Pickup audit found failures; inspect the saved report.");

void Reset(QuestTemplate q, NpcDef npc)
{
    hero.Quests.Clear(); hero.LevelQuest.Clear(); foreach (var bag in hero.Invens) bag.Items.Clear();
    hero.Class = 3; hero.Level = 13; hero.Country = npc.Country == 3 ? (byte)0 : npc.Country; hero.AidCountry = 3;
    hero.MapId = npc.MapId; hero.PosX = npc.PosX; hero.PosY = npc.PosY; hero.PosZ = npc.PosZ; hero.SetMoneyTotal(0);
    void Seed(uint id, bool complete, byte minimum = 1)
    {
        if (id == 0 || !templates.Quests.TryGetValue(id, out var template)) return;
        byte count = Math.Max((byte)1, minimum);
        hero.Quests[id] = new QuestProgress { Template = template, TriggerCount = count, CompleteCount = complete ? count : (byte)0 };
    }
    Seed(q.ParentId, false);
    foreach (var c in q.Conditions)
        if (c.ConditionType == 3) Seed(c.ConditionId, false);
        else if (c.ConditionType == 15) Seed(c.ConditionId, true, c.Count);
}
bool Empty() => !hero.Invens.SelectMany(b => b.Items).Any();
byte? ResultCode(ushort expectedItem)
{
    if (client.Last(Msg.CS_ITEMBUY_ACK) is not { } packet) return null;
    var r = new PacketReader(packet);
    if (r.Remaining != 15) throw new InvalidOperationException("Wrong item-pickup response length.");
    byte code = r.ReadByte();
    if (r.ReadUInt16() != expectedItem) throw new InvalidOperationException("Wrong item-pickup response identity.");
    return code;
}
List<(uint, byte)> Offers()
{
    var rows = new List<(uint, byte)>();
    var r = new PacketReader(client.Last(Msg.CS_QUESTLIST_POSSIBLE_ACK)!);
    byte n = r.ReadByte();
    for (int i = 0; i < n; i++) { r.ReadUInt16(); r.ReadByte(); byte count = r.ReadByte(); for (int j = 0; j < count; j++) rows.Add((r.ReadUInt32(), r.ReadByte())); }
    return rows;
}

sealed class PickupResult
{
    public uint Quest { get; set; }
    public uint Parent { get; set; }
    public ushort Npc { get; set; }
    public uint Item { get; set; }
    public string Category { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Offered { get; set; }
    public bool ListCorrect { get; set; }
    public byte? PickupResultCode { get; set; }
    public int ItemCount { get; set; }
    public bool? ObjectiveUpdated { get; set; }
    public bool RepeatBlocked { get; set; }
    public bool MissingPrerequisiteBlocked { get; set; }
    public bool WrongMapBlocked { get; set; }
    public bool WrongNpcBlocked { get; set; }
}
