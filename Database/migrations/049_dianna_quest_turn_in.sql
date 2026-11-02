-- Diannina premena (8006): the pinned original TQNode.qpd assigns completion to Karl (20012).
-- Navigation SHA-256: d95510c95549d7303d75876e672abee799d8e635e189d981799760d67ac1f5d0
-- Fix only the inherited completion NPC. Keep objectives, rewards, other quest links and player progress.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;
IF (SELECT COUNT(*) FROM dbo.TQUESTCHART WITH(UPDLOCK,HOLDLOCK)
    WHERE dwQuestID=8007 AND dwParentID=8006 AND bType=7 AND bTriggerType=3
      AND dwTriggerID IN(20004,20012))<>1
    THROW 51049,'Dianna completion quest baseline differs; inspect before applying 049',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART WITH(HOLDLOCK)
    WHERE dwQuestID=8006 AND bType=8 AND bTriggerType=3 AND dwTriggerID=20012)
    THROW 51049,'Dianna mission identity differs',1;
IF (SELECT COUNT(*) FROM dbo.TQUESTTERMCHART WITH(HOLDLOCK) WHERE dwQuestID=8007)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.TQUESTTERMCHART WHERE dwQuestID=8007 AND bTermType=1 AND dwTermID=8006 AND bCount=0)
    THROW 51049,'Dianna completion objective differs',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WITH(HOLDLOCK) WHERE wID=20012 AND wMapID=0)
    THROW 51049,'Karl is missing from the expected map',1;
UPDATE dbo.TQUESTCHART SET dwTriggerID=20012 WHERE dwQuestID=8007 AND dwTriggerID=20004;
COMMIT TRANSACTION;
GO
