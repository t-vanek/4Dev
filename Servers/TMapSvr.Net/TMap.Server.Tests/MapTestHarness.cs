using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;

namespace TMap.Server.Tests;

/// <summary>
/// A DB-free driver for <see cref="MapService"/>: builds the wire packets a world server and a client
/// would send, and captures what the map sends back. No sockets, no DB — the service runs against the
/// in-memory <see cref="MapState"/> exactly as it does on the batch thread.
/// </summary>
internal sealed class MapTestHarness
{
    public MapService Service { get; }
    public MapState State { get; } = new();
    public FakeWorldSink World { get; } = new();
    public MapServerOptions Options { get; }

    /// <summary>The generic monster melee — 2447 of the 3536 live monsters carry it as wSkill1.</summary>
    public const ushort MonsterMelee = 700;

    /// <summary>
    /// Gives a monster chart entry a real attack skill, the shape every live attacking monster has. A monster with
    /// no usable chart skill never swings (C++ BeginAtk/Attack need m_pNextSkill), so a fixture that wants a
    /// monster to attack must register one — the client crashes on an unknown skill id, so there is no
    /// skill-less fallback attack to rely on.
    /// </summary>
    public static TemplateStore WithMonsterMelee(TemplateStore? t = null, ushort chartId = 500)
    {
        t ??= new TemplateStore();
        t.MonsterTemplates[chartId] = t.MonsterTemplates.TryGetValue(chartId, out var existing)
            ? existing with { Skill1 = MonsterMelee }
            : new MonsterTemplate(chartId, 5, 0, Skill1: MonsterMelee);
        t.Skills.TryAdd(MonsterMelee, new SkillTemplate(MonsterMelee, Kind: 0, UseMp: 0, UseMpType: 0, UseHp: 0,
            UseHpType: 0, StartLevel: 1, MaxLevel: 1, NextLevel: 0, ReuseDelay: 0, ReuseDelayInc: 0, LoopDelay: 0,
            KindDelay: 0, SpeedApply: 0, Positive: 0, MapId: 0xFFFF));
        if (t.Skills[MonsterMelee].Data.Count == 0)
            t.Skills[MonsterMelee].Data.Add(new SkillDataRow(0, 1, 1, 30, 1, 0, 0, 0));
        return t;
    }

    public MapTestHarness(TemplateStore? templates = null, IMapEntryStore? entryStore = null)
    {
        _templates = templates ?? new TemplateStore();
        Options = new MapServerOptions { ServerId = 1, Channels = new byte[] { 1 } };
        Service = new MapService(Options, State, World, gameDb: null,
            _templates, NullLogger<MapService>.Instance, entryStore ?? new TestEntryStore());
    }

    private readonly TemplateStore _templates;

    /// <summary>Drive the paid cast before its impact, as the real client does. Older combat-only fixtures
    /// use skill 0 for a synthetic basic attack; give that fixture an explicit learned damage template.
    /// Security tests use Service.DispatchClientAsync directly to exercise unauthorised packets.</summary>
    public async Task CastAndHitAsync(ClientSession s, byte[] packet)
    {
        var body = packet.AsSpan(PacketHeader.Size).ToArray();
        ushort message = PacketHeader.ReadId(packet);
        byte type = body[message == Msg.CS_DEFEND_REQ ? 12 : 8];
        if (type != 1) { await Service.DispatchClientAsync(s, packet); return; }
        ushort skillId = BitConverter.ToUInt16(body, message == Msg.CS_DEFEND_REQ ? 56 : 21);
        uint attacker = BitConverter.ToUInt32(body, message == Msg.CS_DEFEND_REQ ? 4 : 0);
        var targets = new List<(uint id, byte type)>();
        if (message == Msg.CS_DEFEND_REQ) targets.Add((BitConverter.ToUInt32(body, 8), body[13]));
        else for (int i = 0; i < body[33]; i++) targets.Add((BitConverter.ToUInt32(body, 34 + i * 5), body[38 + i * 5]));
        if (skillId == 0 && s.Char is { } ch && ch.Skills.All(k => k.SkillId != 0))
        {
            var basic = new SkillTemplate(0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0xFFFF);
            basic.Data.Add(new SkillDataRow(0, 1, 1, 30, 1, 0, 0, 0));
            _templates.Skills[0] = basic;
            ch.Skills.Add(new Skill { SkillId = 0, Level = 1, Template = basic });
        }
        if (s.Char?.Skills.FirstOrDefault(k => k.SkillId == skillId)?.Template is { } known)
            _templates.Skills.TryAdd(skillId, known);
        await Service.DispatchClientAsync(s, SkillUseReq(attacker, skillId, targets: targets.ToArray()));
        await Service.DispatchClientAsync(s, packet);
    }

    private sealed class TestEntryStore : IMapEntryStore
    {
        public Task<bool> ValidateAsync(MapEntry entry) => Task.FromResult(true);
        public Task<bool> EnterAsync(MapEntry entry) => Task.FromResult(true);
    }

    // ---- client packet builders ----

    public static byte[] ConnectReq(uint charId, uint userId, uint key, byte channel = 1,
        ushort version = Proto.ClientVersion, uint ip = 0x0100007F, ushort port = 5000, long? checksum = null)
    {
        long cs = checksum ?? Proto.ComputeConnectChecksum(version, userId, key, charId);
        var w = new PacketWriter(Msg.CS_CONNECT_REQ);
        w.WriteUInt16(version);
        w.WriteByte(channel);
        w.WriteUInt32(userId);
        w.WriteUInt32(charId);
        w.WriteUInt32(key);
        w.WriteUInt32(ip);
        w.WriteUInt16(port);
        w.WriteInt64(cs);
        return w.ToArray();
    }

    public static byte[] ConReady() => new PacketWriter(Msg.CS_CONREADY_REQ).ToArray();

    public static byte[] CharStatInfoReq(uint charId)
        => new PacketWriter(Msg.CS_CHARSTATINFO_REQ).WriteUInt32(charId).ToArray();

    public static byte[] MoveItemReq(byte srcInven, byte srcSlot, byte dstInven, byte dstSlot, byte count)
    {
        var w = new PacketWriter(Msg.CS_MOVEITEM_REQ);
        w.WriteByte(srcInven); w.WriteByte(srcSlot); w.WriteByte(dstInven); w.WriteByte(dstSlot); w.WriteByte(count);
        return w.ToArray();
    }

    /// <summary>A self-targeted item use (no targets — <paramref name="targets"/> defaults to 0).</summary>
    public static byte[] ItemUseReq(ushort tempId, byte inven, byte slot, ushort delayGroup = 0, byte targets = 0)
    {
        var w = new PacketWriter(Msg.CS_ITEMUSE_REQ);
        w.WriteUInt16(tempId); w.WriteByte(inven); w.WriteByte(slot); w.WriteUInt16(delayGroup); w.WriteByte(targets);
        for (int i = 0; i < targets; i++) { w.WriteUInt32(0); w.WriteByte(0); }
        return w.ToArray();
    }

    /// <summary>A durability-repair request. Defaults to a physical-NPC repair (no portable-smith item):
    /// <paramref name="npcInven"/>=INVEN_NULL and <paramref name="npcItem"/>=INVALID_SLOT skip that branch.</summary>
    public static byte[] DurationRepReq(RepairType type, byte inven, byte item, byte needCost = 0,
        ushort npcId = 0, byte npcInven = 0xFC, byte npcItem = 0xFF)
    {
        var w = new PacketWriter(Msg.CS_DURATIONREP_REQ);
        w.WriteByte(needCost); w.WriteByte((byte)type); w.WriteByte(inven); w.WriteByte(item);
        w.WriteUInt16(npcId); w.WriteByte(npcInven); w.WriteByte(npcItem);
        return w.ToArray();
    }

    /// <summary>A hit report against a target (CS_DEFEND_REQ). Defaults to a player (OT_PC) striking a
    /// monster (OT_MON). The 33-field body is filled with zeros except the fields the handler uses.</summary>
    public static byte[] DefendReq(uint attackerId, uint targetId, byte attackType = 1, byte targetType = 2,
        ushort skillId = 0, byte skillLevel = 1, byte attackerLevel = 10, uint hostId = 0,
        ushort transHp = 0, ushort transMp = 0, uint actId = 0, uint aniId = 0)
    {
        var w = new PacketWriter(Msg.CS_DEFEND_REQ);
        w.WriteUInt32(hostId);       // dwHostID
        w.WriteUInt32(attackerId);   // dwAttackID
        w.WriteUInt32(targetId);     // dwTargetID
        w.WriteByte(attackType);     // bAttackType
        w.WriteByte(targetType);     // bTargetType
        w.WriteUInt16(0);            // wAttackPartyID
        w.WriteUInt32(actId);        // dwActID
        w.WriteUInt32(aniId);        // dwAniID
        w.WriteByte(1);              // bChannel
        w.WriteUInt16(0);            // wMapID
        w.WriteByte(attackerLevel);  // bAttackerLevel
        w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0); // pys/mg min/max power
        w.WriteUInt16(transHp); w.WriteUInt16(transMp); // wTransHP, wTransMP
        w.WriteByte(0);              // bCurseProb
        w.WriteByte(0);              // bEquipSpecial
        w.WriteByte(1);              // bCanSelect
        w.WriteByte(0);              // bAttackCountry
        w.WriteByte(0);              // bAttackAidCountry
        w.WriteUInt16(0);            // wAttackLevel
        w.WriteByte(0);              // bCP
        w.WriteUInt16(skillId);      // wSkillID
        w.WriteByte(skillLevel);     // bSkillLevel
        w.WriteFloat(0); w.WriteFloat(0); w.WriteFloat(0); // fAtkPos
        w.WriteFloat(0); w.WriteFloat(0); w.WriteFloat(0); // fDefPos
        w.WriteUInt32(0);            // dwRemainTick
        return w.ToArray();
    }

    /// <summary>A skill-announce request (CS_SKILLUSE_REQ). Defaults to a player (OT_PC) casting with no
    /// targets; pass <paramref name="targets"/> as (id, type) pairs to fill the target list.</summary>
    public static byte[] SkillUseReq(uint attackerId, ushort skillId, byte attackType = 1, byte actionId = 0,
        uint actId = 0, uint aniId = 0, float x = 0, float y = 0, float z = 0, (uint id, byte type)[]? targets = null)
    {
        targets ??= System.Array.Empty<(uint, byte)>();
        var w = new PacketWriter(Msg.CS_SKILLUSE_REQ);
        w.WriteUInt32(attackerId);   // dwAttackID
        w.WriteByte(attackType);     // bAttackType
        w.WriteByte(1);              // bChannel
        w.WriteUInt16(0);            // wMapID
        w.WriteUInt16(skillId);      // wSkillID
        w.WriteByte(actionId);       // bActionID
        w.WriteUInt32(actId);        // dwActID
        w.WriteUInt32(aniId);        // dwAniID
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z); // fPos
        w.WriteByte((byte)targets.Length); // bCount
        foreach (var (id, type) in targets) { w.WriteUInt32(id); w.WriteByte(type); w.WriteByte(1); }
        return w.ToArray();
    }

    /// <summary>Open a monster corpse's loot list (CS_MONITEMLIST_REQ): bWant + dwMonID.</summary>
    public static byte[] MonItemListReq(uint monId, byte want = 1)
        => new PacketWriter(Msg.CS_MONITEMLIST_REQ).WriteByte(want).WriteUInt32(monId).ToArray();

    /// <summary>Take a corpse's money (CS_MONMONEYTAKE_REQ): dwMonID.</summary>
    public static byte[] MonMoneyTakeReq(uint monId)
        => new PacketWriter(Msg.CS_MONMONEYTAKE_REQ).WriteUInt32(monId).ToArray();

    /// <summary>Take one corpse item (CS_MONITEMTAKE_REQ): dwMonID, bItemID, bInvenID, bSlotID.</summary>
    public static byte[] MonItemTakeReq(uint monId, byte itemId, byte invenId = 0xFF, byte slotId = 0xFF)
    {
        var w = new PacketWriter(Msg.CS_MONITEMTAKE_REQ);
        w.WriteUInt32(monId); w.WriteByte(itemId); w.WriteByte(invenId); w.WriteByte(slotId);
        return w.ToArray();
    }

    /// <summary>A client-reported aggro-bound crossing (CS_ENTERLB/LEAVELB/ENTERAB/LEAVEAB_REQ — the four
    /// share one layout): dwCharID, dwTargetID, bTargetType, dwMonID, bChannel, wMapID.</summary>
    public static byte[] AggroBoundReq(ushort msgId, uint charId, uint targetId, byte targetType, uint monId,
        byte channel = 1, ushort mapId = 0)
    {
        var w = new PacketWriter(msgId);
        w.WriteUInt32(charId); w.WriteUInt32(targetId); w.WriteByte(targetType);
        w.WriteUInt32(monId); w.WriteByte(channel); w.WriteUInt16(mapId);
        return w.ToArray();
    }

    public static byte[] EnterLbReq(uint charId, uint targetId, byte targetType, uint monId,
        byte channel = 1, ushort mapId = 0)
        => AggroBoundReq(Msg.CS_ENTERLB_REQ, charId, targetId, targetType, monId, channel, mapId);

    /// <summary>Open (unlock) a cabinet (CS_CABINETOPEN_REQ): bCabinetID.</summary>
    public static byte[] CabinetOpenReq(byte cabinetId)
        => new PacketWriter(Msg.CS_CABINETOPEN_REQ).WriteByte(cabinetId).ToArray();

    /// <summary>List the cabinets (CS_CABINETLIST_REQ): no body.</summary>
    public static byte[] CabinetListReq() => new PacketWriter(Msg.CS_CABINETLIST_REQ).ToArray();

    /// <summary>List one cabinet's items (CS_CABINETITEMLIST_REQ): bCabinetID.</summary>
    public static byte[] CabinetItemListReq(byte cabinetId)
        => new PacketWriter(Msg.CS_CABINETITEMLIST_REQ).WriteByte(cabinetId).ToArray();

    /// <summary>Deposit a bag item (CS_CABINETPUTIN_REQ): bCabinetID, bInven, bItemID, bCount, bNpcInvenID, bNpcItemID.</summary>
    public static byte[] CabinetPutinReq(byte cabinetId, byte invenId, byte itemSlot, byte count,
        byte npcInven = 0xFC, byte npcItem = 0xFF)
    {
        var w = new PacketWriter(Msg.CS_CABINETPUTIN_REQ);
        w.WriteByte(cabinetId); w.WriteByte(invenId); w.WriteByte(itemSlot); w.WriteByte(count);
        w.WriteByte(npcInven); w.WriteByte(npcItem);
        return w.ToArray();
    }

    /// <summary>Withdraw a stored item (CS_CABINETTAKEOUT_REQ): bCabinetID, dwStItemID, bCount, bInvenID, bItemID, bNpcInvenID, bNpcItemID.</summary>
    public static byte[] CabinetTakeoutReq(byte cabinetId, uint stItemId, byte count, byte invenId, byte itemSlot,
        byte npcInven = 0xFC, byte npcItem = 0xFF)
    {
        var w = new PacketWriter(Msg.CS_CABINETTAKEOUT_REQ);
        w.WriteByte(cabinetId); w.WriteUInt32(stItemId); w.WriteByte(count);
        w.WriteByte(invenId); w.WriteByte(itemSlot); w.WriteByte(npcInven); w.WriteByte(npcItem);
        return w.ToArray();
    }

    /// <summary>Invite a player to trade (CS_DEALITEMASK_REQ): strTarget.</summary>
    public static byte[] DealAskReq(string target)
        => new PacketWriter(Msg.CS_DEALITEMASK_REQ).WriteString(target).ToArray();

    /// <summary>Accept (reply 0) / decline a trade invite (CS_DEALITEMRLY_REQ): bReply, strInviter.</summary>
    public static byte[] DealRlyReq(byte reply, string inviter)
    {
        var w = new PacketWriter(Msg.CS_DEALITEMRLY_REQ);
        w.WriteByte(reply); w.WriteString(inviter);
        return w.ToArray();
    }

    /// <summary>Submit a trade offer (CS_DEALITEMADD_REQ): gold, silver, cooper, count, {bInvenID, bItemID}.</summary>
    public static byte[] DealAddReq(uint gold, uint silver, uint cooper, params (byte inven, byte slot)[] items)
    {
        var w = new PacketWriter(Msg.CS_DEALITEMADD_REQ);
        w.WriteUInt32(gold); w.WriteUInt32(silver); w.WriteUInt32(cooper);
        w.WriteByte((byte)items.Length);
        foreach (var (inven, slot) in items) { w.WriteByte(inven); w.WriteByte(slot); }
        return w.ToArray();
    }

    /// <summary>Confirm (okey 1) / cancel (0) a trade (CS_DEALITEM_REQ): bOkey.</summary>
    public static byte[] DealReq(byte okey) => new PacketWriter(Msg.CS_DEALITEM_REQ).WriteByte(okey).ToArray();

    /// <summary>Open a personal store (CS_STOREOPEN_REQ): strName, count, {gold,silver,cooper,credits,inven,slot,count}.</summary>
    public static byte[] StoreOpenReq(string name,
        params (uint gold, uint silver, uint cooper, uint credits, byte inven, byte slot, byte count)[] items)
    {
        var w = new PacketWriter(Msg.CS_STOREOPEN_REQ);
        w.WriteString(name);
        w.WriteByte((byte)items.Length);
        foreach (var it in items)
        {
            w.WriteUInt32(it.gold); w.WriteUInt32(it.silver); w.WriteUInt32(it.cooper); w.WriteUInt32(it.credits);
            w.WriteByte(it.inven); w.WriteByte(it.slot); w.WriteByte(it.count);
        }
        return w.ToArray();
    }

    /// <summary>Close a personal store (CS_STORECLOSE_REQ): no body.</summary>
    public static byte[] StoreCloseReq() => new PacketWriter(Msg.CS_STORECLOSE_REQ).ToArray();

    /// <summary>Browse a store (CS_STOREITEMLIST_REQ): strTarget.</summary>
    public static byte[] StoreItemListReq(string target)
        => new PacketWriter(Msg.CS_STOREITEMLIST_REQ).WriteString(target).ToArray();

    /// <summary>Buy from a store (CS_STOREITEMBUY_REQ): strTarget, bItem(index), bItemCount.</summary>
    public static byte[] StoreItemBuyReq(string target, byte item, byte count)
    {
        var w = new PacketWriter(Msg.CS_STOREITEMBUY_REQ);
        w.WriteString(target); w.WriteByte(item); w.WriteByte(count);
        return w.ToArray();
    }

    /// <summary>A dead player's revival request (CS_REVIVAL_REQ): fPos + bType (REVIVAL_TYPE).</summary>
    public static byte[] RevivalReq(float x, float y, float z, RevivalType type)
    {
        var w = new PacketWriter(Msg.CS_REVIVAL_REQ);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z); w.WriteByte((byte)type);
        return w.ToArray();
    }

    /// <summary>Open an NPC dialog (CS_NPCTALK_REQ): wNpcID.</summary>
    public static byte[] NpcTalkReq(ushort npcId)
        => new PacketWriter(Msg.CS_NPCTALK_REQ).WriteUInt16(npcId).ToArray();

    /// <summary>Buy an item from an NPC shop (CS_ITEMBUY_REQ). Defaults to a non-quest, non-portable buy:
    /// <paramref name="questId"/>=0, <paramref name="npcInven"/>=INVEN_NULL, <paramref name="npcItem"/>=INVALID_SLOT.</summary>
    public static byte[] ItemBuyReq(ushort npcId, ushort itemId, byte count,
        uint questId = 0, byte npcInven = 0xFC, byte npcItem = 0xFF)
    {
        var w = new PacketWriter(Msg.CS_ITEMBUY_REQ);
        w.WriteUInt16(npcId); w.WriteUInt32(questId); w.WriteUInt16(itemId);
        w.WriteByte(count); w.WriteByte(npcInven); w.WriteByte(npcItem);
        return w.ToArray();
    }

    /// <summary>Sell an inventory item to an NPC (CS_ITEMSELL_REQ). Defaults to a non-portable sell
    /// (<paramref name="npcInven"/>=INVEN_NULL, <paramref name="npcItem"/>=INVALID_SLOT).</summary>
    public static byte[] ItemSellReq(byte inven, byte pos, byte count, byte npcInven = 0xFC, byte npcItem = 0xFF)
    {
        var w = new PacketWriter(Msg.CS_ITEMSELL_REQ);
        w.WriteByte(inven); w.WriteByte(pos); w.WriteByte(count); w.WriteByte(npcInven); w.WriteByte(npcItem);
        return w.ToArray();
    }

    /// <summary>Accept / run a quest (CS_QUESTEXEC_REQ): dwQuestID, bRewardType, dwRewardID.</summary>
    public static byte[] QuestExecReq(uint questId, byte rewardType = 0, uint rewardId = 0)
    {
        var w = new PacketWriter(Msg.CS_QUESTEXEC_REQ);
        w.WriteUInt32(questId); w.WriteByte(rewardType); w.WriteUInt32(rewardId);
        return w.ToArray();
    }

    /// <summary>Abandon a quest (CS_QUESTDROP_REQ): dwQuestID.</summary>
    public static byte[] QuestDropReq(uint questId)
        => new PacketWriter(Msg.CS_QUESTDROP_REQ).WriteUInt32(questId).ToArray();

    /// <summary>Ask which quests the given NPCs offer (CS_QUESTLIST_POSSIBLE_REQ): bCount + wNpcID[].</summary>
    public static byte[] QuestListPossibleReq(params ushort[] npcIds)
    {
        var w = new PacketWriter(Msg.CS_QUESTLIST_POSSIBLE_REQ);
        w.WriteByte((byte)npcIds.Length);
        foreach (var id in npcIds) w.WriteUInt16(id);
        return w.ToArray();
    }

    public static byte[] MoveReq(ushort mapId, float x, float y, float z, ushort dir, float speed,
        ushort pitch = 0, byte mouseDir = 0, byte keyDir = 0, byte action = 0, byte ghost = 0)
    {
        var w = new PacketWriter(Msg.CS_MOVE_REQ);
        w.WriteUInt16(mapId);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        w.WriteUInt16(pitch); w.WriteUInt16(dir);
        w.WriteByte(mouseDir); w.WriteByte(keyDir); w.WriteByte(action); w.WriteByte(ghost);
        w.WriteFloat(speed);
        return w.ToArray();
    }

    public static byte[] JumpReq(uint objId, byte objType, ushort mapId, float x, float y, float z,
        ushort pitch = 0, ushort dir = 0, byte action = 0, byte channel = 1)
    {
        var w = new PacketWriter(Msg.CS_JUMP_REQ);
        w.WriteUInt32(objId); w.WriteByte(objType); w.WriteByte(channel); w.WriteUInt16(mapId);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        w.WriteUInt16(pitch); w.WriteUInt16(dir); w.WriteByte(action);
        return w.ToArray();
    }

    public static byte[] BlockReq(uint objId, byte objType, ushort mapId, float x, float y, float z,
        ushort pitch = 0, ushort dir = 0, byte action = 0, byte block = 0, byte channel = 1)
    {
        var w = new PacketWriter(Msg.CS_BLOCK_REQ);
        w.WriteUInt32(objId); w.WriteByte(objType); w.WriteByte(channel); w.WriteUInt16(mapId);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        w.WriteUInt16(pitch); w.WriteUInt16(dir); w.WriteByte(action); w.WriteByte(block);
        return w.ToArray();
    }

    public static byte[] ChatReq(string sender, ChatGroup group, uint target, string name, string talk)
    {
        var w = new PacketWriter(Msg.CS_CHAT_REQ);
        w.WriteString(sender);
        w.WriteByte((byte)group);
        w.WriteUInt32(target);
        w.WriteString(name);
        w.WriteString(talk);
        return w.ToArray();
    }

    // ---- world → map packet builders (byte layout must match the map's read order) ----

    public static byte[] EnterSvrReq(uint charId, uint key, byte dbLoad = 1)
    {
        var w = new PacketWriter(Msg.MW_ENTERSVR_REQ);
        w.WriteByte(dbLoad);
        w.WriteUInt32(charId);
        w.WriteUInt32(key);
        return w.ToArray();
    }

    public static byte[] CharDataReq(uint charId, uint key)
        => new PacketWriter(Msg.MW_CHARDATA_REQ).WriteUInt32(charId).WriteUInt32(key).ToArray();

    public static byte[] CharInfoReq(uint charId, uint key)
    {
        var w = new PacketWriter(Msg.MW_CHARINFO_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key);
        w.WriteUInt32(0);            // guildId
        w.WriteByte(0);              // guildCountry
        w.WriteString("");           // guildName
        w.WriteUInt32(0);            // fame
        w.WriteUInt32(0);            // fameColor
        w.WriteUInt32(0);            // tacticsId
        w.WriteString("");           // tacticsName
        w.WriteByte(0);              // duty
        w.WriteByte(0);              // peer
        w.WriteUInt16(0);            // castle
        w.WriteByte(0);              // camp
        w.WriteUInt16(0);            // partyId
        w.WriteByte(0);              // partyType
        w.WriteUInt32(0);            // chiefId
        w.WriteUInt16(0);            // titleId
        w.WriteUInt32(0);            // rankPoint
        return w.ToArray();
    }

    public static byte[] RouteReq(uint charId, uint key, byte channel, ushort mapId, float x, float y, float z)
    {
        var w = new PacketWriter(Msg.MW_ROUTE_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key);
        w.WriteByte(channel); w.WriteUInt16(mapId);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        return w.ToArray();
    }

    public static byte[] EnterCharReq(uint charId, uint key, string name, ushort mapId, float x, float y, float z,
        byte level = 10, byte country = 1)
    {
        var w = new PacketWriter(Msg.MW_ENTERCHAR_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key);
        w.WriteByte(0);              // startAct
        w.WriteString(name);
        w.WriteUInt16(mapId);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        w.WriteUInt32(0);            // guildId
        w.WriteUInt32(0);            // fame
        w.WriteUInt32(0);            // fameColor
        w.WriteString("");           // guildName
        w.WriteByte(0);              // duty
        w.WriteByte(0);              // peer
        w.WriteUInt16(0);            // castle
        w.WriteByte(0);              // camp
        w.WriteUInt32(0);            // tacticsId
        w.WriteString("");           // tacticsName
        w.WriteUInt16(0);            // partyId
        w.WriteByte(0);              // partyType
        w.WriteUInt32(0);            // chiefId
        w.WriteUInt16(0);            // commanderId
        w.WriteByte(level);
        w.WriteByte(0);              // helmetHide
        w.WriteByte(country);
        w.WriteByte(3);              // aidCountry: TCONTRY_N (none), as for any non-Broa character
        w.WriteByte(0);              // mode
        return w.ToArray();
    }

    public static byte[] CheckMainReq(uint charId, uint key, byte channel, ushort mapId, float x, float y, float z)
    {
        var w = new PacketWriter(Msg.MW_CHECKMAIN_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key);
        w.WriteByte(channel); w.WriteUInt16(mapId);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        return w.ToArray();
    }

    public static byte[] ConResultReq(uint charId, uint key, ConnectResult result)
    {
        var w = new PacketWriter(Msg.MW_CONRESULT_REQ);
        w.WriteUInt32(charId); w.WriteUInt32(key);
        w.WriteByte((byte)result);
        w.WriteByte(0);              // server-id count
        return w.ToArray();
    }

    // ---- high-level flow ----

    /// <summary>Creates a client, drives the full enter handshake to <see cref="EnterState.InGame"/>,
    /// and returns its session + captured client channel. When <paramref name="preSeeded"/> is supplied it
    /// is used as the character (bypassing the DB-free synthesis), so tests can inject inventory/skills.</summary>
    public async Task<(ClientSession session, FakeClientChannel client)> EnterAsync(
        uint charId, uint userId, uint key, string name = "Hero", ushort mapId = 0, byte channel = 1,
        float x = 3663f, float y = 0f, float z = 557f, Character? preSeeded = null)
    {
        var client = new FakeClientChannel();
        var session = new ClientSession(client);
        if (preSeeded is not null) session.Char = preSeeded;

        await Service.DispatchClientAsync(session, ConnectReq(charId, userId, key, channel));
        await Service.DispatchWorldAsync(EnterSvrReq(charId, key));
        await Service.DispatchWorldAsync(CharDataReq(charId, key));
        await Service.DispatchWorldAsync(CharInfoReq(charId, key));
        await Service.DispatchWorldAsync(RouteReq(charId, key, channel, mapId, x, y, z));
        await Service.DispatchWorldAsync(EnterCharReq(charId, key, name, mapId, x, y, z));
        await Service.DispatchWorldAsync(CheckMainReq(charId, key, channel, mapId, x, y, z));
        await Service.DispatchWorldAsync(ConResultReq(charId, key, ConnectResult.Success));
        await Service.DispatchClientAsync(session, ConReady());
        _clients.Add((session, client));
        return (session, client);
    }

    private readonly List<(ClientSession session, FakeClientChannel client)> _clients = new();

    /// <summary>Finish a world-coordinated move after STARTTELEPORT: verdict, destination connection list,
    /// CHECKMAIN and CONRESULT, then the client's ready packet. Ready alone cannot grant a new world entry.</summary>
    public async Task FinishTeleportAsync(ClientSession s)
    {
        var ch = s.Char!;
        foreach (ushort message in new[] { Msg.MW_TELEPORT_REQ, Msg.MW_CONLIST_REQ })
        {
            var w = new PacketWriter(message).WriteUInt32(s.CharId).WriteUInt32(s.Key).WriteByte(s.Channel)
                .WriteUInt16(ch.MapId).WriteFloat(ch.PosX).WriteFloat(ch.PosY).WriteFloat(ch.PosZ);
            if (message == Msg.MW_TELEPORT_REQ) w.WriteByte(0);
            await Service.DispatchWorldAsync(w.ToArray());
        }
        await Service.DispatchWorldAsync(CheckMainReq(s.CharId, s.Key, s.Channel, ch.MapId, ch.PosX, ch.PosY, ch.PosZ));
        await Service.DispatchWorldAsync(ConResultReq(s.CharId, s.Key, ConnectResult.Success));
        await Service.DispatchClientAsync(s, ConReady());
    }

    /// <summary>One monster-AI tick, then every client plays the host's part: each <c>CS_MONATTACK_ACK</c> it
    /// received during the tick is answered with the <c>CS_DEFEND_REQ</c> the real client sends when that
    /// swing lands. Monster damage is dealt only on that report, so this is how a test sees it.</summary>
    public async Task MonsterTurnAsync(long nowMs)
    {
        var before = _clients.Select(x => x.client.WithId(Msg.CS_MONATTACK_ACK).Count()).ToList();
        Service.RunMonsterAI(nowMs);
        for (int i = 0; i < _clients.Count; i++)
        {
            var (session, client) = _clients[i];
            foreach (var ack in client.WithId(Msg.CS_MONATTACK_ACK).Skip(before[i]).ToList())
            {
                var r = new PacketReader(ack);
                uint monId = r.ReadUInt32(), targetId = r.ReadUInt32();
                r.ReadByte(); r.ReadByte();
                ushort skillId = r.ReadUInt16();
                await Service.DispatchClientAsync(session, MonsterHitReq(monId, targetId, skillId));
            }
        }
    }

    /// <summary>The host client's report that a monster swing landed on a player.</summary>
    public static byte[] MonsterHitReq(uint monId, uint charId, ushort skillId = MonsterMelee)
        => DefendReq(monId, charId, attackType: 2, targetType: 1, skillId: skillId);
}
