-- REVIEW ONLY: requires explicit approval of the functional DL/MDL substitute.
-- Kept outside Database/migrations so service startup cannot activate it accidentally.
-- Evidence: Gameforge/gor-training/moray-defense-proposal.json
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51060,'Moray defense lock unavailable',1;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK)) THROW 51060,'Pending character snapshots must drain',1;
IF (SELECT COUNT(*) FROM dbo.TMONSTERCHART WITH(UPDLOCK,HOLDLOCK) WHERE wID=801 AND bLevel=10 AND wMonAttr=30050)<>1
 OR EXISTS(SELECT 1 FROM dbo.TMONSTERCHART WHERE wMonAttr=30050 AND wID<>801)
 THROW 51060,'Moray identity or attribute ownership differs',1;
IF (SELECT COUNT(*) FROM dbo.TMONATTRCHART WITH(UPDLOCK,HOLDLOCK) WHERE wID=30050 AND bLevel=10)<>1
 THROW 51060,'Moray attribute missing or duplicated',1;
IF EXISTS(SELECT [wID],[bLevel],[wAP],[wLAP],[dwAtkSpeed],[wAL],[wDL],[bCriticalPP],[dwMaxHP],[bHPRecover],[wMAP],[bCriticalMP],[dwMaxMP],[bMPRecover],[wDP],[wMDP],[wMinWAP],[wMaxWAP],[wWDP],[wMAL],[wMDL] FROM dbo.TMONATTRCHART WHERE wID=30050 AND bLevel=10 EXCEPT SELECT 30050,10,56,250,0,23,124,15,711,255,58,15,485,255,100,100,16,19,230,703,124)
 AND EXISTS(SELECT [wID],[bLevel],[wAP],[wLAP],[dwAtkSpeed],[wAL],[wDL],[bCriticalPP],[dwMaxHP],[bHPRecover],[wMAP],[bCriticalMP],[dwMaxMP],[bMPRecover],[wDP],[wMDP],[wMinWAP],[wMaxWAP],[wWDP],[wMAL],[wMDL] FROM dbo.TMONATTRCHART WHERE wID=30050 AND bLevel=10 EXCEPT SELECT 30050,10,56,250,0,23,11,15,711,255,58,15,485,255,100,100,16,19,230,703,11)
 THROW 51060,'Moray defense baseline differs',1;
UPDATE dbo.TMONATTRCHART SET wDL=11,wMDL=11 WHERE wID=30050 AND bLevel=10;
IF EXISTS(SELECT [wID],[bLevel],[wAP],[wLAP],[dwAtkSpeed],[wAL],[wDL],[bCriticalPP],[dwMaxHP],[bHPRecover],[wMAP],[bCriticalMP],[dwMaxMP],[bMPRecover],[wDP],[wMDP],[wMinWAP],[wMaxWAP],[wWDP],[wMAL],[wMDL] FROM dbo.TMONATTRCHART WHERE wID=30050 AND bLevel=10 EXCEPT SELECT 30050,10,56,250,0,23,11,15,711,255,58,15,485,255,100,100,16,19,230,703,11) THROW 51060,'Moray defense postcondition differs',1;
COMMIT TRANSACTION;
GO
