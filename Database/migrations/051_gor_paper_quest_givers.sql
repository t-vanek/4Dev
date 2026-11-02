-- Gor/Tebekut: pinned quest navigation assigns these three missions to Houba (22919), at both ends.
-- Completion NPCs were repaired by 050; this restores the matching acceptance NPC only.
-- TQNode.qpd SHA-256: d95510c95549d7303d75876e672abee799d8e635e189d981799760d67ac1f5d0
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @q TABLE(Id int PRIMARY KEY,CompleteId int,FetchId int,RewardId int);
INSERT @q VALUES(8883,8884,18133,18129),(8885,8886,18131,18130),(8887,8888,18132,18128);
BEGIN TRANSACTION;
IF EXISTS(SELECT 1 FROM @q r WHERE NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART q WITH(UPDLOCK,HOLDLOCK)
    WHERE q.dwQuestID=r.Id AND q.dwParentID=0 AND q.bType=8 AND q.bTriggerType=3 AND q.dwTriggerID IN(26306,22919)
      AND q.bForceRun=0 AND q.bCountMax=0 AND q.bLevel=1 AND q.bMain=0 AND q.bConditionCheck=0))
    THROW 51051,'Gor paper mission baseline differs',1;
IF EXISTS(SELECT 1 FROM @q r WHERE NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART q WITH(HOLDLOCK)
    WHERE q.dwQuestID=r.CompleteId AND q.dwParentID=r.Id AND q.bType=7 AND q.bTriggerType=3 AND q.dwTriggerID=22919))
    THROW 51051,'Gor completion NPC repair 050 is missing or changed',1;
IF EXISTS(SELECT 1 FROM @q r WHERE
    (SELECT COUNT(*) FROM dbo.TQUESTTERMCHART t WITH(HOLDLOCK) WHERE t.dwQuestID=r.Id)<>1 OR
    NOT EXISTS(SELECT 1 FROM dbo.TQUESTTERMCHART t WHERE t.dwQuestID=r.Id AND t.bTermType=2 AND t.dwTermID=r.FetchId AND t.bCount=20) OR
    (SELECT COUNT(*) FROM dbo.TQUESTTERMCHART t WHERE t.dwQuestID=r.CompleteId)<>1 OR
    NOT EXISTS(SELECT 1 FROM dbo.TQUESTTERMCHART t WHERE t.dwQuestID=r.CompleteId AND t.bTermType=1 AND t.dwTermID=r.Id AND t.bCount=0))
    THROW 51051,'Gor paper objectives differ',1;
IF EXISTS(SELECT 1 FROM @q r WHERE
    (SELECT COUNT(*) FROM dbo.TQREWARDCHART w WITH(HOLDLOCK) WHERE w.dwQuestID=r.Id)<>1 OR
    NOT EXISTS(SELECT 1 FROM dbo.TQREWARDCHART w WHERE w.dwQuestID=r.Id AND w.bRewardType=2 AND w.dwRewardID=r.RewardId AND w.bCount=1 AND w.bTakeMethod=1 AND w.bTakeData=0))
    THROW 51051,'Gor paper rewards differ',1;
IF EXISTS(SELECT 1 FROM @q r WHERE EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c WITH(HOLDLOCK) WHERE c.dwQuestID=r.Id) OR
    (SELECT COUNT(*) FROM dbo.TQCONDITIONCHART c WHERE c.dwQuestID=r.CompleteId)<>1 OR
    NOT EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c WHERE c.dwQuestID=r.CompleteId AND c.bConditionType=3 AND c.dwConditionID=r.Id AND c.bCount=0))
    THROW 51051,'Gor paper conditions differ',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WITH(HOLDLOCK) WHERE wID=22919 AND wMapID=0 AND bCountryID=3)
    THROW 51051,'Houba NPC identity differs',1;
UPDATE q SET dwTriggerID=22919 FROM dbo.TQUESTCHART q JOIN @q r ON q.dwQuestID=r.Id WHERE q.dwTriggerID=26306;
COMMIT TRANSACTION;
GO
