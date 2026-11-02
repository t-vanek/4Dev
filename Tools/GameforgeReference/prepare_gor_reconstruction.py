"""Freeze the user-approved five-mission reconstruction; never infer a population export."""
import copy
import json
import subprocess
from pathlib import Path

from audit_quest_turn_in_npcs import load_reference
from prepare_gor_event_activation import digest, sql_values


def query(sql):
    r = subprocess.run(['docker', 'exec', 'araz-mssql', '/bin/bash', '-c',
        '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -d TGame_gsp -y 0 -w 65535 -Q "$1"',
        'gor-reconstruction', 'SET NOCOUNT ON; '+sql], capture_output=True, text=True, check=True)
    raw = ''.join(r.stdout.splitlines())
    return json.loads(raw[min(i for i in (raw.find('['), raw.find('{')) if i >= 0):])


def main():
    output = Path('Gameforge/gor-remaining-quests/reconstruction-plan.json')
    migration = Path('Database/migrations/054_gor_reconstructed_missions.sql')
    if output.exists() or migration.exists():
        raise ValueError('Keep the frozen migration evidence; do not regenerate against a changed database')
    proposal = json.loads(Path('Gameforge/gor-remaining-quests/reconstruction-proposal.json').read_text())
    reference, navigation, _, inputs = load_reference(Path('C:/Program Files (x86)/4Story'), json.loads(Path('Gameforge/reference.json').read_text()))
    pairs = {1082: (10552,26424,26427),1084: (10553,26424,26427),1203: (10596,26425,26428),
             1208: (10597,26425,26428),3209: (9716,26081,26081),3210: (3211,26474,26474)}
    quarantined = [1083,1085,1204,1209,3212,3213]
    ids = sorted(set(pairs) | {v[0] for v in pairs.values()} | set(quarantined))
    idsql = ','.join(map(str,ids))
    groups = [('headers','TQUESTCHART','dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck'),
              ('terms','TQUESTTERMCHART','dwQuestID,bTermType,dwTermID,bCount'),
              ('conditions','TQCONDITIONCHART','dwQuestID,bConditionType,dwConditionID,bCount'),
              ('rewards','TQREWARDCHART','dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ')]
    snapshot = 'SELECT '+',\n'.join("JSON_QUERY(COALESCE((SELECT "+','.join('CONVERT(int,'+c+') AS '+c for c in cols.split(','))+
        ' FROM dbo.'+table+' WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN('+idsql+') ORDER BY '+cols+
        " FOR JSON PATH),N'[]')) AS "+name for name,table,cols in groups)+' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;'
    before = query(snapshot)
    old = {r['dwQuestID']:r for r in before['headers']}
    assert set(old)==set(ids)-{10552,10553,10596,10597,9716}
    assert all(old[i]['bType']==0 and old[i]['bCountMax']==1 for i in (1082,1084,1203,1208,3209))
    after = dict(headers=[],terms=[],conditions=[],rewards=[])
    for ident,(completion,giver,receiver) in pairs.items():
        q, c = reference[ident],reference[completion]
        assert q['bType']==8 and q['unknown_prefix']==0
        assert c['bType']==7 and c['unknown_prefix']==ident and c['terms']==[dict(bTermType=1,dwTermID=ident,bCount=0)]
        assert {n['npc_id'] for n in navigation[ident] if n['type']==19}=={giver}
        assert {n['npc_id'] for n in navigation[ident] if n['type']==20}=={receiver}
        for key,source,parent,typ,npc in ((ident,q,0,8,giver),(completion,c,ident,7,receiver)):
            after['headers'].append(dict(dwQuestID=key,dwParentID=parent,bType=typ,bForceRun=0,bTriggerType=3,
                dwTriggerID=npc,bCountMax=old.get(key,{}).get('bCountMax',0),bLevel=source['bLevel'],bMain=source['bMain'],bConditionCheck=0))
            after['terms'] += [dict(dwQuestID=key,**t) for t in source['terms']]
        for typ,level in ((1,q['min_level']),(2,q['max_level'])):
            if level: after['conditions'].append(dict(dwQuestID=ident,bConditionType=typ,dwConditionID=level,bCount=0))
        after['conditions'].append(dict(dwQuestID=completion,bConditionType=3,dwConditionID=ident,bCount=0))
        for r in q['rewards']:
            after['rewards'].append(dict(dwQuestID=ident,bRewardType=r['bRewardType'],dwRewardID=r['dwRewardID'],
                bTakeMethod=r['bTakeMethod'],bTakeData=r['bTakeData'],bCount=r['bCount'],dwQuestMob=0,dwQuestTime=0,dwQuestPathMob=0,dwTicketID=0,bSendQ=0))
    for ident in quarantined:
        h=copy.deepcopy(old[ident]);h['bType']=0;after['headers'].append(h)
        for group in ('terms','conditions','rewards'):
            after[group] += [copy.deepcopy(r) for r in before[group] if r['dwQuestID']==ident]
    assert next(r for r in after['headers'] if r['dwQuestID']==3210)['bCountMax']==0
    for rows in after.values(): rows.sort(key=lambda r:tuple(r.values()))
    spawns=[];mapmons=[]
    for i,s in enumerate(proposal['ragoen']['proposed_spawns'],32398):
        spawns.append(dict(wID=i,wGroup=0,wLocalID=0,wMapID=s['map_id'],fPosX=s['x'],fPosY=s['y'],fPosZ=s['z'],
            wDir=0,bCountry=3,bCount=1,bRange=0,bArea=0,bLink=0,bProb=100,bRoamType=0,dwRegion=0,dwDelay=60000,bEvent=0,wPartyID=0))
        mapmons.append(dict(wSpawnID=i,wMonID=s['monster_template_id'],bEssential=0,bLeader=0,bProb=100))
    plan=dict(schema=1,approval='User explicitly approved this reconstruction for these five missions; see reconstruction-proposal.json.',
        enabled_ids=[1082,1084,1203,1208,3209],producer_id=3210,quarantined_ids=quarantined,
        before=before,after=after,before_hash=digest(before),after_hash=digest(after),snapshot_query=snapshot,
        spawns=spawns,map_monsters=mapmons,original_inputs=inputs,
        source_quests={str(i):reference[i] for i in pairs},source_completions={str(v[0]):reference[v[0]] for v in pairs.values()},
        source_boundary='Objectives, rewards, NPCs and target coordinates are pinned. Population count/delay and two-round PvP rules are explicitly approved reconstruction, not certified Gameforge parity.')
    sql='''-- Explicitly approved functional reconstruction. NOT a certified Gameforge population/arena export.
-- Evidence and frozen identities: Gameforge/gor-remaining-quests/reconstruction-plan.json.
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51054,'Quest import lock unavailable',1;
DECLARE @state nvarchar(max);
'''
    state='SET @state=('+snapshot.removesuffix(';')+');\n'
    sql+=state+f"DECLARE @hash varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2);\nIF @hash NOT IN('{digest(before)}','{digest(after)}') THROW 51054,'Reconstruction baseline differs',1;\n"
    sql+="IF COL_LENGTH('dbo.TARENACHART','bQuestMatch') IS NULL EXEC(N'ALTER TABLE dbo.TARENACHART ADD bQuestMatch bit NOT NULL CONSTRAINT DF_TARENACHART_QuestMatch DEFAULT(0)');\n"
    sql+=f"IF @hash='{digest(before)}'\nBEGIN\n"
    sql+=f'''IF EXISTS(SELECT 1 FROM dbo.TQUESTTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({idsql}))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({idsql}))
 THROW 51054,'Legacy player progress must be retained before identity replacement',1;
IF EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c JOIN dbo.TQUESTCHART q ON q.dwQuestID=c.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN({idsql}) AND c.bConditionType IN(3,15,18) AND c.dwConditionID IN({idsql}))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMCHART t JOIN dbo.TQUESTCHART q ON q.dwQuestID=t.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN({idsql}) AND t.bTermType IN(1,18) AND t.dwTermID IN({idsql}))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE bType<>0 AND dwQuestID NOT IN({idsql}) AND (dwParentID IN({idsql}) OR (bTriggerType IN(1,9) AND dwTriggerID IN({idsql}))))
 THROW 51054,'Unexpected active legacy dependency',1;
IF (SELECT COUNT(*) FROM dbo.TNPCCHART WHERE wID IN(26424,26425,26427,26428,26474) AND wMapID=0 AND bCountryID=3)<>5
 OR NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26081 AND wMapID=0 AND bCountryID=2)
 OR NOT EXISTS(SELECT 1 FROM dbo.TITEMCHART WHERE wItemID=9737)
 THROW 51054,'Required NPC or seal template missing',1;
IF (SELECT COUNT(*) FROM dbo.TARENACHART)<>2
 OR NOT EXISTS(SELECT 1 FROM dbo.TARENACHART WHERE wID=1 AND bType=3 AND dwFee=50000 AND wInPos=17000 AND wOutPos=17001)
 OR NOT EXISTS(SELECT 1 FROM dbo.TARENACHART WHERE wID=2 AND bType=7 AND dwFee=100000 AND wInPos=17002 AND wOutPos=17003)
 THROW 51054,'Arena baseline differs',1;
EXEC(N'IF EXISTS(SELECT 1 FROM dbo.TARENACHART WHERE bQuestMatch<>0) THROW 51054,''Arena reconstruction flag already changed'',1;');
IF EXISTS(SELECT 1 FROM dbo.TMONSPAWNCHART WHERE wID BETWEEN 32398 AND 32403)
 OR EXISTS(SELECT 1 FROM dbo.TMAPMONCHART WHERE wSpawnID BETWEEN 32398 AND 32403 OR wMonID IN(3504,3502,3512,3510,3521,3519))
 THROW 51054,'Population identities already occupied',1;
IF (SELECT COUNT(*) FROM dbo.TMONSTERCHART m JOIN dbo.TMONATTRCHART a ON a.wID=m.wMonAttr AND a.bLevel=m.bLevel
 WHERE (m.wID=3502 AND m.wMonAttr=30069 AND m.bLevel=93) OR (m.wID=3504 AND m.wMonAttr=30071 AND m.bLevel=95)
 OR (m.wID=3510 AND m.wMonAttr=30073 AND m.bLevel=94) OR (m.wID=3512 AND m.wMonAttr=30074 AND m.bLevel=96)
 OR (m.wID=3519 AND m.wMonAttr=30078 AND m.bLevel=95) OR (m.wID=3521 AND m.wMonAttr=30079 AND m.bLevel=97))<>6
 THROW 51054,'Required monster template missing',1;
'''
    sql+='DECLARE @h TABLE('+','.join(c+' int' for c in after['headers'][0])+');\nINSERT @h VALUES\n'+sql_values(after['headers'])+';\n'
    cols=list(after['headers'][0]);others=cols[1:]
    sql+='UPDATE q SET '+','.join(c+'=h.'+c for c in others)+' FROM dbo.TQUESTCHART q JOIN @h h ON q.dwQuestID=h.dwQuestID;\n'
    sql+='INSERT dbo.TQUESTCHART('+','.join(cols)+') SELECT '+','.join('h.'+c for c in cols)+' FROM @h h WHERE NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART q WHERE q.dwQuestID=h.dwQuestID);\n'
    for name,table,_ in groups[1:]:
        rows=after[name];cols=list(rows[0])
        sql+='DECLARE @'+name+' TABLE('+','.join(c+' int' for c in cols)+');\nINSERT @'+name+' VALUES\n'+sql_values(rows)+';\n'
        sql+=f'DELETE dbo.{table} WHERE dwQuestID IN({idsql});\nDECLARE @{name}Base int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.{table} WITH(UPDLOCK,HOLDLOCK));\n'
        sql+=f'INSERT dbo.{table}(dwID,'+','.join(cols)+f') SELECT @{name}Base+ROW_NUMBER() OVER(ORDER BY '+','.join(cols)+'),'+','.join(cols)+' FROM @'+name+';\n'
    for rows,table in ((spawns,'TMONSPAWNCHART'),(mapmons,'TMAPMONCHART')):
        sql+='INSERT dbo.'+table+'('+','.join(rows[0])+') VALUES\n'+sql_values(rows)+';\n'
    sql+="EXEC(N'UPDATE dbo.TARENACHART SET bType=CASE wID WHEN 1 THEN 3 ELSE 5 END,bQuestMatch=1 WHERE wID IN(1,2);');\nEND;\n"
    sql+=state+f"IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'{digest(after)}' THROW 51054,'Quest postcondition failed',1;\n"
    sql+="EXEC(N'IF (SELECT COUNT(*) FROM dbo.TARENACHART WHERE bQuestMatch=1 AND ((wID=1 AND bType=3 AND dwFee=50000 AND wInPos=17000 AND wOutPos=17001) OR (wID=2 AND bType=5 AND dwFee=100000 AND wInPos=17002 AND wOutPos=17003)))<>2 THROW 51054,''Arena postcondition failed'',1;');\n"
    for rows,table,key in ((spawns,'TMONSPAWNCHART','wID'),(mapmons,'TMAPMONCHART','wSpawnID')):
        types={c:('real' if c.startswith('fPos') else 'bigint') for c in rows[0]}
        sql+='DECLARE @verify'+table+' TABLE('+','.join(c+' '+t for c,t in types.items())+');\nINSERT @verify'+table+' VALUES\n'+sql_values(rows)+';\n'
        cols=','.join(rows[0]);scoped=f'SELECT {cols} FROM dbo.{table} WHERE {key} BETWEEN 32398 AND 32403'
        sql+=f'IF EXISTS({scoped} EXCEPT SELECT * FROM @verify{table}) OR EXISTS(SELECT * FROM @verify{table} EXCEPT {scoped}) THROW 51054,\'Population postcondition failed\',1;\n'
    sql+='COMMIT TRANSACTION;\nGO\n'
    output.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    migration.write_text(sql,encoding='utf-8')
    print(json.dumps(dict(before_hash=digest(before),after_hash=digest(after),quest_ids=ids)))


if __name__ == '__main__':
    main()
