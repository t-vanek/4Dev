-- Existing Marka spawn 5024: original instance ID, two captured appearances and pinned hunt marker agree.
-- Only placement changes; inherited spawn mechanics and combat stats remain provisional.
-- Frozen evidence: Gameforge/gor-training/marka-spawn.json.
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51061,'Marka placement lock unavailable',1;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK)) THROW 51061,'Pending character snapshots must drain',1;
IF (SELECT COUNT(*) FROM dbo.TMONSTERCHART WITH(UPDLOCK,HOLDLOCK) WHERE wID=800 AND wKind=800 AND bLevel=10 AND wMonAttr=20414)<>1
 THROW 51061,'Marka monster identity differs',1;
IF (SELECT COUNT(*) FROM dbo.TMAPMONCHART WITH(UPDLOCK,HOLDLOCK) WHERE wSpawnID=5024)<>1
 OR NOT EXISTS(SELECT 1 FROM dbo.TMAPMONCHART WHERE wSpawnID=5024 AND wMonID=800 AND bEssential=0 AND bLeader=0 AND bProb=100)
 THROW 51061,'Marka spawn membership differs',1;
DECLARE @before TABLE(wID smallint,wGroup smallint,wLocalID smallint,wMapID smallint,fPosX real,fPosY real,fPosZ real,wDir smallint,bCountry tinyint,bCount tinyint,bRange tinyint,bArea tinyint,bLink tinyint,bProb tinyint,bRoamType tinyint,dwRegion int,dwDelay int,bEvent tinyint,wPartyID smallint);
INSERT @before VALUES(5024,0,0,0,3173.53,134.37,908.04999,1500,3,1,3,2,0,100,1,0,1000,0,0);
DECLARE @after TABLE(wID smallint,wGroup smallint,wLocalID smallint,wMapID smallint,fPosX real,fPosY real,fPosZ real,wDir smallint,bCountry tinyint,bCount tinyint,bRange tinyint,bArea tinyint,bLink tinyint,bProb tinyint,bRoamType tinyint,dwRegion int,dwDelay int,bEvent tinyint,wPartyID smallint);
INSERT @after SELECT * FROM @before;
UPDATE @after SET fPosX=3230.110107421875,fPosY=124.27999877929688,fPosZ=837.5800170898438;
IF (SELECT COUNT(*) FROM dbo.TMONSPAWNCHART WITH(UPDLOCK,HOLDLOCK) WHERE wID=5024)<>1
 OR (EXISTS(SELECT * FROM dbo.TMONSPAWNCHART WHERE wID=5024 EXCEPT SELECT * FROM @before)
 AND EXISTS(SELECT * FROM dbo.TMONSPAWNCHART WHERE wID=5024 EXCEPT SELECT * FROM @after))
 THROW 51061,'Marka placement baseline differs',1;
UPDATE dbo.TMONSPAWNCHART SET fPosX=3230.110107421875,fPosY=124.27999877929688,fPosZ=837.5800170898438 WHERE wID=5024;
IF EXISTS(SELECT * FROM dbo.TMONSPAWNCHART WHERE wID=5024 EXCEPT SELECT * FROM @after)
 OR EXISTS(SELECT * FROM @after EXCEPT SELECT * FROM dbo.TMONSPAWNCHART WHERE wID=5024)
 THROW 51061,'Marka placement postcondition differs',1;
COMMIT TRANSACTION;
GO
