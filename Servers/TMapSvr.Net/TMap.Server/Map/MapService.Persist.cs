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
/// the actual SQL then runs off-thread (fire-and-forget with logged errors, self-throttled per char), so the
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
/// <para><b>Deferred (documented, PORT_STATUS.md):</b> skill/maintain/hotkey/cabinet/companion/pet saves and
/// the incremental single-item fast-path (<c>TSaveItemDirect</c>).</para>
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

    /// <summary>Collects the dirty (<c>Save</c>-flagged) quests into save rows and clears their flags (C++ resets
    /// <c>m_bSave</c> after packing). Batch-thread; the returned rows are an immutable snapshot.</summary>
    public static List<QuestSaveRow> BuildQuestSaves(Character ch, uint nowMs)
    {
        var rows = new List<QuestSaveRow>();
        foreach (var qp in ch.Quests.Values)
        {
            if (!qp.Save) continue;
            // Timer quests persist their remaining time (C++ dwTick = TimerTick − elapsed, clamped ≥ 0).
            uint tick = qp.IsRunning && qp.BeginTick != 0
                ? (uint)Math.Max(0L, (long)qp.TimerTick - (nowMs - qp.BeginTick)) : 0u;
            var terms = qp.RunningTerms
                .Select(rt => new QuestTermSaveRow(rt.TermId, rt.TermType, rt.Count))
                .ToList();
            rows.Add(new QuestSaveRow(qp.Template.QuestId, tick, qp.CompleteCount, qp.TriggerCount, terms));
            qp.Save = false;
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
            MagicIds(it), MagicValues(it), it.Ext, it.Gem, it.MoggItemId);

    /// <summary>One cabinet item → its <c>TSaveItem</c> value set (Phase 37): <c>STORAGE_CABINET</c>, the
    /// per-cabinet <see cref="Item.StItemId"/> as the storage id, and the cabinet id in the slot field —
    /// exactly the C++ cabinet-save mapping. Reuses the shared 35-value packer.</summary>
    private static ItemSaveData BuildCabinetItemSave(byte cabinetId, Item it) =>
        new(it.DlId, Proto.StorageCabinet, it.StItemId, cabinetId, it.TemplateId, it.Level, it.Count, it.GLevel,
            it.DuraMax, it.DuraCur, it.RefineCur, it.EndTime, it.GradeEffect,
            MagicIds(it), MagicValues(it), it.Ext, it.Gem, it.MoggItemId);

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
    private readonly HashSet<long> _pendingItemDeletes = new();

    /// <summary>Pending incremental upserts (test visibility).</summary>
    public IReadOnlyDictionary<long, (uint charId, ItemSaveData data)> PendingItemUpserts => _pendingItemUpserts;
    /// <summary>Pending incremental deletes by dlID (test visibility).</summary>
    public IReadOnlyCollection<long> PendingItemDeletes => _pendingItemDeletes;

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
        _pendingItemDeletes.Add(removed.DlId);
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
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dbLane.Writer.TryWrite(async () =>
        {
            try { await write(); }
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
    /// <c>TSaveItemDirect</c> per changed item, one delete per removed row. Queues are cleared regardless; the
    /// actual writes no-op DB-free.</summary>
    public void FlushItemDirect()
    {
        if (_pendingItemUpserts.Count == 0 && _pendingItemDeletes.Count == 0) return;
        var upserts = _pendingItemUpserts.Values.ToList();
        var deletes = _pendingItemDeletes.ToList();
        _pendingItemUpserts.Clear();
        _pendingItemDeletes.Clear();
        if (_gameDb is null) return;
        _ = EnqueueDbWrite(() => FlushItemDirectAsync(upserts, deletes));
    }

    private async Task FlushItemDirectAsync(List<(uint charId, ItemSaveData data)> upserts, List<long> deletes)
    {
        try
        {
            foreach (var (charId, data) in upserts) await _gameDb!.SaveItemDirectAsync(charId, data);
            foreach (var dlId in deletes) await _gameDb!.DeleteItemDirectAsync(dlId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Incremental item save failed.");
        }
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
        foreach (var s in _state.AllInGame().ToList())
            if (IsSaveDue(s, nowMs)) SaveCharData(s);
    }

    /// <summary>Snapshots + fires a char save for one session (gate: DB configured, main, DB-loaded). Ignores the
    /// periodic throttle — used by the disconnect/logout path (C++ <c>SetEventCloseSession</c>).</summary>
    private void SaveCharData(ClientSession s)
    {
        if (_gameDb is null || !s.IsMain || s.Char is not { DbLoaded: true, TournamentBackup: null } ch) return;   // not the tournament character
        ch.LastSaveMs = NowMs;
        var charData = WithMeetingExit(WithDuringItems(WithRecalls(BuildCharSave(ch), ch, NowMs), ch, UnixNow()));
        if (ArenaOf(ch) is { } arena)                                                  // a fighter is saved at the exit (SSSender.cpp:823)
            charData = charData with { MapId = arena.Out.MapId, PosX = arena.Out.PosX, PosY = arena.Out.PosY, PosZ = arena.Out.PosZ };
        var quests = BuildQuestSaves(ch, NowMs);
        var inventory = _itemIdReady ? BuildInvenSaves(ch) : ((List<InvenSaveData>, List<ItemSaveData>)?)null;
        var hotkeys = BuildHotkeySaves(ch);
        var pets = PetSnapshot(ch);
        var comps = (CompanionSnapshot(ch), ch.CompanionSlot, ch.Medals);
        var skills = BuildSkillSaves(ch, NowMs);
        _ = EnqueueDbWrite(() => FlushSaveAsync(ch.CharId, charData, quests, inventory, hotkeys, pets, comps, skills));   // snapshot is immutable
    }

    /// <summary>The off-thread write (never throws unobserved — errors are logged and swallowed). The inventory
    /// rewrite runs only when a snapshot is supplied (i.e. the id seed is ready) — else the DB keeps its last
    /// item state, never a destructive empty rewrite.</summary>
    private async Task FlushSaveAsync(uint charId, CharSaveData charData, IReadOnlyList<QuestSaveRow> quests,
        (List<InvenSaveData> invens, List<ItemSaveData> items)? inventory, IReadOnlyList<HotkeySaveRow> hotkeys,
        IReadOnlyList<PetRow>? pets = null, (List<CompanionRow> Rows, byte Slot, uint Medals)? comps = null,
        IReadOnlyList<SkillSaveRow>? skills = null)
    {
        try
        {
            if (skills is not null) await _gameDb!.SaveSkillsAsync(charId, skills);
            await _gameDb!.SaveCharAsync(charData);
            await _gameDb.SaveQuestsAsync(charId, quests);
            if (inventory is { } inv) await _gameDb.SaveInventoryAsync(charId, inv.invens, inv.items);
            await _gameDb.SaveHotkeysAsync(charId, hotkeys);
            if (pets is { Count: > 0 }) await SavePetsAsync(charId, pets);
            if (comps is { } cs) await SaveCompanionsAsync(charId, cs.Rows, cs.Slot, cs.Medals);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Char {Char} save failed.", charId);
        }
    }

    /// <summary>Flushes every in-game char synchronously (awaited) — the shutdown path (C++ <c>SaveAllCharData</c>).</summary>
    public async Task SaveAllCharDataAsync()
    {
        if (_gameDb is null) return;
        foreach (var s in _state.AllInGame().ToList())
        {
            if (!s.IsMain || s.Char is not { DbLoaded: true } ch) continue;
            ch.LastSaveMs = NowMs;
            var charData = WithMeetingExit(WithDuringItems(WithRecalls(BuildCharSave(ch), ch, NowMs), ch, UnixNow()));
            if (ArenaOf(ch) is { } arena)                                                  // a fighter is saved at the exit (SSSender.cpp:823)
                charData = charData with { MapId = arena.Out.MapId, PosX = arena.Out.PosX, PosY = arena.Out.PosY, PosZ = arena.Out.PosZ };
            var quests = BuildQuestSaves(ch, NowMs);
            var inventory = _itemIdReady ? BuildInvenSaves(ch) : ((List<InvenSaveData>, List<ItemSaveData>)?)null;
            var hotkeys = BuildHotkeySaves(ch);
            var pets = PetSnapshot(ch);
            var comps = (CompanionSnapshot(ch), ch.CompanionSlot, ch.Medals);
            var skills = BuildSkillSaves(ch, NowMs);
            await EnqueueDbWrite(() => FlushSaveAsync(ch.CharId, charData, quests, inventory, hotkeys, pets, comps, skills));
        }
    }
}
