"""Opt-in isolated SQL migration and real .NET sequential training checks; no gameplay DB writes."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid

from reconstruction_store import SqlServerStore

ROOT=Path(__file__).resolve().parents[2]


def portal_checks(execute,database):
    migration=(ROOT/'Database/migrations/058_gor_portal_destinations.sql').read_text(encoding='utf8').replace('USE [TGame_gsp];','USE ['+database+'];')
    execute('UPDATE TPORTALCHART SET wSpawnID=wPortalID WHERE wPortalID IN(4506,4507); DELETE TSPAWNPOSCHART WHERE wID=4509; IF NOT EXISTS(SELECT 1 FROM TNPCITEMCHART WHERE wNpcID=26176 AND dwItemID=4507) INSERT TNPCITEMCHART VALUES(26176,4507); SELECT * INTO PortalSeed_TPORTALCHART FROM TPORTALCHART; SELECT * INTO PortalSeed_TSPAWNPOSCHART FROM TSPAWNPOSCHART; SELECT * INTO PortalSeed_TNPCITEMCHART FROM TNPCITEMCHART;')
    def reset():execute('DELETE TPORTALCHART; INSERT TPORTALCHART SELECT * FROM PortalSeed_TPORTALCHART; DELETE TSPAWNPOSCHART; INSERT TSPAWNPOSCHART SELECT * FROM PortalSeed_TSPAWNPOSCHART; DELETE TNPCITEMCHART; INSERT TNPCITEMCHART SELECT * FROM PortalSeed_TNPCITEMCHART; DELETE TCharacterSaveQueue;')
    passed=[]
    execute(migration);execute(migration);passed+=['captured portal repair','portal repair idempotence']
    for table,where in [('TPORTALCHART','WHERE wPortalID NOT IN(4506,4507)'),('TSPAWNPOSCHART','WHERE wID<>4509'),('TNPCITEMCHART','WHERE NOT(wNpcID=26176 AND dwItemID=4507)')]:
        execute(f"IF EXISTS(SELECT * FROM {table} {where} EXCEPT SELECT * FROM PortalSeed_{table} {where}) OR EXISTS(SELECT * FROM PortalSeed_{table} {where} EXCEPT SELECT * FROM {table} {where}) THROW 51058,'Unrelated portal/spawn changed',1;")
    passed.append('unrelated portals and existing spawn positions preserved')
    for label,change in [('portal mapping drift','UPDATE TPORTALCHART SET wSpawnID=0 WHERE wPortalID=4506;'),('captured exit drift','UPDATE TSPAWNPOSCHART SET fPosX=fPosX+1 WHERE wID=4507;'),('occupied new spawn','INSERT TSPAWNPOSCHART SELECT 4509,wMapID,fPosX,fPosY,fPosZ,bType FROM TSPAWNPOSCHART WHERE wID=4506;'),('pending saves','INSERT TCharacterSaveQueue VALUES(1);'),('ambiguous NPC binding','UPDATE TNPCITEMCHART SET dwItemID=4508 WHERE wNpcID=26176 AND dwItemID=4507;'),('missing NPC binding','DELETE TNPCITEMCHART WHERE wNpcID=26176 AND dwItemID=4506;')]:
        reset();execute(change)
        for table in ['TPORTALCHART','TSPAWNPOSCHART','TNPCITEMCHART']:execute(f"IF OBJECT_ID('Rejected_{table}') IS NOT NULL DROP TABLE Rejected_{table}; SELECT * INTO Rejected_{table} FROM {table};")
        try:execute(migration)
        except RuntimeError as exc:
            if '51058' not in str(exc):raise
        else:raise AssertionError('Expected portal rejection: '+label)
        for table in ['TPORTALCHART','TSPAWNPOSCHART','TNPCITEMCHART']:execute(f"IF EXISTS(SELECT * FROM {table} EXCEPT SELECT * FROM Rejected_{table}) OR EXISTS(SELECT * FROM Rejected_{table} EXCEPT SELECT * FROM {table}) THROW 51058,'Rejected repair changed rows',1;")
        passed.append(label+' rejects atomically')
    reset();execute(migration)
    return passed


def main():
    if os.environ.get('FOURDEV_TRAINING_SQL_TEST')!='1':raise RuntimeError('Set FOURDEV_TRAINING_SQL_TEST=1 for isolated fixture checks')
    store=SqlServerStore(initialize=False);database='4DevQuestTest_training_'+uuid.uuid4().hex
    assert re.fullmatch('4DevQuestTest_training_[a-f0-9]{32}',database)
    plan=json.loads((ROOT/'Gameforge/gor-training/plan.json').read_text(encoding='utf8'))
    sql=(ROOT/'Database/migrations/057_gor_training.sql').read_text(encoding='utf8').replace('USE [TGame_gsp];','USE ['+database+'];')
    created=False;passed=[]
    def execute(text):return store.execute(text,database=database)
    def reject(change,label):
        reset();execute(change)
        try:execute(sql)
        except RuntimeError as exc:
            if '51057' not in str(exc):raise
        else:raise AssertionError('Expected rejection: '+label)
        state=execute('DECLARE @state nvarchar(max); SET @state=('+plan['snapshot_query'].rstrip(';')+'); SELECT CONVERT(varchar(64),HASHBYTES(\'SHA2_256\',@state),2);').strip()
        # Occupied header is deliberately outside the original hash; it must survive unchanged.
        if label!='occupied header':assert state==plan['before_hash'],(label,state)
        passed.append(label+' rejects before committing')
    changed=['TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART','TMONSPAWNCHART','TMAPMONCHART','TNPCCHART','TITEMCHART','TMONATTRCHART']
    def reset():
        execute('\n'.join('DELETE dbo.['+t+']; INSERT dbo.['+t+'] SELECT * FROM dbo.[Before_'+t+'];' for t in changed)+'\nDELETE dbo.TCharacterSaveQueue;')
    try:
        store.execute('CREATE DATABASE ['+database+'];',database='master');created=True
        # Preserve all source schema shapes but copy no player rows. Runtime chart inputs are copied.
        objects=json.loads(''.join(store.execute("SELECT name,'table' AS kind FROM TGame_gsp.sys.tables WHERE schema_id=1 UNION ALL SELECT name,'view' FROM TGame_gsp.sys.views WHERE schema_id=1 AND name LIKE '%CHART' FOR JSON PATH;",database='master').splitlines()))
        full_extra={'TSKILLDATA','ACCESSORYMAGICTABLE','TLOCALTABLE','TLOCALOCCUPYTABLE','TMISSIONTABLE'}
        copies=[]
        for obj in objects:
            name=obj['name'];assert re.fullmatch('[A-Za-z0-9_]+',name)
            if name=='TCharacterSaveQueue':continue
            full=name.endswith('CHART') or name in full_extra
            copies.append('SELECT '+('' if full else 'TOP(0) ')+'* INTO dbo.['+name+'] FROM TGame_gsp.dbo.['+name+'];')
        execute('\n'.join(copies))
        execute('CREATE TABLE dbo.TCharacterSaveQueue(FixtureId int NOT NULL); INSERT TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(999,8937,0,1,1);')
        # Recreate the frozen pre-import state even when the source server already has migration 057.
        assert not any(plan['before'].values())
        scoped=','.join(str(r['dwQuestID']) for r in plan['after']['headers'])
        execute('\n'.join('DELETE '+t+' WHERE dwQuestID IN('+scoped+');' for t in changed[:4])+
            '\nDELETE TMAPMONCHART WHERE wSpawnID=32404; DELETE TMONSPAWNCHART WHERE wID=32404;')
        execute('\n'.join('SELECT * INTO dbo.[Before_'+t+'] FROM dbo.['+t+'];' for t in changed))
        execute(sql);execute(sql);passed.extend(['canonical import','idempotent rerun'])
        for table in changed:
            if table in ('TNPCCHART','TITEMCHART','TMONATTRCHART'):where=''
            elif table in ('TMONSPAWNCHART','TMAPMONCHART'):where=' WHERE '+('wID' if table=='TMONSPAWNCHART' else 'wSpawnID')+'<>32404'
            else:where=' WHERE dwQuestID NOT IN('+','.join(str(r['dwQuestID']) for r in plan['after']['headers'])+')'
            execute(f"IF EXISTS(SELECT * FROM [{table}]{where} EXCEPT SELECT * FROM [Before_{table}]) OR EXISTS(SELECT * FROM [Before_{table}] EXCEPT SELECT * FROM [{table}]{where}) THROW 51057,'Unrelated fixture rows changed',1;")
        passed.append('unrelated static rows and legacy routing preserved')
        reject('INSERT TCharacterSaveQueue VALUES(1);','pending character snapshots')
        reject('DELETE TNPCCHART WHERE wID=26025;','missing NPC')
        reject('DELETE TITEMCHART WHERE wItemID=9587;','missing certificate template')
        reject('INSERT TQUESTCHART(dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck) VALUES(11012,0,0,0,0,0,0,1,0,0);','occupied header')
        reject('INSERT TMONSPAWNCHART SELECT 32404,wGroup,wLocalID,wMapID,fPosX,fPosY,fPosZ,wDir,bCountry,bCount,bRange,bArea,bLink,bProb,bRoamType,dwRegion,dwDelay,bEvent,wPartyID FROM TMONSPAWNCHART WHERE wID=4871;','occupied population')
        reject('DELETE TMONATTRCHART WHERE wID IN(SELECT wMonAttr FROM TMONSTERCHART WHERE wID=801);','missing Moray attributes')
        reset();execute(sql)
        if '--portals' in sys.argv:passed+=portal_checks(execute,database)
        # Copy only native persistence procedure definitions into the synthetic database.
        procedures=json.loads(''.join(store.execute("SELECT definition FROM TGame_gsp.sys.sql_modules WHERE object_id IN(SELECT object_id FROM TGame_gsp.sys.procedures WHERE name IN('TSaveQuest','TSaveQuestTerm','TSaveItemDataStart','TSaveItemDataEnd','TSaveInven','TSaveItem')) FOR JSON PATH;",database='master').splitlines()))
        for proc in procedures:execute("EXEC(N'"+proc['definition'].replace("'","''")+"');")
        secret=subprocess.run(['docker','exec','araz-mssql','/bin/bash','-c','printenv MSSQL_SA_PASSWORD'],capture_output=True,text=True,check=True).stdout.strip()
        env=dict(os.environ,QUEST_TEST_DB=f'Server=127.0.0.1,11433;Database={database};User ID=sa;Password={secret};TrustServerCertificate=True;Encrypt=False')
        result=subprocess.run(['dotnet','run','--project',str(ROOT/'Tools/QuestEngineCheck'),'-c','Release','--no-build','--','--gor-training-lifecycle',str(ROOT/'Gameforge/gor-training/plan.json')],env=env,capture_output=True,text=True,encoding='utf8',errors='replace')
        print(result.stdout,flush=True)
        if result.returncode:raise RuntimeError(result.stderr[-7000:])
        if '--portals' in sys.argv:
            portal_result=subprocess.run(['dotnet','run','--project',str(ROOT/'Tools/QuestEngineCheck'),'-c','Release','--no-build','--','--gor-portals-lifecycle',str(ROOT/'Gameforge/gor-training/portal-destinations.json')],env=env,capture_output=True,text=True,encoding='utf8',errors='replace')
            print(portal_result.stdout,flush=True)
            if portal_result.returncode:raise RuntimeError(portal_result.stderr[-7000:])
        execute('SELECT * INTO SavedProgress FROM TQUESTTABLE; SELECT * INTO SavedTerms FROM TQUESTTERMTABLE; SELECT * INTO SavedItems FROM TITEMTABLE;')
        execute(sql)
        for table,saved in [('TQUESTTABLE','SavedProgress'),('TQUESTTERMTABLE','SavedTerms'),('TITEMTABLE','SavedItems')]:execute(f"IF EXISTS(SELECT * FROM {table} EXCEPT SELECT * FROM {saved}) OR EXISTS(SELECT * FROM {saved} EXCEPT SELECT * FROM {table}) THROW 51057,'Replay changed saved state',1;")
        passed.append('post-play migration rerun retains all new and unrelated saved progress')
        print(json.dumps(dict(Passed=passed,SourceDatabaseModified=False,PlayerRowsCopied=0,Fixture=database)),flush=True)
    finally:
        if created:store.execute('ALTER DATABASE ['+database+'] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ['+database+'];',database='master')


if __name__=='__main__':main()
