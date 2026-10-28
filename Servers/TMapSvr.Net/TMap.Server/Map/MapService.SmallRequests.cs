using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The small client and world requests that were left over (C++ CSHandler.cpp / SSHandler.cpp):
/// <list type="bullet">
/// <item><b>Combat:</b> the battle/peace mode switch (<c>CS_CHGMODE_REQ</c>), a skill's cooldown reset (<c>CS_CANCELSKILL_REQ</c>,
/// C++ <c>CTObjBase::CancelSkill</c>), and a cancelled action shown around (<c>CS_CANCELACTION_REQ</c>).</item>
/// <item><b>"Who is your target?":</b> one player asks another (<c>CS_GETTARGET_REQ</c>), who answers (<c>CS_GETTARGETANS_REQ</c>).</item>
/// <item><b>Loot:</b> take the money and every item of a corpse at once (<c>CS_MONITEMTAKEALL_REQ</c>).</item>
/// <item><b>Quests:</b> a timed quest whose time ran out on the client fails (<c>CS_QUESTENDTIMER_REQ</c>), and a hunt term
/// reached by position is done (<c>CS_QUESTPOSEXEC_REQ</c>).</item>
/// <item><b>Character:</b> the helmet shown or hidden, through the world (<c>CS_HELMETHIDE_REQ</c> / <c>MW_HELMETHIDE_REQ</c>), the
/// profile comment shown to the players around of one's side (<c>CS_COMMENT_REQ</c>), a level set by the world
/// (<c>MW_LEVELUP_REQ</c>).</item>
/// <item><b>Inspecting a player on another map server</b> (<c>MW_CHARSTATINFO_ACK</c> → <c>MW_CHARSTATINFOANS_REQ</c> →
/// <c>MW_CHARSTATINFO_REQ</c>).</item>
/// </list>
/// <item><b>An event prize by mail</b> (<c>MW_WORLDPOSTSEND_REQ</c> <c>WPT_LOTITEM</c>): the world's lottery winner gets the item
/// in a package from the operator.</item>
/// <para><c>CS_ACTEND_REQ</c> is a no-op (its C++ body is commented out). <b>Not ported:</b> the chat-ban check on the comment
/// (chat bans are not ported), the chief/order party loot modes,
/// <c>CS_STOPTHECLOCK_REQ</c> (a cash-shop item), and what only happens across several map servers: <c>MW_ADDITEM_REQ</c>
/// (loot), <c>MW_MONSTERDIE_REQ</c> (party exp) and <c>MW_MAGICMIRROR_REQ</c> (a reflected hit).</para>
/// </summary>
public sealed partial class MapService
{
    // ================================ combat ================================

    /// <summary>C++ <c>OnCS_CHGMODE_REQ</c> → <c>CTObjBase::ChgMode</c> (TObjBase.cpp:295): the new mode, the last attack tick
    /// now, the regeneration held back when going into battle, and the players around told.</summary>
    private void OnCS_CHGMODE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte mode = r.ReadByte();
        ch.Mode = mode;
        ch.LastAtkTick = NowMs;
        if (mode == MtBattle) { ch.RecoverHpTick = NowMs + RecoverInit; ch.RecoverMpTick = NowMs + RecoverInit; }
        var w = new PacketWriter(Msg.CS_CHGMODE_ACK, capacity: 8);
        w.WriteUInt32(ch.CharId); w.WriteByte(OtPc); w.WriteByte(mode);
        var ack = w.ToArray();
        foreach (var p in _state.InView(s)) p.Send(ack);
    }

    /// <summary>C++ <c>OnCS_CANCELSKILL_REQ</c> → <c>CTObjBase::CancelSkill</c> (TObjBase.cpp:4588): the skill's cooldown is reset,
    /// and those of its kind that have no more than its kind delay left too. Only one's own skills (the target is oneself in
    /// practice; the port keeps no other player's cooldown to reset through it).</summary>
    private void OnCS_CANCELSKILL_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte type = r.ReadByte();
        uint id = r.ReadUInt32();
        ushort skillId = r.ReadUInt16();
        if (type != OtPc || id != ch.CharId) return;
        if (ch.Skills.FirstOrDefault(k => k.SkillId == skillId) is not { Template: { } t } skill) return;
        foreach (var k in ch.Skills)
            if (k.Template?.Kind == t.Kind && k.GetReuseRemainTick(NowMs) <= t.KindDelay) k.ResetCooldown();
        skill.ResetCooldown();
    }

    /// <summary>C++ <c>OnCS_CANCELACTION_REQ</c>: the cancelled action (object id and type) is shown around.</summary>
    private void OnCS_CANCELACTION_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain) return;
        uint id = r.ReadUInt32();
        byte type = r.ReadByte();
        var w = new PacketWriter(Msg.CS_CANCELACTION_ACK, capacity: 8);
        w.WriteUInt32(id); w.WriteByte(type);
        var ack = w.ToArray();
        foreach (var p in _state.InView(s)) p.Send(ack);
    }

    // ================================ target ================================

    /// <summary>C++ <c>OnCS_GETTARGET_REQ</c>: the asked player is told who asks (<c>CS_GETTARGETANS_ACK</c>).</summary>
    private void OnCS_GETTARGET_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain) return;
        uint charId = r.ReadUInt32();
        if (_state.FindByChar(charId) is not { State: EnterState.InGame } t) return;
        var w = new PacketWriter(Msg.CS_GETTARGETANS_ACK, capacity: 4);
        w.WriteUInt32(s.CharId);
        t.Send(w);
    }

    /// <summary>C++ <c>OnCS_GETTARGETANS_REQ</c>: the answer (its target id and type) goes to the one who asked.</summary>
    private void OnCS_GETTARGETANS_REQ(ClientSession s, PacketReader r)
    {
        uint askerId = r.ReadUInt32(), targetId = r.ReadUInt32();
        byte targetType = r.ReadByte();
        if (_state.FindByChar(askerId) is not { State: EnterState.InGame } asker) return;
        var w = new PacketWriter(Msg.CS_GETTARGET_ACK, capacity: 8);
        w.WriteUInt32(targetId); w.WriteByte(targetType);
        asker.Send(w);
    }

    // ================================ loot ================================

    /// <summary>C++ <c>OnCS_MONITEMTAKEALL_REQ</c>: the corpse's money (<c>MonMoneyTake</c>), then, when one may loot it, every item
    /// that fits (<c>MonItemTake</c> with no slot).</summary>
    private void OnCS_MONITEMTAKEALL_REQ(ClientSession s, PacketReader r)
    {
        uint monId = r.ReadUInt32();
        if (s.Char is not { } ch || !s.IsMain || _state.FindMonster(monId) is not { } mon
            || mon.MapId != ch.MapId || mon.Channel != s.Channel) return;
        if (s.Deal.InProgress) { SendCS_MONITEMTAKE_ACK(s, MonItemTakeResult.Dealing); return; }
        if (!CanLootKeeper(mon, ch) || !CanTakePartyLoot(mon, ch))
        { SendCS_MONITEMLIST_CANTACCESS(s, monId); return; }
        TakeCorpseMoney(s, ch, mon);
        var result = MonItemTakeResult.Success;
        foreach (var item in mon.CorpseInven.Items.OrderBy(i => i.ItemSlot).ToList())
        {
            if (item.OwnerId != 0 && item.OwnerId != ch.CharId) continue;
            var one = new[] { item };
            if (!CanPush(ch, one)) { result = MonItemTakeResult.FullInven; break; }
            mon.CorpseInven.Items.Remove(item);
            PushTItem(s, one);
            SendCS_GETITEM_ACK(s, item, ch.CharId);
        }
        SendCS_MONITEMLIST_ACK(s, mon, update: 1);
        SendCS_MONITEMTAKE_ACK(s, result);
    }

    // ================================ quests ================================

    /// <summary>C++ <c>OnCS_QUESTENDTIMER_REQ</c>: a running quest's timer terms fail (and its timer stops).</summary>
    private void OnCS_QUESTENDTIMER_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        uint questId = r.ReadUInt32();
        if (ch.FindQuest(questId) is not { IsRunning: true } qp) return;
        foreach (var t in qp.Template.Terms.Where(t => t.TermType == QttTimer))
        {
            qp.BeginTick = 0;
            qp.Save = true;
            SendCS_QUESTUPDATE_ACK(s, questId, t.TermId, t.TermType, 0, (byte)QuestTermStatus.Failed);
        }
    }

    /// <summary>C++ <c>OnCS_QUESTPOSEXEC_REQ</c>: a hunt term the client reached by position is done — its running count becomes 1.</summary>
    private void OnCS_QUESTPOSEXEC_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        uint questId = r.ReadUInt32(), termId = r.ReadUInt32();
        if (ch.FindQuest(questId) is not { } qp || FindTemplateTerm(qp, termId, QttHunt) is null) return;
        if (qp.RunningTerms.FirstOrDefault(t => t.TermId == termId) is { } rt) rt.Count = 1;   // CQuest::FinishTerm
        qp.Save = true;
        SendCS_QUESTUPDATE_ACK(s, questId, termId, QttHunt, 1, (byte)QuestTermStatus.Success);
    }

    // ================================ character ================================

    /// <summary>C++ <c>OnCS_HELMETHIDE_REQ</c>: a change goes to the world (<c>MW_HELMETHIDE_ACK</c>).</summary>
    private void OnCS_HELMETHIDE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte hide = r.ReadByte();
        if (ch.HelmetHide == hide) return;
        var w = new PacketWriter(Msg.MW_HELMETHIDE_ACK);
        w.WriteUInt32(ch.CharId); w.WriteUInt32(s.Key); w.WriteByte(hide);
        _world.Send(w);
    }

    /// <summary>C++ <c>OnMW_HELMETHIDE_REQ</c>: the world's value is the player's, shown around (<c>CS_HELMETHIDE_ACK</c>).</summary>
    private void OnMW_HELMETHIDE_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte hide = r.ReadByte();
        if (_state.FindByChar(charId) is not { Char: { } ch } s || s.Key != key || ch.HelmetHide == hide) return;
        ch.HelmetHide = hide;
        var w = new PacketWriter(Msg.CS_HELMETHIDE_ACK, capacity: 8);
        w.WriteUInt32(charId); w.WriteByte(hide);
        var ack = w.ToArray();
        foreach (var p in _state.InView(s)) p.Send(ack);
    }

    /// <summary>C++ <c>OnCS_COMMENT_REQ</c>: the comment is kept and shown to the players around who are not one's enemies
    /// (C++ <c>!CanFight</c>).</summary>
    private void OnCS_COMMENT_REQ(ClientSession s, PacketReader r)
    {
        if (s.Char is not { } ch) return;
        if (ChatBanned(s, ch)) return;                                                          // C++ CSHandler.cpp:14940 (MapService.Gm.cs)
        ch.Comment = r.ReadString();
        if (!s.IsMain) return;
        var w = new PacketWriter(Msg.CS_COMMENT_ACK, capacity: 64);
        w.WriteUInt32(ch.CharId); w.WriteString(ch.Comment);
        var ack = w.ToArray();
        foreach (var p in _state.InView(s))
            if (p.Char is { } other && !CanFight(other, ch)) p.Send(ack);
    }

    /// <summary>C++ <c>CTObjBase::CanFight</c> between two players (TObjBase.cpp:4972): not of the same side, and neither of the
    /// peace country.</summary>
    private static bool CanFight(Character me, Character other)
        => GetAttackCountry(other.Country, other.AidCountry) != WarCountryOf(me)
            && me.Country != TcontryPeace && other.Country != TcontryPeace;

    /// <summary>C++ <c>OnMW_LEVELUP_REQ</c>: the world sets the player's level.</summary>
    private void OnMW_LEVELUP_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32();
        byte level = r.ReadByte();
        if (_state.FindByChar(charId) is { Char: { } ch } s && s.Key == key) ch.Level = level;
    }

    // ================================ mail from the world ================================

    private const byte WptLotItem = 4;   // WORLDPOST_TYPE (CTProtocol.h:43)

    /// <summary>C++ <c>OnMW_WORLDPOSTSEND_REQ</c> (SSHandler.cpp:17091), <c>WPT_LOTITEM</c>: a new item (its count, lasting its
    /// hours) is mailed to the winner in an operator package, and the winner told if online.</summary>
    private async Task OnMW_WORLDPOSTSEND_REQ(PacketReader r)
    {
        byte type = r.ReadByte();
        if (type is WptTacticsKick or WptTacticsEnd) { await MailTacticsEnd(type, r); return; }   // MapService.GuildTactics.cs
        if (type != WptLotItem || PostStore is not { } db) return;
        uint recvId = r.ReadUInt32();
        string recver = r.ReadString(), title = r.ReadString(), message = r.ReadString();
        ushort itemId = r.ReadUInt16();
        byte count = r.ReadByte();
        ushort useTime = r.ReadUInt16();
        if (_templates.Item(itemId) is not { } tpl) return;

        var item = new Item { TemplateId = itemId, Count = count, Template = tpl, DuraMax = tpl.DuraMax, DuraCur = tpl.DuraMax };
        if (useTime != 0) item.EndTime = UnixNow() + useTime * 3600L;
        LinkItemAttr(item);
        long now = UnixNow();
        (title, message) = (NetString(title), NetString(message));        // C++ BuildNetString(NAME_NULL, …)
        (int saved, uint postId, uint recv) = await Safe(() => db.SavePostAsync(0, recvId, recver, "Operator", title, message,
            0, PostPackage, 0, 0, 0, now), (PostInternal, 0u, 0u));
        if (saved != 0 || postId == 0) return;
        var row = BuildItemSave(0, item) with { DlId = _itemIdReady ? GenItemId() : 0, StorageType = StoragePost, StorageId = postId };
        await Safe(async () => { await db.SavePostItemAsync(recv, row); return 0; }, 0);
        NotifyPostRecv(postId, "Operator", recver, title, PostPackage, now);
    }

    // ================================ inspecting across map servers ================================

    /// <summary>C++ <c>OnMW_CHARSTATINFOANS_REQ</c>: someone on another map server inspects this player — its stat sheet goes back
    /// through the world (<c>MW_CHARSTATINFOANS_ACK</c>: the asker's id, then the sheet).</summary>
    private void OnMW_CHARSTATINFOANS_REQ(PacketReader r)
    {
        uint askerId = r.ReadUInt32(), charId = r.ReadUInt32();
        if (_state.FindByChar(charId) is not { State: EnterState.InGame, IsMain: true, Char: { } ch }) return;
        var w = new PacketWriter(Msg.MW_CHARSTATINFOANS_ACK, capacity: 100);
        w.WriteUInt32(askerId);
        WriteStatSheet(w, ch);
        _world.Send(w);
    }

    /// <summary>C++ <c>OnMW_CHARSTATINFO_REQ</c>: the inspected player's sheet arrives — what follows the asker's id is the
    /// <c>CS_CHARSTATINFO_ACK</c> body.</summary>
    private void OnMW_CHARSTATINFO_REQ(PacketReader r)
    {
        uint askerId = r.ReadUInt32();
        if (_state.FindByChar(askerId) is not { State: EnterState.InGame } s) return;
        var w = new PacketWriter(Msg.CS_CHARSTATINFO_ACK, capacity: 100);
        while (r.Remaining > 0) w.WriteByte(r.ReadByte());
        s.Send(w);
    }
}
