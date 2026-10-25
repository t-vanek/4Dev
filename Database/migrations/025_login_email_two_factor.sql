-- Login email OTPs. No account is enrolled automatically; use TLoginTwoFactorConfigure only after
-- independently verifying the account owner's email. No client-supplied recipient or MAC is trusted.
USE [TGlobal_gsp];
GO
IF OBJECT_ID('dbo.TLOGIN2FA', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TLOGIN2FA (
        dwUserID int NOT NULL CONSTRAINT PK_TLOGIN2FA PRIMARY KEY,
        szEmail nvarchar(254) NOT NULL,
        bEnabled bit NOT NULL CONSTRAINT DF_TLOGIN2FA_Enabled DEFAULT 0,
        SessionID uniqueidentifier NULL,
        ChallengeID uniqueidentifier NULL,
        CodeHash binary(32) NULL,
        ExpiresUtc datetime2 NULL,
        Delivered bit NOT NULL CONSTRAINT DF_TLOGIN2FA_Delivered DEFAULT 0,
        VerifiedUntilUtc datetime2 NULL,
        FailedAttempts int NOT NULL CONSTRAINT DF_TLOGIN2FA_Attempts DEFAULT 0,
        AttemptWindowUtc datetime2 NULL,
        LockedUntilUtc datetime2 NULL,
        LastSentUtc datetime2 NULL,
        SendWindowUtc datetime2 NULL,
        SentCount int NOT NULL CONSTRAINT DF_TLOGIN2FA_Sent DEFAULT 0,
        CONSTRAINT FK_TLOGIN2FA_Account FOREIGN KEY(dwUserID) REFERENCES dbo.TACCOUNT_PW(dwUserID) ON DELETE CASCADE,
        CONSTRAINT CK_TLOGIN2FA_Attempts CHECK (FailedAttempts >= 0 AND SentCount >= 0)
    );
END;
GO
-- Administrative enrollment/recovery only. Changing the recipient or disabling 2FA revokes every
-- outstanding code and verified grant, including those held by an already-connected client.
CREATE OR ALTER PROCEDURE dbo.TLoginTwoFactorConfigure
    @dwUserID int,
    @szEmail nvarchar(254),
    @bEnabled bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.TACCOUNT_PW WHERE dwUserID = @dwUserID)
        THROW 50001, 'Account does not exist.', 1;
    IF @szEmail IS NULL OR LEN(@szEmail) < 3 OR CHARINDEX('@', @szEmail) < 2
        OR CHARINDEX(CHAR(13), @szEmail) > 0 OR CHARINDEX(CHAR(10), @szEmail) > 0
        THROW 50002, 'A verified email address is required.', 1;
    BEGIN TRANSACTION;
    UPDATE dbo.TLOGIN2FA WITH (UPDLOCK, HOLDLOCK)
    SET szEmail = @szEmail, bEnabled = @bEnabled,
        SessionID = NULL, ChallengeID = NULL, CodeHash = NULL, ExpiresUtc = NULL,
        Delivered = 0, VerifiedUntilUtc = NULL, FailedAttempts = 0, AttemptWindowUtc = NULL,
        LockedUntilUtc = NULL, LastSentUtc = NULL, SendWindowUtc = NULL, SentCount = 0
    WHERE dwUserID = @dwUserID;
    IF @@ROWCOUNT = 0
        INSERT dbo.TLOGIN2FA(dwUserID, szEmail, bEnabled) VALUES(@dwUserID, @szEmail, @bEnabled);
    COMMIT;
END;
GO
