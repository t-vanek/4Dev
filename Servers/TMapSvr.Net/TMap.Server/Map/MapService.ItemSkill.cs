using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The use-items that are not potions — C++ <c>OnCS_ITEMUSE_REQ</c> (CSHandler.cpp:9092), the <c>IK_SKILL</c> /
/// <c>IK_REVIVAL</c> / <c>IK_RETURN</c> / <c>IK_FACE…IK_SEX</c> cases — and the random skills.
/// <list type="bullet">
/// <item><b>A skill item</b> (buff scrolls, transformation potions, orbs, the revival scroll) casts the skill its
/// <c>wUseValue</c> names on its user, at level 1, for the item's use time when it has one (<c>Defend</c> on oneself).
/// It is refused when a disguise is on and the skill transforms, when the disguise is one's own country's, when the same
/// buff (or another luck / exp potion) is already on, and for an aftermath cure with no aftermath.</item>
/// <item><b>Random skills</b> (C++ <c>RandTransSkill</c> / <c>RandBuffSkill</c>): the skill stands for a run of skills —
/// the potion of doubt, the coincidental metamorphosis potion, the shape-changing clouds, the lucky bag — and one of them,
/// picked at random, is what lands. Also on a cast (<c>FINISHSKILL</c>), per target.</item>
/// <item><b>A return scroll</b> takes its user to the spawn point it names (not from the peace country).</item>
/// <item><b>A face / hair / race / sex change</b> (C++ <c>ChangeCharBase</c>) picks a new look at random — refused while
/// transformed or hidden — and goes through the world, which tells every map; the map shows it around and saves it
/// (<c>TSaveCharBase</c>). A new race drops a mount being ridden and resets the stat sheet.</item>
/// </list>
/// <para>The item delay (<c>m_mapItemCoolTime</c>, per delay group) is enforced for every use item; the exp / premium / money /
/// box kinds are in MapService.DuringItem.cs. <b>Not ported:</b> the tournament and arena gates.</para>
/// </summary>
public sealed partial class MapService
{
    // TITEM_KIND (NetCode.h:1182)
    private const byte IkSkill = 33, IkRevival = 41, IkReturn = 44, IkFace = 45, IkHair = 46, IkRace = 47, IkSex = 49;
    private const uint HourOne = 3600, DayOne = 86400;

    /// <summary>C++ <c>RandTransSkill</c> / <c>RandBuffSkill</c> on a skill that is random (itself otherwise): one skill of
    /// the run, which must transform / buff; null when none fits.</summary>
    private SkillTemplate? PickRandomSkill(SkillTemplate tpl)
    {
        if (tpl.IsRandomTrans())
        {
            if (tpl.RandomPick() is not { } run || !_templates.Skills.TryGetValue((ushort)(run.First + CombatRng.Next(run.Count)), out var t)
                || !t.IsTrans()) return null;
            tpl = t;
        }
        if (tpl.IsRandomBuff())
        {
            if (tpl.RandomPick() is not { } run || !_templates.Skills.TryGetValue((ushort)(run.First + CombatRng.Next(run.Count)), out var t)
                || !t.IsBuffType()) return null;
            tpl = t;
        }
        return tpl;
    }

    /// <summary>The <c>IK_SKILL</c> / <c>IK_REVIVAL</c> case. Returns whether the item was used.</summary>
    private bool UseSkillItem(ClientSession s, Character ch, ItemTemplate it)
    {
        if (!_templates.Skills.TryGetValue(it.UseValue, out var tpl)) return false;
        if (tpl.IsTrans() && ch.MaintainSkills.Any(m => m.Template?.Disguise() > 0)) return false;   // HaveDisguiseBuff
        if (!tpl.CheckCountry(ch.Country)) return false;
        if (!CheckItemBuff(ch, tpl)) return false;
        if (PickRandomSkill(tpl) is not { } cast) return false;
        if (cast.IsAfterMath() && ch.Persist.Aftermath == 0) return false;

        uint remainMs = (it.UseType & DuringTypeTime) != 0 ? it.UseTime * HourOne * 1000
            : (it.UseType & DuringTypeDay) != 0 ? it.UseTime * DayOne * 1000 : 0;
        SkillOnSelf(s, ch, cast, remainMs);
        return true;
    }

    /// <summary>C++ <c>CTPlayer::CheckItemBuff</c> (TPlayer.cpp:5205): not the same buff twice, nor two luck or two exp
    /// potions.</summary>
    private static bool CheckItemBuff(Character ch, SkillTemplate tpl)
    {
        bool lucky = tpl.IsLuckyPotion(), exp = tpl.IsExpPotion();
        foreach (var m in ch.MaintainSkills)
        {
            if (m.SkillId == tpl.Id) return false;
            if (lucky && m.Template?.IsLuckyPotion() == true) return false;
            if (exp && m.Template?.IsExpPotion() == true) return false;
        }
        return true;
    }

    /// <summary>C++ <c>pPlayer->Defend(pTemp, 1, self…)</c>: the skill's buff (for <paramref name="remainMs"/> when set),
    /// its cures (a revival raises its user) and its HP/MP statuses, on oneself.</summary>
    private void SkillOnSelf(ClientSession s, Character ch, SkillTemplate tpl, uint remainMs)
    {
        if (tpl.IsMaintainType()) ForceMaintain(s, ch, tpl.Id, ch.CharId, OtPc, ch.CharId, OtPc, remainMs);
        if (tpl.HasCure()) ApplyPlayerCure(s, ch, ch.CharId, tpl, 1, ch.CharId, 0, 0, ch.PosX, ch.PosY, ch.PosZ);
        if (tpl.HasVitalsStatus() || tpl.HasMovementStatus())
            ApplyPlayerStatus(s, ch, ch.CharId, tpl, 1, ch.CharId, ch.PosX, ch.PosY, ch.PosZ);
    }

    /// <summary>The <c>IK_RETURN</c> case: to the spawn point the scroll names, unless one is of the peace country.</summary>
    private bool UseReturnItem(ClientSession s, Character ch, ItemTemplate it)
        => ch.Country != TcontryPeace && Teleport(s, ch, it.UseValue);

    // ================================ face / hair / race / sex ================================

    /// <summary>C++ <c>CTPlayer::CanUseRaceChange</c> (TPlayer.cpp:7134): not while transformed, hidden, clear or
    /// stand-hidden.</summary>
    private static bool CanUseRaceChange(Character ch)
        => !ch.MaintainSkills.Any(m => m.Template?.Data.Any(d => d.Type == SkillTemplate.SdtTrans
            || (d.Type == SkillTemplate.SdtStatus && d.Inc == 1 /* SVI_INCREASE */
                && d.Exec is SdtStatusHide or SdtStatusClarity or SdtStatusStandHide)) == true);

    private const byte SdtStatusHide = 9, SdtStatusClarity = 40, SdtStatusStandHide = 46;   // SDT_STATUS_TYPE

    /// <summary>C++ <c>CTPlayer::ChangeCharBase</c> (TPlayer.cpp:5054) for a look item: the new value (a different one at
    /// random; the other sex), sent through the world (<c>MW_CHANGECHARBASE_ACK</c>) and saved.</summary>
    private bool ChangeLook(ClientSession s, Character ch, byte kind)
    {
        if (!CanUseRaceChange(ch)) return false;
        byte value;
        switch (kind)
        {
            case IkFace: value = (byte)CombatRng.Next(8); if (value == ch.Face) value = (byte)((ch.Face + 1) % 4); break;   // sic: % 4
            case IkHair: value = (byte)CombatRng.Next(7); if (value == ch.Hair) value = (byte)((ch.Hair + 1) % 7); break;
            case IkRace: value = (byte)CombatRng.Next(3); if (value == ch.Race) value = (byte)((ch.Race + 1) % 3); break;
            case IkSex: value = (byte)(ch.Sex == 0 ? 1 : 0); break;
            default: return false;
        }
        var w = new PacketWriter(Msg.MW_CHANGECHARBASE_ACK);
        w.WriteUInt32(ch.CharId); w.WriteUInt32(s.Key); w.WriteByte(kind); w.WriteByte(value); w.WriteUInt16(ch.TitleId); w.WriteString("");
        _world.Send(w);
        if (_gameDb is { } db) { uint id = ch.CharId; _ = EnqueueDbWrite(() => db.SaveCharBaseAsync(id, kind, value, "")); }
        return true;
    }

    /// <summary>The look kinds of C++ <c>OnMW_CHANGECHARBASE_REQ</c>: the new value is the player's; a new race drops a
    /// ridden mount and sends the new bars and stat sheet.</summary>
    private void ApplyLook(ClientSession s, Character ch, byte kind, byte value)
    {
        switch (kind)
        {
            case IkFace: ch.Face = value; break;
            case IkHair: ch.Hair = value; break;
            case IkSex: ch.Sex = value; break;
            case IkRace:
                ch.Race = value;
                if (!s.IsMain) break;
                if (ch.FindRecallPet() is { } pet) SendMW_RECALLMONDEL_ACK(ch.CharId, s.Key, pet.Id);
                BroadcastHpMp(s, ch);
                SendCS_CHARSTATINFO_ACK(s, ch);
                break;
        }
    }

    private static bool IsLookKind(byte kind) => kind is IkFace or IkHair or IkRace or IkSex;
}
