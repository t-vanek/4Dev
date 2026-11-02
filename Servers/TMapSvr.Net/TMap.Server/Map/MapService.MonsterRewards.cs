using TMap.Protocol;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    private ushort MonsterHuntTerm(Monster mon)
        => _templates.MonsterTemplates.GetValueOrDefault(mon.ChartId)?.HuntKind ?? mon.ChartId;

    // Native TMonster::OnDie secondary-party branch; World forwards the body to the current main.
    private void SendMonsterReward(ClientSession recipient, Monster mon, uint exp)
    {
        if (!_worldReady) return;
        _world.Send(new PacketWriter(Msg.MW_MONSTERDIE_ACK).WriteUInt32(recipient.CharId).WriteUInt32(recipient.Key)
            .WriteUInt32(exp).WriteUInt32(mon.Id).WriteFloat(mon.PosX).WriteFloat(mon.PosY).WriteFloat(mon.PosZ)
            .WriteUInt16(MonsterHuntTerm(mon)).WriteByte(QttHunt).WriteByte(TtKillMon).WriteByte(1));
    }

    /// <summary>SSHandler.cpp:9770. The incoming share has already been scaled at the kill site.
    /// The native remote branch does not run the main-copy equipment/EXP bonus or soulmate branch.</summary>
    private void OnMW_MONSTERDIE_REQ(PacketReader r)
    {
        uint charId = r.ReadUInt32(), key = r.ReadUInt32(), exp = r.ReadUInt32(), monId = r.ReadUInt32();
        float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat();
        ushort term = r.ReadUInt16();
        byte termType = r.ReadByte(), trigger = r.ReadByte(), count = r.ReadByte();
        if (_state.FindByChar(charId) is not { IsMain: true, State: EnterState.InGame, Char: { } ch } s || s.Key != key) return;
        if (ch.Hp != 0 && exp != 0) GainExp(s, ch, exp);
        CheckQuest(s, monId, x, y, z, term, termType, trigger, count);
    }
}
