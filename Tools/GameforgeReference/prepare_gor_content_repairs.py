"""Restore supported Gor definitions from pinned assets, preserving existing eligibility and caps."""
import copy
import json
from pathlib import Path

from audit_quest_turn_in_npcs import load_reference, term_key
from prepare_gor_event_activation import digest, sql_values
from prepare_gor_reconstruction import query


def main():
    folder=Path('Gameforge/gor-content-repairs');folder.mkdir(exist_ok=True)
    plan_path=folder/'plan.json';migration=Path('Database/migrations/055_gor_content_repairs.sql')
    if plan_path.exists() or migration.exists():raise ValueError('Keep frozen import evidence')
    ref,nav,npcs,pins=load_reference(Path('C:/Program Files (x86)/4Story'),json.loads(Path('Gameforge/reference.json').read_text()))
    audit=json.loads(Path('Gameforge/gor-quests/audit.json').read_text(encoding='utf-8'))
    headers=query('SELECT * FROM dbo.TQUESTCHART ORDER BY dwQuestID FOR JSON PATH;');server={q['dwQuestID']:q for q in headers}
    selected=[]
    for r in audit['rows']:
        i=r['id'];s=server.get(i);q=ref[i];issues=set(r['issues'])
        if s and q['bType']==s['bType'] and q['bType'] in (8,18) and issues & {'reward_rows_differ','objectives_differ'} and not issues & {'quest_type_differs','parent_link_differs','start_npc_differs','completion_action_missing_or_wrong_type'}:
            selected.append(i)
    assert len(selected)==41
    recipes=[i for i in selected if ref[i]['bType']==18];assert len(recipes)==32
    missions=[i for i in selected if ref[i]['bType']==8];assert len(missions)==9
    ids=sorted(selected+[5917,5918,5919,5920,10634]);idsql=','.join(map(str,ids))
    groups=[('headers','TQUESTCHART','dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck'),
            ('terms','TQUESTTERMCHART','dwQuestID,bTermType,dwTermID,bCount'),
            ('conditions','TQCONDITIONCHART','dwQuestID,bConditionType,dwConditionID,bCount'),
            ('rewards','TQREWARDCHART','dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ')]
    snapshot='SELECT '+',\n'.join("JSON_QUERY(COALESCE((SELECT "+','.join('CONVERT(int,'+c+') AS '+c for c in cols.split(','))+
        f' FROM dbo.{table} WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({idsql}) ORDER BY '+cols+" FOR JSON PATH),N'[]')) AS "+name for name,table,cols in groups)+' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;'
    before=query(snapshot);after=copy.deepcopy(before)
    before_h={q['dwQuestID']:q for q in before['headers']};assert 10634 not in before_h
    corneus=[5917,5918,5919,5920]
    for i in selected:
        q=ref[i];s=before_h[i]
        assert q['unknown_prefix']==s['dwParentID'] and s['bTriggerType']==3
        if i in missions:assert term_key([r for r in before['terms'] if r['dwQuestID']==i])==term_key(q['terms'])
        assert all(r['dwQuestMob']==r['dwQuestTime']==r['dwQuestPathMob']==r['dwTicketID']==r['bSendQ']==0 for r in before['rewards'] if r['dwQuestID']==i)
        if i in recipes:after['terms']=[r for r in after['terms'] if r['dwQuestID']!=i]+[dict(dwQuestID=i,**t) for t in q['terms']]
    for i,parent,typ in [(5917,0,8),(5918,5917,7),(5919,0,8),(5920,5919,7)]:
        assert before_h[i]['bType']==18 and before_h[i]['bCountMax']==0 and before_h[i]['bTriggerType']==3 and before_h[i]['dwTriggerID']==22745
        assert ref[i]['bType']==typ and ref[i]['unknown_prefix']==parent
        if typ==8:
            assert {p['npc_id'] for p in nav[i] if p['type'] in (19,20)}=={26416}
        next(h for h in after['headers'] if h['dwQuestID']==i).update(dwParentID=parent,bType=typ,dwTriggerID=26416,bLevel=ref[i]['bLevel'],bMain=ref[i]['bMain'])
        after['terms']=[r for r in after['terms'] if r['dwQuestID']!=i]+[dict(dwQuestID=i,**t) for t in ref[i]['terms']]
        after['conditions']=[r for r in after['conditions'] if r['dwQuestID']!=i]
        if typ==7:after['conditions'].append(dict(dwQuestID=i,bConditionType=3,dwConditionID=parent,bCount=0))
    assert ref[10634]['bType']==18 and ref[10634]['unknown_prefix']==0
    assert any(p['name']=='Beshil' for p in npcs[26007])
    after['headers'].append(dict(dwQuestID=10634,dwParentID=0,bType=18,bForceRun=0,bTriggerType=3,dwTriggerID=26007,bCountMax=0,bLevel=ref[10634]['bLevel'],bMain=ref[10634]['bMain'],bConditionCheck=0))
    after['terms'] += [dict(dwQuestID=10634,**t) for t in ref[10634]['terms']]
    changed_rewards=selected+corneus+[10634]
    after['rewards']=[r for r in after['rewards'] if r['dwQuestID'] not in changed_rewards]
    for i in changed_rewards:
        for r in ref[i]['rewards']:
            assert r['bRewardType'] in (1,2,6,9) and r['bTakeMethod']==1
            after['rewards'].append(dict(dwQuestID=i,bRewardType=r['bRewardType'],dwRewardID=r['dwRewardID'],bTakeMethod=r['bTakeMethod'],bTakeData=r['bTakeData'],bCount=r['bCount'],dwQuestMob=0,dwQuestTime=0,dwQuestPathMob=0,dwTicketID=0,bSendQ=0))
    for rows in after.values():rows.sort(key=lambda r:tuple(r.values()))
    required_items=sorted({r['dwTermID'] for r in after['terms'] if r['bTermType']==2}|{r['dwRewardID'] for r in after['rewards'] if r['bRewardType']==2})
    item_ids={r['id'] for r in query('SELECT wItemID AS id FROM dbo.TITEMCHART FOR JSON PATH;')};assert set(required_items)<=item_ids
    plan=dict(schema=1,scope='32 existing recipes, 9 aligned mission reward definitions, 2 Corneus exchanges and one absent recipe. No new populations or invented quest/drop rules.',
        recipe_ids=recipes,aligned_mission_ids=missions,corneus_ids=corneus,new_recipe_id=10634,
        before=before,after=after,before_hash=digest(before),after_hash=digest(after),snapshot_query=snapshot,required_item_ids=required_items,original_inputs=pins,
        source_definitions={str(i):ref[i] for i in ids},preservation='Existing recipe NPCs, class conditions, caps and headers are retained. Existing aligned mission objectives/conditions/caps and player progress are retained. Corneus caps remain 0. New crafting action has no mission counters; metadata type-0 terms stay inactive exactly as in the pinned definition.')
    state='SET @state=('+snapshot.removesuffix(';')+');\n'
    sql='''-- Pinned Gor content repair; no invented populations, probabilities, or reward amounts.
-- Exact plan and source hashes: Gameforge/gor-content-repairs/plan.json.
USE [TGame_gsp];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51055,'Quest import lock unavailable',1;
IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK)) THROW 51055,'Pending character snapshots must drain before content repair',1;
DECLARE @state nvarchar(max);
'''+state+f"DECLARE @hash varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2);\nIF @hash NOT IN('{digest(before)}','{digest(after)}') THROW 51055,'Content baseline differs',1;\nIF @hash='{digest(before)}'\nBEGIN\n"
    sql+='''IF EXISTS(SELECT 1 FROM dbo.TQUESTTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(5917,5918,5919,5920,10634))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN(5917,5918,5919,5920,10634))
 THROW 51055,'Saved legacy Corneus/crafting identity must be preserved',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26416 AND wMapID=0 AND bCountryID=3)
 OR NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26007 AND wMapID=0 AND bCountryID=3)
 THROW 51055,'Required NPC missing',1;
IF EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE bType<>0 AND dwQuestID NOT IN(5917,5918,5919,5920) AND (dwParentID IN(5917,5918,5919,5920) OR (bTriggerType IN(1,9) AND dwTriggerID IN(5917,5918,5919,5920))))
 OR EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c JOIN dbo.TQUESTCHART q ON q.dwQuestID=c.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN(5917,5918,5919,5920) AND c.bConditionType IN(3,15,18) AND c.dwConditionID IN(5917,5918,5919,5920))
 OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMCHART t JOIN dbo.TQUESTCHART q ON q.dwQuestID=t.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN(5917,5918,5919,5920) AND t.bTermType IN(1,18) AND t.dwTermID IN(5917,5918,5919,5920))
 THROW 51055,'Unexpected active legacy Corneus dependency',1;
'''
    sql+='DECLARE @items TABLE(id int PRIMARY KEY);\nINSERT @items VALUES\n'+',\n'.join('('+str(i)+')' for i in required_items)+';\n'
    sql+="IF EXISTS(SELECT 1 FROM @items i WHERE NOT EXISTS(SELECT 1 FROM dbo.TITEMCHART t WHERE t.wItemID=i.id)) THROW 51055,'Required item missing',1;\n"
    h=after['headers'];cols=list(h[0]);sql+='DECLARE @h TABLE('+','.join(c+' int' for c in cols)+');\nINSERT @h VALUES\n'+sql_values(h)+';\n'
    sql+='UPDATE q SET '+','.join(c+'=h.'+c for c in cols[1:])+' FROM dbo.TQUESTCHART q JOIN @h h ON h.dwQuestID=q.dwQuestID;\n'
    sql+='INSERT dbo.TQUESTCHART('+','.join(cols)+') SELECT '+','.join('h.'+c for c in cols)+' FROM @h h WHERE NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART q WHERE q.dwQuestID=h.dwQuestID);\n'
    for name,table,_ in groups[1:]:
        rows=after[name];cols=list(rows[0]);sql+='DECLARE @'+name+' TABLE('+','.join(c+' int' for c in cols)+');\nINSERT @'+name+' VALUES\n'+sql_values(rows)+';\n'
        sql+=f'DELETE dbo.{table} WHERE dwQuestID IN({idsql});\nDECLARE @{name}Base int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.{table} WITH(UPDLOCK,HOLDLOCK));\n'
        sql+=f'INSERT dbo.{table}(dwID,'+','.join(cols)+f') SELECT @{name}Base+ROW_NUMBER() OVER(ORDER BY '+','.join(cols)+'),'+','.join(cols)+' FROM @'+name+';\n'
    sql+='END;\n'+state+f"IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'{digest(after)}' THROW 51055,'Content postcondition failed',1;\nCOMMIT TRANSACTION;\nGO\n"
    plan_path.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');migration.write_text(sql,encoding='utf-8')
    print(json.dumps(dict(recipes=len(recipes),aligned_missions=len(missions),corneus_missions=2,new_recipe=10634,target_headers=len(ids),before_hash=digest(before),after_hash=digest(after))))


if __name__=='__main__':main()
