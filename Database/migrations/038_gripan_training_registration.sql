USE [TGame_gsp];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Gripan (20001), the turn-in NPC for Training Registration (7981 -> 7983).
-- Restore six mapped NPC fields from the pinned Gameforge CZ client and the matching current client.
-- Tcd/TNPC00000402.tcd SHA-256:
-- 94c7a47b3d6f5f303e48105838b7138eed9dbec46443f643a23f92fef91e983a
-- Frozen before/source evidence: Gameforge/quest-training/gripan-repair.json.
-- Names, quest graphs, rewards, NPC stock and character progress are retained.
IF NOT EXISTS (
    SELECT 1 FROM dbo.TNPCCHART WITH (UPDLOCK, HOLDLOCK)
    WHERE wID=20001 AND szName=N'Gripan' AND bType=0 AND wMapID=0
      AND (
        (dwClass=0 AND bCountryID=69 AND wItemID=29545
         AND CONVERT(varbinary(8),fPosX)=0xBC8000002A672D52
         AND CONVERT(varbinary(8),fPosY)=0xB9E8A6E213419353
         AND CONVERT(varbinary(8),fPosZ)=0xC58852D84CC1D2DE)
        OR
        (dwClass=255 AND bCountryID=0 AND wItemID=0
         AND fPosX=2864.0 AND fPosY=66.23999786376953125 AND fPosZ=5031.0400390625)
      )
)
    THROW 51000, 'Gripan NPC identity or field baseline differs; inspect before applying 038.', 1;

UPDATE dbo.TNPCCHART
SET dwClass=255, bCountryID=0, wItemID=0,
    fPosX=2864.0, fPosY=66.23999786376953125, fPosZ=5031.0400390625
WHERE wID=20001;

IF NOT EXISTS (
    SELECT 1 FROM dbo.TNPCCHART
    WHERE wID=20001 AND dwClass=255 AND bCountryID=0 AND wItemID=0
      AND fPosX=2864.0 AND fPosY=66.23999786376953125 AND fPosZ=5031.0400390625
)
    THROW 51000, 'Gripan NPC repair postcondition failed.', 1;

COMMIT TRANSACTION;
GO
