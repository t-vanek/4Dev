namespace TMap.Data;

/// <summary>
/// A monster template row from <c>TMONSTERCHART</c> (C++ <c>CTBLMonster</c> → <c>tagTMONSTER</c>, keyed by
/// <c>m_wID</c>). This is the subset the spawn + client-visibility path needs: the chart id (the client's
/// model/name key), the level, and the attr-chart key (<c>m_wMonAttr</c>) used to look up the level-scaled
/// vitals, plus the exp/money/drop knobs and the AI-script selector (<c>m_bAIType</c>). The remaining
/// combat columns (class/race/roam-type/chase-range/skills…) are deferred — see PORT_STATUS.md.
/// </summary>
public sealed record MonsterTemplate(ushort Id, byte Level, ushort MonAttr,
    // Exp/money: the kill reward (m_wExp) and the money-drop knobs (m_bMoneyProb / m_dwMinMoney /
    // m_dwMaxMoney). (item loot): the per-attempt item chance (m_bItemProb) and attempt count
    // (m_bDropCount); the drop table itself is the linked <see cref="DropRows"/> (from TMONITEMCHART).
    uint Exp = 0, byte MoneyProb = 0, uint MinMoney = 0, uint MaxMoney = 0,
    byte ItemProb = 0, byte DropCount = 0,
    // The AI engine: m_bAIType — which TAICHART script drives this monster. The C++ resolves it
    // once at chart load (pMON->m_pAI = FindTMonsterAI(bAIType), TMapSvr.cpp:2734) and falls back to
    // DEFAULT_AI when the type has no script.
    byte AiType = 0,
    // The monster's attack skills: TMONSTERCHART wSkill1..wSkill4 (C++ CTBLMonster m_wSkill[4], DBAccess.h:473).
    // A monster's basic attack is itself one of these chart skills — there is no "skill 0" basic attack.
    ushort Skill1 = 0, ushort Skill2 = 0, ushort Skill3 = 0, ushort Skill4 = 0,
    // The chase leash (C++ m_wChaseRange): how far from its anchor a fighting monster may be pulled before it
    // gives up. Live values are mostly 50 or 90; 0 means it abandons the chase as soon as it is moved.
    ushort ChaseRange = 0,
    // Summons (C++ CTBLMonster, DBAccess.h:476-508): the recall kind a summon of this template becomes
    // (m_bRecallType — TRECALLTYPE_PET = 7 for mounts), the attr-chart id its stats come from at the owner's level
    // (m_wSummonAttr), the class/race/self/select flags CreateRecallMon copies.
    byte RecallType = 0, ushort SummonAttr = 0, byte Class = 0, byte Race = 0, byte IsSelf = 0, byte CanSelect = 0,
    byte CanAttack = 0,
    // m_bTame: the sorcerer's Enslave Monster can take it (old sources' SDT_TEMPT check, TObjBase.cpp).
    byte Tame = 0,
    // wKind groups monster variants under the same hunt objective; null is only a synthetic fixture fallback.
    ushort? HuntKind = null)
{
    /// <summary>The non-empty skill slots in chart order (wSkill1 first).</summary>
    public IEnumerable<ushort> Skills
    {
        get
        {
            if (Skill1 != 0) yield return Skill1;
            if (Skill2 != 0) yield return Skill2;
            if (Skill3 != 0) yield return Skill3;
            if (Skill4 != 0) yield return Skill4;
        }
    }

    /// <summary>The monster's item-drop rows (C++ <c>m_vMONITEM</c>, TMONITEMCHART) — loaded per monster.</summary>
    public List<MonItemRow> DropRows { get; } = new();

    /// <summary>C++ <c>m_dwMaxWeight</c> — Σ of the drop rows' weights; the roll's denominator. When 0 the
    /// monster drops nothing at all (money included — the C++ <c>AddItem</c> early-returns on it).</summary>
    public uint MaxWeight => (uint)DropRows.Sum(r => (int)r.Weight);
}

/// <summary>
/// One item-drop row from <c>TMONITEMCHART</c> (C++ <c>tagTMONITEM</c>, keyed by <c>wMonID</c>): a candidate
/// drop with its selection weight and the four "normal" probability gates that must all pass. A chart-type row
/// (<see cref="ChartType"/> ≠ 0) drops its item, or with no item a random one of that group within the id and drop-level
/// ranges (C++ <c>MonChoiceItem</c>); a type-0 row drops a prebuilt magic item (<c>TQUESTITEMCHART</c>, C++
/// <c>MonChoiceMagicItem</c>). <see cref="ProbMagic"/>/<see cref="ProbRare"/> and the option counts drive the magic options
/// (MapService.Drops.cs).
/// </summary>
public sealed record MonItemRow(ushort ItemId, ushort Weight, byte ChartType,
    byte Prob1, byte Prob2, byte Prob3, byte Prob4,
    ushort ItemIdMin = 0, ushort ItemIdMax = 0, byte LevelMin = 0, byte LevelMax = 0,
    byte ProbMagic = 0, byte ProbRare = 0, byte MagicOpt = 0, byte RareOpt = 0);

/// <summary>
/// A monster-attribute row from <c>TMONATTRCHART</c> (C++ <c>CTBLMonAttr</c>, keyed by
/// <c>MAKELONG(m_wID, m_bLevel)</c>). Holds the level-scaled vitals; only MaxHP/MaxMP are needed for the
/// client appearance packet (the AP/DP/speed/crit combat columns are deferred).
/// </summary>
public sealed record MonAttrRow(ushort Id, byte Level, uint MaxHp, uint MaxMp, uint DefendPower,
    // Monster attack: the physical attack-power band (C++ CTMonster::GetMinAP/GetMaxAP =
    // m_wAP + m_wMin/MaxWAP) and the attack-speed cadence (m_dwAtkSpeed).
    uint AtkMin = 0, uint AtkMax = 0, uint AtkSpeed = 0,
    // Combat quality: as the DEFENDER — magic defence (wMDP) + the defend levels (wDL/wMDL) that
    // feed the attacker's hit-rate roll; as the ATTACKER — its own attack level (wAL) + crit prob (bCriticalPP).
    uint MagicDefPower = 0, ushort DefendLevel = 0, ushort MagicDefLevel = 0, byte CritProb = 0, ushort AttackLevel = 0,
    // The raw parts a summon's getters combine (C++ CTRecallMon::GetMinAP… add the owner's item ability on top):
    // wAP / wLAP / wMAP, the weapon band wMinWAP / wMaxWAP, wMAL, bCriticalMP, and the defence parts wDP / wMDP / wWDP.
    ushort Ap = 0, ushort LongAp = 0, ushort MagicAp = 0, ushort MinWap = 0, ushort MaxWap = 0, ushort MagicAtkLevel = 0,
    byte CritMagicProb = 0, ushort RawDp = 0, ushort RawMdp = 0, ushort Wdp = 0);

/// <summary>
/// A spawn-point definition from <c>TMONSPAWNCHART</c> (C++ <c>CTBLMonSpawn</c> → <c>tagTMONSPAWN</c>). The
/// subset needed for a basic auto-spawn: the id (high word of each monster's instance id), the map/position/
/// facing anchor, the scatter radius (<c>Range</c>), the per-cycle regen probability (<c>Prob</c>), the
/// respawn <c>Delay</c>, the instance count, and the <c>Event</c> byte (<c>SE_DEFAULT = 0</c> ⇒ auto-spawn at
/// load). Group/link/area/roam/party/local-zone fields are deferred.
/// </summary>
public sealed record MonSpawnRow(
    ushort Id, ushort MapId, float PosX, float PosY, float PosZ, ushort Dir, byte Country,
    byte Count, byte Range, byte Prob, uint Region, uint Delay, byte Event,
    // The ROAM radius (C++ m_bArea). Not Range: Range is the spawn scatter, and is 0 for most spawns while Area
    // is 3-5 — using Range made most monsters roam to their own anchor point and turn on the spot.
    byte Area = 0,
    // C++ m_wLocalID: the territory (TBATTLEZONECHART id) the spawn belongs to — its war brings it out (0 = none).
    ushort LocalId = 0);

/// <summary>
/// One entry of a spawn's monster-type table from <c>TMAPMONCHART</c> (C++ <c>CTBLMapMonAll</c> →
/// <c>tagTMAPMON</c>): a candidate monster type with its selection weight (<c>Prob</c>) and the
/// <c>Essential</c> flag (essential monsters are resolved directly and excluded from the weighted pick).
/// </summary>
public sealed record MapMonRow(ushort SpawnId, ushort MonId, byte Leader, byte Essential, byte Prob);

/// <summary>A spawn point bundled with its candidate monster-type table — the unit the spawn engine works on.</summary>
public sealed record MonsterSpawnDef(MonSpawnRow Spawn, List<MapMonRow> Types);
