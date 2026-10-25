using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// The server-side character state the map needs to drive the client-enter handshake, the view
/// (CS_ENTER/LEAVE) broadcast and movement. A faithful-but-minimal port of the relevant <c>CTPlayer</c> /
/// <c>CTObjBase</c> fields — the deep combat/stat/skill/inventory model is deferred (see PORT_STATUS.md).
/// </summary>
public sealed class Character
{
    public uint CharId { get; set; }
    public string Name { get; set; } = "";

    // Appearance / identity
    public byte Class { get; set; }
    public byte Race { get; set; }
    public byte Country { get; set; }
    public byte AidCountry { get; set; }
    /// <summary>C++ <c>m_bOriCountry</c> (the country a Broa player goes back to) and <c>m_dlAidDate</c> (unix seconds of the
    /// last aid-country change, 0 for none).</summary>
    public byte OriCountry { get; set; }
    public long AidDate { get; set; }
    public byte Sex { get; set; }
    public byte Hair { get; set; }
    public byte Face { get; set; }
    public byte Body { get; set; }
    public byte Pants { get; set; }
    public byte Hand { get; set; }
    public byte Foot { get; set; }
    public byte HelmetHide { get; set; }
    /// <summary>C++ <c>m_strComment</c> — the profile comment (<c>CS_COMMENT_REQ</c>).</summary>
    public string Comment { get; set; } = "";
    public byte Level { get; set; } = 1;
    /// <summary>C++ <c>m_dwLockedMonID</c> — the corpse whose loot window this character has open (0 = none).</summary>
    public uint LockedMonId { get; set; }

    // Party / guild / tactics
    public ushort PartyId { get; set; }
    public uint PartyChiefId { get; set; }
    public ushort CommanderId { get; set; }
    /// <summary>C++ <c>m_bPartyType</c> (PARTY_TYPE / loot mode) — replicated from the world (enter + MW pushes).
    /// <c>PT_SOLO</c> opts the member out of all party sharing (the accessors below then report no party). Phase 38.</summary>
    public byte PartyType { get; set; }
    public uint GuildId { get; set; }

    // ---- PT_SOLO-masked party accessors (C++ CTObjBase::GetPartyID / CTPlayer::GetPartyChiefID / GetCommanderID) ----
    private const byte PtSolo = 1;   // PARTY_TYPE PT_SOLO (NetCode.h:1960)
    /// <summary>C++ <c>GetPartyID</c> — the effective party id, or 0 when solo (opts out of exp/loot sharing).</summary>
    public ushort GetPartyId() => PartyType == PtSolo ? (ushort)0 : PartyId;
    /// <summary>C++ <c>GetPartyChiefID</c> — the party chief id, masked to 0 when solo.</summary>
    public uint GetPartyChiefId() => PartyType == PtSolo ? 0u : PartyChiefId;
    /// <summary>C++ <c>GetCommanderID</c> — the squad/corps commander id, masked to 0 when solo.</summary>
    public ushort GetCommanderId() => PartyType == PtSolo ? (ushort)0 : CommanderId;
    public uint Fame { get; set; }
    public uint FameColor { get; set; }
    public byte GuildDuty { get; set; }
    public byte GuildPeer { get; set; }
    public string GuildName { get; set; } = "";
    public uint TacticsId { get; set; }
    public string TacticsName { get; set; } = "";

    // Money / exp / vitals — money is three DWORD tiers combined via CalcMoney (base 1000).
    public uint Gold { get; set; }
    public uint Silver { get; set; }
    public uint Cooper { get; set; }
    public uint PrevExp { get; set; }
    public uint NextExp { get; set; }
    public uint Exp { get; set; }
    public uint MaxHp { get; set; } = 1;
    public uint Hp { get; set; } = 1;
    public uint MaxMp { get; set; }
    public uint Mp { get; set; }

    // Position / motion
    public uint RegionId { get; set; }

    /// <summary>C++ <c>m_wLocalID</c> — the territory the player stands in (MapService.Territory.cs), as its client names it; and
    /// that territory's two zone buffs (kept out of the save).</summary>
    public ushort LocalId { get; set; }
    public (ushort Skill1, ushort Skill2) ZoneSkills { get; set; }

    /// <summary>C++ <c>m_wCastle</c> / <c>m_bCamp</c> — the castle the player signed up for and its side (CAMP_DEFEND 1 /
    /// CAMP_ATTACK 2), from the world.</summary>
    public ushort Castle { get; set; }
    public byte Camp { get; set; }
    /// <summary>C++ <c>m_wGodBall</c> — the god ball carried in a castle war, 0 = none (MapService.Castle.cs).</summary>
    public ushort GodBall { get; set; }
    /// <summary>C++ <c>m_guildItem</c> — the item on its way into the guild cabinet, until the database answers (MapService.GuildBoard.cs).</summary>
    public Item? GuildItem { get; set; }
    public ushort MapId { get; set; }
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public ushort Dir { get; set; }
    public ushort Pitch { get; set; }
    public byte Action { get; set; }
    public byte Block { get; set; }
    public byte Mode { get; set; }
    public byte MouseDir { get; set; } = Monster.TkdirN;   // C++ CTObjBase: TKDIR_N, not 0 (TKDIR_LF)
    public byte KeyDir { get; set; } = Monster.TkdirN;

    // ---- Phase 45: monster host-acquisition inputs (C++ CTPlayer, read by CTAICmdSetHost). ----
    /// <summary>C++ <c>m_bCanHost</c> (TPlayer.h:109) — whether this player is eligible to become a monster's
    /// host/target. Sticky: set the first time the player moves (C++ CSHandler.cpp:555; here on any MOVE — the
    /// C++ "not alone in a cell" guard is a harmless simplification since acquisition only reads it when a
    /// monster is already nearby). <c>CanHost</c>-the-method's ghost branch is deferred (the port doesn't model
    /// ghost); a dead player is excluded by the acquisition scan's HP guard instead.</summary>
    public bool CanHost { get; set; }
    /// <summary>C++ <c>m_dwMoveTick</c> (TPlayer.h:121) — the map-clock (ms) of this player's last MOVE packet
    /// (C++ CSHandler.cpp:517). Feeds <c>SetHost</c>'s "moved within the last 3000 ms" recency filter.</summary>
    public uint LastMoveMs { get; set; }

    // PvP points
    public uint PvpTotalPoint { get; set; }
    public uint PvpUseablePoint { get; set; }
    /// <summary>C++ <c>m_vPvPRecent</c> — the recent kills and deaths.</summary>
    public List<PvpRecord> PvpRecent { get; } = new();
    /// <summary>C++ <c>m_aPvPRecord[class][win]</c>, flattened as <c>class·2 + (win ? 1 : 0)</c> — the all-time wins and
    /// losses against each class; <see cref="TotalWin"/> / <see cref="TotalLose"/> are their sums.</summary>
    public uint[] PvpRecord { get; } = new uint[12];
    public uint TotalWin { get; set; }
    public uint TotalLose { get; set; }
    /// <summary>C++ <c>m_dwPvPRankOrder</c> / <c>m_bPvPRankPercent</c> — the all-time place, as loaded.</summary>
    public uint PvpRankOrder { get; set; }
    public byte PvpRankPercent { get; set; }
    /// <summary>This month (C++ <c>m_dwMonthPvPoint</c>, <c>m_wMonthWin</c>/<c>Lose</c>, <c>m_dwMonthRankOrder</c>,
    /// <c>m_bMonthRankPercent</c>, <c>m_strMonthSay</c>).</summary>
    public uint MonthPvPoint { get; set; }
    public ushort MonthWin { get; set; }
    public ushort MonthLose { get; set; }
    public uint MonthRankOrder { get; set; }
    public byte MonthRankPercent { get; set; }
    public string MonthSay { get; set; } = "";
    /// <summary>C++ <c>m_mapTTITLE</c> — the owned titles by id, and whether each is the shown one; <see cref="TitleId"/> is
    /// the shown one (C++ <c>m_wTitleID</c>, from the world at login).</summary>
    public SortedDictionary<ushort, bool> Titles { get; } = new();
    public ushort TitleId { get; set; }
    /// <summary>Called after every money check or change (the C++ UseMoney/EarnMoney GOLD_TITLE check).</summary>
    public Action<Character>? MoneyChecked { get; set; }
    /// <summary>C++ <c>m_dwDuelID</c> / <c>m_bDuelType</c> / <c>m_dwDuelTarget</c> — the duel the player is in (0 = none), its
    /// state as the player sees it (only ever 0 or DUEL_END) and the opponent.</summary>
    public uint DuelId { get; set; }
    public byte DuelType { get; set; }
    public uint DuelTarget { get; set; }

    /// <summary>Unspent skill points (C++ <c>m_wSkillPoint</c>), granted on level-up. Not yet consumed
    /// (skill-buy is unported) — bookkeeping only.</summary>
    public uint SkillPoint { get; set; }

    // ---- Phase 16: HP/MP regen (Recover) state. Ticks are absolute deadlines vs the map ms clock. ----
    /// <summary>C++ <c>m_dwRecoverHPTick</c> — the anchor the next HP regen is measured from.</summary>
    public uint RecoverHpTick { get; set; }
    /// <summary>C++ <c>m_dwRecoverMPTick</c>.</summary>
    public uint RecoverMpTick { get; set; }
    /// <summary>C++ <c>m_dwLastAtkTick</c> — set on every combat action; drives the battle→normal timeout.</summary>
    public uint LastAtkTick { get; set; }

    /// <summary>C++ <c>ChgMode(MT_BATTLE)</c> + <c>m_dwLastAtkTick = now</c> combat entry (TObjBase.cpp:310):
    /// entering battle pushes both recover anchors to <c>now + RECOVER_INIT</c> (only on the NORMAL→BATTLE
    /// transition), suppressing HP regen; every hit refreshes <see cref="LastAtkTick"/>.</summary>
    public void EnterBattle(uint now, uint recoverInit)
    {
        if (Mode != 1) // MT_BATTLE
        {
            Mode = 1;
            RecoverHpTick = now + recoverInit;
            RecoverMpTick = now + recoverInit;
        }
        LastAtkTick = now;
    }

    /// <summary>0 = no intro cinematic needed, 1 = play intro (CS_CHARINFO_ACK bStartAct).</summary>
    public byte StartAct { get; set; }

    // ---- Phase 25: persistence ----

    /// <summary>True only when this character was loaded from a real <c>TCHARTABLE</c> row (not synthesized
    /// DB-free). The save path saves only DB-loaded chars — a synthesized char is never written back.</summary>
    public bool DbLoaded { get; set; }

    /// <summary>The map-clock ms of the last save (C++ <c>m_dwSaveTick</c>) — drives the periodic-save throttle.</summary>
    public uint LastSaveMs { get; set; }

    /// <summary>The <c>TSaveChar</c> value columns the map doesn't otherwise model but must round-trip so the
    /// full-row save doesn't zero them (loaded from <c>TCHARTABLE</c>, written back unchanged).</summary>
    public CharPersistExtras Persist { get; } = new();

    // ---- the death penalty (C++ m_aftermath, TMapType.h:1296) ----
    // The step (0..100) is Persist.Aftermath — it is what the DB stores. The rest derives from it.

    /// <summary>C++ <c>m_aftermath.m_dwTick</c> — when the next one-step recovery is due (map ms clock).</summary>
    public uint AftermathTick { get; set; }

    // ---- mail (C++ m_pPost / m_dwPostID / m_wPostTotal / m_wPostRead) ----

    /// <summary>The mail being read — the one take-item, delete and return act on.</summary>
    public Post? OpenPost { get; set; }

    /// <summary>A mail read still in flight (C++ <c>m_dwPostID</c>); 0 when none.</summary>
    public uint PostPending { get; set; }

    public ushort PostTotal { get; set; }
    public ushort PostNotRead { get; set; }

    /// <summary>C++ <c>CalcMoney(gold, silver, cooper)</c> for an arbitrary amount.</summary>
    public long MoneyOf(uint gold, uint silver, uint cooper)
        => cooper + (long)silver * MoneyMultiply + (long)gold * MoneyMultiply * MoneyMultiply;

    /// <summary>C++ <c>m_aftermath.m_fStatDec</c> — the percentage every primary stat loses (<c>step · 0.3</c>).</summary>
    public float AftermathStatDec => (float)(Persist.Aftermath * 0.3);

    // ---- Phase-2: loaded inventory / gear / skills / hotkeys ----

    /// <summary>Inventory containers keyed by container id (0xFF backpack, 0xFE equipped, timed bags…).</summary>
    public List<Inven> Invens { get; } = new();

    /// <summary>Learned skills (CS_CHARINFO_ACK skill sub-loop).</summary>
    public List<Skill> Skills { get; } = new();

    /// <summary>C++ <c>m_mapGuildSkill</c> — the guild skills held, run out or not (MapService.GuildSkill.cs).</summary>
    public Dictionary<ushort, GuildSkillHeld> GuildSkills { get; } = new();

    /// <summary>C++ <c>m_mapTPROTECTED</c> — the protected (block) list by character id (MapService.Social.cs), read once on
    /// entering.</summary>
    public Dictionary<uint, ProtectedEntry> Protected { get; } = new();
    public bool ProtectedLoaded;

    /// <summary>C++ <c>m_dwSoulmate</c> / <c>m_strSoulmate</c> / <c>m_dwSoulSilence</c> — the soulmate chosen, as the world says
    /// (MapService.Soulmate.cs), and when the last bond was ended (0 = none: a day must pass before choosing again).</summary>
    public uint SoulmateId;
    public string SoulmateName = "";
    public uint SoulSilence;

    /// <summary>C++ <c>m_vAuctionInterest</c> / <c>m_vAuctionBid</c> / <c>m_vAuctionReg</c> — lots watched, bid on and sold (MapService.Auction.cs).</summary>
    public List<uint> AuctionInterest = new(), AuctionBid = new(), AuctionReg = new();
    public bool AuctionLoaded;

    /// <summary>C++ <c>m_dwSoulLotEXP</c> / <c>m_bUseLot</c> / <c>m_dwLotNumber1..4</c> / <c>m_dwLotMoney</c> — the soul bar and the soul
    /// lottery drawn (MapService.SoulLottery.cs). Memory only, as in the C++.</summary>
    public uint SoulLotExp;
    public bool UseLot;
    public uint[]? LotNumbers;
    public uint LotMoney;

    /// <summary>C++ <c>m_bRPSType</c> / <c>m_bRPSWin</c> — the rock-paper-scissors game being played (MapService.Rps.cs), 0 = none.</summary>
    public byte RpsType, RpsWin;

    /// <summary>C++ <c>m_wArenaID</c> / <c>m_bArenaTeam</c> — the arena the character fights in and its side (1 or 2), 0 = none
    /// (MapService.Arena.cs).</summary>
    public ushort ArenaId { get; set; }

    /// <summary>C++ <c>m_bIsTournamentPlayer</c> + the <c>m_*_bak</c> it set aside: non-null while playing as the tournament character
    /// (MapService.TournamentMatch.cs). Nothing is saved meanwhile.</summary>
    public MapService.TournamentBackup? TournamentBackup { get; set; }
    public byte ArenaTeam { get; set; }

    /// <summary>C++ <c>m_vRemainSkill</c> — the learned skills whose rows apply for good (<c>IsRemainType</c>). The C++
    /// fills it with every such skill at login and, on learning one, first drops another of the same
    /// <c>m_bPriority</c>; this reads the learned list instead (the live remain skills, 41-46, are one per class).</summary>
    public IEnumerable<Skill> RemainSkills => Skills.Where(k => k.Template?.IsRemainType() == true);

    /// <summary>Active maintained/buff skills (CS_CHARINFO_ACK / CS_ENTER_ACK maintain sub-loop).</summary>
    public List<MaintainSkill> MaintainSkills { get; } = new();

    /// <summary>Hotkey pages (CS_CHARINFO_ACK hotkey sub-loop).</summary>
    public List<HotkeyPage> HotkeyPages { get; } = new();

    // ---- summons, pets, mounts (C++ m_mapRecallMon / m_mapTPET / m_mapSADDLE / m_dwRiding) ----

    /// <summary>The player's summons by recall id (C++ <c>m_mapRecallMon</c>).</summary>
    public Dictionary<uint, RecallMon> Recalls { get; } = new();

    /// <summary>C++ <c>m_dwRiding</c> — the recall id of the mount being ridden, 0 when on foot.</summary>
    public uint Riding { get; set; }

    /// <summary>C++ <c>m_nChatBanTime</c>: the chat is banned until this time (the world's, on entering; MapService.Gm.cs).</summary>
    public long ChatBanTime { get; set; }

    /// <summary>Summons saved at the last logout, waiting to come back (MapService.RecallSave.cs): sent to the world at
    /// login, each taken off when the world's copy arrives, and saved again if the player leaves first.</summary>
    public List<(TMap.Data.RecallSaveRow Row, List<TMap.Data.RecallMaintainRow> Buffs)> PendingRecalls { get; } = new();

    /// <summary>C++ <c>m_diPremium</c> / <c>m_diExp</c> — the running premium and exp boost (MapService.DuringItem.cs).</summary>
    public DuringItem? Premium { get; set; }
    public DuringItem? ExpItem { get; set; }

    /// <summary>C++ <c>m_bInPcBang</c> (PCBANG_REAL 1 / PREMIUM1 2 / PREMIUM2 4), <c>m_dwPcBangTime</c> (bonus seconds used today)
    /// and <c>m_bPcBangItemCnt</c>; <c>DuringTick</c> is the unix second of the last countdown step (C++ <c>m_timeEnter</c>).</summary>
    public byte InPcBang { get; set; }
    public uint PcBangTime { get; set; }
    public byte PcBangItemCnt { get; set; }

    /// <summary>C++ <c>m_bLuckyNumber</c> (<c>TCURRENTUSER.bLuckyNumber</c>, via <c>TGetPcBangData</c>) — the lucky-ticket number (MapService.Events.cs).</summary>
    public byte LuckyNumber { get; set; }
    public long DuringTick { get; set; }

    /// <summary>C++ <c>m_mapItemCoolTime</c> — per item delay group, the map tick its items can be used again.</summary>
    public Dictionary<ushort, uint> ItemCoolTime { get; } = new();

    /// <summary>The account's pets by pet id (C++ <c>m_mapTPET</c>, account-wide).</summary>
    public Dictionary<ushort, Pet> Pets { get; } = new();

    /// <summary>The account's saddle (C++ <c>m_mapSADDLE[userId]</c>), null when none.</summary>
    public TMap.Data.SaddleRow? Saddle { get; set; }

    /// <summary>C++ <c>m_dwMedals</c> — medals, loaded/saved with companions; pays for mount effects and custom cloaks.</summary>
    public uint Medals { get; set; }

    /// <summary>The owned companions by slot 0..4 (C++ <c>m_mapComp</c>).</summary>
    public SortedDictionary<byte, Companion> Companions { get; } = new();

    /// <summary>C++ <c>m_bCompanionSlot</c> — the summoned companion's slot, 0xFF for none. It drives the owner
    /// bonuses even while no companion is out (the owner dead, for instance).</summary>
    public byte CompanionSlot { get; set; } = 0xFF;

    /// <summary>The player's placed objects by id (C++ <c>m_mapSelfMon</c>) — traps, crystals, eyes, arrow rains.</summary>
    public SortedDictionary<uint, RecallMon> SelfObjs { get; } = new();

    /// <summary>The companion creatures in the world (C++ <c>m_mapCompanion</c>).</summary>
    public Dictionary<uint, RecallMon> CompanionObjs { get; } = new();

    public Companion? SummonedCompanion => Companions.GetValueOrDefault(CompanionSlot);

    /// <summary>C++ <c>FindRecallPet</c> (TPlayer.cpp:4170) — the called mount, if any.</summary>
    public RecallMon? FindRecallPet() => Recalls.Values.FirstOrDefault(m => m.RecallType == RecallMon.TypePet);

    // ---- Phase 24: quests (C++ CTPlayer m_mapQUEST / m_mapLevelQuest) ----

    /// <summary>Active/accepted quests keyed by quest id (C++ <c>m_mapQUEST</c>). In-memory only — quest
    /// progress is not persisted (the <c>DM_*</c> save procs are deferred).</summary>
    public Dictionary<uint, QuestProgress> Quests { get; } = new();

    /// <summary>Level-gated quest bookkeeping (C++ <c>m_mapLevelQuest</c>, keyed by the quest's SAMELEVEL
    /// condition value → quest id) — one level-quest at a time.</summary>
    public Dictionary<uint, uint> LevelQuest { get; } = new();

    /// <summary>C++ <c>CTPlayer::FindQuest</c> — the quest progress for an id, or null.</summary>
    public QuestProgress? FindQuest(uint questId) => Quests.GetValueOrDefault(questId);

    /// <summary>C++ <c>CTPlayer::IsRunningQuest</c> — accepted and not yet completed this cycle.</summary>
    public bool IsRunningQuest(uint questId) => FindQuest(questId) is { IsRunning: true };

    /// <summary>The equipped-gear container (id 0xFE), or null if none loaded — used for the CS_ENTER_ACK block.</summary>
    public Inven? Equipped => Invens.FirstOrDefault(i => i.InvenId == 0xFE);

    /// <summary>Finds an inventory container by its id (C++ <c>CTObjBase::FindTInven</c>), or null.</summary>
    public Inven? FindInven(byte invenId) => Invens.FirstOrDefault(i => i.InvenId == invenId);

    /// <summary>The player's cabinets (item warehouse) — C++ <c>CTPlayer::m_mapCabinet</c>. Created lazily on
    /// open (or on the enter-time cabinet load). Phase 37.</summary>
    public List<Cabinet> Cabinets { get; } = new();

    /// <summary>C++ <c>CTPlayer::GetCabinet</c> — the cabinet with this id, or null.</summary>
    public Cabinet? FindCabinet(byte cabinetId) => Cabinets.FirstOrDefault(c => c.CabinetId == cabinetId);

    /// <summary>C++ <c>CTPlayer::PutinCabinet</c> — the cabinet with this id, created (added to
    /// <see cref="Cabinets"/>) if absent. Used by open and the enter-time load.</summary>
    public Cabinet GetOrCreateCabinet(byte cabinetId)
    {
        if (FindCabinet(cabinetId) is { } c) return c;
        var made = new Cabinet { CabinetId = cabinetId };
        Cabinets.Add(made);
        return made;
    }

    // ---- Money (C++ CalcMoney / CTPlayer::UseMoney / EarnMoney) ----

    /// <summary>C++ <c>MONEY_MULTIPLY</c> — the gold/silver/copper tier base.</summary>
    public const uint MoneyMultiply = 1000;

    /// <summary>The combined balance (C++ <c>CalcMoney(gold, silver, cooper)</c>):
    /// <c>cooper + silver·1000 + gold·1000²</c>.</summary>
    public long MoneyTotal => Cooper + (long)Silver * MoneyMultiply + (long)Gold * MoneyMultiply * MoneyMultiply;

    /// <summary>Splits a combined balance back into the three tiers (C++ <c>CalcMoney(total, &amp;gold, &amp;silver, &amp;cooper)</c>).</summary>
    public void SetMoneyTotal(long total)
    {
        Cooper = (uint)(total % MoneyMultiply);
        Silver = (uint)(total / MoneyMultiply % MoneyMultiply);
        Gold = (uint)(total / MoneyMultiply / MoneyMultiply);
    }

    /// <summary>C++ <c>CTPlayer::UseMoney</c> — with <paramref name="commit"/> false it only checks
    /// affordability (returns false if the balance is short); true also deducts. A zero cost always passes.
    /// Then the gold titles are checked (<see cref="MoneyChecked"/>).</summary>
    public bool UseMoney(long cost, bool commit)
    {
        if (cost == 0) return true;
        long mine = MoneyTotal;
        if (mine < cost) return false;
        if (commit) SetMoneyTotal(mine - cost);
        MoneyChecked?.Invoke(this);
        return true;
    }

    /// <summary>C++ <c>CTPlayer::EarnMoney</c> — adds to the balance (returns false for a zero amount, as the
    /// C++ does).</summary>
    public bool EarnMoney(long amount)
    {
        if (amount == 0) return false;
        SetMoneyTotal(amount + MoneyTotal);
        MoneyChecked?.Invoke(this);
        return true;
    }

    /// <summary>The push-target containers in the C++ search order used by <c>CanPush</c>/<c>PushTItem</c>:
    /// <c>INVEN_DEFAULT</c> first, then the other non-equip bags (id-ordered). The equipped container is
    /// never a push target.</summary>
    public List<Inven> PushBags()
    {
        var bags = new List<Inven>();
        if (FindInven(Proto.InvenDefault) is { } def) bags.Add(def);
        foreach (var i in Invens.Where(i => i.InvenId != Proto.InvenDefault && i.InvenId != Proto.InvenEquip)
                                 .OrderBy(i => i.InvenId))
            bags.Add(i);
        return bags;
    }
}

/// <summary>The <c>TSaveChar</c> value columns the map doesn't otherwise use but must persist unchanged
/// (loaded from <c>TCHARTABLE</c>, written straight back) so the full-row save doesn't overwrite them with 0.
/// These back unported subsystems (guild-leave, spawn-return tracking, the stat-EFP level, last-destination).</summary>
public sealed class CharPersistExtras
{
    public byte GuildLeave { get; set; }
    public uint GuildLeaveTime { get; set; }
    public ushort SpawnId { get; set; }
    public ushort LastSpawnId { get; set; }
    public uint LastDestination { get; set; }
    public ushort TemptedMon { get; set; }
    public byte Aftermath { get; set; }
    public byte StatLevel { get; set; }
    public byte StatPoint { get; set; }
    public uint StatExp { get; set; }
}

/// <summary>One recent PvP result (C++ <c>TRECORDSET</c> in <c>m_vPvPRecent</c>): the other player, whether it was a win, the
/// points it moved, and when (map-clock ms).</summary>
public readonly record struct PvpRecord(string Name, bool Win, uint Point, long TimeMs, byte Class = 0, byte Level = 0,
    long UnixTime = 0);
