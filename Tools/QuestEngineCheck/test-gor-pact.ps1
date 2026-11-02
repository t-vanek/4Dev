[CmdletBinding()]
param([string]$Container='araz-mssql',[switch]$RunLifecycle)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$plan=Get-Content (Join-Path $repo 'Gameforge/gor-remaining-quests/pakt-plan.json') -Raw | ConvertFrom-Json
$fixtureDb='4DevQuestTest_pakt_'+[Guid]::NewGuid().ToString('N')
if($fixtureDb -notmatch '^4DevQuestTest_pakt_[a-f0-9]{32}$'){throw 'Invalid fixture name'}
function Sql([string]$Text,[switch]$Reject){
    $out=$Text | & docker exec -i $Container /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -W' 2>&1
    $failed=$LASTEXITCODE -ne 0
    if($Reject){if(-not $failed -or ($out -join "`n") -notmatch '51053'){throw "Expected import guard: $out"}}
    elseif($failed){$Text | Set-Content -LiteralPath (Join-Path $repo '.runtime/gor-remaining-20261031/failed-fixture.sql');throw ($out -join "`n")}
}
function Values($Rows){($Rows | ForEach-Object {'('+(($_.PSObject.Properties.Value | ForEach-Object {[int]$_}) -join ',')+')'}) -join ','}
$created=$false
try{
    Sql "IF DB_ID(N'$fixtureDb') IS NOT NULL THROW 51053,'Fixture exists',1; CREATE DATABASE [$fixtureDb];"
    $created=$true
    $tables=@('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART','TNPCCHART')
    Sql ("USE [$fixtureDb]; "+(($tables | ForEach-Object {"SELECT * INTO dbo.$_ FROM TGame_gsp.dbo.$_;"}) -join "`n")+' SELECT TOP(0) * INTO dbo.TQUESTTABLE FROM TGame_gsp.dbo.TQUESTTABLE; SELECT TOP(0) * INTO dbo.TQUESTTERMTABLE FROM TGame_gsp.dbo.TQUESTTERMTABLE;')
    $columns=$plan.before.headers[0].PSObject.Properties.Name
    $seed="USE [$fixtureDb]; DECLARE @h TABLE("+(($columns | ForEach-Object {"$_ int"}) -join ',')+'); INSERT @h VALUES '+(Values $plan.before.headers)+'; UPDATE q SET '+(($columns | Where-Object {$_ -ne 'dwQuestID'} | ForEach-Object {"$_=h.$_"}) -join ',')+' FROM dbo.TQUESTCHART q JOIN @h h ON h.dwQuestID=q.dwQuestID;'
    foreach($group in @(@('terms','TQUESTTERMCHART'),@('conditions','TQCONDITIONCHART'),@('rewards','TQREWARDCHART'))){
        $name=$group[0];$table=$group[1];$rows=@($plan.before.$name)
        $seed+=" DELETE dbo.$table WHERE dwQuestID IN(547,548,3200);"
        if($rows.Count){
            $cols=$rows[0].PSObject.Properties.Name
            $seed+=" DECLARE @$name TABLE("+(($cols | ForEach-Object {"$_ int"}) -join ',')+"); INSERT @$name VALUES "+(Values $rows)+';'
            $seed+=" DECLARE @base$name int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.$table); INSERT dbo.$table(dwID,"+($cols -join ',')+") SELECT @base$name+ROW_NUMBER() OVER(ORDER BY "+($cols -join ',')+'),'+($cols -join ',')+" FROM @$name;"
        }
    }
    Sql $seed
    $all=$tables+@('TQUESTTABLE','TQUESTTERMTABLE')
    Sql ("USE [$fixtureDb]; "+(($all | ForEach-Object {"SELECT * INTO dbo.Before_$_ FROM dbo.$_;"}) -join "`n")+' CREATE TABLE dbo.RejectedState(payload nvarchar(max));')
    $migration=(Get-Content (Join-Path $repo 'Database/migrations/053_gor_tripartite_pact.sql') -Raw).Replace('USE [TGame_gsp];',"USE [$fixtureDb];")
    $state='DECLARE @state nvarchar(max); SET @state=('+ $plan.snapshot_query.TrimEnd(';')+');'
    $check="USE [$fixtureDb]; $state IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'$($plan.after_hash)' THROW 51053,'Pakt semantics differ',1;"
    foreach($table in $all){
        $where=if($table -in @('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART')){' WHERE dwQuestID NOT IN(547,548,3200)'}else{''}
        $check+="`nIF EXISTS(SELECT * FROM dbo.$table$where EXCEPT SELECT * FROM dbo.Before_$table$where) OR EXISTS(SELECT * FROM dbo.Before_$table$where EXCEPT SELECT * FROM dbo.$table$where) THROW 51053,'Unrelated $table change',1;"
    }
    Sql $migration;Sql $check;Sql $migration;Sql $check
    $reset="USE [$fixtureDb]; "+(($all | ForEach-Object {"DELETE dbo.$_; INSERT dbo.$_ SELECT * FROM dbo.Before_$_;"}) -join "`n")
    $cases=@(
        @{Name='Unknown target identity';Change='UPDATE dbo.TQUESTCHART SET bLevel=20 WHERE dwQuestID=547;'},
        @{Name='Missing destination NPC';Change='DELETE dbo.TNPCCHART WHERE wID=26436;'},
        @{Name='Saved completed old mission';Change='INSERT dbo.TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(1,547,0,1,1);'},
        @{Name='Saved legacy term';Change='INSERT dbo.TQUESTTERMTABLE(dwCharID,dwQuestID,dwTermID,bTermType,bCount) VALUES(1,3200,1,3,1);'},
        @{Name='Active external legacy prerequisite';Change='UPDATE dbo.TQUESTCHART SET bType=8 WHERE dwQuestID=1203;'},
        @{Name='Unexpected execution child';Change='UPDATE dbo.TQUESTCHART SET bType=3,bTriggerType=1,dwTriggerID=548 WHERE dwQuestID=7;'},
        @{Name='Changed unused reward metadata';Change='UPDATE dbo.TQREWARDCHART SET dwQuestTime=37 WHERE dwQuestID=547;'}
    )
    $passed=@('Pinned pact with exact gold EXP soul and minimum level','Rerun idempotence','Legacy dependent quarantine and unrelated row preservation')
    foreach($case in $cases){
        Sql ($reset+$case.Change+" $state DELETE dbo.RejectedState; INSERT dbo.RejectedState VALUES(@state);")
        Sql $migration -Reject
        Sql "USE [$fixtureDb]; $state IF @state<>(SELECT payload FROM dbo.RejectedState) THROW 51053,'Rejected migration changed target data',1;"
        $passed+=$case.Name+' rejects atomically'
    }
    if($RunLifecycle){
        Sql $reset;Sql $migration;Sql $check
        Sql @"
USE [$fixtureDb];
DECLARE @module nvarchar(max);
DECLARE modules CURSOR LOCAL FAST_FORWARD FOR SELECT definition FROM TGame_gsp.sys.sql_modules WHERE object_id IN(SELECT object_id FROM TGame_gsp.sys.procedures WHERE name IN('TSaveQuest','TSaveQuestTerm'));
OPEN modules; FETCH NEXT FROM modules INTO @module;
WHILE @@FETCH_STATUS=0 BEGIN EXEC sys.sp_executesql @module; FETCH NEXT FROM modules INTO @module; END;
CLOSE modules; DEALLOCATE modules;
"@
        $previousConnection=$env:QUEST_TEST_DB
        try{
            $fixtureSecret=(& docker exec $Container /bin/bash -c 'printenv MSSQL_SA_PASSWORD').Trim()
            $env:QUEST_TEST_DB="Server=127.0.0.1,11433;Database=$fixtureDb;User ID=sa;Password=$fixtureSecret;TrustServerCertificate=True;Encrypt=False"
            & dotnet run --project (Join-Path $repo 'Tools/QuestEngineCheck') -c Release -- --gor-pact-lifecycle
            if($LASTEXITCODE -ne 0){throw 'Pact lifecycle check failed'}
        }
        finally{$env:QUEST_TEST_DB=$previousConnection;$fixtureSecret=$null}
    }
    [pscustomobject]@{Passed=$passed;PlayerRowsCopied=0;SourceDatabaseModified=$false} | ConvertTo-Json
}
finally{if($created){Sql "USE master; ALTER DATABASE [$fixtureDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$fixtureDb];"}}
