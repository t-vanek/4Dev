using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Companions (C++ CSHandler.cpp:18514-19610, 20811; TPlayer.cpp:1054, 5665-5723, 7086). Two layers: the owned
/// <b>record</b> in slot 0..4 (<c>TCOMPANIONTABLE</c>, <see cref="Character.Companions"/>) and, while summoned, the
/// <b>creature</b> — a summon of kind <c>OT_COMPANION</c> created through the world (<c>MW_CREATESPOLECNIKMON</c>) and
/// shown with <c>CS_ADDSPOLECNIKMON_ACK</c>. The owner's client moves it; it follows and never fights.
///
/// <para>The summoned slot (<see cref="Character.CompanionSlot"/>) drives the owner bonuses — six stats plus one bonus
/// (attack level, crit, MP…) — even while no creature is out. Every 60 s the summoned companion loses stamina and gains
/// exp (<c>CzechPetHP</c>); below 2400 stamina it is sent away. Records are saved with the character.</para>
///
/// <para><b>Faithful, flagged:</b> <c>CS_USECOMPANIONITEM_REQ</c> and <c>CS_USECOMPANIONPOWDER_REQ</c> eat the item even
/// for a slot the player doesn't own; <c>CS_COMPANIONLUP_REQ</c> does nothing for a companion whose bonus id is not in
/// <c>TCOMPANIONBONUSCHART</c>; entering a map attaches the stats row at the companion's level where every other summon
/// uses the owner's level. <b>Changed:</b> <c>CS_COMPANIONRECALL_REQ</c> summons the record's own species, not the one the
/// client names; deleting a companion no longer turns "none summoned" (0xFF) into 0xFE, and the slot left empty at the
/// end is deleted too (the C++ left a copy of the last companion behind, which came back at the next login);
/// <c>CS_DELETECOMPITEMS_REQ</c> bounds its sub-slot; the transform scroll refuses an unknown species; stamina above 32767
/// is saved as 32767 (the column is a SMALLINT — the C++ save failed outright). <b>Not ported:</b> the companion item
/// kinds' effects (auto-loot, the drop bonus, the angel revive)
/// and the tournament / BoW gates.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte NoCompanion = 0xFF;
    private const int MaxCompanions = 5;
    private const byte CompanionMaxLevel = 20;
    private const uint CompanionMaxLife = 300000, CompanionStartLife = 11000, CompanionLowLife = 2400;
    private const ushort CompanionStarterItem = 18084;
    private const byte ItCompanion = 22, ItCompanionItem = 24;
    private const byte IkCompHp = 109, IkCompExp = 110, IkPowder = 116, IkCompReset = 122, IkPetTransform = 200;
    private const byte CompanionMinEffect = 1, CompanionMaxEffect = 39;
    private const long CompanionTickMs = 60_000;

    /// <summary>C++ <c>rand()%9+1</c> → bonus id (the 10th arm, MTYPE_MHP = 50, is unreachable).</summary>
    private static readonly byte[] CompanionBonusRoll = { 11, 12, 13, 20, 21, 51, 86, 87, 88 };

    public ICompanionStore? CompanionStore { get; set; }

    /// <summary>The companion dice (bonus roll, effect roll). Replaceable in tests.</summary>
    public Random CompanionRng { get; set; } = new();

    private long _nextCompanionTickMs = CompanionTickMs;

    // ================================ load / save ================================

    private async Task LoadCompanionsAsync(Character ch)
    {
        if (CompanionStore is not { } db) return;
        try
        {
            var load = await db.LoadCompanionsAsync(ch.CharId);
            foreach (var row in load.Companions)
            {
                var c = new Companion
                {
                    Slot = row.Slot, MonId = row.MonId, Level = row.Level, Name = row.Name, Exp = row.Exp,
                    NextExp = Companion.NextExpFor(row.Level), Life = row.Life, StatPoints = row.StatPoints,
                    Effect = row.Effect, BonusId = row.BonusId, Tick = row.Tick,
                };
                Array.Copy(row.Stats, c.Stats, 6);
                Array.Copy(row.ItemIds, c.ItemIds, 2);
                Array.Copy(row.EndTimes, c.EndTimes, 2);
                ch.Companions[c.Slot] = c;
            }
            ch.CompanionSlot = load.SummonedSlot;
            ch.Medals = load.Medals;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Char {Char} companion load failed; continuing without companions.", ch.CharId);
        }
    }

    private static List<CompanionRow> CompanionSnapshot(Character ch) => ch.Companions.Values.Select(c =>
        new CompanionRow(c.Slot, c.MonId, c.Level, c.Name, c.Exp, c.Life, c.StatPoints, c.Effect, (byte[])c.Stats.Clone(),
            c.BonusId, (ushort[])c.ItemIds.Clone(), (long[])c.EndTimes.Clone(), c.Tick)).ToList();

    /// <summary>The companion part of the character save (C++ <c>OnDM_SAVECHAR_REQ</c>, SSHandler.cpp:7499):
    /// <c>TSaveCompanion</c> for each, then the summoned slot and the medals.</summary>
    private async Task SaveCompanionsAsync(uint charId, IReadOnlyList<CompanionRow> rows, byte slot, uint medals)
    {
        if (CompanionStore is not { } db) return;
        foreach (var r in rows) await db.SaveCompanionAsync(charId, r);
        await db.SaveLastCompanionAsync(charId, slot);
        await db.SaveMedalsAsync(charId, medals);
    }

    // ================================ create / delete ================================

    private void OnCS_CREATECOMPANION_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTutorial(ch)) return;
        byte invenId = r.ReadByte(), slotId = r.ReadByte();
        string name = r.ReadString();

        if (ch.FindInven(invenId) is not { } bag) return;
        if (bag.Items.FirstOrDefault(i => i.ItemSlot == slotId) is not { Count: > 0, Template.Type: ItCompanion } rune) return;
        if (name.Length == 0 || name.Length > MaxName) { SendCS_CREATECOMPANION_ACK(s, 1, NoCompanion); return; }

        uint species = rune.DuraMax == 0 ? (ushort)rune.Ext[Item.IevCompanion] : (ushort)rune.DuraMax;
        if (!_templates.MonsterTemplates.ContainsKey((ushort)species)) return;

        byte slot = NoCompanion;
        for (byte i = 0; i < MaxCompanions; i++) if (!ch.Companions.ContainsKey(i)) { slot = i; break; }
        if (slot == NoCompanion) { SendCS_CREATECOMPANION_ACK(s, 3, NoCompanion); return; }

        long now = UnixNow();
        var c = new Companion
        {
            Slot = slot, MonId = species, Name = name, BonusId = RollCompanionBonus(ch), Level = 1, StatPoints = 1,
            Life = CompanionStartLife, NextExp = 3600,
        };
        c.ItemIds[0] = CompanionStarterItem;
        c.EndTimes[0] = now + 3600;
        ch.Companions[slot] = c;

        foreach (var obj in ch.CompanionObjs.Values) SendMW_SPOLECNIKMONDEL_ACK(ch.CharId, s.Key, obj.Id);
        ch.CompanionSlot = slot;
        SpawnCompanion(s, ch, c, attrLevel: ch.Level, level: 1, effect: 0);

        SendCS_COMPANIONLIST_ACK(s, ch);
        SendCS_CREATECOMPANION_ACK(s, 0, slot);
        SendCS_CHARSTATINFO_ACK(s, ch);
        UseItemStack(s, invenId, bag, rune, 1);
    }

    /// <summary>C++ bonus roll (CSHandler.cpp:18915): a bonus none of the player's companions has yet. The first die is
    /// thrown away, as in the C++.</summary>
    private byte RollCompanionBonus(Character ch, IEnumerable<byte>? taken = null)
    {
        var owned = new HashSet<byte>(taken ?? ch.Companions.Values.Select(c => c.BonusId));
        CompanionRng.Next(9);
        for (int guard = 0; guard < 1000; guard++)
        {
            byte id = CompanionBonusRoll[CompanionRng.Next(9)];
            if (!owned.Contains(id)) return id;
        }
        return CompanionBonusRoll.FirstOrDefault(id => !owned.Contains(id), CompanionBonusRoll[0]);   // dice that never land
    }

    private async Task OnCS_DELETECOMPANION_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        byte slot = r.ReadByte();
        if (!ch.Companions.ContainsKey(slot)) return;

        if (slot == ch.CompanionSlot)
        {
            foreach (var obj in ch.CompanionObjs.Values) SendMW_SPOLECNIKMONDEL_ACK(ch.CharId, s.Key, obj.Id);
            ch.CompanionSlot = NoCompanion;
            SendCS_UPDATESPAWNEDCOMPANION_REQ(s, NoCompanion);
        }

        int before = ch.Companions.Count;
        ch.Companions.Remove(slot);
        if (CompanionStore is { } db) { uint id = ch.CharId; await EnqueueDbWrite(() => db.DeleteCompanionAsync(id, slot)); }

        // Close the gap: every later companion moves down one slot.
        for (byte i = (byte)(slot + 1); i < MaxCompanions && ch.Companions.Remove(i, out var moved); i++)
        {
            moved.Slot = (byte)(i - 1);
            ch.Companions[moved.Slot] = moved;
        }
        if (ch.CompanionSlot != NoCompanion && ch.CompanionSlot > slot) ch.CompanionSlot--;

        SaveCharData(s);
        // The slot the compaction vacated (the C++ never deleted it, so the old last companion came back at login).
        if (before - 1 > slot && CompanionStore is { } db2)
        {
            uint id = ch.CharId; byte vacated = (byte)(before - 1);
            await EnqueueDbWrite(() => db2.DeleteCompanionAsync(id, vacated));
        }
        SendCS_COMPANIONLIST_ACK(s, ch);
        SendCS_UPDATESPAWNEDCOMPANION_REQ(s, ch.CompanionSlot);
    }

    // ================================ summon / dismiss ================================

    private void OnCS_COMPANIONRECALL_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        uint monId = r.ReadUInt32();
        byte slot = r.ReadByte();
        if (!_templates.MonsterTemplates.ContainsKey((ushort)monId)) return;
        if (!ch.Companions.TryGetValue(slot, out var c)) return;

        bool same = slot == ch.CompanionSlot;
        foreach (var obj in ch.CompanionObjs.Values) SendMW_SPOLECNIKMONDEL_ACK(ch.CharId, s.Key, obj.Id);
        if (same) { ch.CompanionSlot = NoCompanion; return; }

        ch.CompanionSlot = slot;
        SendCS_UPDATESPAWNEDCOMPANION_REQ(s, slot);
        if (ch.Hp == 0) return;                                         // spawned on revival
        SpawnCompanion(s, ch, c, attrLevel: ch.Level, level: c.Level, effect: c.Effect);
        SendCS_CHARSTATINFO_ACK(s, ch);
    }

    private void OnCS_COMPANIONCANCEL_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        foreach (var obj in ch.CompanionObjs.Values) SendMW_SPOLECNIKMONDEL_ACK(ch.CharId, s.Key, obj.Id);
        ch.CompanionSlot = NoCompanion;
        SendCS_UPDATESPAWNEDCOMPANION_REQ(s, NoCompanion);
        SendCS_CHARSTATINFO_ACK(s, ch);
        SendSelfHpMp(s, ch.CharId, MaxHpFor(ch), ch.Hp, MaxMpFor(ch), ch.Mp);
    }

    private void OnCS_HIDECOMPANION_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        RespawnCompanion(s, ch);                                        // despite the name, the C++ re-spawns it
    }

    /// <summary>C++ <c>CTPlayer::RespawnCompanion</c> (TPlayer.cpp:7086) — out again with the record's current level and
    /// effect. It checks neither death nor the map, like the C++.</summary>
    private void RespawnCompanion(ClientSession s, Character ch)
    {
        if (ch.SummonedCompanion is not { } c) return;
        foreach (var obj in ch.CompanionObjs.Values) SendMW_SPOLECNIKMONDEL_ACK(ch.CharId, s.Key, obj.Id);
        if (_templates.MonsterTemplates.ContainsKey((ushort)c.MonId))
            SpawnCompanion(s, ch, c, attrLevel: ch.Level, level: c.Level, effect: c.Effect);
        SendCS_CHARSTATINFO_ACK(s, ch);
    }

    /// <summary>The <c>MW_CREATESPOLECNIKMON_ACK</c> the C++ repeats in four places (SSSender.cpp:640): the world
    /// allocates the id and sends it back to every map the owner is on.</summary>
    private void SpawnCompanion(ClientSession s, Character ch, Companion c, byte attrLevel, byte level, byte effect)
    {
        if (!_templates.MonsterTemplates.TryGetValue((ushort)c.MonId, out var tpl)) return;
        float rad = ch.Dir * MathF.PI / 900f;
        var w = new PacketWriter(Msg.MW_CREATESPOLECNIKMON_ACK, capacity: 96);
        w.WriteUInt32(ch.CharId); w.WriteUInt32(s.Key); w.WriteUInt32(0); w.WriteUInt16((ushort)c.MonId);
        w.WriteUInt32((uint)(tpl.SummonAttr | (attrLevel << 16)));
        w.WriteUInt16(0); w.WriteByte(effect); w.WriteString("");
        w.WriteByte(level); w.WriteByte(tpl.Class); w.WriteByte(tpl.Race);
        w.WriteByte(TaStand); w.WriteByte(1 /* OS_WAKEUP */); w.WriteByte(MtNormal);
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0);
        w.WriteByte(100); w.WriteByte(1);
        w.WriteFloat(ch.PosX - 2f * MathF.Sin(rad)); w.WriteFloat(ch.PosY); w.WriteFloat(ch.PosZ - 2f * MathF.Cos(rad));
        w.WriteUInt16(ch.Dir); w.WriteUInt32(0);
        w.WriteByte(0); w.WriteUInt32(0); w.WriteByte(0);
        var skills = tpl.Skills.ToList();
        w.WriteByte((byte)skills.Count);
        foreach (var id in skills) w.WriteUInt16(id);
        _world.Send(w);
    }

    private void SendMW_SPOLECNIKMONDEL_ACK(uint charId, uint key, uint monId, bool forever = true)
    {
        var w = new PacketWriter(Msg.MW_SPOLECNIKMONDEL_ACK, capacity: 16);
        w.WriteUInt32(charId); w.WriteUInt32(key); w.WriteUInt32(monId); w.WriteByte((byte)(forever ? 1 : 0));
        _world.Send(w);
    }

    /// <summary>C++ <c>OnMW_CREATESPOLECNIKMON_REQ</c> (SSHandler.cpp:1833): any companion already out goes, and the new
    /// one appears.</summary>
    private void OnMW_CREATESPOLECNIKMON_REQ(PacketReader r)
    {
        var rec = ReadRecallRecord(r);
        if (FindPlayer(rec.CharId, rec.Key) is not { Char: { } ch } s) return;
        if (!_templates.MonsterTemplates.TryGetValue(rec.Mon, out var tpl)) return;
        foreach (var old in ch.CompanionObjs.Values) SendMW_SPOLECNIKMONDEL_ACK(rec.CharId, rec.Key, old.Id, forever: false);
        if (ch.CompanionObjs.ContainsKey(rec.MonId)) return;
        if (_templates.MonAttr((ushort)(rec.Attr & 0xFFFF), (byte)(rec.Attr >> 16)) is not { } attr) return;

        var mon = new RecallMon
        {
            Id = rec.MonId, ObjType = RecallMon.OtCompanion, OwnerId = ch.CharId, ChartId = tpl.Id, Template = tpl, Attr = attr,
            Effect = rec.Effect, Name = rec.Name, Level = rec.Level, AtkLevel = ch.Level, AtkSkillLevel = rec.SkillLevel,
            Hit = rec.Hit, Action = rec.Action, Status = rec.Status, Mode = rec.Mode, RecallType = 0,   // TRECALLTYPE_NONE
            MaxHp = rec.MaxHp != 0 ? rec.MaxHp : attr.MaxHp, MaxMp = rec.MaxMp != 0 ? rec.MaxMp : attr.MaxMp,
            Channel = s.Channel, MapId = ch.MapId, Country = ch.Country, AidCountry = ch.AidCountry, Region = ch.RegionId,
            PosX = rec.X, PosY = rec.Y, PosZ = rec.Z, Dir = rec.Dir,
        };
        mon.Hp = mon.MaxHp; mon.Mp = mon.MaxMp;
        AddSummonSkills(mon, rec.Skills, rec.SkillLevel);
        ch.CompanionObjs[mon.Id] = mon;

        SendCS_UPDATESPAWNEDCOMPANION_REQ(s, ch.CompanionSlot);
        if (s.State == EnterState.InGame) EnterRecall(mon);
    }

    /// <summary>C++ <c>OnMW_SPOLECNIKMONDEL_REQ</c> (SSHandler.cpp:1987) — found by char id; the wire's bForever is
    /// ignored, as in the C++.</summary>
    private void OnMW_SPOLECNIKMONDEL_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(); r.ReadUInt32();
        uint monId = r.ReadUInt32(); r.ReadByte();
        if (_state.FindByChar(charId) is not { Char: { } ch } || !ch.CompanionObjs.Remove(monId, out var mon)) return;
        SummonOnDie(mon);                                               // C++ DeleteCompanion → CTCompanion::OnDie
        if (mon.InMap) LeaveRecall(mon, exitMap: true, forever: false);
    }

    private void OnCS_CHGMODESPOLECNIKMON_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        uint monId = r.ReadUInt32();
        byte mode = r.ReadByte();
        if (!ch.CompanionObjs.TryGetValue(monId, out var mon)) return;
        mon.Mode = mode;                                                  // CTObjBase::ChgMode — told to everyone around
        var w = new PacketWriter(Msg.CS_CHGMODE_ACK, capacity: 8);
        w.WriteUInt32(mon.Id); w.WriteByte(RecallMon.OtCompanion); w.WriteByte(mode);
        var ack = w.ToArray();
        foreach (var p in _state.PlayersAround(mon)) p.Send(ack);
    }

    private void OnCS_DELSPOLECNIKMON_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        uint monId = r.ReadUInt32();
        byte type = r.ReadByte();
        if (type == RecallMon.OtCompanion) SendMW_SPOLECNIKMONDEL_ACK(ch.CharId, s.Key, monId);
    }

    // ================================ growth ================================

    private void OnCS_COMPANIONUPGRADE_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        byte index = r.ReadByte(), slot = r.ReadByte();
        if (!ch.Companions.TryGetValue(slot, out var c) || c.StatPoints == 0) return;
        if (index < 6 && c.Stats[index] < 30) { c.StatPoints--; c.Stats[index]++; }
        SendCS_COMPANIONUPDATE_REQ(s, c);
        SendCS_CHARSTATINFO_ACK(s, ch);
    }

    private void OnCS_COMPANIONLUP_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        byte slot = r.ReadByte();
        if (!ch.Companions.TryGetValue(slot, out var c)) return;
        if (c.Level >= CompanionMaxLevel) return;
        if (!_templates.CompanionBonuses.TryGetValue(c.BonusId, out var bonus)) return;
        if (c.Exp < c.NextExp) return;

        byte mult = c.Level switch { <= 4 => 1, <= 9 => 2, <= 12 => 3, <= 14 => 4, <= 19 => 5, _ => 0 };
        c.Level++;
        c.StatPoints += mult;
        if (c.Level == 18)
        {
            for (int i = 0; i < 6; i++) c.Stats[i] += 5;
            SendCS_COMPANIONUPDATE_REQ(s, c);
        }
        c.Exp = 0;
        c.NextExp = Companion.NextExpFor(c.Level);
        var w = new PacketWriter(Msg.CS_COMPANIONLUPDATE_REQ);
        w.WriteByte(slot); w.WriteByte(c.Level); w.WriteUInt32(c.Exp); w.WriteByte(c.StatPoints); w.WriteUInt32(c.NextExp);
        w.WriteFloat(bonus.At(c.Level));
        s.Send(w);
        RespawnCompanion(s, ch);
    }

    /// <summary>C++ <c>OnCS_USEPETITEM_REQ</c> — a companion food (stamina) or exp potion.</summary>
    private void OnCS_USEPETITEM_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        uint param = r.ReadUInt32();
        byte slot = r.ReadByte();
        byte invenId = (byte)(param & 0xFFFF), itemSlot = (byte)(param >> 16);
        if (ch.FindInven(invenId) is not { } bag || bag.Items.FirstOrDefault(i => i.ItemSlot == itemSlot) is not { Count: > 0 } item) return;
        if (item.Template is not { Kind: IkCompExp or IkCompHp } t) return;
        if (!ch.Companions.TryGetValue(slot, out var c)) return;

        if (t.Kind == IkCompExp)
        {
            if (c.Level >= CompanionMaxLevel || c.Exp >= c.NextExp) return;
            c.Exp = Math.Min(c.Exp + t.UseValue, c.NextExp);
        }
        else
        {
            if (c.Life >= CompanionMaxLife) return;
            c.Life = Math.Min(c.Life + t.UseValue, CompanionMaxLife);
        }
        UseItemStack(s, invenId, bag, item, 1);
        var w = new PacketWriter(Msg.CS_UPDATECOMPANIONBYITEM_REQ);
        w.WriteUInt32(ch.CharId); w.WriteByte(slot); w.WriteUInt32(c.Life); w.WriteUInt32(c.Exp);
        s.Send(w);
    }

    // ================================ items ================================

    private void OnCS_USECOMPANIONITEM_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte invenId = r.ReadByte(), itemSlot = r.ReadByte(), slot = r.ReadByte();
        if (ch.FindInven(invenId) is not { } bag) return;
        if (bag.Items.FirstOrDefault(i => i.ItemSlot == itemSlot) is not { Count: > 0, Template: { Type: ItCompanionItem } t } item) return;

        if (ch.Companions.TryGetValue(slot, out var c))
        {
            var cur0 = c.ItemIds[0] != 0 ? _templates.Item(c.ItemIds[0]) : null;
            var cur1 = c.ItemIds[1] != 0 ? _templates.Item(c.ItemIds[1]) : null;
            int sub = cur0 is null || (cur0.Kind == t.Kind && (cur1 is null || cur1.Kind != t.Kind)) ? 0 : 1;
            if ((sub == 0 && cur1 is not null && cur1.Kind == t.Kind) || (sub == 1 && cur0 is not null && cur0.Kind == t.Kind))
                sub = 1 - sub;

            var current = sub == 0 ? cur0 : cur1;
            if (current is not null)
            {
                var back = new Item { TemplateId = c.ItemIds[sub], Template = current, Count = 1, EndTime = c.EndTimes[sub] };
                if (!CanPush(ch, new[] { back })) { SendCS_MOVEITEM_ACK(s, MoveItemResult.InvenFull); return; }
                PushTItem(s, new[] { back });
                SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
            }
            c.ItemIds[sub] = t.ItemId;
            c.EndTimes[sub] = item.EndTime;
            SendCS_UPDATECOMPANIONITEMS_REQ(s, slot, (byte)sub, c.ItemIds[sub], c.EndTimes[sub]);
        }
        UseItemStack(s, invenId, bag, item, 1);                          // eaten even for a slot not owned (faithful)
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
    }

    private void OnCS_DELETECOMPITEMS_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        r.ReadByte(); r.ReadByte();
        byte sub = r.ReadByte(), slot = r.ReadByte();
        if (sub > 1 || !ch.Companions.TryGetValue(slot, out var c)) return;
        if (_templates.Item(c.ItemIds[sub]) is not { } tpl) return;

        var back = new Item { TemplateId = c.ItemIds[sub], Template = tpl, Count = 1, EndTime = c.EndTimes[sub] };
        if (!CanPush(ch, new[] { back })) { SendCS_MOVEITEM_ACK(s, MoveItemResult.InvenFull); return; }
        PushTItem(s, new[] { back });
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
        c.ItemIds[sub] = 0; c.EndTimes[sub] = 0;
        SendCS_UPDATECOMPANIONITEMS_REQ(s, slot, sub, 0, 0);
    }

    private void OnCS_CHANGECOMPANIONEFFECT_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte slot = r.ReadByte();
        if (!ch.Companions.TryGetValue(slot, out var c)) return;

        uint price = c.Effect == 0 ? 1500u : 400u;
        if (ch.Medals < price) { SendCS_PETEFFECTCHANGE_ACK(s, EffectNeedCash, 0); return; }
        ch.Medals -= price;
        var m = new PacketWriter(Msg.CS_UPDATEMEDALS_REQ, capacity: 8);
        m.WriteUInt32(ch.Medals);
        s.Send(m);

        byte effect;
        do effect = (byte)(CompanionRng.Next(CompanionMaxEffect - CompanionMinEffect) + 1); while (effect == c.Effect);
        c.Effect = effect;
        RespawnCompanion(s, ch);
    }

    private void OnCS_USECOMPANIONPOWDER_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte invenId = r.ReadByte(), itemSlot = r.ReadByte(), slot = r.ReadByte();
        if (ch.FindInven(invenId) is not { } bag) return;
        if (bag.Items.FirstOrDefault(i => i.ItemSlot == itemSlot) is not { Count: > 0, Template: { Type: ItUse, Kind: IkPowder } } item) return;

        if (ch.Companions.TryGetValue(slot, out var c))
        {
            c.BonusId = RollCompanionBonus(ch);                           // every owned bonus is excluded, its own too
            SendCS_UPDATECOMPANIONBONUS_REQ(s, c);
        }
        UseItemStack(s, invenId, bag, item, 1);
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
        SendCS_CHARSTATINFO_ACK(s, ch);
    }

    private void OnCS_USECOMPRESET_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte invenId = r.ReadByte(), itemSlot = r.ReadByte(), slot = r.ReadByte();
        if (ch.FindInven(invenId) is not { } bag) return;
        if (bag.Items.FirstOrDefault(i => i.ItemSlot == itemSlot) is not { Count: > 0, Template: { Type: ItUse, Kind: IkCompReset } } item) return;

        if (ch.Companions.TryGetValue(slot, out var c))
        {
            byte points = 0;
            for (int i = 0; i < 6; i++) { points += c.Stats[i]; c.Stats[i] = (byte)(c.Level >= 18 ? 5 : 0); }
            if (c.Level >= 18) points -= 30;
            c.StatPoints += points;
            SendCS_COMPANIONLIST_ACK(s, ch);
        }
        UseItemStack(s, invenId, bag, item, 1);
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
        SendCS_CHARSTATINFO_ACK(s, ch);
    }

    /// <summary>C++ <c>OnCS_FINISHCOMPANIONTRANSFER_ACK</c> (CSHandler.cpp:20811) — a transform scroll changes the species
    /// to the one stored on the scroll.</summary>
    private void OnCS_FINISHCOMPANIONTRANSFER_ACK(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte slot = r.ReadByte(), invenId = r.ReadByte(), itemSlot = r.ReadByte();
        if (ch.FindInven(invenId) is not { } bag) return;
        if (bag.Items.FirstOrDefault(i => i.ItemSlot == itemSlot) is not { Count: > 0, Template: { Type: ItUse, Kind: IkPetTransform } } item) return;
        if (!ch.Companions.TryGetValue(slot, out var c)) return;
        uint species = item.Ext[Item.IevCompanion];
        if (c.MonId == species || !_templates.MonsterTemplates.ContainsKey((ushort)species)) return;

        UseItemStack(s, invenId, bag, item, 1);
        c.MonId = species;
        if (slot == ch.CompanionSlot) RespawnCompanion(s, ch);
        SendCS_COMPANIONLIST_ACK(s, ch);
    }

    // ================================ timers ================================

    /// <summary>C++ <c>CTPlayer::OnTimer</c>: every 60 s <c>CzechPetHP</c> (TPlayer.cpp:1054), and each tick
    /// <c>CheckPetEndTime</c> (TPlayer.cpp:4964).</summary>
    private void RunCompanionTimers(long nowMs)
    {
        bool minute = nowMs >= _nextCompanionTickMs;
        if (minute) _nextCompanionTickMs = nowMs + CompanionTickMs;
        long now = UnixNow();
        foreach (var s in _state.AllInGame())
        {
            if (s.Char is not { Companions.Count: > 0 } ch) continue;
            foreach (var c in ch.Companions.Values)
                for (byte i = 0; i < 2; i++)
                    if (c.ItemIds[i] != 0 && c.EndTimes[i] != 0 && c.EndTimes[i] < now)
                    {
                        c.ItemIds[i] = 0; c.EndTimes[i] = 0;
                        SendCS_UPDATECOMPANIONITEMS_REQ(s, c.Slot, i, 0, 0);
                    }
            if (minute) CzechPetHP(s, ch);
        }
    }

    private void CzechPetHP(ClientSession s, Character ch)
    {
        if (ch.SummonedCompanion is not { } c) return;
        uint drop = c.Life <= CompanionLowLife ? 5u : 1u;
        c.Life = c.Life > drop ? c.Life - drop : 0;
        uint gain = c.Life >= 10000 ? 60u : 30u;
        if (c.Level < CompanionMaxLevel) c.Exp = Math.Min(c.Exp + gain, c.NextExp);

        var w = new PacketWriter(Msg.CS_UPDATECOMPANIONBYSYSTEM_REQ);
        w.WriteByte(ch.CompanionSlot); w.WriteUInt32(c.Life); w.WriteUInt32(c.Exp);
        s.Send(w);

        if (c.Life < CompanionLowLife)
        {
            foreach (var obj in ch.CompanionObjs.Values) SendMW_SPOLECNIKMONDEL_ACK(ch.CharId, s.Key, obj.Id);
            ch.CompanionSlot = NoCompanion;
        }
    }

    // ================================ world presence ================================

    /// <summary>C++ <c>InitMap</c> (TMapSvr.cpp:8222): on entering a map the summoned companion is called out — its stats
    /// row at the companion's own level, as the C++ does here.</summary>
    private void CompanionEnterMap(ClientSession s, Character ch)
    {
        foreach (var obj in ch.CompanionObjs.Values.ToList())
            if (!obj.InMap) { ch.CompanionObjs.Remove(obj.Id); }
        if (ch.SummonedCompanion is { } c && _templates.MonsterTemplates.ContainsKey((ushort)c.MonId))
            SpawnCompanion(s, ch, c, attrLevel: c.Level, level: c.Level, effect: c.Effect);
    }

    private void CompanionExitMap(Character ch)
    {
        foreach (var obj in ch.CompanionObjs.Values)
            if (obj.InMap) LeaveRecall(obj, exitMap: false, forever: false);   // C++ LeaveAllCompanion: LeaveMAP(mon, FALSE)
    }

    /// <summary>The owner died (C++ <c>CTPlayer::OnDie</c>): the creature goes, the summoned slot stays — revival brings
    /// it back.</summary>
    private void CompanionOwnerDied(ClientSession s, Character ch)
    {
        foreach (var obj in ch.CompanionObjs.Values) SendMW_SPOLECNIKMONDEL_ACK(ch.CharId, s.Key, obj.Id);
    }

    private void ClearCompanionObjs(Character ch)
    {
        foreach (var obj in ch.CompanionObjs.Values)
            if (obj.InMap) LeaveRecall(obj, exitMap: true, forever: true);
        ch.CompanionObjs.Clear();
    }

    // ================================ owner bonuses ================================

    /// <summary>C++ <c>CTPlayer::CalcPetATTR</c> (TPlayer.cpp:5665): with a companion summoned, its stat plus 1 for each
    /// owned companion (+5 more for each at level 11+ with stamina ≥ 2400).</summary>
    public static float CompanionStat(Character ch, byte mtype)
    {
        if (ch.SummonedCompanion is not { } c || mtype is < 1 or > 6) return 0f;
        byte adder = 0;
        foreach (var x in ch.Companions.Values)
        {
            adder += 1;
            if (x.Level >= 11 && x.Life >= CompanionLowLife) adder += 5;
        }
        return c.Stats[mtype - 1] + adder;
    }

    /// <summary>C++ <c>CTPlayer::CalcPetBonus</c> (TPlayer.cpp:5700): with a companion summoned, the first owned
    /// companion carrying this bonus (level 11+ or the summoned one, stamina ≥ 2400) gives <c>base + mult·(level-1)</c>.</summary>
    public static float CompanionBonusValue(Character ch, byte bonusId, TemplateStore t)
    {
        if (ch.SummonedCompanion is null || !t.CompanionBonuses.TryGetValue(bonusId, out var b)) return 0f;
        foreach (var (slot, x) in ch.Companions)
            if (x.BonusId == bonusId && (x.Level >= 11 || slot == ch.CompanionSlot) && x.Life >= CompanionLowLife)
                return b.At(x.Level);
        return 0f;
    }

    // ================================ senders ================================

    /// <summary>C++ <c>SendCS_COMPANIONLIST_ACK</c> (CSSender.cpp:6387).</summary>
    private void SendCS_COMPANIONLIST_ACK(ClientSession s, Character ch)
    {
        var w = new PacketWriter(Msg.CS_COMPANIONLIST_ACK, capacity: 128);
        w.WriteByte((byte)ch.Companions.Count);
        foreach (var c in ch.Companions.Values)
        {
            w.WriteByte(c.Slot); w.WriteUInt32(c.MonId); w.WriteString(c.Name); w.WriteUInt32(c.Exp); w.WriteUInt32(c.NextExp);
            w.WriteByte(c.Level); w.WriteUInt32(c.Life); w.WriteByte(c.StatPoints); w.WriteByte(c.Effect);
            foreach (var st in c.Stats) w.WriteByte(st);
            for (int i = 0; i < 2; i++) { w.WriteUInt16(c.ItemIds[i]); w.WriteInt64(c.EndTimes[i]); }
            w.WriteUInt32(c.Tick); w.WriteByte(c.BonusId);
            w.WriteFloat(_templates.CompanionBonuses.TryGetValue(c.BonusId, out var b) ? b.At(c.Level) : 0f);
        }
        s.Send(w);
    }

    private static void SendCS_COMPANIONUPDATE_REQ(ClientSession s, Companion c)
    {
        var w = new PacketWriter(Msg.CS_COMPANIONUPDATE_REQ);
        w.WriteByte(c.Slot); w.WriteByte(c.StatPoints);
        foreach (var st in c.Stats) w.WriteByte(st);
        s.Send(w);
    }

    private static void SendCS_UPDATESPAWNEDCOMPANION_REQ(ClientSession s, byte slot)
    {
        var w = new PacketWriter(Msg.CS_UPDATESPAWNEDCOMPANION_REQ);
        w.WriteByte(slot);
        s.Send(w);
    }

    private static void SendCS_CREATECOMPANION_ACK(ClientSession s, byte result, byte slot)
    {
        var w = new PacketWriter(Msg.CS_CREATECOMPANION_ACK);
        w.WriteByte(result); w.WriteByte(slot);
        s.Send(w);
    }

    private static void SendCS_UPDATECOMPANIONITEMS_REQ(ClientSession s, byte slot, byte sub, ushort itemId, long endTime)
    {
        var w = new PacketWriter(Msg.CS_UPDATECOMPANIONITEMS_REQ);
        w.WriteByte(slot); w.WriteByte(sub); w.WriteUInt16(itemId); w.WriteInt64(endTime);
        s.Send(w);
    }

    private void SendCS_UPDATECOMPANIONBONUS_REQ(ClientSession s, Companion c)
    {
        var w = new PacketWriter(Msg.CS_UPDATECOMPANIONBONUS_REQ);
        w.WriteByte(c.Slot); w.WriteByte(c.BonusId);
        w.WriteFloat(_templates.CompanionBonuses.TryGetValue(c.BonusId, out var b) ? b.At(c.Level) : 0f);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_ADDSPOLECNIKMON_ACK</c> (CSSender.cpp:845). The C++ first sends the <b>viewer</b> its own
    /// stat sheet and HP/MP (that is how the owner's bonus reaches its client); kept for every viewer, as there.</summary>
    private void SendCS_ADDSPOLECNIKMON_ACK(ClientSession s, RecallMon m, bool newMember)
    {
        if (s.Char is { } viewer)
        {
            SendCS_CHARSTATINFO_ACK(s, viewer);
            SendSelfHpMp(s, viewer.CharId, MaxHpFor(viewer), viewer.Hp, MaxMpFor(viewer), viewer.Mp);
        }
        var w = new PacketWriter(Msg.CS_ADDSPOLECNIKMON_ACK, capacity: 128);
        w.WriteUInt32(m.OwnerId); w.WriteUInt32(m.Id); w.WriteUInt16(m.ChartId); w.WriteUInt16(m.PetId); w.WriteByte(m.Effect);
        w.WriteString(m.Name); w.WriteByte(m.Country); w.WriteByte(m.AidCountry);
        w.WriteByte(0);                                                   // GetColor — deferred
        w.WriteByte(m.Level);
        w.WriteUInt32(m.MaxHp); w.WriteUInt32(m.Hp); w.WriteUInt32(m.MaxMp); w.WriteUInt32(m.Mp);
        w.WriteFloat(m.PosX); w.WriteFloat(m.PosY); w.WriteFloat(m.PosZ);
        w.WriteUInt16(m.Pitch); w.WriteUInt16(m.Dir); w.WriteByte(m.MouseDir); w.WriteByte(m.KeyDir);
        w.WriteByte(m.Action); w.WriteByte(m.Mode); w.WriteByte((byte)(newMember ? 1 : 0));
        w.WriteUInt32(m.Region); w.WriteByte(m.Template?.RecallType ?? 0); w.WriteByte(m.Hit); w.WriteByte(m.AtkSkillLevel);
        w.WriteUInt16(m.Attr?.AttackLevel ?? 0); w.WriteByte(m.AtkLevel);
        w.WriteUInt32(m.Attr?.AtkMin ?? 0); w.WriteUInt32(m.Attr?.AtkMax ?? 0);
        w.WriteUInt32(0); w.WriteUInt32(0);                               // GetMin/MaxMagicAP (wMAP unloaded)
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_DELSPOLECNIKMON_ACK</c> (CSSender.cpp:1050) — also preceded by the viewer's own stats.</summary>
    private void SendCS_DELSPOLECNIKMON_ACK(ClientSession s, uint hostId, uint monId, bool exitMap)
    {
        if (s.Char is { } viewer)
        {
            SendCS_CHARSTATINFO_ACK(s, viewer);
            SendSelfHpMp(s, viewer.CharId, MaxHpFor(viewer), viewer.Hp, MaxMpFor(viewer), viewer.Mp);
        }
        var w = new PacketWriter(Msg.CS_DELSPOLECNIKMON_ACK, capacity: 16);
        w.WriteUInt32(hostId); w.WriteUInt32(monId); w.WriteByte((byte)(exitMap ? 1 : 0));
        s.Send(w);
    }
}
