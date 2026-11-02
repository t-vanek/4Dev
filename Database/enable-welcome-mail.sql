-- User-selected scope: new characters only. User delegated gift choice: 5 small HP + 5 small MP potions.
-- Run after migration 048 and the corresponding Map image are deployed.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF (SELECT COUNT(*) FROM dbo.TITEMCHART WHERE (wItemID=8001 AND bKind=26 OR wItemID=8101 AND bKind=27)
    AND bLevel=1 AND bStack>=5)<>2
    THROW 51048,'Expected small life/mana potion templates are unavailable',1;
IF EXISTS(SELECT 1 FROM dbo.TWelcomeMailItem WHERE ItemId NOT IN(8001,8101) OR ItemCount<>5)
    THROW 51048,'Welcome gift already has different SQL settings; refusing to replace them',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TWelcomeMailItem WHERE ItemId=8001) INSERT dbo.TWelcomeMailItem VALUES(8001,5);
IF NOT EXISTS(SELECT 1 FROM dbo.TWelcomeMailItem WHERE ItemId=8101) INSERT dbo.TWelcomeMailItem VALUES(8101,5);
UPDATE dbo.TWelcomeMailSettings SET Enabled=1,Title=N'Vitej ve 4Story!',
    Message=N'Vitej ve svete 4Story! Pro prvni dobrodruzstvi dostavas 5x Maly napoj zivota a 5x Maly napoj many. Prejeme prijemnou hru!'
WHERE SettingsId=1;
IF @@ROWCOUNT<>1 THROW 51048,'Welcome mail settings are missing',1;
COMMIT TRANSACTION;
GO
