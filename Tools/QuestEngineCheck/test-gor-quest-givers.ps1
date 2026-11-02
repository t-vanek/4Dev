[CmdletBinding()]
param([string]$Container='araz-mssql')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureDb='4DevQuestTest_gor_' + [Guid]::NewGuid().ToString('N')
if ($fixtureDb -notmatch '^4DevQuestTest_gor_[a-f0-9]{32}$') { throw 'Invalid disposable database name.' }
function Invoke-FixtureSql([string]$Sql,[switch]$ExpectedFailure) {
    $output=$Sql | & docker exec -i $Container /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -W' 2>&1
    $failed=$LASTEXITCODE -ne 0
    if ($ExpectedFailure) {
        if (-not $failed -or ($output -join "`n") -notmatch '51051') { throw "Expected migration 051 guard: $($output -join ' ')" }
    } elseif ($failed) { throw ($output -join "`n") }
}
$created=$false
try {
    Invoke-FixtureSql "IF DB_ID(N'$fixtureDb') IS NOT NULL THROW 51051,'Fixture exists',1; CREATE DATABASE [$fixtureDb];"
    $created=$true
    $tables=@('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART','TNPCCHART')
    Invoke-FixtureSql ("USE [$fixtureDb]; " + (($tables | ForEach-Object { "SELECT * INTO dbo.$_ FROM TGame_gsp.dbo.$_; SELECT * INTO dbo.Expected_$_ FROM dbo.$_;" }) -join "`n"))
    Invoke-FixtureSql "USE [$fixtureDb]; UPDATE dbo.TQUESTCHART SET dwTriggerID=26306 WHERE dwQuestID IN(8883,8885,8887); UPDATE dbo.Expected_TQUESTCHART SET dwTriggerID=22919 WHERE dwQuestID IN(8883,8885,8887);"
    $migration=(Get-Content (Join-Path $repo 'Database/migrations/051_gor_paper_quest_givers.sql') -Raw).Replace('USE [TGame_gsp];',"USE [$fixtureDb];")
    $assert="USE [$fixtureDb]; " + (($tables | ForEach-Object { "IF EXISTS(SELECT * FROM dbo.$_ EXCEPT SELECT * FROM dbo.Expected_$_) OR EXISTS(SELECT * FROM dbo.Expected_$_ EXCEPT SELECT * FROM dbo.$_) THROW 51051,'Unexpected $_ change',1;" }) -join "`n")
    Invoke-FixtureSql $migration; Invoke-FixtureSql $assert
    Invoke-FixtureSql $migration; Invoke-FixtureSql $assert
    $passed=@('All three Gor giver assignments repaired','Rerun idempotent','Rewards/objectives/conditions/NPCs and unrelated quest fields retained')
    $cases=@(
        @{Name='Unknown giver';Mutate='UPDATE dbo.TQUESTCHART SET dwTriggerID=29999 WHERE dwQuestID=8883;';Restore='UPDATE dbo.TQUESTCHART SET dwTriggerID=26306 WHERE dwQuestID=8883;';Except='AND dwQuestID<>8883'},
        @{Name='Changed fetch count';Mutate='UPDATE dbo.TQUESTTERMCHART SET bCount=21 WHERE dwQuestID=8885;';Restore='UPDATE dbo.TQUESTTERMCHART SET bCount=20 WHERE dwQuestID=8885;'},
        @{Name='Changed reward';Mutate='UPDATE dbo.TQREWARDCHART SET dwRewardID=18131 WHERE dwQuestID=8887;';Restore='UPDATE dbo.TQREWARDCHART SET dwRewardID=18128 WHERE dwQuestID=8887;'},
        @{Name='Wrong NPC map';Mutate='UPDATE dbo.TNPCCHART SET wMapID=32767 WHERE wID=22919;';Restore='UPDATE dbo.TNPCCHART SET wMapID=0 WHERE wID=22919;'}
    )
    foreach ($case in $cases) {
        Invoke-FixtureSql "USE [$fixtureDb]; UPDATE dbo.TQUESTCHART SET dwTriggerID=26306 WHERE dwQuestID IN(8883,8885,8887); $($case.Mutate)"
        Invoke-FixtureSql $migration -ExpectedFailure
        Invoke-FixtureSql "USE [$fixtureDb]; IF EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE dwQuestID IN(8883,8885,8887) AND dwTriggerID<>26306 $($case.Except)) THROW 51051,'Partial giver update survived rejection',1; $($case.Restore)"
        $passed+=$case.Name+' rejects atomically'
    }
    Invoke-FixtureSql $migration; Invoke-FixtureSql $assert
    [pscustomobject]@{Passed=$passed;ChangedGiverRows=3;PlayerRowsCopied=0;SourceDatabaseModified=$false} | ConvertTo-Json
}
finally {
    if ($created) { Invoke-FixtureSql "USE master; ALTER DATABASE [$fixtureDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$fixtureDb];" }
}
