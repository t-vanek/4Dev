using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Guilds, batch G2 — the guild's own things (C++ CSHandler.cpp:4090–4801, 14980, 15068; SSHandler.cpp:8633, 8927–9222,
/// 15642, 15705; the cabinet's DM handlers):
/// <list type="bullet">
/// <item><b>The guild cabinet</b> (a vice-chief or the chief, not a tactics member): its list comes from the world; putting an
/// item in takes it (or part of a stack) out of the bag at once and stores it (<c>TGuildItemPutIn</c>) — refused, it comes back;
/// taking one out reads it from the cabinet (<c>TGuildItemTakeOut</c>) into the asked slot, or stacks / pushes it — with no
/// room it goes back (<c>TGuildItemRollback</c>). The world keeps the cabinet's list in step and sends it again.</item>
/// <item><b>Contribution</b>: exp (from level 20), money and useable PvP points to the guild — checked here, taken once the
/// world has it.</item>
/// <item><b>The board</b> (articles, a vice-chief or the chief), <b>the fame</b> (the chief), <b>recruiting</b> — the wanted
/// post (a vice-chief or the chief), applying to one, the applicants and the answer — and the guild's <b>point log</b> and
/// <b>PvP record</b>: relayed, the world's lists passed on as they are.</item>
/// </list>
/// <para><b>Fixed C++ bugs:</b> the C++ map read a cabinet item from the world with two more fields (companion, custom
/// texture) than the C++ world writes — the world's layout is used both ways here; and an item taken out of a cabinet stack
/// came back with the stack's own row id (two rows, one id) — it gets a fresh id. <b>Deviations:</b> the money and exp a
/// contribution takes are saved with the character (no separate <c>TSaveMoney</c> / <c>TSaveExp</c>); the database answers
/// come back on the next timer tick. <b>Not ported:</b> the security-code lock, the UDP guild log; <c>CS_GUILDLOCALRETURN</c>
/// does nothing in the C++ either.</para>
/// </summary>
public sealed partial class MapService
{
    // TGUILD_CABINET_RESULT, TGUILD_CONTRIBUTION_RESULT (NetCode.h:466/475)
    private const byte GuildCabinetSuccess = 0, GuildCabinetFail = 1, GuildCabinetInvenFull = 4, GuildCabinetNotDuty = 6;
    private const byte GuildContributionNotEnough = 1;
    private const byte GuildNotFound = 9;

    private static void SendByte(ClientSession s, ushort id, byte value) => s.Send(new PacketWriter(id, capacity: 1).WriteByte(value));

    private void RelayToClient(PacketReader r, ushort csId)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        if (FindPlayer(charId, key) is { } s) s.Send(new PacketWriter(csId).WriteRaw(r.ReadBytes(r.Remaining)));
    }

    private void RelayResult(PacketReader r, ushort csId)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte result = r.ReadByte();
        if (FindPlayer(charId, key) is { } s) SendByte(s, csId, result);
    }

    // ================================ the cabinet ================================

    /// <summary>C++ <c>OnCS_GUILDCABINETLIST_REQ</c> (CSHandler.cpp:4104).</summary>
    private void OnCS_GUILDCABINETLIST_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is not { GuildId: not 0, TacticsId: 0 } ch || IsTutorial(ch) || !CheckGuildDuty(ch, GuildDutyViceChief)) return;
        SendToWorld(Msg.MW_GUILDCABINETLIST_ACK, s);
    }

    /// <summary>C++ <c>OnMW_GUILDCABINETLIST_REQ</c> (SSHandler.cpp:8633): the world's cabinet, each item read in the world's
    /// layout (<c>CTServer::WrapItem</c>), to the client — in reverse, as the C++ pops them off its list.</summary>
    private void OnMW_GUILDCABINETLIST_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte max = r.ReadByte(), count = r.ReadByte();
        var items = new List<Item>();
        for (int i = 0; i < count; i++)
        {
            uint slot = r.ReadUInt32();
            var it = ReadWorldItem(r);
            it.StItemId = slot;
            items.Add(it);
        }
        if (FindPlayer(charId, key) is not { IsMain: true } s) return;
        var w = new PacketWriter(Msg.CS_GUILDCABINETLIST_ACK, capacity: 16 + items.Count * 48);
        w.WriteByte(max); w.WriteByte((byte)items.Count);
        for (int i = items.Count - 1; i >= 0; i--) { w.WriteUInt32(items[i].StItemId); items[i].WrapPacketClient(w, s.CharId, addItemId: false); }
        s.Send(w);
    }

    /// <summary>An item in the world's server-plane layout (C++ <c>CTServer::WrapItem</c> / <c>CTWorldSvrModule::CreateItem</c>).</summary>
    private Item ReadWorldItem(PacketReader r)
    {
        var it = new Item { DlId = r.ReadInt64(), ItemSlot = r.ReadByte(), TemplateId = r.ReadUInt16(), Level = r.ReadByte(), Gem = r.ReadByte(),
            MoggItemId = r.ReadUInt16(), Count = r.ReadByte(), GLevel = r.ReadByte(), DuraMax = r.ReadUInt32(), DuraCur = r.ReadUInt32(),
            RefineCur = r.ReadByte(), EndTime = r.ReadInt64(), GradeEffect = r.ReadByte() };
        for (int e = Item.IevEld; e <= Item.IevGuild; e++) it.Ext[e] = r.ReadUInt32();
        byte n = r.ReadByte();
        var ids = new byte[n]; var values = new ushort[n];
        for (int i = 0; i < n; i++) { ids[i] = r.ReadByte(); values[i] = r.ReadUInt16(); }
        it.Template = _templates.Item(it.TemplateId);
        Item.AddPersistedMagic(it, ids, values, _templates);
        LinkItemAttr(it);
        return it;
    }

    private static void WriteWorldItem(PacketWriter w, Item it)
    {
        w.WriteInt64(it.DlId); w.WriteByte(it.ItemSlot); w.WriteUInt16(it.TemplateId); w.WriteByte(it.Level); w.WriteByte(it.Gem);
        w.WriteUInt16(it.MoggItemId); w.WriteByte(it.Count); w.WriteByte(it.GLevel); w.WriteUInt32(it.DuraMax); w.WriteUInt32(it.DuraCur);
        w.WriteByte(it.RefineCur); w.WriteInt64(it.EndTime); w.WriteByte(it.GradeEffect);
        for (int e = Item.IevEld; e <= Item.IevGuild; e++) w.WriteUInt32(it.Ext[e]);
        w.WriteByte((byte)it.Magic.Count);
        foreach (var m in it.Magic) { w.WriteByte(m.Id); w.WriteUInt16(m.Value); }
    }

    /// <summary>C++ <c>OnCS_GUILDCABINETPUTIN_REQ</c> (CSHandler.cpp:4126) + <c>OnDM_GUILDCABINETPUTIN_REQ/ACK</c>.</summary>
    private void OnCS_GUILDCABINETPUTIN_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is not { GuildId: not 0, TacticsId: 0 } ch || IsTutorial(ch)) return;
        if (s.Store.IsOpen || s.Deal.InProgress) return;
        if (!CheckGuildDuty(ch, GuildDutyViceChief)) { SendByte(s, Msg.CS_GUILDCABINETPUTIN_ACK, GuildCabinetNotDuty); return; }
        byte invId = r.ReadByte(), slot = r.ReadByte(), count = r.ReadByte();
        if (ch.GuildItem is not null) return;
        if (ch.FindInven(invId) is not { } inv || inv.FindItem(slot) is not { } item || item.Count < count || count == 0) return;
        if (!item.CanDeal() || item.Template is not { } t || (t.IsSell & ItemtradeCabinet) == 0) { SendByte(s, Msg.CS_GUILDCABINETPUTIN_ACK, GuildCabinetFail); return; }

        // C++ CopyGuildItem(pItem, bCount, pItem->m_bCount - bCount): a part of a stack is a new item (a new row id).
        var copy = item.Clone();
        copy.Count = count;
        copy.DlId = item.Count != count ? (_itemIdReady ? GenItemId() : 0) : item.DlId;
        ch.GuildItem = copy;
        item.Count -= count;
        if (item.Count == 0) { inv.Items.Remove(item); SendCS_DELITEM_ACK(s, invId, item); }
        else SendCS_UPDATEITEM_ACK(s, invId, item);

        uint guild = ch.GuildId;
        void Done(int ret, uint cabinetSlot)
        {
            if (FindPlayer(s.CharId, s.Key) is not { Char: { } c } p) return;
            SendByte(p, Msg.CS_GUILDCABINETPUTIN_ACK, (byte)ret);
            if (c.GuildItem is not { } pending) return;
            c.GuildItem = null;
            if (ret != 0)
            {
                if (CanPush(c, new[] { pending })) PushTItem(p, new[] { pending });   // C++ BackGuildItem
                SendToWorld(Msg.MW_GUILDCABINETLIST_ACK, p);
                return;
            }
            SendToWorld(Msg.MW_GUILDCABINETPUTIN_ACK, p, w => { w.WriteUInt32(cabinetSlot); WriteWorldItem(w, pending); });
        }
        if (_gameDb is not { } db) { Done(0, 1); return; }
        var row = BuildItemSave(0, copy);
        _ = EnqueueDbWrite(async () =>
        {
            (int ret, uint id) res;
            try { res = await db.GuildItemPutInAsync(guild, row); }
            catch (Exception ex) { _log.LogWarning(ex, "TGuildItemPutIn failed."); res = (GuildNotFound, 0); }
            _dbResults.Enqueue(() => Done(res.ret, res.id));
        });
    }

    /// <summary>C++ <c>OnCS_GUILDCABINETTAKEOUT_REQ</c> (CSHandler.cpp:4199) + <c>OnDM_GUILDCABINETTAKEOUT_REQ/ACK</c>.</summary>
    private void OnCS_GUILDCABINETTAKEOUT_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is not { GuildId: not 0, TacticsId: 0 } ch || IsTutorial(ch)) return;
        if (s.Store.IsOpen || s.Deal.InProgress) return;
        if (!CheckGuildDuty(ch, GuildDutyViceChief)) { SendByte(s, Msg.CS_GUILDCABINETTAKEOUT_ACK, GuildCabinetNotDuty); return; }
        uint cabinetSlot = r.ReadUInt32();
        byte count = r.ReadByte(), invId = r.ReadByte(), slot = r.ReadByte();
        if (count == 0 || invId == Proto.InvenEquip || ch.FindInven(invId) is not { } target || !target.ContainsSlot(slot))
        {
            SendByte(s, Msg.CS_GUILDCABINETTAKEOUT_ACK, GuildCabinetFail);
            SendToWorld(Msg.MW_GUILDCABINETLIST_ACK, s);
            return;
        }
        uint guild = ch.GuildId;
        void Done(int ret, Item? taken)
        {
            if (FindPlayer(s.CharId, s.Key) is not { Char: { } c } p || IsTutorial(c)) return;
            if (ret != 0 || taken is null)
            {
                SendByte(p, Msg.CS_GUILDCABINETTAKEOUT_ACK, (byte)ret);
                SendToWorld(Msg.MW_GUILDCABINETLIST_ACK, p);
                return;
            }
            byte got = taken.Count;
            bool placed = false;
            if (c.FindInven(invId) is { } dest)
            {
                if (dest.ContainsSlot(slot) && dest.FindItem(slot) is null)
                {
                    taken.ItemSlot = slot;
                    dest.Items.Add(taken);
                    SendCS_ADDITEM_ACK(p, invId, taken);
                    placed = true;
                }
                else if (CanPush(c, new[] { taken })) { PushTItem(p, new[] { taken }); placed = true; }
            }
            if (!placed)
            {
                SendByte(p, Msg.CS_GUILDCABINETTAKEOUT_ACK, c.FindInven(invId) is null ? GuildCabinetFail : GuildCabinetInvenFull);
                SendToWorld(Msg.MW_GUILDCABINETLIST_ACK, p);
                if (_gameDb is { } rdb) { var back = BuildItemSave(0, taken); _ = EnqueueDbWrite(() => rdb.GuildItemRollbackAsync(guild, cabinetSlot, back)); }
                return;
            }
            SendByte(p, Msg.CS_GUILDCABINETTAKEOUT_ACK, GuildCabinetSuccess);
            SendToWorld(Msg.MW_GUILDCABINETTAKEOUT_ACK, p, w => { w.WriteUInt32(cabinetSlot); w.WriteByte(got); });
        }
        if (_gameDb is not { } db) { Done(GuildCabinetFail, null); return; }
        _ = EnqueueDbWrite(async () =>
        {
            (int ret, ItemSaveData item) res;
            try { res = await db.GuildItemTakeOutAsync(guild, cabinetSlot, count); }
            catch (Exception ex) { _log.LogWarning(ex, "TGuildItemTakeOut failed."); res = (GuildNotFound, default); }
            _dbResults.Enqueue(() => Done(res.ret, res.ret == 0 ? ItemFromSave(res.item) : null));
        });
    }

    /// <summary>An item out of the cabinet: a fresh row id (fixing the C++ shared id of a split stack).</summary>
    private Item ItemFromSave(in ItemSaveData d)
    {
        var it = new Item { DlId = _itemIdReady ? GenItemId() : d.DlId, TemplateId = d.TemplateId, Level = d.Level, Count = d.Count, GLevel = d.GLevel,
            DuraMax = d.DuraMax, DuraCur = d.DuraCur, RefineCur = d.RefineCur, EndTime = d.EndTime, GradeEffect = d.GradeEffect, Gem = d.Gem,
            MoggItemId = d.MoggItemId, Template = _templates.Item(d.TemplateId) };
        for (int i = 0; i < 6; i++) it.Ext[i] = d.Ext[i];
        Item.AddPersistedMagic(it, d.Magic, d.Value, _templates);
        LinkItemAttr(it);
        return it;
    }

    // ================================ contribution ================================

    /// <summary>C++ <c>OnCS_GUILDCONTRIBUTION_REQ</c> (CSHandler.cpp:4280): checked here, taken when the world says yes.</summary>
    private void OnCS_GUILDCONTRIBUTION_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is not { GuildId: not 0, TacticsId: 0 } ch || IsTutorial(ch)) return;
        uint exp = r.ReadUInt32(), gold = r.ReadUInt32(), silver = r.ReadUInt32(), cooper = r.ReadUInt32();
        uint pv = r.Remaining >= 4 ? r.ReadUInt32() : 0;
        long money = CalcMoney(gold, silver, cooper);
        if (exp == 0 && money == 0 && pv == 0) return;
        if ((exp != 0 && (ch.Level < GuildEstablishLevel || !CanUseExp(ch, exp))) || (money != 0 && !ch.UseMoney(money, commit: false))
            || (pv != 0 && ch.PvpUseablePoint < pv))
        {
            SendByte(s, Msg.CS_GUILDCONTRIBUTION_ACK, GuildContributionNotEnough);
            return;
        }
        SendToWorld(Msg.MW_GUILDCONTRIBUTION_ACK, s, w => { w.WriteUInt32(exp); w.WriteUInt32(gold); w.WriteUInt32(silver); w.WriteUInt32(cooper); w.WriteUInt32(pv); });
    }

    /// <summary>C++ <c>OnMW_GUILDCONTRIBUTION_REQ</c> (SSHandler.cpp:8927): what was given is taken.</summary>
    private void OnMW_GUILDCONTRIBUTION_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte result = r.ReadByte();
        uint exp = r.ReadUInt32(), gold = r.ReadUInt32(), silver = r.ReadUInt32(), cooper = r.ReadUInt32(), pv = r.ReadUInt32();
        if (FindPlayer(charId, key) is not { Char: { } ch } s || IsTutorial(ch)) return;
        if (result == 0)
        {
            if (exp != 0 && CanUseExp(ch, exp)) { ch.Exp -= exp; SendCS_EXP_ACK(s, ch); }
            long money = CalcMoney(gold, silver, cooper);
            if (money != 0 && ch.UseMoney(money, commit: true)) SendCS_MONEY_ACK(s, ch);
            if (pv != 0) UsePvPoint(s, ch, pv, PvpeGuild, PvpUseable);
        }
        SendByte(s, Msg.CS_GUILDCONTRIBUTION_ACK, result);
    }

    /// <summary>C++ <c>CTPlayer::UseExp</c> (TPlayer.cpp:1407): only exp above the current level's floor can be given — the level is
    /// never lost.</summary>
    private bool CanUseExp(Character ch, uint exp)
        => exp != 0 && _templates.LevelExpOf(ch.Level - 1) is { } floor && ch.Exp > floor && exp < ch.Exp - floor;

    // ================================ the board, fame, recruiting, records ================================

    private bool InGuild(ClientSession s, out Character ch)
    {
        ch = s.Char!;
        return s.IsMain && s.Char is { GuildId: not 0 } c && !IsTutorial(c);
    }

    private void OnCS_GUILDARTICLELIST_REQ(ClientSession s, PacketReader r)
    {
        if (s.IsMain && s.Char is { GuildId: not 0, TacticsId: 0 }) SendToWorld(Msg.MW_GUILDARTICLELIST_ACK, s);
    }

    private void OnCS_GUILDARTICLEADD_REQ(ClientSession s, PacketReader r)
    {
        if (!InGuild(s, out var ch)) return;
        if (!CheckGuildDuty(ch, GuildDutyViceChief)) { SendByte(s, Msg.CS_GUILDARTICLEADD_ACK, GuildNoDuty); return; }
        string title = r.ReadString(), text = r.ReadString();
        if (title.Length == 0) return;
        SendToWorld(Msg.MW_GUILDARTICLEADD_ACK, s, w => { w.WriteString(title); w.WriteString(text); });
    }

    private void OnCS_GUILDARTICLEDEL_REQ(ClientSession s, PacketReader r)
    {
        if (!InGuild(s, out var ch)) return;
        if (!CheckGuildDuty(ch, GuildDutyViceChief)) { SendByte(s, Msg.CS_GUILDARTICLEDEL_ACK, GuildNoDuty); return; }
        uint id = r.ReadUInt32();
        SendToWorld(Msg.MW_GUILDARTICLEDEL_ACK, s, w => w.WriteUInt32(id));
    }

    private void OnCS_GUILDARTICLEUPDATE_REQ(ClientSession s, PacketReader r)
    {
        if (!InGuild(s, out var ch)) return;
        if (!CheckGuildDuty(ch, GuildDutyViceChief)) { SendByte(s, Msg.CS_GUILDARTICLEUPDATE_ACK, GuildNoDuty); return; }
        uint id = r.ReadUInt32();
        string title = r.ReadString(), text = r.ReadString();
        if (title.Length == 0) return;
        SendToWorld(Msg.MW_GUILDARTICLEUPDATE_ACK, s, w => { w.WriteUInt32(id); w.WriteString(title); w.WriteString(text); });
    }

    /// <summary>C++ <c>OnCS_GUILDFAME_REQ</c> (CSHandler.cpp:4577): the chief sets the guild's fame mark.</summary>
    private void OnCS_GUILDFAME_REQ(ClientSession s, PacketReader r)
    {
        if (!InGuild(s, out var ch)) return;
        if (!CheckGuildDuty(ch, GuildDutyChief))
        {
            var no = new PacketWriter(Msg.CS_GUILDFAME_ACK, capacity: 9);
            no.WriteByte(GuildNoDuty); no.WriteUInt32(0); no.WriteUInt32(0);
            s.Send(no);
            return;
        }
        uint fame = r.ReadUInt32(), color = r.ReadUInt32();
        SendToWorld(Msg.MW_GUILDFAME_ACK, s, w => { w.WriteUInt32(fame); w.WriteUInt32(color); });
    }

    /// <summary>C++ <c>OnMW_GUILDFAME_REQ</c> (SSHandler.cpp:9049): the chief learns the result; every member takes the new mark
    /// and shows it around.</summary>
    private void OnMW_GUILDFAME_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte result = r.ReadByte();
        uint by = r.ReadUInt32(), fame = r.ReadUInt32(), color = r.ReadUInt32();
        if (FindPlayer(charId, key) is not { Char: { } ch } s) return;
        if (by == charId)
        {
            var w = new PacketWriter(Msg.CS_GUILDFAME_ACK, capacity: 9);
            w.WriteByte(result); w.WriteUInt32(fame); w.WriteUInt32(color);
            s.Send(w);
        }
        if (result != GuildSuccess) return;
        ch.Fame = fame; ch.FameColor = color;
        ShowGuildAttr(s, ch, ch.GuildPeer);
    }

    private void OnCS_GUILDWANTEDADD_REQ(ClientSession s, PacketReader r)
    {
        if (!InGuild(s, out var ch)) return;
        if (!CheckGuildDuty(ch, GuildDutyViceChief)) { SendByte(s, Msg.CS_GUILDWANTEDADD_ACK, GuildNoDuty); return; }
        uint id = r.ReadUInt32();
        string title = r.ReadString(), text = r.ReadString();
        byte min = r.ReadByte(), max = r.ReadByte();
        if (title.Length == 0) return;
        SendToWorld(Msg.MW_GUILDWANTEDADD_ACK, s, w => { w.WriteUInt32(id); w.WriteString(title); w.WriteString(text); w.WriteByte(min); w.WriteByte(max); });
    }

    private void OnCS_GUILDWANTEDDEL_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is not { } ch || IsTutorial(ch) || !CheckGuildDuty(ch, GuildDutyViceChief)) return;
        uint id = r.ReadUInt32();
        SendToWorld(Msg.MW_GUILDWANTEDDEL_ACK, s, w => w.WriteUInt32(id));
    }

    private void OnCS_GUILDWANTEDLIST_REQ(ClientSession s, PacketReader r)
    {
        if (s.IsMain && s.Char is not null) SendToWorld(Msg.MW_GUILDWANTEDLIST_ACK, s);
    }

    /// <summary>C++ <c>OnCS_GUILDVOLUNTEERING_REQ</c> (CSHandler.cpp:4701): one without a guild applies to a wanted post.</summary>
    private void OnCS_GUILDVOLUNTEERING_REQ(ClientSession s, PacketReader r)
    {
        if (!s.IsMain || s.Char is not { } ch || IsTutorial(ch)) return;
        if (ch.GuildId != 0) { SendByte(s, Msg.CS_GUILDVOLUNTEERING_ACK, GuildHaveGuild); return; }
        uint id = r.ReadUInt32();
        SendToWorld(Msg.MW_GUILDVOLUNTEERING_ACK, s, w => w.WriteUInt32(id));
    }

    private void OnCS_GUILDVOLUNTEERINGDEL_REQ(ClientSession s, PacketReader r)
    {
        if (s.IsMain && s.Char is { } ch && !IsTutorial(ch)) SendToWorld(Msg.MW_GUILDVOLUNTEERINGDEL_ACK, s);
    }

    private void OnCS_GUILDVOLUNTEERLIST_REQ(ClientSession s, PacketReader r)
    {
        if (s.IsMain && s.Char is { } ch && CheckGuildDuty(ch, GuildDutyViceChief)) SendToWorld(Msg.MW_GUILDVOLUNTEERLIST_ACK, s);
    }

    /// <summary>C++ <c>OnCS_GUILDVOLUNTEERREPLY_REQ</c> (CSHandler.cpp:4766): a vice-chief or the chief takes (or turns down) an
    /// applicant.</summary>
    private void OnCS_GUILDVOLUNTEERREPLY_REQ(ClientSession s, PacketReader r)
    {
        if (!InGuild(s, out var ch)) return;
        if (!CheckGuildDuty(ch, GuildDutyViceChief)) { SendByte(s, Msg.CS_GUILDVOLUNTEERREPLY_ACK, GuildNoDuty); return; }
        uint target = r.ReadUInt32();
        byte reply = r.ReadByte();
        SendToWorld(Msg.MW_GUILDVOLUNTEERREPLY_ACK, s, w => { w.WriteUInt32(target); w.WriteByte(reply); });
    }

    private void OnCS_GUILDPOINTLOG_REQ(ClientSession s, PacketReader r)
    {
        if (s.IsMain && s.Char is { GuildId: not 0, TacticsId: 0 }) SendToWorld(Msg.MW_GUILDPOINTLOG_ACK, s);
    }

    private void OnCS_GUILDPVPRECORD_REQ(ClientSession s, PacketReader r)
    {
        byte type = r.ReadByte();
        if (s.IsMain && s.Char is { } ch && GuildOf(ch) != 0) SendToWorld(Msg.MW_GUILDPVPRECORD_ACK, s, w => w.WriteByte(type));
    }

    /// <summary>C++ <c>OnCS_GUILDLOCALRETURN_REQ</c>: nothing (the C++ checks the guild and returns).</summary>
    private static void OnCS_GUILDLOCALRETURN_REQ(ClientSession s, PacketReader r) { }
}
