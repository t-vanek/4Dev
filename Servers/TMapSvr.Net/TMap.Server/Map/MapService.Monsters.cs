using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Monster visibility — <c>CS_ADDMON_ACK</c> / <c>CS_DELMON_ACK</c> (CSSender.cpp:913/1011) and the
/// grid-driven exchange (C++ <c>CTMap::EnterMAP</c>/<c>OnMove</c> → <c>CTCell::EnterMonster</c>/
/// <c>EnterPlayer</c>). A monster occupies one 64-unit cell exactly like a player; it becomes visible to the
/// players in its 3×3 view block, and a player entering / moving into a cell block learns of the monsters
/// there. Monsters use their own packets (not the player <c>CS_ENTER_ACK</c>).
///
/// <para>This file handles visibility and per-viewer faction/keeper colours. Spawn selection, AI, combat,
/// rewards and maintained effects live in the other MapService partials; see PORT_STATUS.md for their
/// current implementation and verification limits.</para>
/// </summary>
public sealed partial class MapService
{
    /// <summary>Spawns a monster: places it on the grid and announces it to the players in its 3×3 view
    /// (C++ <c>CTMap::EnterMAP(monster)</c> → <c>CTCell::EnterMonster</c>, <c>bNewMember = TRUE</c>).</summary>
    public void SpawnMonster(Monster m)
    {
        if (m.Attr is null && _templates.MonsterTemplates.TryGetValue(m.ChartId, out var chart))
            m.Attr = _templates.MonAttr(chart.MonAttr, m.Level);
        _state.AddMonster(m);
        foreach (var p in _state.PlayersAround(m)) SendCS_ADDMON_ACK(p, m, newMember: true);

        // C++ CTMap::EnterMAP(monster) (TMap.cpp:1014) fires AT_ENTER on every OT_MON it places — respawns
        // included. That is how a respawned monster finds a host among the players already standing there;
        // without it the monster stayed unhosted and no client ever drove it.
        if (m.Ai is not null) OnAiEvent(m, AiTrigger.Enter);
    }

    /// <summary>Despawns a monster: tells the players in its view it is gone, then removes it (C++
    /// <c>LeaveMonster</c> → <c>SendCS_DELMON_ACK</c>).</summary>
    public void DespawnMonster(Monster m, bool exitMap = false)
    {
        foreach (var p in _state.PlayersAround(m)) SendCS_DELMON_ACK(p, m.Id, exitMap);
        _state.RemoveMonster(m);
    }

    /// <summary>Tells a just-entered player about every monster already in its view (C++
    /// <c>CTCell::EnterPlayer</c> <c>m_mapMONSTER</c> loop, <c>bNewMember = FALSE</c>).</summary>
    private void SendMonstersInView(ClientSession s)
    {
        foreach (var m in _state.MonstersInView(s))
        {
            SendCS_ADDMON_ACK(s, m, newMember: false);
            // C++ CTCell::EnterPlayer (TCell.cpp:103) fires AT_ENTER on every monster the arriving
            // player can see — the event-driven aggro-on-sight hook a scripted monster uses in place of the
            // per-tick TryAcquireHost scan. Script-less monsters keep that scan; this is a no-op for them.
            if (m.Ai is not null && s.Char is { CharId: var cid })
                OnAiEvent(m, AiTrigger.Enter, 0, cid, cid, OtPc);
        }
    }

    /// <summary>C++ <c>CTPlayer::SendCS_ADDMON_ACK</c> — the monster appearance block, ending with the
    /// monster's maintained-skill (debuff) list. Field order/types are byte-exact with CSSender.cpp:928-972.</summary>
    private void SendCS_ADDMON_ACK(ClientSession s, Monster m, bool newMember)
    {
        var w = new PacketWriter(Msg.CS_ADDMON_ACK, capacity: 64);
        w.WriteUInt32(m.Id);
        w.WriteUInt16(m.ChartId);
        w.WriteByte(m.Level);
        w.WriteUInt32(m.MaxHp);
        w.WriteUInt32(m.Hp);
        w.WriteUInt32(m.MaxMp);
        w.WriteUInt32(m.Mp);
        w.WriteFloat(m.PosX);
        w.WriteFloat(m.PosY);
        w.WriteFloat(m.PosZ);
        w.WriteUInt16(m.Pitch);
        w.WriteUInt16(m.Dir);
        w.WriteByte(m.MouseDir);
        w.WriteByte(m.KeyDir);
        w.WriteByte(m.Action);
        w.WriteByte(m.Mode);
        w.WriteByte(newMember ? (byte)1 : (byte)0);
        w.WriteByte(m.Country);
        w.WriteByte(m.ColorFor(s.CharId, s.Char?.GetPartyId() ?? 0, s.Char is { } viewer ? WarCountryOf(viewer) : TcontryN));
        w.WriteUInt32(m.Region);
        w.WriteByte((byte)m.MaintainSkills.Count);   // the monster's active debuffs
        foreach (var buff in m.MaintainSkills) WriteMaintainSkill(w, buff, NowMs);
        s.Send(w);
    }

    private static void SendMonsterColor(ClientSession s, Monster mon)
    {
        if (s.Char is not { } ch) return;
        s.Send(new PacketWriter(Msg.CS_CHANGECOLOR_ACK).WriteByte(Monster.OtMon).WriteUInt32(mon.Id)
            .WriteByte(mon.ColorFor(ch.CharId, ch.GetPartyId(), WarCountryOf(ch))).WriteByte(mon.Country));
    }

    /// <summary>C++ <c>CTPlayer::SendCS_DELMON_ACK</c> — <c>dwMonID</c> + <c>bExitMap</c>.</summary>
    private static void SendCS_DELMON_ACK(ClientSession s, uint monId, bool exitMap)
    {
        var w = new PacketWriter(Msg.CS_DELMON_ACK, capacity: 8);
        w.WriteUInt32(monId);
        w.WriteByte(exitMap ? (byte)1 : (byte)0);
        s.Send(w);
    }
}
