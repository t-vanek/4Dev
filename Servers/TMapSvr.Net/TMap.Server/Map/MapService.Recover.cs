namespace TMap.Server.Map;

/// <summary>
/// HP/MP regeneration — the C++ <c>CTObjBase::Recover</c> / <c>CTMonster::Recover</c> (TObjBase.cpp:1322,
/// TMonster.cpp:1920), driven off the 1-second map tick. Each resource regenerates once every
/// <see cref="RecoverTime"/>ms while below max; a change broadcasts the new bar (<c>CS_HPMP_ACK</c>) to the
/// 3×3 view. Combat suppresses regen: attacking / being hit enters <c>MT_BATTLE</c> and pushes both anchors
/// out by <see cref="RecoverInit"/> (see <see cref="Character.EnterBattle"/> / <see cref="Monster.EnterBattle"/>).
///
/// <para><b>Player</b> regen: HP only while <c>MT_NORMAL</c>, MP always; flat amount from
/// <c>GetHPR</c>/<c>GetMPR</c> (<see cref="StatEngine.HpRecover"/>/<see cref="StatEngine.MpRecover"/>).
/// <b>Monster</b> regen: HP while not <c>MT_BATTLE</c>, MP regardless of mode, provided it can act; amount =
/// 25% of max with the HPR/MPR buff layer. HP recovery reduces personal/party and guild damage credit by
/// the healed fraction of the previous HP deficit, releasing the keeper at the native 90% threshold and
/// updating viewer colours. STOPRECOVER blocks both resources for players and monsters.</para>
///
/// <para>The monster battle→normal transition is AI-driven
/// (Phase 19 <see cref="MapService.RunMonsterAI"/> drops aggro on target-loss/leash → <c>MT_NORMAL</c>),
/// re-enabling monster HP regen once disengaged. World-entry anchor init is omitted (a freshly-entered,
/// undamaged player is unaffected).</para>
/// </summary>
public sealed partial class MapService
{
    private const uint RecoverTime = 3000;  // C++ RECOVER_TIME (TMapType.h:35)
    private const uint RecoverInit = 5000;  // C++ RECOVER_INIT (TMapType.h:34)
    private const byte MtNormal = 0, MtBattle = 1; // TMODE_TYPE

    /// <summary>The per-tick regen sweep (C++ <c>OnSM_TIMER_REQ</c>): every in-game player + every live
    /// monster. Public so tests can drive it at a chosen <paramref name="now"/> (the map ms clock).</summary>
    public void RunRecover(uint now)
    {
        foreach (var s in _state.AllInGame())
            if (s.Char is { } ch) RecoverPlayer(s, ch, now);
        foreach (var mon in _state.AllMonsters())
            RecoverMonster(mon, now);
    }

    private void RecoverPlayer(ClientSession s, Character ch, uint now)
    {
        if (ch.Hp == 0) return; // a dead character does not regenerate

        bool changed = false;
        bool canRecover = !BuffLayer.HasStatus(ch.MaintainSkills, BuffLayer.StatusStopRecover);

        uint maxHp = MaxHpFor(ch);
        if (canRecover && ch.Mode == MtNormal && now >= ch.RecoverHpTick + RecoverTime && ch.Hp < maxHp)
        {
            ch.Hp = (uint)Math.Min((ulong)ch.Hp + StatEngine.HpRecover(ch, _templates), maxHp);
            ch.RecoverHpTick = now;
            changed = true;
        }

        uint maxMp = MaxMpFor(ch);
        if (canRecover && now >= ch.RecoverMpTick + RecoverTime && ch.Mp < maxMp) // MP regen is NOT mode-gated
        {
            ch.Mp = (uint)Math.Min((ulong)ch.Mp + StatEngine.MpRecover(ch, _templates), maxMp);
            ch.RecoverMpTick = now;
            changed = true;
        }

        if (changed) BroadcastHpMp(s, ch);

        // C++ CTPlayer::OnTimer: leave battle 5s after the last combat action (re-enables HP regen next tick).
        if (ch.Mode == MtBattle && ch.LastAtkTick + RecoverInit < now) ch.Mode = MtNormal;
    }

    private void RecoverMonster(Monster mon, uint now)
    {
        if (!CheckAttack(mon) || BuffLayer.HasStatus(mon.MaintainSkills, BuffLayer.StatusStopRecover)) return;

        bool changed = false;
        bool keeperChanged = false;

        if (mon.Mode != MtBattle && now >= mon.RecoverHpTick + RecoverTime && mon.Hp < mon.MaxHp)
        {
            uint previousHp = mon.Hp;
            uint amount = BuffLayer.Apply(mon.MaintainSkills, mon.MaxHp / 4, BuffLayer.Hpr);
            mon.Hp = (uint)Math.Min((ulong)mon.Hp + amount, mon.MaxHp);
            mon.RecoverHpTick = now;
            keeperChanged = mon.RecoverDamageCredit(previousHp);
            changed = true;
        }

        if (now >= mon.RecoverMpTick + RecoverTime && mon.Mp < mon.MaxMp)
        {
            uint amount = BuffLayer.Apply(mon.MaintainSkills, mon.MaxMp / 4, BuffLayer.Mpr);
            mon.Mp = (uint)Math.Min((ulong)mon.Mp + amount, mon.MaxMp);
            mon.RecoverMpTick = now;
            changed = true;
        }

        if (changed)
            foreach (var p in _state.PlayersAround(mon))
            {
                SendMonsterHpMp(p, mon);
                if (keeperChanged) SendMonsterColor(p, mon);
            }

        // (Monster battle→normal is AI-driven from Phase 19: DropAggro sets MT_NORMAL on target-loss/leash.)
    }
}
