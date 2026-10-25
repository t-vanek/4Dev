using System.Data;
using Microsoft.Data.SqlClient;
using TMap.Protocol;

namespace TMap.Data;

/// <summary>A character's persistent row from <c>TCHARTABLE</c> (the columns the map's <c>CTBLChar</c> load
/// reads). <c>Hp</c>/<c>Mp</c> are the persisted <b>current</b> HP/MP (<c>dwHP</c>/<c>dwMP</c>) — max HP/MP
/// are computed, not stored. The trailing block (<c>Exp</c>…<c>StatExp</c>) was added so the
/// char save (<c>TSaveChar</c>, which rewrites all 28 value columns) round-trips them instead of zeroing the
/// ones the map doesn't otherwise use.</summary>
public readonly record struct CharLoadRow(
    string Name, byte StartAct, byte Class, byte Race, byte Country, byte Sex, byte Hair, byte Face,
    byte Body, byte Pants, byte Hand, byte Foot, byte Level, uint Region, byte HelmetHide, uint Hp, uint Mp,
    uint Gold, uint Silver, uint Cooper,
    uint Exp, ushort SkillPoint, byte GuildLeave, uint GuildLeaveTime, ushort SpawnId, ushort LastSpawnId,
    uint LastDestination, ushort TemptedMon, byte Aftermath, byte StatLevel, byte StatPoint, uint StatExp,
    // Where the character was saved (C++ CTBLChar wMapID / fPosX / fPosY / fPosZ / wDIR).
    ushort MapId = 0, float PosX = 0, float PosY = 0, float PosZ = 0, ushort Dir = 0);

/// <summary>The full <c>TSaveChar</c> value set (C++ <c>CSPSaveChar</c>, param order preserved). A snapshot
/// built on the batch thread and handed to <see cref="GameDatabase.SaveCharAsync"/> for the off-thread write.
/// The two pc-bang columns are 0 (that subsystem is unported and not loaded). The PvP points ride along: the C++
/// <c>OnDM_SAVECHAR_REQ</c> saves them in the same pass (<c>TSavePvPRecord</c>).</summary>
public readonly record struct CharSaveData(
    uint CharId, byte StartAct, byte Level, byte HelmetHide, uint Gold, uint Silver, uint Cooper,
    byte GuildLeave, uint GuildLeaveTime, uint Exp, uint Hp, uint Mp, ushort SkillPoint, uint Region,
    ushort MapId, ushort SpawnId, ushort LastSpawnId, ushort TemptedMon, byte Aftermath,
    float PosX, float PosY, float PosZ, ushort Dir, uint PcBangTime, byte PcBangItemCnt,
    uint LastDestination, byte StatLevel, byte StatPoint, uint StatExp,
    uint PvpUseablePoint = 0, uint PvpTotalPoint = 0,
    // TSavePvPRecord's per-class record, 6 × (lose, win) in class order; null = all 0.
    uint[]? PvpRecord = null,
    // TSaveMonthPvPoint (only written when the month points are not 0, as in the C++).
    uint MonthPvPoint = 0, ushort MonthWin = 0, ushort MonthLose = 0, string MonthSay = "", byte Country = 0,
    // TClearCharTitle + TSaveCharTitle: every owned title and whether it is the one shown; null = leave the rows alone.
    IReadOnlyList<(ushort Id, bool Selected)>? Titles = null,
    // TRECALLMONTABLE / TRECALLMAINTAINTABLE: the live summons and their buffs; null = leave the rows alone.
    IReadOnlyList<RecallSaveRow>? Recalls = null, IReadOnlyList<RecallMaintainRow>? RecallMaintains = null,
    // TSaveSkillMaintain: the player's own buffs (TSaveChar clears them first); null = none.
    IReadOnlyList<MaintainLoadRow>? Maintains = null,
    // The running premium (account-wide, TGLOBAL TDURINGITEMTABLE) and exp item (TEXPITEMTABLE), written when SaveDuring.
    DuringItemRow? Premium = null, DuringItemRow? ExpItem = null, bool SaveDuring = false);

/// <summary>One saved summon (<c>TRECALLMONTABLE</c>, C++ <c>CTBLRecallMon</c>): its id when saved, chart, mount, attr
/// (<c>MAKELONG(attr, level)</c>), level, HP/MP, skill level, position, the life it had left (ms, 0 = for good) and
/// effect.</summary>
public sealed record RecallSaveRow(uint Id, ushort MonId, ushort PetId, uint Attr, byte Level, uint Hp, uint Mp, byte SkillLevel,
    short X, short Y, short Z, uint Time, byte Effect);

/// <summary>A running premium or exp-boost item (C++ <c>TDURINGITEM</c>): the item, <c>DURINGTYPE_TIME</c> 1 / <c>_DAY</c> 2, the
/// seconds left and when it ends (unix seconds).</summary>
public sealed record DuringItemRow(ushort ItemId, byte Type, uint Remain, long EndTime);

/// <summary>What the login reads about premium / exp items: the account's premium (<c>TVIEW_DURINGITEMTABLE</c>), the
/// character's exp items (<c>TEXPITEMTABLE</c>), and <c>TGetPcBangData</c>'s flags, bonus seconds used today and item count.</summary>
public sealed record DuringLoad(List<DuringItemRow> Premium, List<DuringItemRow> Exp, byte InPcBang, uint PcBangTime, byte PcBangItemCnt,
    byte LuckyNumber = 0);

/// <summary>One buff of a saved summon (<c>TRECALLMAINTAINTABLE</c>, C++ <c>CTBLRecallMaintain</c>); <c>Remain</c> 0 = for good.</summary>
public sealed record RecallMaintainRow(uint RecallId, ushort SkillId, byte Level, uint Remain, byte AttackType, uint AttackId,
    byte HostType, uint HostId, byte AttackCountry);

/// <summary>C++ <c>CSPGetPvPRecord</c> + <c>CSPGetMonthPvPoint</c> at char load: the PvP balances, the all-time rank, the
/// per-class record (6 × (lose, win)), and this month's points, wins, losses and rank.</summary>
public sealed record PvpLoadRow(uint Useable, uint Total, uint RankOrder, byte RankPercent, uint[] Record,
    uint MonthPoint, ushort MonthWin, ushort MonthLose, uint MonthRankOrder, byte MonthRankPercent);

/// <summary>One <c>TTITLECHART</c> row (C++ <c>TTITLETEMP</c>): a title, its window tab, its kind (TITLE_KIND) and what
/// it takes (points, gold, a quest id, a count…).</summary>
public sealed record TitleRow(ushort Id, ushort Category, byte Kind, uint Requirement);

/// <summary>One quest's persisted progress (C++ <c>TSaveQuest</c> + its <c>TSaveQuestTerm</c> rows).</summary>
public readonly record struct QuestSaveRow(
    uint QuestId, uint Tick, byte CompleteCount, byte TriggerCount, IReadOnlyList<QuestTermSaveRow> Terms);

/// <summary>One quest term's persisted count (C++ <c>TSaveQuestTerm</c>).</summary>
public readonly record struct QuestTermSaveRow(uint TermId, byte TermType, byte Count);

/// <summary>One item instance from <c>TITEMTABLE</c> (subset the map needs for the equipped-gear view).</summary>
public readonly record struct ItemLoadRow(
    byte ItemId, ushort TemplateId, byte Level, byte GradeEffect, ushort Color, byte RegGuild,
    ushort CustomTex, ushort MoggItemId);

/// <summary>An inventory container row from <c>TINVENTABLE</c> (CTBLInven).</summary>
public readonly record struct InvenLoadRow(byte InvenId, ushort TemplateId, long EndTime, byte Eld);

/// <summary>A full item instance from <c>TITEMTABLE</c> (CTBLItem; the 32 columns the map needs — the
/// C++ 35-column read additionally selects dlID/bOwnerType/dwOwnerID, which this explicit-column read omits).</summary>
public readonly record struct FullItemRow(
    byte StorageType, uint StorageId, byte ItemSlot, ushort TemplateId, byte Level, byte Count,
    byte GLevel, uint DuraMax, uint DuraCur, byte RefineCur, long EndTime, byte GradeEffect,
    byte[] Magic, ushort[] Value, uint[] Ext, byte Gem, ushort MoggItemId, long DlId);

/// <summary>One inventory container to persist (C++ <c>TSaveInven</c>). <c>EndTime</c> is __time64_t seconds
/// (converted to a datetime by the writer); <c>Eld</c> is the bag-enhancement level.</summary>
public readonly record struct InvenSaveData(byte InvenId, ushort TemplateId, long EndTime, byte Eld);

/// <summary>One item to persist (C++ <c>TSaveItem</c> — the full 35-value set). <c>DlId</c> is the item's
/// unique row id (preserved from load, or freshly generated for an in-session item); <c>StorageType</c> +
/// <c>StorageId</c> + <c>ItemSlot</c> locate it — a bag item is <c>STORAGE_INVEN</c>/invenId/slot; a cabinet
/// item is <c>STORAGE_CABINET</c>/<c>dwStItemID</c>/cabinetId.</summary>
public readonly record struct ItemSaveData(
    long DlId, byte StorageType, uint StorageId, byte ItemSlot, ushort TemplateId, byte Level, byte Count,
    byte GLevel, uint DuraMax, uint DuraCur, byte RefineCur, long EndTime, byte GradeEffect,
    byte[] Magic, ushort[] Value, uint[] Ext, byte Gem, ushort MoggItemId);

/// <summary>A skill to save (C++ <c>TSaveSkill</c>: <c>wSkill · bLevel · dwTick</c>).</summary>
public readonly record struct SkillSaveRow(ushort SkillId, byte Level, uint RemainTick);

/// <summary>A learned skill row from <c>TSKILLTABLE</c> (CTBLSkill).</summary>
public readonly record struct SkillLoadRow(ushort SkillId, byte Level, uint RemainTick);

/// <summary>A maintained/buff skill row from <c>TSKILLMAINTAINTABLE</c> (CTBLSkillMaintain — the 8 persisted fields).</summary>
public readonly record struct MaintainLoadRow(
    ushort SkillId, byte Level, uint RemainTick, byte AttackType, uint AttackId, byte HostType,
    uint HostId, byte AttackCountry);

/// <summary>A hotkey page from <c>THOTKEYTABLE</c> (CTBLHotKey): the page key + 12 (type,id) slots.</summary>
public readonly record struct HotkeyLoadRow(byte InvenKey, (byte Type, ushort Id)[] Slots);

/// <summary>One <c>TSaveHotkey</c> call: <see cref="Verb"/> 1 delete / 2 insert / 3 update (the proc's own codes).</summary>
public readonly record struct HotkeySaveRow(byte Verb, byte InvenKey, (byte Type, ushort Id)[] Slots);

/// <summary>A cabinet open-state header from <c>TCABINETTABLE</c> (C++ <c>CTBLCabinetTable</c>, DBAccess.h:2845):
/// which of the character's cabinets exist and whether each is opened (<c>bUse</c>). Its items are ordinary
/// <c>TITEMTABLE</c> rows with <c>bStorageType=STORAGE_CABINET</c> (loaded by <see cref="GameDatabase.LoadItemsAsync"/>).</summary>
public readonly record struct CabinetHeaderRow(byte CabinetId, bool Use);

/// <summary>A saved quest's header from <c>TQUESTTABLE</c> (CTBLQuestTable): the quest id, the remaining timer
/// tick, and the complete/trigger counts. The counterpart of the <c>TSaveQuest</c> write.</summary>
public readonly record struct QuestLoadRow(uint QuestId, uint Tick, byte CompleteCount, byte TriggerCount);

/// <summary>A saved quest term's running counter from <c>TQUESTTERMTABLE</c> (CTBLQuestTermTable).</summary>
public readonly record struct QuestTermLoadRow(uint QuestId, uint TermId, byte TermType, byte Count);

/// <summary>
/// Access to the game database (DSN <c>TGAME_GSP</c> → <c>TGame_gsp</c>). The C++ <c>TMapSvr</c> embeds the
/// DB-manager role and loads a character on its own <c>m_db</c> connection during the enter handshake
/// (<c>OnDM_LOADCHAR_REQ</c>, from <c>CTBLChar</c>/<c>CTBLItem</c>/…). Here we do the same directly.
///
/// This is a partial load: it covers the character's persistent appearance and equipped gear. The full
/// char blob (inventory containers, skills, quests, hotkeys, pets/recall-mons, cabinet, post, auction,
/// PvP/duel, medals, companions, etc.) and every save proc are deferred — see PORT_STATUS.md.
/// Every method degrades gracefully (throws a <see cref="SqlException"/> the caller catches) so the map
/// runs DB-free.
/// </summary>
public sealed partial class GameDatabase
{
    private readonly string _cs;
    private readonly string? _globalCs;
    public GameDatabase(string connectionString, string? globalConnectionString = null)
    { _cs = connectionString; _globalCs = globalConnectionString; }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    public async Task PingAsync(CancellationToken ct = default)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("SELECT 1", c);
        await cmd.ExecuteScalarAsync(ct);
    }

    // ---- Item + magic template charts (loaded once at startup) ----

    // Subset of the 50-column TITEMCHART (CTBLItemChart) the client wire needs: the refine-max cap and the
    // 4 revision columns → m_fRevision[0..3] (RV_PHYSIC/RV_MAGIC/RV_PHYSICPROB/RV_MAGICPROB).
    private const string ItemChartSql =
        @"SELECT wItemID, bRefineMax, fRevision, fMRevision, fAtRate, fMAtRate, bType, wAttrID, dwSpeedInc,
                 bLevel, dwClassID, dwSlotID, bPrmSlotID, bSubSlotID, bStack, bKind, wUseValue, dwDelay,
                 fPrice, bCanRepair, wDelayGroupID, bConsumable, bIsSell,
                 bGrade, bCanGrade, bCanMagic, bCanRare, bCanWrap, bCanColor, dwDuraMax, wUseTime, bUseType,
                 bItemCountry, fPvPrice, bEquipSkill, dwCode, bCanGamble, bGambleProb, bDestroyProb,
                 bGroupID, bDropLevel, bInitState, wWeight, wItemProb_G FROM TITEMCHART";
    // The per-level chart (CTBLLevelChart): the repair-cost coefficient (m_dwRepairCost), dwEXP (the
    // level-up threshold) + bSkillPoint (granted per level), and dwMoney (the base price the item
    // buy/sell math scales by m_fPrice).
    private const string LevelChartSql = @"SELECT bLevel, dwRepairCost, dwEXP, bSkillPoint, dwMoney, dwRefineCost, dwPvPMoney, wPvPoint, dwSearchCost, dwRegCost, dwGambleCost, dwRepCost FROM TLEVELCHART";
    // The kill-point chart (CTBLPvPointChart): the rows with no local are the open-field kill table (m_mapTPvPointKill).
    private const string PvPointChartSql = @"SELECT wLocalID, bStatus, bEvent, dwIncPoint, dwDecPoint FROM TPVPOINTCHART";
    private const string TitleChartSql = @"SELECT wTitleID, wCategory, bKind, dwRequirement FROM TTITLECHART";
    // The NPC registry + per-NPC shop stock (CTBLNpc → m_mapTNpc, CTBLNpcItemAll → m_mapItem).
    private const string NpcChartSql =
        @"SELECT wID, bType, bCountryID, wLocalID, bCondition, bDiscountRate, bAddProb, wItemID, wMapID,
                 fPosX, fPosY, fPosZ FROM TNPCCHART";
    private const string NpcItemChartSql = @"SELECT wNpcID, dwItemID FROM TNPCITEMCHART ORDER BY wNpcID";
    // Teleport data (CTBLSpawnPos / CTBLPortalChart / CTBLDestinationChartAll, DBAccess.h:2480/2987/3053).
    private const string SpawnPosChartSql = @"SELECT wID, wMapID, fPosX, fPosY, fPosZ, bType FROM TSPAWNPOSCHART";
    private const string PortalChartSql = @"SELECT wPortalID, bCountry, wLocalID, wSpawnID, bCondition FROM TPORTALCHART";
    private const string DestinationChartSql =
        @"SELECT wPortalID, wDestID, dwPrice, bEnable, bCondition1, dwConditionID1, bCondition2, dwConditionID2,
                 bCondition3, dwConditionID3 FROM TDESTINATIONCHART WITH (NOLOCK) ORDER BY wPortalID";
    // The map switch/gate charts (C++ CTBLSwitchChart DBAccess.h:3104 / CTBLGateChart :3092). Column
    // order is the value-exact contract. Gate columns are gateId, switchId, bType, mapId, pos (SELECT order).
    private const string SwitchChartSql =
        @"SELECT dwSwitchID, wMapID, wPosX, wPosY, wPosZ, bStart, bLockOnOpen, bLockOnClose, dwDuration FROM TSWITCHCHART";
    private const string GateChartSql =
        @"SELECT dwGateID, dwSwitchID, bType, wMapID, wPosX, wPosY, wPosZ FROM TGATECHART";
    // Monster spawn charts. The C++ CTBLMonSpawn SELECT filters by a TSVRCHART server/unit-id join
    // (multi-machine topology); this single-server port loads all rows and buckets them by (channel, map).
    private const string MonsterChartSql =
        @"SELECT wID, bLevel, wMonAttr, wExp, bMoneyProb, dwMinMoney, dwMaxMoney, bItemProb, bDropCount, bAIType, wSkill1, wSkill2, wSkill3, wSkill4, wChaseRange,
                 bRecallType, wSummonAttr, bClass, bRace, bIsSelf, bCanSelect, bCanAttack, bTame FROM TMONSTERCHART";
    private const string MountChartSql = @"SELECT wMountID, wDefMonID, wUpgMonID FROM TMOUNTCHART";
    private const string CompanionBonusChartSql = @"SELECT bBonusID, fBase, fLevelMultiplier FROM TCOMPANIONBONUSCHART";
    private const string CompanionRuneChartSql = @"SELECT wItemID, wMonID FROM TCOMPANIONRUNECHART";
    // The AI-script tables (C++ CTBLAICommand / CTBLAICondition / CTBLAIChart, DBAccess.h:365-414).
    // Loaded in that order — commands first (they are the targets the chart rows reference), then each
    // command's guards, then the trigger→command bindings per bAIType.
    //
    // The C++ runs one CTBLAICondition query PER command id (a WHERE dwCmdID = ? in a loop, TMapSvr.cpp:2320);
    // we bulk-load the whole table once and bucket it by dwCmdID — same result, one round trip.
    private const string AiCommandChartSql = @"SELECT dwCmdID, bCmdType FROM TAICMDCHART";
    private const string AiConditionChartSql = @"SELECT dwCmdID, dwConditionID, bConditionType FROM TAICONCHART";
    private const string AiChartSql =
        @"SELECT dwTriggerID, dwCmdID, dwDelay, bTriggerType, bAIType, bLoop FROM TAICHART";
    // The per-monster item drop table (CTBLMonItemAll → m_vMONITEM). Bulk-loaded, bucketed by wMonID
    // (like the skill-data rows), with the ranged-pick and magic-option columns (MapService.Drops.cs).
    private const string MonItemChartSql =
        @"SELECT wMonID, bChartType, wItemID, wWeight, bItemProb_N1, bItemProb_N2, bItemProb_N3, bItemProb_N4,
                 wItemIDMin, wItemIDMax, bLevelMin, bLevelMax, bItemProb_M, bItemProb_R, bItemMagicOpt, bItemRareOpt
                 FROM TMONITEMCHART ORDER BY wMonID";
    private const string MonAttrChartSql =
        @"SELECT wID, bLevel, dwMaxHP, dwMaxMP, wDP, wAP, wMinWAP, wMaxWAP, dwAtkSpeed, wMDP, wDL, wMDL, bCriticalPP, wAL, wWDP,
                 wLAP, wMAP, wMAL, bCriticalMP FROM TMONATTRCHART";
    private const string MonSpawnChartSql =
        @"SELECT wID, wMapID, fPosX, fPosY, fPosZ, wDir, bCountry, bCount, bRange, bProb, dwRegion, dwDelay, bEvent, bArea, wLocalID FROM TMONSPAWNCHART";
    private const string MapMonChartSql = @"SELECT wSpawnID, wMonID, bLeader, bEssential, bProb FROM TMAPMONCHART ORDER BY wSpawnID";
    private const string MagicChartSql =
        @"SELECT bMagic, bRvType, wMaxValue, dwKind, bIsMagic, bIsRare, bExclIndex, bOptionKind, wRareBound, bRefine, bMinLevel, wMaxBound FROM TITEMMAGICCHART";
    // Stat / HP-MP charts (FORMULA_TYPE FTYPE_1ST = 34 is the per-level stat-growth factor).
    private const string FormulaChartSql = @"SELECT bID, dwInit, fRateX, fRateY FROM TFORMULACHART";
    private const string ClassChartSql = @"SELECT bClassID, wSTR, wDEX, wCON, wINT, wWIS, wMEN FROM TCLASSCHART";
    private const string RaceChartSql = @"SELECT bRaceID, wSTR, wDEX, wCON, wINT, wWIS, wMEN FROM TRACECHART";
    // Item-attribute (AP/DP) charts.
    private const string ItemAttrChartSql =
        @"SELECT wID, bKind, bGrade, wMinAP, wMaxAP, wDP, wMinMAP, wMaxMAP, wMDP, bBlockProb FROM TITEMATTRCHART";
    private const string ItemGradeChartSql = @"SELECT bLevel, bGrade, bProb, dwMoney FROM TITEMGRADECHART";
    private const string GemGradeChartSql = @"SELECT bGem, bProb FROM TGEMGRADECHART";
    private const string AccessoryMagicSql = @"SELECT ID, MagicID, MinValue, MaxValue FROM ACCESSORYMAGICTABLE";
    private const string CashGambleChartSql = @"SELECT dwID, dwProb, wGroup, wUseTime, wItemID, bLevel, bCount, bGLevel,
                 dwDuraMax, dwDuraCur, bRefineCur, bGradeEffect,
                 bMagic1, bMagic2, bMagic3, bMagic4, bMagic5, bMagic6, wValue1, wValue2, wValue3, wValue4, wValue5, wValue6,
                 dwTime1, dwTime2, dwTime3, dwTime4, dwTime5, dwTime6, bGem, wMoggItemID FROM TVIEW_CASHGAMBLECHART";
    // C++ CTBLBattleZoneChart (DBAccess.h:797) and the territory tables (CTBLMissionTable / CTBLCastleTable /
    // CTBLSkygardenTable / CTBLLocalTable / CTBLLocalOccupy). The guild name comes from TGUILDTABLE (NULL when gone).
    private const string BattleZoneChartSql = @"SELECT wID, szName, wMapID, wCastle, wBossSpawnID, wLGateKeeperSpawnID,
                 wRGateKeeperSpawnID, wCGateKeeperSpawnID, dwLSwitchID, dwRSwitchID, dwCSwitchID, wNormalItem, wChiefItem, bLine,
                 wSkill1, wSkill2, bItemLevel, wValorianBossDefend, wValorianBossAttack, wDerionBossDefend, wDerionBossAttack,
                 wMiddleSpawnID, wRightSpawnID, wLeftSpawnID FROM TBATTLEZONECHART";
    private const string TerritorySql = @"
SELECT 3, wMissionID, bCountry, 0, NULL, NULL, NULL, '', NULL FROM TMISSIONTABLE;
SELECT 2, c.wCastle, c.bCountry, c.dwGuildID, g.szName, NULL, c.dateWarTime, c.szHero, c.dateHero
  FROM TCASTLETABLE c LEFT JOIN TGUILDTABLE g ON g.dwID = c.dwGuildID;
SELECT 4, wID, bCountry, 0, NULL, NULL, dateWarTime, '', NULL FROM TSKYGARDENTABLE;
SELECT 1, l.wLocalID, l.bCountry, l.dwGuild, g.szName, l.dateOccupy, l.dateDefend, l.szHero, l.dateHero
  FROM TLOCALTABLE l LEFT JOIN TGUILDTABLE g ON g.dwID = l.dwGuild;
SELECT wLocalID, bDay, dwGuildID, bType FROM TLOCALOCCUPYTABLE";
    // C++ CTBLGodBall / CTBLGodTower (the castles' god ball spots and towers).
    private const string GodSql = @"SELECT wID, bCamp, wMapID, fPosX, fPosY, fPosZ FROM TGODBALLCHART ORDER BY wID;
SELECT wID, wMapID, fPosX, fPosY, fPosZ FROM TGODTOWERCHART ORDER BY wID";
    // C++ CTBLSpecialBoxChart (DBAccess.h:4754): the box group is wGroup (the C++ keys its map on it).
    private const string SpecialBoxChartSql = @"SELECT wGroup, wUseTime, bClass, wItemID, bLevel, bCount, bGLevel,
                 dwDuraMax, dwDuraCur, bRefineCur, bGradeEffect,
                 bMagic1, bMagic2, bMagic3, bMagic4, bMagic5, bMagic6, wValue1, wValue2, wValue3, wValue4, wValue5, wValue6,
                 dwTime1, dwTime2, dwTime3, dwTime4, dwTime5, dwTime6, bGem, wMoggItemID FROM TSPECIALBOXCHART";
    // The skill chart (CTBLSkillChart → CTSkillTemp). Of the 56-column C++ SELECT we read the
    // subset the CS_SKILLUSE caster spine uses. Note bLevel → m_bStartLevel (the C++ remaps it, TMapSvr.cpp:2630).
    private const string SkillChartSql =
        @"SELECT wID, bKind, dwUseMP, bUseMPType, dwUseHP, bUseHPType, bLevel, bMaxLevel, bNextLevel,
                 dwReuseDelay, nReuseDelayInc, dwLoopDelay, dwKindDelay, bSpeedApply, bPositive, wMapID,
                 dwDuration, dwDurationInc, bMaintainType, bPriority, bStatic, dwClassID, bGlobal,
                 bIsRide, bIsHideSkill, bIsDismount, bEraseAct, bTargetRange, fPrice, wParentSkillID,
                 dwWeaponID, wPosture, bORadius, bEraseHide, bTargetHit, bCanLearn, wItemID FROM TSKILLCHART";
    // The learning costs per skill level (CTBLSkillPoint → CTSkillTemp::m_mapTSkillPoint).
    private const string SkillPointChartSql =
        @"SELECT wID, bLevel, bSkillPoint, bGroupPoint, bPrevSkillLevel, dwPayback FROM TSKILLPOINTCHART";
    // The skill-effect rows (CTBLSkillData → CTSkillTemp::m_vData). C++ runs one query per skill;
    // this bulk load buckets by wSkillID (natural table order per skill matches the per-skill fetch order).
    private const string SkillDataChartSql =
        @"SELECT wSkillID, bAction, bType, bAttr, bExec, bInc, wValue, wValueInc, bCalc FROM TSKILLDATA";
    private const byte FType1st = 34;

    // The quest template graph (C++ CTMapSvrModule::LoadQuestTemp, the 4-table bulk load
    // DBAccess.h:2288-2408). The ORDER BYs are load-bearing: TQUESTCHART's `bMain DESC` fixes the order children
    // of a parent register in the trigger index (so ExecChildren attempts the main branch first); conditions are
    // `bConditionType DESC`; terms are `dwID`. The rows are assembled by dwQuestID into QuestTemplate.
    private const string QuestChartSql =
        @"SELECT dwParentID, dwQuestID, dwTriggerID, bTriggerType, bForceRun, bCountMax, bType, bConditionCheck
                 FROM TQUESTCHART ORDER BY dwParentID, bMain DESC";
    private const string QuestCondChartSql =
        @"SELECT dwQuestID, dwConditionID, bConditionType, bCount FROM TQCONDITIONCHART ORDER BY dwQuestID, bConditionType DESC";
    private const string QuestRewardChartSql =
        @"SELECT dwQuestID, dwRewardID, bRewardType, bTakeMethod, bTakeData, bCount FROM TQREWARDCHART ORDER BY dwQuestID";
    private const string QuestTermChartSql =
        @"SELECT dwQuestID, dwTermID, bTermType, bCount FROM TQUESTTERMCHART ORDER BY dwQuestID, dwID";

    /// <summary>
    /// Loads the item + magic template charts (C++ <c>CTBLItemChart</c> → <c>m_mapTITEM</c>,
    /// <c>CTBLItemMagicChart</c> → <c>m_mapTItemMagic</c>) once at startup. Drives the client-facing
    /// derived item values: template <c>RefineMax</c> and the computed <c>GetMagicValue</c>.
    /// </summary>
    public async Task<TemplateStore> LoadTemplatesAsync(CancellationToken ct = default)
    {
        var store = new TemplateStore();
        await using var c = await OpenAsync(ct);

        await using (var cmd = new SqlCommand(ItemChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.Items[id] = new ItemTemplate(id, r.GetByteSafe(1),
                    new[] { r.GetFloatSafe(2), r.GetFloatSafe(3), r.GetFloatSafe(4), r.GetFloatSafe(5) },
                    Type: r.GetByteSafe(6), AttrId: r.GetUShortSafe(7), SpeedInc: r.GetUIntSafe(8),
                    DefaultLevel: r.GetByteSafe(9), ClassId: r.GetUIntSafe(10), SlotId: r.GetUIntSafe(11),
                    PrmSlot: r.GetByteSafe(12), SubSlot: r.GetByteSafe(13), Stack: r.GetByteSafe(14),
                    Kind: r.GetByteSafe(15), UseValue: r.GetUShortSafe(16), Delay: r.GetUIntSafe(17),
                    Price: r.GetFloatSafe(18), CanRepair: r.GetByteSafe(19),
                    DelayGroup: r.GetUShortSafe(20), Consumable: r.GetByteSafe(21), IsSell: r.GetByteSafe(22),
                    Grade: r.GetByteSafe(23), CanGrade: r.GetByteSafe(24), CanMagic: r.GetByteSafe(25),
                    CanRare: r.GetByteSafe(26), CanWrap: r.GetByteSafe(27), CanColor: r.GetByteSafe(28),
                    DuraMax: r.GetUIntSafe(29), UseTime: r.GetUShortSafe(30), UseType: r.GetByteSafe(31),
                    ItemCountry: r.GetByteSafe(32), PvPrice: r.GetFloatSafe(33), EquipSkill: r.GetByteSafe(34), Code: r.GetUIntSafe(35),
                    CanGamble: r.GetByteSafe(36), GambleProb: r.GetByteSafe(37), DestroyProb: r.GetByteSafe(38),
                    GroupId: r.GetByteSafe(39), DropLevel: r.GetByteSafe(40), InitState: r.GetByteSafe(41), Weight: r.GetUShortSafe(42),
                    ItemProbG: r.GetUShortSafe(43));
                var t = store.Items[id];
                if (t.DropLevel != 0)                                                        // C++ m_mapTMONITEM (TMapSvr.cpp:1983)
                {
                    if (!store.DropItems.TryGetValue(t.GroupId, out var byLevel)) store.DropItems[t.GroupId] = byLevel = new();
                    if (!byLevel.TryGetValue(t.DropLevel, out var list)) byLevel[t.DropLevel] = list = new();
                    list.Add(t);
                }
            }

        await using (var cmd = new SqlCommand(MagicChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                byte id = r.GetByteSafe(0);
                var m = new MagicTemplate(id, r.GetByteSafe(1), r.GetUShortSafe(2), Kind: r.GetUIntSafe(3),
                    IsMagic: r.GetByteSafe(4), IsRare: r.GetByteSafe(5), ExclIndex: r.GetByteSafe(6),
                    OptionKind: r.GetByteSafe(7), RareBound: r.GetUShortSafe(8), Refine: r.GetByteSafe(9),
                    MinLevel: r.GetByteSafe(10), MaxBound: r.GetUShortSafe(11));
                store.Magics[id] = m;
                // C++ m_vItemMagic[i] for i in 1..IK_HP-1 (IK_HP = 26): one list per item kind the option fits.
                for (byte k = 1; k < 26; k++)
                    if ((m.Kind & (1u << (k - 1))) != 0)
                    {
                        if (!store.MagicsByKind.TryGetValue(k, out var list)) store.MagicsByKind[k] = list = new();
                        list.Add(m);
                    }
            }

        await using (var cmd = new SqlCommand(FormulaChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.Formulas[r.GetByteSafe(0)] = new FormulaRow(r.GetUIntSafe(1), r.GetFloatSafe(2), r.GetFloatSafe(3));
        store.Rate1st = store.Formulas.TryGetValue(FType1st, out var f1) ? f1.RateX : 1f;

        await using (var cmd = new SqlCommand(ClassChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.Classes[r.GetByteSafe(0)] = new StatSeed(r.GetUShortSafe(1), r.GetUShortSafe(2),
                    r.GetUShortSafe(3), r.GetUShortSafe(4), r.GetUShortSafe(5), r.GetUShortSafe(6));

        await using (var cmd = new SqlCommand(RaceChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.Races[r.GetByteSafe(0)] = new StatSeed(r.GetUShortSafe(1), r.GetUShortSafe(2),
                    r.GetUShortSafe(3), r.GetUShortSafe(4), r.GetUShortSafe(5), r.GetUShortSafe(6));

        await using (var cmd = new SqlCommand(ItemAttrChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.ItemAttrs[id] = new ItemAttr(id, r.GetByteSafe(1), r.GetByteSafe(2),
                    r.GetUShortSafe(3), r.GetUShortSafe(4), r.GetUShortSafe(5),
                    r.GetUShortSafe(6), r.GetUShortSafe(7), r.GetUShortSafe(8), r.GetByteSafe(9));
            }
        // SetItemAttr's fallback is std::map::begin() = the lowest-wID row.
        store.DefaultAttr = store.ItemAttrs.Count == 0 ? null
            : store.ItemAttrs[store.ItemAttrs.Keys.Min()];

        await using (var cmd = new SqlCommand(ItemGradeChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                byte level = r.GetByteSafe(0);
                if (level < store.ItemGrades.Length)
                {
                    store.ItemGrades[level] = r.GetByteSafe(1);
                    store.ItemGradeProb[level] = r.GetByteSafe(2);
                    store.ItemGradeMoney[level] = r.GetUIntSafe(3);
                }
            }

        await using (var cmd = new SqlCommand(GemGradeChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                byte gem = r.GetByteSafe(0);
                if (gem < store.GemProbs.Length) store.GemProbs[gem] = r.GetByteSafe(1);
            }

        await using (var cmd = new SqlCommand(AccessoryMagicSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.AccessoryMagic.Add(new AccessoryMagicRow(r.GetUIntSafe(0), r.GetByteSafe(1), r.GetUShortSafe(2), r.GetUShortSafe(3)));

        await using (var cmd = new SqlCommand("SELECT bType, bKind, wReplaceID, bCountMax, bMinLevel, bMaxLevel, wProb FROM TGAMBLECHART", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetByteSafe(0), r.GetByteSafe(1));
                if (!store.Gambles.TryGetValue(key, out var list)) store.Gambles[key] = list = new();
                list.Add(new GambleRow(r.GetUShortSafe(2), r.GetByteSafe(3), r.GetByteSafe(4), r.GetByteSafe(5), r.GetUShortSafe(6)));
            }

        // C++ CTBLInvenTournametChart / CTBLItemTournamentChart (DBAccess.h:1476, 1496): the tournament character's bags and items.
        await using (var cmd = new SqlCommand("SELECT bInvenID, wItemID, bELD FROM TINVENTOURNAMENTCHART", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.TnmtInvens.Add((r.GetByteSafe(0), r.GetUShortSafe(1), r.GetByteSafe(2)));
        await using (var cmd = new SqlCommand(@"SELECT bClass, bInvenID, bItemID, wItemID, bLevel, bCount, bGLevel, dwDuraMax, bGradeEffect,
                 bMagic1, bMagic2, bMagic3, bMagic4, bMagic5, bMagic6, wValue1, wValue2, wValue3, wValue4, wValue5, wValue6,
                 dwTime1, dwTime2, dwTime3, dwTime4, dwTime5, dwTime6, bGem, wMoggItemID FROM TITEMTOURNAMENTCHART", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var magic = new byte[6]; var value = new ushort[6]; var ext = new uint[6];
                for (int i = 0; i < 6; i++) magic[i] = r.GetByteSafe(9 + i);
                for (int i = 0; i < 6; i++) value[i] = r.GetUShortSafe(15 + i);
                for (int i = 0; i < 6; i++) ext[i] = r.GetUIntSafe(21 + i);
                uint dura = r.GetUIntSafe(7);
                var item = new FullItemRow(0, 0, r.GetByteSafe(2), r.GetUShortSafe(3), r.GetByteSafe(4), r.GetByteSafe(5), r.GetByteSafe(6), dura,
                    dura, 0, 0, r.GetByteSafe(8), magic, value, ext, r.GetByteSafe(27), r.GetUShortSafe(28), 0);
                byte cls = r.GetByteSafe(0);
                if (!store.TnmtItems.TryGetValue(cls, out var list)) store.TnmtItems[cls] = list = new();
                list.Add((r.GetByteSafe(1), item));
            }
        // C++ CSPGetLimitedLevel (TGetLimitedLevel): the server's level cap, the tournament character's level.
        try
        {
            var max = SqlProc.Out("@m", SqlDbType.TinyInt);
            await SqlProc.ExecAsync(c, "TGetLimitedLevel", null, new[] { max }, ct);
            store.LimitedLevel = max.AsByte();
        }
        catch (SqlException) { }

        // C++ CTBLCashCategory + CTBLCashShopItem (DBAccess.h:3345, 3366): the cash shop's tabs and items, in their order. An item
        // whose wItemID is not in the item chart is skipped (C++ CreateItem). The original price is dwOriMoney (the C++ took the
        // view's dwMoney, already on sale, so a sale event cut a cut price again).
        try
        {
            await using (var cmd = new SqlCommand("SELECT bID, szName FROM TVIEW_CASHCATEGORYCHART ORDER BY wOrder", c))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) store.CashCategories.Add(new CashCategory(r.GetByteSafe(0), r.GetStringSafe(1), new()));
            await using (var cmd = new SqlCommand(@"SELECT wID, wInfoID, dwMoney, bCategory, bKind, wItemID, bLevel, bCount, bGLevel, dwDuraMax,
                     dwDuraCur, bRefineCur, wUseTime, bGradeEffect, bMagic1, bMagic2, bMagic3, bMagic4, bMagic5, bMagic6, wValue1, wValue2,
                     wValue3, wValue4, wValue5, wValue6, dwTime1, dwTime2, dwTime3, dwTime4, dwTime5, dwTime6, bCanSell, bSaleValue,
                     dwOriMoney FROM TVIEW_CASHSHOPITEMCHART ORDER BY bCategory, wOrder", c))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    ushort itemId = r.GetUShortSafe(5);
                    if (itemId != 0 && !store.Items.ContainsKey(itemId)) continue;
                    var magic = new byte[6]; var value = new ushort[6]; var ext = new uint[6];
                    for (int i = 0; i < 6; i++) magic[i] = r.GetByteSafe(14 + i);
                    for (int i = 0; i < 6; i++) value[i] = r.GetUShortSafe(20 + i);
                    for (int i = 0; i < 6; i++) ext[i] = r.GetUIntSafe(26 + i);
                    var item = new CashItem
                    {
                        Id = r.GetUShortSafe(0), InfoId = r.GetUShortSafe(1), Money = r.GetUIntSafe(2), Category = r.GetByteSafe(3),
                        Kind = r.GetByteSafe(4), UseTime = r.GetUShortSafe(12), CanSell = r.GetByteSafe(32), SaleValue = r.GetByteSafe(33),
                        OriMoney = r.GetUIntSafe(34),
                        Item = new FullItemRow(0, 0, 0, itemId, r.GetByteSafe(6), r.GetByteSafe(7), r.GetByteSafe(8), r.GetUIntSafe(9),
                            r.GetUIntSafe(10), r.GetByteSafe(11), 0, r.GetByteSafe(13), magic, value, ext, 0, 0, 0),
                    };
                    store.CashItems[item.Id] = item;
                    store.CashCategories.FirstOrDefault(k => k.Id == item.Category)?.Items.Add(item);
                }
        }
        catch (SqlException) { }

        // C++ CTBLHelpMessage (DBAccess.h:4506): the help messages, those ended already left out (TMapSvr.cpp:1759).
        try
        {
            await using var cmd = new SqlCommand("SELECT bID, dStart, dEnd, szMessage FROM THELPMESSAGETABLE", c);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            while (await r.ReadAsync(ct))
            {
                long end = ToTime64(r, 2);
                if (end == 0 || end > now) store.HelpMessages.Add((r.GetByteSafe(0), ToTime64(r, 1), end, r.GetStringSafe(3)));
            }
        }
        catch (SqlException) { }

        // C++ CTBLOperatorChart (DBAccess.h:3137): the operators.
        try
        {
            await using var cmd = new SqlCommand("SELECT dwOperatorID FROM TOPERATORTABLE", c);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) store.Operators.Add(r.GetUIntSafe(0));
        }
        catch (SqlException) { }

        // C++ CTBLArena (DBAccess.h:4588)
        await using (var cmd = new SqlCommand("SELECT wID, bType, wInPos, wOutPos, dwFee FROM TARENACHART", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.Arenas.Add(new ArenaRow(r.GetUShortSafe(0), r.GetByteSafe(1), r.GetUIntSafe(4), r.GetUShortSafe(2), r.GetUShortSafe(3)));

        // C++ CTBLRPSGame (DBAccess.h:4545)
        await using (var cmd = new SqlCommand(@"SELECT bType, bWinCount, wItemID, dwRewardMoney, wRewardItem_1, wRewardItem_2, bItemCount_1,
                 bItemCount_2, bProb_Win, bProb_Draw, bProb_Lose, wWinKeep, wWinPeriod FROM TRPSGAMECHART", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var rps = new RpsGame
                {
                    Type = r.GetByteSafe(0), WinCount = r.GetByteSafe(1), ItemId = r.GetUShortSafe(2), RewardMoney = r.GetUIntSafe(3),
                    RewardItem = { [0] = r.GetUShortSafe(4), [1] = r.GetUShortSafe(5) }, ItemCount = { [0] = r.GetByteSafe(6), [1] = r.GetByteSafe(7) },
                    Prob = { [0] = r.GetByteSafe(8), [1] = r.GetByteSafe(9), [2] = r.GetByteSafe(10) },
                    WinKeep = r.GetUShortSafe(11), WinPeriod = r.GetUShortSafe(12),
                };
                store.RpsGames[(ushort)(rps.Type | rps.WinCount << 8)] = rps;
            }

        // C++ CTBLQuestMagicItemChart (DBAccess.h:2180)
        await using (var cmd = new SqlCommand(@"SELECT dwID, wItemID, bLevel, bGLevel, dwDuraMax, dwDuraCur, bRefineCur, wUseTime, bGradeEffect,
                 bMagic1, bMagic2, bMagic3, bMagic4, bMagic5, bMagic6, wValue1, wValue2, wValue3, wValue4, wValue5, wValue6,
                 dwTime1, dwTime2, dwTime3, dwTime4, dwTime5, dwTime6, dwMoney, bGem, bDropLevel FROM TQUESTITEMCHART", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var magic = new byte[6]; var value = new ushort[6]; var ext = new uint[6];
                for (int i = 0; i < 6; i++) magic[i] = r.GetByteSafe(9 + i);
                for (int i = 0; i < 6; i++) value[i] = r.GetUShortSafe(15 + i);
                for (int i = 0; i < 6; i++) ext[i] = r.GetUIntSafe(21 + i);
                var item = new FullItemRow(0, 0, 0, r.GetUShortSafe(1), r.GetByteSafe(2), 1, r.GetByteSafe(3), r.GetUIntSafe(4),
                    r.GetUIntSafe(5), r.GetByteSafe(6), 0, r.GetByteSafe(8), magic, value, ext, r.GetByteSafe(28), 0, 0);
                uint id = r.GetUIntSafe(0);
                store.QuestItems[id] = new QuestItemRow(id, item, r.GetUShortSafe(7), r.GetUIntSafe(27), r.GetByteSafe(29));
            }

        await using (var cmd = new SqlCommand(CashGambleChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var magic = new byte[6]; var value = new ushort[6]; var ext = new uint[6];
                for (int i = 0; i < 6; i++) magic[i] = r.GetByteSafe(12 + i);
                for (int i = 0; i < 6; i++) value[i] = r.GetUShortSafe(18 + i);
                for (int i = 0; i < 6; i++) ext[i] = r.GetUIntSafe(24 + i);
                var item = new FullItemRow(0, 0, 0, r.GetUShortSafe(4), r.GetByteSafe(5), r.GetByteSafe(6), r.GetByteSafe(7),
                    r.GetUIntSafe(8), r.GetUIntSafe(9), r.GetByteSafe(10), 0, r.GetByteSafe(11), magic, value, ext,
                    r.GetByteSafe(30), r.GetUShortSafe(31), 0);
                var row = new CashGambleRow(r.GetUIntSafe(0), r.GetUIntSafe(1), r.GetUShortSafe(2), r.GetUShortSafe(3), item);
                if (!store.CashGamble.TryGetValue(row.Group, out var list)) store.CashGamble[row.Group] = list = new();
                list.Add(row);
                store.CashGambleTotal[row.Group] = store.CashGambleTotal.GetValueOrDefault(row.Group) + row.Prob;
            }

        try
        {
            await using var cmd = new SqlCommand(SpecialBoxChartSql, c);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var magic = new byte[6]; var value = new ushort[6]; var ext = new uint[6];
                for (int i = 0; i < 6; i++) magic[i] = r.GetByteSafe(11 + i);
                for (int i = 0; i < 6; i++) value[i] = r.GetUShortSafe(17 + i);
                for (int i = 0; i < 6; i++) ext[i] = r.GetUIntSafe(23 + i);
                var item = new FullItemRow(0, 0, 0, r.GetUShortSafe(3), r.GetByteSafe(4), r.GetByteSafe(5), r.GetByteSafe(6),
                    r.GetUIntSafe(7), r.GetUIntSafe(8), r.GetByteSafe(9), 0, r.GetByteSafe(10), magic, value, ext,
                    r.GetByteSafe(29), r.GetUShortSafe(30), 0);
                var row = new SpecialBoxRow(r.GetUShortSafe(0), r.GetUShortSafe(1), r.GetByteSafe(2), item);
                if (!store.SpecialBoxes.TryGetValue(row.Group, out var list)) store.SpecialBoxes[row.Group] = list = new();
                list.Add(row);
            }
        }
        catch (SqlException ex) when (ex.Number == 208) { /* no special boxes in this baseline */ }

        try
        {
            await using (var cmd = new SqlCommand(BattleZoneChartSql, c))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var z = new BattleZone(r.GetUShortSafe(0), r.GetStringSafe(1), r.GetUShortSafe(2), r.GetUShortSafe(3),
                        r.GetUShortSafe(4), r.GetUShortSafe(5), r.GetUShortSafe(6), r.GetUShortSafe(7),
                        r.GetUIntSafe(8), r.GetUIntSafe(9), r.GetUIntSafe(10), r.GetUShortSafe(11), r.GetUShortSafe(12), r.GetByteSafe(13),
                        r.GetUShortSafe(14), r.GetUShortSafe(15), r.GetByteSafe(16),
                        r.GetUShortSafe(17), r.GetUShortSafe(18), r.GetUShortSafe(19), r.GetUShortSafe(20),
                        r.GetUShortSafe(21), r.GetUShortSafe(22), r.GetUShortSafe(23));
                    store.BattleZones[z.Id] = z;
                }
            await using (var cmd = new SqlCommand(TerritorySql, c))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                for (int set = 0; set < 4; set++, await r.NextResultAsync(ct))
                    while (await r.ReadAsync(ct))
                        store.Territories.Add(new TerritoryRow(Convert.ToByte(r.GetValue(0)), r.GetUShortSafe(1), r.GetByteSafe(2),
                            r.IsDBNull(3) ? 0 : Convert.ToUInt32(r.GetValue(3)), r.IsDBNull(4) ? "" : r.GetString(4),
                            ToTime64(r, 5), ToTime64(r, 6), r.IsDBNull(7) ? "" : r.GetString(7), ToTime64(r, 8)));
                while (await r.ReadAsync(ct))
                    store.LocalOccupy.Add(new LocalOccupyRow(r.GetUShortSafe(0), r.GetByteSafe(1), r.GetUIntSafe(2), r.GetByteSafe(3)));
            }
            await using (var cmd = new SqlCommand(GodSql, c))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                static float F(SqlDataReader r, int i) => r.IsDBNull(i) ? 0f : Convert.ToSingle(r.GetValue(i));
                while (await r.ReadAsync(ct))
                    store.GodBallSpots.Add(new GodBallSpotRow(r.GetUShortSafe(0), r.GetByteSafe(1), r.GetUShortSafe(2), F(r, 3), F(r, 4), F(r, 5)));
                await r.NextResultAsync(ct);
                while (await r.ReadAsync(ct))
                    store.GodTowers.Add(new GodTowerRow(r.GetUShortSafe(0), r.GetUShortSafe(1), F(r, 2), F(r, 3), F(r, 4)));
            }
        }
        catch (SqlException ex) when (ex.Number == 208) { /* no territory tables in this baseline */ }

        try
        {
            await using var cmd = new SqlCommand("SELECT wID, wNpcID, wSpawnID, dwPrice, wTowerID FROM TMONSTERSHOPCHART; SELECT dwID, szMessage FROM TSVRMSGCHART; SELECT wSkillID, bType FROM TGUILDSKILLCHART", c);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                store.MonsterShops.Add(new MonsterShopRow(r.GetUShortSafe(0), r.GetUShortSafe(1), r.GetUShortSafe(2), r.GetUIntSafe(3), r.GetUShortSafe(4)));
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(1)) store.SvrMsgs[r.GetUIntSafe(0)] = r.GetString(1);
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                store.GuildSkillTypes[r.GetUShortSafe(0)] = r.GetByteSafe(1);
        }
        catch (SqlException ex) when (ex.Number == 208) { /* no guard shop / server messages in this baseline */ }

        await using (var cmd = new SqlCommand(LevelChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                byte level = r.GetByteSafe(0);
                store.RepairCostByLevel[level] = r.GetUIntSafe(1);
                store.LevelExp[level] = r.GetUIntSafe(2);
                store.LevelSkillPoint[level] = r.GetByteSafe(3);
                store.LevelMoney[level] = r.GetUIntSafe(4);
                store.RefineCostByLevel[level] = r.GetUIntSafe(5);
                store.LevelPvPMoney[level] = r.GetUIntSafe(6);
                store.LevelPvPoint[level] = r.GetUShortSafe(7);
                store.SoulmateCost[level] = (r.GetUIntSafe(8), r.GetUIntSafe(9));
                store.GambleCost[level] = (r.GetUIntSafe(10), r.GetUIntSafe(11));
            }

        await using (var cmd = new SqlCommand(PvPointChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (r.GetUShortSafe(0) == 0)
                    store.PvPointKill[(r.GetByteSafe(1), r.GetByteSafe(2))] = (r.GetUIntSafe(3), r.GetUIntSafe(4));
                else                           // a local's own rows: its battle zone's m_mapTPvPoint
                    store.LocalPvPoints[(r.GetUShortSafe(0), r.GetByteSafe(1), r.GetByteSafe(2))] = (r.GetUIntSafe(3), r.GetUIntSafe(4));

        await using (var cmd = new SqlCommand(TitleChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.Titles[id] = new TitleRow(id, r.GetUShortSafe(1), r.GetByteSafe(2), r.GetUIntSafe(3));
            }

        // Skill templates. Each is stamped with the global f1stRateX (= store.Rate1st, the
        // FTYPE_1ST growth base) exactly as the C++ loader does (pSkill->m_f1stRateX = f1stRateX).
        await using (var cmd = new SqlCommand(SkillChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.Skills[id] = new SkillTemplate(id, r.GetByteSafe(1),
                    UseMp: r.GetUIntSafe(2), UseMpType: r.GetByteSafe(3),
                    UseHp: r.GetUIntSafe(4), UseHpType: r.GetByteSafe(5),
                    StartLevel: r.GetByteSafe(6), MaxLevel: r.GetByteSafe(7), NextLevel: r.GetByteSafe(8),
                    ReuseDelay: r.GetUIntSafe(9), ReuseDelayInc: r.GetIntSafe(10),
                    LoopDelay: r.GetUIntSafe(11), KindDelay: r.GetUIntSafe(12),
                    SpeedApply: r.GetByteSafe(13), Positive: r.GetByteSafe(14), MapId: r.GetUShortSafe(15),
                    Duration: r.GetUIntSafe(16), DurationInc: r.GetUIntSafe(17), MaintainKind: r.GetByteSafe(18),
                    Priority: r.GetByteSafe(19), StaticFlag: r.GetByteSafe(20), ClassId: r.GetUIntSafe(21),
                    Rate1stX: store.Rate1st, Global: r.GetByteSafe(22) != 0,
                    IsRide: r.GetByteSafe(23) != 0, IsHideSkill: r.GetByteSafe(24) != 0,
                    IsDismount: r.GetByteSafe(25) != 0, EraseAct: r.GetByteSafe(26), TargetRange: r.GetByteSafe(27),
                    Price: r.IsDBNull(28) ? 0f : Convert.ToSingle(r.GetValue(28)), ParentSkillId: r.GetUShortSafe(29),
                    WeaponId: r.GetUIntSafe(30), Posture: r.GetUShortSafe(31), ORadius: r.GetByteSafe(32),
                    EraseHide: r.GetByteSafe(33), TargetHit: r.GetByteSafe(34), CanLearn: r.GetByteSafe(35), UseItem: r.GetUShortSafe(36));
            }

        await using (var cmd = new SqlCommand(SkillPointChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (store.Skills.TryGetValue(r.GetUShortSafe(0), out var sk))
                    sk.Points[r.GetByteSafe(1)] = new SkillPointRow(r.GetByteSafe(2), r.GetByteSafe(3), r.GetByteSafe(4), r.GetUIntSafe(5));

        await using (var cmd = new SqlCommand(SkillDataChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort skillId = r.GetUShortSafe(0);
                if (store.Skills.TryGetValue(skillId, out var sk))
                    sk.Data.Add(new SkillDataRow(r.GetByteSafe(1), r.GetByteSafe(2), r.GetByteSafe(3),
                        r.GetByteSafe(4), r.GetByteSafe(5), r.GetUShortSafe(6), r.GetUShortSafe(7), r.GetByteSafe(8)));
            }

        // Monster templates, attrs, and spawn points (+ their monster-type tables).
        await using (var cmd = new SqlCommand(MonsterChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.MonsterTemplates[id] = new MonsterTemplate(id, r.GetByteSafe(1), r.GetUShortSafe(2),
                    Exp: r.GetUIntSafe(3), MoneyProb: r.GetByteSafe(4),
                    MinMoney: r.GetUIntSafe(5), MaxMoney: r.GetUIntSafe(6),
                    ItemProb: r.GetByteSafe(7), DropCount: r.GetByteSafe(8),
                    AiType: r.GetByteSafe(9),
                    Skill1: r.GetUShortSafe(10), Skill2: r.GetUShortSafe(11),
                    Skill3: r.GetUShortSafe(12), Skill4: r.GetUShortSafe(13),
                    ChaseRange: r.GetUShortSafe(14),
                    RecallType: r.GetByteSafe(15), SummonAttr: r.GetUShortSafe(16), Class: r.GetByteSafe(17),
                    Race: r.GetByteSafe(18), IsSelf: r.GetByteSafe(19), CanSelect: r.GetByteSafe(20),
                    CanAttack: r.GetByteSafe(21), Tame: r.GetByteSafe(22));
            }

        await using (var cmd = new SqlCommand(MountChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.Mounts[id] = new MountTemplate(id, r.GetUShortSafe(1), r.GetUShortSafe(2));
            }

        await using (var cmd = new SqlCommand(CompanionBonusChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                byte id = r.GetByteSafe(0);
                store.CompanionBonuses.TryAdd(id, new CompanionBonus(id, r.GetFloatSafe(1), r.GetFloatSafe(2)));
            }

        await using (var cmd = new SqlCommand(CompanionRuneChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.CompanionRunes[r.GetUShortSafe(0)] = r.GetUShortSafe(1);

        // The AI scripts. Command templates → their conditions → the per-bAIType trigger bindings.
        var aiCommands = new Dictionary<uint, AiCommandTemplate>();
        await using (var cmd = new SqlCommand(AiCommandChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                uint id = r.GetUIntSafe(0);
                byte kind = r.GetByteSafe(1);
                // An unknown opcode maps to the C++ `default: new CTAICommand()` — a no-op body that still
                // fires AT_AICOMPLETE, so the chain continues. Kept as an out-of-range enum value on purpose.
                aiCommands[id] = new AiCommandTemplate(id, (AiCommandKind)kind);
            }

        await using (var cmd = new SqlCommand(AiConditionChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (aiCommands.TryGetValue(r.GetUIntSafe(0), out var owner))
                    owner.Conditions.Add(new AiCondition((AiConditionKind)r.GetByteSafe(2), r.GetUIntSafe(1)));

        await using (var cmd = new SqlCommand(AiChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                uint triggerId = r.GetUIntSafe(0), cmdId = r.GetUIntSafe(1), delay = r.GetUIntSafe(2);
                byte triggerType = r.GetByteSafe(3), aiType = r.GetByteSafe(4);
                // A chart row pointing at a missing command id is dropped: the C++ CreateCMD(NULL) would
                // build a command with a null m_pCOMMAND and crash in CanRun.
                if (!aiCommands.TryGetValue(cmdId, out var template)) continue;

                if (!store.AiScripts.TryGetValue(aiType, out var script))
                    store.AiScripts[aiType] = script = new AiScript(aiType);
                script.Bind(triggerType, triggerId, new AiBinding(template, delay, r.GetByteSafe(5) != 0));
            }

        await using (var cmd = new SqlCommand(MonAttrChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0); byte level = r.GetByteSafe(1);
                uint ap = r.GetUShortSafe(5), minWap = r.GetUShortSafe(6), maxWap = r.GetUShortSafe(7);
                // C++ GetDefendPower = m_wDP + m_wWDP and GetMagicDefPower = m_wMDP + m_wWDP (the weapon-DP term,
                // added when !HaveDisDefend — always true for a fresh monster, TMonster.cpp:1642/1736). Baked in
                // here to mirror the weapon-AP band (wAP + wMin/MaxWAP) we already fold into the attack.
                uint wWdp = r.GetUShortSafe(14);
                store.MonAttrs[TemplateStore.MonAttrKey(id, level)] =
                    new MonAttrRow(id, level, r.GetUIntSafe(2), r.GetUIntSafe(3), r.GetUShortSafe(4) + wWdp,
                        AtkMin: ap + minWap, AtkMax: ap + maxWap, AtkSpeed: r.GetUIntSafe(8), // C++ GetMin/MaxAP
                        MagicDefPower: r.GetUShortSafe(9) + wWdp, DefendLevel: r.GetUShortSafe(10),
                        MagicDefLevel: r.GetUShortSafe(11), CritProb: r.GetByteSafe(12), AttackLevel: r.GetUShortSafe(13),
                        Ap: (ushort)ap, LongAp: r.GetUShortSafe(15), MagicAp: r.GetUShortSafe(16), MinWap: (ushort)minWap,
                        MaxWap: (ushort)maxWap, MagicAtkLevel: r.GetUShortSafe(17), CritMagicProb: r.GetByteSafe(18),
                        RawDp: r.GetUShortSafe(4), RawMdp: r.GetUShortSafe(9), Wdp: (ushort)wWdp);
            }

        // The per-monster item drop rows, bucketed onto their monster template by wMonID.
        await using (var cmd = new SqlCommand(MonItemChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort monId = r.GetUShortSafe(0);
                if (store.MonsterTemplates.TryGetValue(monId, out var mt))
                    mt.DropRows.Add(new MonItemRow(r.GetUShortSafe(2), r.GetUShortSafe(3), r.GetByteSafe(1),
                        r.GetByteSafe(4), r.GetByteSafe(5), r.GetByteSafe(6), r.GetByteSafe(7),
                        ItemIdMin: r.GetUShortSafe(8), ItemIdMax: r.GetUShortSafe(9), LevelMin: r.GetByteSafe(10), LevelMax: r.GetByteSafe(11),
                        ProbMagic: r.GetByteSafe(12), ProbRare: r.GetByteSafe(13), MagicOpt: r.GetByteSafe(14), RareOpt: r.GetByteSafe(15)));
            }

        var spawnRows = new Dictionary<ushort, MonSpawnRow>();
        await using (var cmd = new SqlCommand(MonSpawnChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                spawnRows[id] = new MonSpawnRow(id, r.GetUShortSafe(1),
                    r.GetFloatSafe(2), r.GetFloatSafe(3), r.GetFloatSafe(4), r.GetUShortSafe(5), r.GetByteSafe(6),
                    r.GetByteSafe(7), r.GetByteSafe(8), r.GetByteSafe(9), r.GetUIntSafe(10), r.GetUIntSafe(11),
                    r.GetByteSafe(12), Area: r.GetByteSafe(13), LocalId: r.GetUShortSafe(14));
            }

        var typesBySpawn = new Dictionary<ushort, List<MapMonRow>>();
        await using (var cmd = new SqlCommand(MapMonChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort spawnId = r.GetUShortSafe(0);
                if (!typesBySpawn.TryGetValue(spawnId, out var list)) typesBySpawn[spawnId] = list = new();
                list.Add(new MapMonRow(spawnId, r.GetUShortSafe(1), r.GetByteSafe(2), r.GetByteSafe(3), r.GetByteSafe(4)));
            }

        foreach (var (id, spawn) in spawnRows)
            store.MonsterSpawns.Add(new MonsterSpawnDef(spawn,
                typesBySpawn.TryGetValue(id, out var types) ? types : new List<MapMonRow>()));

        // The NPC registry (TNPCCHART) + the per-NPC shop stock (TNPCITEMCHART, bulk-loaded like the
        // C++ CTBLNpcItemAll and bucketed by wNpcID). dwItemID is a DWORD but item ids are WORD (C++ (WORD)cast).
        await using (var cmd = new SqlCommand(NpcChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.Npcs[id] = new NpcDef(id, r.GetByteSafe(1), r.GetByteSafe(2), r.GetUShortSafe(3),
                    r.GetByteSafe(4), r.GetByteSafe(5), r.GetByteSafe(6), r.GetUShortSafe(7), r.GetUShortSafe(8),
                    r.GetFloatSafe(9), r.GetFloatSafe(10), r.GetFloatSafe(11));
            }

        await using (var cmd = new SqlCommand(SpawnPosChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.SpawnPositions[id] = new SpawnPosRow(id, r.GetUShortSafe(1), r.GetFloatSafe(2), r.GetFloatSafe(3),
                    r.GetFloatSafe(4), r.GetByteSafe(5));
            }

        await using (var cmd = new SqlCommand(PortalChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort id = r.GetUShortSafe(0);
                store.Portals[id] = new PortalRow(id, r.GetByteSafe(1), r.GetUShortSafe(2), r.GetUShortSafe(3), r.GetByteSafe(4));
            }

        // A destination whose target portal does not exist is dropped, as in the C++ join (TMapSvr.cpp:3806).
        await using (var cmd = new SqlCommand(DestinationChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort portalId = r.GetUShortSafe(0), destId = r.GetUShortSafe(1);
                if (!store.Portals.TryGetValue(portalId, out var portal) || !store.Portals.ContainsKey(destId)) continue;
                var conds = new[]
                {
                    new PortalCondition(r.GetByteSafe(4), r.GetUIntSafe(5)),
                    new PortalCondition(r.GetByteSafe(6), r.GetUIntSafe(7)),
                    new PortalCondition(r.GetByteSafe(8), r.GetUIntSafe(9)),
                };
                portal.Destinations[destId] = new PortalDestination(destId, r.GetUIntSafe(2), r.GetByteSafe(3), conds);
            }

        await using (var cmd = new SqlCommand(NpcItemChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                ushort npcId = r.GetUShortSafe(0);
                if (store.Npcs.TryGetValue(npcId, out var npc)) npc.ItemIds.Add((ushort)r.GetUIntSafe(1));
            }

        // Map switches + their gates (read-only definitions; runtime state is per-map-instance, rebuilt
        // from these at bring-up and never persisted, matching the C++ CTMap load).
        await using (var cmd = new SqlCommand(SwitchChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.Switches.Add(new SwitchDef(r.GetUIntSafe(0), r.GetUShortSafe(1),
                    r.GetUShortSafe(2), r.GetUShortSafe(3), r.GetUShortSafe(4),
                    r.GetByteSafe(5), r.GetByteSafe(6), r.GetByteSafe(7), r.GetUIntSafe(8)));

        await using (var cmd = new SqlCommand(GateChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                store.Gates.Add(new GateDef(r.GetUIntSafe(0), r.GetUIntSafe(1), r.GetByteSafe(2),
                    r.GetUShortSafe(3), r.GetUShortSafe(4), r.GetUShortSafe(5), r.GetUShortSafe(6)));

        // The quest template graph. Build every QuestTemplate from TQUESTCHART (inserted in the
        // bMain-DESC query order — the dict's insertion order is what InitQuests indexes, matching the C++
        // trigger-vector order), then attach conditions / rewards / terms by dwQuestID.
        await using (var cmd = new SqlCommand(QuestChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                uint questId = r.GetUIntSafe(1);
                store.Quests[questId] = new QuestTemplate
                {
                    QuestId = questId, ParentId = r.GetUIntSafe(0), TriggerId = r.GetUIntSafe(2),
                    TriggerType = r.GetByteSafe(3), ForceRun = r.GetByteSafe(4), CountMax = r.GetByteSafe(5),
                    Type = r.GetByteSafe(6), ConditionCheck = r.GetByteSafe(7),
                };
            }

        await using (var cmd = new SqlCommand(QuestCondChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (store.Quests.TryGetValue(r.GetUIntSafe(0), out var q))
                    q.Conditions.Add(new QuestCondition(r.GetUIntSafe(1), r.GetByteSafe(2), r.GetByteSafe(3)));

        await using (var cmd = new SqlCommand(QuestRewardChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (store.Quests.TryGetValue(r.GetUIntSafe(0), out var q))
                    q.Rewards.Add(new QuestReward(r.GetUIntSafe(1), r.GetByteSafe(2), r.GetByteSafe(3),
                        r.GetByteSafe(4), r.GetByteSafe(5)));

        await using (var cmd = new SqlCommand(QuestTermChartSql, c))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (store.Quests.TryGetValue(r.GetUIntSafe(0), out var q))
                    q.Terms.Add(new QuestTerm(r.GetUIntSafe(1), r.GetByteSafe(2), r.GetByteSafe(3)));

        return store;
    }

    // The TCHARTABLE columns the map's CTBLChar (DBAccess.h) reads. The first 20 are the load subset; the
    // trailing block is the rest of the TSaveChar value set (loaded so the save round-trips, not zeroes, them).
    private const string CharSql = @"
SELECT szNAME, bStartAct, bClass, bRace, bCountry, bSex, bHair, bFace, bBody, bPants, bHand, bFoot,
       bLevel, dwRegion, bHelmetHide, dwHP, dwMP, dwGold, dwSilver, dwCooper,
       dwEXP, wSkillPoint, bGuildLeave, dwGuildLeaveTime, wSpawnID, wLastSpawnID, dwLastDestination,
       wTemptedMon, bAftermath, bStatLevel, bStatPoint, dwStatExp, wMapID, fPosX, fPosY, fPosZ, wDir
FROM TCHARTABLE WHERE dwCharID = @dwCharID AND bDelete = 0";

    /// <summary>Loads a character's persistent row, or null if absent. CTBLChar.</summary>
    public async Task<CharLoadRow?> LoadCharAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(CharSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new CharLoadRow(
            r.GetStringSafe(0), r.GetByteSafe(1), r.GetByteSafe(2), r.GetByteSafe(3), r.GetByteSafe(4),
            r.GetByteSafe(5), r.GetByteSafe(6), r.GetByteSafe(7), r.GetByteSafe(8), r.GetByteSafe(9),
            r.GetByteSafe(10), r.GetByteSafe(11), r.GetByteSafe(12), r.GetUIntSafe(13), r.GetByteSafe(14),
            r.GetUIntSafe(15), r.GetUIntSafe(16), r.GetUIntSafe(17), r.GetUIntSafe(18), r.GetUIntSafe(19),
            r.GetUIntSafe(20), r.GetUShortSafe(21), r.GetByteSafe(22), r.GetUIntSafe(23), r.GetUShortSafe(24),
            r.GetUShortSafe(25), r.GetUIntSafe(26), r.GetUShortSafe(27), r.GetByteSafe(28), r.GetByteSafe(29),
            r.GetByteSafe(30), r.GetUIntSafe(31),
            r.GetUShortSafe(32), r.GetFloatSafe(33), r.GetFloatSafe(34), r.GetFloatSafe(35), r.GetUShortSafe(36));
    }

    // ================= Character + quest SAVE (proc calls; bodies live in the .bak baseline) =================

    /// <summary>C++ <c>TSaveChar</c> (CSPSaveChar) — the single-row char UPDATE by <c>dwCharID</c>. Params are
    /// bound positionally in the exact <c>DBAccess.h</c> order (28 value columns; the two pc-bang columns are 0
    /// — that subsystem is unported). A missing proc (2812, not in the baseline) is tolerated silently.</summary>
    public async Task SaveCharAsync(CharSaveData d, CancellationToken ct = default)
    {
        try
        {
            await using var c = await OpenAsync(ct);
            var args = new[]
            {
                SqlProc.In("@p0", SqlDbType.Int, unchecked((int)d.CharId)),
                SqlProc.In("@p1", SqlDbType.TinyInt, d.StartAct),
                SqlProc.In("@p2", SqlDbType.TinyInt, d.Level),
                SqlProc.In("@p3", SqlDbType.TinyInt, d.HelmetHide),
                SqlProc.In("@p4", SqlDbType.Int, unchecked((int)d.Gold)),
                SqlProc.In("@p5", SqlDbType.Int, unchecked((int)d.Silver)),
                SqlProc.In("@p6", SqlDbType.Int, unchecked((int)d.Cooper)),
                SqlProc.In("@p7", SqlDbType.TinyInt, d.GuildLeave),
                SqlProc.In("@p8", SqlDbType.Int, unchecked((int)d.GuildLeaveTime)),
                SqlProc.In("@p9", SqlDbType.Int, unchecked((int)d.Exp)),
                SqlProc.In("@p10", SqlDbType.Int, unchecked((int)d.Hp)),
                SqlProc.In("@p11", SqlDbType.Int, unchecked((int)d.Mp)),
                SqlProc.In("@p12", SqlDbType.SmallInt, unchecked((short)d.SkillPoint)),
                SqlProc.In("@p13", SqlDbType.Int, unchecked((int)d.Region)),
                SqlProc.In("@p14", SqlDbType.SmallInt, unchecked((short)d.MapId)),
                SqlProc.In("@p15", SqlDbType.SmallInt, unchecked((short)d.SpawnId)),
                SqlProc.In("@p16", SqlDbType.SmallInt, unchecked((short)d.LastSpawnId)),
                SqlProc.In("@p17", SqlDbType.SmallInt, unchecked((short)d.TemptedMon)),
                SqlProc.In("@p18", SqlDbType.TinyInt, d.Aftermath),
                SqlProc.In("@p19", SqlDbType.Real, d.PosX),
                SqlProc.In("@p20", SqlDbType.Real, d.PosY),
                SqlProc.In("@p21", SqlDbType.Real, d.PosZ),
                SqlProc.In("@p22", SqlDbType.SmallInt, unchecked((short)d.Dir)),
                SqlProc.In("@p23", SqlDbType.Int, unchecked((int)d.PcBangTime)),
                SqlProc.In("@p24", SqlDbType.TinyInt, d.PcBangItemCnt),
                SqlProc.In("@p25", SqlDbType.Int, unchecked((int)d.LastDestination)),
                SqlProc.In("@p26", SqlDbType.TinyInt, d.StatLevel),
                SqlProc.In("@p27", SqlDbType.TinyInt, d.StatPoint),
                SqlProc.In("@p28", SqlDbType.Int, unchecked((int)d.StatExp)),
            };
            await SqlProc.ExecAsync(c, "TSaveChar", SqlProc.Ret(), args, ct);
            await SaveMaintainsAsync(c, d, ct);
            await SavePvPointAsync(c, d, ct);
            await SaveTitlesAsync(c, d, ct);
            await SaveRecallsAsync(c, d, ct);
            await SaveDuringItemsAsync(c, d, ct);
        }
        catch (SqlException ex) when (ex.Number == 2812) { /* proc absent in this baseline — tolerate */ }
    }

    /// <summary>C++ <c>CSPSavePvPRecord</c> (DBAccess.h:6796) — <c>TSavePvPRecord(dwCharID, useable, total, 6 × (lose, win))</c>,
    /// upserting <c>TPVPOINTTABLE</c> and <c>TPVPRECORDTABLE</c>; then <c>CSPSaveMonthPvPoint</c>
    /// (<c>TSaveMonthPvPoint(dwCharID, point, win, lose, say, country)</c>) when the month points are not 0.</summary>
    private static async Task SavePvPointAsync(SqlConnection c, CharSaveData d, CancellationToken ct)
    {
        var args = new List<SqlParameter>
        {
            SqlProc.In("@p0", SqlDbType.Int, unchecked((int)d.CharId)),
            SqlProc.In("@p1", SqlDbType.Int, unchecked((int)d.PvpUseablePoint)),
            SqlProc.In("@p2", SqlDbType.Int, unchecked((int)d.PvpTotalPoint)),
        };
        for (int i = 0; i < 12; i++)
            args.Add(SqlProc.In($"@p{i + 3}", SqlDbType.Int, unchecked((int)(d.PvpRecord is { Length: 12 } rec ? rec[i] : 0u))));
        await SqlProc.ExecAsync(c, "TSavePvPRecord", null, args, ct);

        if (d.MonthPvPoint == 0) return;
        await SqlProc.ExecAsync(c, "TSaveMonthPvPoint", SqlProc.Ret(), new[]
        {
            SqlProc.In("@m0", SqlDbType.Int, unchecked((int)d.CharId)),
            SqlProc.In("@m1", SqlDbType.Int, unchecked((int)d.MonthPvPoint)),
            SqlProc.In("@m2", SqlDbType.SmallInt, unchecked((short)d.MonthWin)),
            SqlProc.In("@m3", SqlDbType.SmallInt, unchecked((short)d.MonthLose)),
            SqlProc.In("@m4", SqlDbType.VarChar, d.MonthSay, 256),
            SqlProc.In("@m5", SqlDbType.TinyInt, d.Country),
        }, ct);
    }

    /// <summary>C++ <c>DM_CLEARTITLE_REQ</c> + <c>DM_SAVETITLE_REQ</c> (SSSender.cpp:1519): <c>TClearCharTitle(dwCharID)</c>, then
    /// <c>TSaveCharTitle(dwCharID, wTitleID, bSelected)</c> per owned title.</summary>
    private static async Task SaveTitlesAsync(SqlConnection c, CharSaveData d, CancellationToken ct)
    {
        if (d.Titles is null) return;
        await SqlProc.ExecAsync(c, "TClearCharTitle", null, new[] { SqlProc.In("@t0", SqlDbType.Int, unchecked((int)d.CharId)) }, ct);
        foreach (var (id, selected) in d.Titles)
            await SqlProc.ExecAsync(c, "TSaveCharTitle", null, new[]
            {
                SqlProc.In("@t0", SqlDbType.Int, unchecked((int)d.CharId)),
                SqlProc.In("@t1", SqlDbType.SmallInt, unchecked((short)id)),
                SqlProc.In("@t2", SqlDbType.TinyInt, (byte)(selected ? 1 : 0)),
            }, ct);
    }

    /// <summary>C++ <c>CSPSaveSkillMaintain</c> (SSHandler.cpp:7131) — this baseline's <c>TSaveChar</c> deletes the character's
    /// <c>TSKILLMAINTAINTABLE</c> rows, and each buff is written back here.</summary>
    private static async Task SaveMaintainsAsync(SqlConnection c, CharSaveData d, CancellationToken ct)
    {
        foreach (var m in d.Maintains ?? Array.Empty<MaintainLoadRow>())
            await SqlProc.ExecAsync(c, "TSaveSkillMaintain", SqlProc.Ret(), new[]
            {
                SqlProc.In("@s0", SqlDbType.Int, unchecked((int)d.CharId)),
                SqlProc.In("@s1", SqlDbType.SmallInt, unchecked((short)m.SkillId)),
                SqlProc.In("@s2", SqlDbType.SmallInt, (short)m.Level),
                SqlProc.In("@s3", SqlDbType.Int, unchecked((int)m.RemainTick)),
                SqlProc.In("@s4", SqlDbType.TinyInt, m.AttackType),
                SqlProc.In("@s5", SqlDbType.Int, unchecked((int)m.AttackId)),
                SqlProc.In("@s6", SqlDbType.TinyInt, m.HostType),
                SqlProc.In("@s7", SqlDbType.Int, unchecked((int)m.HostId)),
                SqlProc.In("@s8", SqlDbType.TinyInt, m.AttackCountry),
            }, ct);
    }

    /// <summary>C++ <c>OnDM_SAVECHAR_REQ</c>'s summon part (SSHandler.cpp:7277): the owner's rows are replaced by its live
    /// summons and their buffs. Plain SQL, not the procs: this baseline's <c>TSaveRecallMon</c> also inserts an all-zero row
    /// on its "delete" call (an IF/ELSE without BEGIN/END), and its <c>TSaveRecallMaintain</c> clears the player's own buffs
    /// (<c>TSKILLMAINTAINTABLE</c>) instead of saving the summon's.</summary>
    private static async Task SaveRecallsAsync(SqlConnection c, CharSaveData d, CancellationToken ct)
    {
        if (d.Recalls is null) return;
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        async Task Exec(string sql, params SqlParameter[] ps)
        {
            await using var cmd = new SqlCommand(sql, c, tx);
            cmd.Parameters.AddRange(ps);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var owner = SqlProc.In("@o", SqlDbType.Int, unchecked((int)d.CharId));
        await Exec("DELETE TRECALLMONTABLE WHERE dwOwnerID = @o; DELETE TRECALLMAINTAINTABLE WHERE dwCharID = @o", owner);
        foreach (var m in d.Recalls)
            await Exec(@"INSERT INTO TRECALLMONTABLE (dwOwnerID, dwID, wMonID, wPetID, dwATTR, bLevel, dwHP, dwMP, bSkillLevel,
                    wPosX, wPosY, wPosZ, dwTime, bEffect) VALUES (@o, @i, @m, @p, @a, @l, @h, @mp, @s, @x, @y, @z, @t, @e)",
                SqlProc.In("@o", SqlDbType.Int, unchecked((int)d.CharId)), SqlProc.In("@i", SqlDbType.Int, unchecked((int)m.Id)),
                SqlProc.In("@m", SqlDbType.SmallInt, unchecked((short)m.MonId)), SqlProc.In("@p", SqlDbType.SmallInt, unchecked((short)m.PetId)),
                SqlProc.In("@a", SqlDbType.Int, unchecked((int)m.Attr)), SqlProc.In("@l", SqlDbType.TinyInt, m.Level),
                SqlProc.In("@h", SqlDbType.Int, unchecked((int)m.Hp)), SqlProc.In("@mp", SqlDbType.Int, unchecked((int)m.Mp)),
                SqlProc.In("@s", SqlDbType.TinyInt, m.SkillLevel), SqlProc.In("@x", SqlDbType.SmallInt, m.X),
                SqlProc.In("@y", SqlDbType.SmallInt, m.Y), SqlProc.In("@z", SqlDbType.SmallInt, m.Z),
                SqlProc.In("@t", SqlDbType.Int, unchecked((int)m.Time)), SqlProc.In("@e", SqlDbType.TinyInt, m.Effect));
        foreach (var b in d.RecallMaintains ?? Array.Empty<RecallMaintainRow>())
            await Exec(@"INSERT INTO TRECALLMAINTAINTABLE (dwCharID, dwRecallID, wSkillID, bLevel, dwRemainTick, bAttackType,
                    dwAttackID, bHostType, dwHostID, bAttackCountry) VALUES (@o, @r, @k, @l, @t, @at, @ai, CHAR(@ht), @hi, @c)",
                SqlProc.In("@o", SqlDbType.Int, unchecked((int)d.CharId)), SqlProc.In("@r", SqlDbType.Int, unchecked((int)b.RecallId)),
                SqlProc.In("@k", SqlDbType.SmallInt, unchecked((short)b.SkillId)), SqlProc.In("@l", SqlDbType.TinyInt, b.Level),
                SqlProc.In("@t", SqlDbType.Int, unchecked((int)b.Remain)), SqlProc.In("@at", SqlDbType.TinyInt, b.AttackType),
                SqlProc.In("@ai", SqlDbType.Int, unchecked((int)b.AttackId)), SqlProc.In("@ht", SqlDbType.Int, (int)b.HostType),
                SqlProc.In("@hi", SqlDbType.Int, unchecked((int)b.HostId)), SqlProc.In("@c", SqlDbType.TinyInt, b.AttackCountry));
        await tx.CommitAsync(ct);
    }

    /// <summary>C++ <c>OnDM_SAVECHAR_REQ</c>'s during items (SSHandler.cpp:7150) and the <c>TSaveCharDataEnd</c> copy: the
    /// character's exp item replaces its <c>TEXPITEMTABLE</c> rows, the premium the account's <c>TDURINGITEMTABLE</c> rows (what
    /// <c>TSaveExpItem</c> / <c>TSaveDuringItem</c> stage and the end proc moves). Plain SQL, in one transaction.</summary>
    private static async Task SaveDuringItemsAsync(SqlConnection c, CharSaveData d, CancellationToken ct)
    {
        if (!d.SaveDuring) return;
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        async Task Exec(string sql, DuringItemRow? row)
        {
            await using var cmd = new SqlCommand(sql, c, tx);
            cmd.Parameters.Add(SqlProc.In("@c", SqlDbType.Int, unchecked((int)d.CharId)));
            if (row is not null)
            {
                cmd.Parameters.Add(SqlProc.In("@i", SqlDbType.SmallInt, unchecked((short)row.ItemId)));
                cmd.Parameters.Add(SqlProc.In("@t", SqlDbType.TinyInt, row.Type));
                cmd.Parameters.Add(SqlProc.In("@r", SqlDbType.Int, unchecked((int)row.Remain)));
                cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.SmallDateTime) { Value = FromTime64(row.EndTime) });
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
        const string user = "(SELECT dwUserID FROM TCHARTABLE WHERE dwCharID = @c)";
        await Exec("DELETE TEXPITEMTABLE WHERE dwCharID = @c", null);
        await Exec("DELETE TGLOBAL_GSP.dbo.TDURINGITEMTABLE WHERE dwUserID = " + user, null);
        if (d.ExpItem is { } e)
            await Exec("INSERT INTO TEXPITEMTABLE (dwCharID, wItemID, bType, dwRemainTime, dEndTime) VALUES (@c, @i, @t, @r, @e)", e);
        if (d.Premium is { } p)
            await Exec("INSERT INTO TGLOBAL_GSP.dbo.TDURINGITEMTABLE (dwUserID, wItemID, bType, dwRemainTime, dEndTime) SELECT "
                + user + ", @i, @t, @r, @e", p);
        await tx.CommitAsync(ct);
    }

    /// <summary>C++ <c>CTBLDuringItem</c> + <c>CTBLExpItem</c> + <c>CSPGetPcBangData</c> at char load (SSHandler.cpp:3497/3893).
    /// <paramref name="playDate"/> is today as yyyymmdd.</summary>
    public async Task<DuringLoad> LoadDuringItemsAsync(uint charId, uint userId, int playDate, CancellationToken ct = default)
    {
        var premium = new List<DuringItemRow>();
        var exp = new List<DuringItemRow>();
        await using var c = await OpenAsync(ct);
        foreach (var (sql, id, list) in new[]
        {
            ("SELECT wItemID, bType, dwRemainTime, dEndTime FROM TVIEW_DURINGITEMTABLE WHERE dwUserID = @k", userId, premium),
            ("SELECT wItemID, bType, dwRemainTime, dEndTime FROM TEXPITEMTABLE WHERE dwCharID = @k", charId, exp),
        })
        {
            await using var cmd = new SqlCommand(sql, c);
            cmd.Parameters.Add(SqlProc.In("@k", SqlDbType.Int, unchecked((int)id)));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new DuringItemRow(r.GetUShortSafe(0), r.GetByteSafe(1), r.GetUIntSafe(2), ToTime64(r, 3)));
        }
        var ps = new[]
        {
            SqlProc.In("@u", SqlDbType.Int, unchecked((int)userId)), SqlProc.In("@d", SqlDbType.Int, playDate),
            SqlProc.Out("@b", SqlDbType.TinyInt), SqlProc.Out("@t", SqlDbType.Int), SqlProc.Out("@n", SqlDbType.TinyInt),
            SqlProc.Out("@l", SqlDbType.TinyInt),
        };
        try { await SqlProc.ExecAsync(c, "TGetPcBangData", SqlProc.Ret(), ps, ct); }
        catch (SqlException) { return new DuringLoad(premium, exp, 0, 0, 0); }
        static uint U(SqlParameter p) => p.Value is DBNull or null ? 0u : unchecked((uint)Convert.ToInt64(p.Value));
        return new DuringLoad(premium, exp, (byte)U(ps[2]), U(ps[3]), (byte)U(ps[4]), (byte)U(ps[5]));
    }

    /// <summary>C++ <c>CTBLRecallMon</c> + <c>CTBLRecallMaintain</c> at char load (SSHandler.cpp:3942/3981). The host type is
    /// a <c>char</c> column holding the byte (the C++ binds a BYTE to it).</summary>
    public async Task<(List<RecallSaveRow> Recalls, List<RecallMaintainRow> Buffs)> LoadRecallsAsync(uint charId, CancellationToken ct = default)
    {
        var recalls = new List<RecallSaveRow>();
        var buffs = new List<RecallMaintainRow>();
        await using var c = await OpenAsync(ct);
        await using (var cmd = new SqlCommand(@"SELECT dwID, wMonID, wPetID, dwATTR, bLevel, dwHP, dwMP, bSkillLevel, wPosX, wPosY, wPosZ,
                dwTime, bEffect FROM TRECALLMONTABLE WHERE dwOwnerID = @o", c))
        {
            cmd.Parameters.Add(SqlProc.In("@o", SqlDbType.Int, unchecked((int)charId)));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                recalls.Add(new RecallSaveRow(r.GetUIntSafe(0), r.GetUShortSafe(1), r.GetUShortSafe(2), r.GetUIntSafe(3), r.GetByteSafe(4),
                    r.GetUIntSafe(5), r.GetUIntSafe(6), r.GetByteSafe(7), (short)r.GetUShortSafe(8), (short)r.GetUShortSafe(9),
                    (short)r.GetUShortSafe(10), r.GetUIntSafe(11), r.GetByteSafe(12)));
        }
        await using (var cmd = new SqlCommand(@"SELECT dwRecallID, wSkillID, bLevel, dwRemainTick, bAttackType, dwAttackID,
                ISNULL(ASCII(bHostType), 0), dwHostID, bAttackCountry FROM TRECALLMAINTAINTABLE WHERE dwCharID = @o", c))
        {
            cmd.Parameters.Add(SqlProc.In("@o", SqlDbType.Int, unchecked((int)charId)));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                buffs.Add(new RecallMaintainRow(r.GetUIntSafe(0), r.GetUShortSafe(1), r.GetByteSafe(2), r.GetUIntSafe(3), r.GetByteSafe(4),
                    r.GetUIntSafe(5), (byte)r.GetUIntSafe(6), r.GetUIntSafe(7), r.GetByteSafe(8)));
        }
        return (recalls, buffs);
    }

    /// <summary>C++ <c>CSPGetPvPRecord</c> (DBAccess.h:6769) and <c>CSPGetMonthPvPoint</c> (DBAccess.h:7090) at char load.</summary>
    public async Task<PvpLoadRow> LoadPvpAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rec = new List<SqlParameter> { SqlProc.In("@r0", SqlDbType.Int, unchecked((int)charId)) };
        rec.Add(SqlProc.Out("@r1", SqlDbType.Int)); rec.Add(SqlProc.Out("@r2", SqlDbType.Int));
        rec.Add(SqlProc.Out("@r3", SqlDbType.Int)); rec.Add(SqlProc.Out("@r4", SqlDbType.TinyInt));
        for (int i = 0; i < 12; i++) rec.Add(SqlProc.Out($"@r{i + 5}", SqlDbType.Int));
        await SqlProc.ExecAsync(c, "TGetPvPRecord", null, rec, ct);

        var month = new List<SqlParameter>
        {
            SqlProc.In("@m0", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.Out("@m1", SqlDbType.Int), SqlProc.Out("@m2", SqlDbType.SmallInt), SqlProc.Out("@m3", SqlDbType.SmallInt),
            SqlProc.Out("@m4", SqlDbType.Int), SqlProc.Out("@m5", SqlDbType.TinyInt),
        };
        await SqlProc.ExecAsync(c, "TGetMonthPvPoint", SqlProc.Ret(), month, ct);

        static uint U(SqlParameter p) => p.Value is DBNull or null ? 0u : unchecked((uint)Convert.ToInt64(p.Value));
        return new PvpLoadRow(U(rec[1]), U(rec[2]), U(rec[3]), (byte)U(rec[4]), rec.Skip(5).Select(U).ToArray(),
            U(month[1]), (ushort)U(month[2]), (ushort)U(month[3]), U(month[4]), (byte)U(month[5]));
    }

    /// <summary>C++ <c>CTBLTitle</c> (DBAccess.h:1670) at char load — the owned titles and which one is shown.</summary>
    public async Task<List<(ushort Id, bool Selected)>> LoadTitlesAsync(uint charId, CancellationToken ct = default)
    {
        var list = new List<(ushort, bool)>();
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT wTitleID, bSelected FROM TTITLETABLE WHERE dwCharID = @dwCharID", c);
        cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetUShortSafe(0), r.GetByteSafe(1) != 0));
        return list;
    }

    /// <summary>C++ <c>CTMapSvrModule::SaveCharKill</c> → <c>TSaveCharKill(killer, target)</c>, a row in <c>charkilling_log</c>.</summary>
    /// <summary>C++ <c>TSaveCharBase</c> (CSPSaveCharBase) — a look / name / country change (the item kind) of a
    /// character, in TGame and, through the proc, TGlobal's character list.</summary>
    public async Task SaveCharBaseAsync(uint charId, byte kind, byte value, string name, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TSaveCharBase", null, new[]
        {
            SqlProc.In("@c0", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.In("@c1", SqlDbType.TinyInt, kind),
            SqlProc.In("@c2", SqlDbType.TinyInt, value),
            SqlProc.In("@c3", SqlDbType.VarChar, name, 50),
        }, ct);
    }

    /// <summary>C++ <c>CSPCheckDuplicateName</c> (DBAccess.h:6647): 0 when the name is free, else a <c>CCB_*</c> code (1 when a
    /// character, NPC or monster has it).</summary>
    public async Task<int> CheckDuplicateNameAsync(uint charId, string name, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        await SqlProc.ExecAsync(c, "TCheckDuplicateName", ret, new[]
        {
            SqlProc.In("@n0", SqlDbType.Int, unchecked((int)charId)), SqlProc.In("@n1", SqlDbType.VarChar, name, 50),
        }, ct);
        return ret.Value is int v ? v : 9;
    }

    /// <summary>The C++ char load's <c>bOriCountry</c> (<c>CTBLChar</c>) and aid country (<c>CTBLAidTable</c>, DBAccess.h:4599):
    /// the original country, and the aid country with when it was chosen (none: <c>TCONTRY_N</c>, 0).</summary>
    public async Task<(byte OriCountry, byte AidCountry, long AidDate)> LoadAidAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(@"SELECT bOriCountry, ISNULL(a.bCountry, 3), a.dDate FROM TCHARTABLE c
            LEFT JOIN TAIDTABLE a ON a.dwCharID = c.dwCharID WHERE c.dwCharID = @c", c);
        cmd.Parameters.Add(SqlProc.In("@c", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetByteSafe(0), r.GetByteSafe(1), ToTime64(r, 2)) : ((byte)0, (byte)3, 0L);
    }

    /// <summary>C++ <c>CSPSaveMissionOccupy</c> (DBAccess.h:7369) — <c>TSaveMissionOccupy(wLocalID, bType, dwCharID, bCountry)</c>: the
    /// mission's new owner country.</summary>
    /// <summary>C++ <c>CSPEventQuarter</c>: the lucky-ticket winner gets the quarter's prize by mail (<c>TEventQuarter</c>).</summary>
    public async Task EventQuarterAsync(string name, byte day, byte hour, byte minute, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TEventQuarter", null, new[]
        {
            SqlProc.In("@n", SqlDbType.VarChar, name), SqlProc.In("@d", SqlDbType.TinyInt, day),
            SqlProc.In("@h", SqlDbType.TinyInt, hour), SqlProc.In("@m", SqlDbType.TinyInt, minute),
        }, ct);
    }

    public async Task SaveMissionOccupyAsync(ushort localId, byte type, uint charId, byte country, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TSaveMissionOccupy", null, new[]
        {
            SqlProc.In("@m0", SqlDbType.SmallInt, unchecked((short)localId)), SqlProc.In("@m1", SqlDbType.TinyInt, type),
            SqlProc.In("@m2", SqlDbType.Int, unchecked((int)charId)), SqlProc.In("@m3", SqlDbType.TinyInt, country),
        }, ct);
    }

    /// <summary>C++ <c>CSPSaveSkyGardenOccupy</c> (DBAccess.h:5847) — <c>TSaveSkyGardenOccupy(bCountry, wID, bType)</c>.</summary>
    public async Task SaveSkyGardenOccupyAsync(byte country, ushort id, byte type, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TSaveSkyGardenOccupy", null, new[]
        {
            SqlProc.In("@g0", SqlDbType.TinyInt, country), SqlProc.In("@g1", SqlDbType.SmallInt, unchecked((short)id)),
            SqlProc.In("@g2", SqlDbType.TinyInt, type),
        }, ct);
    }

    /// <summary>C++ <c>CSPSaveLocalOccupy</c> (DBAccess.h:5863) — <c>TSaveLocalOccupy(wLocalID, bType, dwGuildID, dwCharID,
    /// OUT bCountry, OUT dwGuild)</c>: the fort's new owner (the proc decides the country from the guild's chief or the killer)
    /// and its week. Returns the proc's result (0 = done), the owner country and the owner guild.</summary>
    public async Task<(int Ret, byte Country, uint Guild)> SaveLocalOccupyAsync(ushort localId, byte type, uint guildId, uint charId,
        CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var ps = new[]
        {
            SqlProc.In("@l0", SqlDbType.SmallInt, unchecked((short)localId)), SqlProc.In("@l1", SqlDbType.TinyInt, type),
            SqlProc.In("@l2", SqlDbType.Int, unchecked((int)guildId)), SqlProc.In("@l3", SqlDbType.Int, unchecked((int)charId)),
            SqlProc.Out("@l4", SqlDbType.TinyInt), SqlProc.Out("@l5", SqlDbType.Int),
        };
        await SqlProc.ExecAsync(c, "TSaveLocalOccupy", ret, ps, ct);
        static uint U(SqlParameter p) => p.Value is DBNull or null ? 0u : unchecked((uint)Convert.ToInt64(p.Value));
        return (ret.AsInt(), (byte)U(ps[4]), U(ps[5]));
    }

    /// <summary>C++ <c>CSPSaveCastleOccupy</c> — <c>TSaveCastleOccupy(OUT bCountry, dwGuild, wCastle, bType)</c>: held, the next
    /// war a week on; taken, the new owner guild and its chief's country. Either way the castle's forts' week and its applicants
    /// are wiped. Returns the proc's result (0 = done; 1 = taken by no guild) and the owner country.</summary>
    public async Task<(int Ret, byte Country)> SaveCastleOccupyAsync(ushort castle, byte type, uint guildId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var ps = new[]
        {
            SqlProc.Out("@c0", SqlDbType.TinyInt), SqlProc.In("@c1", SqlDbType.Int, unchecked((int)guildId)),
            SqlProc.In("@c2", SqlDbType.SmallInt, unchecked((short)castle)), SqlProc.In("@c3", SqlDbType.TinyInt, type),
        };
        await SqlProc.ExecAsync(c, "TSaveCastleOccupy", ret, ps, ct);
        return (ret.AsInt(), ps[0].Value is DBNull or null ? (byte)3 : Convert.ToByte(ps[0].Value));
    }

    // ---- the guild cabinet (C++ CSPGuildItemPutIn / CSPGuildItemTakeOut / CSPGuildItemRollback, DBAccess.h) ----

    /// <summary>The item columns the guild cabinet procs share, from <paramref name="it"/> (dwTime1..6 = the ext values).</summary>
    private static List<SqlParameter> GuildItemArgs(in ItemSaveData it, bool withCount = true)
    {
        var p = new List<SqlParameter>
        {
            SqlProc.In("@i0", SqlDbType.SmallInt, unchecked((short)it.TemplateId)), SqlProc.In("@i1", SqlDbType.TinyInt, it.Level),
            SqlProc.In("@i2", SqlDbType.TinyInt, it.Gem), SqlProc.In("@i3", SqlDbType.SmallInt, unchecked((short)it.MoggItemId)),
        };
        if (withCount) p.Add(SqlProc.In("@i4", SqlDbType.TinyInt, it.Count));
        p.AddRange(new[]
        {
            SqlProc.In("@i5", SqlDbType.TinyInt, it.GLevel), SqlProc.In("@i6", SqlDbType.Int, unchecked((int)it.DuraMax)),
            SqlProc.In("@i7", SqlDbType.Int, unchecked((int)it.DuraCur)), SqlProc.In("@i8", SqlDbType.TinyInt, it.RefineCur),
            SqlProc.In("@i9", SqlDbType.DateTime, FromTime64(it.EndTime)), SqlProc.In("@i10", SqlDbType.TinyInt, it.GradeEffect),
        });
        for (int i = 0; i < 6; i++) p.Add(SqlProc.In($"@m{i}", SqlDbType.TinyInt, it.Magic[i]));
        for (int i = 0; i < 6; i++) p.Add(SqlProc.In($"@v{i}", SqlDbType.SmallInt, unchecked((short)it.Value[i])));
        for (int i = 0; i < 6; i++) p.Add(SqlProc.In($"@t{i}", SqlDbType.Int, unchecked((int)it.Ext[i])));
        return p;
    }

    /// <summary><c>TGuildItemPutIn</c>: the item into the guild's cabinet (stacked on a like item when it fits). Returns the proc's
    /// result (0 done, 2 full, 5 no cabinet) and the cabinet slot.</summary>
    public async Task<(int Ret, uint ItemId)> GuildItemPutInAsync(uint guildId, ItemSaveData item, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        var slot = SqlProc.Out("@g2", SqlDbType.Int);
        var ps = new List<SqlParameter> { SqlProc.In("@g0", SqlDbType.BigInt, item.DlId), SqlProc.In("@g1", SqlDbType.Int, unchecked((int)guildId)), slot };
        ps.AddRange(GuildItemArgs(item));
        await SqlProc.ExecAsync(c, "TGuildItemPutIn", ret, ps, ct);
        return (ret.AsInt(), slot.Value is DBNull or null ? 0u : unchecked((uint)Convert.ToInt32(slot.Value)));
    }

    /// <summary><c>TGuildItemTakeOut</c>: <paramref name="count"/> of the cabinet slot out (the row lessened or gone). Returns the
    /// proc's result (0 done, 3 not there) and the item taken.</summary>
    public async Task<(int Ret, ItemSaveData Item)> GuildItemTakeOutAsync(uint guildId, uint itemId, byte count, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ret = SqlProc.Ret();
        SqlParameter O(string n, SqlDbType t) => SqlProc.Out(n, t);
        var dl = O("@g0", SqlDbType.BigInt);
        var outs = new List<SqlParameter>
        {
            O("@i0", SqlDbType.SmallInt), O("@i1", SqlDbType.TinyInt), O("@i2", SqlDbType.TinyInt), O("@i3", SqlDbType.SmallInt),
        };
        var after = new List<SqlParameter>
        {
            O("@i5", SqlDbType.TinyInt), O("@i6", SqlDbType.Int), O("@i7", SqlDbType.Int), O("@i8", SqlDbType.TinyInt),
            O("@i9", SqlDbType.DateTime), O("@i10", SqlDbType.TinyInt),
        };
        for (int i = 0; i < 6; i++) after.Add(O($"@m{i}", SqlDbType.TinyInt));
        for (int i = 0; i < 6; i++) after.Add(O($"@v{i}", SqlDbType.SmallInt));
        for (int i = 0; i < 6; i++) after.Add(O($"@t{i}", SqlDbType.Int));
        var ps = new List<SqlParameter> { dl, SqlProc.In("@g1", SqlDbType.Int, unchecked((int)guildId)), SqlProc.In("@g2", SqlDbType.Int, unchecked((int)itemId)) };
        ps.AddRange(outs);
        ps.Add(SqlProc.In("@i4", SqlDbType.TinyInt, count));
        ps.AddRange(after);
        await SqlProc.ExecAsync(c, "TGuildItemTakeOut", ret, ps, ct);
        static long L(SqlParameter p) => p.Value is DBNull or null ? 0 : Convert.ToInt64(p.Value);
        long end = after[4].Value is DateTime d && d.Year >= 2000 ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;
        var item = new ItemSaveData(L(dl), 0, 0, 0, (ushort)L(outs[0]), (byte)L(outs[1]), count, (byte)L(after[0]), (uint)L(after[1]),
            (uint)L(after[2]), (byte)L(after[3]), end, (byte)L(after[5]),
            Enumerable.Range(6, 6).Select(i => (byte)L(after[i])).ToArray(),
            Enumerable.Range(12, 6).Select(i => unchecked((ushort)L(after[i]))).ToArray(),
            Enumerable.Range(18, 6).Select(i => unchecked((uint)L(after[i]))).ToArray(),
            (byte)L(outs[2]), unchecked((ushort)L(outs[3])));
        return (ret.AsInt(), item);
    }

    /// <summary><c>TGuildItemRollback</c>: a taken item that found no room goes back to its cabinet slot.</summary>
    public async Task GuildItemRollbackAsync(uint guildId, uint itemId, ItemSaveData item, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var ps = new List<SqlParameter>
        {
            SqlProc.In("@g0", SqlDbType.BigInt, item.DlId), SqlProc.In("@g1", SqlDbType.Int, unchecked((int)guildId)),
            SqlProc.In("@g2", SqlDbType.Int, unchecked((int)itemId)),
        };
        ps.AddRange(GuildItemArgs(item));
        await SqlProc.ExecAsync(c, "TGuildItemRollback", null, ps, ct);
    }

    public async Task SaveCharKillAsync(uint killerId, uint targetId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await SqlProc.ExecAsync(c, "TSaveCharKill", null, new[]
        {
            SqlProc.In("@k0", SqlDbType.Int, unchecked((int)killerId)),
            SqlProc.In("@k1", SqlDbType.Int, unchecked((int)targetId)),
        }, ct);
    }

    /// <summary>C++ <c>TSaveQuest</c> + <c>TSaveQuestTerm</c> (CSPSaveQuest/CSPSaveQuestTerm) — the per-quest
    /// upsert keyed on <c>(dwCharID, dwQuestID)</c> (+ <c>dwTermID</c> for terms). Only the caller's dirty
    /// (<c>Save</c>-flagged) quests are passed. All rows share one connection. Missing procs are tolerated.</summary>
    /// <summary>C++ <c>CSPSaveHotkey</c> (DBAccess.h:5813) — <c>TSaveHotkey(dwCharID, bSave, bInven, 12 × (bType, wID))</c>,
    /// one call per page, from <c>OnDM_SAVECHAR_REQ</c> (SSHandler.cpp:7218).</summary>
    public async Task SaveHotkeysAsync(uint charId, IReadOnlyList<HotkeySaveRow> pages, CancellationToken ct = default)
    {
        if (pages.Count == 0) return;
        try
        {
            await using var c = await OpenAsync(ct);
            foreach (var pg in pages)
            {
                var ps = new List<SqlParameter>
                {
                    SqlProc.In("@p0", SqlDbType.Int, unchecked((int)charId)),
                    SqlProc.In("@p1", SqlDbType.TinyInt, pg.Verb),
                    SqlProc.In("@p2", SqlDbType.TinyInt, pg.InvenKey),
                };
                for (int i = 0; i < HotkeyPosCount; i++)
                {
                    var (type, id) = i < pg.Slots.Length ? pg.Slots[i] : ((byte)0, (ushort)0);
                    ps.Add(SqlProc.In($"@p{3 + i * 2}", SqlDbType.TinyInt, type));
                    ps.Add(SqlProc.In($"@p{4 + i * 2}", SqlDbType.SmallInt, unchecked((short)id)));
                }
                await SqlProc.ExecAsync(c, "TSaveHotkey", SqlProc.Ret(), ps.ToArray(), ct);
            }
        }
        catch (SqlException ex) when (ex.Number == 2812) { /* proc absent — tolerate */ }
    }

    /// <summary>The character's skills — C++ <c>OnDM_SAVECHAR_REQ</c> writes each one (<c>TSaveSkill</c>, into
    /// <c>TTEMPSKILLTABLE</c>) and <c>TLogout</c> then replaces the character's <c>TSKILLTABLE</c> rows with them (this
    /// baseline's <c>TSaveCharDataEnd</c> has that copy commented out). The same end state, in one transaction: the rows
    /// deleted and every held skill — level 0 included — written back with its remaining reuse time.</summary>
    public async Task SaveSkillsAsync(uint charId, IReadOnlyList<SkillSaveRow> skills, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        int cid = unchecked((int)charId);
        await using (var del = new SqlCommand("DELETE FROM TSKILLTABLE WHERE dwCharID = @c", c, tx))
        {
            del.Parameters.Add(SqlProc.In("@c", SqlDbType.Int, cid));
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var k in skills)
        {
            await using var ins = new SqlCommand(
                "INSERT INTO TSKILLTABLE (dwCharID, wSkillID, bLevel, dwRemainTick) VALUES (@c, @s, @l, @t)", c, tx);
            ins.Parameters.Add(SqlProc.In("@c", SqlDbType.Int, cid));
            ins.Parameters.Add(SqlProc.In("@s", SqlDbType.SmallInt, unchecked((short)k.SkillId)));
            ins.Parameters.Add(SqlProc.In("@l", SqlDbType.TinyInt, k.Level));
            ins.Parameters.Add(SqlProc.In("@t", SqlDbType.Int, unchecked((int)k.RemainTick)));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task SaveQuestsAsync(uint charId, IReadOnlyList<QuestSaveRow> quests, CancellationToken ct = default)
    {
        if (quests.Count == 0) return;
        try
        {
            await using var c = await OpenAsync(ct);
            int cid = unchecked((int)charId);
            foreach (var q in quests)
            {
                await SqlProc.ExecAsync(c, "TSaveQuest", SqlProc.Ret(), new[]
                {
                    SqlProc.In("@p0", SqlDbType.Int, cid),
                    SqlProc.In("@p1", SqlDbType.Int, unchecked((int)q.QuestId)),
                    SqlProc.In("@p2", SqlDbType.Int, unchecked((int)q.Tick)),
                    SqlProc.In("@p3", SqlDbType.TinyInt, q.CompleteCount),
                    SqlProc.In("@p4", SqlDbType.TinyInt, q.TriggerCount),
                }, ct);

                foreach (var t in q.Terms)
                    await SqlProc.ExecAsync(c, "TSaveQuestTerm", SqlProc.Ret(), new[]
                    {
                        SqlProc.In("@p0", SqlDbType.Int, cid),
                        SqlProc.In("@p1", SqlDbType.Int, unchecked((int)q.QuestId)),
                        SqlProc.In("@p2", SqlDbType.Int, unchecked((int)t.TermId)),
                        SqlProc.In("@p3", SqlDbType.TinyInt, t.TermType),
                        SqlProc.In("@p4", SqlDbType.TinyInt, t.Count),
                    }, ct);
            }
        }
        catch (SqlException ex) when (ex.Number == 2812) { /* proc absent — tolerate */ }
    }

    /// <summary>C++ <c>TInitGenItemID(@out, @serverId)</c> (CSPInitGenItemID) — the per-server base for the
    /// unique item-id counter (so multiple map servers carve disjoint <c>dlID</c> ranges). Returns 0 when the
    /// DB / proc is unavailable (⇒ the caller won't rewrite inventory, avoiding unsafe ids).</summary>
    public async Task<long> InitGenItemIdAsync(byte serverId, CancellationToken ct = default)
    {
        try
        {
            await using var c = await OpenAsync(ct);
            var outId = SqlProc.Out("@p0", SqlDbType.BigInt);
            await SqlProc.ExecAsync(c, "TInitGenItemID", null,
                new[] { outId, SqlProc.In("@p1", SqlDbType.TinyInt, serverId) }, ct);
            return outId.Value is null or DBNull ? 0L : Convert.ToInt64(outId.Value);
        }
        catch (SqlException ex) when (ex.Number == 2812) { return 0L; }
    }

    /// <summary>C++ char-item SAVE (<c>SSHandler.cpp OnDM_SAVEITEM_REQ</c>, CSPSaveItemData*) — the
    /// delete-then-reinsert rewrite bracketed by <c>TSaveItemDataStart</c>/<c>TSaveItemDataEnd</c>: Start clears
    /// the char's <b>staging</b> tables (<c>TTEMPINVENTABLE</c>/<c>TTEMPITEMTABLE</c>), each container
    /// (<c>TSaveInven</c>) and item (<c>TSaveItem</c>, 35 values in exact param order) is inserted into staging,
    /// then End atomically promotes staging → live (<c>TINVENTABLE</c> and <c>TITEMTABLE</c>, filtered
    /// <c>bOwnerType=0 AND bStorageType=0</c> — matching the <c>TOWNER_CHAR</c>/<c>STORAGE_INVEN</c> we write).
    /// All on one connection so each proc's inner transaction sees the staged rows.
    ///
    /// <para><b>Do not use the <c>TSaveCharData*</c> bracket here:</b> in this baseline its item promotion
    /// (<c>INSERT TITEMTABLE SELECT … TTEMPITEMTABLE</c>) is <i>commented out</i>, so items would stage but never
    /// reach live — verified against the live proc bodies. <c>TSaveCharData*</c> is the separate cabinet/skill/
    /// used-item save.</para>
    /// A missing proc is tolerated.</summary>
    public async Task SaveInventoryAsync(uint charId, IReadOnlyList<InvenSaveData> invens,
        IReadOnlyList<ItemSaveData> items, CancellationToken ct = default)
    {
        try
        {
            await using var c = await OpenAsync(ct);
            int cid = unchecked((int)charId);

            await SqlProc.ExecAsync(c, "TSaveItemDataStart", null, new[] { SqlProc.In("@p0", SqlDbType.Int, cid) }, ct);

            foreach (var iv in invens)
                await SqlProc.ExecAsync(c, "TSaveInven", SqlProc.Ret(), new[]
                {
                    SqlProc.In("@p0", SqlDbType.Int, cid),
                    SqlProc.In("@p1", SqlDbType.TinyInt, iv.InvenId),
                    SqlProc.In("@p2", SqlDbType.SmallInt, unchecked((short)iv.TemplateId)),
                    SqlProc.In("@p3", SqlDbType.DateTime, FromTime64(iv.EndTime)),
                    SqlProc.In("@p4", SqlDbType.TinyInt, iv.Eld),
                }, ct);

            foreach (var it in items)
                await SqlProc.ExecAsync(c, "TSaveItem", SqlProc.Ret(), ItemArgs(cid, it), ct);

            await SqlProc.ExecAsync(c, "TSaveItemDataEnd", null, new[] { SqlProc.In("@p0", SqlDbType.Int, cid) }, ct);
        }
        catch (SqlException ex) when (ex.Number == 2812) { /* proc absent — tolerate */ }
    }

    /// <summary>The <c>TSaveItem</c>/<c>TSaveItemDirect</c> 35-value positional arg set (identical param list,
    /// exact <c>DBAccess.h</c> order). <c>bOwnerType</c> is always <c>TOWNER_CHAR</c> and <c>dwOwnerID</c> the
    /// char; the item's storage type/slot/attributes fill the rest. Shared by the full inventory rewrite and the
    /// incremental direct save.</summary>
    private static SqlParameter[] ItemArgs(int cid, in ItemSaveData it) => new[]
    {
        SqlProc.In("@p0", SqlDbType.BigInt, it.DlId),
        SqlProc.In("@p1", SqlDbType.TinyInt, it.StorageType),
        SqlProc.In("@p2", SqlDbType.Int, unchecked((int)it.StorageId)),
        SqlProc.In("@p3", SqlDbType.TinyInt, Proto.OwnerChar),
        SqlProc.In("@p4", SqlDbType.Int, cid),
        SqlProc.In("@p5", SqlDbType.TinyInt, it.ItemSlot),
        SqlProc.In("@p6", SqlDbType.SmallInt, unchecked((short)it.TemplateId)),
        SqlProc.In("@p7", SqlDbType.TinyInt, it.Level),
        SqlProc.In("@p8", SqlDbType.TinyInt, it.Count),
        SqlProc.In("@p9", SqlDbType.TinyInt, it.GLevel),
        SqlProc.In("@p10", SqlDbType.Int, unchecked((int)it.DuraMax)),
        SqlProc.In("@p11", SqlDbType.Int, unchecked((int)it.DuraCur)),
        SqlProc.In("@p12", SqlDbType.TinyInt, it.RefineCur),
        SqlProc.In("@p13", SqlDbType.DateTime, FromTime64(it.EndTime)),
        SqlProc.In("@p14", SqlDbType.TinyInt, it.GradeEffect),
        SqlProc.In("@p15", SqlDbType.TinyInt, it.Magic[0]),
        SqlProc.In("@p16", SqlDbType.TinyInt, it.Magic[1]),
        SqlProc.In("@p17", SqlDbType.TinyInt, it.Magic[2]),
        SqlProc.In("@p18", SqlDbType.TinyInt, it.Magic[3]),
        SqlProc.In("@p19", SqlDbType.TinyInt, it.Magic[4]),
        SqlProc.In("@p20", SqlDbType.TinyInt, it.Magic[5]),
        SqlProc.In("@p21", SqlDbType.SmallInt, unchecked((short)it.Value[0])),
        SqlProc.In("@p22", SqlDbType.SmallInt, unchecked((short)it.Value[1])),
        SqlProc.In("@p23", SqlDbType.SmallInt, unchecked((short)it.Value[2])),
        SqlProc.In("@p24", SqlDbType.SmallInt, unchecked((short)it.Value[3])),
        SqlProc.In("@p25", SqlDbType.SmallInt, unchecked((short)it.Value[4])),
        SqlProc.In("@p26", SqlDbType.SmallInt, unchecked((short)it.Value[5])),
        SqlProc.In("@p27", SqlDbType.Int, unchecked((int)it.Ext[0])),
        SqlProc.In("@p28", SqlDbType.Int, unchecked((int)it.Ext[1])),
        SqlProc.In("@p29", SqlDbType.Int, unchecked((int)it.Ext[2])),
        SqlProc.In("@p30", SqlDbType.Int, unchecked((int)it.Ext[3])),
        SqlProc.In("@p31", SqlDbType.Int, unchecked((int)it.Ext[4])),
        SqlProc.In("@p32", SqlDbType.Int, unchecked((int)it.Ext[5])),
        SqlProc.In("@p33", SqlDbType.TinyInt, it.Gem),
        SqlProc.In("@p34", SqlDbType.SmallInt, unchecked((short)it.MoggItemId)),
    };

    /// <summary>C++ <c>TSaveItemDirect</c> (CSPSaveItemDirect) — a single-item upsert straight to the live
    /// <c>TITEMTABLE</c> (delete-by-<c>dlID</c>-then-insert), no staging bracket. The incremental fast-path: one
    /// row per changed item so an item change survives a crash before the next full rewrite. Same 35-value param
    /// set as <c>TSaveItem</c>. Missing proc (2812) tolerated.</summary>
    public async Task SaveItemDirectAsync(uint charId, ItemSaveData item, CancellationToken ct = default)
    {
        try
        {
            await using var c = await OpenAsync(ct);
            await SqlProc.ExecAsync(c, "TSaveItemDirect", SqlProc.Ret(), ItemArgs(unchecked((int)charId), item), ct);
        }
        catch (SqlException ex) when (ex.Number == 2812) { /* proc absent — tolerate */ }
    }

    /// <summary>Removes one item row from the live <c>TITEMTABLE</c> by its primary key — the incremental
    /// counterpart of <see cref="SaveItemDirectAsync"/> for a consumed/dropped/sold item. A single-row delete by
    /// <c>dlID</c> (exactly what <c>TSaveItemDirect</c> itself does before re-inserting); it never touches a
    /// shared table. There is no dedicated delete proc in the baseline, so this is a parameterized statement.</summary>
    public async Task DeleteItemDirectAsync(long dlId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE FROM TITEMTABLE WHERE dlID = @dlID", c);
        cmd.Parameters.Add(SqlProc.In("@dlID", SqlDbType.BigInt, dlId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The inverse of <see cref="ToTime64"/> — a __time64_t (seconds since 1970) back to a SQL
    /// <c>smalldatetime</c>. Mirrors the C++ <c>__TIMETODB</c> (TNetDef.h): <b>0 (no expiry) → 1900-01-01</b>,
    /// the sentinel the game reads back as 0 (<c>__DBTOTIME</c>: <c>year &lt; 2000 ⇒ 0</c>). It is <b>never
    /// <see cref="DBNull"/></b> — the <c>dEndTime</c> columns (incl. the staging <c>TTEMPINVENTABLE</c>) are
    /// <c>NOT NULL</c>, so a NULL would fail the insert.</summary>
    private static object FromTime64(long secs) => secs <= 0
        ? new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs);

    private const string ItemSql = @"
SELECT bItemID, wItemID, bLevel, bGradeEffect, dwTime3, dwTime4, dwTime6, wMoggItemID
FROM TITEMTABLE
WHERE dwOwnerID = @dwOwnerID AND bOwnerType = @bOwnerType AND bStorageType = @bStorageType AND dwStorageID = @dwStorageID";

    /// <summary>Items in a storage slot. CTBLItem (subset; dwTime3/4/6 alias color/regGuild/customTex per DBAccess.h).</summary>
    public async Task<List<ItemLoadRow>> ItemListAsync(uint ownerId, byte ownerType, byte storageType, uint storageId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(ItemSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwOwnerID", SqlDbType.Int, unchecked((int)ownerId)));
        cmd.Parameters.Add(SqlProc.In("@bOwnerType", SqlDbType.TinyInt, ownerType));
        cmd.Parameters.Add(SqlProc.In("@bStorageType", SqlDbType.TinyInt, storageType));
        cmd.Parameters.Add(SqlProc.In("@dwStorageID", SqlDbType.Int, unchecked((int)storageId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ItemLoadRow>();
        while (await r.ReadAsync(ct))
            list.Add(new ItemLoadRow(
                r.GetByteSafe(0), r.GetUShortSafe(1), r.GetByteSafe(2), r.GetByteSafe(3),
                r.GetUShortSafe(4), (byte)r.GetUIntSafe(5), r.GetUShortSafe(6), r.GetUShortSafe(7)));
        return list;
    }

    // ================= Full inventory / gear / skills / hotkeys load =================

    private static long ToTime64(SqlDataReader r, int i)
    {
        // TITEMTABLE/TINVENTABLE dEndTime is a smalldatetime; the wire wants __time64_t (seconds since 1970).
        // Mirrors C++ __DBTOTIME (TNetDef.h): a pre-2000 date — chiefly the 1900-01-01 no-expiry sentinel that
        // FromTime64 writes for 0 — reads back as 0.
        if (r.IsDBNull(i)) return 0;
        var dt = Convert.ToDateTime(r.GetValue(i));
        if (dt.Year < 2000) return 0;
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long secs = (long)(DateTime.SpecifyKind(dt, DateTimeKind.Utc) - epoch).TotalSeconds;
        return secs < 0 ? 0 : secs;
    }

    private const string InvenSql =
        "SELECT bInvenID, wItemID, dEndTime, bELD FROM TINVENTABLE WHERE dwCharID = @dwCharID";

    /// <summary>CTBLInven — the character's inventory containers (bags), excluding the implicit backpack/equip.</summary>
    public async Task<List<InvenLoadRow>> LoadInvensAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(InvenSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<InvenLoadRow>();
        while (await r.ReadAsync(ct))
            list.Add(new InvenLoadRow(r.GetByteSafe(0), r.GetUShortSafe(1), ToTime64(r, 2), r.GetByteSafe(3)));
        return list;
    }

    // CTBLItem — the 32 columns the map needs (of the C++ 35-column CTBLItem read in DBAccess.h; dlID/
    // bOwnerType/dwOwnerID are omitted). dwTime1..6 are the m_dwExtValue[] array (not timers).
    private const string FullItemSql = @"
SELECT bStorageType, dwStorageID, bItemID, wItemID, bLevel, bCount, bGLevel, dwDuraMax, dwDuraCur,
       bRefineCur, dEndTime, bGradeEffect,
       bMagic1, bMagic2, bMagic3, bMagic4, bMagic5, bMagic6,
       wValue1, wValue2, wValue3, wValue4, wValue5, wValue6,
       dwTime1, dwTime2, dwTime3, dwTime4, dwTime5, dwTime6,
       bGem, wMoggItemID, dlID
FROM TITEMTABLE WHERE dwOwnerID = @dwOwnerID AND bOwnerType = @bOwnerType AND bStorageType <> 2";

    /// <summary>CTBLItem — every item the character owns (all storages except type 2), with full attributes.</summary>
    public async Task<List<FullItemRow>> LoadItemsAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(FullItemSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwOwnerID", SqlDbType.Int, unchecked((int)charId)));
        cmd.Parameters.Add(SqlProc.In("@bOwnerType", SqlDbType.TinyInt, Proto.OwnerChar));
        return await ReadFullItems(cmd, ct);
    }

    /// <summary>C++ <c>CTBLPostItem</c> (DBAccess.h:1714) — the items attached to one mail: owner = the recipient,
    /// <c>bStorageType = STORAGE_POST</c>, <c>dwStorageID</c> = the post id.</summary>
    public async Task<List<FullItemRow>> LoadPostItemsAsync(uint charId, uint postId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(FullItemSql.Replace("AND bStorageType <> 2", "AND bStorageType = 2 AND dwStorageID = @dwStorageID"), c);
        cmd.Parameters.Add(SqlProc.In("@dwOwnerID", SqlDbType.Int, unchecked((int)charId)));
        cmd.Parameters.Add(SqlProc.In("@bOwnerType", SqlDbType.TinyInt, Proto.OwnerChar));
        cmd.Parameters.Add(SqlProc.In("@dwStorageID", SqlDbType.Int, unchecked((int)postId)));
        return await ReadFullItems(cmd, ct);
    }

    private static async Task<List<FullItemRow>> ReadFullItems(SqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<FullItemRow>();
        while (await r.ReadAsync(ct))
        {
            var magic = new byte[6];
            var value = new ushort[6];
            var ext = new uint[6];
            for (int i = 0; i < 6; i++) magic[i] = r.GetByteSafe(12 + i);
            for (int i = 0; i < 6; i++) value[i] = r.GetUShortSafe(18 + i);
            for (int i = 0; i < 6; i++) ext[i] = r.GetUIntSafe(24 + i);
            list.Add(new FullItemRow(
                r.GetByteSafe(0), r.GetUIntSafe(1), r.GetByteSafe(2), r.GetUShortSafe(3), r.GetByteSafe(4),
                r.GetByteSafe(5), r.GetByteSafe(6), r.GetUIntSafe(7), r.GetUIntSafe(8), r.GetByteSafe(9),
                ToTime64(r, 10), r.GetByteSafe(11), magic, value, ext, r.GetByteSafe(30), r.GetUShortSafe(31),
                r.GetInt64Safe(32)));
        }
        return list;
    }

    private const string SkillSql =
        "SELECT wSkillID, bLevel, dwRemainTick FROM TSKILLTABLE WHERE dwCharID = @dwCharID";

    /// <summary>CTBLSkill — the character's learned skills.</summary>
    public async Task<List<SkillLoadRow>> LoadSkillsAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(SkillSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SkillLoadRow>();
        while (await r.ReadAsync(ct))
            list.Add(new SkillLoadRow(r.GetUShortSafe(0), r.GetByteSafe(1), r.GetUIntSafe(2)));
        return list;
    }

    private const string MaintainSql = @"
SELECT wSkillID, bLevel, dwRemainTick, bAttackType, dwAttackID, bHostType, dwHostID, bAttackCountry
FROM TSKILLMAINTAINTABLE WHERE dwCharID = @dwCharID";

    /// <summary>CTBLSkillMaintain — the character's active maintained/buff skills (8 persisted fields).</summary>
    public async Task<List<MaintainLoadRow>> LoadMaintainAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(MaintainSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<MaintainLoadRow>();
        while (await r.ReadAsync(ct))
            list.Add(new MaintainLoadRow(
                r.GetUShortSafe(0), r.GetByteSafe(1), r.GetUIntSafe(2), r.GetByteSafe(3),
                r.GetUIntSafe(4), r.GetByteSafe(5), r.GetUIntSafe(6), r.GetByteSafe(7)));
        return list;
    }

    private const string QuestTableSql =
        @"SELECT dwQuestID, dwTick, bCompleteCount, bTriggerCount FROM TQUESTTABLE WHERE dwCharID = @dwCharID";
    private const string QuestTermTableSql =
        @"SELECT dwQuestID, dwTermID, bTermType, bCount FROM TQUESTTERMTABLE WHERE dwCharID = @dwCharID";

    /// <summary>C++ <c>CTBLQuestTable</c> + <c>CTBLQuestTermTable</c> (DBAccess.h:2410-2468) — the character's
    /// saved quest progress (headers + running term counters), keyed by <c>dwCharID</c>. The read-back
    /// counterpart of the <c>TSaveQuest</c>/<c>TSaveQuestTerm</c> write.</summary>
    public async Task<(List<QuestLoadRow> quests, List<QuestTermLoadRow> terms)> LoadQuestsAsync(
        uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var quests = new List<QuestLoadRow>();
        var terms = new List<QuestTermLoadRow>();
        int cid = unchecked((int)charId);

        await using (var cmd = new SqlCommand(QuestTableSql, c))
        {
            cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, cid));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                quests.Add(new QuestLoadRow(r.GetUIntSafe(0), r.GetUIntSafe(1), r.GetByteSafe(2), r.GetByteSafe(3)));
        }
        await using (var cmd = new SqlCommand(QuestTermTableSql, c))
        {
            cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, cid));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                terms.Add(new QuestTermLoadRow(r.GetUIntSafe(0), r.GetUIntSafe(1), r.GetByteSafe(2), r.GetByteSafe(3)));
        }
        return (quests, terms);
    }

    private const int HotkeyPosCount = 12; // MAX_HOTKEY_POS

    /// <summary>
    /// CTBLHotKey — hotkey pages. The C++ query is <c>SELECT *</c> bound positionally
    /// (dwCharID, bInven, then 12 × {bType, wHotID}). We read positionally too and tolerate a row whose
    /// shape doesn't match (skips hotkeys) so an unexpected DDL never breaks login.
    /// </summary>
    public async Task<List<HotkeyLoadRow>> LoadHotkeysAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT * FROM THOTKEYTABLE WHERE dwCharID = @dwCharID", c);
        cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<HotkeyLoadRow>();
        const int expected = 2 + HotkeyPosCount * 2; // dwCharID, bInven, 12×{type,id}
        while (await r.ReadAsync(ct))
        {
            if (r.FieldCount < expected) break; // unexpected shape → skip hotkeys
            byte invenKey = r.GetByteSafe(1);
            var slots = new (byte, ushort)[HotkeyPosCount];
            for (int i = 0; i < HotkeyPosCount; i++)
                slots[i] = (r.GetByteSafe(2 + i * 2), r.GetUShortSafe(3 + i * 2));
            list.Add(new HotkeyLoadRow(invenKey, slots));
        }
        return list;
    }

    private const string CabinetSql =
        "SELECT bCabinetID, bUse FROM TCABINETTABLE WHERE dwCharID = @dwCharID ORDER BY bCabinetID";

    /// <summary>C++ <c>CTBLCabinetTable</c> (DBAccess.h:2845) — the character's cabinet open-state headers
    /// (which cabinets exist + their <c>bUse</c> flag). The cabinet <b>items</b> ride in the normal item load
    /// (<see cref="LoadItemsAsync"/>, <c>bStorageType=STORAGE_CABINET</c>).</summary>
    public async Task<List<CabinetHeaderRow>> LoadCabinetsAsync(uint charId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(CabinetSql, c);
        cmd.Parameters.Add(SqlProc.In("@dwCharID", SqlDbType.Int, unchecked((int)charId)));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CabinetHeaderRow>();
        while (await r.ReadAsync(ct))
            list.Add(new CabinetHeaderRow(r.GetByteSafe(0), r.GetByteSafe(1) != 0));
        return list;
    }

    /// <summary>C++ <c>TSaveCabinet</c> (CSPSaveCabinet, DBAccess.h:5688) — persists one cabinet's open-state
    /// (<c>bUse</c>). The cabinet's items persist separately via the incremental item save
    /// (<see cref="SaveItemDirectAsync"/> with <c>STORAGE_CABINET</c>). Missing proc (2812) tolerated.</summary>
    public async Task SaveCabinetAsync(uint charId, byte cabinetId, bool use, CancellationToken ct = default)
    {
        try
        {
            await using var c = await OpenAsync(ct);
            await SqlProc.ExecAsync(c, "TSaveCabinet", SqlProc.Ret(), new[]
            {
                SqlProc.In("@p0", SqlDbType.Int, unchecked((int)charId)),
                SqlProc.In("@p1", SqlDbType.TinyInt, cabinetId),
                SqlProc.In("@p2", SqlDbType.TinyInt, (byte)(use ? 1 : 0)),
            }, ct);
        }
        catch (SqlException ex) when (ex.Number == 2812) { /* proc absent — tolerate */ }
    }
}
