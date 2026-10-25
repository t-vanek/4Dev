namespace TMap.Protocol;

/// <summary>
/// Message IDs for every plane the map server touches. Values come from THIS repo's
/// <c>Lib/Own/TProtocol/include/ProtocolBase.h</c> plane bases and the per-plane offset headers
/// (<c>CSProtocol.h</c>, <c>MWProtocol.h</c>, <c>SSProtocol.h</c>, <c>DMProtocol.h</c>, <c>CTProtocol.h</c>).
/// They differ from the 4retro fork's IDs.
///
/// Naming trap (same as TWorldSvr.Net): on the map↔world (MW) plane the map is the plane <b>initiator</b>
/// that <b>sends <c>MW_*_ACK</c></b> and <b>receives <c>MW_*_REQ</c></b> — inverted from the usual
/// REQ/ACK intuition. On the client (CS) plane the client sends <c>CS_*_REQ</c> and the map replies
/// <c>CS_*_ACK</c> (and pushes some <c>_ACK</c> unsolicited).
/// </summary>
public static class Msg
{
    // ---- plane bases (ProtocolBase.h) ----
    public const ushort SM_BASE = 0x1581;   // system / batch / timer / AI (same-port server sessions)
    public const ushort MW_BASE = 0x9001;   // map <-> world
    public const ushort DM_BASE = 0x5891;   // map <-> DB manager
    public const ushort CS_LOGIN = 0x1987;  // login <-> client
    public const ushort CS_MAP = 0x5280;    // map <-> client
    public const ushort CT_CONTROL = 0x9301;// control <-> server
    public const ushort CS_CUSTOM = 0x3312;

    public const ushort CS_SC_CREATECODE_REQ = CS_MAP + 0x0300, CS_SC_CREATECODE_ACK = CS_MAP + 0x0301;
    public const ushort CS_SC_CHANGECODE_REQ = CS_MAP + 0x0302, CS_SC_CHANGECODE_ACK = CS_MAP + 0x0303;
    public const ushort CS_SC_DISABLECODE_REQ = CS_MAP + 0x0304, CS_SC_DISABLECODE_ACK = CS_MAP + 0x0305;
    public const ushort CS_SC_ENABLECODE_REQ = CS_MAP + 0x0306, CS_SC_ENABLECODE_ACK = CS_MAP + 0x0307;
    public const ushort CS_SC_UNLOCK_REQ = CS_MAP + 0x0308, CS_SC_UNLOCK_ACK = CS_MAP + 0x0309;
    public const ushort CS_SC_CHECKCODE_REQ = CS_MAP + 0x0310, CS_SC_CHECKCODE_ACK = CS_MAP + 0x0311;
    public const ushort CS_SC_GETLOCKTIME_REQ = CS_MAP + 0x0312, CS_SC_GETLOCKTIME_ACK = CS_MAP + 0x0313;
    public const ushort CS_REGGUILDCLOAK_REQ = CS_MAP + 0x0217, CS_REGGUILDCLOAK_ACK = CS_MAP + 0x0218;
    public const ushort CS_CUSTOMCLOAK_LIST_ACK = CS_MAP + 0x0395, CS_CUSTOMCLOAK_CHANGE_ACK = CS_MAP + 0x0396;
    public const ushort CS_CUSTOMCLOAK_LIST_REQ = CS_MAP + 0x0397, CS_CUSTOMCLOAK_CHANGE_REQ = CS_MAP + 0x0398;
    public const ushort CS_MEETINGROOM_REQ = CS_MAP + 0x024E, CS_MEETINGROOM_ACK = CS_MAP + 0x024F;
    public const ushort MW_MEETINGROOM_REQ = MW_BASE + 0x0172, MW_MEETINGROOM_ACK = MW_BASE + 0x0173;
    public const ushort SM_MEETINGROOM_REQ = SM_BASE + 0x002A, SM_MEETINGROOM_ACK = SM_BASE + 0x002B;

    // ================= client plane (CS_MAP) =================
    public const ushort CS_CONNECT_REQ = CS_MAP + 0x0001;
    public const ushort CS_CONNECT_ACK = CS_MAP + 0x0002;   // sent only on error (TCONNECT_RESULT)
    public const ushort CS_INVALIDCHAR_ACK = CS_MAP + 0x0003;
    public const ushort CS_ADDCONNECT_ACK = CS_MAP + 0x0004;
    public const ushort CS_CHARINFO_ACK = CS_MAP + 0x0005;  // full char + spawn
    public const ushort CS_ENTER_ACK = CS_MAP + 0x0006;     // a nearby PLAYER entered view
    public const ushort CS_LEAVE_ACK = CS_MAP + 0x0007;     // a nearby PLAYER left view
    public const ushort CS_ADDMON_ACK = CS_MAP + 0x0011;    // a nearby MONSTER entered view (appearance block)
    public const ushort CS_DELMON_ACK = CS_MAP + 0x0012;    // a nearby MONSTER left view (dwMonID + bExitMap)
    public const ushort CS_CONREADY_REQ = CS_MAP + 0x0008;  // client finished loading
    public const ushort CS_MOVE_REQ = CS_MAP + 0x0009;
    public const ushort CS_MOVE_ACK = CS_MAP + 0x000A;
    public const ushort CS_JUMP_REQ = CS_MAP + 0x000B;
    public const ushort CS_JUMP_ACK = CS_MAP + 0x000C;
    public const ushort CS_BLOCK_REQ = CS_MAP + 0x000D;
    public const ushort CS_BLOCK_ACK = CS_MAP + 0x000E;
    public const ushort CS_MONHOST_ACK = CS_MAP + 0x000F;  // monster retarget/host-change (dwMonID + bSet: TRUE to the new host, FALSE to others)
    public const ushort CS_MOVEITEM_REQ = CS_MAP + 0x0028;  // move/swap/split/merge/drop/equip an item
    public const ushort CS_MOVEITEM_ACK = CS_MAP + 0x0029;  // BYTE result (TMOVEITEM_RESULT)
    public const ushort CS_RESETCLOAK_ACK = CS_MAP + 0x0219; // dwCharID, bInvenID, bItemID — a cloak's guild mark gone
    public const ushort CS_UPDATEITEM_ACK = CS_MAP + 0x002A; // bInvenID + item block (count/slot changed)
    public const ushort CS_ADDITEM_ACK = CS_MAP + 0x002B;    // bInvenID + item block (new item in a slot)
    public const ushort CS_DELITEM_ACK = CS_MAP + 0x002C;    // bInvenID + bItemID (item removed)
    public const ushort CS_EQUIP_ACK = CS_MAP + 0x002D;      // dwCharID + count + equipped items (appearance)
    public const ushort CS_ITEMUSE_REQ = CS_MAP + 0x004A;    // use an item (potion): wTempID/bInven/bItem/wDelayGroup/bCount + targets
    public const ushort CS_ITEMUSE_ACK = CS_MAP + 0x004B;    // BYTE result, WORD delayGroup, BYTE kind, DWORD delay
    public const ushort CS_MONEY_ACK = CS_MAP + 0x0049;      // DWORD gold, silver, cooper (money changed)
    public const ushort CS_DURATIONREP_REQ = CS_MAP + 0x01BC;    // repair durability: bNeedCost/bType/bInven/bItem/wNpcID/bNpcInven/bNpcItem
    public const ushort CS_DURATIONREPCOST_ACK = CS_MAP + 0x01BD; // DWORD cost, BYTE discountRate (the bNeedCost quote)
    public const ushort CS_DURATIONREP_ACK = CS_MAP + 0x01BE;    // BYTE result, BYTE count, count×{bInven,bItem,DWORD duraMax,DWORD duraCur}
    public const ushort CS_CHGMODE_REQ = CS_MAP + 0x001C;
    public const ushort CS_CHGMODE_ACK = CS_MAP + 0x001D;
    public const ushort CS_CHARSTATINFO_REQ = CS_MAP + 0x00A3;  // client requests a char's computed stat sheet
    public const ushort CS_CHARSTATINFO_ACK = CS_MAP + 0x00A4;  // 31-field stats/AP/DP/speed reply
    public const ushort CS_HPMP_ACK = CS_MAP + 0x0022;
    public const ushort CS_DEFEND_REQ = CS_MAP + 0x0020;   // a hit landed on a target — apply damage
    public const ushort CS_DEFEND_ACK = CS_MAP + 0x0021;   // the hit result + per-target damage (broadcast)
    public const ushort CS_DIE_ACK = CS_MAP + 0x0025;      // a target died (dwID + bType)
    public const ushort CS_REVIVAL_REQ = CS_MAP + 0x0026;  // a dead player revives: fPos + bType (REVIVAL_TYPE)
    public const ushort CS_REVIVAL_ACK = CS_MAP + 0x0027;  // dwCharID + revival position (broadcast)
    public const ushort CS_SKILLBUY_REQ = CS_MAP + 0x0032; // learn/level-up a skill from an NPC (the skill window asks TDEF_SKILL_NPC 22047)
    public const ushort CS_NPCITEMLIST_REQ = CS_MAP + 0x0082; // an NPC's list (wNpcID) — the skill trainers' list is ported
    public const ushort CS_SKILLINIT_REQ = CS_MAP + 0x0189;   // reset scroll: wSkillID, bInvenID, bItemID
    public const ushort CS_SKILLINIT_ACK = CS_MAP + 0x018A;   // bResult, wSkillID
    public const ushort CS_SKILLLIST_ACK = CS_MAP + 0x018B;   // wSkillPoint, kind[4], bCount, {wSkillID, bLevel, dwTick}
    public const ushort CS_SKILLINITPOSSIBLE_REQ = CS_MAP + 0x018C; // bInvenID, bItemID
    public const ushort CS_SKILLINITPOSSIBLE_ACK = CS_MAP + 0x018D; // bCount, {wSkillID}
    public const ushort CS_SKILLBUY_ACK = CS_MAP + 0x0033; // a skill was learned/leveled: bRet,wSkillID,bLevel,Tick,gold,silver,copper,skillPoint,kind[4]
    // Every ordinary player attack in this build. Despite the _ACK suffix it is CLIENT -> SERVER: a late
    // addition (near the end of the table) that moves hit resolution server-side. The client routes a finished
    // player skill here instead of CS_DEFEND_REQ (TClientGame.cpp:14381); the server derives the attack power
    // and the hit roll itself and applies Defend to each listed target.
    public const ushort CS_FINISHSKILL_ACK = CS_MAP + 0x0377; // dwAttackID,dwID,bType,fPos,wSkillID,IsLinked(4),IsFake(4),wPartyID,bCount x{dwID,bType}

    // Client-authoritative monster movement. In this engine the server does NOT walk a hosted monster: the
    // AI script's Follow/Roam only announces INTENT to the host client (CS_MONACTION_ACK), and that client
    // then drives the monster and reports each step back here. The server validates (the sender must be the
    // monster's host), applies the position, runs the leash / go-home / roam-range checks, and relays the
    // batch to the OTHER nearby players - the host is excluded, it already knows where it put them.
    // Without this the monster is frozen server-side and can never reach the player, so combat never starts.
    public const ushort CS_MONMOVE_REQ = CS_MAP + 0x0014;  // wMonCount x {dwMonID,bObjType,bChannel,wMapID,fPos,wPitch,wDIR,bMouseDIR,bKeyDIR,bAction}
    public const ushort CS_MONMOVE_ACK = CS_MAP + 0x0015;  // wMonCount x {dwMonID,bType,fPos,wPitch,wDIR,bMouseDIR,bKeyDIR,bAction}

    // The action/animation gate in front of the whole attack+cast sequence (CSHandler.cpp:1233). The client
    // sends this when a swing or cast STARTS and waits for the ACK before it plays the animation and goes on
    // to CS_SKILLUSE/CS_DEFEND. The ACK is broadcast to the 3x3 block INCLUDING the actor (unlike CS_MOVE,
    // which excludes self) — without its own copy the caster never animates and the attack never proceeds.
    public const ushort CS_ACTION_REQ = CS_MAP + 0x002E;   // dwObjID,bObjType,bActionID,dwActID,dwAniID,bChannel,wMapID,wSkillID
    public const ushort CS_ACTION_ACK = CS_MAP + 0x002F;   // bResult,dwObjID,bObjType,bActionID,dwActID,dwAniID,wSkillID

    public const ushort CS_SKILLUSE_REQ = CS_MAP + 0x0034; // announce a skill/attack swing (caster cost + broadcast)
    public const ushort CS_SKILLUSE_ACK = CS_MAP + 0x0035; // the caster's attack-power payload (broadcast to near players)
    public const ushort CS_SKILLEND_REQ = CS_MAP + 0x0036; // client ends/cancels a maintained buff
    public const ushort CS_SKILLEND_ACK = CS_MAP + 0x0037; // a maintained buff ended (dwObjID + bObjType + wSkillID)
    // Map switch/gate (CSProtocol.h:1854-1871). Gate is server-authored (no client REQ). Note the
    // CS_SWITCHCHANGE_ACK body is bResult,switchId,opened — the header comment (switchId,opened) is stale.
    public const ushort CS_GATEADD_ACK = CS_MAP + 0x00EA;      // a gate entered view (dwGateID + bOpened)
    public const ushort CS_GATEDEL_ACK = CS_MAP + 0x00EB;      // a gate left view (dwGateID)
    public const ushort CS_GATECHANGE_ACK = CS_MAP + 0x00EC;   // a gate opened/closed (dwGateID + bOpened)
    public const ushort CS_SWITCHADD_ACK = CS_MAP + 0x00ED;    // a switch entered view (dwSwitchID + bOpened)
    public const ushort CS_SWITCHDEL_ACK = CS_MAP + 0x00EE;    // a switch left view (dwSwitchID)
    public const ushort CS_SWITCHCHANGE_REQ = CS_MAP + 0x00EF; // client activates a switch (dwSwitchID)
    public const ushort CS_SWITCHCHANGE_ACK = CS_MAP + 0x00F0; // a switch flipped (bResult + dwSwitchID + bOpened)
    public const ushort CS_LEVEL_ACK = CS_MAP + 0x0023;    // level changed (dwCharID, bLevel, bShowLevelUp)
    public const ushort CS_EXP_ACK = CS_MAP + 0x0024;      // exp changed (exp, prev-level floor, next-level ceiling, soul)
    public const ushort CS_MONITEMLIST_REQ = CS_MAP + 0x0088; // open a monster corpse's loot list
    public const ushort CS_MONITEMLIST_ACK = CS_MAP + 0x0089; // corpse money (+ items, deferred)
    public const ushort CS_MONMONEYTAKE_REQ = CS_MAP + 0x0114; // take the corpse's money
    public const ushort CS_MONITEMTAKE_REQ = CS_MAP + 0x008A;  // take a corpse item: bItemID/bInvenID/bSlotID
    public const ushort CS_MONITEMTAKE_ACK = CS_MAP + 0x008B;  // BYTE result (MONITEMTAKE_RESULT)
    public const ushort CS_MONITEMLOTTERY_REQ = CS_MAP + 0x008C;  // bAnswer, dwMonID, wItemID (MapService.LootLottery.cs)
    public const ushort CS_MONITEMLOTTERY_ACK = CS_MAP + 0x008D;  // dwMonID + the item block (no slot)
    public const ushort CS_GETITEM_ACK = CS_MAP + 0x0144;      // an item the player received (WrapPacketClient)
    public const ushort CS_MONACTION_ACK = CS_MAP + 0x0013;    // monster AI move/action (dest + action verb + target)
    public const ushort CS_MONATTACK_ACK = CS_MAP + 0x0031;    // monster announces an attack swing (attacker/target/skill)
    // The client-reported aggro bounds. The CLIENT decides when a player crosses a monster's
    // "look bound" / "attack bound" and tells the server, which fires the matching AI trigger (C++
    // OnCS_ENTERLB_REQ etc., CSHandler.cpp:1100-1218). All four share one layout:
    // dwCharID, dwTargetID, bTargetType, dwMonID, bChannel, wMapID.
    public const ushort CS_ENTERLB_REQ = CS_MAP + 0x0018;      // entered a monster's look bound   → AT_ENTERLB
    public const ushort CS_LEAVELB_REQ = CS_MAP + 0x0019;      // left it                          → AT_LEAVELB
    public const ushort CS_ENTERAB_REQ = CS_MAP + 0x001A;      // entered its attack bound         → AT_ENTERAB
    public const ushort CS_LEAVEAB_REQ = CS_MAP + 0x001B;      // left it                          → AT_LEAVEAB
    public const ushort CS_ITEMBUY_REQ = CS_MAP + 0x0084;      // buy an item from an NPC shop (wNpcID/dwQuestID/wItemID/bCount/bNpcInven/bNpcItem)
    public const ushort CS_ITEMBUY_ACK = CS_MAP + 0x0085;      // BYTE result (ITEMBUY_*), wItemID, then the three money tiers
    public const ushort CS_ITEMSELL_REQ = CS_MAP + 0x0086;     // sell an inventory item to an NPC (bInven/bPos/bCount/bNpcInven/bNpcItem)
    public const ushort CS_ITEMSELL_ACK = CS_MAP + 0x0087;     // BYTE result (ITEMSELL_*), then the three money tiers
    public const ushort CS_NPCTALK_REQ = CS_MAP + 0x00DD;      // open an NPC dialog (wNpcID)
    public const ushort CS_NPCTALK_ACK = CS_MAP + 0x00DE;      // dwQuestID (a talk-objective quest, else 0), wNpcID
    public const ushort CS_NPCITEMLIST_ACK = CS_MAP + 0x0083;  // an NPC's item list (wNpcID, TNPC_BOX, discountRate, count, WORD itemIds[]) — quest routing
    public const ushort CS_CHAPTERMSG_ACK = CS_MAP + 0x00E0;   // a quest chapter message: dwQuestID
    // ---- quests (CSProtocol.h 0x50-0x5B) ----
    public const ushort CS_QUESTADD_ACK = CS_MAP + 0x0050;         // a quest began: dwQuestID, bType
    public const ushort CS_QUESTLIST_ACK = CS_MAP + 0x0051;        // running-quest list (enter-time; deferred)
    public const ushort CS_QUESTEXEC_REQ = CS_MAP + 0x0052;        // accept/run a quest: dwQuestID, bRewardType, dwRewardID
    public const ushort CS_QUESTUPDATE_ACK = CS_MAP + 0x0053;      // a term advanced: dwQuestID, dwTermID, bType, bCount, bStatus
    public const ushort CS_QUESTCOMPLETE_ACK = CS_MAP + 0x0054;    // bResult (QR_*), dwQuestID, dwTermID, bType, dwDropID
    public const ushort CS_QUESTDROP_REQ = CS_MAP + 0x0055;        // abandon a quest: dwQuestID
    public const ushort CS_QUESTSTARTTIMER_ACK = CS_MAP + 0x0057;  // a timed quest's clock: dwQuestID, dwTick
    public const ushort CS_QUESTLIST_POSSIBLE_REQ = CS_MAP + 0x0059; // bCount + wNpcID[] — which quests can I get here?
    public const ushort CS_QUESTLIST_POSSIBLE_ACK = CS_MAP + 0x005A; // per-NPC runnable-quest list
    public const ushort CS_CHAT_REQ = CS_MAP + 0x0074;
    public const ushort CS_CHAT_ACK = CS_MAP + 0x0075;
    // The player cabinet (item warehouse). Put-in/take-out have NO dedicated ack — they refresh via
    // CS_CABINETITEMLIST_ACK + the usual bag acks (ADD/DEL/UPDATEITEM) + CS_MONEY_ACK.
    public const ushort CS_CABINETPUTIN_REQ = CS_MAP + 0x0076;   // bCabinetID,bInven,bItemID,bCount,bNpcInvenID,bNpcItemID
    public const ushort CS_CABINETTAKEOUT_REQ = CS_MAP + 0x0078;  // bCabinetID,dwStItemID,bCount,bInvenID,bItemID,bNpcInvenID,bNpcItemID
    public const ushort CS_CABINETLIST_REQ = CS_MAP + 0x007A;
    public const ushort CS_CABINETLIST_ACK = CS_MAP + 0x007B;    // BYTE count, { bCabinetID, bUse }
    public const ushort CS_CABINETITEMLIST_REQ = CS_MAP + 0x007C; // bCabinetID
    public const ushort CS_CABINETITEMLIST_ACK = CS_MAP + 0x007D; // bResult [, bCabinetID, DWORD count, { dwStItemID, item-wrap(addItemId=0) }]
    public const ushort CS_CABINETOPEN_REQ = CS_MAP + 0x007E;    // bCabinetID
    public const ushort CS_CABINETOPEN_ACK = CS_MAP + 0x007F;    // bResult, bCabinetID
    // Party. Membership management is world-authoritative (relays, deferred); this id is the
    // map-local party-loot notification broadcast.
    public const ushort CS_PARTYITEMTAKE_ACK = CS_MAP + 0x0142;  // dwCharID + item block (a party member looted)
    // Player-to-player deal (trade). Same-map, in-memory two-party state machine.
    public const ushort CS_DEALITEMASK_REQ = CS_MAP + 0x010C;    // strTarget (invite)
    public const ushort CS_DEALITEMASK_ACK = CS_MAP + 0x010D;    // strInviter (shown to the target)
    public const ushort CS_DEALITEMRLY_REQ = CS_MAP + 0x010E;    // bReply, strInviter (accept/decline)
    public const ushort CS_DEALITEMSTART_ACK = CS_MAP + 0x010F;  // strTarget (window opens; to both)
    public const ushort CS_DEALITEMADD_REQ = CS_MAP + 0x0110;    // gold,silver,cooper, count, {bInvenID,bItemID}
    public const ushort CS_DEALITEMADD_ACK = CS_MAP + 0x0111;    // gold,silver,cooper, count, {item block} (to partner)
    public const ushort CS_DEALITEM_REQ = CS_MAP + 0x0112;       // bOkey (confirm=1 / cancel=0)
    public const ushort CS_DEALITEMEND_ACK = CS_MAP + 0x0113;    // bResult (DEALITEM_RESULT), strTarget
    // Personal store (player vendor). Same-map, in-memory; offered items stay in the seller's bag.
    public const ushort CS_STOREOPEN_REQ = CS_MAP + 0x0119;      // strName, count, {gold,silver,cooper,credits,inven,itemId,count}
    public const ushort CS_STOREOPEN_ACK = CS_MAP + 0x011A;      // bResult, dwCharID, strName (broadcast to neighbors)
    public const ushort CS_STORECLOSE_REQ = CS_MAP + 0x011B;
    public const ushort CS_STORECLOSE_ACK = CS_MAP + 0x011C;     // bResult, dwCharID (broadcast to neighbors + self)
    public const ushort CS_STOREITEMLIST_REQ = CS_MAP + 0x011D;  // strTarget (browse a seller's store)
    public const ushort CS_STOREITEMLIST_ACK = CS_MAP + 0x011E;  // dwCharID, strName, count, { slotKey, credits, gold, silver, cooper, item block }
    public const ushort CS_STOREITEMBUY_REQ = CS_MAP + 0x011F;   // strTarget, bItem(index), bItemCount
    public const ushort CS_STOREITEMBUY_ACK = CS_MAP + 0x0120;   // bResult, wItemID, bCount (to buyer)
    public const ushort CS_STOREITEMSELL_ACK = CS_MAP + 0x0121;  // bItem(index), bCount (to seller when sold)
    public const ushort CS_REGION_REQ = CS_MAP + 0x00F1;
    public const ushort CS_SYSTEMMSG_ACK = CS_MAP + 0x01D1;
    public const ushort CS_QUESTENDTIMER_REQ = CS_MAP + 0x0058;     // dwQuestID (the client's timer ran out)
    public const ushort CS_CANCELACTION_REQ = CS_MAP + 0x00A5;      // dwObjID, bObjType
    public const ushort CS_CANCELACTION_ACK = CS_MAP + 0x00A6;      // dwObjID, bObjType
    public const ushort CS_CANCELSKILL_REQ = CS_MAP + 0x0146;       // bType, dwID, wSkillID
    public const ushort CS_LOOPSKILL_REQ = CS_MAP + 0x00F2;         // dwAttackID bAttackType bChannel wMapID wSkillID fPos×3 bCount {dwID bType bIsTarget}
    public const ushort CS_LOOPSKILL_ACK = CS_MAP + 0x00F3;         // bResult … (MapService.LoopSkill.cs)
    public const ushort CS_GETTARGET_REQ = CS_MAP + 0x014E;         // dwCharID (whose target)
    public const ushort CS_GETTARGET_ACK = CS_MAP + 0x014F;         // dwTargetID, bTargetType
    public const ushort CS_GETTARGETANS_REQ = CS_MAP + 0x0150;      // dwAskerID, dwTargetID, bTargetType
    public const ushort CS_GETTARGETANS_ACK = CS_MAP + 0x0151;      // dwAskerID
    public const ushort CS_MONITEMTAKEALL_REQ = CS_MAP + 0x0152;    // dwMonID
    public const ushort CS_HELMETHIDE_REQ = CS_MAP + 0x018E;        // bHide
    public const ushort CS_HELMETHIDE_ACK = CS_MAP + 0x018F;        // dwCharID, bHide
    public const ushort CS_ACTEND_REQ = CS_MAP + 0x01C3;            // (a no-op)
    public const ushort CS_COMMENT_REQ = CS_MAP + 0x01E0;           // strComment
    public const ushort CS_COMMENT_ACK = CS_MAP + 0x01E1;           // dwCharID, strComment
    public const ushort CS_QUESTPOSEXEC_REQ = CS_MAP + 0x0354;      // dwQuestID, dwTermID
    // The tournament, batch S6e (CSProtocol.h / MWProtocol.h; MapService.Tournament.cs).
    public const ushort CS_TOURNAMENTAPPLYINFO_REQ = CS_MAP + 0x0227;
    public const ushort CS_TOURNAMENTAPPLYINFO_ACK = CS_MAP + 0x0228;
    public const ushort CS_TOURNAMENTAPPLY_REQ = CS_MAP + 0x0229;    // bEntryID, strHWID
    public const ushort CS_TOURNAMENTAPPLY_ACK = CS_MAP + 0x022A;    // bResult, bEntryID
    public const ushort CS_TOURNAMENTJOINLIST_ACK = CS_MAP + 0x022C;
    public const ushort CS_TOURNAMENTPARTYLIST_REQ = CS_MAP + 0x022D;
    public const ushort CS_TOURNAMENTPARTYLIST_ACK = CS_MAP + 0x022E;
    public const ushort CS_TOURNAMENTPARTYADD_REQ = CS_MAP + 0x022F;
    public const ushort CS_TOURNAMENTPARTYADD_ACK = CS_MAP + 0x0230;
    public const ushort CS_TOURNAMENTMATCHLIST_ACK = CS_MAP + 0x0232;
    public const ushort CS_TOURNAMENTEVENTLIST_REQ = CS_MAP + 0x0233;
    public const ushort CS_TOURNAMENTEVENTLIST_ACK = CS_MAP + 0x0234;
    public const ushort CS_TOURNAMENTEVENTINFO_REQ = CS_MAP + 0x0235;
    public const ushort CS_TOURNAMENTEVENTINFO_ACK = CS_MAP + 0x0236;
    public const ushort CS_TOURNAMENTEVENTJOIN_REQ = CS_MAP + 0x0237;
    public const ushort CS_TOURNAMENTEVENTJOIN_ACK = CS_MAP + 0x0238;
    public const ushort CS_TOURNAMENTCHEER_REQ = CS_MAP + 0x0239;    // bInven, bItem, dwTargetID
    public const ushort CS_TOURNAMENTCHEER_ACK = CS_MAP + 0x023A;    // dwCharID, dwTargetID
    public const ushort CS_TOURNAMENTENTER_ACK = CS_MAP + 0x023B;    // wDefKill, wAtkKill, bCount, the fighters
    public const ushort CS_TOURNAMENTRESULT_ACK = CS_MAP + 0x023C;   // bStep, bRet, dwWin, dwLose, dwBlueHide, dwRedHide
    public const ushort CS_TOURNAMENTPARTYDEL_REQ = CS_MAP + 0x023D;
    public const ushort CS_TOURNAMENTSCHEDULE_REQ = CS_MAP + 0x0241;
    public const ushort CS_TOURNAMENTSCHEDULE_ACK = CS_MAP + 0x0242;
    public const ushort CS_TOURNAMENTSELECTWINNER_ACK = CS_MAP + 0x0243; // bBlue, bRed — the dice of a drawn match
    public const ushort CS_EXITTOURNAMETMAINTAIN_ACK = CS_MAP + 0x0280; // the buffs given back
    public const ushort MW_TOURNAMENTENTERGATE_ACK = MW_BASE + 0x015B; // dwCharID, dwKey, dwMoney, bEnter — a bet ticket at the lounge gate
    public const ushort MW_TOURNAMENTRESULT_REQ = MW_BASE + 0x015C;  // the world's results
    public const ushort MW_TOURNAMENTRESULT_ACK = MW_BASE + 0x015D;  // a match decided on the map
    public const ushort MW_TOURNAMENTENABLE_REQ = MW_BASE + 0x0144;  // bGroup, bStep, dwPeriod, dlNextStep
    public const ushort MW_TOURNAMENT_REQ = MW_BASE + 0x0145;        // dwCharID, dwKey, wProtocol, the answer
    public const ushort MW_TOURNAMENT_ACK = MW_BASE + 0x0146;        // dwCharID, dwKey, wProtocol, the request
    public const ushort MW_TOURNAMENTAPPLYINFO_REQ = MW_BASE + 0x0147;
    public const ushort MW_TOURNAMENTAPPLYINFO_ACK = MW_BASE + 0x0148;
    public const ushort MW_TOURNAMENTAPPLY_REQ = MW_BASE + 0x0149;
    public const ushort MW_TOURNAMENTAPPLY_ACK = MW_BASE + 0x014A;
    public const ushort MW_TOURNAMENTJOINLIST_REQ = MW_BASE + 0x014B;
    public const ushort MW_TOURNAMENTJOINLIST_ACK = MW_BASE + 0x014C;
    public const ushort MW_TOURNAMENTPARTYLIST_REQ = MW_BASE + 0x014D;
    public const ushort MW_TOURNAMENTPARTYLIST_ACK = MW_BASE + 0x014E;
    public const ushort MW_TOURNAMENTPARTYADD_REQ = MW_BASE + 0x014F;
    public const ushort MW_TOURNAMENTPARTYADD_ACK = MW_BASE + 0x0150;
    public const ushort MW_TOURNAMENTMATCHLIST_REQ = MW_BASE + 0x0151;
    public const ushort MW_TOURNAMENTMATCHLIST_ACK = MW_BASE + 0x0152;
    public const ushort MW_TOURNAMENTEVENTLIST_REQ = MW_BASE + 0x0153;
    public const ushort MW_TOURNAMENTEVENTLIST_ACK = MW_BASE + 0x0154;
    public const ushort MW_TOURNAMENTEVENTINFO_REQ = MW_BASE + 0x0155;
    public const ushort MW_TOURNAMENTEVENTINFO_ACK = MW_BASE + 0x0156;
    public const ushort MW_TOURNAMENTEVENTJOIN_REQ = MW_BASE + 0x0157;
    public const ushort MW_TOURNAMENTEVENTJOIN_ACK = MW_BASE + 0x0158;
    public const ushort MW_TOURNAMENTMATCH_REQ = MW_BASE + 0x0159;   // the bracket
    public const ushort MW_TOURNAMENTINFO_REQ = MW_BASE + 0x015A;    // the entries and the step
    public const ushort MW_TOURNAMENTPARTYDEL_ACK = MW_BASE + 0x015E;
    public const ushort MW_TOURNAMENTSCHEDULE_REQ = MW_BASE + 0x015F;
    public const ushort MW_TOURNAMENTSCHEDULE_ACK = MW_BASE + 0x0160;
    public const ushort MW_TOURNAMENTBATPOINT_REQ = MW_BASE + 0x0161; // dwCharID, strName, dwAmount
    // The arena, batch S6d (CSProtocol.h / MWProtocol.h; MapService.Arena.cs).
    public const ushort CS_ARENA_REQ = CS_MAP + 0x0250;              // bCommand (0 join, 1 give up), wNpcID, wArenaID
    public const ushort CS_ARENA_ACK = CS_MAP + 0x0251;              // bResult (ARENA_RESULT)
    public const ushort CS_ARENATEAM_ACK = CS_MAP + 0x0252;          // wArenaID, bTeam (0 = out), bCount, dwCharID...
    public const ushort CS_ARENAINFO_REQ = CS_MAP + 0x039D;          // bArenaID
    public const ushort CS_ARENAINFO_ACK = CS_MAP + 0x039E;
    public const ushort CS_ARENARANKING_REQ = CS_MAP + 0x0540;
    public const ushort CS_ARENARANKING_ACK = CS_MAP + 0x0541;
    public const ushort CS_RESETCOOLTIME_ACK = CS_MAP + 0x01C2;      // wExceptSkill
    public const ushort MW_ARENAJOIN_ACK = MW_BASE + 0x0174;         // dwCharID, dwKey, bJoin, dwCount, dwCharID...
    // Heroes and the warlord's message, batch S6c (CSProtocol.h / MWProtocol.h; MapService.Heroes.cs).
    public const ushort CS_HEROSELECT_REQ = CS_MAP + 0x01CD;         // wBattleZoneID, strHero
    public const ushort CS_HEROSELECT_ACK = CS_MAP + 0x01CE;         // bResult (HERO_SELECT_RESULT)
    public const ushort CS_HEROLIST_REQ = CS_MAP + 0x01CF;
    public const ushort CS_HEROLIST_ACK = CS_MAP + 0x01D0;           // the forts' and castles' (wLocal, strHero)
    public const ushort CS_WARLORDSAY_REQ = CS_MAP + 0x021F;         // bType, strSay
    public const ushort MW_HEROSELECT_ACK = MW_BASE + 0x011C;        // wZone, strHero, tHero
    public const ushort MW_WARLORDSAY_ACK = MW_BASE + 0x0142;        // bType, bRankMonth, dwCharID, strSay
    // The kingdom balance, batch S6b (CSProtocol.h / MWProtocol.h; MapService.CountryBalance.cs).
    public const ushort CS_WARCOUNTRYBALANCE_REQ = CS_MAP + 0x024C;
    public const ushort CS_WARCOUNTRYBALANCE_ACK = CS_MAP + 0x024D;  // dwCountD, dwCountC
    public const ushort MW_WARCOUNTRYBALANCE_REQ = MW_BASE + 0x0170; // dwCharID, dwKey, dwCountD, dwCountC, bGap
    public const ushort MW_WARCOUNTRYBALANCE_ACK = MW_BASE + 0x0171;
    public const ushort MW_RECALCCOUNTRYBALANCE_ACK = MW_BASE + 0x021E; // bCountD, bCountC
    // Rock-paper-scissors, batch S5c (CSProtocol.h / MWProtocol.h; MapService.Rps.cs).
    public const ushort CS_RPSSTART_REQ = CS_MAP + 0x0246;           // bType, bInven, bItemID
    public const ushort CS_RPSSTART_ACK = CS_MAP + 0x0247;           // bResult, dwGold, dwSilver, dwCooper
    public const ushort CS_RPSGAME_REQ = CS_MAP + 0x0248;            // bRPS (3 = cash out)
    public const ushort CS_RPSGAME_ACK = CS_MAP + 0x0249;            // bWinCount, bPlayerRPS, bNpcRPS
    public const ushort MW_RPSGAME_REQ = MW_BASE + 0x016B;
    public const ushort MW_RPSGAME_ACK = MW_BASE + 0x016C;
    public const ushort MW_RPSGAMECHANGE_REQ = MW_BASE + 0x016D;
    // The soul lottery, batch S5b (CSProtocol.h; MapService.SoulLottery.cs).
    public const ushort CS_SOULUP_ACK = CS_MAP + 0x027A;             // dwCharID — the soul bar is full
    public const ushort CS_SOULLOTTERY_REQ = CS_MAP + 0x027B;
    public const ushort CS_SOULLOTTERY_ACK = CS_MAP + 0x027C;        // dwNumber1..4, dwMoney
    public const ushort CS_SOULLOTTERY_TRIGGER_REQ = CS_MAP + 0x027D;
    public const ushort CS_SOULLOTTERY_TRIGGER_ACK = CS_MAP + 0x027E; // dwNumber, dwMoney, bWon
    // The gamble and the magic shop, batch S5a (CSProtocol.h; MapService.Gamble.cs).
    public const ushort CS_GAMBLECHECK_REQ = CS_MAP + 0x01A4;        // bInven, bItem, wNpcID
    public const ushort CS_GAMBLECHECK_ACK = CS_MAP + 0x01A5;        // bResult, bInven, bItem, dwMoney, bDiscountRate
    public const ushort CS_GAMBLEOPEN_REQ = CS_MAP + 0x01A6;         // bInven, bItem, wNpcID, bNpcInvenID, bNpcItemID
    public const ushort CS_GAMBLEOPEN_ACK = CS_MAP + 0x01A7;         // bResult, dwGold, dwSilver, dwCooper, bInven, bItem [, item]
    public const ushort CS_MAGICITEMBUY_REQ = CS_MAP + 0x0256;       // wNpcID, dwQItemID, bCount, bNpcInvenID, bNpcItemID
    public const ushort CS_MAGICITEMBUY_ACK = CS_MAP + 0x0257;       // bResult, dwGold, dwSilver, dwCooper
    // The auction house, batch S4 (CSProtocol.h:3327; MapService.Auction.cs).
    public const ushort CS_AUCTIONREG_REQ = CS_MAP + 0x01FD;
    public const ushort CS_AUCTIONREG_ACK = CS_MAP + 0x01FE;
    public const ushort CS_AUCTIONINTEREST_REQ = CS_MAP + 0x01FF;
    public const ushort CS_AUCTIONINTEREST_ACK = CS_MAP + 0x0200;
    public const ushort CS_AUCTIONINTERESTLIST_REQ = CS_MAP + 0x0201;
    public const ushort CS_AUCTIONINTERESTLIST_ACK = CS_MAP + 0x0202;
    public const ushort CS_AUCTIONBIDLIST_REQ = CS_MAP + 0x0203;
    public const ushort CS_AUCTIONBIDLIST_ACK = CS_MAP + 0x0204;
    public const ushort CS_AUCTIONREGCANCEL_REQ = CS_MAP + 0x0205;
    public const ushort CS_AUCTIONREGCANCEL_ACK = CS_MAP + 0x0206;
    public const ushort CS_AUCTIONBID_REQ = CS_MAP + 0x0207;
    public const ushort CS_AUCTIONBID_ACK = CS_MAP + 0x0208;
    public const ushort CS_AUCTIONBUYDIRECT_REQ = CS_MAP + 0x0209;
    public const ushort CS_AUCTIONBUYDIRECT_ACK = CS_MAP + 0x020A;
    public const ushort CS_AUCTIONFIND_REQ = CS_MAP + 0x020B;
    public const ushort CS_AUCTIONFIND_ACK = CS_MAP + 0x020C;
    public const ushort CS_AUCTIONREGLIST_REQ = CS_MAP + 0x020D;
    public const ushort CS_AUCTIONREGLIST_ACK = CS_MAP + 0x020E;
    // Corps and party member recall, batch S2/S3 (CSProtocol.h; MapService.Corps.cs).
    public const ushort CS_CORPSASK_REQ = CS_MAP + 0x00AC;
    public const ushort CS_CORPSASK_ACK = CS_MAP + 0x00AD;
    public const ushort CS_CORPSREPLY_REQ = CS_MAP + 0x00AE;
    public const ushort CS_CORPSREPLY_ACK = CS_MAP + 0x00AF;
    public const ushort CS_CORPSLEAVE_REQ = CS_MAP + 0x00B0;
    public const ushort CS_CORPSJOIN_ACK = CS_MAP + 0x00B1;
    public const ushort CS_CORPSCMD_REQ = CS_MAP + 0x00B2;
    public const ushort CS_CORPSCMD_ACK = CS_MAP + 0x00B3;
    public const ushort CS_CORPSENEMYLIST_REQ = CS_MAP + 0x00B4;
    public const ushort CS_CORPSENEMYLIST_ACK = CS_MAP + 0x00B5;
    public const ushort CS_MOVECORPSENEMY_REQ = CS_MAP + 0x00B6;
    public const ushort CS_MOVECORPSENEMY_ACK = CS_MAP + 0x00B7;
    public const ushort CS_MOVECORPSUNIT_REQ = CS_MAP + 0x00B8;
    public const ushort CS_MOVECORPSUNIT_ACK = CS_MAP + 0x00B9;
    public const ushort CS_ADDCORPSENEMY_REQ = CS_MAP + 0x00BA;
    public const ushort CS_ADDCORPSENEMY_ACK = CS_MAP + 0x00BB;
    public const ushort CS_DELCORPSENEMY_REQ = CS_MAP + 0x00BC;
    public const ushort CS_DELCORPSENEMY_ACK = CS_MAP + 0x00BD;
    public const ushort CS_ADDCORPSUNIT_ACK = CS_MAP + 0x00BE;
    public const ushort CS_DELCORPSUNIT_ACK = CS_MAP + 0x00BF;
    public const ushort CS_ADDSQUAD_ACK = CS_MAP + 0x00C0;
    public const ushort CS_DELSQUAD_ACK = CS_MAP + 0x00C1;
    public const ushort CS_CORPSHP_REQ = CS_MAP + 0x00FD;
    public const ushort CS_CORPSHP_ACK = CS_MAP + 0x00FE;
    public const ushort CS_CHGCORPSCOMMANDER_REQ = CS_MAP + 0x0103;
    public const ushort CS_CHGCORPSCOMMANDER_ACK = CS_MAP + 0x0104;
    public const ushort CS_REPORTENEMYLIST_ACK = CS_MAP + 0x0105;
    public const ushort CS_CHGSQUADCHIEF_ACK = CS_MAP + 0x0106;
    public const ushort CS_PARTYMEMBERRECALL_REQ = CS_MAP + 0x0190;
    public const ushort CS_PARTYMEMBERRECALL_ACK = CS_MAP + 0x0191;
    public const ushort CS_PARTYMEMBERRECALLANS_REQ = CS_MAP + 0x01B5;
    public const ushort CS_PARTYMEMBERRECALLANS_ACK = CS_MAP + 0x01B6;
    // Friends, the protected list and the messenger, batch S1 (CSProtocol.h; MapService.Social.cs).
    public const ushort CS_FRIENDASK_REQ = CS_MAP + 0x008E;          // strTarget
    public const ushort CS_FRIENDASK_ACK = CS_MAP + 0x008F;          // strInviter
    public const ushort CS_FRIENDREPLY_REQ = CS_MAP + 0x0090;        // strInviter, bReply
    public const ushort CS_FRIENDADD_ACK = CS_MAP + 0x0091;          // bResult, dwFriendID, strName, bLevel, bGroup, bClass, dwRegion
    public const ushort CS_FRIENDERASE_REQ = CS_MAP + 0x0092;        // dwTarget
    public const ushort CS_FRIENDERASE_ACK = CS_MAP + 0x0093;        // bResult, dwFriendID
    public const ushort CS_FRIENDLIST_ACK = CS_MAP + 0x0094;
    public const ushort CS_FRIENDCONNECTION_ACK = CS_MAP + 0x0095;   // bResult, strName, dwRegion
    public const ushort CS_FRIENDREGION_ACK = CS_MAP + 0x0096;       // strName, dwRegion
    public const ushort CS_FRIENDGROUPMAKE_REQ = CS_MAP + 0x00C2;    // bGroup, strName
    public const ushort CS_FRIENDGROUPMAKE_ACK = CS_MAP + 0x00C3;    // bRet, bGroup, strName
    public const ushort CS_FRIENDGROUPDELETE_REQ = CS_MAP + 0x00C4;  // bGroup
    public const ushort CS_FRIENDGROUPDELETE_ACK = CS_MAP + 0x00C5;  // bRet, bGroup
    public const ushort CS_FRIENDGROUPCHANGE_REQ = CS_MAP + 0x00C6;  // dwFriend, bGroup
    public const ushort CS_FRIENDGROUPCHANGE_ACK = CS_MAP + 0x00C7;  // bRet, bGroup, dwFriend
    public const ushort CS_FRIENDGROUPNAME_REQ = CS_MAP + 0x00C8;    // bGroup, strName
    public const ushort CS_FRIENDGROUPNAME_ACK = CS_MAP + 0x00C9;    // bRet, bGroup, strName
    public const ushort CS_TMSSEND_REQ = CS_MAP + 0x00CA;            // dwTMS, strMessage
    public const ushort CS_TMSRECV_ACK = CS_MAP + 0x00CB;            // dwTMS, strSender, strMessage
    public const ushort CS_TMSINVITE_REQ = CS_MAP + 0x00CC;          // dwTMS (0 = new), bCount, dwTarget × count
    public const ushort CS_TMSINVITE_ACK = CS_MAP + 0x00CD;          // dwInviter, dwTMS, bCount, (dwCharID, strName, bClass, bLevel) × count
    public const ushort CS_TMSOUT_REQ = CS_MAP + 0x00D0;             // dwTMS
    public const ushort CS_TMSOUT_ACK = CS_MAP + 0x00D1;             // dwTMS, strMember
    public const ushort CS_PROTECTEDLIST_ACK = CS_MAP + 0x00F6;      // bCount, (dwCharID, strName, bOption) × count
    public const ushort CS_PROTECTEDADD_REQ = CS_MAP + 0x00F7;       // strName
    public const ushort CS_PROTECTEDADD_ACK = CS_MAP + 0x00F8;       // bResult, dwCharID, strName, bOption
    public const ushort CS_PROTECTEDERASE_REQ = CS_MAP + 0x00F9;     // strName
    public const ushort CS_PROTECTEDERASE_ACK = CS_MAP + 0x00FA;     // bResult, strName
    public const ushort CS_PROTECTEDOPTION_REQ = CS_MAP + 0x01F2;    // dwTarget, bOption
    public const ushort CS_FRIENDLIST_REQ = CS_MAP + 0x01F3;
    public const ushort CS_SOULMATE_ACK = CS_MAP + 0x019B;           // dwSoulmate, strSoulmate
    public const ushort CS_SOULMATESEARCH_REQ = CS_MAP + 0x019C;     // bNpcInvenID, bNpcItemID
    public const ushort CS_SOULMATESEARCH_ACK = CS_MAP + 0x019D;     // bResult, dwTargetID, strName, dwRegion, dwGold, dwSilver, dwCooper
    public const ushort CS_SOULMATEREG_REQ = CS_MAP + 0x019E;        // strName, bNpcInvenID, bNpcItemID
    public const ushort CS_SOULMATEREG_ACK = CS_MAP + 0x019F;        // bResult, dwCharID, strName, dwRegion, dwGold, dwSilver, dwCooper
    public const ushort CS_SOULMATEEND_REQ = CS_MAP + 0x01A0;
    public const ushort CS_SOULMATEEND_ACK = CS_MAP + 0x01A1;        // bResult
    public const ushort CS_SOULMATEREGREADY_REQ = CS_MAP + 0x01A2;   // strName, bNpcInvenID, bNpcItemID
    public const ushort CS_SOULMATEREGREADY_ACK = CS_MAP + 0x01A3;   // bResult, strName, bNpcInvenID, bNpcItemID
    // The guild relay, batch G1 (CSProtocol.h; MapService.Guild.cs).
    public const ushort CS_GUILDESTABLISH_REQ = CS_MAP + 0x0060;
    public const ushort CS_GUILDESTABLISH_ACK = CS_MAP + 0x0061;
    public const ushort CS_GUILDDISORGANIZATION_REQ = CS_MAP + 0x0062;
    public const ushort CS_GUILDDISORGANIZATION_ACK = CS_MAP + 0x0063;
    public const ushort CS_GUILDINVITE_REQ = CS_MAP + 0x0064;
    public const ushort CS_GUILDINVITE_ACK = CS_MAP + 0x0065;
    public const ushort CS_GUILDINVITEANSWER_REQ = CS_MAP + 0x0066;
    public const ushort CS_GUILDJOIN_ACK = CS_MAP + 0x0067;
    public const ushort CS_GUILDLEAVE_REQ = CS_MAP + 0x0068;
    public const ushort CS_GUILDKICKOUT_REQ = CS_MAP + 0x0069;
    public const ushort CS_GUILDLEAVE_ACK = CS_MAP + 0x006A;
    public const ushort CS_GUILDDUTY_REQ = CS_MAP + 0x006B;
    public const ushort CS_GUILDDUTY_ACK = CS_MAP + 0x006C;
    public const ushort CS_GUILDMEMBERLIST_REQ = CS_MAP + 0x006D;
    public const ushort CS_GUILDMEMBERLIST_ACK = CS_MAP + 0x006E;
    public const ushort CS_GUILDATTR_ACK = CS_MAP + 0x006F;
    public const ushort CS_GUILDPEER_REQ = CS_MAP + 0x0070;
    public const ushort CS_GUILDPEER_ACK = CS_MAP + 0x0071;
    public const ushort CS_GUILDINFO_REQ = CS_MAP + 0x0072;
    public const ushort CS_GUILDINFO_ACK = CS_MAP + 0x0073;
    public const ushort CS_GUILDSKILLACTION_REQ = CS_MAP + 0x039A; // bAction bCount wParam[bCount] (GS_BUY: wSkillID; GS_RENEW: wSkillID)
    public const ushort CS_GUILDSKILLACTION_ACK = CS_MAP + 0x039B; // bAction bCount wParam[bCount]
    public const ushort CS_GUILDSKILLUPDATE_ACK = CS_MAP + 0x039C;
    // The guild relay, batch G2 (MapService.GuildBoard.cs).
    public const ushort CS_GUILDLOCALRETURN_REQ = CS_MAP + 0x0157;
    public const ushort CS_GUILDLOCALRETURN_ACK = CS_MAP + 0x0158;
    public const ushort CS_GUILDCABINETLIST_REQ = CS_MAP + 0x0159;
    public const ushort CS_GUILDCABINETLIST_ACK = CS_MAP + 0x015A;
    public const ushort CS_GUILDCABINETPUTIN_REQ = CS_MAP + 0x015B;
    public const ushort CS_GUILDCABINETPUTIN_ACK = CS_MAP + 0x015C;
    public const ushort CS_GUILDCABINETTAKEOUT_REQ = CS_MAP + 0x015D;
    public const ushort CS_GUILDCABINETTAKEOUT_ACK = CS_MAP + 0x015E;
    public const ushort CS_GUILDCONTRIBUTION_REQ = CS_MAP + 0x015F;
    public const ushort CS_GUILDCONTRIBUTION_ACK = CS_MAP + 0x0160;
    public const ushort CS_GUILDARTICLELIST_REQ = CS_MAP + 0x0161;
    public const ushort CS_GUILDARTICLELIST_ACK = CS_MAP + 0x0162;
    public const ushort CS_GUILDARTICLEADD_REQ = CS_MAP + 0x0163;
    public const ushort CS_GUILDARTICLEADD_ACK = CS_MAP + 0x0164;
    public const ushort CS_GUILDARTICLEDEL_REQ = CS_MAP + 0x0165;
    public const ushort CS_GUILDARTICLEDEL_ACK = CS_MAP + 0x0166;
    public const ushort CS_GUILDFAME_REQ = CS_MAP + 0x0167;
    public const ushort CS_GUILDFAME_ACK = CS_MAP + 0x0168;
    public const ushort CS_GUILDARTICLEUPDATE_REQ = CS_MAP + 0x0169;
    public const ushort CS_GUILDARTICLEUPDATE_ACK = CS_MAP + 0x016A;
    public const ushort CS_GUILDWANTEDADD_REQ = CS_MAP + 0x016B;
    public const ushort CS_GUILDWANTEDADD_ACK = CS_MAP + 0x016C;
    public const ushort CS_GUILDWANTEDDEL_REQ = CS_MAP + 0x016D;
    public const ushort CS_GUILDWANTEDDEL_ACK = CS_MAP + 0x016E;
    public const ushort CS_GUILDWANTEDLIST_REQ = CS_MAP + 0x016F;
    public const ushort CS_GUILDWANTEDLIST_ACK = CS_MAP + 0x0170;
    public const ushort CS_GUILDVOLUNTEERING_REQ = CS_MAP + 0x0171;
    public const ushort CS_GUILDVOLUNTEERING_ACK = CS_MAP + 0x0172;
    public const ushort CS_GUILDVOLUNTEERINGDEL_REQ = CS_MAP + 0x0173;
    public const ushort CS_GUILDVOLUNTEERINGDEL_ACK = CS_MAP + 0x0174;
    public const ushort CS_GUILDVOLUNTEERLIST_REQ = CS_MAP + 0x0175;
    public const ushort CS_GUILDVOLUNTEERLIST_ACK = CS_MAP + 0x0176;
    public const ushort CS_GUILDVOLUNTEERREPLY_REQ = CS_MAP + 0x0177;
    public const ushort CS_GUILDVOLUNTEERREPLY_ACK = CS_MAP + 0x0178;
    public const ushort CS_GUILDPOINTLOG_REQ = CS_MAP + 0x01E3;
    public const ushort CS_GUILDPOINTLOG_ACK = CS_MAP + 0x01E4;
    public const ushort CS_GUILDPVPRECORD_REQ = CS_MAP + 0x01E7;
    public const ushort CS_GUILDPVPRECORD_ACK = CS_MAP + 0x01E8;
    // guilds G3: mercenaries (tactics), PvP point rewards, the castle guards' shop
    public const ushort CS_GUILDTACTICSWANTEDADD_REQ = CS_MAP + 0x0179;
    public const ushort CS_GUILDTACTICSWANTEDADD_ACK = CS_MAP + 0x017A;
    public const ushort CS_GUILDTACTICSWANTEDDEL_REQ = CS_MAP + 0x017B;
    public const ushort CS_GUILDTACTICSWANTEDDEL_ACK = CS_MAP + 0x017C;
    public const ushort CS_GUILDTACTICSWANTEDLIST_REQ = CS_MAP + 0x017D;
    public const ushort CS_GUILDTACTICSWANTEDLIST_ACK = CS_MAP + 0x017E;
    public const ushort CS_GUILDTACTICSVOLUNTEERING_REQ = CS_MAP + 0x017F;
    public const ushort CS_GUILDTACTICSVOLUNTEERING_ACK = CS_MAP + 0x0180;
    public const ushort CS_GUILDTACTICSVOLUNTEERINGDEL_REQ = CS_MAP + 0x0181;
    public const ushort CS_GUILDTACTICSVOLUNTEERINGDEL_ACK = CS_MAP + 0x0182;
    public const ushort CS_GUILDTACTICSVOLUNTEERLIST_REQ = CS_MAP + 0x0183;
    public const ushort CS_GUILDTACTICSVOLUNTEERLIST_ACK = CS_MAP + 0x0184;
    public const ushort CS_GUILDTACTICSREPLY_REQ = CS_MAP + 0x0185;
    public const ushort CS_GUILDTACTICSREPLY_ACK = CS_MAP + 0x0186;
    public const ushort CS_GUILDTACTICSKICKOUT_REQ = CS_MAP + 0x0187;
    public const ushort CS_GUILDTACTICSKICKOUT_ACK = CS_MAP + 0x0188;
    public const ushort CS_GUILDPOINTREWARD_REQ = CS_MAP + 0x01E5;
    public const ushort CS_GUILDPOINTREWARD_ACK = CS_MAP + 0x01E6;
    public const ushort CS_MONSTERBUY_REQ = CS_MAP + 0x01ED;
    public const ushort CS_MONSTERBUY_ACK = CS_MAP + 0x01EE;
    public const ushort CS_GUILDTACTICSINVITE_REQ = CS_MAP + 0x0211;
    public const ushort CS_GUILDTACTICSINVITE_ACK = CS_MAP + 0x0212;
    public const ushort CS_GUILDTACTICSANSWER_REQ = CS_MAP + 0x0213;
    public const ushort CS_GUILDTACTICSANSWER_ACK = CS_MAP + 0x0214;
    public const ushort CS_GUILDTACTICSLIST_REQ = CS_MAP + 0x0215;
    public const ushort CS_GUILDTACTICSLIST_ACK = CS_MAP + 0x0216;
    public const ushort CS_GUILDLOCALLIST_REQ = CS_MAP + 0x0155;    // (the war-info window)
    public const ushort CS_GUILDLOCALLIST_ACK = CS_MAP + 0x0156;    // castles {…, forts}, missions, sky gardens, BoW / BR
    public const ushort CS_ITEMLEVELREVISION_ACK = CS_MAP + 0x0245; // bLevel (the territory's item cap, 0 = none)
    public const ushort CS_LOCALOCCUPY_ACK = CS_MAP + 0x00A8;       // bType wLocalID bCountry dwGuildID
    public const ushort CS_CASTLEAPPLY_REQ = CS_MAP + 0x00D3;       // wCastle dwTarget
    public const ushort CS_CASTLEAPPLY_ACK = CS_MAP + 0x00D4;       // bResult wCastle dwTarget bCamp
    public const ushort CS_ADDGODTOWER_ACK = CS_MAP + 0x01A8;       // wID fX fY fZ wBall bCamp
    public const ushort CS_ADDGODBALL_ACK = CS_MAP + 0x01AA;        // wID bCamp bGround fX fY fZ
    public const ushort CS_DELGODBALL_ACK = CS_MAP + 0x01AB;        // wID
    public const ushort CS_TAKEGODBALL_REQ = CS_MAP + 0x01AC;       // wBall
    public const ushort CS_TAKEGODBALL_ACK = CS_MAP + 0x01AD;       // dwCharID wBall
    public const ushort CS_REMOVEGODBALL_ACK = CS_MAP + 0x01AE;     // dwCharID
    public const ushort CS_MOUNTGODBALL_REQ = CS_MAP + 0x01B0;      // wTower
    public const ushort CS_MOUNTGODBALL_ACK = CS_MAP + 0x01B1;      // wTower wBall bCamp dwCharID
    public const ushort CS_DEMOUNTGODBALL_REQ = CS_MAP + 0x01B2;    // wTower
    public const ushort CS_DEMOUNTGODBALL_ACK = CS_MAP + 0x01B3;    // wTower dwCharID
    public const ushort CS_BALANCEOFPOWER_ACK = CS_MAP + 0x01B4;     // fDefPower dwLeft wKillAtk wKillDef 4×strOwner 4×wKeep
    public const ushort CS_ENDWAR_ACK = CS_MAP + 0x01EB;            // bType dwWinGuild dwDefTotal dwAtkTotal strDef dwDefPower wDefPoint wDefKill strAtk dwAtkPower wAtkPoint wAtkKill
    public const ushort CS_ENTERCASTLE_ACK = CS_MAP + 0x01EF;       // wCastle bCamp strAtk strDef
    public const ushort CS_LEAVECASTLE_ACK = CS_MAP + 0x01F0;       // (empty)
    public const ushort CS_CHANGECOLOR_ACK = CS_MAP + 0x00DF;       // bType dwID bColor bCountry
    public const ushort CS_ENTERSKYGARDEN_ACK = CS_MAP + 0x0254;    // wID bCamp bDefCountry bLeft bCenter bRight bAtkCountry
    public const ushort CS_LEAVESKYGARDEN_ACK = CS_MAP + 0x0269;    // (empty)
    public const ushort CS_SKYGARDEN_OCCUPY_LEFT_ACK = CS_MAP + 0x0271;   // bCamp now holding the left point
    public const ushort CS_SKYGARDEN_OCCUPY_CENTER_ACK = CS_MAP + 0x0272; // … the centre point
    public const ushort CS_SKYGARDEN_OCCUPY_RIGHT_ACK = CS_MAP + 0x0273;  // … the right point
    public const ushort CS_RESETPCBANG_ACK = CS_MAP + 0x01B9;    // dwCharID, bInPcBang
    public const ushort CS_OPENMONEY_ACK = CS_MAP + 0x01D3;      // dwMoney (a money pouch opened)
    public const ushort CS_TERMINATE_REQ = CS_MAP + 0x01D2;
    public const ushort CS_CHECKRELAY_REQ = CS_MAP + 0x01DA;
    public const ushort CS_DISCONNECT_REQ = CS_MAP + 0x013C;
    public const ushort CS_CHGCHANNEL_REQ = CS_MAP + 0x013D;
    public const ushort CS_CHGCHANNEL_ACK = CS_MAP + 0x013E;
    public const ushort CS_KICKOUT_REQ = CS_MAP + 0x00D5;
    public const ushort CS_KICKOUTMAP_REQ = CS_MAP + 0x020F;
    public const ushort CS_KICKOUTMAP_ACK = CS_MAP + 0x0210;
    public const ushort CS_WARP_ACK = CS_MAP + 0x01F1;
    public const ushort CS_SETTIME_ACK = CS_MAP + 0x0279;
    public const ushort CS_ACDCLOSE_REQ = CS_MAP + 0x024A;
    public const ushort CS_WINLDIC_REQ = CS_MAP + 0x0943;
    public const ushort CS_PINGMEASUREMENT_REQ = CS_MAP + 0x0370;
    public const ushort CS_PINGMEASUREMENT_ACK = CS_MAP + 0x0371;
    // Party management (CSProtocol.h / MWProtocol.h).
    public const ushort CS_PARTYADD_REQ = CS_MAP + 0x0040;
    public const ushort CS_PARTYADD_ACK = CS_MAP + 0x0041;
    public const ushort CS_PARTYJOINASK_ACK = CS_MAP + 0x0042;
    public const ushort CS_PARTYJOIN_REQ = CS_MAP + 0x0043;
    public const ushort CS_PARTYJOIN_ACK = CS_MAP + 0x0044;
    public const ushort CS_PARTYDEL_REQ = CS_MAP + 0x0045;
    public const ushort CS_PARTYDEL_ACK = CS_MAP + 0x0046;
    public const ushort CS_PARTYMANSTAT_ACK = CS_MAP + 0x0047;
    public const ushort CS_PARTYATTR_ACK = CS_MAP + 0x0048;
    public const ushort CS_PARTYMOVE_REQ = CS_MAP + 0x00FF;
    public const ushort CS_PARTYMOVE_ACK = CS_MAP + 0x0100;
    public const ushort CS_CHGPARTYCHIEF_REQ = CS_MAP + 0x0101;
    public const ushort CS_CHGPARTYCHIEF_ACK = CS_MAP + 0x0102;
    public const ushort CS_CHGPARTYTYPE_REQ = CS_MAP + 0x0140;
    public const ushort CS_CHGPARTYTYPE_ACK = CS_MAP + 0x0141;
    public const ushort MW_PARTYADD_REQ = MW_BASE + 0x0018;
    public const ushort MW_PARTYADD_ACK = MW_BASE + 0x0019;
    public const ushort MW_PARTYJOIN_REQ = MW_BASE + 0x0020;
    public const ushort MW_PARTYJOIN_ACK = MW_BASE + 0x0021;
    public const ushort MW_PARTYDEL_REQ = MW_BASE + 0x0022;
    public const ushort MW_PARTYDEL_ACK = MW_BASE + 0x0023;
    public const ushort MW_PARTYMANSTAT_REQ = MW_BASE + 0x0024;
    public const ushort MW_PARTYMANSTAT_ACK = MW_BASE + 0x0025;
    public const ushort MW_PARTYATTR_REQ = MW_BASE + 0x004A;
    public const ushort MW_PARTYMOVE_REQ = MW_BASE + 0x009F;
    public const ushort MW_PARTYMOVE_ACK = MW_BASE + 0x00A0;
    public const ushort MW_CHGPARTYCHIEF_ACK = MW_BASE + 0x00A1;
    public const ushort MW_CHGPARTYCHIEF_REQ = MW_BASE + 0x00A2;
    public const ushort MW_CHGPARTYTYPE_REQ = MW_BASE + 0x00CA;
    public const ushort MW_CHGPARTYTYPE_ACK = MW_BASE + 0x00CB;
    public const ushort CS_INVENADD_REQ = CS_MAP + 0x00E3;      // bDESInven · bSRCInven · bItemID
    public const ushort CS_INVENADD_ACK = CS_MAP + 0x00E4;      // bResult · bInvenID · wItemID · dEndTime · bShowIF
    public const ushort CS_INVENDEL_REQ = CS_MAP + 0x00E5;      // bSRCInven · bDESInven · bPos
    public const ushort CS_INVENDEL_ACK = CS_MAP + 0x00E6;      // bResult · bInvenID · bShowIF
    public const ushort CS_INVENMOVE_REQ = CS_MAP + 0x00E7;     // bSRCInven · bDESInven
    public const ushort CS_INVENMOVE_ACK = CS_MAP + 0x00E8;     // bResult · bSRCInvenID · bDESTInvenID
    public const ushort CS_SETRETURNPOS_REQ = CS_MAP + 0x010A;  // wNpcID
    public const ushort CS_SETRETURNPOS_ACK = CS_MAP + 0x010B;  // bResult
    public const ushort CS_TELEPORT_REQ = CS_MAP + 0x00D7;      // wNpcID · wPortalID
    public const ushort CS_TELEPORT_ACK = CS_MAP + 0x00D8;      // bResult · dwID · bType · dwRange · wMapID · x · y · z
    public const ushort CS_BEGINTELEPORT_ACK = CS_MAP + 0x0139; // bChannel · wMapID
    public const ushort MW_TELEPORT_REQ = MW_BASE + 0x00BD;     // charId · key · ch · map · x · y · z · bResult
    public const ushort MW_TELEPORT_ACK = MW_BASE + 0x00BE;     // charId · key · bServerID
    public const ushort MW_BEGINTELEPORT_ACK = MW_BASE + 0x00C7; // charId · key · bSameChannel · ch · map · x · y · z
    public const ushort MW_STARTTELEPORT_REQ = MW_BASE + 0x00C8; // charId · key · ch · map · x · y · z
    public const ushort CS_REVIVALASK_REQ = CS_MAP + 0x00FB;    // bReply · dwAttackerID · bAttackerType · wSkill · bLevel
    public const ushort CS_REVIVALASK_ACK = CS_MAP + 0x00FC;    // dwID · bType · wSkillID · bLevel
    public const ushort CS_REVIVALREPLY_ACK = CS_MAP + 0x0107;  // bReply · dwDefender
    public const ushort CS_AFTERMATH_ACK = CS_MAP + 0x0222;     // dwCharID · bStep
    public const ushort CS_POSTSEND_REQ = CS_MAP + 0x0122;
    public const ushort CS_POSTSEND_ACK = CS_MAP + 0x0123;
    public const ushort CS_POSTRECV_ACK = CS_MAP + 0x0124;
    public const ushort CS_POSTLIST_ACK = CS_MAP + 0x0125;
    public const ushort CS_POSTVIEW_REQ = CS_MAP + 0x0126;
    public const ushort CS_POSTVIEW_ACK = CS_MAP + 0x0127;
    public const ushort CS_POSTDEL_REQ = CS_MAP + 0x0128;
    public const ushort CS_POSTDEL_ACK = CS_MAP + 0x0129;
    public const ushort CS_POSTGETITEM_REQ = CS_MAP + 0x012A;
    public const ushort CS_POSTGETITEM_ACK = CS_MAP + 0x012B;
    public const ushort CS_POSTRETURN_REQ = CS_MAP + 0x012C;
    public const ushort CS_POSTRETURN_ACK = CS_MAP + 0x012D;
    public const ushort CS_POSTLIST_REQ = CS_MAP + 0x0225;
    public const ushort MW_POSTRECV_REQ = MW_BASE + 0x007C;     // postId · sender · target · title · bType
    public const ushort MW_POSTRECV_ACK = MW_BASE + 0x007D;
    public const ushort CS_ITEMUPGRADE_REQ = CS_MAP + 0x00A9;     // tInv tSlot gInv gSlot wNpc nInv nSlot wColor
    public const ushort CS_ITEMUPGRADE_ACK = CS_MAP + 0x00AA;     // result inv slot level gem effect wColor wMogg
    public const ushort CS_ITEMMAGICGRADE_ACK = CS_MAP + 0x00AB;  // result inv slot count {id wValue}
    public const ushort CS_REFINE_REQ = CS_MAP + 0x01BF;          // needCost inv slot addCount wNpc nInv nSlot {inv slot}
    public const ushort CS_REFINECOST_ACK = CS_MAP + 0x01C0;      // dwCost bDiscountRate
    public const ushort CS_REFINE_ACK = CS_MAP + 0x01C1;          // result inv [item]
    public const ushort CS_ITEMCHANGE_REQ = CS_MAP + 0x01F4;      // inv slot
    public const ushort CS_ITEMCHANGE_ACK = CS_MAP + 0x01F5;      // result wNewID bNewCount
    public const ushort CS_COUNTDOWN_REQ = CS_MAP + 0x01F6;       // dwCommand (GM_EXIT_GAME / GM_EXIT)
    public const ushort CS_COUNTDOWN_ACK = CS_MAP + 0x01F7;       // dwCommand echoed; client then runs it
    public const ushort CS_CHANGEITEMATTR_ACK = CS_MAP + 0x01F8;  // inv item
    public const ushort CS_HOTKEYADD_REQ = CS_MAP + 0x009F;    // bType · wID · bTargetInven · bTargetPos
    public const ushort CS_HOTKEYDEL_REQ = CS_MAP + 0x00A1;    // bInvenID · bPos
    public const ushort CS_HOTKEYCHANGE_ACK = CS_MAP + 0x00A2; // bInvenID · bCount · {bPos bType wID}
    // Summons / recall monsters (CSProtocol.h:1742-1824, MWProtocol.h:306-420).
    public const ushort CS_ADDRECALLMON_ACK = CS_MAP + 0x00D9;
    public const ushort CS_DELRECALLMON_ACK = CS_MAP + 0x00DA;
    public const ushort CS_DELRECALLMON_REQ = CS_MAP + 0x00E1;      // dwMonID · bType
    public const ushort CS_ADDSELFOBJ_ACK = CS_MAP + 0x00F4;        // a placed object (C++ CTSelfObj) comes into view
    public const ushort CS_DELSELFOBJ_ACK = CS_MAP + 0x00F5;        // dwObjID · bExitMap
    public const ushort CS_CHGMODERECALLMON_REQ = CS_MAP + 0x00E2;  // dwMonID · bMode
    public const ushort MW_CREATERECALLMON_REQ = MW_BASE + 0x00B8;
    public const ushort MW_CREATERECALLMON_ACK = MW_BASE + 0x00B9;
    public const ushort MW_RECALLMONDEL_REQ = MW_BASE + 0x00BA;     // charId · key · monId · bForever
    public const ushort MW_RECALLMONDEL_ACK = MW_BASE + 0x00BB;
    public const ushort MW_RECALLMONDATA_REQ = MW_BASE + 0x0117;
    // Pets / mounts (CSProtocol.h:2263-2299, 3323, 4089-4096; MWProtocol.h:330).
    public const ushort CS_PETMAKE_REQ = CS_MAP + 0x012E;           // bInven · bSlot · strName
    public const ushort CS_PETMAKE_ACK = CS_MAP + 0x012F;           // bResult · wPetID · strName · ldwTime
    public const ushort CS_PETDEL_REQ = CS_MAP + 0x0130;            // wPetID
    public const ushort CS_PETDEL_ACK = CS_MAP + 0x0131;            // bResult · wPetID
    public const ushort CS_PETLIST_ACK = CS_MAP + 0x0132;           // bCount · {wPetID strName ldwTime bEffect}
    public const ushort CS_PETRECALL_REQ = CS_MAP + 0x0133;         // wPetID
    public const ushort CS_PETRECALL_ACK = CS_MAP + 0x0134;         // bResult
    public const ushort CS_PETRIDING_REQ = CS_MAP + 0x0135;         // dwMonID · bAction
    public const ushort CS_PETRIDING_ACK = CS_MAP + 0x0136;         // bResult · dwCharID · dwMonID · bAction
    public const ushort CS_PETCANCEL_REQ = CS_MAP + 0x01FB;
    public const ushort CS_PETEFFECTCHANGE_REQ = CS_MAP + 0x03A1;
    public const ushort CS_PETEFFECTCHANGE_ACK = CS_MAP + 0x03A2;
    public const ushort CS_REQUESTSADDLE_REQ = CS_MAP + 0x0319;
    public const ushort CS_SENDSADDLE_REQ = CS_MAP + 0x0320;        // S→C despite the name: dwItemID · bType · endTime · BOOL bOpenUI
    public const ushort CS_CREATESADDLE_REQ = CS_MAP + 0x0321;
    public const ushort CS_DELETESADDLE_REQ = CS_MAP + 0x0323;
    public const ushort CS_UPDATEMEDALS_REQ = CS_MAP + 0x0350;      // S→C: dwMedals
    // ---- the GM tools (MapService.Gm.cs) ----
    public const ushort CS_CHATBAN_REQ = CS_MAP + 0x0223;           // strName · wMinute
    public const ushort CS_CHATBAN_ACK = CS_MAP + 0x0224;           // bResult (CHATBAN_RESULT)
    public const ushort CS_HELPMESSAGE_ACK = CS_MAP + 0x0244;       // bID · strMessage
    public const ushort CS_CMMOVE_REQ = CS_MAP + 0x0253;            // bChannel · wMapID · fX · fY · fZ
    public const ushort CS_CMMOVETOUSER_REQ = CS_MAP + 0x0255;      // strTarget
    public const ushort CS_CMGIFT_REQ = CS_MAP + 0x0258;            // strTarget · wGiftID
    public const ushort CS_CMGIFT_ACK = CS_MAP + 0x0259;            // bResult (CMGIFT_RESULT)
    public const ushort MW_USERPOSITION_REQ = MW_BASE + 0x00C6;     // dwCharID · dwKey · strGM
    public const ushort MW_CHATBAN_REQ = MW_BASE + 0x0115;          // strName · nUntil (INT64) · bResult · dwGMID · dwGMKey
    public const ushort MW_CHATBAN_ACK = MW_BASE + 0x0116;          // strName · wMinute · dwGMID · dwGMKey
    public const ushort MW_ITEMSTATE_REQ = MW_BASE + 0x011F;        // dwID · wCount {wItemID · bInitState}
    public const ushort MW_HELPMESSAGE_REQ = MW_BASE + 0x0164;      // bID · dlStart · dlEnd · strMessage
    public const ushort MW_CMGIFT_REQ = MW_BASE + 0x0175;           // dwTarget · bType · dwValue · bCount · strTitle · strMsg · dwGMID · bTool · wGiftID · wErrID · bRet
    public const ushort MW_CMGIFT_ACK = MW_BASE + 0x0176;           // strTarget · wGiftID · dwGMID
    public const ushort MW_CMGIFTRESULT_REQ = MW_BASE + 0x0177;     // bRet · dwGMID (world -> map)
    public const ushort MW_CMGIFTRESULT_ACK = MW_BASE + 0x0177;     // bRet · bTool · dwGMID (map -> world; the same id)
    public const ushort CT_SERVICEMONITOR_REQ = CT_CONTROL + 0x001D; // dwTick · dwSession · dwUser · dwActiveUser
    public const ushort CT_SERVICEMONITOR_ACK = CT_CONTROL + 0x001E; // dwTick
    public const ushort CT_ANNOUNCEMENT_ACK = CT_CONTROL + 0x002A;   // strAnnounce
    public const ushort CT_USERKICKOUT_ACK = CT_CONTROL + 0x002C;    // strUser
    public const ushort CT_MONSPAWNFIND_ACK = CT_CONTROL + 0x0037;   // in: dwManager · bChannel · wMapID · wSpawnID / out: dwManager · wMapID · wSpawnID · bCount {…}
    public const ushort CT_MONACTION_ACK = CT_CONTROL + 0x0039;      // bChannel · wMapID · dwMonID · bAction · dwTrigger · dwHost · dwRH · bRHType · wSpawnID
    public const ushort CT_USERPOSITION_ACK = CT_CONTROL + 0x0044;   // strTarget · strGM (map -> world)
    public const ushort CT_SERVICEDATACLEAR_ACK = CT_CONTROL + 0x004F;
    public const ushort CT_CTRLSVR_REQ = CT_CONTROL + 0x0058;
    public const ushort CT_CASTLEINFO_REQ = CT_CONTROL + 0x0059;     // dwManager
    public const ushort CT_CASTLEINFO_ACK = CT_CONTROL + 0x005A;     // dwManager · wCount {wLocal · strName · bStatus · dwDef · strDef · bCountry · dwAtk · strAtk · tNext}
    // ---- the cash shop (MapService.CashShop.cs) ----
    public const ushort CS_CASHITEMCABINET_REQ = CS_MAP + 0x0192;
    public const ushort CS_CASHITEMCABINET_ACK = CS_MAP + 0x0193;   // dUseTime (INT64) · dwCash · dwBonus · wCount {dwID · item (no slot)}
    public const ushort CS_CASHITEMGET_REQ = CS_MAP + 0x0194;       // dwID · bInven · bSlot
    public const ushort CS_CASHITEMGET_ACK = CS_MAP + 0x0195;       // bResult · dwID
    public const ushort CS_CASHSHOPITEMLIST_REQ = CS_MAP + 0x0196;  // wItemID (0: all)
    public const ushort CS_CASHSHOPITEMLIST_ACK = CS_MAP + 0x0197;  // dwCash · dwBonus · bCount {strName · wCount {wID · wInfoID · dwMoney · bKind · item (cash form)}}
    public const ushort CS_CASHITEMBUY_REQ = CS_MAP + 0x0198;       // wCashItemID
    public const ushort CS_CASHITEMBUY_ACK = CS_MAP + 0x0199;       // bResult · dwCash · dwBonus
    public const ushort CS_CASHITEMPRESENT_REQ = CS_MAP + 0x01B7;   // strTarget · wCashItemID
    public const ushort CS_CASHCABINETBUY_REQ = CS_MAP + 0x01C4;    // wShopItemID
    public const ushort CS_CASHCABINETBUY_ACK = CS_MAP + 0x01C5;    // bResult · dTime (INT64) · dwCash · dwBonus
    public const ushort CS_CASHITEMPUTIN_REQ = CS_MAP + 0x01C6;     // bInven · bItem
    public const ushort CS_CASHITEMPUTIN_ACK = CS_MAP + 0x01C7;     // bResult · dwNewID [· item (no slot) on success]
    public const ushort CS_CASHSHOPSTOP_ACK = CS_MAP + 0x01FC;      // bStop
    public const ushort MW_CASHSHOPSTOP_REQ = MW_BASE + 0x0132;     // bStop · bSendPlayer (world -> every map)
    public const ushort MW_CASHITEMSALE_REQ = MW_BASE + 0x0133;     // dwIndex · wValue · wCount {wID · bSaleValue}
    public const ushort MW_CASHITEMSALE_ACK = MW_BASE + 0x0134;     // dwIndex · wValue · bRet
    // ---- the item and combat leftovers (MapService.ItemLeftovers.cs) ----
    public const ushort CS_DROPDAMAGE_REQ = CS_MAP + 0x0108;        // dwTargetID · bTargetType · dwDamage · bChannel · wMapID
    public const ushort CS_DROPDAMAGE_ACK = CS_MAP + 0x0109;        // dwID · bType · dwDamage
    public const ushort CS_STOPTHECLOCK_REQ = CS_MAP + 0x01CB;      // bInven · bItem · wShopItemID
    public const ushort CS_STOPTHECLOCK_ACK = CS_MAP + 0x01CC;      // bResult · bInven · bItem · dEndTime (INT64)
    public const ushort CS_WASTEREFINE_REQ = CS_MAP + 0x01F9;       // bInven · bItem
    public const ushort CS_WASTEREFINE_ACK = CS_MAP + 0x01FA;       // bResult
    public const ushort CS_GETREWARDS_REQ = CS_MAP + 0x0325;        // bKind · wUseValue
    public const ushort CS_GETREWARDS_ACK = CS_MAP + 0x0326;        // wCount · items
    public const ushort CS_USEMEDALITEM_REQ = CS_MAP + 0x0351;      // bInven · bSlot · bCount
    public const ushort CS_ACTITEMSUSE_REQ = CS_MAP + 0x0376;       // as CS_SKILLUSE_REQ's head · dwDefendID · bDefendType
    // Companions (CSProtocol.h:4103-4198; MWProtocol.h:537-540). Several "_REQ" here are server → client.
    public const ushort CS_COMPANIONLIST_ACK = CS_MAP + 0x0329;
    public const ushort CS_COMPANIONUPGRADE_REQ = CS_MAP + 0x032B;     // bIndex · bSlot
    public const ushort CS_COMPANIONUPDATE_REQ = CS_MAP + 0x032C;      // S→C: slot · points · 6 stats
    public const ushort CS_COMPANIONLUPDATE_REQ = CS_MAP + 0x032D;     // S→C: slot · level · exp · points · nextExp · bonus
    public const ushort CS_COMPANIONRECALL_REQ = CS_MAP + 0x032E;      // dwMonID · bSlot
    public const ushort CS_COMPANIONCANCEL_REQ = CS_MAP + 0x0330;
    public const ushort CS_COMPANIONLUP_REQ = CS_MAP + 0x033A;         // bSlot
    public const ushort CS_USEPETITEM_REQ = CS_MAP + 0x033B;           // dwParam(inven | slot<<16) · bSlot
    public const ushort CS_UPDATECOMPANIONBYITEM_REQ = CS_MAP + 0x033C;   // S→C
    public const ushort CS_UPDATECOMPANIONBYSYSTEM_REQ = CS_MAP + 0x033D; // S→C
    public const ushort CS_PVPPOINT_ACK = CS_MAP + 0x01E2;          // dwTotal · dwUseable · bEvent · dwMonthPvPoint
    public const ushort CS_DUELINVITE_REQ = CS_MAP + 0x0147;        // dwTarget
    public const ushort CS_DUELINVITE_ACK = CS_MAP + 0x0148;        // dwInviter (to the invited player)
    public const ushort CS_DUELINVITEREPLY_REQ = CS_MAP + 0x0149;   // bResult (ASK_TYPE) · dwInviter
    public const ushort CS_DUELSTART_ACK = CS_MAP + 0x014A;         // bResult (DUEL_RESULT) · dwInviter · dwTarget
    public const ushort CS_DUELEND_REQ = CS_MAP + 0x014B;           // (give up)
    public const ushort CS_DUELEND_ACK = CS_MAP + 0x014C;           // dwLoser (0 = none)
    public const ushort CS_DUELSTANDBY_ACK = CS_MAP + 0x014D;       // dwInviter · dwTarget · fPosX · fPosZ (the arena centre)
    // PvP ranking and titles (MapService.Rank.cs / MapService.Title.cs)
    public const ushort CS_CHANGECHARBASE_ACK = CS_MAP + 0x01CA;    // bResult · dwCharID · bType · bValue · strName · wTitleID · dwSecond
    public const ushort CS_PVPRECORD_REQ = CS_MAP + 0x01E9;         // bType (0 PvP, else duel)
    public const ushort CS_PVPRECORD_ACK = CS_MAP + 0x01EA;
    public const ushort CS_FAMERANKLIST_REQ = CS_MAP + 0x021A;      // bType · bMonth
    public const ushort CS_FAMERANKLIST_ACK = CS_MAP + 0x021B;
    public const ushort CS_UPDATEFAMERANKLIST_ACK = CS_MAP + 0x021C;
    public const ushort CS_MONTHRANKLIST_REQ = CS_MAP + 0x021D;
    public const ushort CS_MONTHRANKLIST_ACK = CS_MAP + 0x021E;
    public const ushort CS_FIRSTGRADEGROUP_REQ = CS_MAP + 0x0220;
    public const ushort CS_FIRSTGRADEGROUP_ACK = CS_MAP + 0x0221;
    public const ushort CS_TITLELIST_REQ = CS_MAP + 0x0274;
    public const ushort CS_TITLELIST_ACK = CS_MAP + 0x0275;         // bCount × (wTitleID · bSelected)
    public const ushort CS_CHANGETITLE_REQ = CS_MAP + 0x0276;       // wTitleID
    public const ushort CS_CHANGENAME_REQ = CS_MAP + 0x01C9;        // bInven, bItem, szName
    public const ushort CS_CHANGECOUNTRY_REQ = CS_MAP + 0x024B;     // bType (IK_COUNTRY / IK_AIDCOUNTRY), bCountry, bInven, bItem
    public const ushort CS_TITLEGAIN_ACK = CS_MAP + 0x0277;         // the list, then wTitleID · bStart
    public const ushort CS_TITLERESET_ACK = CS_MAP + 0x0278;
    public const ushort MW_CHANGECHARBASE_REQ = MW_BASE + 0x0119;   // dwCharID · dwKey · bType · bValue · wTitleID · strName
    public const ushort MW_CHANGECHARBASE_ACK = MW_BASE + 0x011A;
    public const ushort MW_MONTHRANKUPDATE_REQ = MW_BASE + 0x013D;
    public const ushort MW_MONTHRANKUPDATE_ACK = MW_BASE + 0x013E;  // bMonth · bCountry · RANKER
    public const ushort MW_MONTHRANKLIST_REQ = MW_BASE + 0x013F;
    public const ushort MW_MONTHRANKRESET_REQ = MW_BASE + 0x0140;
    public const ushort MW_WARLORDSAY_REQ = MW_BASE + 0x0141;
    public const ushort MW_LEVELUP_REQ = MW_BASE + 0x0026;          // dwCharID, dwKey, bLevel
    public const ushort MW_CHARSTATINFO_REQ = MW_BASE + 0x0063;     // dwAskerID, then the CS_CHARSTATINFO_ACK body
    public const ushort MW_CHARSTATINFO_ACK = MW_BASE + 0x0064;     // dwAskerID, dwCharID
    public const ushort MW_CHARSTATINFOANS_REQ = MW_BASE + 0x0065;  // dwAskerID, dwCharID
    public const ushort MW_CHARSTATINFOANS_ACK = MW_BASE + 0x0066;  // dwAskerID, then the CS_CHARSTATINFO_ACK body
    public const ushort MW_HELMETHIDE_REQ = MW_BASE + 0x0101;       // dwCharID, dwKey, bHide
    public const ushort MW_HELMETHIDE_ACK = MW_BASE + 0x0102;       // dwCharID, dwKey, bHide
    public const ushort MW_WORLDPOSTSEND_REQ = MW_BASE + 0x013B;    // bType (WPT_*), … (MapService.SmallRequests.cs)
    // Territory battles (MapService.Territory.cs)
    public const ushort MW_LOCALENABLE_REQ = MW_BASE + 0x0067;      // bStatus dwSecond dwLocalStart bCastleDay dwCastleStart
    public const ushort MW_CASTLEENABLE_REQ = MW_BASE + 0x007E;     // bStatus dwSecond
    public const ushort MW_REGION_ACK = MW_BASE + 0x00BC;           // dwCharID dwKey dwRegion
    public const ushort MW_CASTLEWARINFO_REQ = MW_BASE + 0x010F;    // the castle scoreboard
    public const ushort MW_CASTLEWARINFO_ACK = MW_BASE + 0x0110;    // wCastle dwGuild bLocals {wLocal 6×{dwGuild bType}}
    public const ushort MW_HEROSELECT_REQ = MW_BASE + 0x011B;       // wZone strHero tHero
    // The guild relay, batch G1 (MWProtocol.h): *_ACK map -> world, *_REQ world -> map.
    // Corps and party member recall, batch S2/S3 (MWProtocol.h; MapService.Corps.cs).
    public const ushort MW_CORPSASK_REQ = MW_BASE + 0x006B;
    public const ushort MW_CORPSASK_ACK = MW_BASE + 0x006C;
    public const ushort MW_CORPSREPLY_REQ = MW_BASE + 0x006D;
    public const ushort MW_CORPSREPLY_ACK = MW_BASE + 0x006E;
    public const ushort MW_CORPSJOIN_REQ = MW_BASE + 0x006F;
    public const ushort MW_ADDSQUAD_REQ = MW_BASE + 0x0070;
    public const ushort MW_DELSQUAD_REQ = MW_BASE + 0x0071;
    public const ushort MW_CORPSLEAVE_ACK = MW_BASE + 0x0072;
    public const ushort MW_DELCORPSUNIT_REQ = MW_BASE + 0x0073;
    public const ushort MW_ADDCORPSUNIT_REQ = MW_BASE + 0x0074;
    public const ushort MW_CORPSCMD_REQ = MW_BASE + 0x0075;
    public const ushort MW_CORPSCMD_ACK = MW_BASE + 0x0092;
    public const ushort MW_CORPSENEMYLIST_REQ = MW_BASE + 0x0093;
    public const ushort MW_CORPSENEMYLIST_ACK = MW_BASE + 0x0094;
    public const ushort MW_MOVECORPSENEMY_REQ = MW_BASE + 0x0095;
    public const ushort MW_MOVECORPSENEMY_ACK = MW_BASE + 0x0096;
    public const ushort MW_MOVECORPSUNIT_REQ = MW_BASE + 0x0097;
    public const ushort MW_MOVECORPSUNIT_ACK = MW_BASE + 0x0098;
    public const ushort MW_ADDCORPSENEMY_REQ = MW_BASE + 0x0099;
    public const ushort MW_ADDCORPSENEMY_ACK = MW_BASE + 0x009A;
    public const ushort MW_DELCORPSENEMY_REQ = MW_BASE + 0x009B;
    public const ushort MW_DELCORPSENEMY_ACK = MW_BASE + 0x009C;
    public const ushort MW_CORPSHP_REQ = MW_BASE + 0x009D;
    public const ushort MW_CORPSHP_ACK = MW_BASE + 0x009E;
    public const ushort MW_CHGCORPSCOMMANDER_ACK = MW_BASE + 0x00A3;
    public const ushort MW_CHGCORPSCOMMANDER_REQ = MW_BASE + 0x00A4;
    public const ushort MW_REPORTENEMYLIST_REQ = MW_BASE + 0x00A5;
    public const ushort MW_CHGSQUADCHIEF_REQ = MW_BASE + 0x00A6;
    public const ushort MW_PARTYMEMBERRECALL_REQ = MW_BASE + 0x0103;
    public const ushort MW_PARTYMEMBERRECALL_ACK = MW_BASE + 0x0104;
    public const ushort MW_PARTYMEMBERRECALLANS_REQ = MW_BASE + 0x0113;
    public const ushort MW_PARTYMEMBERRECALLANS_ACK = MW_BASE + 0x0114;
    public const ushort CT_USERMOVE_ACK = CT_CONTROL + 0x002E;      // map -> world: move a named user; world -> map: dwCharID, dwKey, the place
    // Friends, the protected list and the messenger, batch S1 (MWProtocol.h; MapService.Social.cs).
    public const ushort MW_FRIENDASK_REQ = MW_BASE + 0x0050;
    public const ushort MW_FRIENDASK_ACK = MW_BASE + 0x0051;
    public const ushort MW_FRIENDADD_REQ = MW_BASE + 0x0052;
    public const ushort MW_FRIENDREPLY_ACK = MW_BASE + 0x0053;
    public const ushort MW_FRIENDERASE_REQ = MW_BASE + 0x0054;
    public const ushort MW_FRIENDERASE_ACK = MW_BASE + 0x0055;
    public const ushort MW_FRIENDLIST_REQ = MW_BASE + 0x0056;
    public const ushort MW_FRIENDREGION_REQ = MW_BASE + 0x0057;
    public const ushort MW_FRIENDCONNECTION_REQ = MW_BASE + 0x0058;
    public const ushort MW_FRIENDGROUPMAKE_REQ = MW_BASE + 0x0059;
    public const ushort MW_FRIENDGROUPMAKE_ACK = MW_BASE + 0x005A;
    public const ushort MW_FRIENDGROUPDELETE_REQ = MW_BASE + 0x005B;
    public const ushort MW_FRIENDGROUPDELETE_ACK = MW_BASE + 0x005C;
    public const ushort MW_FRIENDGROUPCHANGE_REQ = MW_BASE + 0x005D;
    public const ushort MW_FRIENDGROUPCHANGE_ACK = MW_BASE + 0x005E;
    public const ushort MW_FRIENDGROUPNAME_REQ = MW_BASE + 0x005F;
    public const ushort MW_FRIENDGROUPNAME_ACK = MW_BASE + 0x0060;
    public const ushort MW_FRIENDLIST_ACK = MW_BASE + 0x012E;
    public const ushort MW_FRIENDPROTECTEDASK_ACK = MW_BASE + 0x008D;
    public const ushort MW_PROTECTEDCHECK_ACK = MW_BASE + 0x008B;
    public const ushort MW_TMSSEND_ACK = MW_BASE + 0x0076;
    public const ushort MW_TMSRECV_REQ = MW_BASE + 0x0077;
    public const ushort MW_TMSINVITE_REQ = MW_BASE + 0x0078;
    public const ushort MW_TMSINVITE_ACK = MW_BASE + 0x0079;
    public const ushort MW_TMSOUT_REQ = MW_BASE + 0x007A;
    public const ushort MW_TMSOUT_ACK = MW_BASE + 0x007B;
    public const ushort MW_TMSINVITEASK_REQ = MW_BASE + 0x00CC;
    public const ushort MW_TMSINVITEASK_ACK = MW_BASE + 0x00CD;
    public const ushort MW_SOULMATE_REQ = MW_BASE + 0x0105;
    public const ushort MW_EVENTQUARTER_REQ = MW_BASE + 0x0100;       // the lucky-ticket draw (MapService.Events.cs)
    public const ushort MW_EVENTUPDATE_REQ = MW_BASE + 0x012F;        // an event starts or ends
    public const ushort MW_EVENTMSG_REQ = MW_BASE + 0x0131;           // an event message
    public const ushort MW_EVENTMSGLOTTERY_REQ = MW_BASE + 0x0168;    // the event lottery's winners (MapService.SoulLottery.cs)
    public const ushort MW_SOULMATESEARCH_REQ = MW_BASE + 0x0107;
    public const ushort MW_SOULMATESEARCH_ACK = MW_BASE + 0x0108;
    public const ushort MW_SOULMATEREG_REQ = MW_BASE + 0x0109;
    public const ushort MW_SOULMATEREG_ACK = MW_BASE + 0x010A;
    public const ushort MW_SOULMATEEND_REQ = MW_BASE + 0x010B;
    public const ushort MW_SOULMATEEND_ACK = MW_BASE + 0x010C;
    public const ushort MW_LEVELUP_ACK = MW_BASE + 0x0027;           // dwCharID, dwKey, bLevel
    public const ushort MW_GUILDESTABLISH_REQ = MW_BASE + 0x002A;
    public const ushort MW_GUILDESTABLISH_ACK = MW_BASE + 0x002B;
    public const ushort MW_GUILDDISORGANIZATION_REQ = MW_BASE + 0x002C;
    public const ushort MW_GUILDDISORGANIZATION_ACK = MW_BASE + 0x002D;
    public const ushort MW_GUILDINVITE_REQ = MW_BASE + 0x002E;
    public const ushort MW_GUILDINVITE_ACK = MW_BASE + 0x002F;
    public const ushort MW_GUILDINVITEANSWER_ACK = MW_BASE + 0x0030;
    public const ushort MW_GUILDJOIN_REQ = MW_BASE + 0x0031;
    public const ushort MW_GUILDLEAVE_REQ = MW_BASE + 0x0032;
    public const ushort MW_GUILDLEAVE_ACK = MW_BASE + 0x0033;
    public const ushort MW_GUILDDUTY_REQ = MW_BASE + 0x0034;
    public const ushort MW_GUILDDUTY_ACK = MW_BASE + 0x0035;
    public const ushort MW_GUILDPEER_REQ = MW_BASE + 0x0036;
    public const ushort MW_GUILDPEER_ACK = MW_BASE + 0x0037;
    public const ushort MW_GUILDINFO_REQ = MW_BASE + 0x0038;
    public const ushort MW_GUILDINFO_ACK = MW_BASE + 0x0039;
    public const ushort MW_GUILDKICKOUT_ACK = MW_BASE + 0x003A;
    public const ushort MW_GUILDMEMBERLIST_REQ = MW_BASE + 0x003B;
    public const ushort MW_GUILDMEMBERLIST_ACK = MW_BASE + 0x003C;
    // The guild relay, batch G2.
    public const ushort MW_GUILDCABINETLIST_REQ = MW_BASE + 0x00D0;
    public const ushort MW_GUILDCABINETLIST_ACK = MW_BASE + 0x00D1;
    public const ushort MW_GUILDCABINETPUTIN_REQ = MW_BASE + 0x00D2;
    public const ushort MW_GUILDCABINETPUTIN_ACK = MW_BASE + 0x00D3;
    public const ushort MW_GUILDCABINETTAKEOUT_REQ = MW_BASE + 0x00D4;
    public const ushort MW_GUILDCABINETTAKEOUT_ACK = MW_BASE + 0x00D5;
    public const ushort MW_GUILDCONTRIBUTION_REQ = MW_BASE + 0x00D6;
    public const ushort MW_GUILDCONTRIBUTION_ACK = MW_BASE + 0x00D7;
    public const ushort MW_GUILDARTICLELIST_REQ = MW_BASE + 0x00D8;
    public const ushort MW_GUILDARTICLELIST_ACK = MW_BASE + 0x00D9;
    public const ushort MW_GUILDARTICLEADD_REQ = MW_BASE + 0x00DA;
    public const ushort MW_GUILDARTICLEADD_ACK = MW_BASE + 0x00DB;
    public const ushort MW_GUILDARTICLEDEL_REQ = MW_BASE + 0x00DC;
    public const ushort MW_GUILDARTICLEDEL_ACK = MW_BASE + 0x00DD;
    public const ushort MW_GUILDFAME_REQ = MW_BASE + 0x00DE;
    public const ushort MW_GUILDFAME_ACK = MW_BASE + 0x00DF;
    public const ushort MW_GUILDARTICLEUPDATE_REQ = MW_BASE + 0x00E0;
    public const ushort MW_GUILDARTICLEUPDATE_ACK = MW_BASE + 0x00E1;
    public const ushort MW_GUILDWANTEDADD_REQ = MW_BASE + 0x00E2;
    public const ushort MW_GUILDWANTEDADD_ACK = MW_BASE + 0x00E3;
    public const ushort MW_GUILDWANTEDDEL_REQ = MW_BASE + 0x00E4;
    public const ushort MW_GUILDWANTEDDEL_ACK = MW_BASE + 0x00E5;
    public const ushort MW_GUILDWANTEDLIST_REQ = MW_BASE + 0x00E6;
    public const ushort MW_GUILDWANTEDLIST_ACK = MW_BASE + 0x00E7;
    public const ushort MW_GUILDVOLUNTEERING_REQ = MW_BASE + 0x00E8;
    public const ushort MW_GUILDVOLUNTEERING_ACK = MW_BASE + 0x00E9;
    public const ushort MW_GUILDVOLUNTEERINGDEL_REQ = MW_BASE + 0x00EA;
    public const ushort MW_GUILDVOLUNTEERINGDEL_ACK = MW_BASE + 0x00EB;
    public const ushort MW_GUILDVOLUNTEERLIST_REQ = MW_BASE + 0x00EC;
    public const ushort MW_GUILDVOLUNTEERLIST_ACK = MW_BASE + 0x00ED;
    public const ushort MW_GUILDVOLUNTEERREPLY_REQ = MW_BASE + 0x00EE;
    public const ushort MW_GUILDVOLUNTEERREPLY_ACK = MW_BASE + 0x00EF;
    public const ushort MW_GUILDPOINTLOG_REQ = MW_BASE + 0x0123;
    public const ushort MW_GUILDPOINTLOG_ACK = MW_BASE + 0x0124;
    public const ushort MW_GUILDPVPRECORD_REQ = MW_BASE + 0x0127;
    public const ushort MW_GUILDPVPRECORD_ACK = MW_BASE + 0x0128;
    public const ushort MW_GUILDTACTICSWANTEDADD_REQ = MW_BASE + 0x00F0;
    public const ushort MW_GUILDTACTICSWANTEDADD_ACK = MW_BASE + 0x00F1;
    public const ushort MW_GUILDTACTICSWANTEDDEL_REQ = MW_BASE + 0x00F2;
    public const ushort MW_GUILDTACTICSWANTEDDEL_ACK = MW_BASE + 0x00F3;
    public const ushort MW_GUILDTACTICSWANTEDLIST_REQ = MW_BASE + 0x00F4;
    public const ushort MW_GUILDTACTICSWANTEDLIST_ACK = MW_BASE + 0x00F5;
    public const ushort MW_GUILDTACTICSVOLUNTEERING_REQ = MW_BASE + 0x00F6;
    public const ushort MW_GUILDTACTICSVOLUNTEERING_ACK = MW_BASE + 0x00F7;
    public const ushort MW_GUILDTACTICSVOLUNTEERINGDEL_REQ = MW_BASE + 0x00F8;
    public const ushort MW_GUILDTACTICSVOLUNTEERINGDEL_ACK = MW_BASE + 0x00F9;
    public const ushort MW_GUILDTACTICSVOLUNTEERLIST_REQ = MW_BASE + 0x00FA;
    public const ushort MW_GUILDTACTICSVOLUNTEERLIST_ACK = MW_BASE + 0x00FB;
    public const ushort MW_GUILDTACTICSREPLY_REQ = MW_BASE + 0x00FC;
    public const ushort MW_GUILDTACTICSREPLY_ACK = MW_BASE + 0x00FD;
    public const ushort MW_GUILDTACTICSKICKOUT_REQ = MW_BASE + 0x00FE;
    public const ushort MW_GUILDTACTICSKICKOUT_ACK = MW_BASE + 0x00FF;
    public const ushort MW_GUILDPOINTREWARD_REQ = MW_BASE + 0x0125;
    public const ushort MW_GUILDPOINTREWARD_ACK = MW_BASE + 0x0126;
    public const ushort MW_MONSTERBUY_REQ = MW_BASE + 0x012A;
    public const ushort MW_GUILDSKILLACTION_REQ = MW_BASE + 0x022B;     // map -> world: dwCharID dwKey bAction bCount wParam[]
    public const ushort MW_GUILDSKILLACTION_ACK = MW_BASE + 0x022C;     // world -> map: the same back
    public const ushort MW_UPDATEGUILDCOOLDOWN_ACK = MW_BASE + 0x022D;  // map -> world: dwGuildID wSkillID bLevel BOOL renew BOOL use
    public const ushort MW_UPDATEGUILDCOOLDOWN_REQ = MW_BASE + 0x022E;  // world -> member's map: dwCharID dwKey wSkillID bLevel BOOL BOOL
    public const ushort MW_ADDCOOLDOWN_REQ = MW_BASE + 0x022F;          // world -> every map: dwGuildID wSkillID
    public const ushort MW_MONSTERBUY_ACK = MW_BASE + 0x012B;
    public const ushort MW_GUILDMONEYRECOVER_ACK = MW_BASE + 0x012C;
    public const ushort MW_GUILDTACTICSINVITE_REQ = MW_BASE + 0x0135;
    public const ushort MW_GUILDTACTICSINVITE_ACK = MW_BASE + 0x0136;
    public const ushort MW_GUILDTACTICSANSWER_REQ = MW_BASE + 0x0137;
    public const ushort MW_GUILDTACTICSANSWER_ACK = MW_BASE + 0x0138;
    public const ushort MW_GUILDTACTICSLIST_REQ = MW_BASE + 0x0139;
    public const ushort MW_GUILDTACTICSLIST_ACK = MW_BASE + 0x013A;
    public const ushort MW_CASTLEGUILDCHG_REQ = MW_BASE + 0x012D;   // wCastle dwDef strDef dwAtk strAtk tNext
    public const ushort MW_CASTLEAPPLICANTCOUNT_REQ = MW_BASE + 0x013C; // wCastle dwGuild bCamp bCount
    public const ushort MW_MISSIONENABLE_REQ = MW_BASE + 0x0165;    // bStatus dwStart dwSecond
    public const ushort MW_MISSIONOCCUPY_REQ = MW_BASE + 0x0166;    // bType wLocalID bCountry (world -> every map)
    public const ushort MW_MISSIONOCCUPY_ACK = MW_BASE + 0x0167;    // bType wLocalID bCountry (the capture, to the world)
    public const ushort MW_SKYGARDENENABLE_REQ = MW_BASE + 0x0181;  // bStatus dwSecond bDay dwStart
    public const ushort MW_SKYGARDENOCCUPY_REQ = MW_BASE + 0x0183;  // bType wID bCountry (world -> every map)
    public const ushort MW_SKYGARDENOCCUPY_ACK = MW_BASE + 0x0184;  // bType wID bCountry (the win, to the world)
    public const ushort MW_CASTLEAPPLY_REQ = MW_BASE + 0x0080;      // dwCharID dwKey bResult wCastle dwTarget bCamp (world -> map)
    public const ushort MW_CASTLEAPPLY_ACK = MW_BASE + 0x0081;      // dwCharID dwKey wCastle dwTarget bCamp (to the world)
    public const ushort MW_CASTLEOCCUPY_REQ = MW_BASE + 0x0085;     // bType wCastle dwGuild bCountry strGuild (world -> every map)
    public const ushort MW_CASTLEOCCUPY_ACK = MW_BASE + 0x0086;     // bType wCastle dwGuild bCountry dwLoseGuild (to the world)
    public const ushort MW_ENDWAR_REQ = MW_BASE + 0x0111;           // wCastle
    public const ushort MW_LOCALOCCUPY_REQ = MW_BASE + 0x0069;      // bType wLocalID bCountry dwGuildID strGuild (world -> every map)
    public const ushort MW_LOCALOCCUPY_ACK = MW_BASE + 0x006A;      // bType wLocalID bCountry dwGuildID bCurCountry (to the world)
    public const ushort MW_GAINPVPPOINT_REQ = MW_BASE + 0x0120;     // dwCharID dwPoint bEvent bType bGain strName bClass bLevel
    public const ushort MW_GAINPVPPOINT_ACK = MW_BASE + 0x0121;     // bOwnerType dwOwnerID dwPoint bEvent bType bGain strName bClass bLevel
    public const ushort MW_LOCALRECORD_ACK = MW_BASE + 0x0122;      // dwGuild dwGuildPoint wCount {dwGuildID wCount {dwCharID wKill wDie dwPoint[8]}}
    public const ushort MW_BATTLEMODESTATUS_REQ = MW_BASE + 0x0220; // dwCharID dwKey
    public const ushort MW_BATTLEMODESTATUS_ACK = MW_BASE + 0x0221; // dwCharID dwKey BoW (status next winner) BR (status next type)
    // ---- the BoW / BR queues (MapService.BattleQueue.cs) ----
    public const ushort MW_ADDTOBOWQUEUE_REQ = MW_BASE + 0x0194;     // dwCharID dwKey
    public const ushort MW_BOWTIMEUPDATE_ACK = MW_BASE + 0x0195;     // bStatus dwSecond bDefugelPoints bCraxionPoints (to every map)
    public const ushort MW_PREPAREFORBOW_REQ = MW_BASE + 0x0196;     // dwCharID dwKey bTeam
    public const ushort MW_ADDTOBOWQUEUE_ACK = MW_BASE + 0x019A;     // bResult dwCharID dwKey dwTick
    public const ushort MW_CANCELBOWQUEUE_REQ = MW_BASE + 0x019B;    // dwCharID dwKey
    public const ushort MW_CANCELBOWQUEUE_ACK = MW_BASE + 0x019C;    // bResult dwCharID dwKey dwTick
    public const ushort MW_NOTIFYNONQUEUEDPLAYER_ACK = MW_BASE + 0x019D; // dwCharID dwKey
    public const ushort MW_ADDTOBRQUEUE_REQ = MW_BASE + 0x0201;      // dwCharID dwKey bOnlyReady
    public const ushort MW_BRTIMEUPDATE_ACK = MW_BASE + 0x0202;      // bStatus dwSecond bType (to every map)
    public const ushort MW_BRTEAMMATEADD_ACK = MW_BASE + 0x0203;     // bResult dwCharID dwKey strName
    public const ushort MW_BRTEAMMATEADD_REQ = MW_BASE + 0x0204;     // dwCharID dwKey strName
    public const ushort MW_BRTEAMMATEADDRESULT_ACK = MW_BASE + 0x0205; // dwCharID dwKey bResult strName
    public const ushort MW_UPDATEBRTEAM_ACK = MW_BASE + 0x0206;      // dwCharID dwKey strChief bTeamReady bCount {dwCharID strName bReady}
    public const ushort MW_BRTEAMMATEDEL_REQ = MW_BASE + 0x0207;     // dwCharID dwKey strName
    public const ushort MW_PREPAREFORBR_REQ = MW_BASE + 0x0208;      // dwCharID dwKey wMapID
    public const ushort MW_ADDTOBRQUEUE_ACK = MW_BASE + 0x0209;      // bResult dwCharID dwKey dwTick
    public const ushort MW_VOTEFORBRMAP_REQ = MW_BASE + 0x020A;      // dwCharID dwKey strMap bMode
    public const ushort MW_CMTELEPORTBATTLEMODE_REQ = MW_BASE + 0x0222; // dwCharID dwKey bType
    public const ushort MW_FIRSTGRADEGROUP_REQ = MW_BASE + 0x0143;
    public const ushort MW_FAMERANKUPDATE_REQ = MW_BASE + 0x0169;
    public const ushort MW_FAMERANKUPDATE_ACK = MW_BASE + 0x016A;    // a fame-list place (the tournament's reward)
    public const ushort MW_MONTHRANKRESETCHAR_REQ = MW_BASE + 0x016E;
    public const ushort CS_CREATECOMPANION_REQ = CS_MAP + 0x033E;      // bInven · bItem · strName
    public const ushort CS_DELETECOMPANION_REQ = CS_MAP + 0x033F;      // bSlot
    public const ushort CS_UPDATESPAWNEDCOMPANION_REQ = CS_MAP + 0x0340; // S→C: bSlot (0xFF none)
    public const ushort CS_CREATECOMPANION_ACK = CS_MAP + 0x0341;      // bResult · bSlot
    public const ushort CS_HIDECOMPANION_REQ = CS_MAP + 0x0342;
    public const ushort CS_UPDATECOMPANIONITEMS_REQ = CS_MAP + 0x0343; // S→C: compSlot · sub · wItemID · endTime
    public const ushort CS_USECOMPANIONITEM_REQ = CS_MAP + 0x0344;     // bInven · bItem · bSlot
    public const ushort CS_DELETECOMPITEMS_REQ = CS_MAP + 0x0345;      // bInven · bItem · bTarget · bSlot
    public const ushort CS_CHANGECOMPANIONEFFECT_REQ = CS_MAP + 0x0346; // bSlot
    public const ushort CS_USECOMPANIONPOWDER_REQ = CS_MAP + 0x0348;   // bInven · bItem · bSlot
    public const ushort CS_UPDATECOMPANIONBONUS_REQ = CS_MAP + 0x0349; // S→C: slot · bonusId · bonus
    public const ushort CS_SPOLECNIKRECALL_REQ = CS_MAP + 0x0360;      // a no-op in the C++
    public const ushort CS_ADDSPOLECNIKMON_ACK = CS_MAP + 0x0362;
    public const ushort CS_CHGMODESPOLECNIKMON_REQ = CS_MAP + 0x0363;  // dwMonID · bMode
    public const ushort CS_DELSPOLECNIKMON_ACK = CS_MAP + 0x0364;      // host · id · bExitMap
    public const ushort CS_DELSPOLECNIKMON_REQ = CS_MAP + 0x0365;      // dwMonID · bType
    public const ushort CS_USECOMPRESET_REQ = CS_MAP + 0x0387;         // bInven · bItem · bSlot
    public const ushort CS_FINISHCOMPANIONTRANSFER_ACK = CS_MAP + 0x0390; // C→S: bSlot · bInven · bItem
    // ---- the BoW / BR queues (MapService.BattleQueue.cs); CS_CASHBOWRESPAWN_REQ (0x0371) is CS_PINGMEASUREMENT_ACK's id ----
    public const ushort CS_BOWTIMEUPDATE_ACK = CS_MAP + 0x0366;          // bStatus · dwSecond · bDefugelPoints · bCraxionPoints
    public const ushort CS_REGISTERBOW_REQ = CS_MAP + 0x0369;
    public const ushort CS_REGISTERBOW_ACK = CS_MAP + 0x036A;            // bResult · dwTick
    public const ushort CS_CANCELBOWQUEUE_REQ = CS_MAP + 0x036B;
    public const ushort CS_CANCELBOWQUEUE_ACK = CS_MAP + 0x036C;         // bResult · dwTick
    public const ushort CS_NOTIFYNONQUEUEDPLAYER_ACK = CS_MAP + 0x036D;
    public const ushort CS_EXCHANGEBOWBP_REQ = CS_MAP + 0x0372;          // dwMedals (the BoW server's)
    public const ushort CS_BOWRANKING_REQ = CS_MAP + 0x0374;             // (the BoW server's)
    public const ushort CS_LEAVEBATTLEFIELD_REQ = CS_MAP + 0x0378;       // (the battle servers')
    public const ushort CS_REGISTERBR_REQ = CS_MAP + 0x0379;             // bOnlyReady
    public const ushort CS_BRTIMEUPDATE_ACK = CS_MAP + 0x037A;           // bStatus · dwSecond · bType
    public const ushort CS_BRTEAMMATEADD_REQ = CS_MAP + 0x037B;          // strName
    public const ushort CS_BRTEAMMATEADD_ACK = CS_MAP + 0x037C;          // bResult · strName
    public const ushort CS_BRTEAMMATEADDRESULT_ACK = CS_MAP + 0x037D;    // C→S: bResult · strName
    public const ushort CS_UPDATEBRTEAM_ACK = CS_MAP + 0x037E;           // bTeamReady · bCount {strName · bReady}
    public const ushort CS_BRTEAMMATEDEL_REQ = CS_MAP + 0x037F;          // strName
    public const ushort CS_ADDTOBRQUEUE_ACK = CS_MAP + 0x0383;           // bResult · dwTick
    public const ushort CS_VOTEFORBRMAP_REQ = CS_MAP + 0x0384;           // strMap · bMode
    public const ushort CS_CMTELEPORTBATTLEMODE_REQ = CS_MAP + 0x0500;   // bType (operators)
    public const ushort MW_CREATESPOLECNIKMON_ACK = MW_BASE + 0x0190;
    public const ushort MW_CREATESPOLECNIKMON_REQ = MW_BASE + 0x0191;
    public const ushort MW_SPOLECNIKMONDEL_ACK = MW_BASE + 0x0192;
    public const ushort MW_SPOLECNIKMONDEL_REQ = MW_BASE + 0x0193;
    public const ushort MW_PETRIDING_REQ = MW_BASE + 0x00CE;        // charId · key · dwRiding
    public const ushort MW_PETRIDING_ACK = MW_BASE + 0x00CF;
    public const ushort CS_TERMINATE_MAP_REQ = CS_MAP + 0x01D2; // alias for readability
    public const ushort CS_VERIFYSESSION_ACK = CS_CUSTOM + 0x1001;

    // ================= map <-> world plane (MW) =================
    // Map SENDS these (_ACK), World SENDS the _REQ counterparts (naming is inverted — see class remark).
    public const ushort MW_CONNECT_ACK = MW_BASE + 0x0001;
    public const ushort MW_ADDCHAR_ACK = MW_BASE + 0x0002;
    public const ushort MW_CHARDATA_REQ = MW_BASE + 0x0003;
    public const ushort MW_CHARDATA_ACK = MW_BASE + 0x0004;
    public const ushort MW_ENTERCHAR_REQ = MW_BASE + 0x0005;
    public const ushort MW_ENTERCHAR_ACK = MW_BASE + 0x0006;
    public const ushort MW_ENTERSVR_REQ = MW_BASE + 0x0007;
    public const ushort MW_ENTERSVR_ACK = MW_BASE + 0x0008;
    public const ushort MW_INVALIDCHAR_REQ = MW_BASE + 0x0009;
    public const ushort MW_CONRESULT_REQ = MW_BASE + 0x000A;
    public const ushort MW_CLOSECHAR_REQ = MW_BASE + 0x000B;
    public const ushort MW_CLOSECHAR_ACK = MW_BASE + 0x000C;
    public const ushort MW_DELCHAR_REQ = MW_BASE + 0x000D;
    public const ushort MW_CHARINFO_REQ = MW_BASE + 0x000E;
    public const ushort MW_ROUTE_REQ = MW_BASE + 0x000F;
    public const ushort MW_ROUTE_ACK = MW_BASE + 0x0010;
    public const ushort MW_ADDCONNECT_REQ = MW_BASE + 0x0011;
    public const ushort MW_MAPSVRLIST_REQ = MW_BASE + 0x0012;
    public const ushort MW_MAPSVRLIST_ACK = MW_BASE + 0x0013;
    public const ushort MW_ROUTELIST_REQ = MW_BASE + 0x0014;
    public const ushort MW_RELEASEMAIN_REQ = MW_BASE + 0x0016;
    public const ushort MW_RELEASEMAIN_ACK = MW_BASE + 0x0017;
    public const ushort MW_CHAT_REQ = MW_BASE + 0x003D;
    public const ushort MW_CHAT_ACK = MW_BASE + 0x003E;
    public const ushort MW_RESETCONNECTION_REQ = MW_BASE + 0x0040;
    public const ushort MW_RESETCONNECTION_ACK = MW_BASE + 0x0041;
    public const ushort MW_GETBLOOD_ACK = MW_BASE + 0x008A;   // HP/MP lifedrain (MTYPE_HI/MI) → world grants it to the attacker
    public const ushort MW_CONLIST_REQ = MW_BASE + 0x00BF;
    public const ushort MW_CONLIST_ACK = MW_BASE + 0x00C0;
    public const ushort MW_ENTERSOLOMAP_REQ = MW_BASE + 0x00C1;
    public const ushort MW_ENTERSOLOMAP_ACK = MW_BASE + 0x00C2;
    public const ushort MW_CHECKMAIN_REQ = MW_BASE + 0x00C4;
    public const ushort MW_CHECKMAIN_ACK = MW_BASE + 0x00C5;
    public const ushort MW_CHARMSG_REQ = MW_BASE + 0x011E;
    public const ushort MW_TERMINATE_REQ = MW_BASE + 0x0162;
    public const ushort MW_TERMINATE_ACK = MW_BASE + 0x0163;

    // ================= system / same-port server plane (SM) =================
    public const ushort SM_DELSESSION_REQ = SM_BASE + 0x0001;
    public const ushort SM_QUITSERVICE_REQ = SM_BASE + 0x0002;
    public const ushort SM_TIMER_REQ = SM_BASE + 0x001B;
    public const ushort SM_VALIDMAPSESSION_REQ = SM_BASE + 0x002D;
}

/// <summary>enum TCONNECT_RESULT (NetCode.h) — the byte carried by CS_CONNECT_ACK / MW_CONRESULT_REQ.</summary>
public enum ConnectResult : byte
{
    Success = 0,   // CN_SUCCESS
    NoChannel,     // CN_NOCHANNEL
    NoChar,        // CN_NOCHAR
    AlreadyExist,  // CN_ALREADYEXIST
    InvalidVer,    // CN_INVALIDVER
    Internal,      // CN_INTERNAL
}

/// <summary>enum CHAT_GROUP (NetCode.h) — the scope byte on CS_CHAT_REQ / CS_CHAT_ACK.</summary>
public enum ChatGroup : byte
{
    Whisper = 0,   // CHAT_WHISPER
    Near,          // CHAT_NEAR   (local cell)
    Map,           // CHAT_MAP
    World,         // CHAT_WORLD
    Party,         // CHAT_PARTY
    Guild,         // CHAT_GUILD
    Force,         // CHAT_FORCE
    Operator,      // CHAT_OPERATOR
    Tactics,       // CHAT_TACTICS
    Show,          // CHAT_SHOW
}

/// <summary>enum TMOVEITEM_RESULT (NetCode.h) — the byte carried by CS_MOVEITEM_ACK.</summary>
public enum MoveItemResult : byte
{
    Success = 0,     // MI_SUCCESS
    NoDestInven,     // MI_NODESTINVEN
    NoSrcInven,      // MI_NOSRCINVEN
    NoSrcItem,       // MI_NOSRCITEM
    SamePos,         // MI_SAMEPOS
    CannotEquip,     // MI_CANNOTEQUIP (slot mask)
    InvenFull,       // MI_INVENFULL
    Dealing,         // MI_DEALING
    BothHandWeapon,  // MI_BOTHHANDWEAPON
    NoSkill,         // MI_NOSKILL
    NoMatchClass,    // MI_NOMATCHCLASS (class mask)
    LowLevel,        // MI_LOWLEVEL
    Block,           // MI_BLOCK
    Dead,            // MI_DEAD
    CantDrop,        // MI_CANTDROP
    Wrap,            // MI_WRAP (sealed/wrapped item)
}

/// <summary>enum TITEMUSE_RESULT (NetCode.h) — the byte carried by CS_ITEMUSE_ACK.</summary>
public enum ItemUseResult : byte
{
    Success = 0,     // IU_SUCCESS
    NotFound,        // IU_NOTFOUND
    NeedTime,        // IU_NEEDTIME (cooldown)
    Full,            // IU_FULL (HP/MP already at max)
    NeedLevel,       // IU_NEEDLEVEL
    Dealing,         // IU_DEALING
    Riding,          // IU_RIDING
    NotParty,        // IU_NOTPARTY
    NotPartyFound,   // IU_NOTPARTYFOUND
    TargetNotFound,  // IU_TARGETNOTFOUND
    TargetBusy,      // IU_TARGETBUSY
    TargetDeny,      // IU_TARGETDENY
    OverlapPremium,  // IU_OVERLAPPREMIUM
    OverlapExpBonus, // IU_OVERLAPEXPBONUS
    Wrapping,        // IU_WRAPPING (sealed item)
    Arena,           // IU_ARENA
}

/// <summary>enum ITEMREPAIR_RESULT (NetCode.h) — the byte carried by CS_DURATIONREP_ACK (values 4-6 are
/// refine-only; the repair handler uses SUCCESS/NOTFOUND/NEEDMONEY/DISALLOW/NPCCALLERROR/INVALIDPOS).</summary>
public enum ItemRepairResult : byte
{
    Success = 0,     // ITEMREPAIR_SUCCESS
    NotFound,        // ITEMREPAIR_NOTFOUND
    NeedMoney,       // ITEMREPAIR_NEEDMONEY
    Disallow,        // ITEMREPAIR_DISALLOW (item's template forbids repair)
    MaxRefine,       // ITEMREPAIR_MAXREFINE (refine-only)
    LevelDiff,       // ITEMREPAIR_LEVELDIFF (refine-only)
    Fail,            // ITEMREPAIR_FAIL (refine-only)
    NpcCallError,    // ITEMREPAIR_NPCCALLERROR (portable-smith item invalid)
    InvalidPos,      // ITEMREPAIR_INVALIDPOS (portable smith not allowed on this map)
}

/// <summary>enum TSKILL_RESULT (NetCode.h:352-388) — the <c>bResult</c> byte carried by CS_SKILLUSE_ACK
/// (sequential from 0). The map's <c>OnCS_SKILLUSE_REQ</c> emits SUCCESS/NOTFOUND/SPEEDYUSE/NEEDMP/NEEDHP
/// (the other codes gate unported subsystems — tournament/arena/premium/peace-zone/buffs/items).</summary>
public enum SkillUseResult : byte
{
    Success = 0,     // SKILL_SUCCESS
    NotFound,        // SKILL_NOTFOUND (unknown/unlearned skill)
    Already,         // SKILL_ALREADY
    NeedParent,      // SKILL_NEEDPARENT
    NeedMoney,       // SKILL_NEEDMONEY
    NeedLevelUp,     // SKILL_NEEDLEVELUP
    SpeedyUse,       // SKILL_SPEEDYUSE (still on cooldown / recast too fast)
    NeedMp,          // SKILL_NEEDMP
    NeedHp,          // SKILL_NEEDHP
    UnsuitWeapon,    // SKILL_UNSUITWEAPON
    NeedPrevAct,     // SKILL_NEEDPREVACT
    MatchClass,      // SKILL_MATCHCLASS
    NeedSkillPoint,  // SKILL_NEEDSKILLPOINT
    WrongTarget,     // SKILL_WRONGTARGET
    TooClose,        // SKILL_TOOCLOSE
    TooFar,          // SKILL_TOOFAR
    WrongDir,        // SKILL_WRONGDIR
    WrongRegion,     // SKILL_WRONGREGION
    NeedGround,      // SKILL_NEEDGROUND
    Trans,           // SKILL_TRANS
    Hide,            // SKILL_HIDE
    Stun,            // SKILL_STUN
    Dead,            // SKILL_DEAD
    Silence,         // SKILL_SILENCE
    Mode,            // SKILL_MODE
    PeaceZone,       // SKILL_PEACEZONE
    NoTarget,        // SKILL_NOTARGET
    NotMoveSkill,    // SKILL_NOTMOVESKILL
    ActionLock,      // SKILL_ACTIONLOCK
    NeedItem,        // SKILL_NEEDITEM
    HaveChild,       // SKILL_HAVECHILD
    NotInit,         // SKILL_NOTINIT
    CannotSee,       // SKILL_CANNOTSEE
}

/// <summary>enum TITEMBUY_RESULT (NetCode.h:489) — the byte carried by CS_ITEMBUY_ACK.</summary>
public enum ItemBuyResult : byte
{
    Success = 0,   // ITEMBUY_SUCCESS
    NotFound,      // ITEMBUY_NOTFOUND (unknown NPC / item not in stock / bCount 0)
    NeedMoney,     // ITEMBUY_NEEDMONEY
    CantPush,      // ITEMBUY_CANTPUSH (bags full)
    Dealing,       // ITEMBUY_DEALING (in a trade — trade unported)
    NpcCallError,  // ITEMBUY_NPCCALLERROR (portable-NPC-call item invalid)
    InvalidPos,    // ITEMBUY_INVALIDPOS (portable NPC call not allowed on this map)
}

/// <summary>enum TITEMSELL_RESULT (NetCode.h:500) — the byte carried by CS_ITEMSELL_ACK.</summary>
public enum ItemSellResult : byte
{
    Success = 0,   // ITEMSELL_SUCCESS
    NotFound,      // ITEMSELL_NOTFOUND (bag/slot empty, count mismatch)
    Dealing,       // ITEMSELL_DEALING (in a trade — trade unported)
    CantSell,      // ITEMSELL_CANTSELL (item's template forbids selling)
    NpcCallError,  // ITEMSELL_NPCCALLERROR (portable-NPC-call item invalid)
    InvalidPos,    // ITEMSELL_INVALIDPOS (portable NPC call not allowed on this map)
}

/// <summary>enum QUEST_TYPE (NetCode.h:1831) — <c>QUESTTEMP.m_bType</c>, the subtype dispatched by
/// <c>CreateQuest</c>. The port implements the foundational four (NpcTalk offer, Mission begin, Complete
/// turn-in, GiveItem) + DefTalk (no-op); the other 15 are deferred (see PORT_STATUS.md).</summary>
public enum QuestType : byte
{
    None = 0, DefTalk = 1, GiveSkill, GiveItem, DropItem, SpawnMon, Teleport, Complete, Mission,
    Routing, NpcTalk, DropQuest, ChapterMsg, Switch, DieMon, DefendSkill, DeleteItem, SendPost,
    Craft, Regen, Guild,
}

/// <summary>enum TQUEST_RESULT (NetCode.h:426) — the <c>bResult</c> byte on CS_QUESTCOMPLETE_ACK.</summary>
public enum QuestResult : byte
{
    Success = 0,      // QR_SUCCESS
    Term,             // QR_TERM (a term is not yet satisfied)
    InventoryFull,    // QR_INVENTORYFULL
    Drop,             // QR_DROP (abandoned)
}

/// <summary>enum QUEST_TERM_STATUS (NetCode.h:1823) — the per-term <c>bStatus</c> on the update/list packets.</summary>
public enum QuestTermStatus : byte
{
    Run = 0,          // QTS_RUN (still in progress)
    Success,          // QTS_SUCCESS (satisfied)
    Failed,           // QTS_FAILED (e.g. timer expired)
}

/// <summary>enum MONITEMTAKE_RESULT (NetCode.h:510) — the byte carried by CS_MONITEMTAKE_ACK.</summary>
public enum MonItemTakeResult : byte
{
    Success = 0,   // MIT_SUCCESS
    FullInven,     // MIT_FULLINVEN
    NotFound,      // MIT_NOTFOUND
    Authority,     // MIT_AUTHORITY (party loot-mode denial)
    Dealing,       // MIT_DEALING
    Lottery,       // MIT_LOTTERY
}

/// <summary>enum CABINET_RESULT (NetCode.h:619) — the <c>bResult</c> byte on CS_CABINETOPEN_ACK /
/// CS_CABINETITEMLIST_ACK (sequential from 0).</summary>
public enum CabinetResult : byte
{
    Success = 0,     // CABINET_SUCCESS
    NotUse,          // CABINET_NOTUSE (cabinet not opened)
    NeedMoney,       // CABINET_NEEDMONEY
    Already,         // CABINET_ALREADY (already opened)
    Full,            // CABINET_FULL (16 items, no mergeable stack)
    NpcCallError,    // CABINET_NPCCALLERROR (the call-scroll path — deferred)
    InvalidPos,      // CABINET_INVALIDPOS (call scroll off the allowed maps — deferred)
    Max,             // CABINET_MAX (all 3 cabinets exist / id out of range)
}

/// <summary>enum PARTY_TYPE (NetCode.h:1960) — a party member's <c>m_bPartyType</c> = the loot/exp share mode.
/// <c>Solo</c> opts the member out of all party sharing.</summary>
public enum PartyType : byte
{
    Free = 0,   // PT_FREE — free-for-all loot
    Solo,       // PT_SOLO — opts out of party exp/loot sharing (GetPartyID → 0)
    Hunter,     // PT_HUNTER — only the top-aggro hunter may loot
    Lottery,    // PT_LOTTERY — dice/roll
    Chief,      // PT_CHIEF — loot goes to the party chief
    Order,      // PT_ORDER — round-robin (world-assigned)
}

/// <summary>enum OWNER_TYPE (NetCode.h:1972) — a monster's keeper class: who owns its exp/loot.</summary>
public enum OwnerType : byte
{
    None = 0,   // OWNER_NONE
    Private,    // OWNER_PRIVATE — a single character (its cumulative-damage keeper)
    Party,      // OWNER_PARTY — a party (keeper id = the party id)
}

/// <summary>enum DEALITEM_RESULT (NetCode.h:677) — the <c>bResult</c> byte on CS_DEALITEMEND_ACK.</summary>
public enum DealResult : byte
{
    Success = 0,   // DEALITEM_SUCCESS
    Deny,          // DEALITEM_DENY (target refused / block list)
    Busy,          // DEALITEM_BUSY (a party is mid-action / pairing broke)
    NoTarget,      // DEALITEM_NOTARGET
    Dealing,       // DEALITEM_DEALING (declared, unused)
    Cancel,        // DEALITEM_CANCEL (bOkey==0)
    NoMoney,       // DEALITEM_NOMONEY
    OverMoney,     // DEALITEM_OVERMONEY (declared, unused)
    NoInven,       // DEALITEM_NOINVEN
    NoItem,        // DEALITEM_NOITEM
    InvalidItem,   // DEALITEM_INVALIDITEM (duplicate slot offered)
    Enemy,         // DEALITEM_ENEMY (declared, unused)
    CantRecv,      // DEALITEM_CANTRECV (partner bag full)
    CantDeal,      // DEALITEM_CANTDEAL (nation/eligibility gate)
}

/// <summary>enum DEAL_STATUS (NetCode.h:233) — the paired <c>m_bStatus</c> and per-side <c>m_bDealing</c> values.</summary>
public enum DealStatus : byte
{
    Ready = 0,   // DEAL_READY (idle)
    Wait,        // DEAL_WAIT (window open, no offer yet — m_bDealing)
    Start,       // DEAL_START (paired window open — the ">= START" in-deal lock threshold)
    AddItem,     // DEAL_ADDITEM (an offer was submitted — m_bDealing)
    Conform,     // DEAL_CONFORM (locked/confirmed)
}

/// <summary>enum STORE_RESULT (NetCode.h:695) — the <c>bResult</c> byte on the store open/close/buy acks.</summary>
public enum StoreResult : byte
{
    Success = 0,     // STORE_SUCCESS
    Fail,            // STORE_FAIL (bad name / no target / not storing)
    ItemNoItem,      // STORE_ITEM_NOITEM
    ItemNotDeal,     // STORE_ITEM_NOTDEAL (untradable / duplicate slot)
    ItemOverMoney,   // STORE_ITEM_OVERMONEY (declared, unused)
    ItemNeedMoney,   // STORE_ITEM_NEEDMONEY
    ItemNoItemCount, // STORE_ITEM_NOITEMCOUNT
    ItemInvenFull,   // STORE_ITEM_INVENFULL (buyer bag full)
}

/// <summary>enum REVIVAL_TYPE (NetCode.h:2169) — the CS_REVIVAL_REQ mode byte. NPC = revive at a town/NPC
/// (30% HP/MP), GHOST = revive-in-place as a ghost (40%), HELP = revived by another player's resurrection
/// skill (full + skill bonus — the deferred CS_REVIVALASK flow).</summary>
public enum RevivalType : byte
{
    Npc = 0,    // REVIVAL_NPC
    Ghost,      // REVIVAL_GHOST
    Help,       // REVIVAL_HELP
}

/// <summary>enum TREPARE_TYPE (NetCode.h) — the CS_DURATIONREP_REQ mode byte.</summary>
public enum RepairType : byte
{
    Normal = 0,      // RPT_NORMAL — one item (bInven/bItem)
    Equip,           // RPT_EQUIP — all equipped items
    All,             // RPT_ALL — every repairable item in every bag
}

/// <summary>enum TSVR_TYPE (NetCode.h) — high byte of a server's <c>wServerID</c>.</summary>
public enum SvrType : byte
{
    None = 0,
    Control,
    Login,
    World,
    Map,      // SVR_MAP = 4
    Relay,
    Kick,
}

/// <summary>Assorted protocol constants + helpers from THIS repo's headers.</summary>
public static class Proto
{
    public const ushort ClientVersion = 0x2918; // TVERSION (ProtocolBase.h)

    public const byte SessionClient = 1;         // SESSION_CLIENT (TNetLib/Session.h)
    public const byte SessionServer = 2;         // SESSION_SERVER

    public const byte SvrGrpMapSvr = 4;          // SVRGRP_MAPSVR (CTProtocol.h)

    public const int MaxName = 50;               // MAX_NAME
    public const int MaxHotkeyPos = 12;          // MAX_HOTKEY_POS

    public const int DefaultGamePort = 5816;     // GamePort (Configurations/TMapSvr.ini)
    public const int DefaultWorldPort = 3816;    // WorldPort (Configurations/TMapSvr.ini)

    // Item storage constants (NetCode.h) — see TITEMTABLE partitioning.
    public const byte OwnerChar = 0;             // TOWNER_CHAR
    public const byte StorageInven = 0;          // STORAGE_INVEN
    public const byte StorageCabinet = 1;        // STORAGE_CABINET (the player warehouse)
    public const byte StoragePost = 2;           // STORAGE_POST (mail — excluded from the char-item load)
    public const byte InvenEquip = 0xFE;         // INVEN_EQUIP   (equipped gear)
    public const byte InvenDefault = 0xFF;       // INVEN_DEFAULT (main backpack)
    public const byte InvenNull = 0xFC;          // INVEN_NULL    (drop/destroy pseudo-target)

    // EQUIP_SLOT indices (NetCode.h) — the equip-slot ids used by the move/equip handler.
    public const byte EsPrmWeapon = 0, EsSndWeapon = 1, EsLongWeapon = 2;

    /// <summary>INVALID_SLOT (NetCode.h) — the "no slot" sentinel returned by the inventory slot finders
    /// (a two-hander has a real sub-slot; a 1H/shield/armour has <c>m_bSubSlotID == INVALID_SLOT</c>).</summary>
    public const byte InvalidSlot = 0xFF;

    // The anti-tamper key shared by the login + connect checksums.
    public const long ConnectChecksumKey = 0x336c3aebf71a8b08;

    /// <summary><c>MAKEWORD(bServerID, bServerType)</c> — the wServerID sent in MW_CONNECT_ACK.</summary>
    public static ushort MakeServerId(byte serverId, SvrType type) => (ushort)(serverId | ((byte)type << 8));

    public static byte ServerIdOf(ushort wId) => (byte)(wId & 0xFF);
    public static SvrType ServerTypeOf(ushort wId) => (SvrType)(byte)(wId >> 8);

    /// <summary>Converts a dotted IPv4 string to the little-endian uint produced by <c>inet_addr</c>.</summary>
    public static uint IpToUInt(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return 0;
        var parts = ip.Split('.');
        if (parts.Length != 4) return 0;
        uint result = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out byte b)) return 0;
            result |= (uint)b << (8 * i); // inet_addr packs the first octet into the low byte
        }
        return result;
    }

    /// <summary>
    /// Recomputes the CS_CONNECT_REQ anti-tamper checksum exactly as the client (CSSender.cpp) and map
    /// server (CSHandler.cpp OnCS_CONNECT_REQ) do: a 32-bit wrapping product zero-extended to 64-bit,
    /// then a small mixing loop keyed by <see cref="ConnectChecksumKey"/>.
    /// </summary>
    public static long ComputeConnectChecksum(ushort version, uint userId, uint key, uint charId)
    {
        uint product = unchecked((uint)version * userId + key * charId); // 32-bit wrap (DWORD math)
        long checksum = product;                                         // zero-extend (non-negative)
        long index = checksum % 8;                                       // % sizeof(INT64)
        long body = checksum / 8;
        for (long i = 0; i < index; i++)
        {
            checksum ^= body;
            checksum += ConnectChecksumKey;
        }
        return checksum;
    }
}
