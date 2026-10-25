# The game client (TClient.vcxproj)

set(TClient_SOURCES
    ../../Lib/Own/GameConfig/GameConfig.cpp
    ArenaRanking.cpp
    BattleRankProgressDlg.cpp
    BattleRankTex.cpp
    BRPannels.cpp
    BRRankingDlg.cpp
    BRTeamDlg.cpp
    BRTRanking.cpp
    CompStyleDlg.cpp
    CSHandler.cpp
    CSSender.cpp
    CSSenderAll.cpp
    Curse.cpp
    GuildSkillsDlg.cpp
    HwidManager.cpp
    MacroProtection.cpp
    Outro.cpp
    SHA1.cpp
    SHA512.cpp
    SmoothScrolling.cpp
    StdAfx.cpp
    TAidCountry.cpp
    TArenaReg.cpp
    TAuctionBasket.cpp
    TAuctionCategory.cpp
    TAuctionCommander.cpp
    TAuctionCtrlList.cpp
    TAuctionInnerFrame.cpp
    TAuctionMainFrame.cpp
    TAuctionMsgBox.cpp
    TAuctionMyBid.cpp
    TAuctionRegist.cpp
    TAuctionSearch.cpp
    TBattleInvenDlg.cpp
    TBFGAlarm.cpp
    TBFGStatus.cpp
    TBlockListDlg.cpp
    TBodGuildRankClass.cpp
    TBodRankClass.cpp
    TBodRankDlg.cpp
    TBodRankTexture.cpp
    TBowBPTradeDlg.cpp
    TBowGuildRankClass.cpp
    TBowRankClass.cpp
    TBowRankingDlg.cpp
    TBowRegisterDlg.cpp
    TBowRegisterWaitingDlg.cpp
    TBowRespawnDlg.cpp
    TBoxOpen.cpp
    TBoxOpenList.cpp
    TCabinetDlg.cpp
    TCandidateFrame.cpp
    TCashCabinetDlg.cpp
    TChallengeEvent.cpp
    TChallengeItem.cpp
    TChallengeWaiting.cpp
    TCharInfoNewDlg.cpp
    TCharInfoNewInner.cpp
    TCharInfoNewPVPDlg.cpp
    TCharInfoNewWarDlg.cpp
    TChatCommander.cpp
    TChatFrame.cpp
    TChatList.cpp
    TChatMenuNew.cpp
    TChatTabButton.cpp
    TCinematicDlg.cpp
    TClient.cpp
    TClientArrow.cpp
    TClientBSP.cpp
    TClientCAM.cpp
    TClientChar.cpp
    TClientCompanion.cpp
    TClientCustomCloak.cpp
    TClientFake.cpp
    TClientFlag.cpp
    TClientGame.cpp
    TClientGate.cpp
    TClientGM.cpp
    TClientGuildMark.cpp
    TClientInven.cpp
    TClientItem.cpp
    TClientKEY.cpp
    TClientMaintain.cpp
    TClientMAP.cpp
    TClientMonster.cpp
    TClientMoveObj.cpp
    TClientMsgBox.cpp
    TClientNET.cpp
    TClientNICEventSink.cpp
    TClientNpc.cpp
    TClientNumber.cpp
    TClientObjBase.cpp
    TClientObject.cpp
    TClientPath.cpp
    TClientPet.cpp
    TClientQClass.cpp
    TClientQuest.cpp
    TClientRecall.cpp
    TClientSession.cpp
    TClientSfx.cpp
    TClientSkill.cpp
    TClientSpolecnik.cpp
    TClientSwitch.cpp
    TClientTalkBox.cpp
    TClientTerm.cpp
    TClientUIBase.cpp
    TClientWnd.cpp
    TColorPicker.cpp
    TCommunityDlg.cpp
    TCompanionAttrs.cpp
    TCompanionInner.cpp
    TCompanionMiniUI.cpp
    TCompanionStats.cpp
    TCraftQuestDlg.cpp
    TCtrlList.cpp
    TCustomCloakDlg.cpp
    TDetailInfo.cpp
    TDetailInfoDlg.cpp
    TDetailInfoManager.cpp
    TDetailNameDlg.cpp
    TDurationFrame.cpp
    TDynamicHelpDlg.cpp
    TFameBaseDlg.cpp
    TFameListDlg.cpp
    TFameRank.cpp
    TFrameGroupBase.cpp
    TGameSettingFrame.cpp
    TGaugePannel.cpp
    TGuildAppInnerFrame.cpp
    TGuildApplicationDlg.cpp
    TGuildCabinetDlg.cpp
    TGuildCmdHelpDlg.cpp
    TGuildCommander.cpp
    TGuildExpDonDlg.cpp
    TGuildJoinNewDlg.cpp
    TGuildMainNewDlg.cpp
    TGuildMarkMakeDlg.cpp
    TGuildMemberFrame.cpp
    TGuildMembersNewDlg.cpp
    TGuildMercenaryNewDlg.cpp
    TGuildMoneyDonDlg.cpp
    TGuildNormalFrame.cpp
    TGuildNotifyDlg.cpp
    TGuildOfferFrame.cpp
    TGuildPointLogFrame.cpp
    TGuildPointRewardFrame.cpp
    TGuildPvPRecordFrame.cpp
    TGuildRegularOffer.cpp
    TGuildRTTypeFrame.cpp
    TGuildSeekFrame.cpp
    TGuildSummaryNewDlg.cpp
    TGuildTacticsOffer.cpp
    TGuildTerritoryFrame.cpp
    TGuildVolunteerFrame.cpp
    TGuildWarInfoNewDlg.cpp
    THelpFrame.cpp
    TInvenDlg.cpp
    TItemCraftDlg.cpp
    TItemExtPeriodDlg.cpp
    TItemGambleDlg.cpp
    TItemRefineDlg.cpp
    TItemRegBox.cpp
    TItemRegGuildDlg.cpp
    TItemRepairDlg.cpp
    TItemSlotBaseDlg.cpp
    TItemUpCashDlg.cpp
    TItemUpDlg.cpp
    TItemUpGaugeDlg.cpp
    TKeySettingDefines.cpp
    TKeySettingDlg.cpp
    TLimitEdit.cpp
    TListEx.cpp
    TMagicItemShopDlg.cpp
    TMailAskMoneyDlg.cpp
    TMailListFrame.cpp
    TMailRecvDlg.cpp
    TMailSendDlg.cpp
    TMailSender.cpp
    TMaintainPannel.cpp
    TMainUI.cpp
    TMessengerBase.cpp
    TMessengerChat.cpp
    TMessengerCommander.cpp
    TMiniDump.cpp
    TMinimapDlg.cpp
    TMiniPopupDlg.cpp
    TMsgChatMemberList.cpp
    TMsgGroupList.cpp
    TNationOption.cpp
    TNetGM.cpp
    TNetHandler.cpp
    TNetSender.cpp
    TNewAuctionHouseDlg.cpp
    TNewQuestDlg.cpp
    TNormalCabinetDlg.cpp
    TNPCTalkDlg.cpp
    TNumberEdit.cpp
    TPartyCommander.cpp
    TPartyItemLottery.cpp
    TPetDlg.cpp
    TPetMakeDlg.cpp
    TPetManageDlg.cpp
    TPopupMenu.cpp
    TPopupNewMenu.cpp
    TPortalShopDlgNew.cpp
    TPotionPannel.cpp
    TPremiumSkillsDlg.cpp
    TPrivateShopDlg.cpp
    TProgressRegDlg.cpp
    TQuestNewDlg.cpp
    TRankingNewDlg.cpp
    TRankingNewMain.cpp
    TRelayHandler.cpp
    TRelaySender.cpp
    TRentSkillDlg.cpp
    TRpcDlg.cpp
    TRpcRewardDlg.cpp
    TRpsRewardList.cpp
    TRSCSDlg.cpp
    TScenePlayer.cpp
    TSecuritySystemDlg.cpp
    TSelectCountry.cpp
    TSettingFrame.cpp
    TShopBaseDlg.cpp
    TShopDlg.cpp
    TShopItem.cpp
    TSiegeEnd.cpp
    TSiegeGodBall.cpp
    TSiegeGodTower.cpp
    TSiegeManager.cpp
    TSiegeUI.cpp
    TSkillDlg.cpp
    TSkygardenEnd.cpp
    TSkygardenMap.cpp
    TSMSDlg.cpp
    TSoulLotteryDlg.cpp
    TSoulLotteryWinDlg.cpp
    TSoulmateFrame.cpp
    TSystemInfo.cpp
    TSystemSettingFrame.cpp
    TTacticsInviteDlg.cpp
    TTextLinkData.cpp
    TTextLinker.cpp
    TTextPopup.cpp
    TTitleDlg.cpp
    TTournament.cpp
    TTournamentEvent.cpp
    TTournamentGage.cpp
    TTournamentMode.cpp
    TTournamentResult.cpp
    TTournamentSelectTarget.cpp
    TTournamentSelectWinner.cpp
    TTradeDlg.cpp
    TUpItemSellDlg.cpp
    TUtilityBarDlg.cpp
    TWelcome.cpp
    TWorldmapDlg.cpp
)

# TClient.dpiaware.manifest = the dpiAware=true part MSBuild adds by default (EnableDpiAwareness).
if(WIN32)
    add_executable(TClient WIN32 ${TClient_SOURCES} TClient.rc TClient.dpiaware.manifest)
else()
    add_executable(TClient ${TClient_SOURCES})
endif()
target_compile_definitions(TClient PRIVATE $<$<PLATFORM_ID:Windows>:WIN32> NEW_IF)

# The mouse cursors (.cur and animated .ani files), compiled in as byte arrays: the window makes
# its SDL cursors from them (CTachyonInput::LoadCursor). TClientCursors.h is generated here.
set(TClient_CURSORS Hand.cur Sword.cur Talk.cur ROUTING.CUR cursor1.cur)
set(TClient_CURSORS_H "${CMAKE_CURRENT_BINARY_DIR}/TClientCursors.h")
set(_TCURSORS "// Generated by Client/TClient/CMakeLists.txt from res/*.cur.\n\n")
foreach(_TCURSOR ${TClient_CURSORS})
    set(_TCURSOR_FILE "${CMAKE_CURRENT_SOURCE_DIR}/res/${_TCURSOR}")
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_TCURSOR_FILE}")
    file(READ "${_TCURSOR_FILE}" _TCURSOR_HEX HEX)
    string(REGEX REPLACE "([0-9a-f][0-9a-f])" "0x\\1," _TCURSOR_HEX "${_TCURSOR_HEX}")
    string(REGEX REPLACE "[^A-Za-z0-9]" "_" _TCURSOR_NAME "${_TCURSOR}")
    string(APPEND _TCURSORS "static const BYTE s_vCURSOR_${_TCURSOR_NAME}[] = {${_TCURSOR_HEX}};\n")
endforeach()
file(CONFIGURE OUTPUT "${TClient_CURSORS_H}" CONTENT "${_TCURSORS}" @ONLY)

target_include_directories(TClient PRIVATE
    "${CMAKE_CURRENT_BINARY_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${TCLIENT_LIB_OWN}/GameConfig"
    "${TCLIENT_LIB_OWN}/TProtocol/Include"
    "${TCLIENT_ROOT}/Client/History Files"
)
target_link_libraries(TClient PRIVATE
    tclient_options
    EngineLib
    TChart
    TCML
    TComp
)
if(WIN32)
    target_link_libraries(TClient PRIVATE
        HwidLib
        dxguid strmiids
        advapi32 imm32 version dbghelp psapi
    )
    if(NOT TCLIENT_NO_D3D9)
        target_link_libraries(TClient PRIVATE d3d9 d3dx9)
    endif()
    target_link_options(TClient PRIVATE
        /DEBUG
        /INCREMENTAL:NO
        /LARGEADDRESSAWARE
        $<$<STREQUAL:${TCLIENT_ARCH},x86>:/SAFESEH:NO>
        /OPT:REF
        /OPT:NOICF
        /MAP
        /MAPINFO:EXPORTS
        "/MANIFESTUAC:level='requireAdministrator' uiAccess='false'"
    )
endif()
# A TARGET post-build command must be declared in the directory that creates the target.
if(TCLIENT_DEPLOY_TO_GAME)
    add_custom_command(TARGET TClient POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:TClient>" "${TCLIENT_ROOT}/Game/TClient.exe"
        COMMENT "Deploying TClient.exe to Game/")
endif()

# Fonts with Arial's metrics, for the text drawn from font files where Arial is not installed.
add_custom_command(TARGET TClient POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "$<TARGET_FILE_DIR:TClient>/Fonts"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${TCLIENT_LIB_3RD}/LiberationFonts/LiberationSans-Regular.ttf"
        "${TCLIENT_LIB_3RD}/LiberationFonts/LiberationSans-Bold.ttf"
        "${TCLIENT_LIB_3RD}/LiberationFonts/LiberationSans-Italic.ttf"
        "${TCLIENT_LIB_3RD}/LiberationFonts/LiberationSans-BoldItalic.ttf"
        "${TCLIENT_LIB_3RD}/LiberationFonts/LICENSE"
        "$<TARGET_FILE_DIR:TClient>/Fonts"
)

# Elsewhere the executable's resources are a table the compat header's FindResource reads.
if(NOT WIN32)
    set(TClient_RESOURCES_CPP "${CMAKE_CURRENT_BINARY_DIR}/TClientResources.cpp")
    set(_TRES "// Generated by Client/TClient/CMakeLists.txt from the resources of TClient.rc.\n\n#include <TWinCompat.h>\n\n")
    set(_TRES_TABLE "")
    foreach(_TRES_ITEM "TEXT;719;res/Curse.txt" "ICON;128;res/TClient.ico")
        list(GET _TRES_ITEM 0 _TRES_TYPE)
        list(GET _TRES_ITEM 1 _TRES_ID)
        list(GET _TRES_ITEM 2 _TRES_FILE)
        set(_TRES_PATH "${CMAKE_CURRENT_SOURCE_DIR}/${_TRES_FILE}")
        set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_TRES_PATH}")
        file(READ "${_TRES_PATH}" _TRES_HEX HEX)
        string(LENGTH "${_TRES_HEX}" _TRES_LENGTH)
        math(EXPR _TRES_SIZE "${_TRES_LENGTH} / 2")
        string(REGEX REPLACE "([0-9a-f][0-9a-f])" "0x\\1," _TRES_HEX "${_TRES_HEX}")
        string(APPEND _TRES "static const BYTE s_vRES_${_TRES_TYPE}_${_TRES_ID}[] = {${_TRES_HEX}};\n")
        string(APPEND _TRES_TABLE "\t{ \"${_TRES_TYPE}\", ${_TRES_ID}, s_vRES_${_TRES_TYPE}_${_TRES_ID}, ${_TRES_SIZE} },\n")
    endforeach()
    string(APPEND _TRES "\nextern const TWINCOMPAT_RESOURCE g_vTWINCOMPAT_RESOURCE[] = {\n${_TRES_TABLE}\t{ NULL, 0, NULL, 0 } };\n")
    file(CONFIGURE OUTPUT "${TClient_RESOURCES_CPP}" CONTENT "${_TRES}" @ONLY)
    # The hardware ID's hash (HwidLib has it on Windows).
    target_sources(TClient PRIVATE "${TClient_RESOURCES_CPP}" __Sha256.cpp)
endif()

# Resources are Korean (Culture=0x0412).
if(WIN32)
    set_source_files_properties(TClient.rc PROPERTIES COMPILE_OPTIONS "/l0x0412" COMPILE_DEFINITIONS KOREA)
endif()
