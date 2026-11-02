using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TMap.Data;

namespace TMap.Server.Map;

public sealed record CharacterSaveSnapshot(CharSaveData Character, List<QuestSaveRow> Quests,
    List<InvenSaveData>? Inventory, List<ItemSaveData>? Items, List<HotkeySaveRow> Hotkeys,
    List<PetRow> Pets, List<CompanionRow> Companions, byte CompanionSlot, uint Medals,
    List<SkillSaveRow> Skills, List<QuestPostIntent>? QuestPostRequests = null,
    List<Guid>? QuestPostReceipts = null);
public sealed record DirectItemSave(uint CharId, ItemSaveData Item);
public sealed record SaveQueueEntry(int Version, CharacterSaveSnapshot? Character,
    List<DirectItemSave>? Upserts, List<long>? Deletes, List<OperatorPostIntent>? OperatorPosts = null,
    List<CharacterSaveSnapshot>? Exchange = null)
{
    public static SaveQueueEntry Full(CharacterSaveSnapshot snapshot, List<DirectItemSave>? upserts = null, List<long>? deletes = null) => new(
        snapshot.QuestPostRequests is not null || snapshot.QuestPostReceipts is not null || upserts is not null ? 2 : 1, snapshot, upserts, deletes);
    public static SaveQueueEntry Direct(List<DirectItemSave> upserts, List<long> deletes) => new(1, null, upserts, deletes);
    public static SaveQueueEntry ServerPosts(List<OperatorPostIntent> posts) => new(3, null, null, null, posts);
    public static SaveQueueEntry PlayerExchange(List<CharacterSaveSnapshot> characters,
        List<DirectItemSave> upserts, List<long> deletes) => new(4, null, upserts, deletes, Exchange: characters);
}

/// <summary>Immutable snapshots committed to SQL before acknowledging gameplay. The failed head
/// remains in SQL and prevents later records from overtaking it. No filesystem storage or fallback.</summary>
public sealed class SqlSaveQueue : IDisposable
{
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private readonly ICharacterSaveQueueStore _store;
    private readonly SemaphoreSlim _drain = new(1, 1);
    private readonly object _gate = new();
    private long _boundary;
    public long Boundary { get { lock (_gate) return _boundary; } }
    public int PendingCount => _store.Count;
    public SqlSaveQueue(ICharacterSaveQueueStore store, string identity)
    {
        _store = store;
        try
        {
            store.Open(identity);
            _boundary = store.Boundary;
            foreach (var row in store.Read(_boundary)) Decode(row); // Validate before applying ANY saved state.
        }
        catch { store.Dispose(); throw; }
    }
    private static void Validate(SaveQueueEntry entry)
    {
        if (entry.Version == 4)
        {
            if (entry.Character is not null || entry.OperatorPosts is not null || entry.Upserts is null || entry.Deletes is null
                || entry.Exchange is not { Count: 2 } exchange || exchange.Any(s => s is null)
                || exchange.Select(s => s.Character.CharId).Distinct().Count() != 2)
                throw new InvalidDataException("Incomplete two-player exchange SQL save entry.");
            foreach (var exchangeSnapshot in exchange)
            {
                // Reuse the version-2 snapshot/receipt contract; exchanges must include both inventories.
                Validate(SaveQueueEntry.Full(exchangeSnapshot with { QuestPostRequests = exchangeSnapshot.QuestPostRequests ?? [] }));
                if (exchangeSnapshot.Inventory is null || exchangeSnapshot.Items is null)
                    throw new InvalidDataException("Player exchange requires complete inventories.");
            }
            var items = exchange.SelectMany(s => s.Items!).ToList();
            if (items.Any(i => i.DlId <= 0) || items.Select(i => i.DlId).Distinct().Count() != items.Count)
                throw new InvalidDataException("Player exchange has invalid or shared inventory item identities.");
            return;
        }
        if (entry.Exchange is not null) throw new InvalidDataException("Exchange metadata requires SQL save version 4.");
        if (entry.Version == 3)
        {
            if (entry.Character is not null || entry.Upserts is not null || entry.Deletes is not null ||
                entry.OperatorPosts is not { Count: > 0 } posts || posts.Any(p => p is null) || posts.Select(p => p.OperationId).Distinct().Count() != posts.Count)
                throw new InvalidDataException("Incomplete server reward SQL save entry.");
            foreach (var post in posts) post.Validate();
            return;
        }
        bool full = entry.Character is not null && (entry.Upserts is null) == (entry.Deletes is null);
        bool direct = entry.Character is null && entry.Upserts is not null && entry.Deletes is not null;
        if (entry.Version is not (1 or 2) || entry.OperatorPosts is not null || (!full && !direct) || (entry.Version == 2 && !full))
            throw new InvalidDataException("Unsupported or incomplete SQL save entry.");
        if (entry.Character is { } s && (s.Character.CharId == 0 || s.Quests is null || s.Hotkeys is null ||
            s.Pets is null || s.Companions is null || s.Skills is null || (s.Inventory is null) != (s.Items is null)))
            throw new InvalidDataException("Incomplete character save snapshot.");
        if (entry.Character is { } snapshot)
        {
            if (entry.Version == 1 && (snapshot.QuestPostRequests is not null || snapshot.QuestPostReceipts is not null || entry.Upserts is not null))
                throw new InvalidDataException("Quest post metadata requires SQL save version 2.");
            foreach (var request in snapshot.QuestPostRequests ?? [])
            {
                request.Validate();
                if (request.CharId != snapshot.Character.CharId) throw new InvalidDataException("Quest post owner mismatch.");
            }
            if (snapshot.QuestPostReceipts is { } receipts &&
                (receipts.Contains(Guid.Empty) || receipts.Distinct().Count() != receipts.Count))
                throw new InvalidDataException("Invalid quest post receipts.");
        }
    }
    private static SaveQueueEntry Decode(StoredCharacterSave row)
    {
        if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Payload))) != row.Checksum)
            throw new InvalidDataException("SQL save checksum mismatch.");
        var entry = JsonSerializer.Deserialize<SaveQueueEntry>(row.Payload, Json)
            ?? throw new InvalidDataException("Empty SQL save payload.");
        Validate(entry);
        return entry;
    }
    public IReadOnlyList<SaveQueueEntry> ReadPending() => _store.Read(Boundary).Select(Decode).ToList();
    public long Append(SaveQueueEntry entry)
    {
        Validate(entry);
        var payload = JsonSerializer.Serialize(entry, Json); // Freeze every array before committing to SQL.
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        lock (_gate)
        {
            long sequence = _store.Append(Guid.NewGuid(), checksum, payload);
            _boundary = Math.Max(_boundary, sequence);
            return sequence;
        }
    }
    public async Task DrainAsync(long boundary, Func<SaveQueueEntry, Task> apply)
    {
        await _drain.WaitAsync();
        try
        {
            var rows = _store.Read(boundary);
            var entries = rows.Select(Decode).ToList(); // Corruption cannot produce partial recovery.
            for (int i = 0; i < rows.Count; i++)
            {
                _store.AssertLease();
                await apply(entries[i]);
                _store.Acknowledge(rows[i].Sequence); // Includes a lease check; failed/ambiguous application stays queued.
            }
        }
        finally { _drain.Release(); }
    }
    public void Dispose() => _store.Dispose();
}
