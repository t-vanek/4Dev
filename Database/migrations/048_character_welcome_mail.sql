-- User-requested custom welcome reward. Disabled until its SQL settings/items are configured.
-- Existing characters are deliberately not enrolled. This does not alter TCreateChar or starter items.
USE [TGame_gsp];
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.TWelcomeMailSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TWelcomeMailSettings(
        SettingsId tinyint NOT NULL CONSTRAINT PK_TWelcomeMailSettings PRIMARY KEY,
        Enabled bit NOT NULL CONSTRAINT DF_TWelcomeMailSettings_Enabled DEFAULT 0,
        Title nvarchar(248) NOT NULL,
        Message nvarchar(2040) NOT NULL,
        CONSTRAINT CK_TWelcomeMailSettings_Singleton CHECK(SettingsId=1)
    );
    INSERT dbo.TWelcomeMailSettings(SettingsId,Enabled,Title,Message)
    VALUES(1,0,N'Vitej ve 4Story!',N'Vitej ve svete 4Story! Prejeme prijemnou hru!');
END;
IF OBJECT_ID(N'dbo.TWelcomeMailItem', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TWelcomeMailItem(
        ItemId int NOT NULL CONSTRAINT PK_TWelcomeMailItem PRIMARY KEY,
        ItemCount int NOT NULL,
        CONSTRAINT CK_TWelcomeMailItem_Id CHECK(ItemId BETWEEN 1 AND 65535),
        CONSTRAINT CK_TWelcomeMailItem_Count CHECK(ItemCount BETWEEN 1 AND 65280)
    );
END;
IF OBJECT_ID(N'dbo.TCharacterWelcomeMail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TCharacterWelcomeMail(
        CharId int NOT NULL CONSTRAINT PK_TCharacterWelcomeMail PRIMARY KEY,
        OperationId uniqueidentifier NOT NULL CONSTRAINT UQ_TCharacterWelcomeMail_Operation UNIQUE,
        CreatedAt datetime2 NOT NULL CONSTRAINT DF_TCharacterWelcomeMail_Created DEFAULT SYSUTCDATETIME(),
        FirstEnteredAt datetime2 NULL,
        Payload nvarchar(max) NULL,
        DispatchedAt datetime2 NULL,
        CONSTRAINT CK_TCharacterWelcomeMail_State CHECK(
            (Payload IS NULL AND DispatchedAt IS NULL) OR (Payload IS NOT NULL AND FirstEnteredAt IS NOT NULL))
    );
    CREATE INDEX IX_TCharacterWelcomeMail_Pending ON dbo.TCharacterWelcomeMail(DispatchedAt,CharId)
        WHERE Payload IS NOT NULL;
END;
-- No foreign key to TCHARTABLE: normal character deletion must not be blocked by a permanent receipt.
EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_TCharacterWelcomeMail_Register
ON dbo.TCHARTABLE AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM dbo.TWelcomeMailSettings WHERE SettingsId=1 AND Enabled=1)
        INSERT dbo.TCharacterWelcomeMail(CharId,OperationId)
            SELECT i.dwCharID,NEWID() FROM inserted i WHERE i.bDelete=0;
END');
COMMIT TRANSACTION;
GO
