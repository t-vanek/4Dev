-- Restore six explicitly requested Gor event missions from pinned NPC labels, objectives and rewards.
-- Evidence: Gameforge/gor-quest-activation.json. This does not invent event drop probabilities.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51052,'Quest template import lock unavailable',1;
DECLARE @state nvarchar(max);
SET @state=(SELECT JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,dwParentID) AS dwParentID,CONVERT(int,bType) AS bType,CONVERT(int,bForceRun) AS bForceRun,CONVERT(int,bTriggerType) AS bTriggerType,CONVERT(int,dwTriggerID) AS dwTriggerID,CONVERT(int,bCountMax) AS bCountMax,CONVERT(int,bLevel) AS bLevel,CONVERT(int,bMain) AS bMain,CONVERT(int,bConditionCheck) AS bConditionCheck FROM dbo.TQUESTCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458) ORDER BY dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FOR JSON PATH),N'[]')) AS headers,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bTermType) AS bTermType,CONVERT(int,dwTermID) AS dwTermID,CONVERT(int,bCount) AS bCount FROM dbo.TQUESTTERMCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458) ORDER BY dwQuestID,bTermType,dwTermID,bCount FOR JSON PATH),N'[]')) AS terms,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bConditionType) AS bConditionType,CONVERT(int,dwConditionID) AS dwConditionID,CONVERT(int,bCount) AS bCount FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458) ORDER BY dwQuestID,bConditionType,dwConditionID,bCount FOR JSON PATH),N'[]')) AS conditions,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bRewardType) AS bRewardType,CONVERT(int,dwRewardID) AS dwRewardID,CONVERT(int,bTakeMethod) AS bTakeMethod,CONVERT(int,bTakeData) AS bTakeData,CONVERT(int,bCount) AS bCount FROM dbo.TQREWARDCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458) ORDER BY dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount FOR JSON PATH),N'[]')) AS rewards FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
DECLARE @hash varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2);
IF @hash NOT IN('B6260FC9401761A89C8DB2587DC0DFC80BAA202A4587F1EC0FD7109587E40D5E','888481BA620CCF3AE772453BA31CF6DD878AC87BB70C145D6F801A4AB76C8B0B') THROW 51052,'Gor event quest baseline differs; inspect activation evidence',1;
IF @hash='B6260FC9401761A89C8DB2587DC0DFC80BAA202A4587F1EC0FD7109587E40D5E'
BEGIN
IF EXISTS(SELECT 1 FROM dbo.TQUESTTABLE WHERE dwQuestID IN(362,233,234,235,236,237,238,239,240,241,242,2398,2458) AND bTriggerCount>bCompleteCount)
    THROW 51052,'A legacy mission is still running; retain its progress before restoring identities',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE dwQuestID=362 AND bType=0)
    THROW 51052,'Reused completion 2458 still belongs to an active old mission',1;
IF EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE bType<>0 AND bTriggerType IN(1,9) AND dwTriggerID IN(233,234,235,236,237,238,239,240,241,242,2398,2458))
    THROW 51052,'Unexpected legacy execution children would change the restored rewards',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26567 AND wMapID=0 AND bCountryID=3)
 OR NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=20040 AND wMapID=0 AND bCountryID=3)
    THROW 51052,'Required Gor event NPC identity missing',1;
IF (SELECT COUNT(*) FROM dbo.TITEMCHART WHERE wItemID IN(7844,18061,18322,18323,18540,18541,18542,18543))<>8
    THROW 51052,'Required quest item template missing',1;
DECLARE @h TABLE(dwQuestID int,dwParentID int,bType int,bForceRun int,bTriggerType int,dwTriggerID int,bCountMax int,bLevel int,bMain int,bConditionCheck int);
INSERT @h VALUES
(233,0,8,0,3,26567,0,1,0,0),
(234,0,8,0,3,26567,0,1,0,0),
(235,0,8,0,3,26567,0,1,0,0),
(236,0,8,0,3,26567,0,1,0,0),
(237,0,8,0,3,26567,0,1,0,0),
(238,233,7,0,3,26567,0,1,0,0),
(239,234,7,0,3,26567,0,1,0,0),
(240,235,7,0,3,26567,0,1,0,0),
(241,236,7,0,3,26567,0,1,0,0),
(242,237,7,0,3,26567,0,1,0,0),
(2398,0,8,0,3,20040,0,1,0,0),
(2458,2398,7,0,3,20040,0,1,0,0);
DECLARE @terms TABLE(dwQuestID int,bTermType int,dwTermID int,bCount int);
INSERT @terms VALUES
(233,2,18540,1),
(233,2,18541,1),
(233,2,18542,1),
(233,2,18543,1),
(234,2,18541,3),
(234,2,18542,3),
(235,2,18540,3),
(235,2,18542,3),
(236,2,18540,3),
(236,2,18541,3),
(237,2,18540,5),
(237,2,18541,5),
(237,2,18542,5),
(238,1,233,0),
(239,1,234,0),
(240,1,235,0),
(241,1,236,0),
(242,1,237,0),
(2398,2,18323,5),
(2458,1,2398,0);
DELETE FROM dbo.TQUESTTERMCHART WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458);
DECLARE @termsBase int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.TQUESTTERMCHART WITH(UPDLOCK,HOLDLOCK));
INSERT dbo.TQUESTTERMCHART(dwID,dwQuestID,bTermType,dwTermID,bCount) SELECT @termsBase+ROW_NUMBER() OVER(ORDER BY dwQuestID,bTermType,dwTermID,bCount),dwQuestID,bTermType,dwTermID,bCount FROM @terms;
DECLARE @conditions TABLE(dwQuestID int,bConditionType int,dwConditionID int,bCount int);
INSERT @conditions VALUES
(238,3,233,0),
(239,3,234,0),
(240,3,235,0),
(241,3,236,0),
(242,3,237,0),
(2458,3,2398,0);
DELETE FROM dbo.TQCONDITIONCHART WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458);
DECLARE @conditionsBase int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK));
INSERT dbo.TQCONDITIONCHART(dwID,dwQuestID,bConditionType,dwConditionID,bCount) SELECT @conditionsBase+ROW_NUMBER() OVER(ORDER BY dwQuestID,bConditionType,dwConditionID,bCount),dwQuestID,bConditionType,dwConditionID,bCount FROM @conditions;
DECLARE @rewards TABLE(dwQuestID int,bRewardType int,dwRewardID int,bTakeMethod int,bTakeData int,bCount int);
INSERT @rewards VALUES
(233,2,18061,1,0,1),
(234,2,18540,1,0,1),
(235,2,18541,1,0,1),
(236,2,18542,1,0,1),
(237,2,18543,1,0,1),
(2398,2,7844,1,0,1),
(2398,2,18322,1,0,5);
DELETE FROM dbo.TQREWARDCHART WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458);
DECLARE @rewardsBase int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.TQREWARDCHART WITH(UPDLOCK,HOLDLOCK));
INSERT dbo.TQREWARDCHART(dwID,dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ) SELECT @rewardsBase+ROW_NUMBER() OVER(ORDER BY dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount),dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,0,0,0,0,0 FROM @rewards;
UPDATE q SET dwParentID=h.dwParentID,bType=h.bType,bForceRun=h.bForceRun,bTriggerType=h.bTriggerType,dwTriggerID=h.dwTriggerID,bCountMax=h.bCountMax,bLevel=h.bLevel,bMain=h.bMain,bConditionCheck=h.bConditionCheck FROM dbo.TQUESTCHART q JOIN @h h ON q.dwQuestID=h.dwQuestID;
END;
SET @state=(SELECT JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,dwParentID) AS dwParentID,CONVERT(int,bType) AS bType,CONVERT(int,bForceRun) AS bForceRun,CONVERT(int,bTriggerType) AS bTriggerType,CONVERT(int,dwTriggerID) AS dwTriggerID,CONVERT(int,bCountMax) AS bCountMax,CONVERT(int,bLevel) AS bLevel,CONVERT(int,bMain) AS bMain,CONVERT(int,bConditionCheck) AS bConditionCheck FROM dbo.TQUESTCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458) ORDER BY dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FOR JSON PATH),N'[]')) AS headers,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bTermType) AS bTermType,CONVERT(int,dwTermID) AS dwTermID,CONVERT(int,bCount) AS bCount FROM dbo.TQUESTTERMCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458) ORDER BY dwQuestID,bTermType,dwTermID,bCount FOR JSON PATH),N'[]')) AS terms,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bConditionType) AS bConditionType,CONVERT(int,dwConditionID) AS dwConditionID,CONVERT(int,bCount) AS bCount FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458) ORDER BY dwQuestID,bConditionType,dwConditionID,bCount FOR JSON PATH),N'[]')) AS conditions,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bRewardType) AS bRewardType,CONVERT(int,dwRewardID) AS dwRewardID,CONVERT(int,bTakeMethod) AS bTakeMethod,CONVERT(int,bTakeData) AS bTakeData,CONVERT(int,bCount) AS bCount FROM dbo.TQREWARDCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(233,234,235,236,237,238,239,240,241,242,2398,2458) ORDER BY dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount FOR JSON PATH),N'[]')) AS rewards FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'888481BA620CCF3AE772453BA31CF6DD878AC87BB70C145D6F801A4AB76C8B0B' THROW 51052,'Gor activation postcondition failed',1;
COMMIT TRANSACTION;
GO
