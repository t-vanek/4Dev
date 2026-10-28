using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    private static readonly byte[] SkillWeaponSlots = { Proto.EsPrmWeapon, Proto.EsSndWeapon, Proto.EsLongWeapon };

    private static bool MatchesSkillWeaponKind(byte kind, uint mask)
        => kind is >= 1 and <= 32 && (mask & (1u << (kind - 1))) != 0;

    /// <summary>The client's FindSkillWeapon checks the three weapon slots, skips broken weapons and
    /// matches the skill's kind mask. A weapon lying in a bag cannot satisfy the requirement.</summary>
    private static Item? FindCastWeapon(Character ch, uint mask)
    {
        if (ch.Equipped is not { } equipped) return null;
        foreach (byte slot in SkillWeaponSlots)
            if (equipped.FindItem(slot) is { Count: > 0, Template: { } template } item
                && item.HasPower() && MatchesSkillWeaponKind(template.Kind, mask)) return item;
        return null;
    }

    /// <summary>UseSkillItem / DeleteSkillItem, planned in full before committing. A rejected cast must
    /// not consume its required item before discovering that its weapon or ammunition is missing.
    /// Ammunition follows the inherited single-inventory search; several stacks in that inventory can
    /// supply base count + (emitted hits - 1). Counts are ints, avoiding the original BYTE wraparound.</summary>
    private bool TryConsumeSkillItems(ClientSession s, Character ch, SkillTemplate? skill, int hitCount)
    {
        if (s.Store.IsOpen || s.Deal.InProgress) return false;
        if (skill is null || skill.WeaponId == 0 && skill.UseItem == 0) return true;

        Item? weapon = null;
        if (skill.WeaponId != 0 && (weapon = FindCastWeapon(ch, skill.WeaponId)) is null) return false;

        var spends = new Dictionary<Item, (Inven Bag, int Count)>();
        var bags = ch.Invens.OrderBy(b => b.InvenId).ToArray();
        if (skill.UseItem != 0)
        {
            foreach (var bag in bags)
                if (bag.Items.Where(i => i.TemplateId == skill.UseItem && i.Count > 0)
                    .OrderBy(i => i.ItemSlot).FirstOrDefault() is { } required)
                { spends[required] = (bag, 1); break; }
            if (spends.Count == 0) return false;
        }

        if (weapon?.Template is { UseItemKind: > 0 } ammoWeapon)
        {
            int needed = ammoWeapon.UseItemCount + Math.Max(0, hitCount - 1);
            bool supplied = needed == 0;
            foreach (var bag in bags)
            {
                if (supplied) break;
                var ammo = bag.Items.Where(i => i.Template?.Kind == ammoWeapon.UseItemKind && i.Count > 0)
                    .OrderBy(i => i.ItemSlot).ToArray();
                int Available(Item item) => item.Count - (spends.TryGetValue(item, out var spent) ? spent.Count : 0);
                if (ammo.Sum(Available) < needed) continue;

                foreach (var item in ammo)
                {
                    int take = Math.Min(needed, Available(item));
                    if (take == 0) continue;
                    int reserved = spends.TryGetValue(item, out var spent) ? spent.Count : 0;
                    spends[item] = (bag, reserved + take);
                    needed -= take;
                    if (needed == 0) break;
                }
                supplied = true;
            }
            if (!supplied) return false;
        }

        // The caller has completed all rejection gates. These update/delete choke points also queue
        // the ordinary incremental item persistence; do not mutate Count without notifying the client.
        foreach (var (item, spend) in spends)
            UseItemStack(s, spend.Bag.InvenId, spend.Bag, item, checked((byte)spend.Count));
        if (spends.Count != 0) SendCS_MOVEITEM_ACK(s, MoveItemResult.Success);
        return true;
    }
}
