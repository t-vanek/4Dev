"""Audit Anbari/Undred dependencies and import only closed, supported NPC-delivery quest chains."""
import argparse
import collections
import datetime
import hashlib
import json
import math
from pathlib import Path
import re
import sqlite3
import struct
import zlib

from catalogue import Tables, quest_facts
from client_facts import Reader, fixed, items, npcs, quests
from guarded_import import additions_sql
from identity_audit import normal


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('source', 'snapshot', 'public', 'cache', 'output', 'sql'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    schema = load(args.snapshot / 'schema.json')
    tables = {p.stem: load(p) for p in args.snapshot.glob('*.json') if p.stem not in ('schema', 'counts')}
    pin = {r['path'].lower(): r['sha256'] for r in load(Path('Gameforge/reference.json'))['files']}
    evidence = []

    def original(path):
        data = (args.source / path).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != pin[path.lower()]:
            raise ValueError('Changed original file: ' + path)
        evidence.append({'path': path, 'sha256': digest})
        return data

    source_quests = {r['dwQuestID']: r for r in quests(original('TQuest.mpq'))['quests']}
    nav = {}
    reader = Reader(original('TQNode.qpd'))
    for _ in range(reader.u32()):
        ident, count = reader.u32(), reader.u32()
        nav[ident] = [dict(zip(('type', 'term_id', 'monster_kind', 'npc_id', 'map_id', 'x', 'y', 'z'),
                              struct.unpack('<BIIIIfff', reader.take(29)))) for _ in range(count)]
    reader.done()
    region_units = {'Anbari': 0x0103, 'Undred': 0x0203}

    def unit(x, z):
        return math.floor(x / 1024) | (math.floor(z / 1024) << 8)

    source_npcs = []
    for name, ident in region_units.items():
        source_npcs += [{**n, 'region_name': name} for n in npcs(original(f'Tcd/TNPC{ident:08x}.tcd'), 0)]
    server_npcs = {r['wID']: r for r in tables['TNPCCHART']}
    for n in source_npcs:
        old = server_npcs.get(n['wID'])
        if old and (old['wMapID'] != 0 or any(abs(old[k] - n[k]) > 0.001 for k in ('fPosX', 'fPosY', 'fPosZ'))):
            raise ValueError('NPC registry no longer matches selected original: ' + str(n['wID']))
    candidates = {i: q for i, q in source_quests.items() if q['bType'] == 8 and q['bLevel'] >= 100
                  and any(p['map_id'] == 0 and unit(p['x'], p['z']) in region_units.values() for p in nav.get(i, []))}
    with sqlite3.connect(args.public / 'quest-details.sqlite') as db:
        published = {i: json.loads(zlib.decompress(b)) for i, b in db.execute("SELECT id,facts FROM details WHERE kind='quest'")}
    facts, public_evidence = {}, []
    for ident, q in candidates.items():
        if ident not in published:
            continue
        item = published[ident]
        data = (args.cache / (hashlib.sha256(item['url'].encode()).hexdigest() + '.html')).read_bytes()
        if hashlib.sha256(data).hexdigest() != item['sha256'] or normal(q['title']) != normal(item['name']):
            raise ValueError('Changed or disputed public mission identity: ' + str(ident))
        facts[ident] = quest_facts(data.decode('utf-8'))
        public_evidence.append({'quest_id': ident, 'url': item['url'], 'sha256': item['sha256']})
    eligible, rejected = {}, {}
    for ident, q in sorted(candidates.items()):
        points = nav[ident]
        giver = {p['npc_id'] for p in points if p['type'] == 19 and p['npc_id']}
        turnin = {p['npc_id'] for p in points if p['type'] == 20 and p['npc_id']}
        completes = [r for r in source_quests.values() if r['bType'] == 7 and r['unknown_prefix'] == ident
                     and r['terms'] == [{'bTermType': 1, 'dwTermID': ident, 'bCount': 0}]]
        reason = None
        if any(t['bTermType'] != 1 for t in q['terms']):
            reason = 'Combat, item, use-item or talk objective needs separate spawn/drop/interaction evidence.'
        elif len(giver) != 1 or len(turnin) != 1 or len(completes) != 1:
            reason = 'Non-unique giver, turn-in or completion record.'
        elif not giver | turnin <= server_npcs.keys():
            reason = 'Missing NPC dependency.'
        elif any(r['bRewardType'] not in (1, 6, 9) for r in q['rewards']):
            reason = 'Unsupported reward type in this delivery-only import.'
        elif ident in facts and (facts[ident]['giver_id'] not in giver or
              any(next((r['dwRewardID'] for r in q['rewards'] if r['bRewardType'] == typ), 0) != facts[ident][label]
                  for typ, label in ((1, 'money'), (6, 'experience')))):
            reason = 'Original/public giver or monetary/EXP rewards disagree.'
        if reason:
            rejected[ident] = reason
        else:
            eligible[ident] = {'mission': q, 'complete': completes[0], 'giver': next(iter(giver)), 'turnin': next(iter(turnin))}
    # A delivery quest must have a fully reachable imported parent, rather than leaving a dead chain in game.
    selected = {}
    while True:
        ready = {i: r for i, r in eligible.items() if i not in selected and
                 (r['mission']['unknown_prefix'] == 0 or r['mission']['unknown_prefix'] in selected)}
        if not ready:
            break
        selected.update(ready)
    for ident in eligible.keys() - selected.keys():
        rejected[ident] = 'Prerequisite mission is not in the closed supported delivery chain.'
    additions = collections.defaultdict(list)
    shop_evidence = []
    original_items = {r['wItemID']: r for r in items(original('Tcd/TItem.tcd'))}
    original_attrs = {r['wID']: r for r in fixed(original('Tcd/TItemAttr.tcd'), 'HBHHHHHHBB',
                      ('wID', 'bGrade', 'min_ap', 'max_ap', 'dp', 'min_map', 'max_map', 'mdp', 'block', 'speed'))}
    original_money = {r['level']: r['money'] for r in fixed(original('Tcd/TLevel.tcd'), 'BIII',
                      ('level', 'money', 'registration', 'search'))}
    server_items = {r['wItemID']: r for r in tables['TITEMCHART']}
    server_attrs = {r['wID']: r for r in tables['TITEMATTRCHART']}
    server_money = {r['bLevel']: r['dwMoney'] for r in tables['TLEVELCHART']}
    with sqlite3.connect(args.public / 'npc-details.sqlite') as db:
        npc_public = {i: json.loads(zlib.decompress(b)) for i, b in db.execute("SELECT id,facts FROM details WHERE kind='npc'")}

    def price(template, attrs, money):
        grade = attrs[template['wAttrID']]['bGrade'] if template['wAttrID'] else template['bLevel']
        return int(struct.unpack('<f', struct.pack('<f', money[grade] * template['fPrice']))[0] + .99)

    for n in source_npcs:
        if n['wID'] in server_npcs or n['bType'] != 2:
            continue
        pub = npc_public[n['wID']]
        if normal(pub['name']) != normal(n['name']):
            raise ValueError('Original/public shop name differs')
        raw = (args.cache / (hashlib.sha256(pub['url'].encode()).hexdigest() + '.html')).read_bytes()
        if hashlib.sha256(raw).hexdigest() != pub['sha256']:
            raise ValueError('Changed public shop evidence')
        parser = Tables()
        parser.feed(raw.decode('utf-8'))
        stock_tables = [t for t in parser.tables if t['class'] == 'detail_left' and
                        any('/item/details/' in link for row in t['rows'] for c in row for link in c['links'])]
        if len(stock_tables) != 1:
            raise ValueError('No unambiguous original shop stock')
        stock = []
        for cells in stock_tables[0]['rows']:
            ids = {int(m[1]) for c in cells for link in c['links'] if (m := re.search(r'/item/details/(\d+)/', link))}
            cost = re.fullmatch(r'(\d+)\s+(\d+)\s+(\d+)', cells[1]['text'])
            if len(ids) != 1 or not cost:
                raise ValueError('Unrecognized published shop row')
            ident = next(iter(ids))
            published_price = int(cost[1]) * 1_000_000 + int(cost[2]) * 1000 + int(cost[3])
            if price(original_items[ident], original_attrs, original_money) != published_price or \
               price(server_items[ident], server_attrs, server_money) != published_price:
                raise ValueError('Original/SQL/published shop price differs: ' + str(ident))
            stock.append({'item_id': ident, 'price': published_price})
        additions['TNPCCHART'].append({'wID': n['wID'], 'NC_szName2': n['title'], 'szName': n['name'],
            'bType': n['bType'], 'dwClass': n['class_index'], 'bCountryID': n['bCountryID'], 'wLocalID': 0,
            'bCondition': 0, 'bDiscountRate': 0, 'bAddProb': 0, 'wItemID': n['wItemID'], 'wMapID': n['wMapID'],
            'fPosX': n['fPosX'], 'fPosY': n['fPosY'], 'fPosZ': n['fPosZ'], 'szLocal': ''})
        additions['TNPCITEMCHART'] += [{'wNpcID': n['wID'], 'dwItemID': r['item_id']} for r in stock]
        shop_evidence.append({'npc_id': n['wID'], 'name': n['name'], 'url': pub['url'], 'sha256': pub['sha256'],
                              'stock': stock, 'compatibility_defaults': {'wLocalID': 0, 'bCondition': 0, 'bDiscountRate': 0, 'bAddProb': 0}})
    counters = {t: max((r['dwID'] for r in tables[t]), default=0) for t in ('TQUESTTERMCHART', 'TQCONDITIONCHART', 'TQREWARDCHART')}

    def add(table, row):
        if 'dwID' in {c['column_name'] for c in schema if c['table_name'] == table}:
            counters[table] += 1
            row = {'dwID': counters[table], **row}
        additions[table].append(row)

    existing_ids = {r['dwQuestID'] for r in tables['TQUESTCHART']}
    for ident, data in sorted(selected.items()):
        q, complete = data['mission'], data['complete']
        if ident in existing_ids or complete['dwQuestID'] in existing_ids:
            raise ValueError('Use the immutable pre-import snapshot')
        for src, npc_id in ((q, data['giver']), (complete, data['turnin'])):
            qid = src['dwQuestID']
            add('TQUESTCHART', {'dwQuestID': qid, 'dwParentID': src['unknown_prefix'], 'bType': src['bType'],
                'bForceRun': 0, 'bTriggerType': 3, 'dwTriggerID': npc_id, 'bCountMax': 1 if src is q else 0,
                'bLevel': src['bLevel'], 'bMain': src['bMain'], 'bConditionCheck': 0})
            for term in src['terms']:
                add('TQUESTTERMCHART', {'dwQuestID': qid, **term})
        add('TQCONDITIONCHART', {'dwQuestID': ident, 'bConditionType': 1,
                               'dwConditionID': max(q['bLevel'], q['min_level']), 'bCount': 0})
        if q['max_level']:
            add('TQCONDITIONCHART', {'dwQuestID': ident, 'bConditionType': 2, 'dwConditionID': q['max_level'], 'bCount': 0})
        for rw in q['rewards']:
            add('TQREWARDCHART', {'dwQuestID': ident, **rw, 'dwQuestMob': 0, 'dwQuestTime': 0,
                                'dwQuestPathMob': 0, 'dwTicketID': 0, 'bSendQ': 0})
    # The official world uses these two terrain units. Group/server/channel assignment is local infrastructure.
    for ident in region_units.values():
        if any(r['wMapID'] == 0 and r['wUnitID'] == ident for r in tables['TUNITCHART'] + tables['TCHANNELCHART']):
            raise ValueError('Unexpected pre-existing terrain-unit route')
        additions['TUNITCHART'].append({'bGroup': 1, 'bServerID': 1, 'wMapID': 0, 'wUnitID': ident})
        additions['TCHANNELCHART'].append({'bGroupID': 1, 'wMapID': 0, 'wUnitID': ident, 'bLogChannel': 1, 'bPhyChannel': 1})
    mon_kinds = sorted({p['monster_kind'] for i in candidates for p in nav[i] if p['monster_kind']})
    spawn_counts = {name: sum(r['wMapID'] == 0 and unit(r['fPosX'], r['fPosZ']) == ident for r in tables['TMONSPAWNCHART'])
                    for name, ident in region_units.items()}
    result = {'prepared_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'source_files': evidence,
              'public_sources': public_evidence, 'region_units': region_units,
              'verified_existing_npcs': [n for n in source_npcs if n['wID'] in server_npcs],
              'missing_service_npcs': [n for n in source_npcs if n['wID'] not in server_npcs],
              'shops_imported': shop_evidence,
              'candidates': list(candidates.values()), 'navigation': {str(i): nav[i] for i in candidates},
              'monster_kinds_referenced': mon_kinds, 'before_spawn_counts': spawn_counts,
              'supported_deliveries': list(selected.values()), 'deferred': [{'quest_id': i, 'reason': r} for i, r in sorted(rejected.items())],
              'additions': dict(additions), 'full_regions_playable': False,
              'reconstruction_policy': {'quest_trigger': 'Unique original navigation NPC, cross-checked against public giver when listed.',
                  'eligibility': 'One completion per delivery mission; original parent and explicit minimum, with displayed level as a conservative minimum. These eligibility defaults are reconstructed, not exported server conditions.',
                  'unused_reward_columns': 'Legacy private-server tracking metadata initialized to zero; ignored by the runtime loader.',
                  'rt_soul': 'Original type-9 rewards are retained, but the inherited native and .NET quest handlers do not implement them.',
                  'routes': 'Original terrain-unit IDs, assigned to this single local server/channel.',
                  'monsters': 'No monster is spawned from a quest guidance marker. Markers do not specify original population, variants, heights for every spawn, timers or drops.'}}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    args.sql.write_text(additions_sql(additions, schema, 'Gameforge/high-level-regions/plan.json',
                       {'TCHANNELCHART': ['bGroupID', 'wMapID', 'wUnitID', 'bLogChannel'],
                        'TNPCITEMCHART': ['wNpcID', 'dwItemID']}), encoding='utf-8')
    print(json.dumps({'candidate_missions': len(candidates), 'supported_delivery_missions': len(selected),
                      'verified_existing_npcs': sum(n['wID'] in server_npcs for n in source_npcs), 'deferred_missions': len(rejected),
                      'spawns_before': spawn_counts, 'rows_added': {t: len(r) for t, r in additions.items()}}))


if __name__ == '__main__':
    main()
