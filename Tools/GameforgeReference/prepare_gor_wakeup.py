"""Freeze the canonical wake-up repair while retaining the old switch action."""
import copy,json,sys
from pathlib import Path
sys.path.insert(0,'Tools/GameforgeReference')
from audit_quest_turn_in_npcs import load_reference
from prepare_gor_event_activation import digest,sql_values
from prepare_gor_reconstruction import query


def main():
    folder=Path('Gameforge/gor-content-repairs');dest=folder/'wakeup-plan.json';migration=Path('Database/migrations/056_gor_wakeup_quest.sql')
    assert not dest.exists() and not migration.exists()
    ref,nav,_,pins=load_reference(Path('C:/Program Files (x86)/4Story'),json.loads(Path('Gameforge/reference.json').read_text()))
    legacy=40007307;ids=f'7307,9720,{legacy}'
    groups=[('headers','TQUESTCHART','dwQuestID,dwParentID,bType,bForceRun,bTriggerType,dwTriggerID,bCountMax,bLevel,bMain,bConditionCheck'),('terms','TQUESTTERMCHART','dwQuestID,bTermType,dwTermID,bCount'),('conditions','TQCONDITIONCHART','dwQuestID,bConditionType,dwConditionID,bCount'),('rewards','TQREWARDCHART','dwQuestID,bRewardType,dwRewardID,bTakeMethod,bTakeData,bCount,dwQuestMob,dwQuestTime,dwQuestPathMob,dwTicketID,bSendQ')]
    snapshot='SELECT '+',\n'.join("JSON_QUERY(COALESCE((SELECT "+','.join('CONVERT(int,'+c+') AS '+c for c in cols.split(','))+f' FROM dbo.{table} WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({ids}) ORDER BY '+cols+" FOR JSON PATH),N'[]')) AS "+name for name,table,cols in groups)+' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;'
    before=query(snapshot);assert len(before['headers'])==1;h=before['headers'][0]
    assert h['dwQuestID']==7307 and h['bType']==13 and h['dwParentID']==0 and h['bTriggerType']==9 and h['dwTriggerID']==7125 and h['bCountMax']==0
    assert ref[7307]['bType']==8 and ref[7307]['unknown_prefix']==7243 and not ref[7307]['rewards']
    assert ref[9720]['bType']==7 and ref[9720]['unknown_prefix']==7307
    assert {p['npc_id'] for p in nav[7307] if p['type']==19}=={25223}
    assert {p['npc_id'] for p in nav[7307] if p['type']==20}=={26003}
    after=copy.deepcopy(before)
    for rows in after.values():
        for r in rows:r['dwQuestID']=legacy
    after['headers']+=[dict(dwQuestID=7307,dwParentID=7243,bType=8,bForceRun=0,bTriggerType=3,dwTriggerID=25223,bCountMax=h['bCountMax'],bLevel=ref[7307]['bLevel'],bMain=ref[7307]['bMain'],bConditionCheck=0),dict(dwQuestID=9720,dwParentID=7307,bType=7,bForceRun=0,bTriggerType=3,dwTriggerID=26003,bCountMax=0,bLevel=1,bMain=0,bConditionCheck=0)]
    after['terms'] += [dict(dwQuestID=i,**t) for i in (7307,9720) for t in ref[i]['terms']]
    after['conditions'].append(dict(dwQuestID=9720,bConditionType=3,dwConditionID=7307,bCount=0))
    for rows in after.values():rows.sort(key=lambda r:tuple(r.values()))
    plan=dict(schema=1,recipe_ids=[],aligned_mission_ids=[7307],extra_root_ids=[],before=before,after=after,before_hash=digest(before),after_hash=digest(after),snapshot_query=snapshot,
        legacy_switch_internal_id=legacy,source_mission=ref[7307],source_completion=ref[9720],original_inputs=pins,
        preservation='The inherited switch action triggered by completing 7125 is copied byte-for-byte semantically to internal ID 40007307. Only its identifier changes. No external references or saved progress may exist. The canonical no-reward mission retains existing cap 0; parent 7243 is required. No trial mechanics or portal rules are invented.',
        limitations='The inherited parent mission 7243 and full trial/portal journey still differ from the pinned client. This fixes the canonical delivery/NPC identity and preserves the legacy switch; it does not certify the whole trial.')
    state='SET @state=('+snapshot.removesuffix(';')+');\n'
    sql='''-- Restore the pinned wake-up delivery without losing the inherited switch action at the same ID.
    -- Evidence: Gameforge/gor-content-repairs/wakeup-plan.json.
    USE [TGame_gsp];
    GO
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @lock int;
    EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
    IF @lock<0 THROW 51056,'Quest import lock unavailable',1;
    IF EXISTS(SELECT 1 FROM dbo.TCharacterSaveQueue WITH(UPDLOCK,HOLDLOCK)) THROW 51056,'Pending character snapshots must drain',1;
    DECLARE @state nvarchar(max);
    '''+state+f"DECLARE @hash varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2);\nIF @hash NOT IN('{digest(before)}','{digest(after)}') THROW 51056,'Wake-up baseline differs',1;\nIF @hash='{digest(before)}'\nBEGIN\n"
    sql+=f'''IF EXISTS(SELECT 1 FROM dbo.TQUESTTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({ids})) OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMTABLE WITH(UPDLOCK,HOLDLOCK) WHERE dwQuestID IN({ids}))
     THROW 51056,'Saved legacy progress must be preserved',1;
    IF EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE bType<>0 AND dwQuestID NOT IN({ids}) AND (dwParentID IN({ids}) OR (bTriggerType IN(1,9) AND dwTriggerID IN({ids}))))
     OR EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c JOIN dbo.TQUESTCHART q ON q.dwQuestID=c.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN({ids}) AND c.bConditionType IN(3,15,18) AND c.dwConditionID IN({ids}))
     OR EXISTS(SELECT 1 FROM dbo.TQUESTTERMCHART t JOIN dbo.TQUESTCHART q ON q.dwQuestID=t.dwQuestID WHERE q.bType<>0 AND q.dwQuestID NOT IN({ids}) AND t.bTermType IN(1,18) AND t.dwTermID IN({ids}))
     THROW 51056,'Unexpected external reference to relocated action',1;
    IF NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=25223 AND wMapID=2010 AND bCountryID=3) OR NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26003 AND wMapID=0 AND bCountryID=3)
     OR NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE dwQuestID=7243 AND bType=8)
     THROW 51056,'Required NPC or parent mission missing',1;
    '''
    for name,table,_ in groups:
        rows=after[name]
        if not rows:continue
        cols=list(rows[0]);sql+='DECLARE @'+name+' TABLE('+','.join(c+' int' for c in cols)+');\nINSERT @'+name+' VALUES\n'+sql_values(rows)+';\n'
        sql+=f'DELETE dbo.{table} WHERE dwQuestID IN({ids});\n'
        if name=='headers':sql+='INSERT dbo.'+table+'('+','.join(cols)+') SELECT '+','.join(cols)+' FROM @'+name+';\n'
        else:
            sql+=f'DECLARE @{name}Base int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.{table} WITH(UPDLOCK,HOLDLOCK));\nINSERT dbo.{table}(dwID,'+','.join(cols)+f') SELECT @{name}Base+ROW_NUMBER() OVER(ORDER BY '+','.join(cols)+'),'+','.join(cols)+' FROM @'+name+';\n'
    sql+='END;\n'+state+f"IF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'{digest(after)}' THROW 51056,'Wake-up postcondition failed',1;\nCOMMIT TRANSACTION;\nGO\n"
    dest.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');migration.write_text(sql,encoding='utf-8');print('Prepared canonical wake-up and preserved switch action')


if __name__ == "__main__":
    main()
