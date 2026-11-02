[CmdletBinding()]
param([string]$Container='araz-mssql')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$plan=Get-Content (Join-Path $repo 'Gameforge/gor-quest-activation/plan.json') -Raw | ConvertFrom-Json
$ids=($plan.after.headers | ForEach-Object { [int]$_.dwQuestID }) -join ','
$fixtureDb='4DevQuestTest_gorevents_'+[Guid]::NewGuid().ToString('N')
if ($fixtureDb -notmatch '^4DevQuestTest_gorevents_[a-f0-9]{32}$') { throw 'Invalid fixture database name.' }
function Invoke-FixtureSql([string]$Sql,[switch]$ExpectedFailure) {
    $output=$Sql | & docker exec -i $Container /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -W' 2>&1
    $failed=$LASTEXITCODE -ne 0
    if ($ExpectedFailure) {
        if (-not $failed -or ($output -join "`n") -notmatch '51052') { throw "Expected activation guard: $($output -join ' ')" }
    } elseif ($failed) { throw ($output -join "`n") }
}
function ValuesSql($Rows) {
    ($Rows | ForEach-Object { '('+(($_.PSObject.Properties.Value | ForEach-Object { [int]$_ }) -join ',')+')' }) -join ','
}
$created=$false
try {
    Invoke-FixtureSql "IF DB_ID(N'$fixtureDb') IS NOT NULL THROW 51052,'Fixture exists',1; CREATE DATABASE [$fixtureDb];"
    $created=$true
    $tables=@('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART','TNPCCHART')
    Invoke-FixtureSql ("USE [$fixtureDb]; "+(($tables | ForEach-Object { "SELECT * INTO dbo.$_ FROM TGame_gsp.dbo.$_;" }) -join "`n")+
        ' SELECT wItemID INTO dbo.TITEMCHART FROM TGame_gsp.dbo.TITEMCHART; SELECT TOP(0) * INTO dbo.TQUESTTABLE FROM TGame_gsp.dbo.TQUESTTABLE;')
    $headerColumns=$plan.before.headers[0].PSObject.Properties.Name
    $seed="USE [$fixtureDb]; DECLARE @h TABLE("+(($headerColumns | ForEach-Object { "$_ int" }) -join ',')+'); INSERT @h VALUES '+(ValuesSql $plan.before.headers)+'; UPDATE q SET '+(($headerColumns | Where-Object { $_ -ne 'dwQuestID' } | ForEach-Object { "$_=h.$_" }) -join ',')+' FROM dbo.TQUESTCHART q JOIN @h h ON h.dwQuestID=q.dwQuestID;'
    foreach ($group in @(@('terms','TQUESTTERMCHART'),@('conditions','TQCONDITIONCHART'),@('rewards','TQREWARDCHART'))) {
        $rows=@($plan.before.($group[0])); $table=$group[1]
        $seed+=" DELETE dbo.$table WHERE dwQuestID IN($ids);"
        if ($rows.Count) {
            $cols=$rows[0].PSObject.Properties.Name
            $seed+=' DECLARE @seed'+$group[0]+' TABLE('+(($cols | ForEach-Object { "$_ int" }) -join ',')+'); INSERT @seed'+$group[0]+' VALUES '+(ValuesSql $rows)+';'
            $seed+=" DECLARE @base$($group[0]) int=(SELECT MAX(dwID) FROM dbo.$table); INSERT dbo.$table(dwID,"+($cols -join ',')+") SELECT @base$($group[0])+ROW_NUMBER() OVER(ORDER BY "+($cols -join ',')+'),'+($cols -join ',')+' FROM @seed'+$group[0]+';'
        }
    }
    Invoke-FixtureSql $seed
    $allTables=$tables+@('TITEMCHART','TQUESTTABLE')
    Invoke-FixtureSql ("USE [$fixtureDb]; "+(($allTables | ForEach-Object { "SELECT * INTO dbo.Before_$_ FROM dbo.$_;" }) -join "`n"))
    $migration=(Get-Content (Join-Path $repo 'Database/migrations/052_gor_event_missions.sql') -Raw).Replace('USE [TGame_gsp];',"USE [$fixtureDb];")
    $state='DECLARE @state nvarchar(max); SET @state=('+ $plan.snapshot_query.Trim().TrimEnd(';') +');'
    $assert="USE [$fixtureDb]; $state IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'$($plan.after_hash)' THROW 51052,'Restored semantics differ from pinned plan',1;"
    foreach ($table in $allTables) {
        $where=if ($table -in @('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART')) { " WHERE dwQuestID NOT IN($ids)" } else { '' }
        $assert+=" IF EXISTS(SELECT * FROM dbo.$table$where EXCEPT SELECT * FROM dbo.Before_$table$where) OR EXISTS(SELECT * FROM dbo.Before_$table$where EXCEPT SELECT * FROM dbo.$table$where) THROW 51052,'Unrelated $table change',1;"
    }
    Invoke-FixtureSql $migration; Invoke-FixtureSql $assert
    Invoke-FixtureSql $migration; Invoke-FixtureSql $assert
    $reset="USE [$fixtureDb]; "+(($allTables | ForEach-Object { "DELETE dbo.$_; INSERT dbo.$_ SELECT * FROM dbo.Before_$_;" }) -join "`n")
    $cases=@(
        @{Name='Changed target identity';Sql='UPDATE dbo.TQUESTCHART SET bLevel=9 WHERE dwQuestID=233;'},
        @{Name='Missing NPC';Sql='DELETE dbo.TNPCCHART WHERE wID=26567;'},
        @{Name='Missing item';Sql='DELETE dbo.TITEMCHART WHERE wItemID=18061;'},
        @{Name='Running obsolete quest';Sql='INSERT dbo.TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(1,362,0,0,1);'},
        @{Name='Active reused old mission';Sql='UPDATE dbo.TQUESTCHART SET bType=8 WHERE dwQuestID=362;'},
        @{Name='Unexpected legacy execution child';Sql='UPDATE dbo.TQUESTCHART SET bType=3,bTriggerType=1,dwTriggerID=233 WHERE dwQuestID=7;'}
    )
    $passed=@('Six missions and six completions match pinned semantics','Rerun idempotent','Unrelated charts and synthetic player state retained')
    foreach ($case in $cases) {
        Invoke-FixtureSql ($reset+$case.Sql)
        Invoke-FixtureSql $migration -ExpectedFailure
        Invoke-FixtureSql "USE [$fixtureDb]; $state IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)='$($plan.after_hash)' THROW 51052,'Rejected activation unexpectedly completed',1;"
        $passed+=$case.Name+' rejects atomically'
    }
    Invoke-FixtureSql $reset; Invoke-FixtureSql $migration; Invoke-FixtureSql $assert
    [pscustomobject]@{Passed=$passed;ActivatedMissions=6;RestoredCompletions=6;PlayerRowsCopied=0;SourceDatabaseModified=$false} | ConvertTo-Json
}
finally {
    if ($created) { Invoke-FixtureSql "USE master; ALTER DATABASE [$fixtureDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$fixtureDb];" }
}
