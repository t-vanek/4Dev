-- Repair the inherited save procedure without changing gameplay formulas or rewards.
-- Stat* values are already part of the native/C# save contract. Remove the private name-only gift.
USE [TGame_gsp];
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @definition nvarchar(max) = OBJECT_DEFINITION(OBJECT_ID(N'dbo.TSaveChar'));
    IF @definition IS NULL OR COL_LENGTH(N'dbo.TCHARTABLE',N'bStatLevel') IS NULL
        OR COL_LENGTH(N'dbo.TCHARTABLE',N'bStatPoint') IS NULL OR COL_LENGTH(N'dbo.TCHARTABLE',N'dwStatExp') IS NULL
        THROW 50041, 'Missing character-save contract; migration 041 was not applied.', 1;

    DECLARE @start int = CHARINDEX(N'DECLARE @szName VARCHAR(255)', @definition);
    IF @start > 0
    BEGIN
        DECLARE @finish int = CHARINDEX(N'COMMIT TRAN TSAVECHAR', @definition, @start);
        DECLARE @block nvarchar(max) = SUBSTRING(@definition, @start, NULLIF(@finish,0)-@start);
        IF @finish=0 OR CHARINDEX(N'EXEC TEventItemGive',@block)=0
            OR CHARINDEX(N'IF(@szName = ''hugoboss1234'')',@block)=0
            THROW 50041, 'Unexpected character-save event block; migration 041 requires review.', 1;
        SET @definition = STUFF(@definition,@start,@finish-@start,N'');
    END;
    IF CHARINDEX(N'EXEC TEventItemGive',@definition)>0
        THROW 50041, 'Unexpected gift call remains in character save.', 1;

    IF CHARINDEX(N'bStatLevel = @StatLevel',@definition)=0
    BEGIN
        DECLARE @anchor nvarchar(100) = N'dwLastDestination = @dwLastDestination';
        IF CHARINDEX(@anchor,@definition)=0
            THROW 50041, 'Unexpected character-save UPDATE; migration 041 requires review.', 1;
        SET @definition = REPLACE(@definition,@anchor,@anchor+
            N', bStatLevel = @StatLevel, bStatPoint = @StatPoint, dwStatExp = @StatExp');
    END;
    IF CHARINDEX(N'bStatPoint = @StatPoint',@definition)=0 OR CHARINDEX(N'dwStatExp = @StatExp',@definition)=0
        THROW 50041, 'Incomplete character stat persistence contract.', 1;

    DECLARE @create int = CHARINDEX(N'CREATE PROCEDURE',UPPER(@definition));
    IF @create>0 SET @definition=STUFF(@definition,@create,6,N'ALTER');
    ELSE IF CHARINDEX(N'ALTER PROCEDURE',UPPER(@definition))=0
        THROW 50041, 'Unexpected procedure declaration.', 1;
    EXEC sys.sp_executesql @definition;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
