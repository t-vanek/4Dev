-- sqlcmd -v QuestTestDb=4DevQuestTest_local -i prepare.sql
-- Schema/procedure copies only. Never copies player data and never overwrites an existing database.
SET NOCOUNT ON;
IF N'$(QuestTestDb)' NOT LIKE N'4DevQuestTest[_]%' THROW 51000, 'Isolated database name required', 1;
IF DB_ID(N'$(QuestTestDb)') IS NOT NULL THROW 51000, 'Test database already exists', 1;
CREATE DATABASE [$(QuestTestDb)];
GO
USE [$(QuestTestDb)];
CREATE TABLE TCHARTABLE(dwCharID int NOT NULL PRIMARY KEY, szName varchar(50) NOT NULL,
    dwUserID int NOT NULL DEFAULT(9), bOriCountry tinyint NOT NULL DEFAULT(1));
CREATE TABLE TQTITLECHART(dwQuestID int NOT NULL PRIMARY KEY, szTitle varchar(255), szMessage varchar(1024), szNPCName varchar(255));
SELECT TOP(0) * INTO TRESERVEDPOST FROM TGame_gsp.dbo.TRESERVEDPOST;
SELECT TOP(0) * INTO TPOSTTABLE FROM TGame_gsp.dbo.TPOSTTABLE;
SELECT TOP(0) * INTO TITEMTABLE FROM TGame_gsp.dbo.TITEMTABLE;
SELECT TOP(0) * INTO TDBITEMINDEXTABLE FROM TGame_gsp.dbo.TDBITEMINDEXTABLE;
SELECT TOP(0) * INTO TQUESTTABLE FROM TGame_gsp.dbo.TQUESTTABLE;
SELECT TOP(0) * INTO TQUESTTERMTABLE FROM TGame_gsp.dbo.TQUESTTERMTABLE;
SELECT TOP(0) * INTO THOTKEYTABLE FROM TGame_gsp.dbo.THOTKEYTABLE;
SELECT TOP(0) * INTO TPETTABLE FROM TGame_gsp.dbo.TPETTABLE;
SELECT TOP(0) * INTO TMOUNTITEMTABLE FROM TGame_gsp.dbo.TMOUNTITEMTABLE;
SELECT TOP(0) * INTO TCOMPANIONTABLE FROM TGame_gsp.dbo.TCOMPANIONTABLE;
SELECT TOP(0) * INTO TCOMPANIONITEMTABLE FROM TGame_gsp.dbo.TCOMPANIONITEMTABLE;
SELECT TOP(0) * INTO TLASTCOMPANIONTABLE FROM TGame_gsp.dbo.TLASTCOMPANIONTABLE;
SELECT TOP(0) * INTO TMEDALS FROM TGame_gsp.dbo.TMEDALS;
-- Use an empty LOCAL table shaped like the view; never copy its cross-database definition.
SELECT TOP(0) * INTO TVIEW_DURINGITEMTABLE FROM TGame_gsp.dbo.TVIEW_DURINGITEMTABLE;
SELECT TOP(0) * INTO TEXPITEMTABLE FROM TGame_gsp.dbo.TEXPITEMTABLE;
SELECT TOP(0) * INTO TRECALLMONTABLE FROM TGame_gsp.dbo.TRECALLMONTABLE;
SELECT TOP(0) * INTO TRECALLMAINTAINTABLE FROM TGame_gsp.dbo.TRECALLMAINTAINTABLE;
SELECT TOP(0) * INTO TAIDTABLE FROM TGame_gsp.dbo.TAIDTABLE;
ALTER TABLE TITEMTABLE ADD PRIMARY KEY(dlID);
ALTER TABLE TPOSTTABLE ADD PRIMARY KEY(dwPostID);
ALTER TABLE TQUESTTABLE ADD PRIMARY KEY(dwCharID,dwQuestID);
ALTER TABLE TQUESTTERMTABLE ADD PRIMARY KEY(dwCharID,dwQuestID,dwTermID);
INSERT TDBITEMINDEXTABLE(bWorld,dlID) VALUES(0,1000000000);
DECLARE @proc nvarchar(max);
DECLARE procs CURSOR LOCAL FAST_FORWARD FOR SELECT definition FROM TGame_gsp.sys.sql_modules WHERE object_id IN (
    SELECT object_id FROM TGame_gsp.sys.procedures WHERE name IN
    ('TGetReservedPost','TSavePost','TSaveItemDirect','TGenerateDBItemID','TInitGenItemID','TSaveQuest','TSaveQuestTerm','TSaveHotkey',
     'TGetLastCompanion','TGetMedals','TSaveLastCompanion','TSaveMedals','TSaveCompanion','TSavePet','TGetMountSaddle'));
OPEN procs; FETCH NEXT FROM procs INTO @proc;
WHILE @@FETCH_STATUS=0 BEGIN
    -- sp_rename in the inherited DB left this module's CREATE header under its old name.
    SET @proc=REPLACE(@proc,N'[dbo].[TGetMountSaddle_copy]',N'[dbo].[TGetMountSaddle]');
    EXEC sys.sp_executesql @proc; FETCH NEXT FROM procs INTO @proc;
END;
CLOSE procs; DEALLOCATE procs;
