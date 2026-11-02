-- Durable receipt for SQL-queued server reward mail. Keep receipts after a player collects/deletes mail.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.TOperatorPostOperation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TOperatorPostOperation(
        OperationId uniqueidentifier NOT NULL CONSTRAINT PK_TOperatorPostOperation PRIMARY KEY,
        CharId int NOT NULL,
        Payload nvarchar(max) NOT NULL,
        Result int NOT NULL,
        PostId int NOT NULL,
        ReceiverName varchar(50) NOT NULL,
        CompletedAt datetime2 NOT NULL CONSTRAINT DF_TOperatorPostOperation_CompletedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_TOperatorPostOperation_Result CHECK ((Result=0 AND PostId<>0) OR (Result=1 AND PostId=0))
    );
END;
COMMIT;
GO
