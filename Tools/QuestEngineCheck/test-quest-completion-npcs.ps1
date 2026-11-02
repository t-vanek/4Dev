[CmdletBinding()]
param([string]$Container = 'araz-mssql')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$report = Get-Content (Join-Path $repo 'Gameforge/quest-turn-in-npcs/audit-before.json') -Raw | ConvertFrom-Json
$changes = @($report.rows | Where-Object status -eq 'verified_npc_mismatch')
if ($changes.Count -ne 30) { throw 'Expected the frozen 30-row repair plan.' }
$values = ($changes | ForEach-Object { "($([int]$_.completion_id),$([int]$_.current_npc),$([int]$_.new_npc))" }) -join ','
$plan = "DECLARE @plan TABLE(Id int PRIMARY KEY,OldNpc int,NewNpc int); INSERT @plan VALUES $values;"
$fixtureDb = '4DevQuestTest_turnin_' + [Guid]::NewGuid().ToString('N')
if ($fixtureDb -notmatch '^4DevQuestTest_turnin_[a-f0-9]{32}$') { throw 'Invalid disposable database name.' }
function Invoke-FixtureSql([string]$Sql, [switch]$ExpectedFailure) {
    $output = $Sql | & docker exec -i $Container /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -W' 2>&1
    $failed = $LASTEXITCODE -ne 0
    if ($ExpectedFailure) {
        if (-not $failed -or ($output -join "`n") -notmatch '51050') { throw "Expected the migration's specific guard failure: $($output -join ' ')" }
    } elseif ($failed) { throw ($output -join "`n") }
}
$created = $false
try {
    Invoke-FixtureSql "IF DB_ID(N'$fixtureDb') IS NOT NULL THROW 51050,'Fixture exists',1; CREATE DATABASE [$fixtureDb];"
    $created = $true
    # Only static chart rows; never copy accounts, character rows or their state.
    $copy = "USE [$fixtureDb]; " + ((@('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TNPCCHART','TQREWARDCHART') | ForEach-Object {
        "SELECT * INTO dbo.$_ FROM TGame_gsp.dbo.$_; SELECT * INTO dbo.Expected_$_ FROM dbo.$_;"
    }) -join "`n")
    Invoke-FixtureSql $copy
    Invoke-FixtureSql "USE [$fixtureDb]; $plan UPDATE q SET dwTriggerID=p.OldNpc FROM dbo.TQUESTCHART q JOIN @plan p ON p.Id=q.dwQuestID; UPDATE q SET dwTriggerID=p.NewNpc FROM dbo.Expected_TQUESTCHART q JOIN @plan p ON p.Id=q.dwQuestID;"
    $migration = (Get-Content (Join-Path $repo 'Database/migrations/050_quest_completion_npcs.sql') -Raw).Replace('USE [TGame_gsp];', "USE [$fixtureDb];")
    Invoke-FixtureSql $migration
    $assert = "USE [$fixtureDb]; " + ((@('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TNPCCHART','TQREWARDCHART') | ForEach-Object {
        "IF EXISTS(SELECT * FROM dbo.$_ EXCEPT SELECT * FROM dbo.Expected_$_) OR EXISTS(SELECT * FROM dbo.Expected_$_ EXCEPT SELECT * FROM dbo.$_) THROW 51050,'Unexpected change to $_',1;"
    }) -join "`n")
    Invoke-FixtureSql $assert
    Invoke-FixtureSql $migration
    Invoke-FixtureSql $assert
    $first = $changes[0]
    $id = [int]$first.completion_id
    $mid = [int]$first.mission_id
    $npc = [int]$first.new_npc
    $old = [int]$first.current_npc
    $cases = @(
        @{ Name='Unknown current NPC'; Mutate="UPDATE dbo.TQUESTCHART SET dwTriggerID=29999 WHERE dwQuestID=$id;"; Restore="UPDATE dbo.TQUESTCHART SET dwTriggerID=$old WHERE dwQuestID=$id;"; BadId=$id },
        @{ Name='Changed mission objective'; Mutate="UPDATE dbo.TQUESTTERMCHART SET dwTermID=29999 WHERE dwID=(SELECT MIN(dwID) FROM dbo.TQUESTTERMCHART WHERE dwQuestID=$mid);"; Restore="UPDATE t SET dwTermID=e.dwTermID FROM dbo.TQUESTTERMCHART t JOIN dbo.Expected_TQUESTTERMCHART e ON e.dwID=t.dwID;" },
        @{ Name='Changed completion condition'; Mutate="UPDATE dbo.TQCONDITIONCHART SET bCount=1 WHERE dwQuestID=$id;"; Restore="UPDATE dbo.TQCONDITIONCHART SET bCount=0 WHERE dwQuestID=$id;" },
        @{ Name='Target NPC on wrong map'; Mutate="UPDATE dbo.TNPCCHART SET wMapID=32767 WHERE wID=$npc;"; Restore="UPDATE t SET wMapID=e.wMapID FROM dbo.TNPCCHART t JOIN dbo.Expected_TNPCCHART e ON e.wID=t.wID;" },
        @{ Name='Changed completion header'; Mutate="UPDATE dbo.TQUESTCHART SET bType=0 WHERE dwQuestID=$id;"; Restore="UPDATE dbo.TQUESTCHART SET bType=7 WHERE dwQuestID=$id;" }
    )
    $passed = @('All 30 NPC assignments repaired','Second application idempotent','All other fields and all four unrelated charts retained')
    foreach ($case in $cases) {
        Invoke-FixtureSql "USE [$fixtureDb]; $plan UPDATE q SET dwTriggerID=p.OldNpc FROM dbo.TQUESTCHART q JOIN @plan p ON p.Id=q.dwQuestID; $($case.Mutate)"
        Invoke-FixtureSql $migration -ExpectedFailure
        $except = if ($case.BadId) { "AND p.Id<>$([int]$case.BadId)" } else { '' }
        Invoke-FixtureSql "USE [$fixtureDb]; $plan IF EXISTS(SELECT 1 FROM @plan p JOIN dbo.TQUESTCHART q ON q.dwQuestID=p.Id WHERE q.dwTriggerID<>p.OldNpc $except) THROW 51050,'Rejected migration partially applied NPC changes',1; $($case.Restore)"
        $passed += $case.Name + ' rejects atomically'
    }
    Invoke-FixtureSql $migration
    Invoke-FixtureSql $assert
    [pscustomobject]@{ Passed=$passed; ChangedNpcRows=30; PlayerRowsCopied=0; SourceDatabaseModified=$false } | ConvertTo-Json
}
finally {
    if ($created) { Invoke-FixtureSql "USE master; ALTER DATABASE [$fixtureDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$fixtureDb];" }
}
