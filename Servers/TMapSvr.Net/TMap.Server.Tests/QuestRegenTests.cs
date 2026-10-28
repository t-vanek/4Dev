using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Phase 34 — the quest <b>Regen</b> subtype: killing a monster mints a one-shot dynamic replacement of another
/// monster kind at the death position (<c>RegenDynamicMonster</c> + <c>AddTimelimitedMon</c>), linked to the
/// killed monster's spawn slot so its respawn force-removes the temp (<c>m_wRegenDelSpawn</c>). The dynamic
/// (SE_DYNAMIC) spawn is one-shot (never re-arms; its reserved id is recycled). All DB-free.
/// </summary>
public class QuestRegenTests
{
    private const ushort OrigMon = 500, DynMon = 501, OrigAttr = 900, DynAttr = 901;
    private const byte Level = 5;
    private const byte QttMonId = 7, TtKillMon = 5;

    // Original: a 5-HP / DP-100 monster on an SE_DEFAULT spawn (id 7) — one player hit kills it. Dynamic
    // replacement: monster 501 (its own attr). `dynHp` lets a test make the replacement one-shot too.
    private static TemplateStore Store(uint dynHp = 100)
    {
        var t = new TemplateStore();
        t.MonsterTemplates[OrigMon] = new MonsterTemplate(OrigMon, Level, OrigAttr);
        t.MonAttrs[TemplateStore.MonAttrKey(OrigAttr, Level)] = new MonAttrRow(OrigAttr, Level, 5, 50, 100);
        t.MonsterTemplates[DynMon] = new MonsterTemplate(DynMon, Level, DynAttr);
        t.MonAttrs[TemplateStore.MonAttrKey(DynAttr, Level)] = new MonAttrRow(DynAttr, Level, dynHp, 50, 100);
        t.MonsterSpawns.Add(new MonsterSpawnDef(
            new MonSpawnRow(Id: 7, MapId: 0, PosX: 100, PosY: 0, PosZ: 100, Dir: 0, Country: 0,
                Count: 1, Range: 0, Prob: 100, Region: 7, Delay: 0, Event: 0),
            new List<MapMonRow> { new(7, OrigMon, 0, 0, 100) }));
        return t;
    }

    // A QT_REGEN quest fired by killing OrigMon, spawning `dynMonId`.
    private static void AddRegenQuest(TemplateStore t, ushort dynMonId)
    {
        var q = new QuestTemplate { QuestId = 8300, Type = (byte)QuestType.Regen, TriggerType = TtKillMon, TriggerId = OrigMon };
        q.Terms.Add(new QuestTerm(dynMonId, QttMonId, 0));
        t.Quests[8300] = q;
    }

    private static async Task<(MapTestHarness h, ClientSession s, uint origId)> Setup(TemplateStore store)
    {
        var h = new MapTestHarness(store);
        var (s, c) = await h.EnterAsync(1, 1, 1, x: 100, z: 100);
        h.Service.InitMonsterSpawns();
        h.Service.InitQuests();
        h.Service.RunMonsterRegen(0);                  // spawn the original at (100,100)
        h.Service.CombatRng = new Random(1);
        c.Clear();
        return (h, s, Monster.MakeId(spawnId: 7, channel: 1, slot: 0));
    }

    [Fact]
    public async Task Regen_MintsDynamicReplacement_AtDeathPosition()
    {
        var store = Store();
        AddRegenQuest(store, DynMon);
        var (h, s, origId) = await Setup(store);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(attackerId: 1, targetId: origId));

        Assert.Null(h.State.FindMonster(origId));                             // original despawned (no loot)
        Assert.Single(h.State.AllMonsters());                                 // exactly one monster: the replacement
        var dyn = h.State.AllMonsters().Single(m => m.ChartId == DynMon);
        Assert.Equal(100f, dyn.PosX);                                         // at the killed monster's position
        Assert.Equal(100f, dyn.PosZ);
    }

    [Fact]
    public async Task DynamicReplacement_ForceRemovedWhenOriginalRespawns()
    {
        var store = Store();
        AddRegenQuest(store, DynMon);
        var (h, s, origId) = await Setup(store);
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, origId));
        Assert.Contains(h.State.AllMonsters(), m => m.ChartId == DynMon);

        h.Service.RunMonsterRegen(1000);                                     // the original respawns → link fires

        Assert.Contains(h.State.AllMonsters(), m => m.ChartId == OrigMon);   // original is back
        Assert.DoesNotContain(h.State.AllMonsters(), m => m.ChartId == DynMon); // temp force-removed
    }

    [Fact]
    public async Task Regen_UnknownMonsterKind_MintsNothing()
    {
        var store = Store();
        AddRegenQuest(store, 9999);                                          // 9999 not in MonsterTemplates
        var (h, s, origId) = await Setup(store);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, origId));

        Assert.Empty(h.State.AllMonsters());                                 // original gone; nothing minted
    }

    [Fact]
    public async Task DynamicReplacement_IsOneShot_DoesNotReArm()
    {
        var store = Store(dynHp: 5);                                         // one-shot dynamic too
        AddRegenQuest(store, DynMon);
        var (h, s, origId) = await Setup(store);
        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, origId));
        var dyn = h.State.AllMonsters().Single(m => m.ChartId == DynMon);

        await h.CastAndHitAsync(s, MapTestHarness.DefendReq(1, dyn.Id)); // kill the replacement
        Assert.DoesNotContain(h.State.AllMonsters(), m => m.ChartId == DynMon);

        h.Service.RunMonsterRegen(1000);                                     // its SE_DYNAMIC spawn must not re-arm
        Assert.DoesNotContain(h.State.AllMonsters(), m => m.ChartId == DynMon);
        Assert.Contains(h.State.AllMonsters(), m => m.ChartId == OrigMon);   // only the original returns
    }
}
