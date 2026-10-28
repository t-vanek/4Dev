-- Restore the target-active prerequisite of skill 201 from the pinned Gameforge CZ client.
-- Tcd/TSkill.tcd SHA-256 c2e4ad449b4c120994d36f871b3122050398387d3f65072581c6fa103c8f448a
-- Original 144-byte skill tail: previous/item/target active IDs at byte offsets 0/2/4.
-- See Gameforge/normalization/skill-active-wear.json. No character or learned-skill rows change.
USE [TGame_gsp];
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
BEGIN TRY
    BEGIN TRANSACTION;
    IF NOT EXISTS (SELECT 1 FROM dbo.TSKILLCHART WITH (UPDLOCK, HOLDLOCK) WHERE wID = 200)
        THROW 50040, 'Missing target-active skill 200; verify the Gameforge baseline before applying migration 040.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.TSKILLCHART WITH (UPDLOCK, HOLDLOCK)
                   WHERE wID = 201 AND wTargetActiveID IN (0, 200))
        THROW 50040, 'Skill 201 is missing or has an unexpected target-active value; migration 040 was not applied.', 1;
    UPDATE dbo.TSKILLCHART SET wTargetActiveID = 200 WHERE wID = 201 AND wTargetActiveID = 0;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
