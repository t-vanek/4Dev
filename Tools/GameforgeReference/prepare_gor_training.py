"""Freeze captured Gor training definitions and the explicitly approved fallback rules."""
import copy
import json
from pathlib import Path

from prepare_gor_reconstruction import query
from prepare_gor_event_activation import digest,sql_values

ROOT=Path(__file__).resolve().parents[2]


def main():
    folder=ROOT/'Gameforge/gor-training';folder.mkdir(exist_ok=True)
    output=folder/'plan.json';migration=ROOT/'Database/migrations/057_gor_training.sql'
    if output.exists() or migration.exists():raise ValueError('Keep frozen evidence; do not regenerate against a changed database')
    proposal=json.loads((ROOT/'Gameforge/gor-content-repairs/training-proposal.json').read_text(encoding='utf8'))
    evidence=json.loads((ROOT/'Gameforge/reconstruction-collector.json').read_text(encoding='utf8'))
    captured=evidence['training_readiness']['transactions'];by_root={t['quest_id']:t for t in captured}
    roots=proposal['mission_ids'];assert set(roots)==set(by_root)
    reference={q['dwQuestID']:q for q in json.loads((ROOT/'.runtime/gameforge-normalization/client-facts/quests.json').read_text(encoding='utf8'))['quests']}
    for ident in roots:assert reference[ident]==proposal['source_quests'][str(ident)]
    routing={40007930:(7930,26055),40007931:(7931,26056),40007932:(7932,26057)}
    helpers=[11029,11030,11033,11036,11079]
    ids=sorted(set(roots)|{t['request_quest_id'] for t in captured}|set(helpers)|set(routing))
    idsql=','.join(map(str,ids))
    groups=[('headers','TQUESTCHART','dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck'),
            ('terms','TQUESTTERMCHART','dwQuestID,bTermType,dwTermID,bCount'),
            ('conditions','TQCONDITIONCHART','dwQuestID,bConditionType,dwConditionID,bCount'),
            ('rewards','TQREWARDCHART','dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ')]
    snapshot='SELECT '+',\n'.join("JSON_QUERY(COALESCE((SELECT "+','.join('CONVERT(int,'+c+') AS '+c for c in cols.split(','))+
        f' FROM dbo.{table} WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({idsql}) ORDER BY '+cols+" FOR JSON PATH),N'[]')) AS "+name for name,table,cols in groups)+' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;'
    before=query(snapshot);assert not any(before.values()),'Selected IDs are no longer absent'
    after=dict(headers=[],terms=[],conditions=[],rewards=[])
    def condition(ident,kind,target,count=0):after['conditions'].append(dict(dwQuestID=ident,bConditionType=kind,dwConditionID=target,bCount=count))
    def header(ident,source,parent,kind,trigger,target,cap=0):
        q=reference[source]
        after['headers'].append(dict(dwQuestID=ident,dwParentID=parent,bType=kind,bForceRun=0,bTriggerType=trigger,dwTriggerID=target,bCountMax=cap,bLevel=q['bLevel'],bMain=q['bMain'],bConditionCheck=0))
        after['terms'].extend(dict(dwQuestID=ident,**t) for t in q['terms'])
    for ident in roots:
        q=reference[ident];nav=proposal['source_navigation'][str(ident)]
        giver={n['npc_id'] for n in nav if n['type']==19};receiver={n['npc_id'] for n in nav if n['type']==20}
        assert len(giver)==len(receiver)==1
        header(ident,ident,q['unknown_prefix'],8,3,next(iter(giver)),1)
        completion=by_root[ident]['request_quest_id'];assert reference[completion]['unknown_prefix']==ident
        header(completion,completion,ident,7,3,next(iter(receiver)))
        condition(completion,3,ident)
        for kind,target in ((1,q['min_level']),(2,q['max_level'])):
            if target:condition(ident,kind,target)
        for reward in q['rewards']:
            after['rewards'].append(dict(dwQuestID=ident,**reward,dwQuestMob=0,dwQuestTime=0,dwQuestPathMob=0,dwTicketID=0,bSendQ=0))
        # Capture and source must agree on experience and money before activation.
        for field,kind in (('exp_deltas',6),('money_deltas',1)):
            deltas=by_root[ident][field]
            # The first balance in a connection has no predecessor; the screenshot/source remains evidence.
            if deltas and all(v is not None for v in deltas):
                assert sum(deltas)==sum(r['dwRewardID'] for r in q['rewards'] if r['bRewardType']==kind),(ident,field,deltas)
    header(11029,11029,11014,3,1,11014);condition(11029,3,11014);condition(11029,13,9589,1)
    for helper,mission,monster,item in ((11030,11015,773,9586),(11033,11025,801,9587)):
        header(helper,helper,mission,4,5,monster);condition(helper,3,mission);condition(helper,13,item,1)
    for ident,(source,npc) in routing.items():
        header(ident,source,11016,9,3,npc);condition(ident,3,11016);condition(ident,13,reference[source]['terms'][0]['dwTermID'],1)
    header(11036,11036,11027,12,5,800);condition(11036,18,11027)
    header(11079,11079,11027,14,1,11036);condition(11079,18,11027)
    # Match the exact SQL JSON column order used by the immutable before/after hash.
    for name,_,cols in groups:
        after[name]=[{c:r[c] for c in cols.split(',')} for r in after[name]]
        after[name].sort(key=lambda r:tuple(r.values()))
    # User approved one fixed Moray, with the captured coordinate and an explicit 60s fallback.
    spawns=[dict(wID=32404,wGroup=0,wLocalID=0,wMapID=0,fPosX=3567.010009765625,fPosY=104.31999969482422,fPosZ=894.5499877929688,
        wDir=0,bCountry=3,bCount=1,bRange=0,bArea=0,bLink=0,bProb=100,bRoamType=0,dwRegion=0,dwDelay=60000,bEvent=0,wPartyID=0)]
    mapmons=[dict(wSpawnID=32404,wMonID=801,bEssential=0,bLeader=0,bProb=100)]
    items=sorted({r['dwRewardID'] for r in after['rewards'] if r['bRewardType']==2}|{r['dwTermID'] for r in after['terms'] if r['bTermType'] in (2,5)})
    plan=dict(schema=1,approval='Ano, použít tuto funkční náhradu',scope='14 observed initial training missions; no other Gor mission activation.',mission_ids=roots,
        before=before,after=after,before_hash=digest(before),after_hash=digest(after),snapshot_query=snapshot,spawns=spawns,map_monsters=mapmons,
        required_items=items,source_quests={str(i):reference[i] for i in set(roots+helpers+[v[0] for v in routing.values()]+[t['request_quest_id'] for t in captured])},
        captured_transactions=captured,source_navigation=proposal['source_navigation'],original_inputs=proposal['original_inputs'],
        routing_aliases={str(i):dict(source_id=v[0],npc=v[1]) for i,v in routing.items()},
        reconstruction_rules=dict(mission_completion_cap=1,quest_drop_probability_percent=100,moray_count=1,moray_respawn_delay_ms=60000),
        preserved='Existing legacy 7930/7931/7932 routing and all legacy mission progress remain unchanged. New routing actions use internal IDs advertised in NPC item-list packets. New Year event remains off.',
        limitations=['Fixed Moray is an approved substitute, not the unknown rare-appearance rule.','Exact repeat and quest drop rules are approved substitutes.','Real-client local playthrough and full baseline parity still require verification.'])
    state='SET @state=('+snapshot.removesuffix(';')+');\n'
    sql="""-- Captured Gor training path, with explicitly approved once/drop/Moray fallback.
-- Frozen scope and evidence: Gameforge/gor-training/plan.json.
USE [TGame_gsp];
GO
SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51057,'Quest import lock unavailable',1;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK)) THROW 51057,'Pending character snapshots must drain',1;
DECLARE @state nvarchar(max);
"""+state+f"DECLARE @hash varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2);\nIF @hash NOT IN('{digest(before)}','{digest(after)}') THROW 51057,'Training baseline differs',1;\nIF @hash='{digest(before)}'\nBEGIN\n"
    sql+=f"IF EXISTS(SELECT 1 FROM TQUESTTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({idsql})) OR EXISTS(SELECT 1 FROM TQUESTTERMTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({idsql})) THROW 51057,'Unmapped saved training progress',1;\n"
    npcs=sorted({r['dwTriggerID'] for r in after['headers'] if r['bTriggerType']==3})
    sql+='DECLARE @npcs TABLE(id int); INSERT @npcs VALUES '+','.join('('+str(i)+')' for i in npcs)+';\n'
    sql+="IF EXISTS(SELECT 1 FROM @npcs x WHERE NOT EXISTS(SELECT 1 FROM TNPCCHART n WHERE n.wID=x.id AND n.wMapID=0 AND n.bCountryID=3)) THROW 51057,'Required NPC missing or incompatible',1;\n"
    sql+='DECLARE @items TABLE(id int); INSERT @items VALUES '+','.join('('+str(i)+')' for i in items)+';\n'
    sql+="IF EXISTS(SELECT 1 FROM @items x WHERE NOT EXISTS(SELECT 1 FROM TITEMCHART i WHERE i.wItemID=x.id)) THROW 51057,'Required item missing',1;\n"
    sql+="IF EXISTS(SELECT 1 FROM TMONSPAWNCHART WHERE wID=32404) OR EXISTS(SELECT 1 FROM TMAPMONCHART WHERE wSpawnID=32404) THROW 51057,'Moray spawn ID occupied',1;\n"
    sql+="IF NOT EXISTS(SELECT 1 FROM TMONSTERCHART m JOIN TMONATTRCHART a ON a.wID=m.wMonAttr AND a.bLevel=m.bLevel WHERE m.wID=801) THROW 51057,'Moray attributes missing',1;\n"
    for name,table,_ in groups:
        rows=after[name];cols=list(rows[0]);prefix='dwID,' if name!='headers' else ''
        if name!='headers':sql+=f'DECLARE @{name}Base int=(SELECT COALESCE(MAX(dwID),0) FROM {table} WITH(UPDLOCK,HOLDLOCK));\n'
        sql+='DECLARE @'+name+' TABLE('+','.join(c+' int' for c in cols)+');\nINSERT @'+name+' VALUES\n'+sql_values(rows)+';\n'
        first=f'@{name}Base+ROW_NUMBER() OVER(ORDER BY '+','.join(cols)+'),' if name!='headers' else ''
        sql+=f'INSERT {table}('+prefix+','.join(cols)+') SELECT '+first+','.join(cols)+' FROM @'+name+';\n'
    for rows,table in ((spawns,'TMONSPAWNCHART'),(mapmons,'TMAPMONCHART')):sql+='INSERT '+table+'('+','.join(rows[0])+') VALUES\n'+sql_values(rows)+';\n'
    sql+='END;\n'+state+f"IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'{digest(after)}' THROW 51057,'Training postcondition failed',1;\n"
    for rows,table,field in ((spawns,'TMONSPAWNCHART','wID'),(mapmons,'TMAPMONCHART','wSpawnID')):
        cols=list(rows[0]);name='verify'+table
        sql+='DECLARE @'+name+' TABLE('+','.join(c+(' real' if c.startswith('fPos') else ' bigint') for c in cols)+');\nINSERT @'+name+' VALUES\n'+sql_values(rows)+';\n'
        scoped='SELECT '+','.join(cols)+f' FROM {table} WHERE {field}=32404'
        sql+=f"IF EXISTS({scoped} EXCEPT SELECT * FROM @{name}) OR EXISTS(SELECT * FROM @{name} EXCEPT {scoped}) THROW 51057,'Moray population differs',1;\n"
    sql+='COMMIT TRANSACTION;\nGO\n'
    output.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+'\n',encoding='utf8');migration.write_text(sql,encoding='utf8')
    print(json.dumps(dict(missions=len(roots),headers=len(ids),before_hash=digest(before),after_hash=digest(after))))


if __name__=='__main__':main()
