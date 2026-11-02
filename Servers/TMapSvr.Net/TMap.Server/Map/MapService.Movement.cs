using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Movement, jump/block, and the view (CS_ENTER/LEAVE) broadcast — ported from <c>OnCS_MOVE_REQ</c>
/// (CSHandler.cpp:439), <c>OnCS_JUMP_REQ</c>, <c>OnCS_BLOCK_REQ</c>, <c>CTMap::OnMove</c> and
/// <c>CTCell::EnterPlayer/LeavePlayer</c>. Visibility is the 3×3 cell block of <see cref="MapGrid"/>
/// (Phase 3): a position change re-buckets the player and, when the centre cell changes, exchanges
/// CS_ENTER/LEAVE with the cells that entered/left the view before broadcasting the move/jump/block ACK to
/// the (new) neighbour block. Cross-server neighbour cells and monster/NPC visibility remain deferred
/// (PORT_STATUS.md).
/// </summary>
public sealed partial class MapService
{
    // Max legit move speed (CSHandler.cpp: > 3.40 and not an operator ⇒ "SpeedHack" kick).
    private const float MaxMoveSpeed = 3.40f;

    private void OnCS_MOVE_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;

        r.ReadUInt16();              // wMapID — read but not persisted (the C++ MOVE handler never writes m_wMapID)
        float posX = r.ReadFloat();
        float posY = r.ReadFloat();
        float posZ = r.ReadFloat();
        ushort pitch = r.ReadUInt16();
        ushort dir = r.ReadUInt16();
        byte mouseDir = r.ReadByte();
        byte keyDir = r.ReadByte();
        byte action = r.ReadByte();
        byte ghost = r.ReadByte();
        float speed = r.ReadFloat();

        if (speed > MaxMoveSpeed)
        {
            _log.LogWarning("SpeedHack from char {Char} (speed {Speed}); dropping.", s.CharId, speed);
            s.Conn.Close();
            return;
        }

        ch.Pitch = pitch; ch.Dir = dir;
        ch.MouseDir = mouseDir; ch.KeyDir = keyDir; ch.Action = action;

        // Monster host-acquisition inputs (C++ CSHandler.cpp:517/555): every move stamps the recency clock; the
        // first real move (not a stand) makes the player host-eligible and fires AT_ENTER on the monsters around
        // it, so the ones that found no host while it stood still get one now.
        ch.LastMoveMs = NowMs;
        if (!ch.CanHost && action != TaStand)
        {
            ch.CanHost = true;
            foreach (var m in _state.MonstersInView(s).ToList())
                if (m.Ai is not null) OnAiEvent(m, AiTrigger.Enter);
        }

        RelocateAndExchangeView(s, posX, posY, posZ);

        var w = new PacketWriter(Msg.CS_MOVE_ACK);
        w.WriteUInt32(s.CharId);
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteUInt16(pitch);
        w.WriteUInt16(dir);
        w.WriteByte(mouseDir);
        w.WriteByte(keyDir);
        w.WriteByte(action);
        w.WriteFloat(speed);
        var ack = w.ToArray();
        // MOVE broadcasts to the 3×3 EXCLUDING self (C++ CSHandler has the `pChar->m_dwID != pPlayer->m_dwID` guard).
        foreach (var other in _state.Neighbors(s)) other.Send(ack);
        if (s.IsMain)
            foreach (var q in TriggerQuests(2 /*TT_POSITION*/, ch.MapId).ToList())
                if (CanRunQuest(ch, q, out _) == QctNone)
                    ExecQuest(s, ch, q, 0, ch.PosX, ch.PosY, ch.PosZ);
    }

    private void OnCS_JUMP_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;

        r.ReadUInt32();              // dwID (target object; self for a player jump)
        byte objType = r.ReadByte(); // bType
        r.ReadByte();                // bChannel
        r.ReadUInt16();              // wMapID
        float posX = r.ReadFloat();
        float posY = r.ReadFloat();
        float posZ = r.ReadFloat();
        ushort pitch = r.ReadUInt16();
        ushort dir = r.ReadUInt16();
        byte action = r.ReadByte();

        ch.Pitch = pitch; ch.Dir = dir; ch.Action = action;

        RelocateAndExchangeView(s, posX, posY, posZ);

        var w = new PacketWriter(Msg.CS_JUMP_ACK);
        w.WriteUInt32(s.CharId);
        w.WriteByte(objType);
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteUInt16(pitch);
        w.WriteUInt16(dir);
        w.WriteByte(action);
        var ack = w.ToArray();
        // JUMP broadcasts via GetNeerPlayer: the 3×3 within CELL_SIZE, INCLUDING self (C++ has no self guard).
        foreach (var other in _state.NearView(s)) other.Send(ack);
    }

    private void OnCS_BLOCK_REQ(ClientSession s, PacketReader r)
    {
        if (s.State != EnterState.InGame || s.Char is not { } ch) return;

        r.ReadUInt32();              // dwID
        byte objType = r.ReadByte(); // bType
        r.ReadByte();                // bChannel
        r.ReadUInt16();              // wMapID
        float posX = r.ReadFloat();
        float posY = r.ReadFloat();
        float posZ = r.ReadFloat();
        ushort pitch = r.ReadUInt16();
        ushort dir = r.ReadUInt16();
        byte action = r.ReadByte();
        byte block = r.ReadByte();

        ch.Pitch = pitch; ch.Dir = dir; ch.Action = action; ch.Block = block;

        RelocateAndExchangeView(s, posX, posY, posZ);

        var w = new PacketWriter(Msg.CS_BLOCK_ACK);
        w.WriteUInt32(s.CharId);
        w.WriteByte(objType);
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteUInt16(pitch);
        w.WriteUInt16(dir);
        w.WriteByte(action);
        w.WriteByte(block);
        var ack = w.ToArray();
        // BLOCK broadcasts via GetNeighbor: the full 3×3, INCLUDING self (C++ has no self guard).
        foreach (var other in _state.InView(s)) other.Send(ack);
    }

    /// <summary>
    /// Writes the new position onto the character, re-buckets it in the grid, and — on a cell change —
    /// exchanges the bidirectional CS_ENTER/LEAVE with the players who entered/left the 3×3 view
    /// (C++ <c>CTMap::OnMove</c> → <c>CTCell::EnterPlayer/LeavePlayer</c>). The caller then broadcasts its
    /// own move/jump/block ACK to the appropriate view set (each C++ handler uses a different one).
    /// </summary>
    private void RelocateAndExchangeView(ClientSession s, float x, float y, float z)
    {
        var ch = s.Char!;
        ch.PosX = x; ch.PosY = y; ch.PosZ = z;

        var diff = _state.Relocate(s, x, z);
        if (!diff.CellChanged) return;

        // Left the view: tell each side the other is gone (bExitMap = FALSE — a cell change, not a map exit).
        foreach (var other in diff.Left)
        {
            s.Send(BuildCS_LEAVE_ACK(other.CharId, exitMap: false));
            other.Send(BuildCS_LEAVE_ACK(s.CharId, exitMap: false));
        }
        // Entered the view: full appearance both directions (move-triggered, so bNewMember = FALSE both ways).
        foreach (var other in diff.Entered)
        {
            s.Send(BuildCS_ENTER_ACK(other));
            other.Send(BuildCS_ENTER_ACK(s));
        }

        // Monsters that entered / left the mover's view (one-directional — monsters have no client). C++
        // CTMap::OnMove runs the same cell diff over m_mapMONSTER via CTCell::EnterPlayer/LeavePlayer.
        foreach (var m in diff.LeftMonsters)
        {
            if (s.Char is { } looter) ReleaseLootLock(looter, m);   // TCell.cpp:387
            SendCS_DELMON_ACK(s, m.Id, exitMap: false);
            MonsterLostSight(m, s.CharId);
        }
        foreach (var m in diff.EnteredMonsters)
        {
            SendCS_ADDMON_ACK(s, m, newMember: false);
            // C++ CTCell::EnterPlayer (TCell.cpp:103): the monster sees the newcomer.
            if (m.Ai is not null) OnAiEvent(m, AiTrigger.Enter, 0, s.CharId, s.CharId, OtPc);
        }
        foreach (var m in diff.LeftRecalls) HideSummon(s, m, exitMap: false, forever: true);
        foreach (var m in diff.EnteredRecalls) ShowSummon(s, m, newMember: false);
        ApplySwitchGateDiff(s, diff); // switches/gates entering/leaving the 3×3 (static objects, Phase 32)
    }

    /// <summary>C++ <c>CTCell::LeavePlayer</c> (TCell.cpp:376-387), per monster: a player it could see is gone,
    /// by walking out of its 3×3 or by leaving the map. Its hate goes (<c>LeaveAggro</c>, which may send a
    /// fighter home) and <c>AT_LEAVE</c> fires — script 1's <c>ChkHost</c>, which hands the monster to another
    /// player in view or resets it. The player must already be out of the monster's view when this runs.</summary>
    private void MonsterLostSight(Monster m, uint charId)
    {
        LeaveAggro(m, charId, charId, OtPc, NowMs);
        if (m.Ai is not null && _state.FindMonster(m.Id) == m)
            OnAiEvent(m, AiTrigger.Leave, 0, charId, charId, OtPc);
    }

    private void BroadcastLeave(ClientSession s, bool exitMap)
    {
        foreach (var other in _state.Neighbors(s))
            other.Send(BuildCS_LEAVE_ACK(s.CharId, exitMap));
    }

    private static byte[] BuildCS_LEAVE_ACK(uint charId, bool exitMap)
    {
        var w = new PacketWriter(Msg.CS_LEAVE_ACK);
        w.WriteUInt32(charId);
        w.WriteByte(exitMap ? (byte)1 : (byte)0);
        return w.ToArray();
    }

    /// <summary>
    /// The full player appearance/position/state block a client renders when another player comes into
    /// view (CSSender.cpp:395). <paramref name="newMember"/> is the trailing <c>bNewMember</c> byte — TRUE
    /// only when an <b>existing</b> player is told about a genuinely first-spawning newcomer (C++
    /// <c>EnterMAP(pPlayer, TRUE)</c> → <c>EnterPlayer</c>); FALSE for the newcomer's own view of others and
    /// for all move-triggered enters.
    /// </summary>
    private byte[] BuildCS_ENTER_ACK(ClientSession s, bool newMember = false)
    {
        var ch = s.Char!;
        var w = new PacketWriter(Msg.CS_ENTER_ACK, capacity: 256);
        w.WriteUInt32(ch.CharId);
        w.WriteString(ch.Name);
        w.WriteUInt16(ch.TitleId);
        w.WriteString(ch.Comment);        // strComment (C++: to allies; bColor is always TNCOLOR_ALLI here)
        w.WriteUInt32(ch.GuildId);
        w.WriteUInt32(ch.Fame);
        w.WriteUInt32(ch.FameColor);
        w.WriteString(ch.GuildName);
        w.WriteByte(ch.GuildPeer);
        w.WriteUInt32(ch.TacticsId);
        w.WriteString(ch.TacticsName);
        w.WriteByte((byte)(s.Store.IsOpen ? 1 : 0));            // bStore (late-joiner sees an open store)
        w.WriteString(s.Store.IsOpen ? s.Store.Name : "");      // strStoreName
        w.WriteUInt32(ch.Riding);         // dwRiding — the mount's recall id
        w.WriteByte(ch.Class);
        w.WriteByte(ch.Race);
        w.WriteByte(ch.Country);
        w.WriteByte(ch.AidCountry);
        w.WriteByte(ch.Sex);
        w.WriteByte(ch.Hair);
        w.WriteByte(ch.Face);
        w.WriteByte(ch.Body);
        w.WriteByte(ch.Pants);
        w.WriteByte(ch.Hand);
        w.WriteByte(ch.Foot);
        w.WriteByte(ch.Level);
        w.WriteByte(ch.HelmetHide);
        uint maxHp = MaxHpFor(ch), maxMp = MaxMpFor(ch); // C++ GetMaxHP/GetMaxMP; current HP/MP clamped down
        w.WriteUInt32(maxHp);
        w.WriteUInt32(Math.Min(ch.Hp, maxHp));
        w.WriteUInt32(maxMp);
        w.WriteUInt32(Math.Min(ch.Mp, maxMp));
        w.WriteUInt32(ch.PartyChiefId);
        w.WriteUInt16(ch.PartyId);
        w.WriteUInt16((ushort)ch.CommanderId);
        w.WriteFloat(ch.PosX);
        w.WriteFloat(ch.PosY);
        w.WriteFloat(ch.PosZ);
        w.WriteByte(ch.Action);
        w.WriteByte(ch.Block);
        w.WriteByte(ch.Mode);
        w.WriteUInt16(ch.Pitch);
        w.WriteUInt16(ch.Dir);
        w.WriteByte(ch.MouseDir);
        w.WriteByte(ch.KeyDir);
        w.WriteByte(0);                   // bColor (TNCOLOR_ALLI)
        w.WriteUInt32(ch.RegionId);
        w.WriteByte(ch.InPcBang);         // bInPcBang
        w.WriteByte(ch.Persist.Aftermath); // aftermath.m_bStep
        w.WriteUInt32(0);                 // dwRankPoint
        var (castle, camp, godBall) = EnterCastleFields(ch);   // MapService.Territory.cs (the entering player's)
        w.WriteUInt16(castle);            // wCastle
        w.WriteByte(camp);                // bCamp
        w.WriteUInt16(godBall);           // wGodBall

        // Maintained (buff) skills — same block as CS_CHARINFO_ACK.
        w.WriteByte((byte)ch.MaintainSkills.Count);
        foreach (var m in ch.MaintainSkills) WriteMaintainSkill(w, m, NowMs);

        // Equipped gear (INVEN_EQUIP container) — no per-container header, just count + items. The C++ equip
        // container is map<BYTE slot>, so items go out ascending by slot (byte-sequence parity, not correctness).
        var equip = ch.Equipped;
        w.WriteByte((byte)(equip?.Items.Count ?? 0));
        if (equip is not null)
            foreach (var it in equip.Items.OrderBy(x => x.ItemSlot)) it.WrapPacketClient(w, ch.CharId);

        w.WriteByte(newMember ? (byte)1 : (byte)0); // bNewMember
        return w.ToArray();
    }
}
