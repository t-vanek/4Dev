-- TLogin looked an account up by username twice, then updated its clustered user-id row in the transaction.
-- Without a username index concurrent lookups scanned rows locked by unrelated accounts' updates.
-- Keep READ COMMITTED and the existing stored-procedure results; do not mask duplicate-session races with NOLOCK.
USE [TGlobal_gsp];
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.TACCOUNT_PW') AND name='IX_TACCOUNT_PW_LoginName')
    CREATE INDEX IX_TACCOUNT_PW_LoginName ON dbo.TACCOUNT_PW(szUserID) INCLUDE(szPasswd, bCheck);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.IPBLACKLIST_game') AND name='IX_IPBLACKLIST_game_IP')
    CREATE INDEX IX_IPBLACKLIST_game_IP ON dbo.IPBLACKLIST_game(szIP);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.TUSERPROTECTED') AND name='IX_TUSERPROTECTED_Login')
    CREATE INDEX IX_TUSERPROTECTED_Login ON dbo.TUSERPROTECTED(dwUserID) INCLUDE(startTime, dwDuration, bEternal);
GO
