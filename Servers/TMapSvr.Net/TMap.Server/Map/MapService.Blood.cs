using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    /// <summary>The receiving half of lifedrain/reflection (SSHandler.cpp:12550). Only World may
    /// deliver this to the authenticated main route. Reflection leaves at least 1 HP and never
    /// re-enters damage/defence processing, so mirrors cannot recursively trigger each other.</summary>
    private void OnMW_GETBLOOD_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32(), attackId = r.ReadUInt32();
        byte type = r.ReadByte(), bloodType = r.ReadByte();
        uint blood = r.ReadUInt32();
        if (_state.FindByChar(charId) is not { IsMain: true, State: EnterState.InGame, Char: { } ch } s
            || s.Key != key || bloodType is not (MtypeHi or MtypeMi or MtypeDamage)) return;

        if (type == OtPc)
        {
            if (attackId != ch.CharId || ch.Hp == 0) return;
            uint maxHp = MaxHpFor(ch), maxMp = MaxMpFor(ch);
            (ch.Hp, ch.Mp) = ApplyBlood(ch.Hp, ch.Mp, maxHp, maxMp, bloodType, blood);
            foreach (var viewer in _state.InView(s)) SendSelfHpMp(viewer, ch.CharId, maxHp, ch.Hp, maxMp, ch.Mp);
        }
        else if (type == Monster.OtMon)
        {
            if (_state.FindMonster(attackId) is not { Hp: > 0 } mon || mon.HostId != ch.CharId
                || mon.MapId != ch.MapId || mon.Channel != s.Channel) return;
            (mon.Hp, mon.Mp) = ApplyBlood(mon.Hp, mon.Mp, mon.MaxHp, mon.MaxMp, bloodType, blood);
            foreach (var viewer in _state.PlayersAround(mon)) SendMonsterHpMp(viewer, mon);
        }
        else if (type is RecallMon.OtRecall or RecallMon.OtSelf or RecallMon.OtCompanion)
        {
            if (_state.FindRecall(type, attackId) is not { Hp: > 0, InMap: true } recall || recall.OwnerId != ch.CharId
                || recall.MapId != ch.MapId || recall.Channel != s.Channel) return;
            (recall.Hp, recall.Mp) = ApplyBlood(recall.Hp, recall.Mp, recall.MaxHp, recall.MaxMp, bloodType, blood);
            foreach (var viewer in _state.PlayersAround(recall)) SendSummonHpMp(viewer, recall);
        }
    }

    private static (uint Hp, uint Mp) ApplyBlood(uint hp, uint mp, uint maxHp, uint maxMp, byte type, uint blood)
        => type switch
        {
            MtypeHi => ((uint)Math.Min((ulong)maxHp, (ulong)hp + blood), mp),
            MtypeMi => (hp, (uint)Math.Min((ulong)maxMp, (ulong)mp + blood)),
            MtypeDamage => (hp > blood ? hp - blood : 1u, mp),
            _ => (hp, mp),
        };
}
