namespace TMap.Server.Map;

/// <summary>
/// C++ <c>CTObjBase::CalcAbilityValue</c> (TObjBase.cpp:1409) for an object that is not a player — a monster or a summon:
/// the <c>SA_BUFF</c> ability rows of its maintained skills on one ability (an <c>MTYPE_*</c>), off the value they
/// scale. These objects have no remain skills, and the cure term and <c>ApplyEffectionBuff</c> are left out as for players
/// (see <see cref="StatEngine.CalcAbilityValue"/>). Also the status tests the getters make (<c>HaveDisWeapon</c>,
/// <c>HaveDisDefend</c>, the immunities).
/// </summary>
public static class BuffLayer
{
    private const byte SaBuff = 3;                     // SKILL_ACTION SA_BUFF
    private const byte SdtStatus = 6;                  // SDT_STATUS
    public const byte StatusDisWeapon = 4, StatusExceptMagic = 18, StatusExceptPhysic = 39,
        StatusDisDefend = 47, StatusStopRecover = 48;

    // MAGIC_TYPE (NetCode.h) — the abilities the monster / summon getters fold their buffs on.
    public const byte Pap = 7, Pdp = 8, Lap = 9, Al = 11, Dl = 12, Cr = 13, Mdp = 16, Map = 17,
        Mcr = 21, Hpr = 32, Mpr = 33, Mal = 86, Mdl = 87;

    /// <summary>The signed delta the buffs add to <paramref name="value"/>.</summary>
    public static int Delta(IReadOnlyList<MaintainSkill> buffs, uint value, byte mtype)
    {
        int d = 0;
        foreach (var m in buffs)
            if (m.Template is { } t) d += t.CalcAbilityValue(m.Level, SaBuff, mtype, value);
        return d;
    }

    /// <summary>The value with its buffs, floored at 0 (C++ <c>dwValue = max(0, dwValue + nIncreaseValue)</c>).</summary>
    public static uint Apply(IReadOnlyList<MaintainSkill> buffs, uint value, byte mtype)
        => (uint)Math.Max(0, (long)value + Delta(buffs, value, mtype));

    /// <summary>C++ <c>HaveDisWeapon</c> / <c>HaveDisDefend</c> / the immunity tests: a maintained skill with that
    /// <c>SDT_STATUS</c> row.</summary>
    public static bool HasStatus(IReadOnlyList<MaintainSkill> buffs, byte status)
        => buffs.Any(m => m.Template?.Data.Any(r => r.Type == SdtStatus && r.Exec == status) == true);

    /// <summary>CTObjBase::CheckAttack: block, hold, hypnosis and AI fear prevent attacks;
    /// flash and darkness additionally stop non-player attackers.</summary>
    public static bool CanAttack(IReadOnlyList<MaintainSkill> buffs, bool player) =>
        !buffs.Any(m => m.Template?.Data.Any(r =>
            r.Type == SdtStatus && (r.Exec is 3 or 8 or 53 || !player && r.Exec is 12 or 45)
            || r.Type == 7 && r.Exec == 1) == true);
}
