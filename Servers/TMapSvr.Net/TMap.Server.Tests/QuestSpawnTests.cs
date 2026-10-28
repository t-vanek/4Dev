using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Phase 33 — the quest-driven monster spawn subtypes on top of the time-limited-spawn engine: <b>SpawnMon</b>
/// (QTT_SPAWNID add / QTT_SPAWNID_DEL remove), <b>DieMon</b> (force-kill a spawn — credited or SE_QUESTDEL
/// silent), and <b>DropItem</b> (attach an owner-locked item to a killed monster's corpse). All DB-free.
/// (The <b>Regen</b> subtype — dynamic spawn templates — is deferred; see PORT_STATUS.md.)
/// </summary>
public class QuestSpawnTests
{
    private const ushort MonId = 500, AttrId = 900, ItemId = 200;
    private const byte Level = 5;
    private const byte SeDefault = 0, SeQuest = 2, SeQuestDel = 3;
    private const byte QttItemId = 5, QttSpawnId = 16, QttSpawnIdDel = 21;
    private const byte TtKillMon = 5;

    /// <summary>A store with monster 500 (level 5) + a spawn (id 7) of the given event; optionally an item chart.</summary>
    private static TemplateStore Store(byte spawnEvent, uint monExp = 0, uint monHp = 100, uint monDp = 0,
        byte count = 1, bool withItem = false)
    {
        var t = new TemplateStore();
        t.MonsterTemplates[MonId] = new MonsterTemplate(MonId, Level, AttrId, Exp: monExp);
        t.MonAttrs[TemplateStore.MonAttrKey(AttrId, Level)] = new MonAttrRow(AttrId, Level, monHp, 50, monDp);
        t.MonsterSpawns.Add(new MonsterSpawnDef(
            new MonSpawnRow(Id: 7, MapId: 0, PosX: 100, PosY: 0, PosZ: 100, Dir: 0, Country: 0,
                Count: count, Range: 0, Prob: 100, Region: 7, Delay: 0, Event: spawnEvent),
            new List<MapMonRow> { new(7, MonId, 0, 0, 100) }));
        if (withItem) t.Items[ItemId] = new ItemTemplate(ItemId, 0, new[] { 0f, 0f, 0f, 0f }, DefaultLevel: 1, Stack: 10);
        return t;
    }

    private static QuestTemplate Q(uint id, QuestType type, params (uint id, byte type, byte count)[] terms)
    {
        var q = new QuestTemplate { QuestId = id, Type = (byte)type, TriggerType = 1 /*not TT_TALKNPC ⇒ no NPC gate*/, TriggerId = 0 };
        foreach (var (tid, tt, tc) in terms) q.Terms.Add(new QuestTerm(tid, tt, tc));
        return q;
    }

    private static byte ReadListCount(byte[] pkt)
    {
        var r = new PacketReader(pkt);
        r.ReadByte(); r.ReadByte();                       // bRet, bUpdate
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); // monId, gold, silver, cooper
        return r.ReadByte();                              // item count
    }

    // ==================== SpawnMon ====================

    [Fact]
    public async Task SpawnMon_AddsTheSpawnsMonsters()
    {
        var store = Store(SeQuest, count: 2);
        store.Quests[8000] = Q(8000, QuestType.SpawnMon, (7, QttSpawnId, 0));
        var h = new MapTestHarness(store);
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(8000));

        Assert.Equal(2, h.State.AllMonsters().Count());
        Assert.NotNull(h.State.FindMonster(Monster.MakeId(spawnId: 7, channel: 1, slot: 0)));
        Assert.NotNull(h.State.FindMonster(Monster.MakeId(7, 1, 1)));
    }

    [Fact]
    public async Task SpawnMonDel_RemovesTheSpawn()
    {
        var store = Store(SeQuest);
        store.Quests[8000] = Q(8000, QuestType.SpawnMon, (7, QttSpawnId, 0));
        store.Quests[8001] = Q(8001, QuestType.SpawnMon, (7, QttSpawnIdDel, 0));
        var h = new MapTestHarness(store);
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(8000));
        Assert.Single(h.State.AllMonsters());

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(8001));
        Assert.Empty(h.State.AllMonsters());
    }

    [Fact]
    public async Task SpawnMon_DoesNotDoubleSpawn()
    {
        var store = Store(SeQuest, count: 1);
        store.Quests[8000] = Q(8000, QuestType.SpawnMon, (7, QttSpawnId, 0));
        var h = new MapTestHarness(store);
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(8000));
        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(8000)); // second cast is a no-op (de-dup)

        Assert.Single(h.State.AllMonsters());
    }

    // ==================== DieMon ====================

    [Fact]
    public async Task DieMon_CreditedKill_GivesExp_AndClearsTheSpawn()
    {
        var store = Store(SeDefault, monExp: 100);           // SE_DEFAULT ⇒ built + populated at bring-up
        store.Quests[8100] = Q(8100, QuestType.DieMon, (7, QttSpawnId, 0));
        var h = new MapTestHarness(store);
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        h.Service.InitMonsterSpawns();
        h.Service.RunMonsterRegen(0);
        Assert.Single(h.State.AllMonsters());
        Assert.Equal(0u, s.Char!.Exp);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(8100));

        Assert.Empty(h.State.AllMonsters());                 // killed; no loot ⇒ despawned
        Assert.True(s.Char!.Exp > 0);                        // credited kill awarded exp to the quester
    }

    [Fact]
    public async Task DieMon_QuestDelSpawn_IsSilent_NoExp()
    {
        var store = Store(SeQuestDel, monExp: 100);          // SE_QUESTDEL ⇒ silent removal, no reward
        store.Quests[8000] = Q(8000, QuestType.SpawnMon, (7, QttSpawnId, 0));
        store.Quests[8100] = Q(8100, QuestType.DieMon, (7, QttSpawnId, 0));
        var h = new MapTestHarness(store);
        var (s, _) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(8000));  // spawn it
        Assert.Single(h.State.AllMonsters());

        await h.Service.DispatchClientAsync(s, MapTestHarness.QuestExecReq(8100));  // force-kill (silent)
        Assert.Empty(h.State.AllMonsters());
        Assert.Equal(0u, s.Char!.Exp);                       // SE_QUESTDEL kill grants no exp
    }

    // ==================== DropItem (death-triggered) ====================

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, uint monId)> KillWithDropQuest()
    {
        var store = Store(SeDefault, monHp: 5, monDp: 100, withItem: true);   // 5-HP / DP-100 ⇒ one hit kills
        // A QT_DROPITEM quest triggered by killing monster kind 500, dropping 3× item 200.
        var q = new QuestTemplate { QuestId = 8200, Type = (byte)QuestType.DropItem, TriggerType = TtKillMon, TriggerId = MonId };
        q.Terms.Add(new QuestTerm(ItemId, QttItemId, 3));
        store.Quests[8200] = q;

        var h = new MapTestHarness(store);
        var owner = new Character { CharId = 1, Name = "Hero", Level = Level, MaxHp = 100, Hp = 100 };
        owner.Invens.Add(new Inven { InvenId = 0xFF });   // a backpack so the owner can take the drop
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100, preSeeded: owner);
        h.Service.InitQuests();                       // index the TT_KILLMON trigger
        uint monId = 0x20001;
        h.Service.SpawnMonster(new Monster
        {
            Id = monId, ChartId = MonId, Level = Level, MaxHp = 5, Hp = 5, MaxMp = 50, Mp = 50, DefendPower = 100,
            PosX = 100, PosY = 0, PosZ = 100, Region = 7, Channel = 1, MapId = 0,
        });
        h.Service.CombatRng = new Random(1);
        c.Clear();
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(attackerId: 1, targetId: monId)); // one-shot kill
        return (h, s, c, monId);
    }

    [Fact]
    public async Task DropItem_AttachesOwnerLockedItemToCorpse()
    {
        var (h, s, _, monId) = await KillWithDropQuest();

        var mon = h.State.FindMonster(monId);
        Assert.NotNull(mon);                                     // corpse persists (it holds the quest item)
        var item = Assert.Single(mon!.CorpseInven.Items);
        Assert.Equal(ItemId, item.TemplateId);
        Assert.Equal((byte)3, item.Count);
        Assert.Equal(s.Char!.CharId, item.OwnerId);              // owner-locked to the quest holder
    }

    [Fact]
    public async Task DropItem_IsHiddenFromNonOwner()
    {
        var (h, s, c, monId) = await KillWithDropQuest();
        var (other, oc) = await h.EnterAsync(2, 2, 2, x: 100, z: 100);

        oc.Clear();
        await h.Service.DispatchClientAsync(other, MapTestHarness.MonItemListReq(monId));
        Assert.Equal((byte)0, ReadListCount(oc.Last(Msg.CS_MONITEMLIST_ACK)!));   // non-owner: corpse looks empty

        c.Clear();
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonItemListReq(monId));
        Assert.Equal((byte)1, ReadListCount(c.Last(Msg.CS_MONITEMLIST_ACK)!));    // owner: sees the quest item
    }

    [Fact]
    public async Task DropItem_OnlyOwnerCanTake()
    {
        var (h, s, c, monId) = await KillWithDropQuest();
        var (other, oc) = await h.EnterAsync(2, 2, 2, x: 100, z: 100);
        byte slot = h.State.FindMonster(monId)!.CorpseInven.Items[0].ItemSlot;

        await h.Service.DispatchClientAsync(other, MapTestHarness.MonItemTakeReq(monId, slot));
        Assert.Equal((byte)MonItemTakeResult.NotFound, new PacketReader(oc.Last(Msg.CS_MONITEMTAKE_ACK)!).ReadByte());
        Assert.Single(h.State.FindMonster(monId)!.CorpseInven.Items);   // still there

        c.Clear();
        await h.Service.DispatchClientAsync(s, MapTestHarness.MonItemTakeReq(monId, slot));
        Assert.Equal((byte)MonItemTakeResult.Success, new PacketReader(c.Last(Msg.CS_MONITEMTAKE_ACK)!).ReadByte());
        Assert.Empty(h.State.FindMonster(monId)!.CorpseInven.Items);    // taken
    }
}
