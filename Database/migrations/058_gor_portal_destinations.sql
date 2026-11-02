-- Captured Gameforge trips: 4506 -> second guard post, 4507 -> Marka's hideout.
-- Preserve all existing spawn points and player state. Evidence: Gameforge/gor-training/portal-destinations.json.
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51058,'Portal repair lock unavailable',1;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK)) THROW 51058,'Pending character snapshots must drain',1;
DECLARE @before TABLE(wPortalID int,bCountry int,wLocalID int,wSpawnID int,bCondition int);
DECLARE @after TABLE(wPortalID int,bCountry int,wLocalID int,wSpawnID int,bCondition int);
INSERT @before VALUES(4506,3,0,4506,0),(4507,3,0,4507,0);
INSERT @after VALUES(4506,3,0,4507,0),(4507,3,0,4509,0);
DECLARE @isBefore bit=0,@isAfter bit=0;
IF NOT EXISTS(SELECT * FROM dbo.TPORTALCHART WITH(UPDLOCK,HOLDLOCK) WHERE wPortalID IN(4506,4507) EXCEPT SELECT * FROM @before)
 AND NOT EXISTS(SELECT * FROM @before EXCEPT SELECT * FROM dbo.TPORTALCHART WHERE wPortalID IN(4506,4507)) SET @isBefore=1;
IF NOT EXISTS(SELECT * FROM dbo.TPORTALCHART WHERE wPortalID IN(4506,4507) EXCEPT SELECT * FROM @after)
 AND NOT EXISTS(SELECT * FROM @after EXCEPT SELECT * FROM dbo.TPORTALCHART WHERE wPortalID IN(4506,4507)) SET @isAfter=1;
IF @isBefore=0 AND @isAfter=0 THROW 51058,'Portal mapping baseline differs',1;
DECLARE @npcBefore TABLE(wNpcID int,dwItemID int);
DECLARE @npcAfter TABLE(wNpcID int,dwItemID int);
INSERT @npcBefore VALUES(26176,4506),(26176,4507);
INSERT @npcAfter VALUES(26176,4506);
IF @isBefore=1 AND ((SELECT COUNT(*) FROM dbo.TNPCITEMCHART WITH(UPDLOCK,HOLDLOCK) WHERE wNpcID=26176)<>2
 OR EXISTS(SELECT * FROM dbo.TNPCITEMCHART WHERE wNpcID=26176 EXCEPT SELECT * FROM @npcBefore)
 OR EXISTS(SELECT * FROM @npcBefore EXCEPT SELECT * FROM dbo.TNPCITEMCHART WHERE wNpcID=26176))
 THROW 51058,'Second-post NPC portal binding baseline differs',1;
IF @isAfter=1 AND ((SELECT COUNT(*) FROM dbo.TNPCITEMCHART WHERE wNpcID=26176)<>1
 OR EXISTS(SELECT * FROM dbo.TNPCITEMCHART WHERE wNpcID=26176 EXCEPT SELECT * FROM @npcAfter)
 OR EXISTS(SELECT * FROM @npcAfter EXCEPT SELECT * FROM dbo.TNPCITEMCHART WHERE wNpcID=26176))
 THROW 51058,'Second-post NPC portal binding postcondition differs',1;
DECLARE @second TABLE(wID smallint,wMapID smallint,fPosX real,fPosY real,fPosZ real,bType tinyint);
DECLARE @marka TABLE(wID smallint,wMapID smallint,fPosX real,fPosY real,fPosZ real,bType tinyint);
INSERT @second VALUES(4507,0,3618.10009765625,105.25,915.27001953125,0);
INSERT @marka VALUES(4509,0,3278.3701171875,124.5,821.8400268554688,0);
IF EXISTS(SELECT * FROM @second EXCEPT SELECT * FROM dbo.TSPAWNPOSCHART WITH(UPDLOCK,HOLDLOCK) WHERE wID=4507)
 OR EXISTS(SELECT * FROM dbo.TSPAWNPOSCHART WHERE wID=4507 EXCEPT SELECT * FROM @second)
 THROW 51058,'Captured second-post spawn differs',1;
IF EXISTS(SELECT 1 FROM dbo.TPORTALCHART WHERE wSpawnID=4509 AND wPortalID<>4507) THROW 51058,'New spawn is referenced by another portal',1;
IF @isBefore=1
BEGIN
 IF EXISTS(SELECT 1 FROM dbo.TSPAWNPOSCHART WITH(UPDLOCK,HOLDLOCK) WHERE wID=4509) THROW 51058,'New spawn identity occupied',1;
 INSERT dbo.TSPAWNPOSCHART SELECT * FROM @marka;
 UPDATE p SET wSpawnID=a.wSpawnID FROM dbo.TPORTALCHART p JOIN @after a ON a.wPortalID=p.wPortalID;
 DELETE dbo.TNPCITEMCHART WHERE wNpcID=26176 AND dwItemID=4507;
END;
IF EXISTS(SELECT * FROM dbo.TPORTALCHART WHERE wPortalID IN(4506,4507) EXCEPT SELECT * FROM @after)
 OR EXISTS(SELECT * FROM @after EXCEPT SELECT * FROM dbo.TPORTALCHART WHERE wPortalID IN(4506,4507))
 THROW 51058,'Portal repair postcondition failed',1;
IF EXISTS(SELECT * FROM @marka EXCEPT SELECT * FROM dbo.TSPAWNPOSCHART WHERE wID=4509)
 OR EXISTS(SELECT * FROM dbo.TSPAWNPOSCHART WHERE wID=4509 EXCEPT SELECT * FROM @marka)
 THROW 51058,'Captured Marka spawn differs',1;
COMMIT TRANSACTION;
GO
