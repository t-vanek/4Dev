-- Restore the pinned wake-up delivery without losing the inherited switch action at the same ID.
-- Evidence: Gameforge/gor-content-repairs/wakeup-plan.json.
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51056,'Quest import lock unavailable',1;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK)) THROW 51056,'Pending character snapshots must drain',1;
DECLARE @state nvarchar(max);
SET @state=(SELECT JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,dwParentID) AS dwParentID,CONVERT(int,bType) AS bType,CONVERT(int,bForceRun) AS bForceRun,CONVERT(int,bTriggerType) AS bTriggerType,CONVERT(int,dwTriggerID) AS dwTriggerID,CONVERT(int,bCountMax) AS bCountMax,CONVERT(int,bLevel) AS bLevel,CONVERT(int,bMain) AS bMain,CONVERT(int,bConditionCheck) AS bConditionCheck FROM dbo.TQUESTCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307) ORDER BY dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FOR JSON PATH),N'[]')) AS headers,
JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bTermType) AS bTermType,CONVERT(int,dwTermID) AS dwTermID,CONVERT(int,bCount) AS bCount FROM dbo.TQUESTTERMCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307) ORDER BY dwQuestID,bTermType,dwTermID,bCount FOR JSON PATH),N'[]')) AS terms,
JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bConditionType) AS bConditionType,CONVERT(int,dwConditionID) AS dwConditionID,CONVERT(int,bCount) AS bCount FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307) ORDER BY dwQuestID,bConditionType,dwConditionID,bCount FOR JSON PATH),N'[]')) AS conditions,
JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bRewardType) AS bRewardType,CONVERT(int,dwRewardID) AS dwRewardID,CONVERT(int,bTakeMethod) AS bTakeMethod,CONVERT(int,bTakeData) AS bTakeData,CONVERT(int,bCount) AS bCount,CONVERT(int,dwQuestMob) AS dwQuestMob,CONVERT(int,dwQuestTime) AS dwQuestTime,CONVERT(int,dwQuestPathMob) AS dwQuestPathMob,CONVERT(int,dwTicketID) AS dwTicketID,CONVERT(int,bSendQ) AS bSendQ FROM dbo.TQREWARDCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307) ORDER BY dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ FOR JSON PATH),N'[]')) AS rewards FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
DECLARE @hash varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2);
IF @hash NOT IN('5C1C2FE68D6207963C36C2F347FAF47A252FF0AF0A9E67C16CD8071FB562150C','F6B58EEEBF916365F8364C0BCCA0516118C3997A85F6CA1683AD320DCA6242BA') THROW 51056,'Wake-up baseline differs',1;
IF @hash='5C1C2FE68D6207963C36C2F347FAF47A252FF0AF0A9E67C16CD8071FB562150C'
BEGIN
IF EXISTS(SELECT 1 FROM dbo.TQUESTTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307)) OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307))
 THROW 51056,'Saved legacy progress must be preserved',1;
IF EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE bType<>0 AND dwQuestID NOT IN(7307,9720,40007307) AND (dwParentID IN(7307,9720,40007307) OR (bTriggerType IN(1,9) AND dwTriggerID IN(7307,9720,40007307))))
 OR EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c JOIN dbo.TQUESTCHART q ON q.dwQuestID=c.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN(7307,9720,40007307) AND c.bConditionType IN(3,15,18) AND c.dwConditionID IN(7307,9720,40007307))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMCHART t JOIN dbo.TQUESTCHART q ON q.dwQuestID=t.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN(7307,9720,40007307) AND t.bTermType IN(1,18) AND t.dwTermID IN(7307,9720,40007307))
 THROW 51056,'Unexpected external reference to relocated action',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=25223 AND wMapID=2010 AND bCountryID=3) OR NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26003 AND wMapID=0 AND bCountryID=3)
 OR NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE dwQuestID=7243 AND bType=8)
 THROW 51056,'Required NPC or parent mission missing',1;
DECLARE @headers TABLE(dwQuestID int,dwParentID int,bType int,bForceRun int,bTriggerType int,dwTriggerID int,bCountMax int,bLevel int,bMain int,bConditionCheck int);
INSERT @headers VALUES
(7307,7243,8,0,3,25223,0,80,0,0),
(9720,7307,7,0,3,26003,0,1,0,0),
(40007307,0,13,0,9,7125,0,1,0,0);
DELETE dbo.TQUESTCHART WHERE dwQuestID IN(7307,9720,40007307);
INSERT dbo.TQUESTCHART(dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck) SELECT dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FROM @headers;
DECLARE @terms TABLE(dwQuestID int,bTermType int,dwTermID int,bCount int);
INSERT @terms VALUES
(7307,1,7307,0),
(9720,1,7307,0),
(40007307,15,363,0);
DELETE dbo.TQUESTTERMCHART WHERE dwQuestID IN(7307,9720,40007307);
DECLARE @termsBase int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.TQUESTTERMCHART WITH(UPDLOCK,HOLDLOCK));
INSERT dbo.TQUESTTERMCHART(dwID,dwQuestID,bTermType,dwTermID,bCount) SELECT @termsBase+ROW_NUMBER() OVER(ORDER BY dwQuestID,bTermType,dwTermID,bCount),dwQuestID,bTermType,dwTermID,bCount FROM @terms;
DECLARE @conditions TABLE(dwQuestID int,bConditionType int,dwConditionID int,bCount int);
INSERT @conditions VALUES
(9720,3,7307,0),
(40007307,20,363,0);
DELETE dbo.TQCONDITIONCHART WHERE dwQuestID IN(7307,9720,40007307);
DECLARE @conditionsBase int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK));
INSERT dbo.TQCONDITIONCHART(dwID,dwQuestID,bConditionType,dwConditionID,bCount) SELECT @conditionsBase+ROW_NUMBER() OVER(ORDER BY dwQuestID,bConditionType,dwConditionID,bCount),dwQuestID,bConditionType,dwConditionID,bCount FROM @conditions;
END;
SET @state=(SELECT JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,dwParentID) AS dwParentID,CONVERT(int,bType) AS bType,CONVERT(int,bForceRun) AS bForceRun,CONVERT(int,bTriggerType) AS bTriggerType,CONVERT(int,dwTriggerID) AS dwTriggerID,CONVERT(int,bCountMax) AS bCountMax,CONVERT(int,bLevel) AS bLevel,CONVERT(int,bMain) AS bMain,CONVERT(int,bConditionCheck) AS bConditionCheck FROM dbo.TQUESTCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307) ORDER BY dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FOR JSON PATH),N'[]')) AS headers,
JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bTermType) AS bTermType,CONVERT(int,dwTermID) AS dwTermID,CONVERT(int,bCount) AS bCount FROM dbo.TQUESTTERMCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307) ORDER BY dwQuestID,bTermType,dwTermID,bCount FOR JSON PATH),N'[]')) AS terms,
JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bConditionType) AS bConditionType,CONVERT(int,dwConditionID) AS dwConditionID,CONVERT(int,bCount) AS bCount FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307) ORDER BY dwQuestID,bConditionType,dwConditionID,bCount FOR JSON PATH),N'[]')) AS conditions,
JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bRewardType) AS bRewardType,CONVERT(int,dwRewardID) AS dwRewardID,CONVERT(int,bTakeMethod) AS bTakeMethod,CONVERT(int,bTakeData) AS bTakeData,CONVERT(int,bCount) AS bCount,CONVERT(int,dwQuestMob) AS dwQuestMob,CONVERT(int,dwQuestTime) AS dwQuestTime,CONVERT(int,dwQuestPathMob) AS dwQuestPathMob,CONVERT(int,dwTicketID) AS dwTicketID,CONVERT(int,bSendQ) AS bSendQ FROM dbo.TQREWARDCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(7307,9720,40007307) ORDER BY dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ FOR JSON PATH),N'[]')) AS rewards FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'F6B58EEEBF916365F8364C0BCCA0516118C3997A85F6CA1683AD320DCA6242BA' THROW 51056,'Wake-up postcondition failed',1;
COMMIT TRANSACTION;
GO
