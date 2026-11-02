-- Restore Trojstranny pakt from matching pinned quest/navigation and publisher evidence.
-- Requires the RT_SOUL-capable Map deployment. Evidence: Gameforge/gor-remaining-quests.json.
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51053,'Quest import lock unavailable',1;
DECLARE @state nvarchar(max);
SET @state=(SELECT JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,dwParentID) AS dwParentID,CONVERT(int,bType) AS bType,CONVERT(int,bForceRun) AS bForceRun,CONVERT(int,bTriggerType) AS bTriggerType,CONVERT(int,dwTriggerID) AS dwTriggerID,CONVERT(int,bCountMax) AS bCountMax,CONVERT(int,bLevel) AS bLevel,CONVERT(int,bMain) AS bMain,CONVERT(int,bConditionCheck) AS bConditionCheck FROM dbo.TQUESTCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200) ORDER BY dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FOR JSON PATH),N'[]')) AS headers,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bTermType) AS bTermType,CONVERT(int,dwTermID) AS dwTermID,CONVERT(int,bCount) AS bCount FROM dbo.TQUESTTERMCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200) ORDER BY dwQuestID,bTermType,dwTermID,bCount FOR JSON PATH),N'[]')) AS terms,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bConditionType) AS bConditionType,CONVERT(int,dwConditionID) AS dwConditionID,CONVERT(int,bCount) AS bCount FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200) ORDER BY dwQuestID,bConditionType,dwConditionID,bCount FOR JSON PATH),N'[]')) AS conditions,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bRewardType) AS bRewardType,CONVERT(int,dwRewardID) AS dwRewardID,CONVERT(int,bTakeMethod) AS bTakeMethod,CONVERT(int,bTakeData) AS bTakeData,CONVERT(int,bCount) AS bCount,CONVERT(int,dwQuestMob) AS dwQuestMob,CONVERT(int,dwQuestTime) AS dwQuestTime,CONVERT(int,dwQuestPathMob) AS dwQuestPathMob,CONVERT(int,dwTicketID) AS dwTicketID,CONVERT(int,bSendQ) AS bSendQ FROM dbo.TQREWARDCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200) ORDER BY dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ FOR JSON PATH),N'[]')) AS rewards FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
DECLARE @hash varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2);
IF @hash NOT IN('6CD5A0D50B75A6011743EEBFF832F6B6B39AC80A74CF2AEBA19F76BC82177F4B','C88BE1A3D77436769256EA280AC6151A8EC581DF000CDD1C78C4296752467917') THROW 51053,'Pakt baseline differs; preserve legacy identity',1;
IF @hash='6CD5A0D50B75A6011743EEBFF832F6B6B39AC80A74CF2AEBA19F76BC82177F4B'
BEGIN
IF EXISTS(SELECT 1 FROM dbo.TQUESTTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200))
 THROW 51053,'Legacy player progress must be retained before quest identity replacement',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26200 AND wMapID=0 AND bCountryID=3)
 OR NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26436 AND wMapID=0 AND bCountryID=3)
 THROW 51053,'Required publisher NPC missing',1;
IF EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c JOIN dbo.TQUESTCHART q ON q.dwQuestID=c.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN(547,548,3200) AND c.bConditionType IN(3,15,18) AND c.dwConditionID IN(547,548))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMCHART t JOIN dbo.TQUESTCHART q ON q.dwQuestID=t.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN(547,548,3200) AND t.bTermType IN(1,18) AND t.dwTermID IN(547,548))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE bType<>0 AND dwQuestID NOT IN(547,548,3200) AND (dwParentID IN(547,548) OR (bTriggerType IN(1,9) AND dwTriggerID IN(547,548))))
 THROW 51053,'Unexpected active legacy dependencies on reused quest identity',1;
UPDATE dbo.TQUESTCHART SET bType=8,dwTriggerID=26200,bLevel=90 WHERE dwQuestID=547;
UPDATE dbo.TQUESTCHART SET dwTriggerID=26436 WHERE dwQuestID=548;
UPDATE dbo.TQUESTCHART SET bType=0 WHERE dwQuestID=3200;
DELETE dbo.TQCONDITIONCHART WHERE dwQuestID=547;
DECLARE @conditionId int=(SELECT COALESCE(MAX(dwID),0)+1 FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK));
INSERT dbo.TQCONDITIONCHART(dwID,dwQuestID,bConditionType,dwConditionID,bCount) VALUES(@conditionId,547,1,90,0);
DELETE dbo.TQREWARDCHART WHERE dwQuestID=547;
DECLARE @rewardId int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.TQREWARDCHART WITH(UPDLOCK,HOLDLOCK));
INSERT dbo.TQREWARDCHART(dwID,dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ)
VALUES(@rewardId+1,547,1,1782,1,0,0,0,0,0,0,0),(@rewardId+2,547,6,855,1,0,0,0,0,0,0,0),(@rewardId+3,547,9,167,1,0,0,0,0,0,0,0);
END;
SET @state=(SELECT JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,dwParentID) AS dwParentID,CONVERT(int,bType) AS bType,CONVERT(int,bForceRun) AS bForceRun,CONVERT(int,bTriggerType) AS bTriggerType,CONVERT(int,dwTriggerID) AS dwTriggerID,CONVERT(int,bCountMax) AS bCountMax,CONVERT(int,bLevel) AS bLevel,CONVERT(int,bMain) AS bMain,CONVERT(int,bConditionCheck) AS bConditionCheck FROM dbo.TQUESTCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200) ORDER BY dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FOR JSON PATH),N'[]')) AS headers,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bTermType) AS bTermType,CONVERT(int,dwTermID) AS dwTermID,CONVERT(int,bCount) AS bCount FROM dbo.TQUESTTERMCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200) ORDER BY dwQuestID,bTermType,dwTermID,bCount FOR JSON PATH),N'[]')) AS terms,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bConditionType) AS bConditionType,CONVERT(int,dwConditionID) AS dwConditionID,CONVERT(int,bCount) AS bCount FROM dbo.TQCONDITIONCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200) ORDER BY dwQuestID,bConditionType,dwConditionID,bCount FOR JSON PATH),N'[]')) AS conditions,JSON_QUERY(COALESCE((SELECT CONVERT(int,dwQuestID) AS dwQuestID,CONVERT(int,bRewardType) AS bRewardType,CONVERT(int,dwRewardID) AS dwRewardID,CONVERT(int,bTakeMethod) AS bTakeMethod,CONVERT(int,bTakeData) AS bTakeData,CONVERT(int,bCount) AS bCount,CONVERT(int,dwQuestMob) AS dwQuestMob,CONVERT(int,dwQuestTime) AS dwQuestTime,CONVERT(int,dwQuestPathMob) AS dwQuestPathMob,CONVERT(int,dwTicketID) AS dwTicketID,CONVERT(int,bSendQ) AS bSendQ FROM dbo.TQREWARDCHART WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(547,548,3200) ORDER BY dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ FOR JSON PATH),N'[]')) AS rewards FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'C88BE1A3D77436769256EA280AC6151A8EC581DF000CDD1C78C4296752467917' THROW 51053,'Pakt postcondition failed',1;
COMMIT TRANSACTION;
GO
