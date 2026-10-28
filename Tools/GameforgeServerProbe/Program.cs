// Load the real SQL charts, then exercise the actual packet handlers with ephemeral characters.
// The connection is read-only; no database store is attached to the packet harness.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using TMap.Server.Tests;

if (args.Length != 3 || !Regex.IsMatch(args[0], @"^TGame_(?:gsp|ContentStage_[0-9_]+)$"))
    throw new ArgumentException("Expected explicit staging/live database, plan path and output path.");
var configured = Environment.GetEnvironmentVariable("Map__Db__GameConnectionString");
var connection = new SqlConnectionStringBuilder(configured ?? "") { InitialCatalog = args[0], ConnectTimeout = 5 };
if (string.IsNullOrEmpty(configured))
{
    string secretLine = File.ReadLines(".env").Single(s => s.StartsWith("SA_PASSWORD=", StringComparison.Ordinal));
    connection.Password = secretLine["SA_PASSWORD=".Length..].Trim().Trim('"', '\'');
    connection.DataSource = "127.0.0.1,11433";
    connection.UserID = "sa";
    connection.TrustServerCertificate = true;
    connection.Encrypt = false;
}
var templates = await new GameDatabase(connection.ConnectionString).LoadTemplatesAsync();
using var document = JsonDocument.Parse(File.ReadAllBytes(args[1]));
var additions = document.RootElement.GetProperty("additions");
var results = new List<object>();
foreach (var row in additions.GetProperty("TQUESTCHART").EnumerateArray())
{
    uint id = row.GetProperty("dwQuestID").GetUInt32();
    var recipe = templates.Quests[id];
    var giver = templates.Npcs[(ushort)recipe.TriggerId];
    if (recipe.Type != (byte)QuestType.Craft || giver.Type != 20)
        throw new InvalidDataException($"Wrong recipe/giver {id}.");
    var output = recipe.Rewards.Single();
    var target = templates.Item((ushort)output.RewardId)!;
    uint mask = target.ClassId;
    foreach (var restriction in recipe.Conditions.Where(c => c.ConditionType == 5)) mask &= restriction.ConditionId;
    byte profession = Enumerable.Range(0, 6).Select(c => (byte)c).First(c => (mask & (1u << c)) != 0);
    var hero = new Character { CharId = 1, Name = "ContentProbe", MaxHp = 100, Hp = 100 };
    hero.Invens.Add(new Inven { InvenId = 0xFF });
    var harness = new MapTestHarness(templates);
    var (session, client) = await harness.EnterAsync(1, 1, 1, mapId: giver.MapId,
        x: giver.PosX, y: giver.PosY, z: giver.PosZ, preSeeded: hero);
    hero.Class = profession;
    hero.Country = giver.Country <= 2 ? giver.Country : (byte)1;
    hero.AidCountry = hero.Country;
    hero.Level = 1;
    harness.Service.InitNpcs();
    harness.Service.InitQuests();
    var bag = hero.FindInven(0xFF)!;
    int Count(ushort itemId) => hero.Invens.Sum(b => b.Items.Where(i => i.TemplateId == itemId).Sum(i => (int)i.Count));
    for (int cycle = 0; cycle < 2; cycle++)
    {
        foreach (var ingredient in recipe.Terms)
        {
            var template = templates.Item((ushort)ingredient.TermId)!;
            int remaining = ingredient.Count;
            while (remaining > 0)
            {
                int count = Math.Min(remaining, Math.Max(1, (int)template.Stack));
                byte slot = bag.GetBlankPos();
                if (slot == 0xFF) throw new InvalidDataException("Probe backpack unexpectedly full.");
                bag.Items.Add(new Item { ItemSlot = slot, TemplateId = template.ItemId, Count = (byte)count, Template = template });
                remaining -= count;
            }
        }
        var expected = recipe.Terms.Select(t => (ushort)t.TermId).Append((ushort)output.RewardId).Distinct()
            .ToDictionary(i => i, Count);
        foreach (var term in recipe.Terms) expected[(ushort)term.TermId] -= term.Count;
        expected[(ushort)output.RewardId] += output.Count;
        client.Clear();
        await harness.Service.DispatchClientAsync(session, MapTestHarness.QuestExecReq(id));
        byte[] ack = client.Last(Msg.CS_QUESTCOMPLETE_ACK) ?? throw new InvalidDataException($"Recipe {id} produced no completion packet.");
        var reader = new PacketReader(ack);
        if (reader.ReadByte() != (byte)QuestResult.Success || reader.ReadUInt32() != id)
            throw new InvalidDataException($"Recipe {id} did not complete.");
        foreach (var (itemId, count) in expected)
            if (Count(itemId) != count) throw new InvalidDataException($"Recipe {id}: wrong final item count for {itemId}.");
        if (hero.FindQuest(id) is not null || client.Has(Msg.CS_QUESTADD_ACK))
            throw new InvalidDataException("Crafting incorrectly left a running mission.");
    }
    var ingredientIds = recipe.Terms.Select(t => (ushort)t.TermId).ToHashSet();
    bag.Items.RemoveAll(i => ingredientIds.Contains(i.TemplateId));
    int oldOutput = Count((ushort)output.RewardId);
    client.Clear();
    await harness.Service.DispatchClientAsync(session, MapTestHarness.QuestExecReq(id));
    var denied = new PacketReader(client.Last(Msg.CS_QUESTCOMPLETE_ACK) ?? throw new InvalidDataException("Missing failure packet."));
    if (denied.ReadByte() != (byte)QuestResult.Term || Count((ushort)output.RewardId) != oldOutput)
        throw new InvalidDataException($"Recipe {id} can grant an output without ingredients.");
    results.Add(new { questId = id, giverId = giver.Id, repeatedSuccesses = 2, insufficientIngredientsRejected = true });
}
var mountResults = new List<object>();
foreach (var row in additions.GetProperty("TITEMCHART").EnumerateArray())
{
    ushort itemId = row.GetProperty("wItemID").GetUInt16();
    var item = templates.Item(itemId)!;
    if (item.Type != 12 || item.Kind != 23 || !templates.Mounts.ContainsKey(item.UseValue))
        throw new InvalidDataException($"Unsupported mount deed {itemId}.");
    var hero = new Character { CharId = 1, Name = "MountProbe", MaxHp = 100, Hp = 100 };
    hero.Invens.Add(new Inven { InvenId = 0xFF });
    var harness = new MapTestHarness(templates);
    var (session, client) = await harness.EnterAsync(1, 1, 1, preSeeded: hero);
    hero.FindInven(0xFF)!.Items.Add(new Item { ItemSlot = 0, TemplateId = itemId, Count = 1, Template = item });
    client.Clear();
    var request = new PacketWriter(Msg.CS_PETMAKE_REQ).WriteByte(0xFF).WriteByte(0).WriteString("OriginalMount");
    await harness.Service.DispatchClientAsync(session, request.ToArray());
    var ack = new PacketReader(client.Last(Msg.CS_PETMAKE_ACK) ?? throw new InvalidDataException($"No mount result for {itemId}."));
    if (ack.ReadByte() != 0 || ack.ReadUInt16() != item.UseValue || !hero.Pets.ContainsKey(item.UseValue)
        || hero.FindInven(0xFF)!.Items.Any(i => i.TemplateId == itemId))
        throw new InvalidDataException($"Mount deed {itemId} failed registration/consumption.");
    mountResults.Add(new { itemId, mountId = item.UseValue, consumed = true, registered = true });
}
var registry = new MapTestHarness(templates);
registry.Service.InitNpcs();
foreach (var row in additions.GetProperty("TNPCCHART").EnumerateArray())
{
    ushort id = row.GetProperty("wID").GetUInt16();
    if (registry.State.FindNpc(id) is not { } npc || npc.MapId != row.GetProperty("wMapID").GetUInt16())
        throw new InvalidDataException($"NPC {id} did not register on its original map.");
}
foreach (var row in additions.GetProperty("TNPCITEMCHART").EnumerateArray())
{
    ushort id = row.GetProperty("wNpcID").GetUInt16(), portal = row.GetProperty("dwItemID").GetUInt16();
    if (registry.State.FindNpc(id)?.PortalId != portal || !templates.Portals.ContainsKey(portal))
        throw new InvalidDataException($"NPC {id} portal association did not resolve.");
}
var report = new
{
    database = args[0], passed = true,
    planSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))).ToLowerInvariant(),
    recipes = results, successfulCrafts = results.Count * 2, rejectedCrafts = results.Count,
    mounts = mountResults, npcRegistrations = additions.GetProperty("TNPCCHART").GetArrayLength(),
    portalAssociations = additions.GetProperty("TNPCITEMCHART").GetArrayLength(),
    writesToSql = 0, note = "Actual SQL chart loader and packet handlers, ephemeral in-memory characters; not a graphical client playthrough.",
};
File.WriteAllText(args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { report.database, report.passed, recipes = results.Count, report.successfulCrafts, report.rejectedCrafts,
    mounts = mountResults.Count, report.npcRegistrations, report.portalAssociations, report.writesToSql }));
