using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Pets and mounts (C++ CSHandler.cpp:11547-12015, 15443, 18054-18309; TPlayer.cpp:392-435, 4232). A pet is an
/// account-wide mount licence (<c>TPETTABLE</c>). Using a mount item (<c>IT_PET</c>) makes or extends one
/// (<c>CS_PETMAKE_REQ</c>). Calling it (<c>CS_PETRECALL_REQ</c>) summons a recall monster of type
/// <c>TRECALLTYPE_PET</c> through the world; when it appears the owner's client asks to ride it
/// (<c>CS_PETRIDING_REQ</c>), which sets <c>m_dwRiding</c> and tells everyone around. A saddle (<c>IT_SADDLE</c>) is an
/// account-wide upgrade that switches every mount to its saddled model.
///
/// <para>Pets are written only by the character save (<c>TSavePet</c> for each, like the C++ <c>DM_SAVECHAR</c>) and
/// deleted by <c>TPetDelete</c>. Riding is never saved: leaving the map always dismounts.</para>
///
/// <para><b>Faithful, flagged:</b> the permanent-pet check in <c>CS_PETMAKE_REQ</c> looks up <c>MAKEWORD(race, id)</c>
/// while pets are stored by id, so it only ever matches for race 0; the mount item is used up before the pet can be
/// refused as full or unknown; <c>CS_PETRIDING_REQ</c> does not check the mount id is the player's own pet; and a
/// permanent pet can be deleted. <b>Changed:</b> <c>CS_PETEFFECTCHANGE_REQ</c> checks the pet exists before reading it
/// (the C++ read it first) and does not change the shared monster template. <b>Not ported:</b> duels, tournament maps,
/// the secure code, and Korean premium / PC-bang pets.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte PetSuccess = 0, PetFail = 1, PetNotItem = 2, PetNotFound = 3, PetFull = 4, PetUseTime = 5;   // PET_RESULT
    private const byte EffectSuccess = 0, EffectFail = 1, EffectNeedCash = 2, EffectDeleteSuccess = 3;             // PET_EFFECTCHANGE
    private const byte PetActionRiding = 1, PetActionDismount = 2;                                                  // PET_ACTION
    private const byte ItPet = 12, IkPet = 23, ItSaddle = 21, IkSaddle = 98, IkWhip = 99;
    private const byte DuringTypeTime = 0x01, DuringTypeDay = 0x02;
    private const int MaxPetCount = 232;               // MAX_PET_COUNT 1000 stored in a BYTE
    private const long PetLiveDuration = 604800;       // PET_LIVE_DURATION (7 days)
    private const byte MaxEffect = 39;
    private const ushort TblockRideSkill = 805;
    private const ushort PcbangPet = 102, Premium2Pet = 103;
    private const byte BeaRide = 3;
    private const int MaxPetName = 50;

    /// <summary>The pet database — the game database in production, a fake in tests.</summary>
    public IPetStore? PetStore { get; set; }

    // ================================ load / save ================================

    /// <summary>C++ <c>OnDM_LOADCHAR_ACK</c> (SSHandler.cpp:5117-5174): the account's saddle and pets. Pets whose mount
    /// no longer exists are left out.</summary>
    private async Task LoadPetsAsync(ClientSession s, Character ch)
    {
        if (PetStore is not { } db || s.UserId == 0) return;
        var saddle = await db.GetSaddleAsync(s.UserId);
        var pets = await db.LoadPetsAsync(s.UserId);
        ch.Saddle = saddle.ItemId != 0 ? saddle : null;
        foreach (var row in pets)
            if (_templates.Mounts.TryGetValue(row.PetId, out var tpl))
                ch.Pets[row.PetId] = new Pet { PetId = row.PetId, Name = row.Name, EndTime = row.EndTime, Effect = row.Effect, Template = tpl };
    }

    /// <summary>The pets part of the character save (C++ <c>OnDM_SAVECHAR_REQ</c>, SSHandler.cpp:7343): one
    /// <c>TSavePet</c> per pet — there is no delete step here.</summary>
    private async Task SavePetsAsync(uint charId, IReadOnlyList<PetRow> pets)
    {
        if (PetStore is not { } db) return;
        foreach (var p in pets) await db.SavePetAsync(charId, p);
    }

    private static List<PetRow> PetSnapshot(Character ch)
        => ch.Pets.Values.Select(p => new PetRow(p.PetId, p.Name, p.EndTime, p.Effect)).ToList();

    // ================================ make / delete ================================

    private void OnCS_PETMAKE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte invenId = r.ReadByte(), slot = r.ReadByte();
        string name = r.ReadString();
        if (s.Store.IsOpen || s.Deal.Status != (byte)DealStatus.Ready) return;

        if (name.Length > MaxPetName) { SendCS_PETMAKE_ACK(s, PetNotFound, 0, "", 0); return; }
        if (ch.FindInven(invenId) is not { } bag) { SendCS_PETMAKE_ACK(s, PetNotItem, 0, "", 0); return; }
        if (bag.Items.FirstOrDefault(i => i.ItemSlot == slot) is not { Count: > 0 } item)
        { SendCS_PETMAKE_ACK(s, PetNotItem, 0, "", 0); return; }
        if (item.Template is not { Type: ItPet, Kind: IkPet } t) { SendCS_PETMAKE_ACK(s, PetNotItem, 0, "", 0); return; }
        if (!item.CanUse) { SendCS_PETMAKE_ACK(s, PetFail, 0, "", 0); return; }

        ushort petId = t.UseValue;
        long duration = (t.UseType & DuringTypeTime) != 0 ? t.UseTime * 3600L
            : (t.UseType & DuringTypeDay) != 0 ? t.UseTime * 86400L : 0;

        // C++ m_mapTPET.find(MAKEWORD(m_bRace, wPetID)) — see the class remarks.
        ushort key = (ushort)(ch.Race | (petId << 8));
        if (ch.Pets.TryGetValue(key, out var perm) && perm.EndTime == 0) { SendCS_PETMAKE_ACK(s, PetUseTime, 0, "", 0); return; }

        UseItemStack(s, invenId, bag, item, 1);
        if (PetMake(s, ch, petId, name, duration) is { } pet)
            SendCS_PETMAKE_ACK(s, PetSuccess, pet.PetId, pet.Name, pet.EndTime);
    }

    /// <summary>C++ <c>CTPlayer::PetMake</c> (TPlayer.cpp:392): rename and extend an owned pet, or add a new one.</summary>
    private Pet? PetMake(ClientSession s, Character ch, ushort petId, string name, long duration)
    {
        long now = UnixNow();
        if (ch.Pets.TryGetValue(petId, out var pet))
        {
            pet.Name = name;
            if (pet.EndTime < now || petId is PcbangPet or Premium2Pet) pet.EndTime = now + duration;
            else pet.EndTime += duration;
            if (duration == 0) pet.EndTime = 0;
            return pet;
        }
        if (ch.Pets.Count >= MaxPetCount) { SendCS_PETMAKE_ACK(s, PetFull, 0, "", 0); return null; }
        if (!_templates.Mounts.TryGetValue(petId, out var tpl)) { SendCS_PETMAKE_ACK(s, PetNotFound, 0, "", 0); return null; }
        pet = new Pet { PetId = petId, Name = name, EndTime = duration != 0 ? now + duration : 0, Template = tpl };
        ch.Pets[petId] = pet;
        return pet;
    }

    private async Task OnCS_PETDEL_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        ushort petId = r.ReadUInt16();
        if (!ch.Pets.TryGetValue(petId, out var pet)) { SendCS_PETDEL_ACK(s, PetNotFound, petId); return; }
        if (pet.EndTime > UnixNow()) { SendCS_PETDEL_ACK(s, PetUseTime, petId); return; }

        ch.Pets.Remove(petId);
        SendCS_PETDEL_ACK(s, PetSuccess, petId);
        if (PetStore is { } db) { uint user = s.UserId; await EnqueueDbWrite(() => db.DeletePetAsync(user, petId)); }
    }

    // ================================ call / dismiss ================================

    private void OnCS_PETRECALL_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        if (s.State != EnterState.InGame || !s.IsMain) { SendCS_PETRECALL_ACK(s, PetFail); return; }
        if (HasRideBlockingBuff(ch)) { SendCS_PETRECALL_ACK(s, PetFail); return; }
        ushort petId = r.ReadUInt16();

        RefreshSaddle(s, ch);
        if (ch.Hp == 0) { SendCS_PETRECALL_ACK(s, PetFail); return; }
        if (!ch.Pets.TryGetValue(petId, out var pet) || pet.Template is not { } mount) { SendCS_PETRECALL_ACK(s, PetNotFound); return; }
        long now = UnixNow();
        if (pet.EndTime != 0 && pet.EndTime <= now) { SendCS_PETRECALL_ACK(s, PetUseTime); return; }

        ushort monId = ch.Saddle is not null ? mount.SaddleMonId : mount.NormalMonId;
        if (!_templates.MonsterTemplates.TryGetValue(monId, out var tpl)) { SendCS_PETRECALL_ACK(s, PetNotFound); return; }
        if (ch.MaintainSkills.Any(m => m.SkillId == TblockRideSkill)) { SendCS_PETRECALL_ACK(s, PetFail); return; }

        if (ch.FindRecallPet() is { } old) SendMW_RECALLMONDEL_ACK(ch.CharId, s.Key, old.Id);

        long live = Math.Min(PetLiveDuration, pet.EndTime != 0 ? pet.EndTime - now : 0) * 1000;
        SendMW_CREATERECALLMON_ACK(new RecallRecord(ch.CharId, s.Key, 0, monId, (uint)(tpl.SummonAttr | (ch.Level << 16)),
            pet.PetId, pet.Effect, pet.Name, ch.Level, tpl.Class, tpl.Race, TaStand, 1 /* OS_WAKEUP */, MtNormal,
            0, 0, 0, 0, 100, 1, ch.PosX, ch.PosY, ch.PosZ, ch.Dir, (uint)live, 0, 0, 0, tpl.Skills.ToList()));
        // No success ACK: the client clears its "calling" state when CS_ADDRECALLMON_ACK arrives.
    }

    private void OnCS_PETCANCEL_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        if (ch.FindRecallPet() is { } pet) SendMW_RECALLMONDEL_ACK(ch.CharId, s.Key, pet.Id);
        else SendCS_PETRECALL_ACK(s, PetFail);
    }

    // ================================ riding ================================

    private void OnCS_PETRIDING_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        uint monId = r.ReadUInt32();
        byte action = r.ReadByte();

        RefreshSaddle(s, ch);
        if (ch.FindRecallPet() is null) return;
        if (s.Store.IsOpen || ch.Hp == 0) return;
        if (action == PetActionRiding && ch.MaintainSkills.Any(m => m.Template?.IsRide == true)) return;

        if (action == PetActionRiding)
        {
            EraseBuffByRide(s, ch);
            PetRiding(s, ch, monId);
        }
        else if (action == PetActionDismount) PetRiding(s, ch, 0);
    }

    /// <summary>C++ <c>CTPlayer::PetRiding</c> (TPlayer.cpp:4232): tell the world and everyone around (the rider too).</summary>
    private void PetRiding(ClientSession s, Character ch, uint riding)
    {
        if (!s.IsMain || riding == ch.Riding) return;
        uint monId = riding != 0 ? riding : ch.Riding;
        ch.Riding = riding;

        var mw = new PacketWriter(Msg.MW_PETRIDING_ACK, capacity: 16);
        mw.WriteUInt32(ch.CharId); mw.WriteUInt32(s.Key); mw.WriteUInt32(riding);
        _world.Send(mw);

        var w = new PacketWriter(Msg.CS_PETRIDING_ACK, capacity: 16);
        w.WriteByte(PetSuccess); w.WriteUInt32(ch.CharId); w.WriteUInt32(monId);
        w.WriteByte(riding != 0 ? PetActionRiding : PetActionDismount);
        var ack = w.ToArray();
        foreach (var p in _state.InView(s)) p.Send(ack);
    }

    /// <summary>C++ <c>OnMW_PETRIDING_REQ</c> (SSHandler.cpp:13004) — another server's copy of the rider's state.</summary>
    private void OnMW_PETRIDING_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32(), riding = r.ReadUInt32();
        if (FindPlayer(charId, key) is { Char: { } ch }) ch.Riding = riding;
    }

    /// <summary>C++ <c>CTObjBase::EraseBuffByRide</c> (TObjBase.cpp:4700): buffs that end on mounting go.</summary>
    private void EraseBuffByRide(ClientSession s, Character ch)
    {
        for (int i = 0; i < ch.MaintainSkills.Count;)
        {
            if (ch.MaintainSkills[i].Template is { } t && (t.EraseAct & BeaRide) != 0) EraseMaintainPlayer(s, ch, i);
            else i++;
        }
    }

    /// <summary>A maintained buff flagged <c>m_bIsRide</c> that is not a hide skill blocks calling a pet
    /// (CSHandler.cpp:11677).</summary>
    private static bool HasRideBlockingBuff(Character ch)
        => ch.MaintainSkills.Any(m => m.Template is { IsRide: true, IsHideSkill: false });

    // ================================ mount effect ================================

    private void OnCS_PETEFFECTCHANGE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        ushort petId = r.ReadUInt16();
        byte effect = r.ReadByte();
        ushort price = r.ReadUInt16();

        if (effect > MaxEffect || ch.Hp == 0) { SendCS_PETEFFECTCHANGE_ACK(s, EffectFail, effect); return; }
        if (!ch.Pets.TryGetValue(petId, out var pet) || pet.Template is not { } mount) { SendCS_PETEFFECTCHANGE_ACK(s, EffectFail, effect); return; }

        uint realPrice = pet.Effect == 0 ? 1500u : 400u;                // the client's price is not trusted
        if (effect == 0 && price == 0) realPrice = 0;
        if (pet.EndTime != 0 && pet.EndTime <= UnixNow()) { SendCS_PETEFFECTCHANGE_ACK(s, EffectFail, effect); return; }
        ushort monId = ch.Saddle is not null ? mount.SaddleMonId : mount.NormalMonId;
        if (!_templates.MonsterTemplates.ContainsKey(monId)) { SendCS_PETEFFECTCHANGE_ACK(s, EffectFail, effect); return; }

        if (ch.Medals < realPrice) { SendCS_PETEFFECTCHANGE_ACK(s, EffectNeedCash, 0); return; }
        pet.Effect = effect;
        ch.Medals -= realPrice;
        var m = new PacketWriter(Msg.CS_UPDATEMEDALS_REQ, capacity: 8);
        m.WriteUInt32(ch.Medals);
        s.Send(m);
        if (price == 0 && effect == 0) SendCS_PETEFFECTCHANGE_ACK(s, EffectDeleteSuccess, 0);
        else SendCS_PETEFFECTCHANGE_ACK(s, EffectSuccess, effect);
    }

    // ================================ saddle ================================

    /// <summary>The saddle check the C++ repeats in PETRECALL / PETRIDING / REQUESTSADDLE: an expired timed saddle is
    /// deleted, then the client is told what the account has.</summary>
    private void RefreshSaddle(ClientSession s, Character ch)
    {
        if (ch.Saddle is { } sd && sd.EndTime < UnixNow() && sd.EndTime != 0 && sd.Type == 0)
        {
            ch.Saddle = null;
            if (PetStore is { } db) { uint user = s.UserId; _ = EnqueueDbWrite(() => db.DeleteSaddleAsync(user)); }
        }
        var cur = ch.Saddle ?? default;
        SendCS_SENDSADDLE_REQ(s, cur.ItemId, cur.EndTime, cur.Type, openUi: false);
    }

    private void OnCS_REQUESTSADDLE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        RefreshSaddle(s, ch);
    }

    /// <summary>C++ <c>OnCS_CREATESADDLE_REQ</c> (CSHandler.cpp:18101): fit a saddle — a new one, more time on the same
    /// kind, or a swap that gives the old one back as an item. The called mount is sent away and the rider dismounted.</summary>
    private async Task OnCS_CREATESADDLE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte invenId = r.ReadByte(), slot = r.ReadByte();
        if (ch.FindInven(invenId) is not { } bag) return;
        if (bag.Items.FirstOrDefault(i => i.ItemSlot == slot) is not { Count: > 0 } item
            || item.Template is not { Type: ItSaddle, Kind: IkSaddle } t) return;

        long now = UnixNow();
        long end = item.EndTime != 0 ? item.EndTime : t.UseTime * 86400L + now;
        byte permanent = (byte)(t.UseTime == 0 ? 1 : 0);
        SaddleRow saddle;

        if (ch.Saddle is not { } old)
        {
            saddle = new SaddleRow(t.ItemId, end, permanent);
        }
        else
        {
            if (_templates.Item(old.ItemId) is not { } oldTpl) return;
            if (oldTpl.UseValue == t.UseValue && t.UseTime != 0 && oldTpl.UseTime != 0)
            {
                long extra = item.EndTime != 0 ? item.EndTime - now : t.UseTime * 86400L;
                saddle = old with { EndTime = old.EndTime + extra };
            }
            else
            {
                var back = new Item { TemplateId = old.ItemId, Template = oldTpl, Count = 1, EndTime = old.Type == 0 ? old.EndTime : 0 };
                if (!CanPush(ch, new[] { back })) { SendCS_MOVEITEM_ACK(s, MoveItemResult.InvenFull); return; }
                PushTItem(s, new[] { back });
                SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
                saddle = new SaddleRow(t.ItemId, end, permanent);
            }
        }

        ch.Saddle = saddle;
        SendCS_SENDSADDLE_REQ(s, saddle.ItemId, saddle.EndTime, saddle.Type, openUi: true);
        if (ch.FindRecallPet() is { } pet) SendMW_RECALLMONDEL_ACK(ch.CharId, s.Key, pet.Id);
        PetRiding(s, ch, 0);
        UseItemStack(s, invenId, bag, item, 1);
        SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
        if (PetStore is { } db) { uint user = s.UserId; await EnqueueDbWrite(() => db.SetSaddleAsync(user, saddle)); }
    }

    /// <summary>C++ <c>OnCS_DELETESADDLE_REQ</c> (CSHandler.cpp:18244): take the saddle off, back into the bag.</summary>
    private async Task OnCS_DELETESADDLE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        r.ReadByte(); r.ReadByte(); r.ReadUInt16();                                     // bInvenID · bItemID · wSaddle — unused
        if (ch.Saddle is not { } old) return;

        if (_templates.Item(old.ItemId) is { } tpl)
        {
            var back = new Item { TemplateId = old.ItemId, Template = tpl, Count = 1, EndTime = old.Type == 0 ? old.EndTime : 0 };
            if (!CanPush(ch, new[] { back })) { SendCS_MOVEITEM_ACK(s, MoveItemResult.InvenFull); return; }
            PushTItem(s, new[] { back });
            SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
            ch.Saddle = null;
            SendCS_SENDSADDLE_REQ(s, 0, 0, 0, openUi: true);
            if (PetStore is { } db) { uint user = s.UserId; await EnqueueDbWrite(() => db.DeleteSaddleAsync(user)); }
        }
        if (ch.FindRecallPet() is { } pet) SendMW_RECALLMONDEL_ACK(ch.CharId, s.Key, pet.Id);
        PetRiding(s, ch, 0);
    }

    // ================================ senders ================================

    private static void SendCS_PETMAKE_ACK(ClientSession s, byte result, ushort petId, string name, long endTime)
    {
        var w = new PacketWriter(Msg.CS_PETMAKE_ACK);
        w.WriteByte(result); w.WriteUInt16(petId); w.WriteString(name); w.WriteInt64(endTime);
        s.Send(w);
    }

    private static void SendCS_PETDEL_ACK(ClientSession s, byte result, ushort petId)
    {
        var w = new PacketWriter(Msg.CS_PETDEL_ACK);
        w.WriteByte(result); w.WriteUInt16(petId);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_PETLIST_ACK</c> (CSSender.cpp:4228) — in pet-id order (a C++ <c>map</c>).</summary>
    private static void SendCS_PETLIST_ACK(ClientSession s, Character ch)
    {
        var w = new PacketWriter(Msg.CS_PETLIST_ACK);
        w.WriteByte((byte)ch.Pets.Count);
        foreach (var p in ch.Pets.Values.OrderBy(p => p.PetId))
        {
            w.WriteUInt16(p.PetId); w.WriteString(p.Name); w.WriteInt64(p.EndTime); w.WriteByte(p.Effect);
        }
        s.Send(w);
    }

    private static void SendCS_PETRECALL_ACK(ClientSession s, byte result)
    {
        var w = new PacketWriter(Msg.CS_PETRECALL_ACK);
        w.WriteByte(result);
        s.Send(w);
    }

    private static void SendCS_PETEFFECTCHANGE_ACK(ClientSession s, byte result, byte effect)
    {
        var w = new PacketWriter(Msg.CS_PETEFFECTCHANGE_ACK);
        w.WriteByte(result); w.WriteByte(effect);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_SENDSADDLE_REQ</c> (CSSender.cpp:6315) — <c>dwItemID · bType · endTime · BOOL bOpenUI</c>.</summary>
    private static void SendCS_SENDSADDLE_REQ(ClientSession s, uint itemId, long endTime, byte type, bool openUi)
    {
        var w = new PacketWriter(Msg.CS_SENDSADDLE_REQ);
        w.WriteUInt32(itemId); w.WriteByte(type); w.WriteInt64(endTime); w.WriteUInt32(openUi ? 1u : 0u);
        s.Send(w);
    }
}
