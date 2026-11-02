-- SQL-only durable save queue. Apply before deploying the Map server using it.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.TCharacterSaveOwner', N'U') IS NULL
    CREATE TABLE dbo.TCharacterSaveOwner
    (
        OwnerKey nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        IdentityHash char(64) NOT NULL
    );
IF OBJECT_ID(N'dbo.TCharacterSaveQueue', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TCharacterSaveQueue
    (
        Sequence bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        OwnerKey nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL REFERENCES dbo.TCharacterSaveOwner(OwnerKey),
        OperationId uniqueidentifier NOT NULL,
        Checksum char(64) NOT NULL,
        Payload nvarchar(max) NOT NULL,
        CONSTRAINT UQ_TCharacterSaveQueue_Operation UNIQUE(OwnerKey, OperationId)
    );
    CREATE INDEX IX_TCharacterSaveQueue_Owner ON dbo.TCharacterSaveQueue(OwnerKey, Sequence);
END;
IF COL_LENGTH(N'dbo.TCharacterSaveOwner', N'IdentityHash') IS NULL
    OR COL_LENGTH(N'dbo.TCharacterSaveQueue', N'OwnerKey') IS NULL
    OR COL_LENGTH(N'dbo.TCharacterSaveQueue', N'OperationId') IS NULL
    OR COL_LENGTH(N'dbo.TCharacterSaveQueue', N'Checksum') IS NULL
    OR COL_LENGTH(N'dbo.TCharacterSaveQueue', N'Payload') IS NULL
    THROW 51043, 'Unexpected SQL character save queue schema', 1;
COMMIT;
GO
