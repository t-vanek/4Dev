[CmdletBinding()]
param([string]$Container='araz-mssql',[switch]$RunLifecycle,[switch]$WakeUp)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$planFile=if($WakeUp){'Gameforge/gor-content-repairs/wakeup-plan.json'}else{'Gameforge/gor-content-repairs/plan.json'}
$migrationFile=if($WakeUp){'Database/migrations/056_gor_wakeup_quest.sql'}else{'Database/migrations/055_gor_content_repairs.sql'}
$errorId=if($WakeUp){51056}else{51055}
$plan=Get-Content (Join-Path $repo $planFile) -Raw | ConvertFrom-Json
$fixtureDb='4DevQuestTest_content_'+[Guid]::NewGuid().ToString('N');$ids=$plan.after.headers.dwQuestID -join ','
function Sql([string]$Text,[switch]$Reject){
    $out=$Text | docker exec -i $Container /bin/bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -W' 2>&1
    $failed=$LASTEXITCODE -ne 0
    if($Reject){if(-not $failed -or ($out -join "`n") -notmatch [string]$errorId){throw "Expected import rejection: $out"}}
    elseif($failed){throw ($out -join "`n")}
}
function Values($Rows){($Rows | ForEach-Object {'('+(($_.PSObject.Properties.Value | ForEach-Object {[string]$_}) -join ',')+')'}) -join ",`n"}
$created=$false
try{
    Sql "CREATE DATABASE [$fixtureDb];";$created=$true
    $charts=@('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART','TNPCCHART','TITEMCHART','TSWITCHCHART')
    $empty=@('TQUESTTABLE','TQUESTTERMTABLE','TITEMTABLE','TINVENTABLE','TTEMPITEMTABLE','TTEMPINVENTABLE')
    Sql ("USE [$fixtureDb]; SET NOCOUNT ON;`n"+(($charts|ForEach-Object {"SELECT * INTO dbo.$_ FROM TGame_gsp.dbo.$_;"}) -join "`n")+"`n"+(($empty|ForEach-Object {"SELECT TOP(0) * INTO dbo.$_ FROM TGame_gsp.dbo.$_;"}) -join "`n")+"`nCREATE TABLE dbo.TCharacterSaveQueue(FixtureId int NOT NULL); CREATE TABLE dbo.RejectedState(payload nvarchar(max));")
    $cols=$plan.before.headers[0].PSObject.Properties.Name
    $seed="USE [$fixtureDb]; DELETE dbo.TQUESTCHART WHERE dwQuestID IN($ids);`nINSERT dbo.TQUESTCHART("+($cols -join ',')+") VALUES`n"+(Values $plan.before.headers)+';'
    foreach($g in @(@('terms','TQUESTTERMCHART'),@('conditions','TQCONDITIONCHART'),@('rewards','TQREWARDCHART'))){
        $name=$g[0];$table=$g[1];$rows=@($plan.before.$name);$cols=$rows[0].PSObject.Properties.Name
        $seed+="`nDELETE dbo.$table WHERE dwQuestID IN($ids);"
        if(-not $rows.Count){continue}
        $seed+="`nDECLARE @$name TABLE("+(($cols|ForEach-Object {"$_ int"}) -join ',')+"); INSERT @$name VALUES`n"+(Values $rows)+';'
        $seed+="`nDECLARE @base$name int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.$table); INSERT dbo.$table(dwID,"+($cols -join ',')+") SELECT @base$name+ROW_NUMBER() OVER(ORDER BY "+($cols -join ',')+'),'+($cols -join ',')+" FROM @$name;"
    }
    $seed+="`nINSERT dbo.TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(999,8937,0,1,1);"
    Sql $seed
    $all=$charts+$empty+@('TCharacterSaveQueue')
    Sql ("USE [$fixtureDb]; "+(($all|ForEach-Object {"SELECT * INTO dbo.Before_$_ FROM dbo.$_;"}) -join "`n"))
    $migration=(Get-Content (Join-Path $repo $migrationFile) -Raw).Replace('USE [TGame_gsp];',"USE [$fixtureDb];")
    $state='DECLARE @state nvarchar(max); SET @state=('+ $plan.snapshot_query.TrimEnd(';')+');'
    $check="USE [$fixtureDb]; $state IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'$($plan.after_hash)' THROW 51055,'Content differs',1;"
    foreach($table in $all){
        $where=if($table -in @('TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART')){" WHERE dwQuestID NOT IN($ids)"}else{''}
        $check+="`nIF EXISTS(SELECT * FROM dbo.$table$where EXCEPT SELECT * FROM dbo.Before_$table$where) OR EXISTS(SELECT * FROM dbo.Before_$table$where EXCEPT SELECT * FROM dbo.$table$where) THROW 51055,'Unrelated $table changed',1;"
    }
    Sql $migration;Sql $check;Sql $migration;Sql $check
    $reset="USE [$fixtureDb]; "+(($all|ForEach-Object {"DELETE dbo.$_; INSERT dbo.$_ SELECT * FROM dbo.Before_$_;"}) -join "`n")
    $cases=@(
        @{Name='Changed recipe eligibility';Change='UPDATE dbo.TQCONDITIONCHART SET dwConditionID=123 WHERE dwQuestID=5873;'},
        @{Name='Changed recipe cap';Change='UPDATE dbo.TQUESTCHART SET bCountMax=1 WHERE dwQuestID=5873;'},
        @{Name='Missing item';Change='DELETE dbo.TITEMCHART WHERE wItemID=11919;'},
        @{Name='Missing Corneus';Change='DELETE dbo.TNPCCHART WHERE wID=26416;'},
        @{Name='Unexpected reward metadata';Change='UPDATE dbo.TQREWARDCHART SET dwQuestTime=37 WHERE dwQuestID=5873;'},
        @{Name='Legacy Corneus progress';Change='INSERT dbo.TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(1,5917,0,1,1);'},
        @{Name='Legacy completion term';Change='INSERT dbo.TQUESTTERMTABLE(dwCharID,dwQuestID,dwTermID,bTermType,bCount) VALUES(1,5918,1,3,1);'},
        @{Name='External legacy dependency';Change='UPDATE dbo.TQUESTCHART SET bType=8,dwParentID=5918 WHERE dwQuestID=7;'},
        @{Name='Pending character snapshot';Change='INSERT dbo.TCharacterSaveQueue VALUES(1);'},
        @{Name='Occupied new recipe';Change='INSERT dbo.TQUESTCHART SELECT 10634,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FROM dbo.TQUESTCHART WHERE dwQuestID=5873;'}
    )
    $passed=@('Exact client ingredients and rewards','Existing headers conditions and caps retained','Saved aligned mission progress and unrelated rows preserved','New recipe and two Corneus pairs','Idempotent rerun')
    if($WakeUp){
        $cases=@(
            @{Name='Changed legacy switch';Change='UPDATE dbo.TQUESTTERMCHART SET dwTermID=364 WHERE dwQuestID=7307;'},
            @{Name='Missing giver';Change='DELETE dbo.TNPCCHART WHERE wID=25223;'},
            @{Name='Missing destination';Change='DELETE dbo.TNPCCHART WHERE wID=26003;'},
            @{Name='Missing parent';Change='UPDATE dbo.TQUESTCHART SET bType=0 WHERE dwQuestID=7243;'},
            @{Name='Legacy saved progress';Change='INSERT dbo.TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(1,7307,0,1,1);'},
            @{Name='Legacy saved term';Change='INSERT dbo.TQUESTTERMTABLE(dwCharID,dwQuestID,dwTermID,bTermType,bCount) VALUES(1,7307,1,3,1);'},
            @{Name='External reference';Change='UPDATE dbo.TQUESTCHART SET bType=8,dwParentID=7307 WHERE dwQuestID=7;'},
            @{Name='Occupied internal ID';Change='INSERT dbo.TQUESTCHART SELECT 40007307,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck FROM dbo.TQUESTCHART WHERE dwQuestID=7307;'},
            @{Name='Pending snapshots';Change='INSERT dbo.TCharacterSaveQueue VALUES(1);'}
        )
        $passed=@('Canonical NPC and parent mapping','Legacy switch retained under internal ID','Unrelated rows and synthetic saved progress retained','Idempotent rerun')
    }
    foreach($case in $cases){
        Sql ($reset+"`n"+$case.Change+"`n$state DELETE dbo.RejectedState; INSERT dbo.RejectedState VALUES(@state);")
        try{Sql $migration -Reject}catch{throw "$($case.Name): $_"}
        Sql "USE [$fixtureDb]; $state IF @state<>(SELECT payload FROM dbo.RejectedState) THROW 51055,'Rejected import changed state',1;"
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
        $previous=$env:QUEST_TEST_DB
        try{
            $fixtureSecret=(& docker exec $Container /bin/bash -c 'printenv MSSQL_SA_PASSWORD').Trim()
            $env:QUEST_TEST_DB="Server=127.0.0.1,11433;Database=$fixtureDb;User ID=sa;Password=$fixtureSecret;TrustServerCertificate=True;Encrypt=False"
            & dotnet run --project (Join-Path $repo 'Tools/QuestEngineCheck') -c Release -- --gor-content-lifecycle (Join-Path $repo $planFile)
            if($LASTEXITCODE -ne 0){throw 'Content lifecycle failed'}
        }finally{$env:QUEST_TEST_DB=$previous;$fixtureSecret=$null}
        foreach($table in $empty){Sql "USE [$fixtureDb]; SELECT * INTO dbo.AfterPlay_$table FROM dbo.$table;"}
        Sql $migration
        foreach($table in $empty){Sql "USE [$fixtureDb]; IF EXISTS(SELECT * FROM dbo.$table EXCEPT SELECT * FROM dbo.AfterPlay_$table) OR EXISTS(SELECT * FROM dbo.AfterPlay_$table EXCEPT SELECT * FROM dbo.$table) THROW 51055,'Rerun changed new progress',1;"}
    }
    [pscustomobject]@{Passed=$passed;PlayerRowsCopied=0;SourceDatabaseModified=$false} | ConvertTo-Json
}finally{if($created){Sql "USE master; ALTER DATABASE [$fixtureDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$fixtureDb];"}}
