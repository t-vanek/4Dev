-- Durable SendPost issuance identity and continuation outbox. Native quest/mail tables are unchanged.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.TQuestPostOperation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TQuestPostOperation
    (
        Sequence bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        OperationId uniqueidentifier NOT NULL,
        CharId int NOT NULL,
        Payload nvarchar(max) NOT NULL,
        Result int NOT NULL,
        IssuedCount tinyint NOT NULL,
        Completed bit NOT NULL,
        CONSTRAINT UQ_TQuestPostOperation_Id UNIQUE (OperationId)
    );
    CREATE INDEX IX_TQuestPostOperation_Pending ON dbo.TQuestPostOperation(CharId, Completed, Sequence);
END;
IF COL_LENGTH(N'dbo.TQuestPostOperation', N'OperationId') IS NULL
   OR COL_LENGTH(N'dbo.TQuestPostOperation', N'Sequence') IS NULL
   OR COL_LENGTH(N'dbo.TQuestPostOperation', N'CharId') IS NULL
   OR COL_LENGTH(N'dbo.TQuestPostOperation', N'Payload') IS NULL
   OR COL_LENGTH(N'dbo.TQuestPostOperation', N'Result') IS NULL
   OR COL_LENGTH(N'dbo.TQuestPostOperation', N'IssuedCount') IS NULL
   OR COL_LENGTH(N'dbo.TQuestPostOperation', N'Completed') IS NULL
    THROW 51042, 'Unexpected quest post operation schema', 1;
COMMIT;
GO
