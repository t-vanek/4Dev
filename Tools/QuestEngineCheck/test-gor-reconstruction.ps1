[CmdletBinding()]
param([string]$Container='araz-mssql',[switch]$RunLifecycle)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$plan=Get-Content (Join-Path $repo 'Gameforge/gor-remaining-quests/reconstruction-plan.json') -Raw | ConvertFrom-Json
$fixtureDb='4DevQuestTest_gor_'+[Guid]::NewGuid().ToString('N')
$ids=($plan.after.headers.dwQuestID -join ',')
function Sql([string]$Text,[switch]$Reject){
    $out=$Text | & docker exec -i $Container /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -W' 2>&1
    $failed=$LASTEXITCODE -ne 0
    if($Reject){if(-not $failed -or ($out -join "`n") -notmatch '51054'){throw "Expected import guard: $out"}}
    elseif($failed){throw ($out -join "`n")}
}
function Values($Rows){($Rows | ForEach-Object {'('+(($_.PSObject.Properties.Value | ForEach-Object {[string]$_}) -join ',')+')'}) -join ",`n"}
$created=$false
try{
    Sql "CREATE DATABASE [$fixtureDb];";$created=$true
    $charts=@('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART','TNPCCHART','TITEMCHART','TARENACHART','TMONSTERCHART','TMONATTRCHART','TMONSPAWNCHART','TMAPMONCHART')
    $empty=@('TQUESTTABLE','TQUESTTERMTABLE','TITEMTABLE','TTEMPITEMTABLE','TINVENTABLE','TTEMPINVENTABLE')
    Sql ("USE [$fixtureDb]; "+(($charts | ForEach-Object {"SELECT * INTO dbo.$_ FROM TGame_gsp.dbo.$_;"}) -join "`n")+"`n"+(($empty | ForEach-Object {"SELECT TOP(0) * INTO dbo.$_ FROM TGame_gsp.dbo.$_;"}) -join "`n"))
    Sql "USE [$fixtureDb]; IF COL_LENGTH('dbo.TARENACHART','bQuestMatch') IS NULL EXEC('ALTER TABLE dbo.TARENACHART ADD bQuestMatch bit NOT NULL DEFAULT(0)');"
    $seed="USE [$fixtureDb]; DELETE dbo.TQUESTCHART WHERE dwQuestID IN($ids);"
    $cols=$plan.before.headers[0].PSObject.Properties.Name
    $seed+="`nINSERT dbo.TQUESTCHART("+($cols -join ',')+") VALUES`n"+(Values $plan.before.headers)+';'
    foreach($group in @(@('terms','TQUESTTERMCHART'),@('conditions','TQCONDITIONCHART'),@('rewards','TQREWARDCHART'))){
        $name=$group[0];$table=$group[1];$rows=@($plan.before.$name);$cols=$rows[0].PSObject.Properties.Name
        $seed+="`nDELETE dbo.$table WHERE dwQuestID IN($ids);`nDECLARE @$name TABLE("+(($cols | ForEach-Object {"$_ int"}) -join ',')+"); INSERT @$name VALUES`n"+(Values $rows)+';'
        $seed+="`nDECLARE @base$name int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.$table); INSERT dbo.$table(dwID,"+($cols -join ',')+") SELECT @base$name+ROW_NUMBER() OVER(ORDER BY "+($cols -join ',')+'),'+($cols -join ',')+" FROM @$name;"
    }
    $seed+="`nUPDATE dbo.TARENACHART SET bType=CASE wID WHEN 1 THEN 3 ELSE 7 END,bQuestMatch=0; DELETE dbo.TMONSPAWNCHART WHERE wID BETWEEN 32398 AND 32403; DELETE dbo.TMAPMONCHART WHERE wSpawnID BETWEEN 32398 AND 32403;"
    Sql $seed
    $all=$charts+$empty
    Sql ("USE [$fixtureDb]; "+(($all | ForEach-Object {"SELECT * INTO dbo.Before_$_ FROM dbo.$_;"}) -join "`n")+' CREATE TABLE dbo.RejectedState(payload nvarchar(max));')
    $migration=(Get-Content (Join-Path $repo 'Database/migrations/054_gor_reconstructed_missions.sql') -Raw).Replace('USE [TGame_gsp];',"USE [$fixtureDb];")
    $state='DECLARE @state nvarchar(max); SET @state=('+ $plan.snapshot_query.TrimEnd(';')+');'
    $check="USE [$fixtureDb]; $state IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'$($plan.after_hash)' THROW 51054,'Quest semantics differ',1;"
    foreach($table in $all){
        $where=switch($table){
            {$_ -in @('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART')} {" WHERE dwQuestID NOT IN($ids)"}
            'TARENACHART' {' WHERE wID NOT IN(1,2)'}
            'TMONSPAWNCHART' {' WHERE wID NOT BETWEEN 32398 AND 32403'}
            'TMAPMONCHART' {' WHERE wSpawnID NOT BETWEEN 32398 AND 32403'}
            default {''}
        }
        $check+="`nIF EXISTS(SELECT * FROM dbo.$table$where EXCEPT SELECT * FROM dbo.Before_$table$where) OR EXISTS(SELECT * FROM dbo.Before_$table$where EXCEPT SELECT * FROM dbo.$table$where) THROW 51054,'Unrelated $table change',1;"
    }
    Sql $migration;Sql $check;Sql $migration;Sql $check
    $reset="USE [$fixtureDb]; "+(($all | ForEach-Object {"DELETE dbo.$_; INSERT dbo.$_ SELECT * FROM dbo.Before_$_;"}) -join "`n")
    $cases=@(
        @{Name='Unknown quest identity';Change='UPDATE dbo.TQUESTCHART SET bLevel=20 WHERE dwQuestID=1082;'},
        @{Name='Missing turn-in NPC';Change='DELETE dbo.TNPCCHART WHERE wID=26427;'},
        @{Name='Missing seal template';Change='DELETE dbo.TITEMCHART WHERE wItemID=9737;'},
        @{Name='Missing monster template';Change='DELETE dbo.TMONSTERCHART WHERE wID=3504;'},
        @{Name='Missing monster attributes';Change='DELETE dbo.TMONATTRCHART WHERE wID=30071;'},
        @{Name='Changed arena fee';Change='UPDATE dbo.TARENACHART SET dwFee=1 WHERE wID=2;'},
        @{Name='Saved obsolete completion';Change='INSERT dbo.TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(1,3212,0,1,1);'},
        @{Name='Saved legacy term';Change='INSERT dbo.TQUESTTERMTABLE(dwCharID,dwQuestID,dwTermID,bTermType,bCount) VALUES(1,1082,1,3,1);'},
        @{Name='Active outside dependency';Change='UPDATE dbo.TQUESTCHART SET bType=8,dwParentID=3210 WHERE dwQuestID=7;'},
        @{Name='Changed unused reward metadata';Change='UPDATE dbo.TQREWARDCHART SET dwQuestTime=37 WHERE dwQuestID=1082;'},
        @{Name='Occupied spawn ID';Change='UPDATE dbo.TMONSPAWNCHART SET wID=32398 WHERE wID=(SELECT MIN(wID) FROM dbo.TMONSPAWNCHART);'}
    )
    $passed=@('Exact pinned objectives rewards NPCs and level bounds','Idempotent import','Six approved spawn positions count delay and probabilities','Only arenas 1 and 2 opt in','Unrelated charts and all player rows preserved')
    foreach($case in $cases){
        Sql ($reset+"`n"+$case.Change+"`n$state DELETE dbo.RejectedState; INSERT dbo.RejectedState VALUES(@state);")
        try{Sql $migration -Reject}catch{throw "$($case.Name): $_"}
        Sql "USE [$fixtureDb]; $state IF @state<>(SELECT payload FROM dbo.RejectedState) THROW 51054,'Rejected migration changed quest data',1;"
        $passed+=$case.Name+' rejects atomically'
    }
    if($RunLifecycle){
        Sql $reset;Sql $migration;Sql $check
        Sql @"
USE [$fixtureDb];
DECLARE @module nvarchar(max);
DECLARE modules CURSOR LOCAL FAST_FORWARD FOR SELECT definition FROM TGame_gsp.sys.sql_modules WHERE object_id IN(SELECT object_id FROM TGame_gsp.sys.procedures WHERE name IN('TSaveQuest','TSaveQuestTerm','TSaveItemDataStart','TSaveItemDataEnd','TSaveInven','TSaveItem'));
OPEN modules; FETCH NEXT FROM modules INTO @module;
WHILE @@FETCH_STATUS=0 BEGIN EXEC sys.sp_executesql @module; FETCH NEXT FROM modules INTO @module; END;
CLOSE modules; DEALLOCATE modules;
"@
        $previousConnection=$env:QUEST_TEST_DB
        try{
            $fixtureSecret=(& docker exec $Container /bin/bash -c 'printenv MSSQL_SA_PASSWORD').Trim()
            $env:QUEST_TEST_DB="Server=127.0.0.1,11433;Database=$fixtureDb;User ID=sa;Password=$fixtureSecret;TrustServerCertificate=True;Encrypt=False"
            & dotnet run --project (Join-Path $repo 'Tools/QuestEngineCheck') -c Release -- --gor-reconstruction-lifecycle
            if($LASTEXITCODE -ne 0){throw 'Reconstruction lifecycle check failed'}
        }finally{$env:QUEST_TEST_DB=$previousConnection;$fixtureSecret=$null}
    }
    # A rerun after real new progress must preserve it; only the pre-import identity replacement rejects legacy saves.
    foreach($table in $empty){Sql "USE [$fixtureDb]; SELECT * INTO dbo.AfterPlay_$table FROM dbo.$table;"}
    Sql $migration
    foreach($table in $empty){Sql "USE [$fixtureDb]; IF EXISTS(SELECT * FROM dbo.$table EXCEPT SELECT * FROM dbo.AfterPlay_$table) OR EXISTS(SELECT * FROM dbo.AfterPlay_$table EXCEPT SELECT * FROM dbo.$table) THROW 51054,'Rerun changed new player progress',1;"}
    [pscustomobject]@{Passed=$passed;RerunAfterNewProgressPreserved=$true;PlayerRowsCopied=0;SourceDatabaseModified=$false} | ConvertTo-Json
}finally{if($created){Sql "USE master; ALTER DATABASE [$fixtureDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$fixtureDb];"}}
