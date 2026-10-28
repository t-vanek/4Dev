param([string]$Container = 'araz-mssql')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$migration = [IO.File]::ReadAllText((Join-Path $repository 'Database/migrations/026_gameforge_monster_levels_hp.sql'))
$reference = Get-Content -LiteralPath (Join-Path $repository 'Gameforge/monster-restoration.json') -Raw | ConvertFrom-Json
$prefix = @'
USE [TGame_gsp];
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.TGAMEFORGEMONSTERRESTORE',N'U') IS NOT NULL
    THROW 51000,'Run this rollback test against the pre-restoration baseline.',1;
BEGIN TRANSACTION;
DECLARE @beforeCount INT=(SELECT COUNT(*) FROM dbo.TMONATTRCHART);
DECLARE @beforeHash VARBINARY(32)=HASHBYTES('SHA2_256',(SELECT a.* FROM dbo.TMONATTRCHART a ORDER BY a.wID,a.bLevel FOR JSON PATH));
SELECT @beforeCount AS OriginalAttributeRows,@beforeHash AS OriginalAttributeHash INTO #OriginalAttributes;
SELECT wID,wExp INTO #OriginalMonsterExp FROM dbo.TMONSTERCHART;
GO
'@
$suffix = @'
USE [TGame_gsp];
DECLARE @oldHash VARBINARY(32)=(SELECT OriginalAttributeHash FROM #OriginalAttributes);
DECLARE @currentHash VARBINARY(32)=HASHBYTES('SHA2_256',(SELECT a.* FROM dbo.TMONATTRCHART a WHERE a.wID NOT IN(SELECT wReferenceAttr FROM dbo.TGAMEFORGEMONSTERRESTORE) ORDER BY a.wID,a.bLevel FOR JSON PATH));
IF @oldHash<>@currentHash THROW 51000,'An original monster attribute row was modified.',1;
IF EXISTS(SELECT 1 FROM dbo.TMONSTERCHART m JOIN #OriginalMonsterExp e ON e.wID=m.wID WHERE e.wExp<>m.wExp) THROW 51000,'Monster EXP changed without a verified source.',1;
IF (SELECT COUNT(*) FROM dbo.TGAMEFORGEMONSTERRESTORE)<>__COUNT__ THROW 51000,'Unexpected number of restored monsters.',1;
IF (SELECT COUNT(*) FROM dbo.TMONATTRCHART)<>(SELECT OriginalAttributeRows+__COUNT__ FROM #OriginalAttributes) THROW 51000,'Unexpected number of cloned attribute rows.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TMONSTERCHART m JOIN dbo.TMONATTRCHART a ON a.wID=m.wMonAttr AND a.bLevel=m.bLevel WHERE m.wID=243 AND m.bLevel=11 AND a.dwMaxHP=311)
    THROW 51000,'The independently checked Moray example is wrong.',1;
SELECT m.wID,m.bLevel,a.dwMaxHP,m.wExp FROM dbo.TMONSTERCHART m JOIN dbo.TMONATTRCHART a ON a.wID=m.wMonAttr AND a.bLevel=m.bLevel WHERE m.wID IN(15,241,243,251,252) ORDER BY m.wID;
ROLLBACK TRANSACTION;
IF OBJECT_ID(N'dbo.TGAMEFORGEMONSTERRESTORE',N'U') IS NOT NULL THROW 51000,'The restoration test did not fully roll back.',1;
IF (SELECT bLevel FROM dbo.TMONSTERCHART WHERE wID=243)<>80 THROW 51000,'The original Moray level was not restored by rollback.',1;
SELECT 'PASS: __COUNT__ level/HP corrections, idempotent rerun, original attributes/EXP preserved, full rollback' AS Verification;
GO
'@
$suffix = $suffix.Replace('__COUNT__', [string]$reference.corrections.Count)
($prefix + "`n" + $migration + "`n" + $migration + "`n" + $suffix) |
    docker exec -i $Container /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -d TGame_gsp -w 220'
if ($LASTEXITCODE -ne 0) { throw 'Monster restoration transaction verification failed.' }
