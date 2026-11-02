using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Territory battles, batch B: the mission areas — a country-against-country race for one map.
/// <list type="bullet">
/// <item><b>The war</b> (<c>MW_MISSIONENABLE_REQ</c>): in battle the area's spawns (TMONSPAWNCHART <c>wLocalID</c>) come out
/// <i>suspended</i> — a dead monster stays dead and its death is a battle-zone event (C++ <c>MONSPAWN_SUSPEND</c>,
/// <c>CTMonster::OnBattleZoneEvent</c>); at normal they are taken away.</item>
/// <item><b>The capture</b>: killing a gatekeeper of the area (its left, right or centre one) takes it for the <i>other</i>
/// country than the gatekeeper's, at once (C++ <c>DM_MISSIONOCCUPY_REQ OCCUPY_ACCEPT</c>). A war that ends with nobody taking it
/// leaves it to no country (<c>OCCUPY_DEFEND</c>, <c>TCONTRY_N</c>).</item>
/// <item><b>Then</b> (C++ <c>OnDM_MISSIONOCCUPY_ACK</c>): it is saved (<c>TSaveMissionOccupy</c>), its monsters go, the winners on
/// its map get 200 useable PvP points (and the area's reward item ×5 by mail, when it has one — none in this data), and the
/// world is told (<c>MW_MISSIONOCCUPY_ACK</c>), which tells every map (<c>MW_MISSIONOCCUPY_REQ</c>): the news for everyone
/// (<c>SM_MISSION_BOSSDIE</c> / <c>SM_MISSION_TIMEOUT</c>) and the new owner. Its own country then gets the area's zone buffs
/// there (MapService.Territory.cs).</item>
/// <item>Leaving a map drops its territory's zone buffs and item cap (C++ <c>EraseMissionSkill</c> in <c>ExitMAP</c>).</item>
/// </list>
/// <para>Wars run on the default channel (C++ <c>DEFAULT_CHANNEL</c>). <b>Deviations:</b> a dead monster of a suspended spawn
/// leaves at its usual time (the C++ keeps the corpse) — it just does not come back until the spawn is released.
/// Capture acknowledgements and rewards wait for the SQL procedure's successful return.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte DefaultChannel = 1, OccupyDefend = 0, OccupyAccept = 1;
    private const byte SmMissionBossDie = 22, SmMissionTimeout = 26;

    /// <summary>The spawns that belong to a territory (C++ <c>TBATTLEZONE::m_vSpawnID</c>).</summary>
    private IEnumerable<ushort> ZoneSpawnIds(Territory t)
        => _templates.MonsterSpawns.Where(d => d.Spawn.LocalId == t.Id).Select(d => d.Spawn.Id);

    /// <summary>C++ <c>AddMonSpawn(pMap, wSpawnID, MONSPAWN_SUSPEND)</c>: the spawn comes out on the default channel, suspended.</summary>
    private void AddWarSpawn(ushort spawnId, bool suspend, byte? country = null)
    {
        if (spawnId == 0) return;
        AddTimelimitedMon(spawnId, DefaultChannel, 0, _tickSeconds * 1000L);
        foreach (var sp in _spawns.Where(p => p.Def.Spawn.Id == spawnId && p.Channel == DefaultChannel))
        {
            sp.Suspended = suspend;
            if (country is { } c)                                               // C++ AddMonSpawn(pSpawn, channel, bCountry)
                foreach (var slot in sp.Slots) if (slot.Live is { } m) m.Country = c;
        }
    }

    /// <summary>C++ <c>m_bStatus = MONSPAWN_READY</c> + <c>OnEvent(AT_DEAD)</c> on its monsters: the spawn is released and its live
    /// monsters go (no kill credited); its slots come back on their delay.</summary>
    private void ReleaseWarSpawn(ushort spawnId)
    {
        long now = _tickSeconds * 1000L;
        foreach (var sp in _spawns.Where(p => p.Def.Spawn.Id == spawnId && p.Channel == DefaultChannel))
        {
            sp.Suspended = false;
            foreach (var slot in sp.Slots)
            {
                if (slot.Live is { } m) { DespawnMonster(m, exitMap: true); slot.Live = null; slot.NextRegenMs = now + sp.Def.Spawn.Delay; }
                else if (slot.NextRegenMs == long.MaxValue) slot.NextRegenMs = now + sp.Def.Spawn.Delay;
            }
        }
    }

    /// <summary>The war-phase part of <c>OnMW_MISSIONENABLE_REQ</c> for one mission that changed status (MapService.Territory.cs).</summary>
    private void MissionPhase(Territory m, byte status)
    {
        switch (status)
        {
            case BsNormal:
                foreach (var id in ZoneSpawnIds(m)) DelMonSpawn(id, DefaultChannel);
                break;
            case BsBattle:
                foreach (var id in ZoneSpawnIds(m)) AddWarSpawn(id, suspend: true);
                break;
            case BsPeace:
                if (!m.Occupied) MissionOccupy(m, OccupyDefend, TcontryN, 0);
                foreach (var id in ZoneSpawnIds(m)) ReleaseWarSpawn(id);
                break;
        }
    }

    /// <summary>C++ <c>CTMonster::OnBattleZoneEvent</c> (TMonster.cpp:2041), called when a monster of a suspended spawn dies: a
    /// mission's gatekeeper takes the mission for the other country. (A fort's and the sky garden's: their own files.)</summary>
    private void OnBattleZoneEvent(Monster mon, uint attackerId)
    {
        ushort spawnId = (ushort)(mon.Id >> 16);
        if (SpawnById(spawnId)?.Spawn is not { LocalId: not 0 } spawn || !_territories.TryGetValue(spawn.LocalId, out var t)) return;
        var z = t.Zone;
        bool gate = spawnId == z.LGateKeeperSpawnId || spawnId == z.RGateKeeperSpawnId || spawnId == z.CGateKeeperSpawnId;
        // A gatekeeper opens its gate (C++ ChangeSwitch SWC_OPEN) — for a fort, the first one brings its boss out.
        uint gateSwitch = spawnId == z.LGateKeeperSpawnId ? z.LSwitchId : spawnId == z.RGateKeeperSpawnId ? z.RSwitchId
            : spawnId == z.CGateKeeperSpawnId ? z.CSwitchId : 0;
        bool opened = gateSwitch != 0 && ChangeSwitchModule(DefaultChannel, z.MapId, gateSwitch, SwcOpen);
        if (t.Type == LocalType.Occupation && t.Valid) FortZoneEvent(t, mon, spawnId, opened, attackerId);   // MapService.Fort.cs
        if (t.Type == LocalType.SkyGarden && t.Valid) SkyGardenZoneEvent(t, mon, spawnId, attackerId);
        if (t.Type == LocalType.Mission && t.Valid && gate && !t.Occupied)
        {
            t.Occupied = true;
            t.Status = BsPeace;
            MissionOccupy(t, OccupyAccept, mon.Country == TcontryD ? TcontryC : TcontryD, attackerId);
        }
    }

    /// <summary>C++ <c>SendDM_MISSIONOCCUPY_REQ</c> → <c>TSaveMissionOccupy</c> → <c>OnDM_MISSIONOCCUPY_ACK</c>: saved, its monsters
    /// gone, the winners rewarded, the new owner, and the world told.</summary>
    private void MissionOccupy(Territory m, byte type, byte country, uint charId)
    {
        if (TerritoryOccupationStore is { } db)
        {
            QueueTerritoryWrite(m, async () =>
            {
                int result = await SaveTerritoryResultAsync(() => db.SaveMissionOccupyAsync(m.Id, type, charId, country));
                if (result == 0) OnMissionOccupied(m, type, country);
            });
            return;
        }
        OnMissionOccupied(m, type, country);
    }

    private void OnMissionOccupied(Territory m, byte type, byte country)
    {
        foreach (var id in ZoneSpawnIds(m)) ReleaseWarSpawn(id);
        MissionReward(m, type, country);
        m.Country = country;
        var w = new PacketWriter(Msg.MW_MISSIONOCCUPY_ACK);
        w.WriteByte(type); w.WriteUInt16(m.Id); w.WriteByte(country);
        _world.Send(w);
        _log.LogInformation("Mission {Id} {How} for country {Country}.", m.Id, type == OccupyAccept ? "taken" : "timed out", country);
    }

    /// <summary>C++ <c>MissionReward</c> (TMapSvr.cpp:10047): on a capture, every player of the winning country on the area's map gets
    /// 200 useable PvP points, and the area's reward item ×5 by mail when it has one.</summary>
    private void MissionReward(Territory m, byte type, byte country)
    {
        if (type != OccupyAccept) return;
        var reward = m.Zone.NormalItem != 0 ? _templates.Item(m.Zone.NormalItem) : null;
        foreach (var s in _state.AllInGame().ToList())
        {
            if (!s.IsMain || s.Channel != DefaultChannel || s.Char is not { } ch || ch.MapId != m.Zone.MapId || WarCountryOf(ch) != country) continue;
            if (reward is not null) _ = MailOperatorItem(ch.CharId, ch.Name, "Mission reward", "", reward, 5);
            GainPvPoint(s, ch, 200, PvpeGuild, PvpUseable);
            ScheduleQuestCheckpoint(ch);
        }
    }

    /// <summary>A package from the operator with an item (C++ <c>SendDM_POSTRECV_REQ(0, …, POST_PACKATE, …, pItem)</c>).</summary>
    private Task MailOperatorItem(uint recvId, string recver, string title, string message, ItemTemplate tpl, byte count,
        uint silver = 0) => MailOperator(recvId, recver, title, message, new[] { (tpl, count) }, 0, silver, 0);

    /// <summary>C++ <c>SendDM_POSTRECVFROMSVR_REQ</c>: a package from the operator with money and new items, one slot each.</summary>
    private Task MailOperator(uint recvId, string recver, string title, string message,
        IReadOnlyList<(ItemTemplate Tpl, byte Count)> items, uint gold, uint silver, uint cooper, byte type = PostPackage)
    {
        if (_gameDb is not null)
        {
            if (OperatorPostStore is null) throw new InvalidOperationException("Durable server reward mail store required.");
            // Freeze and commit the complete intent to SQL synchronously before returning even an
            // ignored Task. Recovery reuses this identity; a collected/deleted letter is never reissued.
            var rows = new List<ItemSaveData>();
            for (int i = 0; i < items.Count; i++)
            {
                var (tpl, count) = items[i];
                var item = new Item { TemplateId = tpl.ItemId, Count = count, Template = tpl, DuraMax = tpl.DuraMax, DuraCur = tpl.DuraMax };
                LinkItemAttr(item);
                rows.Add(BuildItemSave(0, item) with { DlId = 0, StorageType = StoragePost, StorageId = 0, ItemSlot = checked((byte)i) });
            }
            var intent = new OperatorPostIntent(Guid.NewGuid(), recvId, recver, NetString(title), message, type, gold, silver, cooper, UnixNow(), rows);
            if (OperatorPostEnqueue is { } enqueue)
            {
                enqueue(intent);
                return Task.CompletedTask;
            }
            SaveQueue.Append(SaveQueueEntry.ServerPosts([intent]));
            return EnqueueDbWrite(() => Task.CompletedTask);
        }
        return DeliverLegacyOperatorPostAsync(recvId, recver, title, message, items, gold, silver, cooper, type);
    }

    // DB-free store seam used by the existing gameplay fixtures. Configured SQL never uses this path.
    private async Task DeliverLegacyOperatorPostAsync(uint recvId, string recver, string title, string message,
        IReadOnlyList<(ItemTemplate Tpl, byte Count)> items, uint gold, uint silver, uint cooper, byte type)
    {
        if (PostStore is not { } db) return;
        long now = UnixNow();
        title = NetString(title);                                          // C++ BuildNetString(NAME_NULL, GetSvrMsg(…)), the text as is
        (int saved, uint postId, uint recv) = await Safe(() => db.SavePostAsync(0, recvId, recver, "Operator", title, message,
            0, type, gold, silver, cooper, now), (PostInternal, 0u, 0u));
        if (saved != 0 || postId == 0) return;
        for (int i = 0; i < items.Count; i++)
        {
            var (tpl, count) = items[i];
            var item = new Item { TemplateId = tpl.ItemId, Count = count, Template = tpl, DuraMax = tpl.DuraMax, DuraCur = tpl.DuraMax };
            LinkItemAttr(item);
            var row = BuildItemSave(0, item) with { DlId = _itemIdReady ? GenItemId() : 0, StorageType = StoragePost, StorageId = postId, ItemSlot = (byte)i };
            await Safe(async () => { await db.SavePostItemAsync(recv, row); return 0; }, 0);
        }
        NotifyPostRecv(postId, "Operator", recver, title, type, now);
    }

    /// <summary>C++ <c>OnMW_MISSIONOCCUPY_REQ</c> (SSHandler.cpp:11439): the news for everyone (who took it, or that nobody did) and
    /// the new owner (C++ <c>ResetMission</c>).</summary>
    private void OnMW_MISSIONOCCUPY_REQ(PacketReader r)
    {
        byte type = r.ReadByte();
        ushort id = r.ReadUInt16();
        byte country = r.ReadByte();
        if (!_territories.TryGetValue(id, out var m) || m.Type != LocalType.Mission) return;
        NotifyLocalInfo(type == OccupyAccept ? SmMissionBossDie : SmMissionTimeout, country, m.Zone.MapId, m.Zone.Name);
        m.Country = country;
        if (m.Status != BsNormal) m.Status = BsPeace;
    }

    /// <summary>C++ <c>EraseMissionSkill</c> (TMapSvr.cpp:11744), on leaving a map: the territory's zone buffs and item cap come off.</summary>
    private void EraseZoneEffects(ClientSession s, Character ch)
    {
        if (LocalOf(ch) is not { } t) return;
        EraseMaintainById(s, ch, t.Zone.Skill1);
        EraseMaintainById(s, ch, t.Zone.Skill2);
        if (EquipItemRevision(s, ch, false, t.Zone.ItemLevel)) SendCS_ITEMLEVELREVISION_ACK(s, 0);
    }
}
