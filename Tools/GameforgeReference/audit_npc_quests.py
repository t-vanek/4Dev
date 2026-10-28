"""Audit installed NPCs and quest references against pinned client records; never write gameplay data."""
import argparse
import collections
import datetime
import hashlib
import html
import json
import math
from pathlib import Path
import re
import struct

from client_facts import npcs, quests
from client_records import quests as current_quests
from identity_audit import normal


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError('Choose a fresh audit directory; retain previous evidence.')
    pins = {r['path'].lower(): r['sha256'] for r in load(Path('Gameforge/reference.json'))['files']}
    evidence, client_evidence = {}, {}

    def original(relative):
        path = args.source / relative
        value = digest(path)
        if value != pins[relative.lower()]:
            raise ValueError('Changed pinned input: ' + relative)
        evidence[relative] = value
        return path.read_bytes()

    placements, client_placements = collections.defaultdict(list), collections.defaultdict(list)
    for path in sorted((args.source / 'Tcd').glob('TNPC*.tcd')):
        match = re.fullmatch(r'TNPC([0-9a-fA-F]{8})\.tcd', path.name)
        if not match or path.stat().st_size < 2:
            continue
        relative = 'Tcd/' + path.name
        map_id = int(match[1], 16) >> 16
        for row in npcs(original(relative), map_id):
            placements[row['wID']].append({**row, 'source_file': relative})
        current = args.client / relative
        if current.exists() and current.stat().st_size >= 2:
            client_evidence[relative] = digest(current)
            for row in npcs(current.read_bytes(), map_id):
                client_placements[row['wID']].append(row)
    oq = {q['dwQuestID']: q for q in quests(original('TQuest.mpq'))['quests']}
    _, current_rows, _ = current_quests((args.client / 'TQuest.mpq').read_bytes(), False)
    client_evidence['TQuest.mpq'] = digest(args.client / 'TQuest.mpq')
    cq = {r.key: r for r in current_rows}
    tables = {p.stem: load(p) for p in args.snapshot.glob('T*.json')}
    server = {r['wID']: r for r in tables['TNPCCHART']}
    sq = {r['dwQuestID']: r for r in tables['TQUESTCHART']}
    active = {k: q for k, q in sq.items() if q['bType'] != 0}
    ids = {
        'item': {r['wItemID'] for r in tables['TITEMCHART']},
        'skill': {r['wID'] for r in tables['TSKILLCHART']},
        'map': {r['wMapID'] for r in tables['TMAPCHART']},
        'spawn': {r['wID'] for r in tables['TMONSPAWNCHART']},
        'portal': {r['wPortalID'] for r in tables['TPORTALCHART']},
        'return': {r['wID'] for r in tables['TSPAWNPOSCHART']},
    }
    issues, differences, comparisons, current_comparisons = [], [], [], []

    def issue(code, domain, ident, detail, severity='dependency'):
        issues.append({'code': code, 'domain': domain, 'id': ident, 'detail': detail, 'severity': severity})

    fields = {'bType': 'bType', 'dwClass': 'class_index', 'bCountryID': 'bCountryID', 'wItemID': 'wItemID',
              'wMapID': 'wMapID', 'fPosX': 'fPosX', 'fPosY': 'fPosY', 'fPosZ': 'fPosZ'}

    def position_key(row):
        return tuple(row[k] for k in ('name', 'bType', 'class_index', 'bCountryID', 'wItemID', 'wMapID', 'fPosX', 'fPosY', 'fPosZ'))

    for ident, n in server.items():
        if n['bCountryID'] not in range(5):
            issue('invalid_npc_country', 'npc', ident, n['bCountryID'], 'error')
        if any(not math.isfinite(n[k]) or abs(n[k]) > 1e7 for k in ('fPosX', 'fPosY', 'fPosZ')):
            issue('invalid_npc_position', 'npc', ident, {k: n[k] for k in ('fPosX', 'fPosY', 'fPosZ')}, 'error')
        current_variants = {position_key(r): r for r in client_placements.get(ident, []) if any(r[k] != 0 for k in ('fPosX', 'fPosY', 'fPosZ'))}
        if len(current_variants) == 1:
            actual = next(iter(current_variants.values()))
            current_diff = {}
            for column, key in fields.items():
                same = struct.pack('<f', n[column]) == struct.pack('<f', actual[key]) if column.startswith('fPos') else n[column] == actual[key]
                if not same:
                    current_diff[column] = {'server': n[column], 'client': actual[key]}
            current_comparisons.append({'id': ident, 'name': actual['name'], 'differences': current_diff,
                                        'map_id': actual['wMapID'], 'country': actual['bCountryID'],
                                        'x': actual['fPosX'], 'y': actual['fPosY'], 'z': actual['fPosZ']})
        variants = {position_key(r): r for r in placements.get(ident, []) if any(r[k] != 0 for k in ('fPosX', 'fPosY', 'fPosZ'))}
        if len(variants) != 1:
            issue('no_unique_original_placement', 'npc', ident, {'variants': len(variants)}, 'review')
            continue
        source = next(iter(variants.values()))
        current_matches = any(position_key(r) == position_key(source) for r in client_placements.get(ident, []))
        diff = {}
        for column, key in fields.items():
            same = struct.pack('<f', n[column]) == struct.pack('<f', source[key]) if column.startswith('fPos') else n[column] == source[key]
            if not same:
                diff[column] = {'server': n[column], 'client': source[key]}
        comparison = {'id': ident, 'name': source['name'], 'server_name': n['szName'], 'source_file': source['source_file'],
                      'current_client_matches': current_matches, 'name_matches': normal(n['szName']) == normal(source['name']), 'differences': diff}
        comparisons.append(comparison)
        if diff:
            differences.append(comparison)
        if not current_matches:
            issue('current_npc_differs_from_original', 'npc', ident, source['source_file'], 'review')

    quest_relations = collections.defaultdict(list)
    for q in active.values():
        ident = q['dwQuestID']
        if q['dwParentID'] and q['dwParentID'] not in sq:
            issue('missing_parent_quest', 'quest', ident, q['dwParentID'])
        if q['bTriggerType'] == 3:
            quest_relations[q['dwTriggerID']].append(ident)
            if q['dwTriggerID'] not in server:
                issue('missing_quest_npc', 'quest', ident, q['dwTriggerID'])
            elif q['dwTriggerID'] not in client_placements:
                issue('quest_npc_missing_from_client_units', 'quest', ident, q['dwTriggerID'])
        if q['bTriggerType'] in (1, 9) and q['dwTriggerID'] not in sq:
            issue('missing_trigger_quest', 'quest', ident, q['dwTriggerID'])
        if ident not in cq:
            issue('active_quest_missing_from_client', 'quest', ident, q['bType'])
        elif cq[ident].extra['type'] != q['bType']:
            issue('active_quest_type_differs_from_client', 'quest', ident, {'server': q['bType'], 'client': cq[ident].extra['type']}, 'review')
        if q['bType'] == 17:
            issue('unimplemented_send_post_quest', 'quest', ident, 'SendPost has no dedicated runtime handler', 'implementation')
        seen, current = set(), ident
        while current in sq and sq[current]['dwParentID']:
            if current in seen:
                issue('quest_parent_cycle', 'quest', ident, sorted(seen), 'error')
                break
            seen.add(current)
            current = sq[current]['dwParentID']

    condition_counts = collections.Counter()
    for row in tables['TQCONDITIONCHART']:
        if row['dwQuestID'] not in active:
            continue
        kind, value, ident = row['bConditionType'], row['dwConditionID'], row['dwQuestID']
        condition_counts[kind] += 1
        if kind in (3, 15, 18) and value not in sq:
            issue('missing_condition_quest', 'quest', ident, value)
        if kind in (4, 13) and value and value not in ids['item']:
            issue('missing_condition_item', 'quest', ident, value)
        if kind == 7 and value not in ids['map']:
            issue('missing_condition_map', 'quest', ident, value)
        if kind == 19 and value not in ids['skill']:
            issue('missing_condition_skill', 'quest', ident, value)
        if kind == 6:
            issue('unimplemented_monster_condition', 'quest', ident, value, 'implementation')
    for row in tables['TQUESTTERMCHART']:
        if row['dwQuestID'] not in active:
            continue
        kind, value, ident = row['bTermType'], row['dwTermID'], row['dwQuestID']
        if kind in (1, 18) and value not in sq:
            issue('missing_objective_quest', 'quest', ident, value)
        if kind in (2, 5, 17) and value and value not in ids['item']:
            issue('missing_objective_item', 'quest', ident, value)
        if kind == 4 and value and value not in ids['skill']:
            issue('missing_objective_skill', 'quest', ident, value)
        if kind == 13 and value not in server:
            issue('missing_talk_objective_npc', 'quest', ident, value)
        if kind in (16, 21) and value and value not in ids['spawn']:
            issue('missing_objective_spawn', 'quest', ident, value)
    for row in tables['TQREWARDCHART']:
        if row['dwQuestID'] not in active:
            continue
        kind, value, ident = row['bRewardType'], row['dwRewardID'], row['dwQuestID']
        if kind == 2 and value and value not in ids['item']:
            issue('missing_reward_item', 'quest', ident, value)
        if kind == 7:
            issue('unimplemented_magic_item_reward', 'quest', ident, value, 'implementation')
        if kind in (3, 4, 5, 8, 9, 10):
            issue('unimplemented_direct_reward_type', 'quest', ident, kind, 'implementation')
    stock = collections.defaultdict(list)
    for row in tables['TNPCITEMCHART']:
        stock[row['wNpcID']].append(row['dwItemID'])
    for ident, n in server.items():
        domain = {1: 'skill', 19: 'skill', 2: 'item', 21: 'item', 8: 'portal', 14: 'return'}.get(n['bType'])
        if domain:
            for value in stock[ident]:
                if value not in ids[domain]:
                    issue('missing_npc_' + domain + '_link', 'npc', ident, value)
    missing_original = [{'id': ident, 'name': rows[0]['name']} for ident, rows in placements.items() if ident not in server]
    summary = {'server_npcs': len(server), 'server_quests': len(sq), 'active_quests': len(active),
               'original_quest_records': len(oq), 'current_quest_records': len(cq),
               'unambiguous_npc_comparisons': len(comparisons), 'npc_field_differences': len(differences),
               'unambiguous_current_npc_comparisons': len(current_comparisons),
               'server_current_npc_differences': sum(bool(r['differences']) for r in current_comparisons),
               'missing_original_npcs': len(missing_original), 'npc_ids_with_active_quests': len(quest_relations),
               'issues_by_code': dict(collections.Counter(r['code'] for r in issues))}
    report = {'checked_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'summary': summary,
              'source_files': evidence, 'current_client_files': client_evidence,
              'snapshot_sha256': {p.name: digest(p) for p in args.snapshot.glob('*.json')},
              'current_npc_comparisons': current_comparisons,
              'npc_comparisons': comparisons, 'npc_differences': differences, 'missing_original_npcs': missing_original,
              'active_npc_quest_links': dict(quest_relations), 'issues': issues,
              'limits': ['Static reference checks do not establish quest playability or complete spawn/drop rules.',
                         'Source/SQL field differences need review; no repair is applied by this audit.',
                         'Spawn term 21 is checked against spawn instances, never monster templates.',
                         'NPC positions with multiple distinct original placements remain unresolved.']}
    args.output.mkdir(parents=True)
    (args.output / 'audit.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    rows = ''.join('<tr><td>' + '</td><td>'.join(html.escape(str(r[k])) for k in ('severity', 'code', 'domain', 'id', 'detail')) + '</td></tr>' for r in issues)
    doc = '<!doctype html><meta charset="utf-8"><title>Kontrola NPC a questů</title><style>body{font:16px system-ui;max-width:1400px;margin:3rem auto;padding:0 2rem;color:#192330}table{border-collapse:collapse;width:100%;font-size:13px}td,th{padding:9px;border:1px solid #d8dee5;text-align:left}th{background:#eaf0f6}pre{background:#f3f6fa;padding:20px;white-space:pre-wrap}h1{color:#123c62}</style>'
    doc += '<h1>Kontrola NPC a questů</h1><p>Porovnání serverových tabulek s připnutým původním klientem a současným klientem. Statické nálezy vyžadují kontrolu; tento audit nemění herní data.</p><pre>' + html.escape(json.dumps(summary, ensure_ascii=False, indent=2)) + '</pre>'
    doc += '<h2>Nálezy a závislosti</h2><table><thead><tr><th>Kategorie</th><th>Nález</th><th>Typ</th><th>ID</th><th>Podrobnosti</th></tr></thead><tbody>' + rows + '</tbody></table>'
    (args.output / 'report.html').write_text(doc, encoding='utf-8')
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == '__main__':
    main()
