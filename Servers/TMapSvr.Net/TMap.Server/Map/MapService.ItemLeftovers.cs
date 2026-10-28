using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The item and combat leftovers, batch S7a (C++ CSHandler.cpp:6216, 10608, 14735, 15376, 19612, 19875; SSHandler.cpp:15307–15410;
/// CSSender.cpp:2695, 4036, 4989, 5511).
/// <list type="bullet">
/// <item><b>Fall damage</b> (<c>CS_DROPDAMAGE_REQ</c>): the client reports its own fall; the HP goes down for everyone around and
/// may kill. Reporting someone else's is answered with the operator's chat line, as in the C++.</item>
/// <item><b>Act items</b> (<c>CS_ACTITEMSUSE_REQ</c>): an act skill (fireworks and the like) spends its item (<c>wItemID</c> of the
/// skill chart) under the item's delay, and the cast is shown around.</item>
/// <item><b>Medal items</b> (<c>CS_USEMEDALITEM_REQ</c>, a package of kind 118): their medals are added.</item>
/// <item><b>The rewards window</b> (<c>CS_GETREWARDS_REQ</c>): what a special box (for one's class) or a cash gamble group holds.</item>
/// <item><b>Stop the clock</b> (<c>CS_STOPTHECLOCK_REQ</c>): a timed item or bag buys a cash item's days (<c>TStopTheClock</c> pays
/// the cash).</item>
/// <item><b>Waste refine</b> (<c>CS_WASTEREFINE_REQ</c>): a refine stone becomes the item its use value names.</item>
/// </list>
/// <para><b>C++ bugs fixed:</b> the act-item caster was the packet's <c>dwAttackID</c>, any player — whose items were spent; it must
/// be the sender. The item delay was armed before checking the item is there, so a use without the item still cost the delay.
/// The medal handler had no map/main check.</para>
/// <para>Act items also validate attack-blocking effects and weapon/ammunition requirements before consuming
/// the item or arming its delay. <c>CheckPrevAct</c> remains unported.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte IkMedal = 118;                                                           // a medal package (IT_PACKAGE kind)
    private const byte WasteRefineSuccess = 0, WasteRefineNotFound = 1, WasteRefineNoRefine = 2, WasteRefineFullInven = 3;
    private const byte StcSuccess = 0, StcNeedCash = 1, StcFail = 2;                            // STOPTHECLOCK result
    private const byte ItPackage = 15, InvalidSlot = 0xFF;                                    // IT_PACKAGE, INVALID_SLOT

    public ICashStore? CashStore { get; set; }

    // ================================ fall damage ================================

    /// <summary>C++ <c>OnCS_DROPDAMAGE_REQ</c>.</summary>
    private void OnCS_DROPDAMAGE_REQ(ClientSession s, PacketReader r)
    {
        uint targetId = r.ReadUInt32();
        byte targetType = r.ReadByte();
        uint damage = r.ReadUInt32();
        byte channel = r.ReadByte();
        ushort mapId = r.ReadUInt16();
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;
        // A delayed report from before a map/channel change must not hurt the player at the destination.
        if (channel != s.Channel || mapId != ch.MapId) return;
        if (targetType != OtPc && targetType != Monster.OtMon) return;
        if (targetId != ch.CharId)
        {
            // Someone else's fall: refused with the operator's word (only a player or a monster is answered).
            string msg = targetType == OtPc && _state.FindByChar(targetId)?.Char is { } other
                ? $"Trying to hack on our svr {ch.Name}? I'm sure you don't wanna hurt {other.Name} right? :D"
                : targetType == Monster.OtMon ? "Why would you try to hurt poor monster?" : null!;
            if (msg is not null) s.Send(BuildCS_CHAT_ACK(ChatGroup.Operator, 0, SvrMsg(MsgNameOperator), NetString(msg)));
            return;
        }
        if (!s.IsMain || targetType != OtPc || ch.Hp == 0) return;

        uint hpBefore = ch.Hp;
        ch.Hp = ch.Hp > damage ? ch.Hp - damage : 0;
        _log.LogInformation("Fall damage char {Char} on map {Map}: damage {Damage}, HP {Before} -> {After}.", ch.CharId, ch.MapId, damage, hpBefore, ch.Hp);
        ch.EnterBattle(NowMs, RecoverInit);                                                     // m_dwRecoverHP/MPTick = now + RECOVER_INIT
        var viewers = _state.InView(s).ToList();
        var fall = new PacketWriter(Msg.CS_DROPDAMAGE_ACK).WriteUInt32(targetId).WriteByte(targetType).WriteUInt32(damage).ToArray();
        foreach (var v in viewers)
        {
            v.Send(fall);
            SendSelfHpMp(v, ch.CharId, MaxHpFor(ch), ch.Hp, MaxMpFor(ch), ch.Mp);
        }
        if (ch.Hp == 0) PlayerDied(ch, viewers);
    }

    // ================================ act items ================================

    /// <summary>C++ <c>OnCS_ACTITEMSUSE_REQ</c>.</summary>
    private void OnCS_ACTITEMSUSE_REQ(ClientSession s, PacketReader r)
    {
        uint attackId = r.ReadUInt32();
        byte attackType = r.ReadByte();
        r.ReadByte(); r.ReadUInt16();                                                           // bChannel, wMapID
        ushort skillId = r.ReadUInt16();
        byte actionId = r.ReadByte();
        uint actId = r.ReadUInt32(), aniId = r.ReadUInt32();
        float posX = r.ReadFloat(), posY = r.ReadFloat(), posZ = r.ReadFloat();
        uint defendId = r.ReadUInt32();
        byte defendType = r.ReadByte();
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;
        void Fail(SkillUseResult why) => SendSkillUseFail(s, why, attackId, attackType, skillId, actionId, actId, aniId);

        if (!s.IsMain || attackId != ch.CharId || attackType != OtPc) return;
        if (ch.Riding != 0) { Fail(SkillUseResult.ActionLock); return; }
        if (!_templates.Skills.TryGetValue(skillId, out var skill)) { Fail(SkillUseResult.NotFound); return; }
        if (skill.MapId != 0xFFFF && skill.MapId != ch.MapId) { Fail(SkillUseResult.WrongRegion); return; }
        if (ch.Hp == 0 || skill.IsNegative && !BuffLayer.CanAttack(ch.MaintainSkills, player: true))
        { Fail(SkillUseResult.ActionLock); return; }
        if (attackType != Monster.OtMon && skill.IsNegative && CheckPeaceZone(ch)) { Fail(SkillUseResult.PeaceZone); return; }
        if (TournamentSkillBanned(ch, skillId) is { } banned) { Fail(banned); return; }
        if (ch.ArenaId != 0 && skillId == TournamentCantUseSkill) { Fail(SkillUseResult.NotFound); return; }

        if (!HasActiveSkill(ch.MaintainSkills, skill.PrevActiveId)) { Fail(SkillUseResult.NeedPrevAct); return; }
        if (skill.UseItem == 0 || ItemCount(ch, skill.UseItem) < 1) { Fail(SkillUseResult.NeedItem); return; }
        _templates.Items.TryGetValue(skill.UseItem, out var it);
        if (it is not null && ch.ItemCoolTime.TryGetValue(it.DelayGroup, out uint ready) && unchecked((int)(ready - NowMs)) > 0)
        { Fail(SkillUseResult.SpeedyUse); return; }
        if (!TryConsumeSkillItems(s, ch, skill, hitCount: 1)) { Fail(SkillUseResult.UnsuitWeapon); return; }

        if (skill.IsNegative) ch.EnterBattle(NowMs, RecoverInit);
        EraseBuffByAttack(s, ch, skill);
        if (it is not null)
        {
            ch.ItemCoolTime[it.DelayGroup] = NowMs + it.Delay;
            SendCS_ITEMUSE_ACK(s, ItemUseResult.Success, it.DelayGroup, it.Kind, it.Delay);
        }
        AuthorizePlayerCast(s, new Skill { SkillId = skillId, Level = 1, Template = skill },
            new[] { (defendId, defendType) }, requireLearned: false);

        var ack = BuildCS_SKILLUSE_ACK(SkillUseResult.Success, attackId, attackType, skillId, actionId, actId, aniId,
            skillLevel: 1, backSkill: 0, attackLevel: 0, attackerLevel: ch.Level, pysMin: 0, pysMax: 0, mgMin: 0, mgMax: 0,
            transHp: 0, transMp: 0, curseProb: 0, equipSpecial: 0, canSelect: 0,
            ch.Country, ch.AidCountry, cp: 0, ch.PosX, ch.PosY, ch.PosZ, new[] { (defendId, defendType) });
        foreach (var p in _state.NearView(s)) p.Send(ack);
    }

    // ================================ medals ================================

    /// <summary>C++ <c>OnCS_USEMEDALITEM_REQ</c>: a medal package's <c>wUseValue</c> medals, per item.</summary>
    private async Task OnCS_USEMEDALITEM_REQ(ClientSession s, PacketReader r)
    {
        byte invenId = r.ReadByte(), slot = r.ReadByte(), count = r.ReadByte();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        if (ch.FindInven(invenId) is not { } bag || bag.FindItem(slot) is not { Count: > 0 } item
            || item.Template is not { Type: ItPackage, Kind: IkMedal } t || item.Count < count) return;

        if (count == 0 || (ulong)ch.Medals + (uint)t.UseValue * count > int.MaxValue) return;
        uint amount = (uint)t.UseValue * count;
        if (CloakStore is { } db)
        {
            // Persist consumption and credit together; a crash must not leave both the package and its medals.
            FlushItemDirect();
            uint? balance = null;
            await EnqueueDbWrite(async () => balance = await db.RedeemMedalItemAsync(ch.CharId, item.DlId, count, amount));
            if (balance is null) return;
            ch.Medals = balance.Value;
        }
        else ch.Medals += amount; // DB-free development fixtures
        s.Send(new PacketWriter(Msg.CS_UPDATEMEDALS_REQ).WriteUInt32(ch.Medals));
        UseItemStack(s, invenId, bag, item, count);
    }

    // ================================ the rewards window ================================

    /// <summary>C++ <c>OnCS_GETREWARDS_REQ</c> + <c>SendCS_GETREWARDS_ACK</c>: a special box's items for one's class, or a cash
    /// gamble group's.</summary>
    private void OnCS_GETREWARDS_REQ(ClientSession s, PacketReader r)
    {
        byte kind = r.ReadByte();
        ushort useValue = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;

        var rows = new List<FullItemRow>();
        if (kind == IkSpecialBox)
        {
            if (!_templates.SpecialBoxes.TryGetValue(useValue, out var box)) return;
            rows.AddRange(box.Where(b => b.Class == ch.Class || b.Class == TclassCount).Select(b => b.Item));
        }
        else
        {
            if (!_templates.CashGamble.TryGetValue(useValue, out var group)) return;
            rows.AddRange(group.Select(g => g.Item));
        }
        var w = new PacketWriter(Msg.CS_GETREWARDS_ACK);
        var items = rows.Select(ItemFromRow).ToList();
        w.WriteUInt16((ushort)items.Count(i => i is not null));
        foreach (var item in items) item?.WrapPacketClient(w, ch.CharId);
        s.Send(w);
    }

    // ================================ stop the clock ================================

    /// <summary>C++ <c>OnCS_STOPTHECLOCK_REQ</c> → <c>TStopTheClock</c> → <c>OnDM_STOPTHECLOCK_ACK</c>: a timed item (or, with slot
    /// 0xFF, the bag) gets the cash item's days.</summary>
    private async Task OnCS_STOPTHECLOCK_REQ(ClientSession s, PacketReader r)
    {
        byte invenId = r.ReadByte(), slot = r.ReadByte();
        ushort shopItemId = r.ReadUInt16();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch || IsTutorial(ch)) return;
        if (!_templates.CashItems.TryGetValue(shopItemId, out var cashItem)) return;
        if (ch.FindInven(invenId) is not { } bag) return;
        if (slot == InvalidSlot ? bag.EndTime == 0 : bag.FindItem(slot) is not { EndTime: not 0 }) return;

        int? ret = CashStore is { } db ? await db.StopTheClockAsync(ch.CharId, shopItemId) : 0;
        byte result = ret is null ? StcFail : ret != 0 ? StcNeedCash : StcSuccess;
        if (s.Char != ch) return;
        long endTime = 0;
        if (result == StcSuccess)
        {
            if (ch.FindInven(invenId) is not { } b) return;
            if (slot == InvalidSlot)
            {
                b.EndTime += cashItem.UseTime * (long)DayOne;
                endTime = b.EndTime;
                SaveCharData(s);                                                                // the bag's time is saved with the char
            }
            else if (b.FindItem(slot) is { } item)
            {
                item.EndTime += cashItem.UseTime * (long)DayOne;
                endTime = item.EndTime;
                EnqueueItemSave(s, invenId, item);
            }
        }
        s.Send(new PacketWriter(Msg.CS_STOPTHECLOCK_ACK).WriteByte(result).WriteByte(invenId).WriteByte(slot).WriteInt64(endTime));
    }

    // ================================ waste refine ================================

    /// <summary>C++ <c>OnCS_WASTEREFINE_REQ</c>: one refine stone → one of the item its use value names.</summary>
    private void OnCS_WASTEREFINE_REQ(ClientSession s, PacketReader r)
    {
        byte invenId = r.ReadByte(), slot = r.ReadByte();
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        void Ack(byte result) => s.Send(new PacketWriter(Msg.CS_WASTEREFINE_ACK).WriteByte(result));

        if (ch.FindInven(invenId) is not { } bag || bag.FindItem(slot) is not { Count: > 0 } item || item.Template is not { } t)
        { Ack(WasteRefineNotFound); return; }
        if (t.Type != ItRefine) { Ack(WasteRefineNoRefine); return; }
        if (!_templates.Items.TryGetValue(t.UseValue, out var to)) { Ack(WasteRefineNotFound); return; }

        var give = new List<Item> { new() { TemplateId = to.ItemId, Template = to, Count = 1 } };
        if (!CanPush(ch, give)) { Ack(WasteRefineFullInven); return; }
        UseItemStack(s, invenId, bag, item, 1);
        PushTItem(s, give);
        Ack(WasteRefineSuccess);
    }
}
