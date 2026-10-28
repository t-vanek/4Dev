using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Monster death rewards — EXP + level-up + money loot (C++ <c>CTMonster::OnDie</c> TMonster.cpp:528 →
/// <c>GetExp</c>/<c>CTPlayer::GainExp</c>/<c>AddItem</c>). On a kill the loot/exp <b>owner</b> (the first
/// attacker to deal ≥10% of MaxHP, tracked by <see cref="Monster.AddDamage"/>) gains exp (value-exact,
/// attacker-level-scaled) which may level them up (full heal + skill points + <c>CS_LEVEL_ACK</c>/
/// <c>CS_EXP_ACK</c>); money is rolled onto the corpse, which persists as a lootable object until taken
/// (<c>CS_MONMONEYTAKE_REQ</c>) or it expires.
///
/// <para><b>Slice (documented — PORT_STATUS.md):</b> solo owner (<c>OWNER_PRIVATE</c>) for public loot; the
/// per-item owner-lock (<c>m_dwOwnerID</c>) is honored for quest <c>DropItem</c> drops. Deferred:
/// the loot magic/rare option generation, party exp+money split &amp; loot modes (lottery/chief/order routing),
/// cross-map (<c>MW_*</c>) relay for remote party members, and the vital/soul/premium/exp-buff/pet-collection
/// bonuses (all → 0, reducing the formulas to their base cases). EU build ⇒ the non-Korea monster exp-rate
/// curve. Corpse lifetime is a fixed approximation of the C++ <c>CTAICmdLeave</c> AI timeout.</para>
/// </summary>
public sealed partial class MapService
{
    private const long CorpseLifeMs = 30_000; // lootable-corpse lifetime (C++ CTAICmdLeave AI timeout ~ approx)

    /// <summary>RNG for the money-drop rolls. Seedable for tests (not C <c>rand()</c>).</summary>
    public Random LootRng { get; set; } = new();

    /// <summary>C++ <c>CTMonster::OnDie</c> spine: award exp to the keeper, roll the money drop, broadcast
    /// <c>CS_DIE_ACK</c>, then either keep the corpse (if it holds loot) or despawn + re-arm the spawn slot
    /// immediately (the behavior for a loot-less kill).</summary>
    private void OnMonsterDeath(Monster mon, uint killerId = 0)
    {
        long nowMs = _tickSeconds * 1000L;
        AwardKill(mon);   // exp (solo or party split) + the per-recipient hunt-quest advance
        // C++ TMonster.cpp:785 — dwAttackID, the one who struck the last blow (the keeper when it is not known).
        if (SpawnOf(mon.Id) is { Suspended: true }) OnBattleZoneEvent(mon, killerId != 0 ? killerId : mon.KeeperId);
        RollLoot(mon, killerId);
        foreach (var p in _state.PlayersAround(mon)) SendCS_DIE_ACK(p, mon.Id, Monster.OtMon);
        ReleaseMaintainMonster(mon, notify: false);   // C++ OnDie → ReleaseMaintain: drop the monster's debuffs

        // OS_DEAD is the state the corpse-side AI commands gate on (Leave / Lottery), and the epoch
        // bump discards anything still queued against the living monster (chase, attack, roam).
        mon.Status = OsDead;
        mon.HostKey++;

        if (mon.Ai is not null)
        {
            // C++ CTMonster::OnDie (TMonster.cpp:899) ends on OnEvent(AT_DEAD, 0, dwAttackID, 0, 0); the
            // script owns the corpse from here (typically AT_DEAD → AC_LEAVE after a delay → AT_DELETE →
            // AC_REGEN). CorpseExpireMs stays set as a backstop in case the chart never removes it.
            mon.Dead = true;
            mon.CorpseExpireMs = nowMs + CorpseLifeMs;
            OnAiEvent(mon, AiTrigger.Dead, 0, mon.KeeperId);
            return;
        }

        if (mon.HasLoot)                           // money and/or items rolled onto the corpse
        {
            mon.Dead = true;                       // a lootable corpse (not attackable, no regen)
            mon.CorpseExpireMs = nowMs + CorpseLifeMs;
        }
        else
        {
            CancelAi(mon);   // its scripted Leave must not outlive the body
            DespawnMonster(mon);
            RearmSpawnSlot(mon.Id, nowMs);
        }
    }

    /// <summary>Despawn + re-arm any corpse whose lifetime has elapsed (driven off the tick).</summary>
    public void RunCorpseExpiry(long nowMs)
    {
        // Collect first (despawning mutates the registry), into a reused list: this runs every tick over every monster.
        foreach (var mon in _state.AllMonsters())
            if (mon.Dead && mon.CorpseExpireMs <= nowMs) _expiredCorpses.Add(mon);
        if (_expiredCorpses.Count == 0) return;
        foreach (var mon in _expiredCorpses)
        {
            CancelAi(mon);   // its scripted Leave must not outlive the body
            DespawnMonster(mon);
            RearmSpawnSlot(mon.Id, nowMs);
        }
        _expiredCorpses.Clear();
    }

    private readonly List<Monster> _expiredCorpses = new();

    // ---- EXP ----

    // OWNER_TYPE (NetCode.h:1972)
    private const byte OwnerPrivate = 1, OwnerParty = 2;
    private const byte PtHunter = 2;                                    // PARTY_TYPE PT_HUNTER (NetCode.h:1964)

    private void AwardKill(Monster mon)
    {
        if (mon.KeeperType == 0) return;                                  // OWNER_NONE — nobody crossed 10%
        uint getExp = mon.Exp * (uint)MonExpRate(mon.Level) / 100;        // C++ GetExp (non-Korea rate)
        if (mon.MapId is >= 700 and < 800) getExp = getExp * 120 / 100;   // IsInMissionMap
        getExp += getExp * EventValue(EventExpAdd) / 100;                 // the exp event (MapService.Events.cs)

        if (mon.KeeperType == OwnerParty) { AwardPartyKill(mon, getExp); return; }

        // OWNER_PRIVATE (solo): the keeper is a char id.
        if (_state.FindByChar(mon.KeeperId) is not { State: EnterState.InGame, Char: { } killer } ks) return;
        float rate = LevelRate(killer.Level, mon.Level);                 // attacker-level penalty
        uint gain = (uint)((double)((float)getExp * rate) + 0.99);       // ceil (C++ DWORD(... + 0.99))
        GainExp(ks, killer, WithExpBonus(killer, gain));                 // + premium / exp boost / gain-exp buff
        HuntQuest(ks, killer, mon);
    }

    /// <summary>C++ <c>CTMonster::OnDie</c> OWNER_PARTY exp path (TMonster.cpp:615-694): the near party members
    /// (the monster's 3×3 neighborhood whose effective party id equals the keeper party) share the exp — the
    /// party-size bonus scales the pool, which is split by member level, then each is level-gap-scaled and
    /// awarded with its <c>wBonus</c> (MapService.DuringItem.cs), its soulmate among them getting a tenth more
    /// (<see cref="CheckSoulExp"/>). Cross-map members (<c>SendMW_MONSTERDIE_ACK</c>) are deferred (documented).</summary>
    private void AwardPartyKill(Monster mon, uint getExp)
    {
        var members = _state.PlayersAround(mon)
            .Where(p => p.State == EnterState.InGame && p.Char is { } c && c.GetPartyId() == mon.KeeperId)
            .ToList();
        if (members.Count == 0) return;

        int n = members.Count;
        uint totalLevel = (uint)members.Sum(p => p.Char!.Level);
        if (totalLevel == 0) totalLevel = 100;                            // C++ guard (wTotalLevel==0 → 100)
        float bonus = 1.0f + 0.01f * ((n * n) / 2.0f + n - 1.5f);         // 1 + 0.01·(n²/2 + n − 1.5); n=1 ⇒ 1.0
        uint totalExp = (uint)((float)getExp * bonus);

        foreach (var ks in members)
        {
            var m = ks.Char!;
            uint shared = totalExp * m.Level / totalLevel;                // integer level-weighted split
            uint gain = WithExpBonus(m, (uint)((double)((float)shared * LevelRate(m.Level, mon.Level)) + 0.99));
            CheckSoulExp(members, m, gain);                               // MapService.Soulmate.cs
            GainExp(ks, m, gain);
            HuntQuest(ks, m, mon);                                        // C++ fires the hunt quest per member
        }
    }

    /// <summary>Advance/trigger the killer's hunt quests (C++ <c>CheckQuest TT_KILLMON</c>, keyed by monster
    /// chart id). The QTT_SPAWNID_DEL monster-instance proximity nuance is deferred.</summary>
    private void HuntQuest(ClientSession s, Character ch, Monster mon)
        => CheckQuest(s, mon.Id, ch.PosX, ch.PosY, ch.PosZ, mon.ChartId, QttHunt, TtKillMon, 1);

    /// <summary>C++ <c>CTObjBase::GetMonExpRate</c> (TObjBase.cpp:4947, non-Korea): <c>64 − min(39, level)·9/39</c>
    /// percent (integer) — 64% at level 0 sliding to 55% at level ≥39.</summary>
    private static int MonExpRate(byte monLevel) => 64 - Math.Min(39, (int)monLevel) * 9 / 39;

    /// <summary>C++ <c>CTPlayer::GetLevelRate</c> (TPlayer.cpp:3440): <c>1 − min(max(playerLevel − monLevel, 0), 10)·0.1</c>
    /// — full exp at/below the monster level, −10% per level above, 0 at ≥10 levels above.</summary>
    private static float LevelRate(byte playerLevel, byte monLevel)
        => (float)(1 - Math.Min(Math.Max(playerLevel - monLevel, 0), 10) * 0.1);

    /// <summary>C++ <c>CTPlayer::GainExp</c> (TPlayer.cpp:1094) — add exp, level up across as many levels as the
    /// exp covers (full heal + skill points), broadcast the changes. The vital/soul/premium bonuses and the
    /// quest / map-level-kick hooks are deferred (0 / no-op). A zero gain is a no-op (we skip the C++ 0-exp
    /// <c>CS_EXP_ACK</c> to avoid a packet on every loot-less kill).</summary>
    private void GainExp(ClientSession s, Character ch, uint gain)
    {
        const byte playerLevelCap = 140;        // Gameforge 5.0.1 maximum; see Gameforge/target.json.
        if (gain == 0 || ch.Level >= playerLevelCap) return;
        FillSoulBar(s, ch, gain);                // C++ GainExp's soul bar (MapService.SoulLottery.cs)
        ulong total = (ulong)ch.Exp + gain;
        uint ceiling = _templates.LevelExpOf(playerLevelCap - 1) ?? uint.MaxValue;
        ch.Exp = (uint)Math.Min(total, ceiling);

        byte oldLevel = ch.Level;
        byte lvl = ch.Level;
        while (lvl < playerLevelCap && _templates.LevelExpOf(lvl) is { } th && th != 0 && th <= ch.Exp) lvl++;

        if (lvl != oldLevel)
        {
            for (byte i = oldLevel; i < lvl; i++) ch.SkillPoint += _templates.SkillPointOf(i);
            uint oldWidth = SoulBarWidth(ch);
            float soulShare = oldWidth == 0 ? 0f : (float)ch.SoulLotExp / oldWidth;
            ch.Level = lvl;
            ch.SoulLotExp = (uint)(SoulBarWidth(ch) * soulShare);   // C++ fOldSoulPercent
            ch.Hp = MaxHpFor(ch);       // full heal on level up (C++ m_dwHP = GetMaxHP())
            ch.Mp = MaxMpFor(ch);
            foreach (var p in _state.InView(s)) SendCS_LEVEL_ACK(p, ch.CharId, ch.Level);
            BroadcastHpMp(s, ch);
            SendCS_CHARSTATINFO_ACK(s, ch);
            SendMW_LEVELUP_ACK(s, ch);  // the world's level (guild, party, soulmate) — MapService.Soulmate.cs
        }
        SendCS_EXP_ACK(s, ch);
    }

    // ---- money loot ----

    private void RollLoot(Monster mon, uint killerId = 0)
    {
        // C++ AddItem (TMonster.cpp:1080) early-returns on an empty drop table — so no drop table ⇒ NO loot
        // at all, money included.
        if (mon.MaxWeight == 0) return;

        // Anti-farm (C++ TMonster.cpp:797): no drops when the best-levelled looter is ≥25 levels above the monster
        // (fixed: a party's keeper id was looked up as a character).
        var (country, maxLevel, addMagic, addDrop) = DropContext(mon, killerId);
        if (maxLevel > mon.Level && maxLevel - mon.Level >= 25) return;
        addDrop += mon.ItemProb * EventValueOnMap(EventItemDrop, mon.MapId) / 100;   // the drop events (MapService.Events.cs)
        addMagic += EventValueOnMap(EventItemMagicDrop, mon.MapId);

        // Money roll: m_bMoneyProb% chance, valid [min, max) range.
        if (mon.MoneyProb > LootRng.Next(100) && mon.MaxMoney > mon.MinMoney)
            mon.CorpseMoney = mon.MinMoney + (uint)LootRng.Next((int)(mon.MaxMoney - mon.MinMoney));
        mon.CorpseMoney += mon.CorpseMoney * EventValueOnMap(EventMoneyDrop, mon.MapId) / 100;   // the money event

        // Item roll: bDropCount attempts, each gated by m_bItemProb; weighted pick + the four "normal" gates.
        for (int i = 0; i < mon.DropCount; i++)
        {
            if (mon.ItemProb + addDrop <= LootRng.Next(100)) continue;     // C++ if(bItemProb + nAddItemDrop > rand%100)
            byte blank = mon.CorpseInven.GetBlankPos();
            if (blank == Proto.InvalidSlot) return;                        // corpse full

            var row = PickDropRow(mon);
            if (row is null) continue;
            if (!(row.Prob1 > LootRng.Next(100) && row.Prob2 > LootRng.Next(100)
                  && row.Prob3 > LootRng.Next(100) && row.Prob4 > LootRng.Next(100))) continue;

            if (MakeDropItem(mon, row, blank, country, addMagic) is not { } item) return;   // MapService.Drops.cs
            mon.CorpseInven.Items.Add(item);
        }
    }

    /// <summary>C++ <c>CTMonster::AddItem(CTItem*)</c> (TMonster.cpp:1057) — append a fully-built item to the
    /// corpse's <c>INVEN_DEFAULT</c>. Unlike the weighted <see cref="RollLoot"/>, this is <b>not</b> gated on
    /// <c>MaxWeight</c>, so a quest <c>DropItem</c> lands even on a monster with no normal drop table. The item
    /// carries <paramref name="ownerId"/> (owner-locked to the quest holder).</summary>
    private void AddCorpseItem(Monster mon, ushort itemId, byte count, uint ownerId, ItemTemplate? tpl)
    {
        byte blank = mon.CorpseInven.GetBlankPos();
        if (blank == Proto.InvalidSlot) return;   // corpse full
        var item = new Item { ItemSlot = blank, TemplateId = itemId, Count = count, OwnerId = ownerId, Template = tpl };
        LinkItemAttr(item);
        mon.CorpseInven.Items.Add(item);
    }

    /// <summary>C++ weighted pick over the drop rows (TMonster.cpp:1108): <c>roll = TRand(maxWeight)</c>, then
    /// the first row whose running weight total exceeds the roll.</summary>
    private MonItemRow? PickDropRow(Monster mon)
    {
        int roll = LootRng.Next((int)mon.MaxWeight);
        int acc = 0;
        foreach (var row in mon.DropRows) { acc += row.Weight; if (roll < acc) return row; }
        return null;
    }

    /// <summary>C++ <c>OnCS_MONITEMLIST_REQ</c> (CSHandler.cpp:6776) — <c>bWant · dwMonID</c>: open a corpse's loot window
    /// (<c>bWant</c> 1) or close it (0 — no answer: an answer would open it again). Someone else's loot answers
    /// <c>MIL_CANTACCESS</c>, and so does a party corpse in hunter mode for anyone but the monster's top-hate host.
    /// One looter at a time: opening locks the corpse (<c>m_dwInvenLock</c>), and a corpse locked by someone else answers
    /// <c>MIL_CANTACCESS</c>. The lock goes on close, when the corpse leaves the looter's view (moving, teleport), on
    /// logout (<see cref="ReleaseLootLock"/>) and when the corpse leaves the map.</summary>
    private void OnCS_MONITEMLIST_REQ(ClientSession s, PacketReader r)
    {
        bool want = r.ReadByte() != 0;  // bWant
        uint monId = r.ReadUInt32();    // dwMonID
        if (s.Char is not { } ch || ch.Riding != 0) return;   // no looting from a mount (CSHandler.cpp:6785)
        if (_state.FindMonster(monId) is not { } mon) return;
        if (!CanLootKeeper(mon, ch)) { SendCS_MONITEMLIST_CANTACCESS(s, monId); return; }
        if (!want && (mon.InvenLock == 0 || mon.InvenLock == ch.CharId))
        {
            ch.LockedMonId = 0;                                 // closing the window
            mon.InvenLock = 0;
            return;
        }
        if (mon.InvenLock != 0) { SendCS_MONITEMLIST_CANTACCESS(s, monId); return; }   // someone else is looting
        if (mon.KeeperType == OwnerParty && ch.PartyType == PtHunter && ch.CharId != GetHunter(mon))
        { SendCS_MONITEMLIST_CANTACCESS(s, monId); return; }
        mon.InvenLock = ch.CharId;
        ch.LockedMonId = monId;
        SendCS_MONITEMLIST_ACK(s, mon, update: 0);
    }

    /// <summary>The C++ lock releases (TCell.cpp:223/320/387 — the corpse leaves the looter's view; SSHandler.cpp:19205 and
    /// TMapSvr.cpp:1544 — logout): the corpse this character was looting is free again.</summary>
    private void ReleaseLootLock(Character ch)
    {
        if (ch.LockedMonId != 0 && _state.FindMonster(ch.LockedMonId) is { } mon) ReleaseLootLock(ch, mon);
    }

    /// <summary>The per-monster form (TCell.cpp): <paramref name="mon"/> leaves the view of <paramref name="ch"/>.</summary>
    private static void ReleaseLootLock(Character ch, Monster mon)
    {
        if (mon.InvenLock != ch.CharId) return;
        ch.LockedMonId = 0;
        mon.InvenLock = 0;
    }

    /// <summary>C++ <c>CTMonster::GetHunter</c> (TMonster.cpp:1009) — for a party corpse, the host with the most hate.</summary>
    private static uint GetHunter(Monster mon)
        => mon.KeeperType == OwnerParty && mon.AggroTable.Values.Where(a => a.Aggro > 0).MaxBy(a => a.Aggro) is { } top
            ? top.HostId : 0;

    /// <summary>C++ <c>SendCS_MONITEMLIST_ACK(MIL_CANTACCESS, dwMonID, 0, NULL, FALSE)</c> — no item count follows.</summary>
    private static void SendCS_MONITEMLIST_CANTACCESS(ClientSession s, uint monId)
    {
        var w = new PacketWriter(Msg.CS_MONITEMLIST_ACK, capacity: 20);
        w.WriteByte(1 /* MIL_CANTACCESS */); w.WriteByte(0); w.WriteUInt32(monId);
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0);
        s.Send(w);
    }

    private void OnCS_MONMONEYTAKE_REQ(ClientSession s, PacketReader r)
    {
        uint monId = r.ReadUInt32();
        if (s.Char is not { } ch || _state.FindMonster(monId) is not { } mon) return;
        // Keeper rule: the private keeper, or any member of the keeper party. The C++ level-weighted party
        // money split (near members each get money·level/ΣlvL) is deferred — a party member takes the full
        // corpse money here (documented, PORT_STATUS.md).
        if (!CanLootKeeper(mon, ch) || mon.CorpseMoney == 0) return;
        ch.EarnMoney(mon.CorpseMoney);
        mon.CorpseMoney = 0;
        SendCS_MONEY_ACK(s, ch);
        SendCS_MONITEMLIST_ACK(s, mon, update: 1);
    }

    /// <summary>C++ <c>OnCS_MONITEMTAKE_REQ</c> → <c>MonItemTake</c> (TMapSvr.cpp:9089), solo/OWNER_PRIVATE path:
    /// move one corpse item into the taker's bags. Party loot modes, item owner-tags/routing, the deal guard,
    /// and the cross-map relay are deferred.</summary>
    private void OnCS_MONITEMTAKE_REQ(ClientSession s, PacketReader r)
    {
        uint monId = r.ReadUInt32();  // dwMonID
        byte itemId = r.ReadByte();   // bItemID (the corpse slot)
        r.ReadByte();                 // bInvenID (target bag — the free-slot allocator is used instead)
        r.ReadByte();                 // bSlotID

        if (s.Char is not { } ch || _state.FindMonster(monId) is not { } mon) return;
        if (mon.CorpseInven.FindItem(itemId) is not { } item)
        {
            SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.NotFound);
            return;
        }
        // Owner-locked (quest DropItem) items bypass the keeper check but only the owner may take them
        // (C++ TMapSvr.cpp:9152); public items (OwnerId 0) fall under the OWNER_PRIVATE keeper rule.
        if (item.OwnerId != 0)
        {
            if (item.OwnerId != ch.CharId) { SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.NotFound); return; }
        }
        else if (!CanLootKeeper(mon, ch))     // keeper char, or any member of the keeper party
        {
            SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.NotFound);
            return;
        }
        if (item.MaxRouting != 0) return;     // being drawn for (C++ TMapSvr.cpp:9139)
        if (StartLootLottery(s, ch, mon, item))   // PT_LOTTERY (MapService.LootLottery.cs)
        {
            SendCS_MONITEMLIST_ACK(s, mon, update: 1);
            SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.Success);
            return;
        }

        var one = new[] { item };
        if (!CanPush(ch, one)) // bags full ⇒ can't take (C++ MIT_FULLINVEN)
        {
            SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.FullInven);
            SendCS_MONITEMLIST_ACK(s, mon, update: 1);
            return;
        }

        mon.CorpseInven.Items.Remove(item);   // C++ EraseItem
        PushTItem(s, one);                     // place into the taker's bags (+ CS_ADDITEM/UPDATEITEM)
        SendCS_GETITEM_ACK(s, item, ch.CharId);
        if (mon.KeeperType == OwnerParty) PartyMonItemTake(s, ch, item);
        SendCS_MONITEMLIST_ACK(s, mon, update: 1);
        SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.Success);
    }

    /// <summary>C++ <c>PartyMonItemTake</c>: tell the looter's near party members what was taken.</summary>
    private void PartyMonItemTake(ClientSession s, Character ch, Item item)
    {
        foreach (var p in _state.Neighbors(s))
            if (p.Char is { } c && c.GetPartyId() != 0 && c.GetPartyId() == ch.GetPartyId())
                SendCS_PARTYITEMTAKE_ACK(p, ch.CharId, item);
    }

    /// <summary>C++ corpse keeper access rule: for a public (owner-0) corpse item, the private keeper char may
    /// take it (OWNER_PRIVATE) or any member of the keeper party may (OWNER_PARTY); an unkept corpse (OWNER_NONE)
    /// is open. This is PT_FREE free-for-all among the party — the PT_HUNTER/LOTTERY/CHIEF/ORDER modes are
    /// deferred (documented, PORT_STATUS.md).</summary>
    private static bool CanLootKeeper(Monster mon, Character ch)
    {
        if (mon.KeeperType == OwnerParty) return ch.GetPartyId() != 0 && ch.GetPartyId() == mon.KeeperId;
        if (mon.KeeperType == OwnerPrivate) return mon.KeeperId == ch.CharId;
        return true;   // OWNER_NONE
    }

    /// <summary>C++ <c>SendCS_PARTYITEMTAKE_ACK</c> (CSSender.cpp:4303) — <c>dwCharID</c> (the looter) + the item
    /// block via <see cref="Item.WrapPacketClient"/> with <c>addItemId=false</c> (the leading slot byte omitted).</summary>
    private static void SendCS_PARTYITEMTAKE_ACK(ClientSession s, uint charId, Item item)
    {
        var w = new PacketWriter(Msg.CS_PARTYITEMTAKE_ACK, capacity: 48);
        w.WriteUInt32(charId);
        item.WrapPacketClient(w, charId, addItemId: false);
        s.Send(w);
    }

    // ---- senders ----

    private static void SendCS_LEVEL_ACK(ClientSession p, uint charId, byte level)
    {
        var w = new PacketWriter(Msg.CS_LEVEL_ACK, capacity: 8);
        w.WriteUInt32(charId);
        w.WriteByte(level);
        w.WriteByte(1);            // bShowLevelUp
        p.Send(w);
    }

    private void SendCS_EXP_ACK(ClientSession s, Character ch)
    {
        var w = new PacketWriter(Msg.CS_EXP_ACK, capacity: 20);
        w.WriteUInt32(ch.Exp);                                    // current total exp
        w.WriteUInt32(_templates.LevelExpOf(ch.Level - 1) ?? 0);  // current level's exp floor
        w.WriteUInt32(_templates.LevelExpOf(ch.Level) ?? 0);      // next-level threshold (ceiling)
        w.WriteUInt32(ch.SoulLotExp);                             // the soul bar (MapService.SoulLottery.cs)
        s.Send(w);
    }

    private static void SendCS_MONITEMLIST_ACK(ClientSession s, Monster mon, byte update)
    {
        SplitMoney(mon.CorpseMoney, out uint gold, out uint silver, out uint cooper);
        var w = new PacketWriter(Msg.CS_MONITEMLIST_ACK, capacity: 64);
        w.WriteByte(0);            // bRet (MIL_SUCCESS)
        w.WriteByte(update);       // bUpdate
        w.WriteUInt32(mon.Id);
        w.WriteUInt32(gold);
        w.WriteUInt32(silver);
        w.WriteUInt32(cooper);
        // Owner filter (C++ CSSender.cpp:3024): a viewer sees public items (OwnerId 0) + those locked to them.
        uint owner = s.Char?.CharId ?? 0;
        var items = mon.CorpseInven.Items.Where(it => it.MaxRouting == 0 && (it.OwnerId == 0 || it.OwnerId == owner)).ToList();
        w.WriteByte((byte)items.Count);
        foreach (var it in items) it.WrapPacketClient(w, owner);
        s.Send(w);
    }

    private static void SendCS_GETITEM_ACK(ClientSession s, Item item, uint ownerCharId)
    {
        var w = new PacketWriter(Msg.CS_GETITEM_ACK, capacity: 48);
        item.WrapPacketClient(w, ownerCharId);   // C++ SendCS_GETITEM_ACK: just the item block
        s.Send(w);
    }

    private static void SendCS_MONITEMTAKE_ACK(ClientSession s, MonItemTakeResult result)
    {
        var w = new PacketWriter(Msg.CS_MONITEMTAKE_ACK, capacity: 4);
        w.WriteByte((byte)result);
        s.Send(w);
    }

    /// <summary>C++ <c>CalcMoney(dlMoney, &amp;gold, &amp;silver, &amp;cooper)</c> — split a combined copper amount into
    /// the three tiers (base 1000).</summary>
    private static void SplitMoney(uint money, out uint gold, out uint silver, out uint cooper)
    {
        const uint m = Character.MoneyMultiply;
        cooper = money % m;
        silver = money / m % m;
        gold = money / m / m;
    }
}
