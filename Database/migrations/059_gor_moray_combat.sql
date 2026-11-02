-- Moray 801 / quest 11025: captured effective power, not a global damage-rate change.
-- Evidence and inferred base/weapon decomposition: Gameforge/gor-training/moray-combat.json.
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51059,'Moray repair lock unavailable',1;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK)) THROW 51059,'Pending character snapshots must drain',1;
IF (SELECT COUNT(*) FROM dbo.TMONSTERCHART WITH(UPDLOCK,HOLDLOCK) WHERE wID=801 AND bLevel=10 AND wMonAttr=30050 AND wSkill1=700 AND wSkill2=0 AND wSkill3=0)<>1
 OR EXISTS(SELECT 1 FROM dbo.TMONSTERCHART WHERE wMonAttr=30050 AND wID<>801)
 THROW 51059,'Moray identity or attribute ownership differs',1;
IF (SELECT COUNT(*) FROM dbo.TMONATTRCHART WITH(UPDLOCK,HOLDLOCK) WHERE wID=30050 AND bLevel=10)<>1
 THROW 51059,'Moray attribute row missing or duplicated',1;
IF EXISTS(SELECT [wID],[bLevel],[wAP],[wLAP],[dwAtkSpeed],[wAL],[wDL],[bCriticalPP],[dwMaxHP],[bHPRecover],[wMAP],[bCriticalMP],[dwMaxMP],[bMPRecover],[wDP],[wMDP],[wMinWAP],[wMaxWAP],[wWDP],[wMAL],[wMDL] FROM dbo.TMONATTRCHART WHERE wID=30050 AND bLevel=10 EXCEPT SELECT 30050,10,250,250,0,250,124,15,711,255,500,15,14528,255,100,100,100,100,230,703,124) AND EXISTS(SELECT [wID],[bLevel],[wAP],[wLAP],[dwAtkSpeed],[wAL],[wDL],[bCriticalPP],[dwMaxHP],[bHPRecover],[wMAP],[bCriticalMP],[dwMaxMP],[bMPRecover],[wDP],[wMDP],[wMinWAP],[wMaxWAP],[wWDP],[wMAL],[wMDL] FROM dbo.TMONATTRCHART WHERE wID=30050 AND bLevel=10 EXCEPT SELECT 30050,10,56,250,0,23,124,15,711,255,58,15,485,255,100,100,16,19,230,703,124)
 THROW 51059,'Moray combat baseline differs',1;
UPDATE dbo.TMONATTRCHART SET [wAP]=56,[wAL]=23,[wMAP]=58,[dwMaxMP]=485,[wMinWAP]=16,[wMaxWAP]=19 WHERE wID=30050 AND bLevel=10;
IF EXISTS(SELECT [wID],[bLevel],[wAP],[wLAP],[dwAtkSpeed],[wAL],[wDL],[bCriticalPP],[dwMaxHP],[bHPRecover],[wMAP],[bCriticalMP],[dwMaxMP],[bMPRecover],[wDP],[wMDP],[wMinWAP],[wMaxWAP],[wWDP],[wMAL],[wMDL] FROM dbo.TMONATTRCHART WHERE wID=30050 AND bLevel=10 EXCEPT SELECT 30050,10,56,250,0,23,124,15,711,255,58,15,485,255,100,100,16,19,230,703,124) THROW 51059,'Moray repair postcondition failed',1;
COMMIT TRANSACTION;
GO
