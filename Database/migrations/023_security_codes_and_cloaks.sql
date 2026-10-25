-- Persistent account security codes and the custom-cloak lookup index.
-- Existing plaintext codes are read for compatibility and replaced with a salted hash on their next save.
USE [TGlobal_gsp];
GO
IF OBJECT_ID('dbo.TSECURECODE', 'U') IS NULL
    CREATE TABLE dbo.TSECURECODE (
        dwUserID int NOT NULL PRIMARY KEY, strSecurityCode varchar(55) NOT NULL DEFAULT '',
        bEnabled int NOT NULL DEFAULT 1, bTries int NOT NULL DEFAULT 0, iLockTick int NOT NULL DEFAULT 0);
GO
IF COL_LENGTH('dbo.TSECURECODE', 'strCodeHash') IS NULL
    ALTER TABLE dbo.TSECURECODE ADD strCodeHash varchar(100) NULL;
IF COL_LENGTH('dbo.TSECURECODE', 'iLockUntilUnix') IS NULL
    ALTER TABLE dbo.TSECURECODE ADD iLockUntilUnix bigint NOT NULL CONSTRAINT DF_TSECURECODE_LockUntil DEFAULT 0;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TSECURECODE') AND name = 'IX_TSECURECODE_User')
    CREATE INDEX IX_TSECURECODE_User ON dbo.TSECURECODE(dwUserID);
GO
-- An old GetTickCount value has no meaning after a process restart. Retain an existing lock for 24 hours once.
UPDATE dbo.TSECURECODE SET iLockUntilUnix = DATEDIFF_BIG(SECOND, '19700101', SYSUTCDATETIME()) + 86400
    WHERE bTries >= 10 AND iLockUntilUnix = 0;
GO
USE [TGame_gsp];
GO
IF OBJECT_ID('dbo.TCUSTOMCLOAKTABLE', 'U') IS NULL
    CREATE TABLE dbo.TCUSTOMCLOAKTABLE (dwUserID int NOT NULL, wID smallint NOT NULL, dDateCreate smalldatetime NOT NULL DEFAULT GETDATE());
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TCUSTOMCLOAKTABLE') AND name = 'IX_TCUSTOMCLOAKTABLE_User')
    CREATE INDEX IX_TCUSTOMCLOAKTABLE_User ON dbo.TCUSTOMCLOAKTABLE(dwUserID, wID) INCLUDE(dDateCreate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TMEDALS') AND name = 'IX_TMEDALS_Char')
    CREATE INDEX IX_TMEDALS_Char ON dbo.TMEDALS(dwCharID) INCLUDE(dwMedals);
GO
