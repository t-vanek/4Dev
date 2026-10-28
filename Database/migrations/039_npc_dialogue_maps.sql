USE [TGame_gsp];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- NPCs 22424..22433: the client places them on map 12, but SQL used map 11.
-- Both the pinned original CZ client and the current client agree on this map.
-- Source: Tcd/TNPC000c0000.tcd; SHA-256:
-- 5ef5ebda5691f9edabf32aebcf74e9f7c722a202713a769356b637732ca169cc
-- Frozen row/source evidence: Gameforge/npc-map-repair/plan.json.
-- Only wMapID changes; quest graphs, stock, positions and player progress are retained.
DECLARE @Expected TABLE (
    NpcId smallint NOT NULL PRIMARY KEY, QuestId int NOT NULL, NpcName nvarchar(128) NOT NULL,
    Country tinyint NOT NULL, ItemId int NOT NULL,
    XBits varbinary(8) NOT NULL, YBits varbinary(8) NOT NULL, ZBits varbinary(8) NOT NULL
);
INSERT INTO @Expected VALUES
    (22424, 4578, N'Gelten', 1, 0, 0x407799999999999A, 0x3FF30A3D70A3D70A, 0x4072F73333333333),
    (22425, 4579, N'Sillelia', 1, 9430, 0x407725999999999A, 0x4013CCCCCCCCCCCD, 0x407B075C28F5C28F),
    (22426, 4580, N'Sillelia', 1, 9237, 0x407725999999999A, 0x4013CCCCCCCCCCCD, 0x407B075C28F5C28F),
    (22427, 4581, N'Dark Elf', 0, 0, 0x40801F0A3D70A3D7, 0xC00BAE147AE147AE, 0x407FDEB851EB851F),
    (22428, 4582, N'Dark Elf', 0, 0, 0x40801147AE147AE1, 0xC00C28F5C28F5C29, 0x407FD8CCCCCCCCCD),
    (22429, 4583, N'Dark Elf', 0, 0, 0x4080311EB851EB85, 0xC00ACCCCCCCCCCCD, 0x407FD7AE147AE148),
    (22430, 4584, N'Dark Elf', 0, 0, 0x4077D3851EB851EC, 0x3FF4F5C28F5C28F6, 0x40805CCCCCCCCCCD),
    (22431, 4585, N'Dark Elf', 0, 0, 0x4077DD70A3D70A3D, 0x3FFA147AE147AE14, 0x4080453333333333),
    (22432, 4586, N'Dark Elf', 0, 0, 0x4077C80000000000, 0x3FF3333333333333, 0x4072F5EB851EB852),
    (22433, 4587, N'Dark Elf', 0, 0, 0x4077AA8F5C28F5C3, 0x3FF428F5C28F5C29, 0x4072C5C28F5C28F6);

IF NOT EXISTS (SELECT 1 FROM dbo.TMAPCHART WHERE wMapID=12)
    THROW 51000, 'NPC map repair requires map 12; inspect before applying 039.', 1;

IF (SELECT COUNT(*) FROM @Expected e
    JOIN dbo.TNPCCHART n WITH (UPDLOCK, HOLDLOCK) ON n.wID=e.NpcId
    WHERE n.szName=e.NpcName AND n.bType=0 AND n.dwClass=255
      AND n.bCountryID=e.Country AND n.wItemID=e.ItemId AND n.wLocalID=0
      AND n.wMapID IN (11,12)
      AND CONVERT(varbinary(8),n.fPosX)=e.XBits
      AND CONVERT(varbinary(8),n.fPosY)=e.YBits
      AND CONVERT(varbinary(8),n.fPosZ)=e.ZBits)<>10
    THROW 51000, 'NPC identity, placement or map baseline differs; inspect before applying 039.', 1;

IF (SELECT COUNT(*) FROM @Expected e
    JOIN dbo.TQUESTCHART q ON q.dwQuestID=e.QuestId
    WHERE q.bType=10 AND q.dwParentID=4558 AND q.bTriggerType=3 AND q.dwTriggerID=e.NpcId)<>10
    THROW 51000, 'NPC dialogue quest links differ; inspect before applying 039.', 1;

UPDATE n SET wMapID=12
FROM dbo.TNPCCHART n JOIN @Expected e ON n.wID=e.NpcId
WHERE n.wMapID=11;

IF (SELECT COUNT(*) FROM @Expected e JOIN dbo.TNPCCHART n ON n.wID=e.NpcId WHERE n.wMapID=12)<>10
    THROW 51000, 'NPC map repair postcondition failed.', 1;

COMMIT TRANSACTION;
GO
