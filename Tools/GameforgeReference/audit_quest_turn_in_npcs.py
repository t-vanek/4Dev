"""Compare every server completion NPC with the pinned original quest/navigation records."""
import argparse
import collections
import hashlib
import html
import json
from pathlib import Path
import re
import struct

from client_facts import Reader, npcs, quests


def term_key(rows):
    return sorted((r['bTermType'], r['dwTermID'], r['bCount']) for r in rows)


def analyze(tables, reference, navigation):
    server = {r['dwQuestID']: r for r in tables['TQUESTCHART']}
    npc = {r['wID']: r for r in tables['TNPCCHART']}
    if len(server) != len(tables['TQUESTCHART']) or len(npc) != len(tables['TNPCCHART']):
        raise ValueError('Duplicate quest/NPC identities in SQL snapshot')
    terms, conditions = collections.defaultdict(list), collections.defaultdict(list)
    for r in tables['TQUESTTERMCHART']:
        terms[r['dwQuestID']].append(r)
    for r in tables['TQCONDITIONCHART']:
        conditions[r['dwQuestID']].append(r)
    sql_completions, ref_completions = collections.defaultdict(list), collections.defaultdict(list)
    for rows, index, get_terms in ((server, sql_completions, lambda q: terms[q['dwQuestID']]),
                                    (reference, ref_completions, lambda q: q['terms'])):
        for q in rows.values():
            if q['bType'] == 7:
                for mid in {t['dwTermID'] for t in get_terms(q) if t['bTermType'] == 1}:
                    index[mid].append(q['dwQuestID'])
    rows = []
    for q in sorted(server.values(), key=lambda q: q['dwQuestID']):
        if q['bType'] != 7:
            continue
        ident = q['dwQuestID']
        row = {'completion_id': ident, 'current_npc': q['dwTriggerID']}
        targets = [t['dwTermID'] for t in terms[ident] if t['bTermType'] == 1]
        reason = None
        if q['bTriggerType'] != 3:
            reason = 'not_npc_trigger'
        elif len(targets) != 1 or len(terms[ident]) != 1:
            reason = 'nonunique_completion_target'
        elif ident not in reference:
            reason = 'completion_absent_from_reference'
        else:
            mid = targets[0]
            ref = reference[ident]
            points = [p for p in navigation.get(mid, []) if p['type'] == 20 and p['npc_id']]
            ids = sorted({p['npc_id'] for p in points})
            row.update(mission_id=mid, title=reference.get(mid, {}).get('title'), reference_npcs=ids)
            if (ref['bType'] != 7 or term_key(ref['terms']) != term_key(terms[ident])
                    or ref['unknown_prefix'] != q['dwParentID'] or q['dwParentID'] != mid):
                reason = 'completion_graph_differs'
            elif mid not in reference or mid not in server or reference[mid]['bType'] != 8 or server[mid]['bType'] != 8:
                reason = 'mission_identity_differs'
            elif not ids:
                reason = 'no_reference_npc_marker'
            elif len(ids) != 1 or len({p['map_id'] for p in points}) != 1:
                reason = 'ambiguous_reference_npc'
            elif ids[0] not in npc or npc[ids[0]]['wMapID'] != points[0]['map_id']:
                reason = 'target_npc_missing_or_wrong_map'
            elif q['dwTriggerID'] == ids[0]:
                reason = 'npc_aligned'
            elif len(sql_completions[mid]) != 1 or len(ref_completions[mid]) != 1:
                reason = 'multiple_completion_actions'
            elif term_key(terms[mid]) != term_key(reference[mid]['terms']):
                reason = 'mission_objectives_differ'
            elif (q['bConditionCheck'] != 0 or len(conditions[ident]) != 1
                  or (conditions[ident][0]['bConditionType'], conditions[ident][0]['dwConditionID'], conditions[ident][0]['bCount']) != (3, mid, 0)):
                reason = 'completion_conditions_need_review'
            else:
                reason = 'verified_npc_mismatch'
                row.update(new_npc=ids[0], target_map=points[0]['map_id'],
                           target_country=npc[ids[0]]['bCountryID'], completion_header=q,
                           navigation=points, expected_terms={str(key): terms[key] for key in (mid, ident)})
        row['status'] = reason
        rows.append(row)
    return {'completion_actions': len(rows), 'counts': dict(collections.Counter(r['status'] for r in rows)), 'rows': rows}


def load_reference(source, manifest):
    pins = {r['path'].lower(): r['sha256'] for r in manifest['files']}
    evidence = []

    def read(relative):
        data = (source / relative).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if pins.get(relative.lower()) != digest:
            raise ValueError('Unpinned or changed original input: ' + relative)
        evidence.append({'path': relative, 'sha256': digest})
        return data

    records = quests(read('TQuest.mpq'))['quests']
    reference = {q['dwQuestID']: q for q in records}
    if len(reference) != len(records):
        raise ValueError('Duplicate quest identities in pinned reference')
    reader = Reader(read('TQNode.qpd'))
    navigation = collections.defaultdict(list)
    for _ in range(reader.u32()):
        ident, count = reader.u32(), reader.u32()
        for _ in range(count):
            navigation[ident].append(dict(zip(('type', 'term_id', 'monster_kind', 'npc_id', 'map_id', 'x', 'y', 'z'),
                                              struct.unpack('<BIIIIfff', reader.take(29)))))
    reader.done()
    placements = collections.defaultdict(list)
    for relative in sorted(pins):
        if not re.fullmatch(r'tcd/tnpc[0-9a-f]{8}\.tcd', relative):
            continue
        data = read(relative)
        if len(data) < 2:  # the original installation contains pinned empty unit files
            continue
        for n in npcs(data, int(Path(relative).stem[4:], 16) >> 16):
            placements[n['wID']].append({**n, 'source_file': relative})
    return reference, navigation, placements, evidence


def add_npc_evidence(report, tables, placements):
    server = {r['wID']: r for r in tables['TNPCCHART']}
    for row in report['rows']:
        for key, output in (('current_npc', 'current_npc_name'), ('new_npc', 'new_npc_name')):
            names = sorted({n['name'] for n in placements.get(row.get(key), [])})
            row[output] = ' / '.join(names) or server.get(row.get(key), {}).get('szName', '')
        if row['status'] != 'verified_npc_mismatch':
            continue
        variants = [n for n in placements.get(row['new_npc'], [])
                    if n['wMapID'] == row['target_map'] and n['bCountryID'] == row['target_country']]
        if not variants:
            row['status'] = 'target_npc_reference_identity_differs'
            continue
        row['npc_reference'] = variants
        actual = server[row['new_npc']]
        row['target_position_matches_reference'] = any(all(abs(n[k] - actual[k]) < 0.01 for k in ('fPosX', 'fPosY', 'fPosZ')) for n in variants)
    report['counts'] = dict(collections.Counter(r['status'] for r in report['rows']))


def migration_sql(report):
    changes = [r for r in report['rows'] if r['status'] == 'verified_npc_mismatch']
    if not changes:
        raise ValueError('No verified NPC changes to generate')
    values, term_rows = [], set()
    for r in changes:
        q = r['completion_header']
        values.append('(' + ','.join(map(str, (r['completion_id'], r['mission_id'], r['current_npc'], r['new_npc'],
                      r['target_map'], r['target_country'], q['bForceRun'], q['bCountMax'], q['bLevel'], q['bMain'], q['bConditionCheck']))) + ')')
        for ident, terms in r['expected_terms'].items():
            for typ, term, count in term_key(terms):
                term_rows.add((int(ident), typ, term, count))
    terms_sql = ',\n'.join('(' + ','.join(map(str, t)) + ')' for t in sorted(term_rows))
    return """-- Correct only verified completion NPC assignments; evidence: Gameforge/quest-turn-in-npcs.json.
-- TQNode.qpd SHA-256: d95510c95549d7303d75876e672abee799d8e635e189d981799760d67ac1f5d0
-- No mission links, objectives, rewards, NPC placement or character state are modified.
USE [TGame_gsp];
GO
SET XACT_ABORT ON;
SET NOCOUNT ON;
DECLARE @repair TABLE(CompletionId int PRIMARY KEY,MissionId int,OldNpc int,NewNpc int,MapId int,Country int,
    ForceRun int,CountMax int,Level int,Main int,ConditionCheck int);
INSERT @repair VALUES
""" + ',\n'.join(values) + ";\n" + """DECLARE @terms TABLE(QuestId int,TermType int,TermId int,TermCount int);
INSERT @terms VALUES
""" + terms_sql + ";\n" + """BEGIN TRANSACTION;
IF EXISTS(SELECT 1 FROM @repair r WHERE NOT EXISTS(
    SELECT 1 FROM dbo.TQUESTCHART q WITH(UPDLOCK,HOLDLOCK)
    WHERE q.dwQuestID=r.CompletionId AND q.dwParentID=r.MissionId AND q.bType=7 AND q.bTriggerType=3
      AND q.dwTriggerID IN(r.OldNpc,r.NewNpc) AND q.bForceRun=r.ForceRun AND q.bCountMax=r.CountMax
      AND q.bLevel=r.Level AND q.bMain=r.Main AND q.bConditionCheck=r.ConditionCheck))
    THROW 51050,'Completion quest baseline differs; inspect migration 050 evidence',1;
IF EXISTS(SELECT 1 FROM @repair r WHERE NOT EXISTS(
    SELECT 1 FROM dbo.TQUESTCHART q WITH(HOLDLOCK) WHERE q.dwQuestID=r.MissionId AND q.bType=8))
    THROW 51050,'Mission identity differs',1;
IF EXISTS(SELECT t.dwQuestID,t.bTermType,t.dwTermID,t.bCount FROM dbo.TQUESTTERMCHART t WITH(HOLDLOCK)
    WHERE t.dwQuestID IN(SELECT CompletionId FROM @repair UNION SELECT MissionId FROM @repair)
    EXCEPT SELECT QuestId,TermType,TermId,TermCount FROM @terms)
 OR EXISTS(SELECT QuestId,TermType,TermId,TermCount FROM @terms EXCEPT
    SELECT t.dwQuestID,t.bTermType,t.dwTermID,t.bCount FROM dbo.TQUESTTERMCHART t
    WHERE t.dwQuestID IN(SELECT CompletionId FROM @repair UNION SELECT MissionId FROM @repair))
 OR EXISTS(SELECT 1 FROM (SELECT DISTINCT QuestId FROM @terms) k WHERE
    (SELECT COUNT(*) FROM dbo.TQUESTTERMCHART t WHERE t.dwQuestID=k.QuestId)<>(SELECT COUNT(*) FROM @terms t WHERE t.QuestId=k.QuestId))
    THROW 51050,'Mission/completion objectives differ',1;
IF EXISTS(SELECT 1 FROM @repair r WHERE
    (SELECT COUNT(*) FROM dbo.TQCONDITIONCHART c WITH(HOLDLOCK) WHERE c.dwQuestID=r.CompletionId)<>1
    OR NOT EXISTS(SELECT 1 FROM dbo.TQCONDITIONCHART c WHERE c.dwQuestID=r.CompletionId
        AND c.bConditionType=3 AND c.dwConditionID=r.MissionId AND c.bCount=0))
    THROW 51050,'Completion conditions differ',1;
IF EXISTS(SELECT 1 FROM @repair r WHERE NOT EXISTS(
    SELECT 1 FROM dbo.TNPCCHART n WITH(HOLDLOCK) WHERE n.wID=r.NewNpc AND n.wMapID=r.MapId AND n.bCountryID=r.Country))
    THROW 51050,'Completion NPC is missing or has a different map/country',1;
UPDATE q SET dwTriggerID=r.NewNpc FROM dbo.TQUESTCHART q JOIN @repair r ON r.CompletionId=q.dwQuestID
WHERE q.dwTriggerID=r.OldNpc;
COMMIT TRANSACTION;
GO
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=Path('C:/Program Files (x86)/4Story'))
    parser.add_argument('--manifest', type=Path, default=Path('Gameforge/reference.json'))
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--migration', type=Path)
    args = parser.parse_args()
    if args.output.exists() or (args.migration and args.migration.exists()):
        raise ValueError('Retain prior evidence; choose fresh output/migration paths')
    names = ('TQUESTCHART', 'TQUESTTERMCHART', 'TQCONDITIONCHART', 'TNPCCHART')
    tables = {n: json.loads((args.snapshot / (n + '.json')).read_text(encoding='utf-8-sig')) for n in names}
    ref, nav, placements, evidence = load_reference(args.source, json.loads(args.manifest.read_text(encoding='utf-8')))
    report = analyze(tables, ref, nav)
    add_npc_evidence(report, tables, placements)
    report.update(schema=1, scope='All SQL QT_COMPLETE actions; NPC alignment is not full quest/playthrough certification.',
                  original_inputs=evidence,
                  snapshot_inputs=[{'table': n, 'sha256': hashlib.sha256((args.snapshot / (n + '.json')).read_bytes()).hexdigest()} for n in names])
    args.output.mkdir(parents=True)
    (args.output / 'audit.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    changes = [r for r in report['rows'] if r['status'] == 'verified_npc_mismatch']
    rows = ''.join('<tr>' + ''.join('<td>' + html.escape(str(v)) + '</td>' for v in
                   (r['mission_id'], r['completion_id'], r['title'], str(r['current_npc']) + ' ' + r['current_npc_name'],
                    str(r['new_npc']) + ' ' + r['new_npc_name'])) + '</tr>' for r in changes)
    review = ''.join('<tr>' + ''.join('<td>' + html.escape(str(v)) + '</td>' for v in
                     (r['completion_id'], r.get('mission_id', ''), r.get('title', ''), r['status'])) + '</tr>'
                     for r in report['rows'] if r['status'] not in ('verified_npc_mismatch', 'npc_aligned'))
    page = '<!doctype html><meta charset="utf-8"><title>Quest completion NPC audit</title><style>body{font:16px system-ui;margin:32px;max-width:1400px}td,th{padding:8px;border:1px solid #ddd;text-align:left}table{border-collapse:collapse}code{font-size:13px}</style><h1>Audit NPC pro odevzdání úkolů</h1>'
    page += '<p>Kontrolováno dokončovacích akcí: ' + str(report['completion_actions']) + '. Shoda NPC neznamená ověření celého úkolu ve hře.</p><pre>' + html.escape(json.dumps(report['counts'], indent=2)) + '</pre>'
    page += '<h2>Jednoznačně doložená chybná přiřazení</h2><table><tr><th>Mise</th><th>Dokončení</th><th>Název</th><th>Před</th><th>Podle klienta</th></tr>' + rows + '</table>'
    page += '<h2>Případy vyžadující další důkazy nebo opravu návazností</h2><table><tr><th>Dokončení</th><th>Mise</th><th>Název</th><th>Důvod</th></tr>' + review + '</table>'
    (args.output / 'audit.html').write_text(page, encoding='utf-8')
    if args.migration:
        args.migration.write_text(migration_sql(report), encoding='utf-8')
    print(json.dumps({'completion_actions': report['completion_actions'], 'counts': report['counts'], 'output': str(args.output)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
