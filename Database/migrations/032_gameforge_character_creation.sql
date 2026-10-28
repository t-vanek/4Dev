-- Remove the identified inherited private-server creation behavior; not a claim of a full official stored procedure.
-- Existing accounts, characters, pets, mail and starter charts are retained.
USE [TGame_gsp];
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
DECLARE @hash VARCHAR(64)=CONVERT(VARCHAR(64),HASHBYTES('SHA2_256',REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'dbo.TCreateChar')),CHAR(13),'')),2);
IF @hash IS NULL OR @hash NOT IN('add9e29920053c31875be49fee9e2c96990c3526db3d9da9f3ecad60eab83d43','5734b24fc535188588efff66348f80de56844c20f37a0179d49f00e87f29051c') THROW 51000,'Character creation procedure baseline changed',1;
IF @hash='add9e29920053c31875be49fee9e2c96990c3526db3d9da9f3ecad60eab83d43' EXEC sys.sp_executesql N'






/* CREATE CHARACTER PROCESS

========================================================
PARAMETER
========================================================
@szNAME		VARCHAR(50)
@dwCharID		INT		OUTPUT
@dwUserID		INT
@bSlot			TINYINT
@bClass		TINYINT
@bRace		TINYINT
@bCountry		TINYINT
@bSex			TINYINT
@bHair			TINYINT
@bFace		TINYINT
@bBody		TINYINT
@bPants		TINYINT
@bHand		TINYINT
@bFoot			TINYINT

========================================================
RETURN VALUE
========================================================
0	: SUCCESS
1	: NO GROUP
2	: DUPLICATE NAME
3	: INVALID SLOT
4	: Non Class ID 

========================================================
PROCESS
========================================================
1. Check duplicate name
2. Check slot
3. Insert character data

*/


ALTER PROCEDURE [dbo].[TCreateChar]
	@bCreateCnt		TINYINT	OUTPUT,
	@szNAME		VARCHAR(50),
	@dwCharID		INT		OUTPUT,
	@dwUserID		INT,
	@bGroup		TINYINT,
	@bSlot			TINYINT,
	@bClass		TINYINT,
	@bRace		TINYINT,
	@bCountry		TINYINT,
	@bSex			TINYINT,
	@bHair			TINYINT,
	@bFace		TINYINT,
	@bBody		TINYINT,
	@bPants		TINYINT,
	@bHand		TINYINT,
	@bFoot			TINYINT,
	@bLevelOption			TINYINT
AS
	DECLARE @dwCharSeq INT
	DECLARE @dwMP	INT
	DECLARE @dwHP	INT
	DECLARE @fPosX	FLOAT
	DECLARE @fPosY	FLOAT
	DECLARE @fPosZ	FLOAT
	DECLARE @wDIR	SMALLINT
	DECLARE @wSpawnID SMALLINT
	DECLARE @nCheckGlobal INT
	DECLARE @bRealSex	TINYINT

	SET @bCreateCnt = 0
	SET @dwCharID = 0
	SET @dwMP = 2
	SET @dwHP = 2

	SELECT TOP 1 @dwHP = @dwHP + wCON, @dwMP = @dwMP + wMEN FROM TCLASSCHART WHERE bClassID = @bClass
	SELECT TOP 1 @dwHP = @dwHP + wCON, @dwMP = @dwMP + wMEN FROM TRACECHART WHERE bRaceID = @bRace

	SET @dwHP = 7 * @dwHP + 1
	SET @dwMP = 9 * @dwMP + 1

	IF EXISTS( SELECT TOP 1 dwCharID FROM TCHARTABLE WHERE szNAME = @szNAME)
		RETURN 2
	/*
   	 * Duplicate npc name 
	 */
	IF EXISTS(SELECT TOP 1 * FROM TNPCCHART WHERE szNAME = @szNAME)
		RETURN 2
	/*
	 * Duplicate monster name
	 */
	IF EXISTS(SELECT TOP 1 * FROM TMONSTERCHART WHERE szNAME = @szNAME)
		RETURN 2
	/*
	 * Invalid slot
	 */
	IF EXISTS(SELECT TOP 1 dwCharID FROM TCHARTABLE WHERE dwUserID = @dwUserID AND bSlot = @bSlot AND bDelete = 0)
		RETURN 3
	/*
 	 * Check the country
	 */

	DECLARE @bOriCountry TINYINT
	DECLARE @bLevel	TINYINT
	DECLARE @dwExp	INT
	DECLARE @wSkillPoint	SMALLINT
	SET @bOriCountry = 4
	SET @bLevel = 1
	SET @dwExp = 1
	SET @wSpawnID = 15003
	SET @wSkillPoint = 0

	SELECT TOP 1 @bOriCountry = bOriCountry FROM TCHARTABLE WHERE dwUserID=@dwUserID AND bDelete=0 AND bOriCountry < 2
	-- All new characters use the neutral level-one path. The legacy option parameter is ignored.
	SET @bCountry = 4

	SET @fPosX 	= 3664.405
	SET @fPosY 	= 86.16578
	SET @fPosZ 	= 557.2542
	SET @wDIR = 762

	EXEC @nCheckGlobal = TGLOBAL_GSP.DBO.TCreateChar
		@dwCharSeq  OUTPUT,
		@bCreateCnt OUTPUT,
		@bRealSex OUTPUT,
		@dwUserID,
		@bGroup,
		@dwCharID,
		@bSlot,
		@szNAME,
		@bClass,
		@bRace,
		@bCountry,
		@bSex,
		@bHair,		-- bHair
		@bFace,	-- bFace
		@bBody,	-- bBody
		@bPants,	-- bPants
		@bHand,	-- bHand
		@bFoot,	-- bFoot		
		@bLevel,
		@dwExp	-- dwExp

	IF(@nCheckGlobal <> 0)
		RETURN @nCheckGlobal

	BEGIN TRAN TCREATECHAR

	INSERT INTO TCHARTABLE
	(
		dwUserID,
		bSlot,
		szNAME,
		bRace,
		bCountry,
		bOriCountry,
		bSex,
		bRealSex,
		bClass,
		bLevel,
		bHair,
		bFace,
		bBody,
		bPants,
		bHand,
		bFoot,
		dwEXP,
		dwHP,
		dwMP,
		wSkillPoint,
		dwGold,
		dwSilver,
		dwCooper,
		wMapID,
		wSpawnID,
		wTemptedMon,
		bAftermath,
		fPosX,
		fPosY,
		fPosZ,
		wDIR
	) 
	VALUES
	(
		@dwUserID,	-- dwUserID
		@bSlot,		-- bSlot
		@szNAME,	-- szNAME
		@bRace,	-- bRace
		@bCountry,	-- bCountry
		@bOriCountry,
		@bSex,		-- bSex
		@bRealSex,	-- bRealSex
		@bClass,	-- bClass
		@bLevel,	-- bLevel
		@bHair,		-- bHair
		@bFace,
		@bBody,
		@bPants,
		@bHand,
		@bFoot,
		@dwExp,
		@dwHP,
		@dwMP,
		@wSkillPoint,
		0,
		0,
		0,
		2010,
		@wSpawnID,
		0,
		0,
		@fPosX,
		@fPosY,
		@fPosZ,
		@wDIR
	)

	SET @dwCharID = @@IDENTITY
	INSERT INTO TINVENTABLE
	(
		dwCharID,
		bInvenID,
		wItemID,
		dEndTime
	) 
	VALUES
	(
		@dwCharID,
		255, 
		3,
		0
	)

	INSERT INTO TINVENTABLE
	(
		dwCharID,
		bInvenID,
		wItemID,
		dEndTime
	) 
	VALUES
	(
		@dwCharID,
		254, 
		2,
		0
	)

	INSERT INTO TTITLETABLE VALUES(@dwCharID, 0, 1)
	INSERT INTO TCABINETTABLE VALUES(@dwCharID, 0, 1)
	INSERT INTO TSKILLTABLE SELECT @dwCharID, wSkillID, bLevel, 0 FROM TSTARTSKILL WHERE bClassID=@bClass
	COMMIT TRAN TCREATECHAR

	DECLARE @bStartInven TINYINT
	DECLARE @bStartSlot TINYINT
	DECLARE @bStartChartType TINYINT
	DECLARE @wStartItemID SMALLINT
	DECLARE @bStartCount TINYINT
	DECLARE CUR_STARTITEM CURSOR FOR
	SELECT bInven, bSlot, bChartType, wItemID, bCount FROM TSTARTITEMCHART WHERE bCountry=@bCountry AND bClass = @bClass
	OPEN CUR_STARTITEM
	FETCH NEXT FROM CUR_STARTITEM INTO @bStartInven, @bStartSlot, @bStartChartType, @wStartItemID, @bStartCount
	WHILE @@FETCH_STATUS = 0
	BEGIN
		EXEC TPutItemInInven @dwCharID, @bStartInven, @bStartSlot, @bStartChartType, @wStartItemID, @bStartCount
		FETCH NEXT FROM CUR_STARTITEM INTO  @bStartInven, @bStartSlot, @bStartChartType, @wStartItemID, @bStartCount
	END
	CLOSE CUR_STARTITEM
	DEALLOCATE CUR_STARTITEM

-- Inherited 4StoryPW welcome mail removed.


	UPDATE TGLOBAL_GSP.DBO.TALLCHARTABLE SET dwCharID = @dwCharID WHERE dwSeq = @dwCharSeq

	INSERT INTO THOTKEYTABLE 
		SELECT @dwCharID, bInvenID, bType1, wID1, bType2, wID2, bType3, wID3, bType4, wID4, bType5, wID5,
			  bType6, wID6, bType7, wID7, bType8, wID8, bType9, wID9, bType10, wID10, bType11, wID11, bType12, wID12 
		FROM TSTARTHOTKEY WHERE bClassID=@bClass

	--UPDATE THOTKEYTABLE SET wID12 = 1+@bRace WHERE dwCharID = @dwCharID and bInvenID = 1

	IF EXISTS( SELECT bClassID FROM TSTARTRECALL WHERE bClassID = @bClass AND bCountryID = @bCountry)
	BEGIN
		DECLARE @dwMonID INT
		DECLARE @wMonTemp SMALLINT
		DECLARE @dwATTR INT
		DECLARE @dwMaxHP INT
		DECLARE @dwMaxMP INT
		SET @wMonTemp = 0
		SELECT @wMonTemp = wMonID FROM TSTARTRECALL WHERE bClassID = @bClass AND bCountryID = @bCountry
		IF(@wMonTemp > 0)
		BEGIN
			SELECT @dwATTR = wSummonAttr FROM TMONSTERCHART WHERE wID= @wMonTemp
			SELECT @dwMaxHP=dwMaxHP, @dwMaxMP = dwMaxMP FROM TMONATTRCHART WHERE wID=@dwATTR AND bLevel=1
			UPDATE TCHARTABLE SET wTemptedMon = @wMonTemp WHERE dwCharID = @dwCharID
			SET @dwATTR = @dwATTR + POWER(2,16)
			EXEC TCreateRecallMon 
				@dwMonID OUTPUT, 
				@dwCharID, 
				@wMonTemp, 
				0, 
				@dwATTR, 
				1, 
				@dwMaxHP, 
				@dwMaxMP, 
				1,
				@fPosX, 
				@fPosY,

				@fPosZ,
				0
		END
	END
	-- No automatic account-mount deletion or grant during character creation.


	RETURN @@ERROR
';
IF CONVERT(VARCHAR(64),HASHBYTES('SHA2_256',REPLACE(OBJECT_DEFINITION(OBJECT_ID(N'dbo.TCreateChar')),CHAR(13),'')),2)<>'5734b24fc535188588efff66348f80de56844c20f37a0179d49f00e87f29051c'
    THROW 51000,'Character creation procedure postcondition failed',1;
COMMIT TRANSACTION;
GO
