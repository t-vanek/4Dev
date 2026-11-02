using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Character persistence — the C# port of the C++ save path (<c>SendDM_SAVECHAR_REQ</c> →
/// <c>OnDM_SAVECHAR_REQ</c> stored-proc writes). It writes the char record (<c>TSaveChar</c>, one UPDATE by
/// <c>dwCharID</c>) and the dirty quest progress (<c>TSaveQuest</c>/<c>TSaveQuestTerm</c> upserts) on the same
/// triggers the C++ uses: a periodic 30-minute timer (<c>CHAR_SAVE_TICK</c>), on disconnect/logout
/// (<c>SetEventCloseSession</c>), and on shutdown (<c>SaveAllCharData</c>).
///
/// <para><b>Threading:</b> the snapshot (<see cref="BuildCharSave"/>/<see cref="BuildQuestSaves"/>) is built on
/// the batch thread — where all char state is lock-free — and the LastSave/quest-dirty flags are updated there;
/// the actual SQL then runs off-thread from durable snapshots, retried in FIFO order, so the
/// map never blocks on the DB. This replaces the C++ dedicated DB job queue.</para>
///
/// <para><b>DB-free:</b> everything no-ops when <c>_gameDb is null</c>. Only chars loaded from a real
/// <c>TCHARTABLE</c> row (<see cref="Character.DbLoaded"/>) and marked main (<see cref="ClientSession.IsMain"/>)
/// are saved — a synthesized DB-free char is never written back.</para>
///
/// <para><b>Inventory/item save (Phase 26, live-DB verified):</b> the containers + items are saved via the
/// <c>TSaveItemData*</c> bracket (see <see cref="GameDatabase.SaveInventoryAsync"/>) on the same triggers, so
/// <b>money and inventory now persist together</b> — the Phase-25 relog desync is closed. Gated on the item-id
/// seed (<see cref="_itemIdReady"/>) so an unseeded counter never rewrites with colliding <c>dlID</c>s.</para>
///
/// <para>Full character snapshots and incremental item changes are journaled before dirty state is cleared.
/// Pending writes are recovered before startup and character loading; other DB operations keep their existing semantics.</para>
/// </summary>
public sealed partial class MapService
{
    /// <summary>C++ <c>CHAR_SAVE_TICK</c> (TMapType.h) — 30 minutes between periodic char saves.</summary>
    public const uint SaveIntervalMs = 30 * 60 * 1000;

    /// <summary>Whether a periodic save is due for this session (C++ <c>OnTimer</c> gate): a main, DB-loaded,
    /// in-game char whose last save is at least one interval old. Independent of <c>_gameDb</c> so the gating is
    /// unit-testable DB-free.</summary>
    public bool IsSaveDue(ClientSession s, uint nowMs) =>
        s.State == EnterState.InGame && s.IsMain && s.Char is { DbLoaded: true } ch
        && nowMs - ch.LastSaveMs >= SaveIntervalMs;

    /// <summary>Snapshots the char record into the <c>TSaveChar</c> value set (batch-thread; immutable result is
    /// safe to hand to the off-thread write). The two pc-bang columns are 0 (subsystem unported).</summary>
    public static CharSaveData BuildCharSave(Character ch) => new(
        CharId: ch.CharId, StartAct: ch.StartAct, Level: ch.Level, HelmetHide: ch.HelmetHide,
        Gold: ch.Gold, Silver: ch.Silver, Cooper: ch.Cooper,
        GuildLeave: ch.Persist.GuildLeave, GuildLeaveTime: ch.Persist.GuildLeaveTime,
        Exp: ch.Exp, Hp: ch.Hp, Mp: ch.Mp, SkillPoint: (ushort)ch.SkillPoint, Region: ch.RegionId,
        MapId: ch.MapId, SpawnId: ch.Persist.SpawnId, LastSpawnId: ch.Persist.LastSpawnId,
        TemptedMon: ch.Persist.TemptedMon, Aftermath: ch.Persist.Aftermath,
        PosX: ch.PosX, PosY: ch.PosY, PosZ: ch.PosZ, Dir: ch.Dir,
        PcBangTime: 0, PcBangItemCnt: 0, LastDestination: ch.Persist.LastDestination,
        StatLevel: ch.Persist.StatLevel, StatPoint: ch.Persist.StatPoint, StatExp: ch.Persist.StatExp,
        PvpUseablePoint: ch.PvpUseablePoint, PvpTotalPoint: ch.PvpTotalPoint, PvpRecord: (uint[])ch.PvpRecord.Clone(),
        MonthPvPoint: ch.MonthPvPoint, MonthWin: ch.MonthWin, MonthLose: ch.MonthLose, MonthSay: ch.MonthSay, Country: ch.Country,
        Titles: ch.Titles.Select(t => (t.Key, t.Value)).ToList());

    /// <summary>Collects dirty quests and running timers into save rows and clears dirty flags. Timers
    /// must be included again even without an objective change, since their remaining time decays.
    /// Batch-thread; the returned rows are an immutable snapshot.</summary>
    public static List<QuestSaveRow> BuildQuestSaves(Character ch, uint nowMs, bool clearFlags = true)
    {
        var rows = new List<QuestSaveRow>();
        foreach (var qp in ch.Quests.Values)
        {
            // An unchanged active timer still decays between saves and must be snapshotted on logout.
            if (!qp.Save && !(qp.IsRunning && qp.HasTimer)) continue;
            // Timer quests persist their remaining time (C++ dwTick = TimerTick − elapsed, clamped ≥ 0).
            uint tick = qp.IsRunning ? qp.RemainingTimer(nowMs) : 0u;
            var terms = qp.RunningTerms
                .Select(rt => new QuestTermSaveRow(rt.TermId, rt.TermType, rt.Count))
                .ToList();
            rows.Add(new QuestSaveRow(qp.Template.QuestId, tick, qp.CompleteCount, qp.TriggerCount, terms));
            if (clearFlags) qp.Save = false;
        }
        return rows;
    }

    // ---- item-id generator (C++ m_dlGenItemID; seeded per-server by TInitGenItemID) ----

    private long _genItemId;
    private bool _itemIdReady;  // true once the per-server id base is seeded — the gate for the inventory rewrite

    /// <summary>C++ <c>CTMapSvrModule::GenItemID</c> — the next unique item row id (<c>++m_dlGenItemID</c>).
    /// Batch-thread only, so no lock. Public for tests.</summary>
    public long GenItemId() => ++_genItemId;

    /// <summary>Seeds the item-id counter from <c>TInitGenItemID</c> at bring-up (C++ <c>LoadData</c>
    /// <c>CSPInitGenItemID</c>). Until this succeeds the inventory save is disabled — an unseeded counter would
    /// mint <c>dlID</c>s that collide with existing <c>TITEMTABLE</c> PKs. No-op / skip when DB-free.</summary>
    public async Task InitItemIdSeedAsync()
    {
        if (_gameDb is null) return;
        long seed = await _gameDb.InitGenItemIdAsync((byte)_opt.ServerId);
        if (seed > 0) ApplyItemIdSeed(seed);
        else _log.LogWarning("TInitGenItemID unavailable — inventory save disabled (char + quest save still active).");
    }

    /// <summary>Sets the item-id counter base and arms the inventory save (both the full rewrite and the
    /// incremental direct-save gate on <see cref="_itemIdReady"/>). Called by <see cref="InitItemIdSeedAsync"/>
    /// after the DB seed; public so DB-free tests can arm the fast-path without a live seed proc.</summary>
    public void ApplyItemIdSeed(long seed) { _genItemId = seed; _itemIdReady = seed > 0; }

    /// <summary>Snapshots the character's containers + items into the <c>TSaveInven</c>/<c>TSaveItem</c> value
    /// sets (batch thread). An in-session item with no id yet (<c>DlId == 0</c>) is stamped from the generator —
    /// the C++ <c>WrapItemQuery</c> assigns <c>GenItemID()</c> to a 0-id item before it is written.</summary>
    public (List<InvenSaveData> invens, List<ItemSaveData> items) BuildInvenSaves(Character ch)
    {
        var invens = new List<InvenSaveData>();
        var items = new List<ItemSaveData>();
        foreach (var inv in ch.Invens)
        {
            invens.Add(new InvenSaveData(inv.InvenId, inv.TemplateId, inv.EndTime, inv.Eld));
            foreach (var it in inv.Items)
            {
                if (it.DlId == 0) it.DlId = GenItemId();
                items.Add(BuildItemSave(inv.InvenId, it));
            }
        }
        return (invens, items);
    }

    /// <summary>One item → its <c>TSaveItem</c>/<c>TSaveItemDirect</c> value set. All items are
    /// <c>STORAGE_INVEN</c> keyed by their container id (equip 0xFE / backpack 0xFF alike — the port's
    /// consistent load/save convention), with the magic set packed into the fixed 6 slots.</summary>
    private static ItemSaveData BuildItemSave(byte invenId, Item it) =>
        new(it.DlId, Proto.StorageInven, invenId, it.ItemSlot, it.TemplateId, it.Level, it.Count, it.GLevel,
            it.DuraMax, it.DuraCur, it.RefineCur, it.EndTime, it.GradeEffect,
            MagicIds(it), MagicValues(it), (uint[])it.Ext.Clone(), it.Gem, it.MoggItemId);

    /// <summary>One cabinet item → its <c>TSaveItem</c> value set (Phase 37): <c>STORAGE_CABINET</c>, the
    /// per-cabinet <see cref="Item.StItemId"/> as the storage id, and the cabinet id in the slot field —
    /// exactly the C++ cabinet-save mapping. Reuses the shared 35-value packer.</summary>
    private static ItemSaveData BuildCabinetItemSave(byte cabinetId, Item it) =>
        new(it.DlId, Proto.StorageCabinet, it.StItemId, cabinetId, it.TemplateId, it.Level, it.Count, it.GLevel,
            it.DuraMax, it.DuraCur, it.RefineCur, it.EndTime, it.GradeEffect,
            MagicIds(it), MagicValues(it), (uint[])it.Ext.Clone(), it.Gem, it.MoggItemId);

    // ---- incremental item persistence (the fast-path; C++ TSaveItemDirect) ----
    //
    // Every item mutation flows through SendCS_ADDITEM/UPDATEITEM/DELITEM_ACK, which record the change here.
    // FlushItemDirect drains it each tick and writes one TSaveItemDirect (upsert) per changed item + one delete
    // per removed row — so an item change survives a hard crash instead of waiting up to 30 min for the full
    // rewrite. This is a deliberate deviation: the C++ uses TSaveItemDirect only for server-side grants
    // (auction/guild) and lets the periodic full save cover client changes; the periodic full save (Phase 26)
    // stays as the authoritative reconciler and supersedes any in-flight direct op.

    // Keyed by dlID so repeated changes to one item within a tick collapse to a single write; a pending upsert
    // and delete for the same row are mutually exclusive (whichever op came last wins), so pick-up-then-drop in
    // one tick nets to a delete and never resurrects an item.
    private readonly Dictionary<long, (uint charId, ItemSaveData data)> _pendingItemUpserts = new();
    private readonly Dictionary<long, uint> _pendingItemDeletes = new();

    /// <summary>Pending incremental upserts (test visibility).</summary>
    public IReadOnlyDictionary<long, (uint charId, ItemSaveData data)> PendingItemUpserts => _pendingItemUpserts;
    /// <summary>Pending incremental deletes by dlID (test visibility).</summary>
    public IReadOnlyCollection<long> PendingItemDeletes => _pendingItemDeletes.Keys;

    /// <summary>Whether this session's item changes should persist incrementally: a main, DB-loaded char with the
    /// item-id base seeded (so a minted dlID can't collide with a live PK — the same gate as the full inventory
    /// save). Independent of <c>_gameDb</c> so the enqueue logic is unit-testable DB-free.</summary>
    private bool CanPersistItems(ClientSession s) => _itemIdReady && s.IsMain && s.Char is { DbLoaded: true, TournamentBackup: null };

    /// <summary>Records an added/changed item for an incremental direct save. Stamps a fresh dlID on an
    /// in-session item (0) so its row has a stable PK, queues the latest snapshot, and cancels any pending delete
    /// of the same row. Batch-thread only.</summary>
    public void EnqueueItemSave(ClientSession s, byte invenId, Item it)
    {
        if (!CanPersistItems(s)) return;
        if (it.DlId == 0) it.DlId = GenItemId();
        _pendingItemUpserts[it.DlId] = (s.Char!.CharId, BuildItemSave(invenId, it));
        _pendingItemDeletes.Remove(it.DlId);
    }

    /// <summary>Records a removed item (consumed/dropped/sold) for an incremental delete. An item never persisted
    /// (dlID 0) has no DB row, so nothing is queued; cancels any pending upsert of the same row.</summary>
    public void EnqueueItemDelete(ClientSession s, Item removed)
    {
        if (!CanPersistItems(s) || removed.DlId == 0) return;
        _pendingItemDeletes[removed.DlId] = s.Char!.CharId;
        _pendingItemUpserts.Remove(removed.DlId);
    }

    /// <summary>Records an added/changed CABINET item for an incremental direct save (Phase 37): same machinery
    /// as <see cref="EnqueueItemSave"/> but stamped <c>STORAGE_CABINET</c>/<c>StItemId</c>/cabinetId. The full
    /// inventory snapshot is <c>bStorageType=0</c>-scoped, so cabinet rows persist solely through this path (plus
    /// the delete on take-out). Batch-thread only.</summary>
    public void EnqueueCabinetItemSave(ClientSession s, byte cabinetId, Item it)
    {
        if (!CanPersistItems(s)) return;
        if (it.DlId == 0) it.DlId = GenItemId();
        _pendingItemUpserts[it.DlId] = (s.Char!.CharId, BuildCabinetItemSave(cabinetId, it));
        _pendingItemDeletes.Remove(it.DlId);
    }

    // ---- the DB write lane (C++: one DB thread) ----

    private readonly System.Threading.Channels.Channel<Func<Task>> _dbLane =
        System.Threading.Channels.Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private Task? _dbLaneTask;

    /// <summary>
    /// Queues a background write. The C++ posts every save to its single DB thread, so writes never overlap; firing
    /// them in parallel let two logout saves (each a <c>TSaveInven</c>/<c>TSaveItem</c> batch into the shared staging
    /// tables) deadlock, and SQL Server dropped one of them whole. Writes now run one at a time, in the order queued —
    /// which also keeps an incremental item write from landing after the full save that superseded it.
    /// </summary>
    private Task EnqueueDbWrite(Func<Task> write)
    {
        long boundary = _saveQueue?.Boundary ?? 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dbLane.Writer.TryWrite(async () =>
        {
            try
            {
                if (_saveQueue is { } journal) await journal.DrainAsync(boundary, ApplyQueuedSaveAsync);
                await write();
            }
            catch (Exception ex) { _log.LogWarning(ex, "Database write failed."); }
            finally { done.TrySetResult(); }
        });
        _dbLaneTask ??= Task.Run(async () =>
        {
            await foreach (var w in _dbLane.Reader.ReadAllAsync()) await w();
        });
        return done.Task;
    }

    /// <summary>Persists a cabinet's open-state (<c>bUse</c>) via <c>TSaveCabinet</c>, fired off-thread on open.
    /// Gated like the other saves (main, DB-loaded); no-ops DB-free.</summary>
    private void PersistCabinetHeader(ClientSession s, Cabinet cab)
    {
        if (_gameDb is null || !s.IsMain || s.Char is not { DbLoaded: true } ch) return;
        _ = EnqueueDbWrite(() => SaveCabinetHeaderAsync(ch.CharId, cab.CabinetId, cab.Use));
    }

    private async Task SaveCabinetHeaderAsync(uint charId, byte cabinetId, bool use)
    {
        try { await _gameDb!.SaveCabinetAsync(charId, cabinetId, use); }
        catch (Exception ex) { _log.LogWarning(ex, "Cabinet header save failed."); }
    }

    /// <summary>Drains the pending item ops (batch thread) and fires the direct writes off-thread — one
    /// <c>TSaveItemDirect</c> per changed item, one delete per removed row. With a DB, pending operations are
    /// cleared only after the journal accepts them; DB-free operation still clears the transient queue.</summary>
    public void FlushItemDirect()
    {
        if (_capturingQuestPost is not null) return; // the continuation's inventory and receipt share one SQL queue record
        if (_pendingItemUpserts.Count == 0 && _pendingItemDeletes.Count == 0) return;
        var upserts = _pendingItemUpserts.Values.ToList();
        var deletes = _pendingItemDeletes.Keys.ToList();
        if (_gameDb is not null)
        {
            SaveQueue.Append(SaveQueueEntry.Direct(upserts.Select(x => new DirectItemSave(x.charId, x.data)).ToList(), deletes));
            _ = EnqueueDbWrite(() => Task.CompletedTask);
        }
        _pendingItemUpserts.Clear();
        _pendingItemDeletes.Clear();
    }

    // The item's magic set → the fixed 6 (bMagic[i], wValue[i]) slots TSaveItem expects (raw stored values;
    // position is immaterial — magic is keyed by id, so any ordering round-trips).
    private static byte[] MagicIds(Item it)
    {
        var a = new byte[6];
        int k = 0;
        foreach (var m in it.Magic) { if (k >= 6) break; a[k++] = m.Id; }
        return a;
    }

    private static ushort[] MagicValues(Item it)
    {
        var a = new ushort[6];
        int k = 0;
        foreach (var m in it.Magic) { if (k >= 6) break; a[k++] = m.Value; }
        return a;
    }

    /// <summary>The skills to save (C++ <c>SendDM_SAVECHAR_REQ</c>'s skill loop: every held skill, its remaining reuse
    /// time) — but not the guild skills, which the guild tables keep. Batch-thread; the result is an immutable snapshot.</summary>
    public static List<SkillSaveRow> BuildSkillSaves(Character ch, uint nowMs)
        => ch.Skills.Where(k => !ch.GuildSkills.ContainsKey(k.SkillId)).OrderBy(k => k.SkillId).Select(k => new SkillSaveRow(k.SkillId, k.Level, k.GetReuseRemainTick(nowMs))).ToList();

    /// <summary>The periodic-save tick (C++ per-player <c>OnTimer</c> 30-min flush). Saves every due session.</summary>
    public void RunPeriodicSaves(uint nowMs)
    {
        if (_gameDb is null) return;
        RetryPendingSaves(nowMs);
        foreach (var s in _state.AllInGame().ToList())
            if (IsSaveDue(s, nowMs)) SaveCharData(s);
    }

    /// <summary>Snapshots + fires a char save for one session (gate: DB configured, main, DB-loaded). Ignores the
    /// periodic throttle — used by the disconnect/logout path (C++ <c>SetEventCloseSession</c>).</summary>
    private void SaveCharData(ClientSession s) => QueueCharacterSave(s);

    private Task QueueCharacterSave(ClientSession s)
    {
        if (_capturingQuestPost is not null) return Task.CompletedTask;
        if (_gameDb is null || !s.IsMain || s.Char is not { DbLoaded: true, TournamentBackup: null } ch)
            return Task.CompletedTask;
        // Item packets accumulated earlier in this batch must precede the full inventory snapshot.
        bool postSnapshot = _questPostReceipts.ContainsKey(ch) || _questPostRequests.ContainsKey(ch);
        bool combinedItems = postSnapshot || _pendingItemUpserts.Count != 0 || _pendingItemDeletes.Count != 0;
        var snapshot = CaptureCharacterSave(ch);
        SaveQueue.Append(SaveQueueEntry.Full(snapshot,
            combinedItems ? _pendingItemUpserts.Values.Select(x => new DirectItemSave(x.charId, x.data)).ToList() : null,
            combinedItems ? _pendingItemDeletes.Keys.ToList() : null));
        if (combinedItems) { _pendingItemUpserts.Clear(); _pendingItemDeletes.Clear(); }
        if (postSnapshot) _unpublishedQuestPost = null;
        CompleteCharacterCheckpoint(ch, snapshot);
        return EnqueueDbWrite(() => Task.CompletedTask);
    }

    private CharacterSaveSnapshot CaptureCharacterSave(Character ch)
    {
        var charData = WithMeetingExit(WithDuringItems(WithRecalls(BuildCharSave(ch), ch, NowMs), ch, UnixNow()));
        if (ArenaOf(ch) is { } arena)
            charData = charData with { MapId = arena.Out.MapId, PosX = arena.Out.PosX, PosY = arena.Out.PosY, PosZ = arena.Out.PosZ };
        var quests = BuildQuestSaves(ch, NowMs, clearFlags: false);
        var inventory = _itemIdReady ? BuildInvenSaves(ch) : ((List<InvenSaveData>, List<ItemSaveData>)?)null;
        var hotkeys = BuildHotkeySaves(ch, clearFlags: false);
        return new CharacterSaveSnapshot(charData, quests,
            inventory?.Item1, inventory?.Item2, hotkeys, PetSnapshot(ch), CompanionSnapshot(ch),
            ch.CompanionSlot, ch.Medals, BuildSkillSaves(ch, NowMs),
            _questPostRequests.GetValueOrDefault(ch), _questPostReceipts.GetValueOrDefault(ch));
    }

    private void CompleteCharacterCheckpoint(Character ch, CharacterSaveSnapshot snapshot)
    {
        _questPostRequests.Remove(ch);
        _questPostReceipts.Remove(ch);
        _questCheckpoints.Remove(ch);
        // A durable record now owns these changes, even if SQL fails or the session disappears.
        foreach (var row in snapshot.Quests) ch.Quests[row.QuestId].Save = false;
        foreach (var row in snapshot.Hotkeys) ch.HotkeyPages.First(p => p.InvenKey == row.InvenKey).Save = HotkeyPage.SaveLoad;
        ch.LastSaveMs = NowMs;
    }

    private void CheckpointPlayerExchange(Character a, Character b)
    {
        if (_gameDb is null) return;
        var snapshots = new List<CharacterSaveSnapshot> { CaptureCharacterSave(a), CaptureCharacterSave(b) };
        SaveQueue.Append(SaveQueueEntry.PlayerExchange(snapshots,
            _pendingItemUpserts.Values.Select(x => new DirectItemSave(x.charId, x.data)).ToList(), _pendingItemDeletes.Keys.ToList()));
        _pendingItemUpserts.Clear(); _pendingItemDeletes.Clear();
        CompleteCharacterCheckpoint(a, snapshots[0]); CompleteCharacterCheckpoint(b, snapshots[1]);
        if (ReferenceEquals(_unpublishedQuestPost, a) || ReferenceEquals(_unpublishedQuestPost, b)) _unpublishedQuestPost = null;
        _ = EnqueueDbWrite(() => Task.CompletedTask);
    }

    private async Task ApplyQueuedSaveAsync(SaveQueueEntry entry)
    {
        var db = _gameDb ?? throw new InvalidOperationException("Save recovery requires the game database.");
        if (entry.OperatorPosts is { } serverPosts)
        {
            var posts = OperatorPostStore ?? throw new InvalidOperationException("Durable server reward mail store required.");
            foreach (var intent in serverPosts)
            {
                if (LegacyOperatorPostForward is { } forward)
                {
                    await forward(intent); // acknowledge the old queue only AFTER Wolverine durably accepts the same operation ID
                    continue;
                }
                var result = await posts.DeliverOperatorPostAsync(intent);
                if (result.Result != 0)
                    _log.LogWarning("Server reward mail {Operation} for {Character} rejected: {Result}.", intent.OperationId, intent.CharId, result.Result);
                else if (result.Created)
                    _dbResults.Enqueue(() => NotifyPostRecv(result.PostId, "Operator", result.Receiver, intent.Title, intent.Type, intent.Time));
            }
        }
        // Version 2 mail checkpoints include direct item operations and the full result in ONE
        // record. Applying the operations first preserves cabinet/storage moves and the existing
        // direct-before-full order without a crash gap between two SQL queue commits.
        if (entry.Upserts is not null)
        {
            foreach (var item in entry.Upserts) await db.SaveItemDirectAsync(item.CharId, item.Item, requireProcedures: true);
            foreach (long id in entry.Deletes!) await db.DeleteItemDirectAsync(id);
        }
        var snapshots = entry.Exchange ?? (entry.Character is { } character ? [character] : new List<CharacterSaveSnapshot>());
        foreach (var s in snapshots)
        {
            uint id = s.Character.CharId;
            await db.SaveSkillsAsync(id, s.Skills);
            await db.SaveCharAsync(s.Character, requireProcedures: true);
            await db.SaveQuestsAsync(id, s.Quests, requireProcedures: true);
            if (s.Inventory is not null) await db.SaveInventoryAsync(id, s.Inventory, s.Items!, requireProcedures: true);
            await db.SaveHotkeysAsync(id, s.Hotkeys, requireProcedures: true);
            await SavePetsAsync(id, s.Pets);
            await SaveCompanionsAsync(id, s.Companions, s.CompanionSlot, s.Medals);
            if (s.QuestPostRequests is not null || s.QuestPostReceipts is not null)
            {
                var posts = QuestPostStore as IQuestPostOperationStore
                    ?? throw new InvalidOperationException("Durable quest post store required for recovery.");
                foreach (var request in s.QuestPostRequests ?? [])
                {
                    var result = await posts.ReserveQuestPostAsync(request);
                    if (result.Result is not (0 or QuestPostResult.AlreadyIssued))
                        _log.LogWarning("Journaled quest mail {Quest} for {Character} rejected: {Result}.", request.QuestId, id, result.Result);
                }
                // Last stage: only a replayable, fully-applied child snapshot may acknowledge an outbox event.
                await posts.AcknowledgeQuestPostOperationsAsync(id, s.QuestPostReceipts ?? []);
            }
        }
    }

    private SqlSaveQueue? _saveQueue;
    public ICharacterSaveQueueStore? SaveQueueStore { get; set; }
    private SqlSaveQueue SaveQueue => _saveQueue ??= CreateSaveQueue();
    private string SaveQueueOwner => _opt.SaveQueueName ?? $"{_opt.GroupId}-{_opt.ServerId}";
    private SqlSaveQueue CreateSaveQueue()
    {
        string DatabaseIdentity(string connection)
        {
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection);
            return b.DataSource + "\n" + b.InitialCatalog;
        }
        var identity = DatabaseIdentity(_opt.Db.GameConnectionString) + "\n" + DatabaseIdentity(_opt.Db.GlobalConnectionString ?? "");
        return new SqlSaveQueue(SaveQueueStore ?? new CharacterSaveQueueStore(_opt.Db.GameConnectionString, SaveQueueOwner), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity))));
    }

    /// <summary>Hold the SQL lease for the worker lifetime. Configured SQL cannot silently become DB-free.</summary>
    public IDisposable? OpenSaveQueue()
    {
        if (_gameDb is not null) return SaveQueue;
        if (!string.IsNullOrWhiteSpace(_opt.Db.GameConnectionString))
            throw new InvalidOperationException("Configured SQL save recovery is required before startup.");
        return null;
    }

    /// <summary>Startup/login barrier. Unlike background logging, failures here deny admission.</summary>
    public async Task RecoverPendingSavesAsync()
    {
        if (_gameDb is null) return;
        _ = SaveQueue; // initialize before capturing the enqueue boundary
        await EnqueueDbWrite(() => Task.CompletedTask);
        if (SaveQueue.PendingCount != 0)
            throw new IOException("Character save recovery is incomplete; admission denied.");
    }

    private uint _lastSaveRetry;
    private Task? _saveRetry;
    private void RetryPendingSaves(uint nowMs)
    {
        if (_saveQueue is not { PendingCount: > 0 } || _saveRetry is { IsCompleted: false } || nowMs - _lastSaveRetry < 5000) return;
        _lastSaveRetry = nowMs;
        _saveRetry = EnqueueDbWrite(() => Task.CompletedTask);
    }

    public async Task SaveAllCharDataAsync()
    {
        if (_gameDb is null) return;
        FlushItemDirect();
        foreach (var s in _state.AllInGame().ToList()) await QueueCharacterSave(s);
        await RecoverPendingSavesAsync();
    }
}
