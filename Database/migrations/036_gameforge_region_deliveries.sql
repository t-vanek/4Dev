-- Audited sources and reconstruction policies: Gameforge/high-level-regions/plan.json
USE [TGame_gsp];
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
CREATE TABLE #Gf_TNPCCHART([wID] SMALLINT NOT NULL,[NC_szName2] nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL,[szName] nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL,[bType] TINYINT NOT NULL,[dwClass] INT NOT NULL,[bCountryID] TINYINT NOT NULL,[wLocalID] SMALLINT NOT NULL,[bCondition] TINYINT NOT NULL,[bDiscountRate] TINYINT NOT NULL,[bAddProb] TINYINT NOT NULL,[wItemID] SMALLINT NOT NULL,[wMapID] SMALLINT NOT NULL,[fPosX] FLOAT(53) NOT NULL,[fPosY] FLOAT(53) NOT NULL,[fPosZ] FLOAT(53) NOT NULL,[szLocal] varchar(100) COLLATE DATABASE_DEFAULT NOT NULL);
INSERT #Gf_TNPCCHART ([wID],[NC_szName2],[szName],[bType],[dwClass],[bCountryID],[wLocalID],[bCondition],[bDiscountRate],[bAddProb],[wItemID],[wMapID],[fPosX],[fPosY],[fPosZ],[szLocal]) VALUES (26704,N'Elfský obchodník se spotřebním zbožím',N'Lamian',2,255,3,0,0,0,0,0,0,3979.780029296875,65.010002136230469,1260.93994140625,N''),
(26718,N'Elfský obchodník se spotřebním zbožím',N'Elina',2,255,3,0,0,0,0,0,0,3553.27001953125,51.009998321533203,2592.6201171875,N'');
IF EXISTS(SELECT 1 FROM #Gf_TNPCCHART n JOIN dbo.[TNPCCHART] t ON t.[wID]=n.[wID] WHERE EXISTS(SELECT t.[wID],t.[NC_szName2],t.[szName],t.[bType],t.[dwClass],t.[bCountryID],t.[wLocalID],t.[bCondition],t.[bDiscountRate],t.[bAddProb],t.[wItemID],t.[wMapID],t.[fPosX],t.[fPosY],t.[fPosZ],t.[szLocal] EXCEPT SELECT n.[wID],n.[NC_szName2],n.[szName],n.[bType],n.[dwClass],n.[bCountryID],n.[wLocalID],n.[bCondition],n.[bDiscountRate],n.[bAddProb],n.[wItemID],n.[wMapID],n.[fPosX],n.[fPosY],n.[fPosZ],n.[szLocal])) THROW 51000,'Conflicting existing TNPCCHART key',1;
INSERT dbo.[TNPCCHART] ([wID],[NC_szName2],[szName],[bType],[dwClass],[bCountryID],[wLocalID],[bCondition],[bDiscountRate],[bAddProb],[wItemID],[wMapID],[fPosX],[fPosY],[fPosZ],[szLocal]) SELECT n.[wID],n.[NC_szName2],n.[szName],n.[bType],n.[dwClass],n.[bCountryID],n.[wLocalID],n.[bCondition],n.[bDiscountRate],n.[bAddProb],n.[wItemID],n.[wMapID],n.[fPosX],n.[fPosY],n.[fPosZ],n.[szLocal] FROM #Gf_TNPCCHART n WHERE NOT EXISTS(SELECT 1 FROM dbo.[TNPCCHART] t WHERE t.[wID]=n.[wID]);
IF EXISTS(SELECT [wID],[NC_szName2],[szName],[bType],[dwClass],[bCountryID],[wLocalID],[bCondition],[bDiscountRate],[bAddProb],[wItemID],[wMapID],[fPosX],[fPosY],[fPosZ],[szLocal] FROM #Gf_TNPCCHART EXCEPT SELECT [wID],[NC_szName2],[szName],[bType],[dwClass],[bCountryID],[wLocalID],[bCondition],[bDiscountRate],[bAddProb],[wItemID],[wMapID],[fPosX],[fPosY],[fPosZ],[szLocal] FROM dbo.[TNPCCHART]) THROW 51000,'Missing or changed TNPCCHART row',1;
DROP TABLE #Gf_TNPCCHART;
CREATE TABLE #Gf_TNPCITEMCHART([wNpcID] SMALLINT NOT NULL,[dwItemID] INT NOT NULL);
INSERT #Gf_TNPCITEMCHART ([wNpcID],[dwItemID]) VALUES (26704,8005),
(26704,8006),
(26704,8007),
(26704,8011),
(26704,8105),
(26704,8106),
(26704,8107),
(26704,8111),
(26704,8201),
(26704,8202),
(26704,8401),
(26704,8402),
(26704,8412),
(26718,8005),
(26718,8006),
(26718,8007),
(26718,8011),
(26718,8105),
(26718,8106),
(26718,8107),
(26718,8111),
(26718,8201),
(26718,8202),
(26718,8401),
(26718,8402),
(26718,8412);
IF EXISTS(SELECT 1 FROM #Gf_TNPCITEMCHART n JOIN dbo.[TNPCITEMCHART] t ON t.[wNpcID]=n.[wNpcID] AND t.[dwItemID]=n.[dwItemID] WHERE EXISTS(SELECT t.[wNpcID],t.[dwItemID] EXCEPT SELECT n.[wNpcID],n.[dwItemID])) THROW 51000,'Conflicting existing TNPCITEMCHART key',1;
INSERT dbo.[TNPCITEMCHART] ([wNpcID],[dwItemID]) SELECT n.[wNpcID],n.[dwItemID] FROM #Gf_TNPCITEMCHART n WHERE NOT EXISTS(SELECT 1 FROM dbo.[TNPCITEMCHART] t WHERE t.[wNpcID]=n.[wNpcID] AND t.[dwItemID]=n.[dwItemID]);
IF EXISTS(SELECT [wNpcID],[dwItemID] FROM #Gf_TNPCITEMCHART EXCEPT SELECT [wNpcID],[dwItemID] FROM dbo.[TNPCITEMCHART]) THROW 51000,'Missing or changed TNPCITEMCHART row',1;
DROP TABLE #Gf_TNPCITEMCHART;
CREATE TABLE #Gf_TQUESTCHART([dwQuestID] INT NOT NULL,[dwParentID] INT NOT NULL,[bType] TINYINT NOT NULL,[bForceRun] TINYINT NOT NULL,[bTriggerType] TINYINT NOT NULL,[dwTriggerID] INT NOT NULL,[bCountMax] TINYINT NOT NULL,[bLevel] TINYINT NOT NULL,[bMain] TINYINT NOT NULL,[bConditionCheck] TINYINT NOT NULL);
INSERT #Gf_TQUESTCHART ([dwQuestID],[dwParentID],[bType],[bForceRun],[bTriggerType],[dwTriggerID],[bCountMax],[bLevel],[bMain],[bConditionCheck]) VALUES (11232,0,8,0,3,26653,1,101,1,0),
(11170,11232,7,0,3,26654,0,1,0,0),
(11233,11232,8,0,3,26654,1,101,1,0),
(11171,11233,7,0,3,26655,0,1,0,0),
(11259,0,8,0,3,26654,1,110,1,0),
(11260,11259,7,0,3,26660,0,1,0,0),
(11300,0,8,0,3,26666,1,121,1,0),
(11301,11300,7,0,3,26667,0,1,0,0),
(11327,0,8,0,3,26676,1,126,1,0),
(11328,11327,7,0,3,26677,0,1,0,0),
(11356,0,8,0,3,26676,1,132,1,0),
(11357,11356,7,0,3,26675,0,1,0,0),
(11612,0,8,0,3,22754,1,100,0,0),
(11623,11612,7,0,3,26653,0,1,0,0),
(11613,0,8,0,3,22751,1,100,0,0),
(11627,11613,7,0,3,26653,0,1,0,0),
(11614,0,8,0,3,26021,1,100,0,0),
(11625,11614,7,0,3,26653,0,1,0,0),
(11615,0,8,0,3,22755,1,120,0,0),
(11626,11615,7,0,3,26666,0,1,0,0),
(11616,0,8,0,3,22752,1,120,0,0),
(11618,11616,7,0,3,26666,0,1,0,0),
(11617,0,8,0,3,26013,1,120,0,0),
(11619,11617,7,0,3,26666,0,1,0,0);
IF EXISTS(SELECT 1 FROM #Gf_TQUESTCHART n JOIN dbo.[TQUESTCHART] t ON t.[dwQuestID]=n.[dwQuestID] WHERE EXISTS(SELECT t.[dwQuestID],t.[dwParentID],t.[bType],t.[bForceRun],t.[bTriggerType],t.[dwTriggerID],t.[bCountMax],t.[bLevel],t.[bMain],t.[bConditionCheck] EXCEPT SELECT n.[dwQuestID],n.[dwParentID],n.[bType],n.[bForceRun],n.[bTriggerType],n.[dwTriggerID],n.[bCountMax],n.[bLevel],n.[bMain],n.[bConditionCheck])) THROW 51000,'Conflicting existing TQUESTCHART key',1;
INSERT dbo.[TQUESTCHART] ([dwQuestID],[dwParentID],[bType],[bForceRun],[bTriggerType],[dwTriggerID],[bCountMax],[bLevel],[bMain],[bConditionCheck]) SELECT n.[dwQuestID],n.[dwParentID],n.[bType],n.[bForceRun],n.[bTriggerType],n.[dwTriggerID],n.[bCountMax],n.[bLevel],n.[bMain],n.[bConditionCheck] FROM #Gf_TQUESTCHART n WHERE NOT EXISTS(SELECT 1 FROM dbo.[TQUESTCHART] t WHERE t.[dwQuestID]=n.[dwQuestID]);
IF EXISTS(SELECT [dwQuestID],[dwParentID],[bType],[bForceRun],[bTriggerType],[dwTriggerID],[bCountMax],[bLevel],[bMain],[bConditionCheck] FROM #Gf_TQUESTCHART EXCEPT SELECT [dwQuestID],[dwParentID],[bType],[bForceRun],[bTriggerType],[dwTriggerID],[bCountMax],[bLevel],[bMain],[bConditionCheck] FROM dbo.[TQUESTCHART]) THROW 51000,'Missing or changed TQUESTCHART row',1;
DROP TABLE #Gf_TQUESTCHART;
CREATE TABLE #Gf_TQUESTTERMCHART([dwID] INT NOT NULL,[dwQuestID] INT NOT NULL,[bTermType] TINYINT NOT NULL,[dwTermID] INT NOT NULL,[bCount] TINYINT NOT NULL);
INSERT #Gf_TQUESTTERMCHART ([dwID],[dwQuestID],[bTermType],[dwTermID],[bCount]) VALUES (31330,11232,1,11232,0),
(31331,11170,1,11232,0),
(31332,11233,1,11232,0),
(31333,11171,1,11233,0),
(31334,11259,1,11259,0),
(31335,11260,1,11259,0),
(31336,11300,1,11300,0),
(31337,11301,1,11300,0),
(31338,11327,1,11327,0),
(31339,11328,1,11327,0),
(31340,11356,1,11356,0),
(31341,11357,1,11356,0),
(31342,11612,1,11612,0),
(31343,11623,1,11612,0),
(31344,11613,1,11613,0),
(31345,11627,1,11613,0),
(31346,11614,1,11614,0),
(31347,11625,1,11614,0),
(31348,11615,1,11615,0),
(31349,11626,1,11615,0),
(31350,11616,1,11616,0),
(31351,11618,1,11616,0),
(31352,11617,1,11617,0),
(31353,11619,1,11617,0);
IF EXISTS(SELECT 1 FROM #Gf_TQUESTTERMCHART n JOIN dbo.[TQUESTTERMCHART] t ON t.[dwID]=n.[dwID] WHERE EXISTS(SELECT t.[dwID],t.[dwQuestID],t.[bTermType],t.[dwTermID],t.[bCount] EXCEPT SELECT n.[dwID],n.[dwQuestID],n.[bTermType],n.[dwTermID],n.[bCount])) THROW 51000,'Conflicting existing TQUESTTERMCHART key',1;
INSERT dbo.[TQUESTTERMCHART] ([dwID],[dwQuestID],[bTermType],[dwTermID],[bCount]) SELECT n.[dwID],n.[dwQuestID],n.[bTermType],n.[dwTermID],n.[bCount] FROM #Gf_TQUESTTERMCHART n WHERE NOT EXISTS(SELECT 1 FROM dbo.[TQUESTTERMCHART] t WHERE t.[dwID]=n.[dwID]);
IF EXISTS(SELECT [dwID],[dwQuestID],[bTermType],[dwTermID],[bCount] FROM #Gf_TQUESTTERMCHART EXCEPT SELECT [dwID],[dwQuestID],[bTermType],[dwTermID],[bCount] FROM dbo.[TQUESTTERMCHART]) THROW 51000,'Missing or changed TQUESTTERMCHART row',1;
DROP TABLE #Gf_TQUESTTERMCHART;
CREATE TABLE #Gf_TQCONDITIONCHART([dwID] INT NOT NULL,[dwQuestID] INT NOT NULL,[bConditionType] TINYINT NOT NULL,[dwConditionID] INT NOT NULL,[bCount] TINYINT NOT NULL);
INSERT #Gf_TQCONDITIONCHART ([dwID],[dwQuestID],[bConditionType],[dwConditionID],[bCount]) VALUES (9641,11232,1,101,0),
(9642,11233,1,101,0),
(9643,11259,1,110,0),
(9644,11300,1,121,0),
(9645,11327,1,126,0),
(9646,11356,1,132,0),
(9647,11612,1,100,0),
(9648,11613,1,100,0),
(9649,11614,1,100,0),
(9650,11615,1,120,0),
(9651,11616,1,120,0),
(9652,11617,1,120,0);
IF EXISTS(SELECT 1 FROM #Gf_TQCONDITIONCHART n JOIN dbo.[TQCONDITIONCHART] t ON t.[dwID]=n.[dwID] WHERE EXISTS(SELECT t.[dwID],t.[dwQuestID],t.[bConditionType],t.[dwConditionID],t.[bCount] EXCEPT SELECT n.[dwID],n.[dwQuestID],n.[bConditionType],n.[dwConditionID],n.[bCount])) THROW 51000,'Conflicting existing TQCONDITIONCHART key',1;
INSERT dbo.[TQCONDITIONCHART] ([dwID],[dwQuestID],[bConditionType],[dwConditionID],[bCount]) SELECT n.[dwID],n.[dwQuestID],n.[bConditionType],n.[dwConditionID],n.[bCount] FROM #Gf_TQCONDITIONCHART n WHERE NOT EXISTS(SELECT 1 FROM dbo.[TQCONDITIONCHART] t WHERE t.[dwID]=n.[dwID]);
IF EXISTS(SELECT [dwID],[dwQuestID],[bConditionType],[dwConditionID],[bCount] FROM #Gf_TQCONDITIONCHART EXCEPT SELECT [dwID],[dwQuestID],[bConditionType],[dwConditionID],[bCount] FROM dbo.[TQCONDITIONCHART]) THROW 51000,'Missing or changed TQCONDITIONCHART row',1;
DROP TABLE #Gf_TQCONDITIONCHART;
CREATE TABLE #Gf_TQREWARDCHART([dwID] INT NOT NULL,[dwQuestID] INT NOT NULL,[bRewardType] TINYINT NOT NULL,[dwRewardID] INT NOT NULL,[bTakeMethod] TINYINT NOT NULL,[bTakeData] TINYINT NOT NULL,[bCount] TINYINT NOT NULL,[dwQuestMob] INT NOT NULL,[dwQuestTime] INT NOT NULL,[dwQuestPathMob] INT NOT NULL,[dwTicketID] INT NOT NULL,[bSendQ] TINYINT NOT NULL);
INSERT #Gf_TQREWARDCHART ([dwID],[dwQuestID],[bRewardType],[dwRewardID],[bTakeMethod],[bTakeData],[bCount],[dwQuestMob],[dwQuestTime],[dwQuestPathMob],[dwTicketID],[bSendQ]) VALUES (6002,11232,1,24184,1,0,0,0,0,0,0,0),
(6003,11232,6,131867,1,0,0,0,0,0,0,0),
(6004,11232,9,187,1,0,0,0,0,0,0,0),
(6005,11233,1,44084,1,0,0,0,0,0,0,0),
(6006,11233,6,241162,1,0,0,0,0,0,0,0),
(6007,11233,9,4006,1,0,0,0,0,0,0,0),
(6008,11259,1,28083,1,0,0,0,0,0,0,0),
(6009,11259,6,180958,1,0,0,0,0,0,0,0),
(6010,11259,9,203,1,0,0,0,0,0,0,0),
(6011,11300,1,29232,1,0,0,0,0,0,0,0),
(6012,11300,6,232127,1,0,0,0,0,0,0,0),
(6013,11300,9,224,1,0,0,0,0,0,0,0),
(6014,11327,1,29276,1,0,0,0,0,0,0,0),
(6015,11327,6,232434,1,0,0,0,0,0,0,0),
(6016,11327,9,233,1,0,0,0,0,0,0,0),
(6017,11356,1,50806,1,0,0,0,0,0,0,0),
(6018,11356,6,399423,1,0,0,0,0,0,0,0),
(6019,11356,9,3753,1,0,0,0,0,0,0,0),
(6020,11612,1,1980,1,0,0,0,0,0,0,0),
(6021,11612,6,936,1,0,0,0,0,0,0,0),
(6022,11612,9,185,1,0,0,0,0,0,0,0),
(6023,11613,1,1980,1,0,0,0,0,0,0,0),
(6024,11613,6,936,1,0,0,0,0,0,0,0),
(6025,11613,9,185,1,0,0,0,0,0,0,0),
(6026,11614,1,1980,1,0,0,0,0,0,0,0),
(6027,11614,6,936,1,0,0,0,0,0,0,0),
(6028,11614,9,185,1,0,0,0,0,0,0,0),
(6029,11615,1,2376,1,0,0,0,0,0,0,0),
(6030,11615,6,1086,1,0,0,0,0,0,0,0),
(6031,11615,9,222,1,0,0,0,0,0,0,0),
(6032,11616,1,2376,1,0,0,0,0,0,0,0),
(6033,11616,6,1086,1,0,0,0,0,0,0,0),
(6034,11616,9,222,1,0,0,0,0,0,0,0),
(6035,11617,1,2376,1,0,0,0,0,0,0,0),
(6036,11617,6,1086,1,0,0,0,0,0,0,0),
(6037,11617,9,222,1,0,0,0,0,0,0,0);
IF EXISTS(SELECT 1 FROM #Gf_TQREWARDCHART n JOIN dbo.[TQREWARDCHART] t ON t.[dwID]=n.[dwID] WHERE EXISTS(SELECT t.[dwID],t.[dwQuestID],t.[bRewardType],t.[dwRewardID],t.[bTakeMethod],t.[bTakeData],t.[bCount],t.[dwQuestMob],t.[dwQuestTime],t.[dwQuestPathMob],t.[dwTicketID],t.[bSendQ] EXCEPT SELECT n.[dwID],n.[dwQuestID],n.[bRewardType],n.[dwRewardID],n.[bTakeMethod],n.[bTakeData],n.[bCount],n.[dwQuestMob],n.[dwQuestTime],n.[dwQuestPathMob],n.[dwTicketID],n.[bSendQ])) THROW 51000,'Conflicting existing TQREWARDCHART key',1;
INSERT dbo.[TQREWARDCHART] ([dwID],[dwQuestID],[bRewardType],[dwRewardID],[bTakeMethod],[bTakeData],[bCount],[dwQuestMob],[dwQuestTime],[dwQuestPathMob],[dwTicketID],[bSendQ]) SELECT n.[dwID],n.[dwQuestID],n.[bRewardType],n.[dwRewardID],n.[bTakeMethod],n.[bTakeData],n.[bCount],n.[dwQuestMob],n.[dwQuestTime],n.[dwQuestPathMob],n.[dwTicketID],n.[bSendQ] FROM #Gf_TQREWARDCHART n WHERE NOT EXISTS(SELECT 1 FROM dbo.[TQREWARDCHART] t WHERE t.[dwID]=n.[dwID]);
IF EXISTS(SELECT [dwID],[dwQuestID],[bRewardType],[dwRewardID],[bTakeMethod],[bTakeData],[bCount],[dwQuestMob],[dwQuestTime],[dwQuestPathMob],[dwTicketID],[bSendQ] FROM #Gf_TQREWARDCHART EXCEPT SELECT [dwID],[dwQuestID],[bRewardType],[dwRewardID],[bTakeMethod],[bTakeData],[bCount],[dwQuestMob],[dwQuestTime],[dwQuestPathMob],[dwTicketID],[bSendQ] FROM dbo.[TQREWARDCHART]) THROW 51000,'Missing or changed TQREWARDCHART row',1;
DROP TABLE #Gf_TQREWARDCHART;
CREATE TABLE #Gf_TUNITCHART([bGroup] TINYINT NOT NULL,[bServerID] TINYINT NOT NULL,[wMapID] SMALLINT NOT NULL,[wUnitID] SMALLINT NOT NULL);
INSERT #Gf_TUNITCHART ([bGroup],[bServerID],[wMapID],[wUnitID]) VALUES (1,1,0,259),
(1,1,0,515);
IF EXISTS(SELECT 1 FROM #Gf_TUNITCHART n JOIN dbo.[TUNITCHART] t ON t.[bGroup]=n.[bGroup] AND t.[bServerID]=n.[bServerID] AND t.[wMapID]=n.[wMapID] AND t.[wUnitID]=n.[wUnitID] WHERE EXISTS(SELECT t.[bGroup],t.[bServerID],t.[wMapID],t.[wUnitID] EXCEPT SELECT n.[bGroup],n.[bServerID],n.[wMapID],n.[wUnitID])) THROW 51000,'Conflicting existing TUNITCHART key',1;
INSERT dbo.[TUNITCHART] ([bGroup],[bServerID],[wMapID],[wUnitID]) SELECT n.[bGroup],n.[bServerID],n.[wMapID],n.[wUnitID] FROM #Gf_TUNITCHART n WHERE NOT EXISTS(SELECT 1 FROM dbo.[TUNITCHART] t WHERE t.[bGroup]=n.[bGroup] AND t.[bServerID]=n.[bServerID] AND t.[wMapID]=n.[wMapID] AND t.[wUnitID]=n.[wUnitID]);
IF EXISTS(SELECT [bGroup],[bServerID],[wMapID],[wUnitID] FROM #Gf_TUNITCHART EXCEPT SELECT [bGroup],[bServerID],[wMapID],[wUnitID] FROM dbo.[TUNITCHART]) THROW 51000,'Missing or changed TUNITCHART row',1;
DROP TABLE #Gf_TUNITCHART;
CREATE TABLE #Gf_TCHANNELCHART([bGroupID] TINYINT NOT NULL,[wMapID] SMALLINT NOT NULL,[wUnitID] SMALLINT NOT NULL,[bLogChannel] TINYINT NOT NULL,[bPhyChannel] TINYINT NOT NULL);
INSERT #Gf_TCHANNELCHART ([bGroupID],[wMapID],[wUnitID],[bLogChannel],[bPhyChannel]) VALUES (1,0,259,1,1),
(1,0,515,1,1);
IF EXISTS(SELECT 1 FROM #Gf_TCHANNELCHART n JOIN dbo.[TCHANNELCHART] t ON t.[bGroupID]=n.[bGroupID] AND t.[wMapID]=n.[wMapID] AND t.[wUnitID]=n.[wUnitID] AND t.[bLogChannel]=n.[bLogChannel] WHERE EXISTS(SELECT t.[bGroupID],t.[wMapID],t.[wUnitID],t.[bLogChannel],t.[bPhyChannel] EXCEPT SELECT n.[bGroupID],n.[wMapID],n.[wUnitID],n.[bLogChannel],n.[bPhyChannel])) THROW 51000,'Conflicting existing TCHANNELCHART key',1;
INSERT dbo.[TCHANNELCHART] ([bGroupID],[wMapID],[wUnitID],[bLogChannel],[bPhyChannel]) SELECT n.[bGroupID],n.[wMapID],n.[wUnitID],n.[bLogChannel],n.[bPhyChannel] FROM #Gf_TCHANNELCHART n WHERE NOT EXISTS(SELECT 1 FROM dbo.[TCHANNELCHART] t WHERE t.[bGroupID]=n.[bGroupID] AND t.[wMapID]=n.[wMapID] AND t.[wUnitID]=n.[wUnitID] AND t.[bLogChannel]=n.[bLogChannel]);
IF EXISTS(SELECT [bGroupID],[wMapID],[wUnitID],[bLogChannel],[bPhyChannel] FROM #Gf_TCHANNELCHART EXCEPT SELECT [bGroupID],[wMapID],[wUnitID],[bLogChannel],[bPhyChannel] FROM dbo.[TCHANNELCHART]) THROW 51000,'Missing or changed TCHANNELCHART row',1;
DROP TABLE #Gf_TCHANNELCHART;
COMMIT TRANSACTION;
GO
