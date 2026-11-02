-- Functional target-integrity repair; no reward, timing or balance changes.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @definition nvarchar(max), @normalized nvarchar(max);
SET @definition=OBJECT_DEFINITION(OBJECT_ID(N'dbo.TSaveMissionOccupy'));
IF @definition IS NULL THROW 51044,'TSaveMissionOccupy is required',1;
IF CHARINDEX(N'4DEV_044_TARGET_GUARD',@definition)=0
BEGIN
    SET @normalized=REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@definition,CHAR(13),''),CHAR(10),''),CHAR(9),''),' ',''),'[',''),']','');
    IF @normalized<>N'CREATEPROCEDUREdbo.TSaveMissionOccupy@wLocalIDSMALLINT,@bTypeTINYINT,@dwCharIDINT,@bCountryTINYINTASUPDATETMISSIONTABLESETbCountry=@bCountryWHEREwMissionID=@wLocalID'
        THROW 51044,'Unexpected TSaveMissionOccupy body; review existing repairs before replacing it',1;
    EXEC(N'ALTER PROCEDURE dbo.TSaveMissionOccupy
        @wLocalID SMALLINT,@bType TINYINT,@dwCharID INT,@bCountry TINYINT
        AS BEGIN
            -- 4DEV_044_TARGET_GUARD
            UPDATE dbo.TMISSIONTABLE SET bCountry=@bCountry WHERE wMissionID=@wLocalID;
            IF @@ROWCOUNT<>1 RETURN 1;
            RETURN 0;
        END');
END;
SET @definition=OBJECT_DEFINITION(OBJECT_ID(N'dbo.TSaveSkyGardenOccupy'));
IF @definition IS NULL THROW 51044,'TSaveSkyGardenOccupy is required',1;
IF CHARINDEX(N'4DEV_044_TARGET_GUARD',@definition)=0
BEGIN
    SET @normalized=REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@definition,CHAR(13),''),CHAR(10),''),CHAR(9),''),' ',''),'[',''),']','');
    IF @normalized<>N'CREATEPROCEDUREdbo.TSaveSkyGardenOccupy@bCountryTINYINT,@wIDSMALLINT,@bTypeTINYINTASUPDATETSKYGARDENTABLESETbCountry=@bCountry'
        THROW 51044,'Unexpected TSaveSkyGardenOccupy body; review existing repairs before replacing it',1;
    EXEC(N'ALTER PROCEDURE dbo.TSaveSkyGardenOccupy
        @bCountry TINYINT,@wID SMALLINT,@bType TINYINT
        AS BEGIN
            -- 4DEV_044_TARGET_GUARD
            UPDATE dbo.TSKYGARDENTABLE SET bCountry=@bCountry WHERE wID=@wID;
            IF @@ROWCOUNT<>1 RETURN 1;
            RETURN 0;
        END');
END;
COMMIT;
GO
