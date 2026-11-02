"""Prepare only Gor event missions whose NPC, objectives and rewards are present in pinned assets."""
import argparse
import copy
import hashlib
import json
from pathlib import Path

from audit_quest_turn_in_npcs import load_reference
from client_records import quests as quest_texts


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':'))


def digest(value):
    return hashlib.sha256(compact(value).encode('utf-16le')).hexdigest().upper()


def prepare(before, reference, texts, placements):
    selected = {233: ('Elyssa', 238), 234: ('Elyssa', 239), 235: ('Elyssa', 240),
                236: ('Elyssa', 241), 237: ('Elyssa', 242), 2398: ('Pivař', 2458)}
    by_id = {q['dwQuestID']: q for q in before['headers']}
    if set(by_id) != set(selected) | {c for _, c in selected.values()} or len(by_id) != len(before['headers']):
        raise ValueError('Unexpected activation snapshot identities')
    after = dict(headers=[], terms=[], conditions=[], rewards=[])
    evidence = []
    for ident, (name, completion) in selected.items():
        q, c = reference[ident], reference[completion]
        if q['bType'] != 8 or q['unknown_prefix'] != 0 or q['min_level'] or q['max_level']:
            raise ValueError('Mission identity/eligibility changed')
        if c['bType'] != 7 or c['unknown_prefix'] != ident or c['terms'] != [dict(bTermType=1, dwTermID=ident, bCount=0)]:
            raise ValueError('Completion identity changed')
        if texts[ident].names[6] != name:
            raise ValueError('Pinned quest NPC label differs')
        matches = {key: variants for key, variants in placements.items() if any(n['name'] == name and n['source_file'] == 'tcd/tnpc00000003.tcd' for n in variants)}
        if len(matches) != 1:
            raise ValueError('NPC identity is ambiguous')
        npc, variants = next(iter(matches.items()))
        if by_id[ident]['bType'] != 0 or by_id[ident]['bCountMax'] != 0:
            raise ValueError('Only disabled missions with the existing repeat cap are supported')
        if not q['rewards'] or any(t['bTermType'] != 2 for t in q['terms']) or any(r['bRewardType'] != 2 or r['bTakeMethod'] != 1 for r in q['rewards']):
            raise ValueError('Unsupported objective or incomplete reward contract')
        for key, parent, typ, level, main in ((ident, 0, 8, q['bLevel'], q['bMain']), (completion, ident, 7, c['bLevel'], c['bMain'])):
            after['headers'].append(dict(dwQuestID=key, dwParentID=parent, bType=typ, bForceRun=0, bTriggerType=3,
                dwTriggerID=npc, bCountMax=by_id[key]['bCountMax'], bLevel=level, bMain=main, bConditionCheck=0))
        for key, terms in ((ident, q['terms']), (completion, c['terms'])):
            after['terms'] += [dict(dwQuestID=key, **t) for t in terms]
        after['conditions'].append(dict(dwQuestID=completion, bConditionType=3, dwConditionID=ident, bCount=0))
        # Keep SQL projection order identical to snapshot-query.sql for the frozen state guard.
        for r in q['rewards']:
            after['rewards'].append(dict(dwQuestID=ident, bRewardType=r['bRewardType'], dwRewardID=r['dwRewardID'],
                bTakeMethod=r['bTakeMethod'], bTakeData=r['bTakeData'], bCount=r['bCount']))
        evidence.append(dict(id=ident, title=q['title'], npc_id=npc, npc_name=name, completion_id=completion,
                             npc_reference=variants, repeat_cap_retained=by_id[ident]['bCountMax']))
    for key, rows in after.items():
        # Objective field order in the reader is quest/type/id/count, not the asset's type/id/count.
        if key == 'terms':
            rows[:] = [dict(dwQuestID=r['dwQuestID'], bTermType=r['bTermType'], dwTermID=r['dwTermID'], bCount=r['bCount']) for r in rows]
        rows.sort(key=lambda r: tuple(r.values()))
    return after, evidence


def sql_values(rows):
    return ',\n'.join('(' + ','.join(str(v) for v in r.values()) + ')' for r in rows)


def migration(before, after, query):
    ids = ','.join(str(q['dwQuestID']) for q in after['headers'])
    scalar = query.strip().removesuffix(';').replace('SELECT JSON_QUERY', 'SELECT JSON_QUERY', 1)
    current = 'SET @state=(' + scalar + ');'
    body = []
    body.append('DECLARE @h TABLE(' + ','.join(k + ' int' for k in after['headers'][0]) + ');\nINSERT @h VALUES\n' + sql_values(after['headers']) + ';')
    for group, table in (('terms', 'TQUESTTERMCHART'), ('conditions', 'TQCONDITIONCHART'), ('rewards', 'TQREWARDCHART')):
        rows = after[group]
        body.append('DECLARE @' + group + ' TABLE(' + ','.join(k + ' int' for k in rows[0]) + ');\nINSERT @' + group + ' VALUES\n' + sql_values(rows) + ';')
        body.append('DELETE FROM dbo.' + table + ' WHERE dwQuestID IN(' + ids + ');')
        body.append('DECLARE @' + group + 'Base int=(SELECT COALESCE(MAX(dwID),0) FROM dbo.' + table + ' WITH(UPDLOCK,HOLDLOCK));')
        columns = list(rows[0])
        extras = ['dwQuestMob', 'dwQuestTime', 'dwQuestPathMob', 'dwTicketID', 'bSendQ'] if group == 'rewards' else []
        body.append('INSERT dbo.' + table + '(dwID,' + ','.join(columns + extras) + ') SELECT @' + group + 'Base+ROW_NUMBER() OVER(ORDER BY ' + ','.join(columns) + '),' + ','.join(columns) + (',0,0,0,0,0' if extras else '') + ' FROM @' + group + ';')
    cols = list(after['headers'][0])[1:]
    body.append('UPDATE q SET ' + ','.join(k + '=h.' + k for k in cols) + ' FROM dbo.TQUESTCHART q JOIN @h h ON q.dwQuestID=h.dwQuestID;')
    item_ids = sorted({r['dwTermID'] for r in after['terms'] if r['bTermType'] == 2} | {r['dwRewardID'] for r in after['rewards']})
    return """-- Restore six explicitly requested Gor event missions from pinned NPC labels, objectives and rewards.
-- Evidence: Gameforge/gor-quest-activation.json. This does not invent event drop probabilities.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;
DECLARE @lock int;
EXEC @lock=sys.sp_getapplock @Resource=N'4Dev.QuestTemplateImport',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
IF @lock<0 THROW 51052,'Quest template import lock unavailable',1;
DECLARE @state nvarchar(max);
""" + current + "\nDECLARE @hash varchar(64)=CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2);\n" + f"IF @hash NOT IN('{digest(before)}','{digest(after)}') THROW 51052,'Gor event quest baseline differs; inspect activation evidence',1;\n" + f"IF @hash='{digest(before)}'\nBEGIN\n" + """IF EXISTS(SELECT 1 FROM dbo.TQUESTTABLE WHERE dwQuestID IN(362,""" + ids + """) AND bTriggerCount>bCompleteCount)
    THROW 51052,'A legacy mission is still running; retain its progress before restoring identities',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE dwQuestID=362 AND bType=0)
    THROW 51052,'Reused completion 2458 still belongs to an active old mission',1;
IF EXISTS(SELECT 1 FROM dbo.TQUESTCHART WHERE bType<>0 AND bTriggerType IN(1,9) AND dwTriggerID IN(""" + ids + """))
    THROW 51052,'Unexpected legacy execution children would change the restored rewards',1;
IF NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=26567 AND wMapID=0 AND bCountryID=3)
 OR NOT EXISTS(SELECT 1 FROM dbo.TNPCCHART WHERE wID=20040 AND wMapID=0 AND bCountryID=3)
    THROW 51052,'Required Gor event NPC identity missing',1;
IF (SELECT COUNT(*) FROM dbo.TITEMCHART WHERE wItemID IN(""" + ','.join(map(str, item_ids)) + '))<>' + str(len(item_ids)) + """
    THROW 51052,'Required quest item template missing',1;
""" + '\n'.join(body) + '\nEND;\n' + current + f"\nIF CONVERT(varchar(64),HASHBYTES('SHA2_256',@state),2)<>'{digest(after)}' THROW 51052,'Gor activation postcondition failed',1;\nCOMMIT TRANSACTION;\nGO\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--query', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--migration', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.migration.exists():
        raise ValueError('Retain earlier plans; choose fresh output files')
    source = Path('C:/Program Files (x86)/4Story')
    reference, _, placements, inputs = load_reference(source, json.loads(Path('Gameforge/reference.json').read_text(encoding='utf-8')))
    _, records, _ = quest_texts((source / 'TQuest.mpq').read_bytes(), True)
    before = json.loads(args.snapshot.read_text(encoding='utf-8'))
    after, evidence = prepare(before, reference, {r.key: r for r in records}, placements)
    plan = dict(schema=1, requested_ids=[233,234,235,236,237,291,547,1082,1084,1203,1208,2398,3209],
                enabled_ids=[r['id'] for r in evidence], changes=evidence, before=before, after=after,
                before_hash=digest(before), after_hash=digest(after), snapshot_query=args.query.read_text(encoding='utf-8'),
                original_inputs=inputs, unsupported_ids=[291,547,1082,1084,1203,1208,3209])
    args.output.write_text(json.dumps(plan, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    args.migration.write_text(migration(before, after, plan['snapshot_query']), encoding='utf-8')
    print(json.dumps(dict(enabled=plan['enabled_ids'], deferred=plan['unsupported_ids'], rows={k:len(v) for k,v in after.items()})))


if __name__ == '__main__':
    main()
