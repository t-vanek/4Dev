using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// What an NPC offers — C++ <c>OnCS_NPCITEMLIST_REQ</c> (CSHandler.cpp:6194) and <c>SendCS_NPCITEMLIST_ACK(CTNpc*)</c>
/// (CSSender.cpp:2756, the same in the old sources). The client opens the NPC's window only on this answer:
/// <c>wNpcID · bType · bDiscountRate · bCount</c> then, per entry, <c>wID · dwPrice</c>:
/// <list type="bullet">
/// <item>a skill trainer — what the character can learn (MapService.SkillBuy.cs);</item>
/// <item>a merchant (<c>TNPC_ITEM</c>) or a PvP shop (<c>TNPC_PVPOINT</c>) — the stock for the character's class and
/// country, at the gold price (<c>GetItemPrice</c>) or the PvP-point price (<c>GetItemPvPrice</c>);</item>
/// <item>a teleporter (<c>TNPC_PORTAL</c>) — the destinations whose conditions the character meets, with their price;</item>
/// <item>a castle guards' shop (<c>TNPC_MONSTER</c>) — its guard posts and their price (MapService.GuildTactics.cs).</item>
/// </list>
/// Prices are before discount: the client applies <c>bDiscountRate</c> (<c>GetDiscountRate</c>, MapService.Fort.cs). The magic
/// shop (<c>TNPC_MAGICITEM</c>) is in MapService.Gamble.cs. Portal conditions tied to tournaments (unported) never pass.
/// </summary>
public sealed partial class MapService
{
    private const byte DccAllCountry = 4;     // DISCOUNT_CONDITION DCC_ALLCOUNTRY — the shop sells every country's stock
    private const byte PctNone = 0, PctCountry = 1, PctDownLevel = 5, PctUpLevel = 6, PctUpDownLevel = 7, PctGuild = 8;   // PORTALCONDITION_TYPE

    private void OnCS_NPCITEMLIST_REQ(ClientSession s, PacketReader r)
    {
        ushort npcId = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        if (_state.FindNpc(npcId) is not { } npc || !CanTalk(npc, ch)) return;
        switch (npc.Type)
        {
            case TnpcSkillMaster or TnpcSkillRent: SendCS_NPCSKILLLIST_ACK(s, ch, npc); break;
            case TnpcItem or TnpcPvPoint: SendCS_NPCITEMLIST_ACK(s, ch, npc); break;
            case TnpcPortal: SendCS_NPCPORTALLIST_ACK(s, ch, npc); break;
            case TnpcMonster: SendCS_NPCMONSTERLIST_ACK(s, ch, npc); break;   // MapService.GuildTactics.cs
            case TnpcMagicItem: SendCS_NPCMAGICLIST_ACK(s, ch, npc); break;   // MapService.Gamble.cs
        }
    }

    /// <summary>The <c>TNPC_ITEM</c>/<c>TNPC_PVPOINT</c> branch: stock whose class mask has the character's class and that
    /// is sold to its country (or to everyone, or the shop sells every country's), by item id.</summary>
    private void SendCS_NPCITEMLIST_ACK(ClientSession s, Character ch, Npc npc)
    {
        var list = new SortedDictionary<ushort, uint>();
        foreach (var (id, t) in npc.Items)
            if ((t.ClassId & (1u << ch.Class)) != 0
                && (t.ItemCountry == TcontryN || npc.DiscountCondition == DccAllCountry || t.ItemCountry == ch.Country))
                list[id] = npc.Type == TnpcPvPoint ? GetItemPvPrice(t) : GetItemPrice(t);
        SendNpcList(s, ch, npc, list);
    }

    /// <summary>The <c>TNPC_PORTAL</c> branch: when the NPC's portal lets the character in, every destination whose three
    /// conditions it meets (each checked against the destination's own portal), by destination id.</summary>
    private void SendCS_NPCPORTALLIST_ACK(ClientSession s, Character ch, Npc npc)
    {
        var list = new SortedDictionary<ushort, uint>();
        if (npc.PortalId != 0 && _templates.Portals.TryGetValue(npc.PortalId, out var portal)
            && CheckPortalCondition(ch, portal, portal.Condition, 0))
            foreach (var (destId, dest) in portal.Destinations)
                if (_templates.Portals.TryGetValue(destId, out var target)
                    && dest.Conditions.All(c => CheckPortalCondition(ch, target, c.Type, c.Id))
                    && (!dest.Conditions.Any(c => c.Type == PctMeeting)
                        || !MeetingDestinationBusy(s.Channel, portal.PortalId, destId, s.CharId)))
                    list[destId] = dest.Price;
        SendNpcList(s, ch, npc, list);
    }

    private void SendNpcList(ClientSession s, Character ch, Npc npc, SortedDictionary<ushort, uint> list)
    {
        var w = new PacketWriter(Msg.CS_NPCITEMLIST_ACK, capacity: 8 + list.Count * 6);
        w.WriteUInt16(npc.Id); w.WriteByte(npc.Type); w.WriteByte(DiscountRate(ch, npc));
        w.WriteByte((byte)list.Count);
        foreach (var (id, price) in list) { w.WriteUInt16(id); w.WriteUInt32(price); }
        s.Send(w);
    }

    /// <summary>C++ <c>CTMapSvrModule::GetItemPvPrice</c> (TMapSvr.cpp:9624) — <c>DWORD(m_dwPvPMoney[grade]·m_fPvPrice + 0.99)</c>,
    /// grade as in <see cref="GetItemPrice"/>.</summary>
    private uint GetItemPvPrice(ItemTemplate t)
    {
        byte grade = t.AttrId != 0 ? (_templates.Attr(t.AttrId)?.Grade ?? 0) : t.DefaultLevel;
        return _templates.LevelPvPMoneyOf(grade) is { } money ? (uint)(money * t.PvPrice + 0.99) : 0u;
    }

    /// <summary>C++ <c>CTPlayer::CheckPortalCondition</c> (TPlayer.cpp:3220), the tournament's included. The castles' are in MapService.Castle.cs, the sky garden's in MapService.SkyGarden.cs.</summary>
    private bool CheckPortalCondition(Character ch, PortalRow portal, byte condition, uint conditionId)
    {
        switch (condition)
        {
            case PctNone: return true;
            case PctCountry:
                // The portal's own country, or its territory's for a neutral one (C++ m_pLocal: any territory); disguise unported ⇒ 0.
                if (portal.Country != TcontryN) return ch.Country == portal.Country || ch.AidCountry == portal.Country;
                byte pc = _territories.TryGetValue(portal.LocalId, out var pt) ? pt.Country : portal.Country;
                return pc == TcontryN || ch.Country == pc || ch.AidCountry == pc;
            case PctGuild:                                                       // the portal's territory's guild
                return _territories.TryGetValue(portal.LocalId, out var f) && f.Guild != 0 && ch.GuildId == f.Guild;
            case PctHaveItem: return ch.Invens.Any(i => i.Items.Any(it => it.TemplateId == (ushort)conditionId));
            case PctDownLevel: return ch.Level >= conditionId;
            case PctUpLevel: return ch.Level <= conditionId;
            case PctUpDownLevel: return ch.Level >= (conditionId & 0xFFFF) && ch.Level <= (conditionId >> 16);
            case PctMeeting: return conditionId == 0;
            case PctTournamentPlayer: return conditionId == TournamentMapOf(ch.CharId, _tnmtGroup);    // MapService.Tournament.cs
            case PctTournamentLounge:
                return CanDoTournament(TnmtEnter) && (conditionId == 0
                    || (conditionId == 1 && FindTournamentPlayer(ch.CharId, active: true) is not null)
                    || (conditionId == 2 && FindTournamentPlayer(ch.CharId, active: true) is null));
            case PctTournamentGuest: return IsActiveTournamentMap((ushort)conditionId);
            case PctAttackPos or PctDefendPos: return CheckCastlePortal(ch, portal, condition);   // MapService.Castle.cs
            case >= PctSkyAttackPos and <= PctSkyAttackPos + 10: return CheckSkyGardenPortal(ch, portal, condition);   // MapService.SkyGarden.cs
            case > PctMeeting + 11: return true;                                 // past the enum: C++ default
            default: return false;
        }
    }
}
