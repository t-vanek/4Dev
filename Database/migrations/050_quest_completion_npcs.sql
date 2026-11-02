-- Correct only verified completion NPC assignments; evidence: Gameforge/quest-turn-in-npcs.json.
-- TQNode.qpd SHA-256: d95510c95549d7303d75876e672abee799d8e635e189d981799760d67ac1f5d0
-- No mission links, objectives, rewards, NPC placement or character state are modified.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
SET NOCOUNT ON;
DECLARE @repair TABLE(CompletionId int PRIMARY KEY,MissionId int,OldNpc int,NewNpc int,MapId int,Country int,
    ForceRun int,CountMax int,Level int,Main int,ConditionCheck int);
INSERT @repair VALUES
(2060,580,21079,20339,0,0,0,0,1,0,0),
(2082,582,20339,22294,0,0,0,0,1,0,0),
(3420,3419,22274,22273,0,0,0,0,1,0,0),
(3422,3421,22274,22273,0,0,0,0,1,0,0),
(3427,3426,20004,20313,0,0,0,0,1,0,0),
(3507,3506,20182,22277,0,1,0,0,1,0,0),
(3566,3565,20154,20195,0,1,0,0,1,0,0),
(3698,3697,21079,20339,0,0,0,0,1,0,0),
(3960,3959,21130,21157,0,1,0,0,1,0,0),
(3962,3961,21130,21157,0,1,0,0,1,1,0),
(3964,3963,21130,21157,0,1,0,0,1,0,0),
(3966,3965,20863,21130,0,1,0,0,1,0,0),
(3976,812,20863,21130,0,1,0,0,1,0,0),
(4027,4026,21094,21115,0,0,0,0,1,0,0),
(4273,4272,22101,22097,0,1,0,0,1,0,0),
(5446,5445,22101,22097,0,1,0,0,1,0,0),
(7143,7125,25208,25209,2010,3,0,1,1,0,0),
(7194,7193,25209,25212,2010,3,0,1,1,0,0),
(7210,7209,25212,25216,2010,3,0,1,1,0,0),
(7306,7305,25208,25209,2010,3,0,0,1,0,0),
(8011,8010,20301,20014,0,0,0,0,1,0,0),
(8013,8012,20301,20014,0,0,0,0,1,0,0),
(8092,8091,22260,20004,0,0,0,0,1,0,0),
(8186,8185,20121,20861,0,1,0,0,1,0,0),
(8245,8244,20861,20101,0,1,0,0,1,0,0),
(8255,8252,20101,20109,0,1,0,0,1,0,0),
(8884,8883,26306,22919,0,3,0,0,1,0,0),
(8886,8885,26306,22919,0,3,0,0,1,0,0),
(8888,8887,26306,22919,0,3,0,0,1,0,0),
(8894,8893,26302,22919,0,3,0,0,1,0,0);
DECLARE @terms TABLE(QuestId int,TermType int,TermId int,TermCount int);
INSERT @terms VALUES
(580,1,580,0),
(582,1,582,0),
(812,1,812,0),
(2060,1,580,0),
(2082,1,582,0),
(3419,1,3419,0),
(3420,1,3419,0),
(3421,13,20359,1),
(3421,13,20362,1),
(3421,13,20363,1),
(3422,1,3421,0),
(3426,1,3426,0),
(3427,1,3426,0),
(3506,1,3506,0),
(3507,1,3506,0),
(3565,1,3565,0),
(3566,1,3565,0),
(3697,1,3697,0),
(3698,1,3697,0),
(3959,3,2476,15),
(3959,3,2477,15),
(3960,1,3959,0),
(3961,2,9156,10),
(3962,1,3961,0),
(3963,2,9157,1),
(3963,13,22312,1),
(3963,13,22313,1),
(3964,1,3963,0),
(3965,1,3965,0),
(3966,1,3965,0),
(3976,1,812,0),
(4026,1,4026,0),
(4027,1,4026,0),
(4272,2,9902,1),
(4273,1,4272,0),
(5445,1,5445,0),
(5446,1,5445,0),
(7125,1,7125,0),
(7143,1,7125,0),
(7193,1,7193,0),
(7194,1,7193,0),
(7209,1,7209,0),
(7210,1,7209,0),
(7305,1,7305,0),
(7306,1,7305,0),
(8010,1,8010,0),
(8011,1,8010,0),
(8012,13,20454,1),
(8013,1,8012,0),
(8091,3,416,1),
(8092,1,8091,0),
(8185,1,8185,0),
(8186,1,8185,0),
(8244,1,8244,0),
(8245,1,8244,0),
(8252,1,8252,0),
(8255,1,8252,0),
(8883,2,18133,20),
(8884,1,8883,0),
(8885,2,18131,20),
(8886,1,8885,0),
(8887,2,18132,20),
(8888,1,8887,0),
(8893,2,18136,1),
(8893,2,18137,1),
(8894,1,8893,0);
BEGIN TRANSACTION;
IF EXISTS(SELECT 1 FROM @repair r WHERE NOT EXISTS(
    SELECT 1 FROM dbo.TQUESTCHART q WITH(UPDLOCK,HOLDLOCK)
    WHERE q.dwQuestID=r.CompletionId AND q.dwParentID=r.MissionId AND q.bType=7 AND q.bTriggerType=3
      AND q.dwTriggerID IN(r.OldNpc,r.NewNpc) AND q.bForceRun=r.ForceRun AND q.bCountMax=r.CountMax
      AND q.bLevel=r.Level AND q.bMain=r.Main AND q.bConditionCheck=r.ConditionCheck))
    THROW 51050,'Completion quest baseline differs; inspect migration 050 evidence',1;
IF EXISTS(SELECT 1 FROM @repair r WHERE NOT EXISTS(
    SELECT 1 FROM dbo.TQUESTCHART q WITH(HOLDLOCK) WHERE q.dwQuestID=r.MissionId AND q.bType=8))
    THROW 51050,'Mission identity differs',1;
IF EXISTS(SELECT t.dwQuestID,t.bTermType,t.dwTermID,t.bCount FROM dbo.TQUESTTERMCHART t WITH(HOLDLOCK)
    WHERE t.dwQuestID IN(SELECT CompletionId FROM @repair UNION SELECT MissionId FROM @repair)
    EXCEPT SELECT QuestId,TermType,TermId,TermCount FROM @terms)
 OR EXISTS(SELECT QuestId,TermType,TermId,TermCount FROM @terms EXCEPT
    SELECT t.dwQuestID,t.bTermType,t.dwTermID,t.bCount FROM dbo.TQUESTTERMCHART t
    WHERE t.dwQuestID IN(SELECT CompletionId FROM @repair UNION SELECT MissionId FROM @repair))
 OR EXISTS(SELECT 1 FROM (SELECT DISTINCT QuestId FROM @terms) k WHERE
    (SELECT COUNT(*) FROM dbo.TQUESTTERMCHART t WHERE t.dwQuestID=k.QuestId)<>(SELECT COUNT(*) FROM @terms t WHERE t.QuestId=k.QuestId))
    THROW 51050,'Mission/completion objectives differ',1;
IF EXISTS(SELECT 1 FROM @repair r WHERE
    (SELECT COUNT(*) FROM dbo.TQCONDITIONCHART c WITH(HOLDLOCK) WHERE c.dwQuestID=r.CompletionId)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c WHERE c.dwQuestID=r.CompletionId
        AND c.bConditionType=3 AND c.dwConditionID=r.MissionId AND c.bCount=0))
    THROW 51050,'Completion conditions differ',1;
IF EXISTS(SELECT 1 FROM @repair r WHERE NOT EXISTS(
    SELECT 1 FROM dbo.TNPCCHART n WITH(HOLDLOCK) WHERE n.wID=r.NewNpc AND n.wMapID=r.MapId AND n.bCountryID=r.Country))
    THROW 51050,'Completion NPC is missing or has a different map/country',1;
UPDATE q SET dwTriggerID=r.NewNpc FROM dbo.TQUESTCHART q JOIN @repair r ON r.CompletionId=q.dwQuestID
WHERE q.dwTriggerID=r.OldNpc;
COMMIT TRANSACTION;
GO
