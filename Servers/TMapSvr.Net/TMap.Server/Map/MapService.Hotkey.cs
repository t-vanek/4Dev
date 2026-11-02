using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The action bar: <c>CS_HOTKEYADD_REQ</c> / <c>CS_HOTKEYDEL_REQ</c> (CSHandler.cpp:7081-7118) →
/// <c>CTPlayer::AddHotKey</c> / <c>EraseHotKey</c> (TPlayer.cpp:822-907). The pages were already loaded and sent
/// at login; without these the bar reset to its login state on every relog.
///
/// <para>Faithful quirks: adding a key first clears every slot on the page holding the same (type, id) and the
/// target slot itself, each clear answered with its own one-entry <c>CS_HOTKEYCHANGE_ACK</c>; a brand-new
/// page skips that sweep. A <b>global</b> skill (<c>m_bGlobal</c>) is granted by placing it and revoked by
/// clearing it. Neither handler validates the page id or the key type — the client is trusted, as in the C++.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte HotkeyNone = 0, HotkeySkill = 1;   // HOTKEY_TYPE (NetCode.h:1494)

    private void OnCS_HOTKEYADD_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte type = r.ReadByte();
        ushort id = r.ReadUInt16();
        byte inven = r.ReadByte();
        byte pos = r.ReadByte();
        AddHotKey(s, ch, inven, pos, type, id);
    }

    private void OnCS_HOTKEYDEL_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || !s.IsMain || s.Char is not { } ch) return;
        byte inven = r.ReadByte();
        byte pos = r.ReadByte();
        EraseHotKey(s, ch, inven, pos);
    }

    private void AddHotKey(ClientSession s, Character ch, byte inven, byte pos, byte type, ushort id)
    {
        if (pos >= HotkeyPage.SlotCount) return;

        var page = ch.HotkeyPages.FirstOrDefault(p => p.InvenKey == inven);
        if (page is not null)
        {
            for (byte i = 0; i < HotkeyPage.SlotCount; i++)
            {
                var k = page.Slots[i];
                if ((k.Type == type && k.Id == id) || (i == pos && k.Type != HotkeyNone))
                    EraseHotKey(s, ch, inven, i);
            }
            page.Save |= HotkeyPage.SaveUpdate;
        }
        else
        {
            page = new HotkeyPage { InvenKey = inven, Save = HotkeyPage.SaveInsert };
            ch.HotkeyPages.Add(page);
        }
        page.Slots[pos] = new HotkeySlot(type, id);

        if (type == HotkeySkill && ch.Skills.All(k => k.SkillId != id)
            && _templates.Skills.TryGetValue(id, out var tpl) && tpl.Global)
        {
            ch.Skills.Add(new Skill { SkillId = id, Level = 1, Template = tpl });   // CTSkill() starts at level 1
            SendCS_SKILLBUY_ACK(s, ch, SkillUseResult.Success, id, 1);
        }

        SendCS_HOTKEYCHANGE_ACK(s, inven, (pos, type, id));
    }

    private void EraseHotKey(ClientSession s, Character ch, byte inven, byte pos)
    {
        if (pos >= HotkeyPage.SlotCount) return;

        var page = ch.HotkeyPages.FirstOrDefault(p => p.InvenKey == inven);
        if (page is null)
        {
            SendCS_HOTKEYCHANGE_ACK(s, inven);   // the C++ still answers, with an empty list
            return;
        }

        var old = page.Slots[pos];
        if (old.Type == HotkeySkill && ch.Skills.FirstOrDefault(k => k.SkillId == old.Id) is { Template.Global: true } g)
            ch.Skills.Remove(g);

        page.Slots[pos] = new HotkeySlot(HotkeyNone, 0);
        page.Save |= HotkeyPage.SaveUpdate;
        SendCS_HOTKEYCHANGE_ACK(s, inven, (pos, HotkeyNone, 0));
    }

    /// <summary>C++ <c>SendCS_HOTKEYCHANGE_ACK</c> (CSSender.cpp:3166) — <c>bInven · bCount · {bPos bType wID}</c>.</summary>
    private static void SendCS_HOTKEYCHANGE_ACK(ClientSession s, byte inven, params (byte pos, byte type, ushort id)[] keys)
    {
        var w = new PacketWriter(Msg.CS_HOTKEYCHANGE_ACK, capacity: 8 + keys.Length * 4);
        w.WriteByte(inven);
        w.WriteByte((byte)keys.Length);
        foreach (var (pos, type, id) in keys)
        {
            w.WriteByte(pos);
            w.WriteByte(type);
            w.WriteUInt16(id);
        }
        s.Send(w);
    }

    /// <summary>C++ <c>SendDM_SAVECHAR_REQ</c>'s hotkey block (SSSender.cpp:1213): the page flag becomes the
    /// <c>TSaveHotkey</c> verb — an all-empty page 1 (delete), a new page 2 (insert), an edited page 3 (update),
    /// an untouched page is skipped — and every page sent drops back to <c>ITEM_SAVE_LOAD</c>.</summary>
    public static List<HotkeySaveRow> BuildHotkeySaves(Character ch, bool clearFlags = true)
    {
        var rows = new List<HotkeySaveRow>();
        foreach (var page in ch.HotkeyPages.OrderBy(p => p.InvenKey))
        {
            byte verb;
            if (page.IsEmpty) verb = 1;
            else if ((page.Save & HotkeyPage.SaveInsert) != 0) verb = 2;
            else if ((page.Save & HotkeyPage.SaveUpdate) != 0) verb = 3;
            else continue;

            rows.Add(new HotkeySaveRow(verb, page.InvenKey, page.Slots.Select(k => (k.Type, k.Id)).ToArray()));
            if (clearFlags) page.Save = HotkeyPage.SaveLoad;
        }
        return rows;
    }
}
