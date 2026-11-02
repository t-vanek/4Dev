-- Repair items placed beyond client-visible container capacity by the old 100-slot fallback.
-- Maintenance only: stop Map writers first and drain their SQL save queues. No items are deleted,
-- merged, granted or changed beyond their inventory/slot coordinates. Fail atomically if space is lacking.
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue)
        THROW 51047,'Drain character save queues before repairing inventory slots',1;
    DECLARE @locked bigint;
    SELECT @locked=COUNT_BIG(*) FROM dbo.TITEMTABLE WITH(TABLOCKX,HOLDLOCK);
    CREATE TABLE #bags(CharId int NOT NULL, BagId int NOT NULL, Slots int NOT NULL, PRIMARY KEY(CharId,BagId));
    INSERT #bags
        SELECT i.dwCharID,i.bInvenID,t.bSlotCount
        FROM dbo.TINVENTABLE i JOIN dbo.TITEMCHART t ON t.wItemID=i.wItemID
        WHERE (i.bInvenID<5 OR i.bInvenID=255) AND t.bSlotCount>0;
    -- The native/client header uses item 3 for the always-present default inventory.
    INSERT #bags
        SELECT c.dwCharID,255,t.bSlotCount FROM dbo.TCHARTABLE c CROSS JOIN dbo.TITEMCHART t
        WHERE t.wItemID=3 AND t.bSlotCount>0
          AND NOT EXISTS(SELECT 1 FROM #bags b WHERE b.CharId=c.dwCharID AND b.BagId=255)
          AND NOT EXISTS(SELECT 1 FROM dbo.TINVENTABLE i WHERE i.dwCharID=c.dwCharID AND i.bInvenID=255);
    CREATE TABLE #bad(Id bigint NOT NULL PRIMARY KEY,CharId int NOT NULL,Ordinal bigint NOT NULL);
    ;WITH located AS (
        SELECT i.dlID,i.dwOwnerID,
            CASE WHEN b.BagId IS NULL OR i.bItemID>=b.Slots THEN 1 ELSE 0 END AS Invalid,
            ROW_NUMBER() OVER(PARTITION BY i.dwOwnerID,i.dwStorageID,i.bItemID ORDER BY i.dlID) AS Duplicate
        FROM dbo.TITEMTABLE i JOIN dbo.TCHARTABLE c ON c.dwCharID=i.dwOwnerID
        LEFT JOIN #bags b ON b.CharId=i.dwOwnerID AND b.BagId=i.dwStorageID
        WHERE i.bStorageType=0 AND i.bOwnerType=0 AND i.dwStorageID NOT IN (254,250)
    )
    INSERT #bad SELECT dlID,dwOwnerID,ROW_NUMBER() OVER(PARTITION BY dwOwnerID ORDER BY dlID)
        FROM located WHERE Invalid=1 OR Duplicate>1;
    CREATE TABLE #free(CharId int NOT NULL,BagId int NOT NULL,Slot int NOT NULL,Ordinal bigint NOT NULL);
    ;WITH numbers AS (SELECT 0 AS n UNION ALL SELECT n+1 FROM numbers WHERE n<254)
    INSERT #free
        SELECT b.CharId,b.BagId,n.n,
            ROW_NUMBER() OVER(PARTITION BY b.CharId ORDER BY CASE WHEN b.BagId=255 THEN -1 ELSE b.BagId END,n.n)
        FROM #bags b CROSS JOIN numbers n
        WHERE n.n<b.Slots AND NOT EXISTS(
            SELECT 1 FROM dbo.TITEMTABLE i WHERE i.bStorageType=0 AND i.bOwnerType=0
                AND i.dwOwnerID=b.CharId AND i.dwStorageID=b.BagId AND i.bItemID=n.n)
        OPTION(MAXRECURSION 255);
    IF EXISTS(SELECT 1 FROM #bad b LEFT JOIN #free f ON f.CharId=b.CharId AND f.Ordinal=b.Ordinal WHERE f.Ordinal IS NULL)
        THROW 51047,'Insufficient visible inventory space; no items were moved or deleted',1;
    UPDATE i SET dwStorageID=f.BagId,bItemID=f.Slot
        FROM dbo.TITEMTABLE i JOIN #bad b ON b.Id=i.dlID
        JOIN #free f ON f.CharId=b.CharId AND f.Ordinal=b.Ordinal;
    DECLARE @moved int=@@ROWCOUNT;
    DROP TABLE #free;
    DROP TABLE #bad;
    DROP TABLE #bags;
    COMMIT;
    SELECT @moved AS RecoveredInvisibleItems;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK;
    THROW;
END CATCH;
GO
