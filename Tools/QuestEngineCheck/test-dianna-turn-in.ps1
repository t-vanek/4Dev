[CmdletBinding()]
param([string]$Container = 'araz-mssql')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureDb = '4DevQuestTest_dianna_' + [Guid]::NewGuid().ToString('N')
if ($fixtureDb -notmatch '^4DevQuestTest_dianna_[a-f0-9]{32}$') { throw 'Invalid disposable database name.' }
function Invoke-FixtureSql([string]$Sql, [switch]$ExpectedFailure) {
    $output = $Sql | & docker exec -i $Container /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -W' 2>&1
    $failed = $LASTEXITCODE -ne 0
    if ($ExpectedFailure -and -not $failed) { throw 'Conflicting fixture unexpectedly accepted migration.' }
    if (-not $ExpectedFailure -and $failed) { throw ($output -join "`n") }
}
$created = $false
try {
    Invoke-FixtureSql "IF DB_ID(N'$fixtureDb') IS NOT NULL THROW 51049,'Fixture exists',1; CREATE DATABASE [$fixtureDb];"
    $created = $true
    Invoke-FixtureSql @"
USE [$fixtureDb];
SELECT * INTO dbo.TQUESTCHART FROM TGame_gsp.dbo.TQUESTCHART WHERE dwQuestID BETWEEN 8003 AND 8011;
SELECT * INTO dbo.TQUESTTERMCHART FROM TGame_gsp.dbo.TQUESTTERMCHART WHERE dwQuestID BETWEEN 8003 AND 8011;
SELECT * INTO dbo.TNPCCHART FROM TGame_gsp.dbo.TNPCCHART WHERE wID IN(20004,20012);
UPDATE dbo.TQUESTCHART SET dwTriggerID=20004 WHERE dwQuestID=8007;
SELECT * INTO dbo.ExpectedQuests FROM dbo.TQUESTCHART;
UPDATE dbo.ExpectedQuests SET dwTriggerID=20012 WHERE dwQuestID=8007;
SELECT * INTO dbo.ExpectedTerms FROM dbo.TQUESTTERMCHART;
"@
    $migration = (Get-Content (Join-Path $repo 'Database/migrations/049_dianna_quest_turn_in.sql') -Raw).Replace('USE [TGame_gsp];', "USE [$fixtureDb];")
    Invoke-FixtureSql $migration
    Invoke-FixtureSql $migration
    Invoke-FixtureSql @"
USE [$fixtureDb];
IF EXISTS(SELECT * FROM dbo.TQUESTCHART EXCEPT SELECT * FROM dbo.ExpectedQuests)
 OR EXISTS(SELECT * FROM dbo.ExpectedQuests EXCEPT SELECT * FROM dbo.TQUESTCHART)
 OR EXISTS(SELECT * FROM dbo.TQUESTTERMCHART EXCEPT SELECT * FROM dbo.ExpectedTerms)
 OR EXISTS(SELECT * FROM dbo.ExpectedTerms EXCEPT SELECT * FROM dbo.TQUESTTERMCHART)
 THROW 51049,'Unexpected change or missing repair',1;
UPDATE dbo.TQUESTCHART SET dwTriggerID=29999 WHERE dwQuestID=8007;
"@
    Invoke-FixtureSql $migration -ExpectedFailure
    Invoke-FixtureSql "USE [$fixtureDb]; IF NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE dwQuestID=8007 AND dwTriggerID=29999) THROW 51049,'Conflict was overwritten',1; UPDATE dbo.TQUESTCHART SET dwTriggerID=20004 WHERE dwQuestID=8007; UPDATE dbo.TQUESTTERMCHART SET dwTermID=8003 WHERE dwQuestID=8007;"
    Invoke-FixtureSql $migration -ExpectedFailure
    Invoke-FixtureSql "USE [$fixtureDb]; IF NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE dwQuestID=8007 AND dwTriggerID=20004) THROW 51049,'Failed term guard partially changed quest',1; UPDATE dbo.TQUESTTERMCHART SET dwTermID=8006 WHERE dwQuestID=8007; DELETE dbo.TNPCCHART WHERE wID=20012;"
    Invoke-FixtureSql $migration -ExpectedFailure
    Invoke-FixtureSql "USE [$fixtureDb]; IF NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE dwQuestID=8007 AND dwTriggerID=20004) THROW 51049,'Failed NPC guard partially changed quest',1;"
    [pscustomobject]@{ Passed = @('NPC repair only','Idempotent second application','Unknown NPC rejected','Changed objective rejected atomically','Missing Karl rejected atomically'); PlayerRowsCopied = 0; SourceDatabaseModified = $false } | ConvertTo-Json
}
finally {
    if ($created) { Invoke-FixtureSql "USE master; ALTER DATABASE [$fixtureDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$fixtureDb];" }
}
