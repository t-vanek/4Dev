using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    /// <summary>C++ PerformSkill (TObjBase.cpp:3592–3689): instant return and blink effects on a player.
    /// A blink is sent to its owner, whose client checks the landing and then synchronizes CS_MOVE_REQ;
    /// a return uses the normal map/world teleport pipeline.</summary>
    private void ApplyPlayerMovementStatus(ClientSession s, Character ch, SkillTemplate tpl, byte level,
        float x, float y, float z)
    {
        if (ch.Hp == 0) return;
        foreach (var d in tpl.Data)
        {
            if (d.Type != SkillTemplate.SdtStatus) continue;
            switch (d.Exec)
            {
                case SkillTemplate.SdtStatusTeleport:
                case SkillTemplate.SdtStatusWarp:
                    if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) break;
                    PetRiding(s, ch, 0);
                    uint range = d.Exec == SkillTemplate.SdtStatusWarp ? (uint)Math.Max(0, tpl.GetValue(d, level)) : 0;
                    SendCS_WARP_ACK(s, ch.CharId, OtPc, range, x, y, z);
                    break;
                case SkillTemplate.SdtStatusReturn:
                    // A normal map server must not use the two BoW-team return spawns.
                    if (ch.Persist.SpawnId != BowDefaultSpawnId && ch.Persist.SpawnId != BowDefaultSpawnId + TcontryC)
                        Teleport(s, ch, ch.Persist.SpawnId);
                    break;
                case SkillTemplate.SdtStatusReturnPos:
                    Teleport(s, ch, d.Value);
                    break;
            }
        }
    }

    /// <summary>A forced warp on a monster is delivered to its host, which checks the landing and reports
    /// CS_MONMOVE_REQ. The original server excludes the protected monster-template range 30756–30768.</summary>
    private bool ApplyMonsterMovementStatus(Monster mon, SkillTemplate tpl, byte level, float x, float y, float z)
    {
        foreach (var d in tpl.Data)
        {
            if (d.Type != SkillTemplate.SdtStatus || d.Exec != SkillTemplate.SdtStatusWarp) continue;
            if (mon.ChartId is >= 30756 and <= 30768 || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return false;
            if (_state.FindByChar(mon.HostId) is not { State: EnterState.InGame, Char: { } host } s
                || s.Channel != mon.Channel || host.MapId != mon.MapId)
                return false;
            SendCS_WARP_ACK(s, mon.Id, Monster.OtMon, (uint)Math.Max(0, tpl.GetValue(d, level)), x, y, z);
        }
        return true;
    }

    private static void SendCS_WARP_ACK(ClientSession s, uint objectId, byte objectType, uint range,
        float x, float y, float z)
    {
        var w = new PacketWriter(Msg.CS_WARP_ACK);
        w.WriteUInt32(objectId); w.WriteByte(objectType); w.WriteUInt32(range);
        w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        s.Send(w);
    }
}
