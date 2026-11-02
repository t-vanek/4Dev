using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Mini-games, batch S5c — the party loot lottery (<c>PT_LOTTERY</c>; C++ TMapSvr.cpp:9163–9191, CSHandler.cpp:6912,
/// TMonster.cpp:1352, TAICmdLottery.cpp, CSSender.cpp:3036).
/// <list type="bullet">
/// <item>In a lottery party, taking a party corpse's item asks every member near the taker (<c>CS_MONITEMLOTTERY_ACK</c>); the
/// item leaves the loot list while it is drawn for, and the corpse's AI gets <c>AT_LOTTERY</c> (its script's timeout).</item>
/// <item>Each member answers yes or no (<c>CS_MONITEMLOTTERY_REQ</c>); once everyone has answered, or the AI's
/// <c>AC_LOTTERY</c> fires, one of those who said yes gets it at random.</item>
/// </list>
/// <para>Fixed C++ bugs: only the members asked may answer, and once each (the C++ took a "no" from anyone, any number of times,
/// and could close the draw early); and the answer goes to the item being drawn for, not the corpse's first item of that kind.
/// A winner on another map server (the C++ <c>MW_ADDITEM_ACK</c>) is not ported: the item stays on the corpse.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte PtLottery = 3;                                   // PARTY_TYPE PT_LOTTERY

    /// <summary>The <c>PT_LOTTERY</c> branch of C++ <c>MonItemTake</c>: false when the take is not a lottery.</summary>
    private bool StartLootLottery(ClientSession s, Character ch, Monster mon, Item item)
    {
        if (mon.KeeperType != OwnerParty || item.OwnerId != 0 || ch.PartyType != PtLottery) return false;
        item.MaxRouting = 1;
        item.RoutingJoiner = new() { [ch.CharId] = s.Key };
        item.RoutingAsked = new();
        foreach (var p in _state.InView(s))
            if (p.Char is { } c && c.CharId != ch.CharId && c.GetPartyId() == mon.KeeperId)
            {
                item.MaxRouting++;
                item.RoutingAsked.Add(c.CharId);
                var w = new PacketWriter(Msg.CS_MONITEMLOTTERY_ACK, capacity: 48);
                w.WriteUInt32(mon.Id);
                item.WrapPacketClient(w, c.CharId, addItemId: false);
                p.Send(w);
            }
        if (item.MaxRouting == 1) Lottery(mon, item);
        else OnAiEvent(mon, AiTrigger.Lottery, 0, 0, item.ItemSlot, 0);
        return true;
    }

    /// <summary>C++ <c>OnCS_MONITEMLOTTERY_REQ</c> (CSHandler.cpp:6912): a member's yes or no.</summary>
    private void OnCS_MONITEMLOTTERY_REQ(ClientSession s, PacketReader r)
    {
        bool yes = r.ReadByte() != 0;
        uint monId = r.ReadUInt32();
        ushort itemId = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || _state.FindMonster(monId) is not { } mon) return;
        var item = mon.CorpseInven.Items.FirstOrDefault(i => i.TemplateId == itemId && i.RoutingAsked?.Contains(ch.CharId) == true);
        if (item?.RoutingAsked is null || item.RoutingJoiner is null) return;
        item.RoutingAsked.Remove(ch.CharId);
        if (yes) item.RoutingJoiner[ch.CharId] = s.Key;
        else if (item.MaxRouting != 0) item.MaxRouting--;
        if (item.MaxRouting == item.RoutingJoiner.Count) Lottery(mon, item);
    }

    /// <summary>C++ <c>CTAICmdLottery::ExecAI</c>: the draw's time is up.</summary>
    private bool ExecLottery(AiBinding b, Monster mon, uint eventHost, uint rhId, byte rhType)
    {
        if (mon.CorpseInven.FindItem((byte)rhId) is { MaxRouting: not 0 } item) Lottery(mon, item);
        return AiComplete(b, mon, eventHost, rhId, rhType);
    }

    /// <summary>C++ <c>CTMonster::Lottery</c> (TMonster.cpp:1352): one of those who said yes gets the item.</summary>
    private void Lottery(Monster mon, Item item)
    {
        var joiners = item.RoutingJoiner;
        item.MaxRouting = 0;
        item.RoutingJoiner = null;
        item.RoutingAsked = null;
        if (joiners is not { Count: > 0 }) return;
        var (charId, key) = joiners.ElementAt(Rand() % joiners.Count);
        if (FindPlayer(charId, key) is not { IsMain: true, State: EnterState.InGame, Char: { } ch } s) { ReviewMonItemList(mon); return; }
        var one = new[] { item };
        if (!CanPush(ch, one))
        {
            SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.FullInven);
            ReviewMonItemList(mon);
            return;
        }
        mon.CorpseInven.Items.Remove(item);
        GrantMonsterItem(s, ch, item, mon.Id);
        SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.Success);
        SendCS_GETITEM_ACK(s, item, ch.CharId);
        ReviewMonItemList(mon);
        PartyMonItemTake(s, ch, item);
    }

    /// <summary>C++ <c>ReviewMonItemList</c> (TMapSvr.cpp:8347): the corpse's looter sees the list again.</summary>
    private void ReviewMonItemList(Monster mon)
    {
        if (mon.InvenLock != 0 && _state.FindByChar(mon.InvenLock) is { } p) SendCS_MONITEMLIST_ACK(p, mon, update: 1);
    }
}
