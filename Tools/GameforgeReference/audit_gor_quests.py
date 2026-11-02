"""Read-only Gor/Tebekut quest coverage and blocking-dependency audit."""
import argparse
import collections
import hashlib
import html
import json
from pathlib import Path

from audit_quest_turn_in_npcs import load_reference, term_key


def in_gor_unit(point):
    return point.get('map_id') == 0 and 3072 <= point.get('x', -1) < 4096 and 0 <= point.get('z', -1) < 1024


def select_roots(reference, navigation, placements, catalogue):
    # TNPC00000003 is the pinned world unit containing Tebekut and the two training posts.
    gor_npcs = {ident for ident, variants in placements.items() if any(
        n['bCountryID'] == 2 or (n['source_file'].lower() == 'tcd/tnpc00000003.tcd'
        and in_gor_unit(dict(map_id=n['wMapID'], x=n['fPosX'], z=n['fPosZ']))) for n in variants)}
    reasons = collections.defaultdict(set)
    for ident, quest in reference.items():
        if quest['bType'] not in (8, 18):
            continue
        if any(in_gor_unit(p) or p['npc_id'] in gor_npcs for p in navigation.get(ident, [])):
            reasons[ident].add('pinned_gor_npc_or_navigation')
    for record in catalogue:
        if record['cells'][-1] == 'Tebekut':
            reasons[record['id']].add('publisher_tebekut_catalogue')
    return gor_npcs, reasons


def reward_key(rows):
    return sorted((r['bRewardType'], r['dwRewardID'], r['bCount'], r['bTakeMethod'], r['bTakeData']) for r in rows)


def quest_issues(ident, reference, navigation, server, terms, rewards, conditions, completions, dependencies):
    q = reference.get(ident)
    current = server.get(ident)
    issues = []
    if q is None:
        return ['absent_from_pinned_quest_file']
    if q['bType'] == 0:
        return ['disabled_in_pinned_reference']
    if current is None:
        issues.append('server_quest_missing')
    elif current['bType'] == 0:
        issues.append('server_quest_disabled')
    else:
        if current['bType'] != q['bType']:
            issues.append('quest_type_differs')
        if term_key(terms[ident]) != term_key(q['terms']):
            issues.append('objectives_differ')
        if current['dwParentID'] != q['unknown_prefix']:
            issues.append('parent_link_differs')
        if reward_key(rewards[ident]) != reward_key(q['rewards']):
            issues.append('reward_rows_differ')
        if any(c['bConditionType'] not in set(range(21)) - {6} for c in conditions[ident]):
            issues.append('unsupported_server_condition')
    parent = q['unknown_prefix']
    if parent and (parent not in server or server[parent]['bType'] == 0):
        issues.append('reference_parent_not_active_on_server')
    start = {p['npc_id'] for p in navigation.get(ident, []) if p['type'] == 19 and p['npc_id']}
    if not start:
        issues.append('start_npc_not_proven_by_navigation')
    elif len(start) != 1:
        issues.append('multiple_start_npcs_need_review')
    elif current and current['bType'] != 0 and (current['bTriggerType'] != 3 or current['dwTriggerID'] not in start):
        issues.append('start_npc_differs')
    if q['bType'] == 8:
        actions = completions[ident]
        end = {p['npc_id'] for p in navigation.get(ident, []) if p['type'] == 20 and p['npc_id']}
        if len(actions) != 1 or len(end) != 1:
            issues.append('completion_mapping_needs_more_evidence')
        else:
            expected = actions[0]
            actual = server.get(expected['dwQuestID'])
            if actual is None or actual['bType'] != 7:
                issues.append('completion_action_missing_or_wrong_type')
            else:
                if actual['dwParentID'] != ident or term_key(terms[actual['dwQuestID']]) != term_key(expected['terms']):
                    issues.append('completion_action_targets_another_quest')
                if actual['bTriggerType'] != 3 or actual['dwTriggerID'] not in end:
                    issues.append('completion_npc_differs')
    if any(r['bRewardType'] not in (1, 2, 3, 4, 6, 7, 9, 10)
           or (r['bRewardType'] in (9, 10) and r['bTakeMethod'] != 1) for r in q['rewards']):
        issues.append('reference_reward_contract_not_implemented')
    for t in q['terms']:
        if t['bTermType'] in (2, 5, 17) and t['dwTermID'] not in dependencies['items']:
            issues.append('objective_item_template_missing')
        if t['bTermType'] == 3:
            hunt = dependencies['hunts'].get(t['dwTermID'])
            if not hunt:
                issues.append('hunt_kind_missing')
            elif not hunt['spawn_points']:
                issues.append('hunt_kind_has_no_spawn_reference')
    for r in q['rewards']:
        keys = {2: 'items', 7: 'magic', 3: 'skills', 4: 'skills'}
        if r['bRewardType'] in keys and r['dwRewardID'] not in dependencies[keys[r['bRewardType']]]:
            issues.append('reward_template_missing')
    return sorted(set(issues))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=Path('C:/Program Files (x86)/4Story'))
    parser.add_argument('--manifest', type=Path, default=Path('Gameforge/reference.json'))
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--catalogue', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError('Choose fresh output; retain earlier evidence')
    ref, nav, placements, evidence = load_reference(args.source, json.loads(args.manifest.read_text(encoding='utf-8')))
    names = ('TQUESTCHART', 'TQUESTTERMCHART', 'TQCONDITIONCHART', 'TNPCCHART', 'TQREWARDCHART', 'ItemIds', 'MagicItemIds', 'SkillIds', 'HuntKinds')
    tables = {name: json.loads((args.snapshot / (name + '.json')).read_text(encoding='utf-8-sig')) for name in names}
    catalogue = json.loads(args.catalogue.read_text(encoding='utf-8-sig'))
    gor_npcs, roots = select_roots(ref, nav, placements, catalogue['records'])
    server = {q['dwQuestID']: q for q in tables['TQUESTCHART']}
    terms, conditions, rewards, completions = (collections.defaultdict(list) for _ in range(4))
    for name, index in (('TQUESTTERMCHART', terms), ('TQCONDITIONCHART', conditions), ('TQREWARDCHART', rewards)):
        for row in tables[name]:
            index[row['dwQuestID']].append(row)
    for q in ref.values():
        if q['bType'] == 7:
            for ident in {t['dwTermID'] for t in q['terms'] if t['bTermType'] == 1}:
                completions[ident].append(q)
    deps = {'items': {r['id'] for r in tables['ItemIds']}, 'magic': {r['id'] for r in tables['MagicItemIds']},
            'skills': {r['id'] for r in tables['SkillIds']}, 'hunts': {r['id']: r for r in tables['HuntKinds']}}
    public = {q['id']: q for q in catalogue['records']}
    rows = []
    for ident in sorted(roots):
        q = ref.get(ident, {})
        points = nav.get(ident, [])
        rows.append({'id': ident, 'name': q.get('title', public.get(ident, {}).get('name')), 'type': q.get('bType'),
                     'level': q.get('bLevel'), 'scope_evidence': sorted(roots[ident]),
                     'server_type': server.get(ident, {}).get('bType'), 'parent': q.get('unknown_prefix'),
                     'start_npcs': sorted({p['npc_id'] for p in points if p['type'] == 19 and p['npc_id']}),
                     'completion_npcs': sorted({p['npc_id'] for p in points if p['type'] == 20 and p['npc_id']}),
                     'reference_completion_actions': [a['dwQuestID'] for a in completions[ident]],
                     'issues': quest_issues(ident, ref, nav, server, terms, rewards, conditions, completions, deps),
                     'publisher_url': public.get(ident, {}).get('url'), 'client_playthrough_verified': False})
    local_actions = [q for q in server.values() if q['bTriggerType'] == 3 and q['dwTriggerID'] in gor_npcs]
    report = {'schema': 1, 'scope': 'All pinned Gor-area/Gor-NPC missions and recipes plus the publisher Tebekut catalogue, including seasonal content; read-only audit.',
              'scope_definition': 'Pinned TNPC00000003 world unit (map 0, x 3072..4096, z 0..1024) and pinned Gor-country NPCs (country 2); include any quest with a Gor navigation point/NPC and every publisher Tebekut catalogue row.',
              'counts': {'root_quests': len(rows), 'reference_missions': sum(r['type'] == 8 for r in rows),
                         'reference_recipes': sum(r['type'] == 18 for r in rows), 'gor_npcs': len(gor_npcs),
                         'sql_npc_actions': len(local_actions), 'missing_server_roots': sum(r['server_type'] is None for r in rows),
                         'disabled_server_roots': sum(r['server_type'] == 0 for r in rows),
                         'issue_counts': dict(collections.Counter(issue for r in rows for issue in r['issues']))},
              'rows': rows, 'sql_npc_actions': local_actions,
              'npc_assignment_repairs_already_deployed': [8884, 8886, 8888, 8894],
              'original_inputs': evidence,
              'snapshot_inputs': [{'name': n, 'sha256': hashlib.sha256((args.snapshot / (n + '.json')).read_bytes()).hexdigest()} for n in names],
              'publisher_catalogue': {'captured_utc': catalogue['captured_utc'], 'sha256': hashlib.sha256(args.catalogue.read_bytes()).hexdigest(), 'release_version': catalogue.get('release_version'), 'used_for_scope_only': True},
              'limitations': ['The catalogue has no established patch identity; never activate a quest from its listing alone.',
                             'Spawn references and template existence do not verify positions, drop probabilities, item acquisition, NPC interaction or a playable route.',
                             'No unknown conditions, rewards, spawn coordinates, repeat caps or missing quest actions are invented.',
                             'No player state is written by this audit; no complete Gor or Gameforge parity claim.']}
    args.output.mkdir(parents=True)
    (args.output / 'audit.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    table = ''.join('<tr>' + ''.join('<td>' + html.escape(str(v)) + '</td>' for v in
                    (r['id'], r['name'], r['level'], r['server_type'], ', '.join(map(str, r['completion_npcs'])), ', '.join(r['issues']))) + '</tr>' for r in rows)
    page = '<!doctype html><meta charset="utf-8"><title>Gor quest audit</title><style>body{font:15px system-ui;margin:30px}td,th{padding:7px;border:1px solid #ccc;text-align:left}table{border-collapse:collapse}</style><h1>Gor a Tebekut: kontrola questů</h1><p>Statická kontrola dat původního klienta a SQL. Zahrnuje běžné, výcvikové, výrobní i sezónní úkoly. Nenahrazuje odehrání ve skutečném klientovi.</p><pre>'
    page += html.escape(json.dumps(report['counts'], ensure_ascii=False, indent=2)) + '</pre><table><tr><th>ID</th><th>Úkol</th><th>Level</th><th>Typ v SQL</th><th>Cílová NPC podle klienta</th><th>Neshody / chybějící důkazy</th></tr>' + table + '</table>'
    (args.output / 'audit.html').write_text(page, encoding='utf-8')
    print(json.dumps(report['counts'], ensure_ascii=False))


if __name__ == '__main__':
    main()
