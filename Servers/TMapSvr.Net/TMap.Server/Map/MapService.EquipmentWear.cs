using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    // Separate from hit/crit rolls so equipment changes do not perturb combat test sequences.
    public Random EquipmentRng { get; set; } = Random.Shared;
    private static readonly byte[] WearArmorSlots = { 3, 5, 6, 8, 7, 4 }; // head, body, pants, hand, foot, back

    /// <summary>CTPlayer::DurationDec / CTItem::GetAutoSkill: chart-selected equipment, one 0..2 wear
    /// roll per item, and the first successful option proc (raw stored option value, ordered by ID).
    /// Indestructible 0/0 equipment can proc but must not be destroyed by natural wear.</summary>
    private ushort WearSkillEquipment(ClientSession s, Character ch, SkillTemplate? skill)
        => skill is null ? (ushort)0 : WearEquipment(s, ch, skill.DuraSlot, skill.IsItemDelUser());

    private ushort WearEquipment(ClientSession s, Character ch, byte selector, bool exhaust)
    {
        if (selector == 0 || ch.Equipped is not { } equipped) return 0;
        byte[] slots = selector switch
        {
            1 => new[] { Proto.EsLongWeapon },
            2 => new[] { Proto.EsPrmWeapon, Proto.EsSndWeapon },
            3 => new[] { WearArmorSlots[EquipmentRng.Next(WearArmorSlots.Length)] },
            4 => new[] { Proto.EsSndWeapon },
            _ => Array.Empty<byte>(),
        };
        ushort backSkill = 0;
        bool refresh = false;
        foreach (byte slot in slots)
        {
            if (equipped.FindItem(slot) is not { Count: > 0, Template: { } template } item || !item.HasPower()) continue;
            if (selector == 2 && template.Type == 6 || selector == 4 && template.Type != 6) continue;

            if (backSkill == 0)
                foreach (var option in item.Magic.OrderBy(m => m.Id))
                {
                    var magic = option.Template ?? _templates.Magics.GetValueOrDefault(option.Id);
                    if (magic is { AutoSkill: > 0 } && _templates.Skill(magic.AutoSkill) is not null
                        && option.Value > EquipmentRng.Next(100))
                    { backSkill = magic.AutoSkill; break; }
                }

            if (item.DuraMax == 0 && !exhaust) continue;
            uint before = item.DuraCur, wear = (uint)EquipmentRng.Next(3);
            item.DuraCur = exhaust || before <= wear ? 0 : before - wear;
            if (item.DuraCur == 0)
            {
                bool deleted = template.CanRepair == 0;
                s.Send(new PacketWriter(Msg.CS_DURATIONEND_ACK).WriteByte(Proto.InvenEquip)
                    .WriteByte(item.ItemSlot).WriteByte(deleted ? (byte)1 : (byte)0));
                if (deleted)
                {
                    equipped.Items.Remove(item);
                    SendCS_DELITEM_ACK(s, Proto.InvenEquip, item);
                }
                else EnqueueItemSave(s, Proto.InvenEquip, item);
                refresh = true;
            }
            else
            {
                s.Send(new PacketWriter(Msg.CS_DURATIONDEC_ACK).WriteByte(Proto.InvenEquip)
                    .WriteByte(item.ItemSlot).WriteUInt32(item.DuraCur));
                if (item.DuraCur != before) EnqueueItemSave(s, Proto.InvenEquip, item);
            }
        }
        if (refresh) ChangeEquipItem(s);
        return backSkill;
    }

    /// <summary>CTObjBase::Defend: wear after CalcDamage, before OnDamage. A block wears the shield;
    /// otherwise positive net HP damage selects one armor slot. The damage map remains the original
    /// pre-reaction display, as in the native sender; the HP/MP update reports the actual resulting bars.</summary>
    private ushort ApplyDefensiveEquipment(ClientSession s, Character ch, AttackerPower attacker,
        uint attackId, byte attackType, uint hostId, SkillTemplate? incoming, byte level, byte hitType,
        ref DamageResult damage)
    {
        if (ch.Hp == 0 || hitType == HtMiss || incoming?.IsDamageType() != true) return 0;
        byte selector = damage.Blocked ? (byte)4 : damage.DamageHp > 0 ? (byte)3 : (byte)0;
        ushort autoId = WearEquipment(s, ch, selector, incoming.IsItemDelTarget());
        // CanFight still gates the reaction, even though the hit itself has already worn the gear.
        if (autoId == 0 || !CanReactToAttack(ch, attackType, attacker.Country, attacker.AidCountry)
            || _templates.Skill(autoId) is not { } auto) return 0;

        if (damage.DamageHp > 0)
        {
            if (auto.IsDamageType())
            {
                int hp = damage.DamageHp;
                hp += auto.CalcValue(level, SdtAbility, MtypeDamage, unchecked((uint)hp));
                hp += auto.CalcValue(level, SdtAbility, MtypeHp, unchecked((uint)hp));
                damage = damage with { DamageHp = hp };
                return 0;
            }
            if (auto.IsMirrorType())
            {
                SendMW_GETBLOOD_ACK(attackId, attackType, hostId, MtypeDamage, (uint)damage.DamageHp);
                damage = damage with { DamageHp = 0 };
                return 0;
            }
        }

        // The defender's client receives wBackSkill in DEFEND_ACK and launches it at the attacker,
        // at level 1. It needs a separate authorization; it does not spend the defender's skill costs.
        AuthorizePlayerCast(s, new Skill { SkillId = autoId, Level = 1, Template = auto },
            new[] { (attackId, attackType) }, requireLearned: false);
        return autoId;
    }

    private static bool CanReactToAttack(Character defender, byte type, byte country, byte aidCountry)
        => GetAttackCountry(country, aidCountry) != WarCountryOf(defender)
            && (type == Monster.OtMon || defender.Country != TcontryPeace && country != TcontryPeace);
}
