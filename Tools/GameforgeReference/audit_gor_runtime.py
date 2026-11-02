"""Audit pinned and inherited Gor quests against a fresh SQL snapshot and the working client."""
import argparse
import collections
import hashlib
import json
from pathlib import Path

from audit_gor_quests import in_gor_unit,select_roots,quest_issues
from audit_quest_turn_in_npcs import load_reference
from client_records import quests
from reconstruction_client import navigation


def index(rows,field):
    result=collections.defaultdict(list)
    for r in rows:result[r[field]].append(r)
    return result


def completion_index(headers,terms):
    result=collections.defaultdict(list)
    for q in headers.values():
        if q['bType']==7:
            for mid in {t['dwTermID'] for t in terms[q['dwQuestID']] if t['bTermType']==1}:result[mid].append(q)
    return result


def local_scope(headers,navigation,npcs,reference_ids):
    gor={n['wID'] for n in npcs if n['bCountryID']==2 or in_gor_unit(dict(map_id=n['wMapID'],x=n['fPosX'],z=n['fPosZ']))}
    reasons=collections.defaultdict(set)
    for ident in reference_ids:reasons[ident].add('pinned_or_publisher_gor_root')
    for q in headers.values():
        if q['bType'] not in (8,18):continue
        ident=q['dwQuestID']
        if q['bTriggerType']==3 and q['dwTriggerID'] in gor:reasons[ident].add('existing_sql_gor_giver')
        if any(in_gor_unit(p) or p['npc_id'] in gor for p in navigation.get(ident,[])):reasons[ident].add('working_gor_navigation')
    return gor,reasons


def archived_reference(facts,manifest,source):
    """Use explicitly selected prior decoded facts without relabeling changed live files as pinned."""
    pins={p['path'].lower():p['sha256'] for p in manifest['files']}
    provenance=json.loads((facts/'evidence.json').read_text(encoding='utf8'))['files']
    recorded={p['path'].lower():p['sha256'] for p in provenance}
    needed={'tquest.mpq'}
    rows=json.loads((facts/'npcs.json').read_text(encoding='utf8'))
    needed.update(n['source_file'].lower() for n in rows)
    if any(recorded.get(p)!=pins.get(p) or p not in pins for p in needed):raise ValueError('Archived decoded facts do not identify the pinned source hashes')
    raw=(source/'TQNode.qpd').read_bytes()
    if hashlib.sha256(raw).hexdigest()!=pins['tqnode.qpd']:raise ValueError('Navigation raw input differs from pinned source')
    ref={q['dwQuestID']:q for q in json.loads((facts/'quests.json').read_text(encoding='utf8'))['quests']}
    placements=collections.defaultdict(list)
    for n in rows:placements[n['wID']].append(dict(n,source_file=n['source_file'].lower()))
    evidence=[dict(p,verification='archived_decoder_source_hash_matches_manifest_raw_not_available_here') for p in provenance if p['path'].lower() in needed]
    evidence.append(dict(path='TQNode.qpd',sha256=pins['tqnode.qpd'],verification='raw_hash_reverified'))
    for name in ['quests.json','npcs.json','evidence.json']:evidence.append(dict(path=str(facts/name),sha256=hashlib.sha256((facts/name).read_bytes()).hexdigest(),verification='archived_decoded_artifact_hash_recorded'))
    return ref,index(navigation(raw),'quest_id'),placements,evidence


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--source',type=Path,default=Path('C:/Program Files (x86)/4Story'))
    parser.add_argument('--game',type=Path,default=Path('Game'))
    parser.add_argument('--manifest',type=Path,default=Path('Gameforge/reference.json'))
    parser.add_argument('--catalogue',type=Path,default=Path('.runtime/gameforge-normalization/public/quest-catalogue.json'))
    parser.add_argument('--archived-facts',type=Path,help='Explicit prior decoded pinned facts; report raw re-verification limit')
    args=parser.parse_args()
    if args.output.exists():raise ValueError('Retain existing evidence; choose a fresh output')
    load=lambda name:json.loads((args.snapshot/(name+'.json')).read_text(encoding='utf8'))
    names=['TQUESTCHART','TQUESTTERMCHART','TQCONDITIONCHART','TQREWARDCHART','TNPCCHART','TITEMCHART','TMONSTERCHART','TMONSPAWNCHART','TMAPMONCHART','TQUESTITEMCHART','TSKILLCHART']
    tables={name:load(name) for name in names}
    manifest=json.loads(args.manifest.read_text(encoding='utf8'))
    ref,refnav,placements,pins=archived_reference(args.archived_facts,manifest,args.source) if args.archived_facts else load_reference(args.source,manifest)
    public=json.loads(args.catalogue.read_text(encoding='utf8'));_,reference_ids=select_roots(ref,refnav,placements,public['records'])
    headers={q['dwQuestID']:q for q in tables['TQUESTCHART']};npcs={q['wID']:q for q in tables['TNPCCHART']}
    terms=index(tables['TQUESTTERMCHART'],'dwQuestID');conditions=index(tables['TQCONDITIONCHART'],'dwQuestID');rewards=index(tables['TQREWARDCHART'],'dwQuestID')
    nav=index(navigation((args.game/'TQNode.qpd').read_bytes()),'quest_id');_,client_rows,_=quests((args.game/'TQuest.mpq').read_bytes(),False);client={q.key:q for q in client_rows}
    gor,reasons=local_scope(headers,nav,tables['TNPCCHART'],reference_ids)
    ends=completion_index(headers,terms);refends=completion_index(ref,collections.defaultdict(list,{k:q['terms'] for k,q in ref.items()}))
    items={r['wItemID'] for r in tables['TITEMCHART']};magic={r['dwID']:r for r in tables['TQUESTITEMCHART']};skills={r['wID'] for r in tables['TSKILLCHART']}
    monsters={r['wID']:r for r in tables['TMONSTERCHART']};kinds=index(tables['TMONSTERCHART'],'wKind');spawns={r['wID']:r for r in tables['TMONSPAWNCHART']}
    enabled=collections.defaultdict(list)
    for m in tables['TMAPMONCHART']:
        s=spawns.get(m['wSpawnID']);mon=monsters.get(m['wMonID'])
        if s and mon and s['bCount']>0 and s['bProb']>0 and m['bProb']>0:
            enabled[mon['wKind']].append(dict(monster_id=mon['wID'],spawn_id=s['wID'],event=s['bEvent'],map_id=s['wMapID'],x=s['fPosX'],y=s['fPosY'],z=s['fPosZ']))
    deps=dict(items=items,magic=set(magic),skills=skills,hunts={k:dict(templates=len(v),spawn_points=len(enabled[k])) for k,v in kinds.items()})
    producers=collections.defaultdict(list)
    for q in headers.values():
        if not q['bType']:continue
        for r in rewards[q['dwQuestID']]:
            item=r['dwRewardID'] if r['bRewardType']==2 else magic.get(r['dwRewardID'],{}).get('wItemID') if r['bRewardType']==7 else None
            if item:producers[item].append(dict(quest_id=q['dwQuestID'],type=q['bType'],trigger_type=q['bTriggerType'],trigger_id=q['dwTriggerID'],parent=q['dwParentID'],conditions=conditions[q['dwQuestID']]))
    rows=[];plans=[]
    for ident in sorted(reasons):
        q=headers.get(ident);c=client.get(ident);r=ref.get(ident);issues=[];notes=[];end=ends[ident];acquisition=[];hunt=[]
        if not q:issues.append('server_definition_missing')
        elif not q['bType']:issues.append('server_definition_disabled')
        if q and q['bType'] in (8,18):
            if not c:issues.append('working_client_definition_missing')
            else:
                if c.extra['type']!=q['bType']:issues.append('working_client_quest_type_differs')
                if sorted(c.extra['terms'])!=sorted((t['bTermType'],t['dwTermID'],t['bCount']) for t in terms[ident]):issues.append('working_client_objectives_differ')
                if sorted(c.extra['rewards'])!=sorted((t['bRewardType'],t['dwRewardID'],t['bCount'],t['bTakeMethod'],t['bTakeData']) for t in rewards[ident]):issues.append('working_client_rewards_differ')
            if q['bTriggerType']==3:
                giver=npcs.get(q['dwTriggerID'])
                if not giver:issues.append('giver_npc_missing')
                elif giver['fPosX']==giver['fPosY']==giver['fPosZ']==0:issues.append('giver_npc_zero_position')
            if q['bType']==8:
                if len(end)!=1:issues.append('completion_action_missing_or_multiple')
                for e in end:
                    if e['dwParentID']!=ident:issues.append('completion_parent_differs')
                    if e['bTriggerType']==3 and e['dwTriggerID'] not in npcs:issues.append('completion_npc_missing')
                    elif e['bTriggerType']==3 and all(npcs[e['dwTriggerID']][k]==0 for k in ('fPosX','fPosY','fPosZ')):issues.append('completion_npc_zero_position')
                    if e['dwQuestID'] not in client:issues.append('completion_action_missing_from_working_client')
            for condition in conditions[ident]:
                typ,target=condition['bConditionType'],condition['dwConditionID']
                if typ not in (0,1,2,3,4,5,7,8,9,10,11,12,13,14,15,16,17,18,19,20):issues.append('unsupported_condition_type')
                if typ in (3,15) and (target not in headers or not headers[target]['bType']):issues.append('required_quest_missing_or_disabled')
            for t in terms[ident]:
                typ,target=t['bTermType'],t['dwTermID']
                if typ in (2,5,17):
                    if target not in items:issues.append('objective_item_missing')
                    if typ==2:acquisition.append(dict(item=target,quest_producers=producers[target],status='quest_producer_found_requires_path_verification' if producers[target] else 'no_quest_producer_found_other_loot_shop_sources_not_excluded'))
                if typ==3:
                    hunt.append(dict(kind=target,enabled_spawn_slots=len(enabled[target]),normal_spawn_slots=sum(not s['event'] for s in enabled[target])))
                    if target not in kinds:issues.append('hunt_kind_missing')
                    elif not enabled[target]:issues.append('hunt_has_no_enabled_spawn_slot_requires_script_review')
                if typ not in (1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,21,28) and not(q['bType']==18 and typ==0):issues.append('objective_term_requires_engine_review')
            for reward in rewards[ident]:
                typ,target=reward['bRewardType'],reward['dwRewardID']
                if typ==2 and target not in items:issues.append('reward_item_missing')
                if typ==7 and (target not in magic or magic[target]['wItemID'] not in items):issues.append('reward_magic_item_missing')
                if typ in (3,4) and target not in skills:issues.append('reward_skill_missing')
            if q['bType']==8 and not nav[ident]:issues.append('working_navigation_group_missing')
            if any(n['x']==n['y']==n['z']==0 for n in nav[ident]):issues.append('working_navigation_has_zero_positions')
            for t in terms[ident]:
                if q['bType']==8 and t['bTermType'] in (2,3,13,16,17) and t['bCount'] and not any(n['type']==t['bTermType'] and n['term_id']==t['dwTermID'] for n in nav[ident]):issues.append('working_navigation_objective_marker_missing')
            # Only update existing giver/turn-in markers when the SQL/client mission identities
            # agree and the original pinned NPC placement exactly matches the live SQL NPC.
            reference_conflict=r and r['bType']==8 and (sorted((t['bTermType'],t['dwTermID'],t['bCount']) for t in r['terms'])!=sorted((t['bTermType'],t['dwTermID'],t['bCount']) for t in terms[ident]))
            if c and c.extra['type']==q['bType'] and 'working_client_objectives_differ' not in issues and not reference_conflict and nav[ident] and len(end)==1:
                after=[dict(n) for n in nav[ident]];changes=[]
                for typ,npcid in [(19,q['dwTriggerID'] if q['bTriggerType']==3 else 0),(20,end[0]['dwTriggerID'] if end[0]['bTriggerType']==3 else 0)]:
                    actual=npcs.get(npcid);original=[p for p in placements.get(npcid,[]) if actual and p['wMapID']==actual['wMapID'] and all(abs(p[k]-actual[k])<0.001 for k in ('fPosX','fPosY','fPosZ'))]
                    targets=[n for n in after if n['type']==typ]
                    if not original or len(targets)!=1 or all(original[0][k]==0 for k in ('fPosX','fPosY','fPosZ')):continue
                    p=original[0];n=targets[0];updated=dict(n,npc_id=npcid,map_id=p['wMapID'],x=p['fPosX'],y=p['fPosY'],z=p['fPosZ'])
                    if n['npc_id']!=npcid or n['map_id']!=p['wMapID'] or n['x']==n['y']==n['z']==0 or (n['x']-p['fPosX'])**2+(n['z']-p['fPosZ'])**2>25:after[after.index(n)]=updated;changes.append(dict(type=typ,npc=npcid,reference=p))
                if changes:plans.append(dict(schema=1,quest_id=ident,title=c.names[0],before_nodes=nav[ident],after_nodes=after,changes_evidence=changes,scope='Existing giver/turn-in markers only; unresolved objective markers remain reported.'))
        refissues=quest_issues(ident,ref,refnav,headers,terms,rewards,conditions,refends,deps) if ident in reference_ids else ['inherited_local_root_outside_active_pinned_scope']
        rows.append(dict(id=ident,title=c.names[0] if c and c.names[0] else r.get('title') if r else None,scope_reasons=sorted(reasons[ident]),server_type=q['bType'] if q else None,reference_type=r['bType'] if r else None,local_issues=sorted(set(issues)),reference_issues=refissues,giver=q['dwTriggerID'] if q and q['bTriggerType']==3 else None,completion_actions=[e['dwQuestID'] for e in end],completion_npcs=[e['dwTriggerID'] for e in end if e['bTriggerType']==3],terms=terms[ident],conditions=conditions[ident],rewards=rewards[ident],item_acquisition=acquisition,hunts=hunt,working_navigation=nav[ident],real_client_playthrough_verified=False))
    report=dict(schema=1,status='read_only_audit_not_full_playthrough',scope='Union of pinned/publisher Gor roots and all active SQL mission/recipe roots with a Gor giver or working Gor navigation; Gor includes Tebekut and Gor-country NPCs.',counts=dict(total_roots=len(rows),pinned_publisher_roots=len(reference_ids),active_local_missions=sum(r['server_type']==8 for r in rows),active_local_recipes=sum(r['server_type']==18 for r in rows),other_conflicting_root_types=sum(r['server_type'] not in (None,0,8,18) for r in rows),missing_pinned_roots=sum(r['server_type'] is None for r in rows),disabled_roots=sum(r['server_type']==0 for r in rows),local_issue_counts=dict(collections.Counter(i for r in rows for i in r['local_issues'])),reference_issue_counts=dict(collections.Counter(i for r in rows for i in r['reference_issues'])),safe_npc_navigation_plans=len(plans)),rows=rows,plans=plans,source_navigation_sha256=hashlib.sha256((args.game/'TQNode.qpd').read_bytes()).hexdigest(),source_quest_sha256=hashlib.sha256((args.game/'TQuest.mpq').read_bytes()).hexdigest(),snapshot_inputs=[dict(name=n,sha256=hashlib.sha256((args.snapshot/(n+'.json')).read_bytes()).hexdigest()) for n in names],original_inputs=pins,limitations=['Static template/graph integrity is not a full gameplay or persistence proof.','No quest producer found does not prove an item unobtainable; normal loot, shops, scripted and external-region sources need separate review.','No enabled spawn slot does not exclude quest/event/instance-controlled spawning.','Inherited active missions and approved substitutes remain distinct from publisher parity; no probabilities, spawns, rewards, event activation or player state changed.'])
    if args.archived_facts:report['limitations'].append('Installed official quest/NPC bytes differ from the pinned manifest. Prior decoded facts identify matching pinned source hashes; original quest/NPC raw bytes were unavailable for independent re-verification. Raw pinned navigation hash was reverified. No patch switch was made.')
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf8');print(json.dumps(report['counts'],ensure_ascii=False))


if __name__=='__main__':main()
