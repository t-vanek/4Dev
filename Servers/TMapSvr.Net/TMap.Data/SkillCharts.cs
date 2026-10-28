namespace TMap.Data;

/// <summary>
/// One <c>TSKILLDATA</c> row (C++ <c>tagTSKILLDATA</c>, TMapType.h:1969) — a skill's effect descriptor.
/// <see cref="Attr"/> classifies the attack (SATT_PHYSIC/LONG/MAGIC*), <see cref="Type"/> the effect kind
/// (SDT_ABILITY/CURE/STATUS…), <see cref="Exec"/> the sub-selector (MTYPE_DAMAGE…), and
/// <see cref="Inc"/>/<see cref="Value"/>/<see cref="ValueInc"/>/<see cref="Calc"/> the value formula.
/// </summary>
public sealed record SkillDataRow(byte Action, byte Type, byte Attr, byte Exec, byte Inc,
    ushort Value, ushort ValueInc, byte Calc);

/// <summary>
/// A skill-chart template row from <c>TSKILLCHART</c> (C++ <c>CTBLSkillChart</c> → <c>CTSkillTemp</c>,
/// keyed by <c>m_wID</c>). This is the subset the <c>CS_SKILLUSE</c> caster-side spine needs: the MP/HP
/// cost fields (<c>m_dwUseMP</c>/<c>m_bUseMPType</c>/<c>m_dwUseHP</c>/<c>m_bUseHPType</c>), the per-level
/// cost scale (<c>m_bStartLevel</c>/<c>m_bNextLevel</c> + the config-injected <c>m_f1stRateX</c>), the
/// reuse-cooldown fields (<c>m_dwReuseDelay</c>/<c>m_nReuseDelayInc</c>/<c>m_dwLoopDelay</c>/
/// <c>m_dwKindDelay</c>/<c>m_bKind</c>), the attack-speed selector (<c>m_bSpeedApply</c>, a TATTACK_DELAY),
/// the offensive flag (<c>m_bPositive</c>), and the bound map (<c>m_wMapID</c>). The rest of the 56-column
/// chart + the skill-data rows (<c>m_vData</c>, which drive damage scaling / cure / attack-type
/// classification) are deferred — see PORT_STATUS.md.
/// </summary>
/// <remarks><b>Value-exact note:</b> the DB column <c>bLevel</c> maps to <see cref="StartLevel"/> (not a
/// "level" field), and <see cref="Rate1stX"/> is <b>not</b> a chart column — the C++ stamps every template
/// with the global <c>f1stRateX = TFORMULACHART[FTYPE_1ST].fRateX</c> at load (TMapSvr.cpp:2596-2655),
/// which is exactly the value the stat engine caches as <c>TemplateStore.Rate1st</c>.</remarks>
/// <summary>One <c>TSKILLPOINTCHART</c> row (C++ <c>TSKILLPOINT</c>): what one level of a skill costs to learn.</summary>
public readonly record struct SkillPointRow(byte SkillPoint, byte GroupPoint, byte ParentLevel, uint Payback);

public sealed record SkillTemplate(
    ushort Id, byte Kind,
    uint UseMp, byte UseMpType, uint UseHp, byte UseHpType,
    byte StartLevel, byte MaxLevel, byte NextLevel,
    uint ReuseDelay, int ReuseDelayInc, uint LoopDelay, uint KindDelay,
    byte SpeedApply, byte Positive, ushort MapId,
    uint Duration = 0, uint DurationInc = 0, byte MaintainKind = 0, byte Priority = 0, byte StaticFlag = 0,
    uint ClassId = 0, float Rate1stX = 1f, uint Aggro = 0,
    // C++ m_bGlobal: a skill every character may use without learning it. Placing it on the hotkey bar grants it
    // (CTPlayer::AddHotKey, TPlayer.cpp:860) and clearing that slot takes it away again. 5 of 765 live skills.
    bool Global = false,
    // Riding (C++ m_bIsRide / m_bIsHideSkill / m_bIsDismount / m_bEraseAct): a buff that blocks calling or mounting a
    // pet (unless it is a hide skill), a hit that throws the target off its mount, and the actions that end a buff
    // (BUFFERASEACTION_TYPE — EraseBuffByRide tests m_bEraseAct & BEA_RIDE, BEA_RIDE = 3).
    bool IsRide = false, bool IsHideSkill = false, bool IsDismount = false, byte EraseAct = 0,
    // m_bTargetRange (TSKILLRANGE_TYPE): TSKILLRANGE_POINT = 1 places a summon on the ground point the client picked.
    byte TargetRange = 0,
    // Learning (CS_SKILLBUY): m_fPrice scales the level chart's dwMoney into the price; m_wParentSkillID is the skill
    // that must be known first (its level checked against TSKILLPOINTCHART.bPrevSkillLevel).
    float Price = 0f, ushort ParentSkillId = 0,
    // m_dwWeapon: the weapons a cast or self-buff needs (bit IK-1 of an equipped item's kind); 0 = none. m_wPosture: the
    // stance a buff belongs to — it falls off with that stance (CTPlayer::CheckEquipSkill).
    uint WeaponId = 0, ushort Posture = 0,
    // m_bORadius: an area buff; a tie against it keeps it (UpdateBuffSkill).
    byte ORadius = 0,
    // m_bEraseHide (BUFFERASEACTION_TYPE bits): which actions of this skill also end a hide buff (EraseBuffByAttack/Defend).
    byte EraseHide = 0,
    // m_bTargetHit: the most missiles a multi-attack skill puts on one target.
    byte TargetHit = 1,
    // m_bCanLearn: a skill of the class's tree (the tournament character gets them all at their top level).
    byte CanLearn = 0,
    // m_wUseItem (TSKILLCHART.wItemID): one required item per ordinary/loop cast or act-item use.
    ushort UseItem = 0,
    byte RepeatCount = 0, ushort TriggerId = 0, uint ActionTime = 0,
    ushort PrevActiveId = 0, ushort TargetActiveId = 0, byte DuraSlot = 0)
{
    /// <summary>SDT_ITEM / SDT_ITEM_DELUSER: this cast exhausts the selected equipment.</summary>
    public bool IsItemDelUser() => Data.Any(d => d.Type == 8 && d.Exec == 1);

    public bool IsItemDelTarget() => Data.Any(d => d.Type == 8 && d.Exec == 2);

    /// <summary>CTSkillTemp::IsDamageType: a nonzero-attribute ability row acting on HP/MP.</summary>
    public bool IsDamageType() => Data.Any(d => d.Type == 1 && d.Attr != 0 && d.Exec is 30 or 88 or 14 or 22);

    /// <summary>SDT_STATUS_MAGICMIRROR: reflect this incoming HP loss through the World route.</summary>
    public bool IsMirrorType() => Data.Any(d => d.Type == 6 && d.Exec == 17);

    /// <summary>MAGIC_TYPE MTYPE_EFC (NetCode.h): how many missiles a skill fires at once.</summary>
    public const byte MtypeEfc = 36;

    /// <summary>C++ <c>CTSkillTemp::IsMultiAttack</c> (TSkillTemp.cpp:359): an <c>SDT_ABILITY</c> / <c>MTYPE_EFC</c> row.</summary>
    public bool IsMultiAttack() => Data.Any(d => d.Type == 1 /* SDT_ABILITY */ && d.Exec == MtypeEfc);

    /// <summary>The per-level learning costs (C++ <c>m_mapTSkillPoint</c>, loaded from <c>TSKILLPOINTCHART</c>).</summary>
    public Dictionary<byte, SkillPointRow> Points { get; } = new();

    /// <summary>C++ <c>CTSkillTemp::GetNeedSkillPoint</c> (TSkillTemp.cpp:405) — skill points to reach <paramref name="level"/>.</summary>
    public byte GetNeedSkillPoint(byte level) => Points.TryGetValue(level, out var p) ? p.SkillPoint : (byte)0;

    /// <summary>C++ <c>CTSkillTemp::GetNeedKindPoint</c> (TSkillTemp.cpp:414) — points already spent in the skill's kind
    /// (its skill-window tab) needed to learn <paramref name="level"/>.</summary>
    public byte GetNeedKindPoint(byte level) => Points.TryGetValue(level, out var p) ? p.GroupPoint : (byte)0;

    /// <summary>C++ <c>CTSkillTemp::CheckParentSkill</c> (TSkillTemp.cpp:423) — the parent skill is high enough to learn
    /// <paramref name="learnLevel"/>.</summary>
    public bool CheckParentSkill(byte learnLevel, byte parentLevel)
        => Points.TryGetValue(learnLevel, out var p) && p.ParentLevel <= parentLevel;

    /// <summary>C++ <c>CTSkillTemp::GetPrice</c> (TSkillTemp.cpp:400) — <c>DWORD(dwMoney · m_fPrice)</c>.</summary>
    public uint GetPrice(uint money) => (uint)(money * Price);

    /// <summary>C++ <c>CTSkillTemp::GetAggro</c> (TSkillTemp.cpp:448) — the hate a hostile cast adds to a
    /// monster at <paramref name="level"/>: <c>m_dwAggro·pow(m_f1stRateX, exp)/100</c> (<c>exp = 0</c> at
    /// level 0, else <c>m_bStartLevel + (level-1)·m_bNextLevel</c>), truncated to DWORD <b>before</b> the
    /// integer /100. Zero when <see cref="Aggro"/> is 0. The caller floors it at 1 (<c>max(1, GetAggro())</c>,
    /// TMonster.cpp:944), so any hostile hit adds ≥1 aggro.
    /// <para><b>Deferred:</b> the <c>dwAggro</c> chart column isn't loaded (see PORT_STATUS.md), so live
    /// magnitude collapses to <c>max(1,0)=1</c> per hit until it is — the retarget <i>mechanics</i> are exact;
    /// tests set <see cref="Aggro"/> directly to exercise the magnitude formula.</para></summary>
    /// <summary>C++ <c>CTSkillTemp::GetValue</c> (TSkillTemp.cpp:62) — a data row's value at a skill level, by its
    /// <c>m_bCalc</c> mode (flat / +inc per level / rate power / −inc per level). A summon skill's row names the
    /// monster it summons this way.</summary>
    public int GetValue(SkillDataRow d, byte level) => d.Calc switch
    {
        0 => d.Value,
        1 => d.Value + (level - 1) * d.ValueInc,
        2 => (int)(d.Value * Math.Pow(Rate1stX, level == 0 ? 0 : StartLevel + (level - 1) * NextLevel) / 100),
        3 => d.Value - (level - 1) * d.ValueInc,
        _ => 0,
    };

    /// <summary>C++ <c>CTSkill::GetMaintainTick</c> (TSkill.cpp:147) — <c>duration + durationInc·(level-1)</c>.</summary>
    public uint MaintainTick(byte level) => Duration + DurationInc * (uint)(level - 1);

    public uint GetAggro(byte level)
    {
        if (Aggro == 0) return 0;
        double exp = level == 0 ? 0 : StartLevel + (level - 1) * NextLevel;
        return (uint)(Aggro * System.Math.Pow(Rate1stX, exp)) / 100u;
    }

    /// <summary>C++ class gate <c>m_dwClassID &amp; BITSHIFTID(m_bClass)</c> (QuestGiveSkill.cpp:43,
    /// OnCS_SKILLBUY_REQ CSHandler.cpp:2359) — the skill's allowed-class bitmask (<c>m_dwClassID</c>) has the
    /// bit for class index <paramref name="class"/> set (<c>BITSHIFTID(a) = 1u &lt;&lt; a</c>, TMapType.h:33).</summary>
    public bool IsClassMatch(byte @class) => (ClassId & (1u << @class)) != 0;

    /// <summary>C++ <c>CTSkillTemp::IsNegative</c> (TSkillTemp.cpp:31) — <c>!(m_bPositive % 2)</c>: an even
    /// <c>m_bPositive</c> is an offensive skill (the peace-zone / mode / CheckAttack gate).</summary>
    public bool IsNegative => (Positive % 2) == 0;

    /// <summary>C++ <c>CTSkillTemp::IsPositive</c> (TSkillTemp.cpp:26) — <c>m_bPositive == SPT_POSITIVE</c> (1).</summary>
    public bool IsPositive => Positive == SptPositive;

    /// <summary>C++ <c>m_bStatic</c> — a permanent buff that survives death/logout (never auto-released).</summary>
    public bool IsStatic => StaticFlag != 0;

    private const byte SptPositive = 1;              // SPT_* (NetCode.h:2263)
    public const byte SaBuff = 3;                    // SKILL_ACTION SA_BUFF (NetCode.h:1533)
    /// <summary>SKILL_DATA_TYPE SDT_CURE (NetCode.h:1540) — a cure/dispel data row.</summary>
    public const byte SdtCure = 5;

    /// <summary>The skill's effect rows (C++ <c>m_vData</c>), loaded from <c>TSKILLDATA</c> — drives the
    /// damage-scaling engine, attack-type classification, and (deferred) cure/status effects.</summary>
    public List<SkillDataRow> Data { get; } = new();

    // ---- SKILL_DATA_TYPE (bType), SKILL_DATA_INC (bInc), SKILL_DATA_ATTR (bAttr), SKILL_ATTACK_TYPE,
    // and the MTYPE_* / MAGIC_TYPE selectors this engine uses (NetCode.h / TMapType.h). ----
    public const byte SdtAbility = 1;                                        // SDT_ABILITY
    private const byte SviIncrease = 1, SviDecrease = 2, SviMultiply = 3, SviDivide = 4, SviPercent = 5;
    private const byte SattPhysic = 1, SattLong = 2, SattMagicNo = 3;        // SATT_* (magic attrs are 3..9)
    /// <summary>SKILL_ATTACK_TYPE (TMapType.h:356).</summary>
    public const byte SatNone = 0, SatPhysic = 1, SatLong = 2, SatMagic = 3;
    public const byte MtypeLap = 9, MtypeDamage = 30;                        // MAGIC_TYPE selectors

    /// <summary>C++ <c>CTSkillTemp::GetValue</c> (TSkillTemp.cpp:62) — the per-row value at
    /// <paramref name="level"/>, by <c>m_bCalc</c>: 0 flat, 1 level-linear, 2 <c>pow</c>-scaled (double
    /// division by 100), 3 level-linear-decrease (pure int).</summary>
    public int DataValue(SkillDataRow d, byte level) => d.Calc switch
    {
        0 => d.Value,
        1 => d.Value + (level - 1) * d.ValueInc,
        2 => (int)((double)d.Value * System.Math.Pow(Rate1stX, level == 0 ? 0 : StartLevel + (level - 1) * NextLevel) / 100.0),
        3 => (int)d.Value - ((int)level - 1) * (int)d.ValueInc,
        _ => 0,
    };

    /// <summary>C++ <c>CTSkillTemp::Calculate</c> (TSkillTemp.cpp:76) — the <c>m_bInc</c> operator applied to
    /// <paramref name="value"/>, returning the <b>delta</b> the caller adds (MULTIPLY/DIVIDE/PERCENT return
    /// <c>op(value) − value</c>). Unsigned DWORD arithmetic reproduced (wrap on a negative per-row value).</summary>
    public int Calculate(byte level, int index, uint value)
    {
        int calc = DataValue(Data[index], level);
        return Data[index].Inc switch
        {
            SviIncrease => calc,
            SviDecrease => -calc,
            SviMultiply => unchecked((int)(value * (uint)calc - value)),
            SviDivide => unchecked((int)(value / (uint)(calc <= 0 ? 1 : calc) - value)),
            SviPercent => unchecked((int)((int)((double)value * calc / 100.0) - value)),
            _ => 0,
        };
    }

    /// <summary>C++ <c>CTSkillTemp::CalcValue</c> (TSkillTemp.cpp:49) — Σ <see cref="Calculate"/> over every
    /// row matching (<paramref name="type"/>, <paramref name="exec"/>). This is the skill's own scaling of a
    /// base value (e.g. the AP−DP damage roll).</summary>
    public int CalcValue(byte level, byte type, byte exec, uint value)
    {
        int inc = 0;
        for (int i = 0; i < Data.Count; i++)
            if (Data[i].Type == type && Data[i].Exec == exec) inc += Calculate(level, i, value);
        return inc;
    }

    /// <summary>C++ <c>CTSkillTemp::GetAttackType</c> (TSkillTemp.cpp:172) — physical/long attr ⇒ SAT_PHYSIC
    /// (early), MAGICNO ⇒ SAT_MAGIC (early), other magic attrs set SAT_MAGIC but keep scanning.</summary>
    public byte GetAttackType()
    {
        byte type = SatNone;
        foreach (var d in Data)
        {
            if (d.Attr is SattPhysic or SattLong) return SatPhysic;
            if (d.Attr == SattMagicNo) return SatMagic;
            if (d.Attr > SattMagicNo && d.Attr <= 9) type = SatMagic; // SATT_MAGICSR..SATT_MAGICIR
        }
        return type;
    }

    /// <summary>C++ <c>CTSkillTemp::IsLongAttack</c> (TSkillTemp.cpp:196) — any ability row whose exec is
    /// <c>MTYPE_LAP</c> or whose attr is <c>SATT_LONG</c>.</summary>
    public bool IsLongAttack()
    {
        foreach (var d in Data)
            if ((d.Type == SdtAbility && d.Exec == MtypeLap) || d.Attr == SattLong) return true;
        return false;
    }

    /// <summary>The instance-skill damage scaling applied to a base roll (the only live term of C++
    /// <c>CalcAbilityValue(dwValue, MTYPE_DAMAGE)</c> — maintain/remain buffs + cure + effection are
    /// deferred): <c>max(0, roll + CalcValue(level, SDT_ABILITY, MTYPE_DAMAGE, roll))</c>.</summary>
    public uint ScaleDamage(byte level, uint roll)
        => (uint)System.Math.Max(0, (int)roll + CalcValue(level, SdtAbility, MtypeDamage, roll));

    /// <summary>C++ <c>CTSkillTemp::IsMaintainType</c> (TSkillTemp.cpp:226) — the skill has at least one
    /// <c>SA_BUFF</c> data row, i.e. casting it applies an active maintained buff/debuff.</summary>
    public bool IsMaintainType()
    {
        foreach (var d in Data)
            if (d.Action == SaBuff) return true;
        return false;
    }

    /// <summary>The skill carries at least one <c>SDT_CURE</c> data row (a cure/dispel skill — C++
    /// <c>PerformSkill</c> runs a cure effect per such row).</summary>
    public bool HasCure()
    {
        foreach (var d in Data)
            if (d.Type == SdtCure) return true;
        return false;
    }

    /// <summary>SKILL_DATA_TYPE SDT_RECALL / SDT_AI, SDT_AI_TYPE SDT_TEMPT, SKILL_EXEC_RECALL SER_MONSTER,
    /// SKILL_CURE_TYPE SCT_REVIVAL (NetCode.h).</summary>
    public const byte SdtRecall = 2, SdtAi = 7, SdtTempt = 4, SerMonster = 7, SctRevival = 1;

    /// <summary>C++ <c>CTSkillTemp::CanDefendAtDie</c> (TSkillTemp.cpp:505) — a revival or an enslave skill, the only
    /// ones that can land on a corpse.</summary>
    public bool CanDefendAtDie()
    {
        foreach (var d in Data)
            if ((d.Type == SdtCure && d.Exec == SctRevival) || (d.Type == SdtAi && d.Exec == SdtTempt)) return true;
        return false;
    }

    /// <summary>The skill carries an <c>SDT_AI</c>/<c>SDT_TEMPT</c> row — the sorcerer's Enslave Monster.</summary>
    public bool IsTempt()
    {
        foreach (var d in Data)
            if (d.Type == SdtAi && d.Exec == SdtTempt) return true;
        return false;
    }

    /// <summary>C++ <c>CTSkillTemp::IsMonRecall</c> (TSkillTemp.cpp:519) — an <c>SDT_RECALL</c>/<c>SER_MONSTER</c> row:
    /// the sorcerer's Evocate Monster, which summons the enslaved monster.</summary>
    public bool IsMonRecall()
    {
        foreach (var d in Data)
            if (d.Type == SdtRecall && d.Exec == SerMonster) return true;
        return false;
    }

    // ---- the item-skill tests (C++ TSkillTemp.cpp) ----
    /// <summary>SKILL_DATA_TYPE SDT_TRANS, SDT_TRANS_TYPE (RANDOM / the three country disguises), SDT_STATUS RANDOM /
    /// ITEMUPGRADE / AUTOEXP, SKILL_CURE_TYPE SCT_AFTERMATH (NetCode.h).</summary>
    public const byte SdtTrans = 3, SdtTransRandom = 3, SdtTransDisguiseD = 4, SdtTransDisguiseC = 5, SdtTransDisguiseB = 6,
        SdtStatusRandom = 61, SdtStatusItemUpgrade = 42, SdtStatusAutoExp = 67, SctAftermath = 13;

    private bool Has(byte type, byte exec) => Data.Any(d => d.Type == type && d.Exec == exec);

    /// <summary>C++ <c>IsRandomTrans</c> / <c>IsRandomBuff</c> — the skill stands for one of a run of skills, picked at
    /// random (<see cref="RandomPick"/>).</summary>
    public bool IsRandomTrans() => Has(SdtTrans, SdtTransRandom);
    public bool IsRandomBuff() => Has(SdtStatus, SdtStatusRandom);

    /// <summary>C++ <c>RandTransSkill</c> / <c>RandBuffSkill</c> (TMapSvr.cpp:8454): the random row's <c>wValue</c> is the
    /// first skill of the run, <c>wValueInc</c> how many; null when the row is empty.</summary>
    public (ushort First, ushort Count)? RandomPick()
    {
        foreach (var d in Data)
            if ((d.Type == SdtTrans && d.Exec == SdtTransRandom) || (d.Type == SdtStatus && d.Exec == SdtStatusRandom))
                return d.Value == 0 || d.ValueInc == 0 ? null : (d.Value, d.ValueInc);
        return null;
    }

    /// <summary>C++ <c>IsTrans</c> (a transformation), <c>IsBuffType</c> (an <c>SA_BUFF</c> row), <c>IsAfterMath</c>.</summary>
    public bool IsTrans() => Data.Any(d => d.Type == SdtTrans);
    public bool IsBuffType() => Data.Any(d => d.Action == SaBuff);
    public bool IsAfterMath() => Has(SdtCure, SctAftermath);
    public bool IsLuckyPotion() => Has(SdtStatus, SdtStatusItemUpgrade);
    public bool IsExpPotion() => Has(SdtStatus, SdtStatusAutoExp);

    /// <summary>C++ <c>IsDisguise</c> part of <c>HaveDisguiseBuff</c>: the country-disguise transformation it makes, or 0.</summary>
    public byte Disguise()
    {
        foreach (var d in Data)
            if (d.Type == SdtTrans && d.Exec is SdtTransDisguiseD or SdtTransDisguiseC or SdtTransDisguiseB) return d.Exec;
        return 0;
    }

    /// <summary>C++ <c>CheckCountry</c> (TSkillTemp.cpp:402): one cannot disguise as one's own country.</summary>
    public bool CheckCountry(byte country) => Disguise() switch
    {
        SdtTransDisguiseD => country != 0,                                     // TCONTRY_D
        SdtTransDisguiseC => country != 1,                                     // TCONTRY_C
        SdtTransDisguiseB => country != 2,                                     // TCONTRY_B
        _ => true,
    };

    /// <summary>SKILL_ACTION SA_CONTINUE / SA_PASSIVE, SKILL_DATA_TYPE SDT_EQUIP (NetCode.h:1530/1540).</summary>
    public const byte SaContinue = 1, SaPassive = 4, SdtEquip = 0;

    /// <summary>C++ <c>CTSkillTemp::IsRemainType</c> (TSkillTemp.cpp:134) — an <c>SA_CONTINUE</c> row: once learned, the
    /// skill sits in the owner's remain list (<c>m_vRemainSkill</c>) and its rows apply for good.</summary>
    public bool IsRemainType() => _isRemain ??= Data.Any(d => d.Action == SaContinue);   // read by every stat getter
    private bool? _isRemain;

    /// <summary>C++ <c>CTSkillTemp::CanEquip</c> (TSkillTemp.cpp:209) — a weapon mastery: an <c>SDT_EQUIP</c> row for
    /// the item kind <paramref name="kind"/>.</summary>
    public bool CanEquip(byte kind)
    {
        foreach (var d in Data)
            if (d.Type == SdtEquip && d.Exec == kind) return true;
        return false;
    }

    private const byte SdtStatusAtkMode = 7, SdtStatusCrazeMode = 26, SdtStatusDefendMode = 27, SerSkill = 12;

    /// <summary>C++ <c>CTSkillTemp::IsRecall</c> (TSkillTemp.cpp:531) — an <c>SDT_RECALL</c> row that summons something
    /// (anything but <c>SER_SKILL</c>).</summary>
    public bool IsRecall()
    {
        foreach (var d in Data)
            if (d.Type == SdtRecall && d.Exec != SerSkill) return true;
        return false;
    }

    /// <summary>C++ <c>CTSkillTemp::IsPosture</c> (TSkillTemp.cpp:157) — a stance: an attack, craze or defend mode
    /// status row.</summary>
    public bool IsPosture()
    {
        foreach (var d in Data)
            if (d.Type == SdtStatus && d.Exec is SdtStatusAtkMode or SdtStatusCrazeMode or SdtStatusDefendMode) return true;
        return false;
    }

    /// <summary>SKILL_DATA_TYPE SDT_STATUS (NetCode.h:1552) — a status-effect data row.</summary>
    public const byte SdtStatus = 6;
    public const byte SdtStatusTeleport = 16, SdtStatusWarp = 24, SdtStatusReturn = 31, SdtStatusReturnPos = 56;

    /// <summary>Instant movement effects executed when a skill lands (C++ PerformSkill SDT_STATUS).</summary>
    public bool HasMovementStatus() => Data.Any(d => d.Type == SdtStatus
        && d.Exec is SdtStatusTeleport or SdtStatusWarp or SdtStatusReturn or SdtStatusReturnPos);

    /// <summary>SDT_STATUS_TYPE HP↔MP execs (NetCode.h:1737) — swap HP↔MP, and sacrifice half HP into MP; the
    /// only vitals-mutating status effects the map server applies inline (the rest are movement / flags).</summary>
    public const byte SdtStatusHpMpChange = 50, SdtStatusHpToMp = 51;

    /// <summary>The skill carries at least one HP↔MP status row (<c>SDT_STATUS</c> +
    /// <c>SDT_STATUS_HPMPCHANGE</c>/<c>HPTOMP</c>) — the vitals-mutating status effects handled on the DEFEND path.</summary>
    public bool HasVitalsStatus()
    {
        foreach (var d in Data)
            if (d.Type == SdtStatus && (d.Exec == SdtStatusHpMpChange || d.Exec == SdtStatusHpToMp)) return true;
        return false;
    }

    /// <summary>C++ <c>CTSkill::GetMaintainTick</c> (TSkill.cpp:147) — the buff's total duration in ms at
    /// <paramref name="level"/>: <c>m_dwDuration + m_dwDurationInc·(level-1)</c>.</summary>
    public uint GetMaintainTick(byte level)
        => Duration + DurationInc * (uint)(level < 1 ? 0 : level - 1);

    /// <summary>C++ <c>CTSkill::CalcAbilityValue</c> (TSkill.cpp:151) — the per-skill buff delta for a target
    /// ability: Σ <see cref="Calculate"/> over the rows matching (<paramref name="action"/>, <c>SDT_ABILITY</c>,
    /// <c>m_bExec == exec</c>). <paramref name="action"/> is a <c>SA_*</c> (the maintain sum uses <c>SA_BUFF</c>).</summary>
    public int CalcAbilityValue(byte level, byte action, byte exec, uint value)
    {
        int inc = 0;
        for (int i = 0; i < Data.Count; i++)
            if (Data[i].Action == action && Data[i].Type == SdtAbility && Data[i].Exec == exec)
                inc += Calculate(level, i, value);
        return inc;
    }

    /// <summary>C++ <c>CTSkillTemp::HaveSkillData</c> (TSkillTemp.cpp:36) — this template has a data row matching
    /// another's (<paramref name="type"/>, <paramref name="attr"/>, non-zero <paramref name="exec"/>). Used by
    /// the buff-stack resolver to detect two buffs contending over the same effect.</summary>
    public bool HaveSkillData(byte type, byte attr, byte exec)
    {
        foreach (var d in Data)
            if (d.Type == type && d.Attr == attr && d.Exec != 0 && d.Exec == exec) return true;
        return false;
    }

    /// <summary>The first <c>SA_BUFF</c> <c>SDT_ABILITY</c> row this and <paramref name="other"/> both carry
    /// (the exec the stack resolver compares on), or <c>false</c> if none is shared.</summary>
    public bool SharedBuffAbility(SkillTemplate other, out byte exec)
    {
        foreach (var d in Data)
            if (d.Action == SaBuff && d.Type == SdtAbility && other.HaveSkillData(d.Type, d.Attr, d.Exec))
            {
                exec = d.Exec;
                return true;
            }
        exec = 0;
        return false;
    }
}
