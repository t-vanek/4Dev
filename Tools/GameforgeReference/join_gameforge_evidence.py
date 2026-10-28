"""Join original client, public Gameforge evidence and a read-only server snapshot by typed IDs."""
import argparse
import collections
import datetime
import hashlib
from html import escape
from html.parser import HTMLParser
import json
import os
from pathlib import Path
import re
import sqlite3
import struct

from analyze_gameforge_sources import Chain
from catalogue import Tables, listing, quest_facts
from client_facts import items, npcs, quests
from client_records import named
from identity_audit import normal
from monsters import bounds, text


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value, compact=False):
    path.write_text(json.dumps(value, ensure_ascii=False,
                               **({'separators': (',', ':')} if compact else {'indent': 2})) + '\n', encoding='utf-8')


class Objectives(HTMLParser):
    """Keep nested loot-source links with their outer objective, without inventing item IDs."""
    def __init__(self, markup):
        super().__init__(convert_charrefs=True)
        self.depth, self.current, self.records = 0, None, []
        section = re.search(r'id="tabs-1"(.*?)(?=<div[^>]+id="tabs-2")', markup, re.S)
        right = section[1].split('<div class="detail_right">', 1) if section else []
        if len(right) == 2:
            self.feed(re.split(r'<hr\s*/?>', right[1], maxsplit=1)[0])

    def handle_starttag(self, tag, attrs):
        if tag == 'li':
            self.depth += 1
            if self.depth == 1:
                self.current = {'chunks': [], 'entity_links': []}
        elif tag == 'a' and self.current is not None:
            m = re.search(r'/(monster|npc|item)/details/(\d+)', dict(attrs).get('href', ''))
            if m:
                link = {'kind': m[1], 'id': int(m[2])}
                if link not in self.current['entity_links']:
                    self.current['entity_links'].append(link)

    def handle_data(self, value):
        if self.current is not None:
            self.current['chunks'].append(value)

    def handle_endtag(self, tag):
        if tag != 'li' or not self.depth:
            return
        if self.depth == 1:
            row = self.current
            row['text'] = ' '.join(' '.join(row.pop('chunks')).split())
            row['action'] = ('collect' if row['text'].startswith('Kořist ') else
                             'hunt' if row['text'].startswith('Zlikvidovat ') else
                             'use' if row['text'].startswith('Použít ') else
                             'talk' if row['text'].startswith(('Mluvit ', 'Promluvit ')) else 'other')
            count = re.search(r'(\d+)\s*×|×\s*(\d+)', row['text'])
            row['published_count'] = int(count[1] or count[2]) if count else None
            if row['action'] == 'collect':
                row['item_name_without_published_id'] = re.sub(r'^Kořist\s+\d+\s*×\s*', '', row['text']).split('(', 1)[0].strip()
            self.records.append(row)
            self.current = None
        self.depth -= 1


class PublicEvidence:
    def __init__(self, public, cache, fresh, fresh_cache, extra):
        self.public, self.cache = public, cache
        self.sources, self.overrides, self.markup = {}, {}, {}
        self.catalogues, self.catalogue_evidence, self.unavailable = {}, {}, {}
        for kind in ('quest', 'npc', 'monster', 'item'):
            cat = load(public / (kind + '-catalogue.json'))
            records = {r['id']: r for r in cat['records']}
            found = {}
            for page in cat['pages']:
                path = cache / (hashlib.sha256(page['url'].encode()).hexdigest() + '.html')
                if digest(path) != page['sha256']:
                    raise ValueError('Public catalogue capture changed: ' + str(path))
                parsed, total = listing(path.read_text(encoding='utf-8'), 'https://cz.4story.gameforge.com', kind)
                if total != len(records):
                    raise ValueError('Public catalogue total does not reconcile')
                for row in parsed:
                    if row['id'] in found:
                        raise ValueError('Repeated public catalogue ID')
                    found[row['id']] = row
            if found.keys() != records.keys() or any(normal(r['name']) != normal(found[i]['name']) for i, r in records.items()):
                raise ValueError('Saved catalogue rows do not match captured pages')
            self.catalogues[kind] = records
            self.catalogue_evidence[kind] = {'source': cat['source'], 'captured_utc': cat.get('captured_utc'),
                                             'release_version': cat.get('release_version'), 'records_verified': len(records),
                                             'pages': cat['pages']}
        self.stores = {kind: sqlite3.connect('file:' + (public / (kind + '-details.sqlite')).resolve().as_posix() + '?mode=ro', uri=True)
                       for kind in ('quest', 'npc', 'item')}
        self.stores['monster'] = sqlite3.connect('file:' + (public / 'monster-variants.sqlite').resolve().as_posix() + '?mode=ro', uri=True)
        for r in load(fresh):
            if r['kind'] != 'catalogue':
                self.overrides[(r['kind'], r['id'])] = (r['url'], r['sha256'], fresh_cache / r['cache_file'])
        for f in sorted(extra.glob('*.json')):
            m = load(f)
            match = re.fullmatch(r'https://cz\.4story\.gameforge\.com/(quest|monster|npc|item)/details/(\d+)', m.get('url', ''))
            if match:
                self.overrides[(match[1], int(match[2]))] = (m['url'], m['sha256'], f.with_suffix('.html'))

    def detail(self, kind, ident, required=True):
        key = f'{kind}:{ident}'
        if key in self.sources:
            return self.sources[key], self.markup[key]
        row = self.overrides.get((kind, ident))
        if row is None:
            record = self.stores[kind].execute('SELECT url,sha256 FROM details WHERE kind=? AND id=?', (kind, ident)).fetchone()
            if record:
                row = (*record, self.cache / (hashlib.sha256(record[0].encode()).hexdigest() + '.html'))
        if row is None:
            if required:
                raise ValueError('No captured public entity page: ' + key)
            self.unavailable[key] = {'reason': 'no_captured_entity_detail'}
            return None, None
        url, sha, path = row
        if digest(path) != sha:
            raise ValueError('Public capture hash mismatch: ' + str(path))
        h = path.read_text(encoding='utf-8')
        match = re.search(r'<h1\b[^>]*>(.*?)</h1>', h, re.S)
        heading = text(match[1]) if match else ''
        if not heading or normal(heading) in {normal(s) for s in ('NPC v Iberii', 'Nestvůry v Iberii', 'Úkoly v Iberii', 'Předměty v Iberii')}:
            if not required:
                self.unavailable[key] = {'reason': 'captured_response_is_not_an_entity_detail', 'url': url,
                                         'sha256': sha, 'cache_file': path.as_posix(), 'heading': heading}
                return None, None
            raise ValueError('Expected entity page, received a landing page: ' + url)
        metadata = load(path.with_suffix('.json')) if path.with_suffix('.json').exists() else {}
        if metadata and (metadata.get('sha256') != sha or metadata.get('url') != url):
            raise ValueError('Public capture metadata mismatch: ' + url)
        source = {'kind': kind, 'id': ident, 'url': url, 'heading': heading, 'sha256': sha,
                  'cache_file': path.as_posix(), 'retrieved_utc': metadata.get('retrieved_utc')}
        self.sources[key], self.markup[key] = source, h
        return source, h


def compare(client, publisher):
    if publisher is None:
        return 'not_published'
    if client is None:
        return 'missing_in_client'
    return 'match' if client == publisher else 'conflict'


def check_objective(row, q, navigation, client_items):
    terms = q['terms']
    ids = {kind: {link['id'] for link in row['entity_links'] if link['kind'] == kind} for kind in ('monster', 'npc', 'item')}
    action = row['action']
    if action == 'hunt':
        matching = [t for t in terms if t['bTermType'] == 3 and t['dwTermID'] in ids['monster']]
        return 'match' if matching and row['published_count'] is not None and any(t['bCount'] == row['published_count'] for t in matching) else 'conflict'
    if action == 'collect':
        matching = [t for t in terms if t['bTermType'] == 2 and t['bCount'] == row['published_count']
                    and normal(client_items.get(t['dwTermID'], {}).get('szNAME', '')) == normal(row['item_name_without_published_id'])]
        # Item ID here comes from the client. The public page supplies an unlinked item name/count.
        return 'item_name_count_match' if matching else 'conflict'
    if action == 'use':
        return 'id_match_count_not_published' if any(t['bTermType'] == 17 and t['dwTermID'] in ids['item'] for t in terms) else 'conflict'
    if action == 'talk':
        npcs_from_client = {t['dwTermID'] for t in terms if t['bTermType'] == 13}
        npcs_from_client.update(n['npc_id'] for n in navigation if n['type'] in (13, 20) and n['npc_id'])
        return 'id_match_count_not_published' if ids['npc'] and ids['npc'] <= npcs_from_client else 'conflict'
    return 'not_compared'


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source', type=Path, default=Path('C:/Program Files (x86)/4Story'))
    p.add_argument('--snapshot', type=Path, default=Path('.runtime/gameforge-combined-evidence/live'))
    p.add_argument('--public', type=Path, default=Path('.runtime/gameforge-normalization/public'))
    p.add_argument('--cache', type=Path, default=Path('.runtime/gameforge-website/cz'))
    p.add_argument('--fresh', type=Path, default=Path('.runtime/gameforge-gap-audit/fresh-sources.json'))
    p.add_argument('--fresh-cache', type=Path, default=Path('.runtime/gameforge-gap-audit/web'))
    p.add_argument('--extra-cache', type=Path, default=Path('.runtime/gameforge-combined-evidence/web'))
    p.add_argument('--output', type=Path, default=Path('Gameforge/combined-evidence'))
    a = p.parse_args()
    a.output.mkdir(parents=True, exist_ok=True)
    inputs = {}

    def input_json(path):
        inputs[path.as_posix()] = digest(path)
        return load(path)

    reference = input_json(Path('Gameforge/reference.json'))
    pins = {r['path'].lower(): r['sha256'] for r in reference['files']}
    original_files = {}

    def original(relative):
        path = a.source / relative
        sha = digest(path)
        if sha != pins[relative.lower()]:
            raise ValueError('Original client changed: ' + relative)
        original_files[relative] = sha
        return path.read_bytes()

    extracted = input_json(Path('Gameforge/client-reconstruction/evidence.json'))
    for f in extracted['files']:
        original(f['path'])
        if original_files[f['path']] != f['sha256']:
            raise ValueError('Extraction evidence disagrees with the pinned client')
    gaps = input_json(Path('Gameforge/content-gaps/audit.json'))
    plan = input_json(Path('Gameforge/high-level-regions/plan.json'))
    deployed = input_json(Path('Gameforge/high-level-regions/deployed-gameplay.json'))
    if deployed['regionPlanSha256'] != inputs['Gameforge/high-level-regions/plan.json']:
        raise ValueError('Applied region plan changed')
    target = input_json(Path('Gameforge/target.json'))
    tables = {path.stem: input_json(path) for path in sorted(a.snapshot.glob('*.json')) if path.stem not in ('schema', 'counts')}
    sq = {r['dwQuestID']: r for r in tables['TQUESTCHART']}
    sn = {r['wID']: r for r in tables['TNPCCHART']}
    sm = {r['wID']: r for r in tables['TMONSTERCHART']}
    sa = {(r['wID'], r['bLevel']): r for r in tables['TMONATTRCHART']}
    si = {r['wItemID']: r for r in tables['TITEMCHART']}
    sp = {r['wPortalID']: r for r in tables['TPORTALCHART']}
    sd = {(r['wPortalID'], r['wDestID']): r for r in tables['TDESTINATIONCHART']}
    server_children, server_terms, server_rewards, server_conditions = (collections.defaultdict(list) for _ in range(4))
    for r in sq.values():
        server_children[r['dwParentID']].append(r)
    for table, group in [('TQUESTTERMCHART', server_terms), ('TQREWARDCHART', server_rewards), ('TQCONDITIONCHART', server_conditions)]:
        for r in tables[table]:
            group[r['dwQuestID']].append(r)
    cq = {r['dwQuestID']: r for r in quests(original('TQuest.mpq'))['quests']}
    ci = {r['wItemID']: r for r in items(original('Tcd/TItem.tcd'))}
    original('Tcd/TMon.tcd')
    cm = {r.key: {'id': r.key, 'name': r.names[-1], 'kind': int.from_bytes(r.raw[2:4], 'little'),
                  'skills': list(struct.unpack_from('<4H', r.raw[-125:], 54)),
                  'can_select': r.raw[-125:][63], 'can_attack': r.raw[-125:][65], 'can_tame': r.raw[-125:][67],
                  'size': struct.unpack_from('<f', r.raw[-125:], 74)[0]}
          for r in named(a.source / 'Tcd/TMon.tcd')[0]}
    cn = collections.defaultdict(list)
    for path in sorted((a.source / 'Tcd').glob('TNPC*.tcd')):
        match = re.fullmatch(r'TNPC([0-9a-fA-F]{8})\.tcd', path.name)
        if match and path.stat().st_size:
            for r in npcs(original('Tcd/' + path.name), int(match[1], 16) >> 16):
                cn[r['wID']].append(r)
    pub = PublicEvidence(a.public, a.cache, a.fresh, a.fresh_cache, a.extra_cache)
    for kind in pub.catalogues:
        input_json(a.public / (kind + '-catalogue.json'))
    input_json(a.fresh)
    wanted = {q['dwQuestID'] for q in plan['candidates']}
    missing_before = {q['id'] for q in gaps['missing_regional_quests']}
    if wanted - sq.keys() != missing_before:
        raise ValueError('The live mission inventory changed; refresh the gap audit first')
    navigation = plan['navigation']
    child_records = collections.defaultdict(list)
    for q in cq.values():
        if q['unknown_prefix'] in wanted and q['bType'] != 8:
            child_records[q['unknown_prefix']].append(q)
    result_quests, conflicts = [], []
    npc_quest_refs, mon_quest_refs, item_quest_refs = (collections.defaultdict(set) for _ in range(3))
    for ident in sorted(wanted):
        q = cq[ident]
        if q != next(c for c in plan['candidates'] if c['dwQuestID'] == ident):
            raise ValueError('Candidate no longer agrees with original client')
        source, h = pub.detail('quest', ident)
        facts = quest_facts(h)
        objectives = Objectives(h).records
        nav = navigation[str(ident)]
        for objective in objectives:
            objective['comparison'] = check_objective(objective, q, nav, ci)
        tree = Chain()
        tree.feed(h)
        parents = sorted(x for x, y in tree.edges if y == ident)
        giver_ids = sorted({n['npc_id'] for n in nav if n['type'] == 19 and n['npc_id']})
        turnin_ids = sorted({n['npc_id'] for n in nav if n['type'] == 20 and n['npc_id']})
        rewards = {kind: sum(r['dwRewardID'] for r in q['rewards'] if r['bRewardType'] == code) for kind, code in [('money', 1), ('experience', 6)]}
        checks = {'name': compare(normal(q['title']), normal(source['heading'])),
                  'level': compare(str(q['bLevel']), facts['level_label']),
                  'money': compare(rewards['money'], facts['money']),
                  'experience': compare(rewards['experience'], facts['experience']),
                  'giver': ('not_published' if facts['giver_id'] is None else 'missing_in_client' if not giver_ids else 'match' if facts['giver_id'] in giver_ids else 'conflict'),
                  'parent': ('not_published' if not parents else 'match' if q['unknown_prefix'] in parents else 'conflict')}
        client_item_rewards = sorted((r['dwRewardID'], r['bCount']) for r in q['rewards'] if r['bRewardType'] == 2)
        public_item_rewards = sorted((r['id'], r['count']) for r in facts.get('reward_items', []))
        checks['item_rewards'] = (compare(client_item_rewards, public_item_rewards)
                                  if client_item_rewards or public_item_rewards else 'not_published')
        client_values = {'name': q['title'], 'level': q['bLevel'], **rewards, 'giver': giver_ids,
                         'parent': q['unknown_prefix'], 'item_rewards': client_item_rewards}
        public_values = {'name': source['heading'], 'level': facts['level_label'], 'money': facts['money'],
                         'experience': facts['experience'], 'giver': facts['giver_id'], 'parent': parents,
                         'item_rewards': public_item_rewards}
        for field, status in checks.items():
            if status == 'conflict':
                conflicts.append({'kind': 'quest', 'id': ident, 'field': field,
                                  'client': client_values[field], 'publisher': public_values[field],
                                  'source_ref': f'quest:{ident}'})
        for obj in objectives:
            if obj['comparison'] == 'conflict':
                conflicts.append({'kind': 'quest', 'id': ident, 'field': 'objective', 'client': q['terms'], 'publisher': obj, 'source_ref': f'quest:{ident}'})
        for npc_id in set(giver_ids + turnin_ids + ([facts['giver_id']] if facts['giver_id'] else [])):
            npc_quest_refs[npc_id].add(ident)
        for n in nav:
            if n['npc_id']:
                npc_quest_refs[n['npc_id']].add(ident)
            if n['monster_kind']:
                mon_quest_refs[n['monster_kind']].add(ident)
        for t in q['terms']:
            if t['bTermType'] in (2, 17):
                item_quest_refs[t['dwTermID']].add(ident)
            if t['bTermType'] == 3:
                mon_quest_refs[t['dwTermID']].add(ident)
        blockers = []
        if ident not in sq:
            blockers.append('missing_server_mission')
        if q['unknown_prefix'] and q['unknown_prefix'] not in sq:
            blockers.append('missing_server_parent')
        if any(r['bTermType'] in (2, 3, 21) for r in q['terms']):
            blockers.append('combat_or_loot_dependencies_need_server_data')
        if any(r['bRewardType'] == 9 for r in q['rewards']):
            blockers.append('reward_type_9_not_implemented_in_current_server')
        if any(r['bTermType'] == 21 for r in q['terms']):
            blockers.append('spawn_reference_term_21_requires_instance_binding_not_a_monster_id')
        typed_terms = [{**t, 'id_namespace': {1: 'quest', 2: 'item', 3: 'monster_kind', 13: 'npc', 17: 'item', 21: 'spawn_reference'}.get(t['bTermType'], 'unmapped')}
                       for t in q['terms']]
        result_quests.append({'id': ident, 'name': q['title'], 'client': q,
                              'client_terms_with_id_namespaces': typed_terms,
                              'client_helper_records': child_records[ident],
                              'client_giver_ids': giver_ids, 'client_turnin_ids': turnin_ids,
                              'client_navigation': nav, 'publisher_source_ref': f'quest:{ident}',
                              'publisher': {**facts, 'objectives_structured': objectives, 'parent_ids': parents},
                              'comparisons': checks,
                              'server': {'record_exists': ident in sq, 'record': sq.get(ident), 'terms': server_terms[ident],
                                         'rewards': server_rewards[ident], 'conditions': server_conditions[ident], 'child_records': server_children[ident]},
                              'known_blockers': blockers,
                              'full_original_conditions_recovered': False})

    regional_ids = {r['id'] for r in gaps['regional_monsters']}
    monster_ids = regional_ids | mon_quest_refs.keys()
    mon_spawns, mon_drops = collections.defaultdict(list), collections.defaultdict(list)
    for r in tables['TMAPMONCHART']:
        mon_spawns[r['wMonID']].append(r)
    for r in tables['TMONITEMCHART']:
        mon_drops[r['wMonID']].append(r)
    result_monsters, loot = [], []
    item_mon_refs = collections.defaultdict(set)
    for ident in sorted(monster_ids):
        source, h = pub.detail('monster', ident)
        parser = Tables()
        parser.feed(h)
        fields = {r[0]['text'].rstrip(':'): r[1]['text'] for t in parser.tables for r in t['rows'] if len(r) == 2 and r[0]['header']}
        level, hp = bounds(fields['Level']), bounds(fields['Život'])
        drops = {}
        for table in parser.tables:
            for cells in table['rows']:
                if len(cells) != 4 or any(c['header'] for c in cells):
                    continue
                ids = {int(m[1]) for c in cells for link in c['links'] if (m := re.search(r'/item/details/(\d+)', link))}
                if len(ids) == 1:
                    item_id = next(iter(ids))
                    drops[item_id] = {'monster_id': ident, 'item_id': item_id, 'frequency_label': cells[0]['text'], 'publisher_label': cells[1]['text'], 'publisher_source_ref': f'monster:{ident}'}
                    item_mon_refs[item_id].add(ident)
        loot.extend(drops.values())
        server = sm.get(ident)
        attributes = sa.get((server['wMonAttr'], server['bLevel'])) if server else None
        catalogue = pub.catalogues['monster'].get(ident)
        catalogue_level = bounds(catalogue['cells'][1]) if catalogue else None
        catalogue_range_differs = catalogue_level is not None and catalogue_level != level
        catalogue_disagrees = catalogue_level is not None and not catalogue_level[0] <= level[0] <= level[1] <= catalogue_level[1]
        level_conflict = bool(server and not level[0] <= server['bLevel'] <= level[1])
        if level_conflict:
            conflicts.append({'kind': 'monster', 'id': ident, 'field': 'server_level', 'server': server['bLevel'], 'publisher': level, 'source_ref': f'monster:{ident}'})
        if ident not in cm:
            raise ValueError('Referenced monster ID is not in the original client')
        if catalogue_disagrees:
            conflicts.append({'kind': 'monster', 'id': ident, 'field': 'publisher_catalogue_vs_detail_level',
                              'client': {'publisher_catalogue': catalogue_level}, 'publisher': {'publisher_detail': level}, 'source_ref': f'monster:{ident}'})
        presentation_comparisons = {}
        if server:
            for client_key, server_key in [('kind', 'wKind'), ('can_select', 'bCanSelect'), ('can_attack', 'bCanAttack'), ('can_tame', 'bTame')]:
                presentation_comparisons[client_key] = compare(cm[ident][client_key], server[server_key])
            presentation_comparisons['skills'] = compare(cm[ident]['skills'], [server[f'wSkill{i}'] for i in range(1, 5)])
            presentation_comparisons['size'] = 'match' if abs(cm[ident]['size'] - server['fSize']) < 0.00001 else 'conflict'
        hp_outside_range = bool(attributes and not hp[0] <= attributes['dwMaxHP'] <= hp[1])
        result_monsters.append({'id': ident, 'name': cm[ident]['name'], 'client': cm[ident],
                                'in_regional_public_catalogue': ident in regional_ids,
                                'publisher_source_ref': f'monster:{ident}',
                                'publisher': {'level_range': level, 'hp_range': hp, 'fields': fields, 'possible_loot_items': len(drops),
                                              'catalogue_level_range': catalogue_level, 'catalogue_range_differs_from_detail': catalogue_range_differs,
                                              'catalogue_and_detail_level_disagree': catalogue_disagrees},
                                'related_quest_ids_by_kind': sorted(mon_quest_refs[cm[ident]['kind']]),
                                'client_name_matches_publisher': normal(cm[ident]['name']) == normal(source['heading']),
                                'server': {'record_exists': server is not None, 'record': server,
                                           'attribute_row_exists': attributes is not None, 'attribute_row': attributes,
                                           'hp_outside_published_range': hp_outside_range,
                                           'client_field_comparisons': presentation_comparisons,
                                           'level_outside_published_range': level_conflict,
                                           'spawn_memberships_any_map': mon_spawns[ident], 'drop_rows': mon_drops[ident]},
                                'original_spawn_count_or_respawn_recovered': False, 'numeric_drop_probability_recovered': False})

    result_npcs = []
    regional_npcs = {r['id']: r for r in gaps['regional_npcs']}
    for ident in sorted(regional_npcs.keys() | npc_quest_refs.keys()):
        source, _ = pub.detail('npc', ident, required=False)
        originals = cn.get(ident, [])
        server = sn.get(ident)
        stock = [r for r in tables['TNPCITEMCHART'] if r['wNpcID'] == ident]
        for r in stock:
            item_quest_refs.setdefault(r['dwItemID'], set())
        result_npcs.append({'id': ident, 'name': originals[0]['name'] if originals else source['heading'] if source else '',
                            'region': regional_npcs.get(ident, {}).get('region'), 'client_records': originals,
                            'publisher_source_ref': f'npc:{ident}' if source else None,
                            'client_name_and_title_match_publisher': normal(source['heading']) in {
                                normal(originals[0]['name']), normal(originals[0]['title'] + ' ' + originals[0]['name'])
                            } if originals and source else None,
                            'related_quest_ids': sorted(npc_quest_refs[ident]),
                            'server': {'record_exists': server is not None, 'record': server, 'shop_stock': stock}})

    result_items = []
    for q in result_quests:
        for r in q['client']['rewards']:
            if r['bRewardType'] == 2:
                item_quest_refs[r['dwRewardID']].add(q['id'])
    item_ids = item_mon_refs.keys() | item_quest_refs.keys()
    for ident in sorted(item_ids):
        client, published = ci.get(ident), pub.catalogues['item'].get(ident)
        result_items.append({'id': ident, 'name': client['szNAME'] if client else published['name'] if published else '',
                             'client_record': client, 'publisher_catalogue_record': published,
                             'publisher_catalogue_evidence_ref': 'item' if published else None,
                             'client_name_matches_publisher_catalogue': normal(client['szNAME']) == normal(published['name']) if client and published else None,
                             'server_record_exists': ident in si, 'server_record': si.get(ident),
                             'related_quest_ids': sorted(item_quest_refs.get(ident, set())), 'possible_loot_from_monster_ids': sorted(item_mon_refs.get(ident, set()))})

    portals = []
    for portal in extracted['portals']:
        ident = portal['portal_id']
        source, _ = pub.detail('npc', portal['npc_id'])
        edges = [{**r, 'server_record': sd.get((r['portal_id'], r['destination_id'])),
                  'server_record_exists': (r['portal_id'], r['destination_id']) in sd}
                 for r in extracted['portal_edges'] if ident in (r['portal_id'], r['destination_id'])]
        portals.append({**portal, 'publisher_npc_source_ref': f'npc:{portal["npc_id"]}',
                        'publisher_route_or_price_confirmation': False, 'server_portal_record': sp.get(ident),
                        'server_npc_record': sn.get(portal['npc_id']), 'client_edges': edges})

    # Recheck extracted identities against fresh decodes; never silently carry forward altered evidence.
    if any(cq[r['dwQuestID']] != r for r in extracted['quest_records']):
        raise ValueError('Prior quest extraction differs from current decoding')
    if any(r not in cn[r['wID']] for r in extracted['npc_records']):
        raise ValueError('Prior NPC extraction differs from current decoding')
    for row in extracted['monster_templates']:
        if any(cm[row['id']][field] != row[field] for field in ('id', 'name', 'kind', 'skills', 'can_select', 'can_attack', 'can_tame', 'size')):
            raise ValueError('Prior monster extraction differs from current decoding')
    if len({(r['monster_id'], r['item_id']) for r in loot}) != len(loot):
        raise ValueError('Duplicate typed loot membership')
    checks = collections.Counter((field + ':' + status) for q in result_quests for field, status in q['comparisons'].items())
    objective_checks = collections.Counter(o['comparison'] for q in result_quests for o in q['publisher']['objectives_structured'])
    summary = {'missions_joined': len(result_quests), 'missing_server_missions': sum(not q['server']['record_exists'] for q in result_quests),
               'present_server_missions': sum(q['server']['record_exists'] for q in result_quests),
               'quest_field_comparisons': dict(sorted(checks.items())), 'objective_comparisons': dict(sorted(objective_checks.items())),
               'npcs_joined_including_external_quest_dependencies': len(result_npcs), 'regional_npcs': len(regional_npcs),
               'regional_npcs_missing': sum(r['id'] in regional_npcs and not r['server']['record_exists'] for r in result_npcs),
               'monsters_joined': len(result_monsters), 'additional_monsters_confirmed_by_direct_page': len(monster_ids - regional_ids),
               'missing_monster_templates': sum(not r['server']['record_exists'] for r in result_monsters),
               'server_monster_level_conflicts': sum(r['server']['level_outside_published_range'] for r in result_monsters),
               'server_monster_attribute_rows_missing': sum(not r['server']['attribute_row_exists'] for r in result_monsters),
               'server_monster_hp_outside_published_range': sum(r['server']['hp_outside_published_range'] for r in result_monsters),
               'publisher_catalogue_detail_level_differences': sum(r['publisher']['catalogue_and_detail_level_disagree'] for r in result_monsters),
               'monsters_with_no_server_spawn_membership': sum(not r['server']['spawn_memberships_any_map'] for r in result_monsters),
               'monsters_with_published_loot_but_no_drop_rows': sum(r['publisher']['possible_loot_items'] > 0 and not r['server']['drop_rows'] for r in result_monsters),
               'possible_loot_memberships': len(loot), 'distinct_possible_loot_items': sum(bool(v) for v in item_mon_refs.values()),
               'items_joined': len(result_items), 'missing_server_items': sum(not r['server_record_exists'] for r in result_items),
               'quest_item_monster_links': len(extracted['quest_item_links']), 'original_quest_drop_helpers': sum(len(r['client_drop_helper_records']) for r in extracted['quest_item_links']),
               'portals_joined': len(portals), 'distinct_client_portal_edges': len(extracted['portal_edges']),
               'missing_server_portals': sum(r['server_portal_record'] is None for r in portals),
               'public_detail_pages_verified': len(pub.sources), 'original_files_verified': len(original_files),
               'conflicts': len(conflicts)}
    manifest = {'generated_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                'original_client': str(a.source), 'original_files_sha256': original_files,
                'input_files_sha256': inputs, 'sql_snapshot': str(a.snapshot), 'sql_database': 'TGame_gsp',
                'sql_read_only': True, 'sql_snapshot_latest_file_utc': datetime.datetime.fromtimestamp(max(f.stat().st_mtime for f in a.snapshot.glob('*.json')), datetime.timezone.utc).isoformat(),
                'public_detail_sources': pub.sources}
    manifest['public_catalogue_sources'] = pub.catalogue_evidence
    manifest['unavailable_public_details'] = pub.unavailable
    result = {'schema': 1, 'generated_utc': manifest['generated_utc'], 'scope': 'Anbari and Undred, including quest dependencies in other regions',
              'target': target, 'reference_client_patch_verified': False, 'server_deployment_performed': False,
              'summary': summary, 'quests': result_quests, 'npcs': result_npcs, 'monsters': result_monsters,
              'items': result_items, 'quest_item_monster_links': extracted['quest_item_links'],
              'portals': portals, 'conflicts': conflicts,
              'limitations': ['Matching IDs and published facts do not recover the complete original server logic.',
                              'The selected original client patch is unknown; the target is Gameforge 5.0+ with baseline 5.0.1.',
                              'A public monster ID, client monster kind and item ID are separate namespaces.',
                              'Term type 21 is QTT_SPAWNID_DEL in the current native/server protocol, not a monster-template ID. Its numeric coincidences are not joins.',
                              'Quest navigation points are guidance markers, not a complete spawn chart.',
                              'Public possible loot memberships and qualitative rarity do not define numeric drop probabilities.',
                              'Terrain Y is not a verified portal landing height; platforms and collision geometry can raise the floor.',
                              'Missing public fields are unknown, not conflicts or zero rewards.',
                              'The eight quest collection pages do not publish item IDs in their objective labels; those IDs come from the client.',
                              'Presence in SQL does not certify full gameplay support, especially reward type 9 and reconstructed conditions.']}
    save(a.output / 'joined.json', result)
    save(a.output / 'sources.json', manifest)
    save(a.output / 'loot-memberships.json', loot, compact=True)
    readiness = {'generated_utc': manifest['generated_utc'], 'source_join_sha256': digest(a.output / 'joined.json'),
                 'server_deployment_performed': False,
                 'policy': 'Do not infer original spawn population, drop odds, quest reset/conditions or combat donors from matching names/IDs.',
                 'original_npc_rows_available_but_portal_service_dependencies_missing': [r for r in result_npcs if r['region'] and not r['server']['record_exists']],
                 'monster_candidates': [],
                 'missing_mission_ids': sorted(missing_before),
                 'quest_navigation_is_not_a_spawn_chart': True,
                 'missing_portal_ids': [r['portal_id'] for r in portals if r['server_portal_record'] is None],
                 'portal_arrival_heights_verified': False,
                 'quest_item_source_relationships': extracted['quest_item_links'],
                 'quest_repeat_policy_verified': False, 'quest_conditions_complete': False,
                 'ordinary_loot_qualitative_only': True}
    for row in result_monsters:
        server, facts = row['server'], row['publisher']
        reasons = []
        if not server['attribute_row_exists']:
            reasons.append('missing_current_server_attribute_row_no_combat_donor')
        if facts['level_range'][0] != facts['level_range'][1]:
            reasons.append('public_level_is_a_range')
        if facts['hp_range'][0] != facts['hp_range'][1]:
            reasons.append('public_hp_is_a_range')
        if facts['catalogue_and_detail_level_disagree']:
            reasons.append('public_catalogue_and_detail_disagree')
        if server['level_outside_published_range'] or server['hp_outside_published_range'] or not server['attribute_row_exists']:
            readiness['monster_candidates'].append({'id': row['id'], 'name': row['name'], 'source_ref': row['publisher_source_ref'],
                                                     'published_level': facts['level_range'], 'published_hp': facts['hp_range'],
                                                     'blocking_reasons': reasons,
                                                     'singleton_level_hp_and_existing_combat_donor': not reasons,
                                                     'retained_combat_donor_is_original_gameforge': False,
                                                     'complete_gameplay_import_ready': False})
    save(a.output / 'reconstruction-plan.json', readiness)
    report(a.output, result, manifest)
    print(json.dumps(summary, ensure_ascii=False, indent=2))


def report(output, result, manifest):
    s = result['summary']
    statuses = {'match': 'Shoda', 'conflict': 'Rozpor', 'not_published': 'Nezveřejněno', 'missing_in_client': 'V klientovi chybí',
                'item_name_count_match': 'Shoda názvu a počtu (ID z klienta)', 'id_match_count_not_published': 'Shoda ID, počet nezveřejněn', 'not_compared': 'Neověřeno'}

    def e(v):
        return escape(str(v), quote=True)

    def pub(ref):
        r = manifest['public_detail_sources'].get(ref)
        return f'<a href="{e(r["url"])}" target="_blank" rel="noopener">Gameforge</a>' if r else 'Bez detailu'

    def table(head, rows):
        return '<div class="scroll"><table><thead><tr>' + ''.join('<th>' + e(h) + '</th>' for h in head) + '</tr></thead><tbody>' + ''.join(rows) + '</tbody></table></div>'

    def tr(cells, ident=''):
        return '<tr' + (f' id="{e(ident)}"' if ident else '') + '>' + ''.join('<td>' + c + '</td>' for c in cells) + '</tr>'

    parts = ['<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">',
             '<title>Gameforge — propojení klienta, veřejné databáze a serveru</title><style>',
             'body{font:16px/1.55 system-ui,sans-serif;margin:0;background:#101722;color:#e4eaf2}main{max-width:1500px;margin:auto;padding:30px}a{color:#90c8ff}h1{font-size:30px}h2{margin-top:42px}nav{display:flex;flex-wrap:wrap;gap:18px}.cards{display:flex;flex-wrap:wrap;gap:12px;margin:25px 0}.card{padding:16px;background:#202e40;border-radius:10px;min-width:160px}.card b{display:block;font-size:27px}.scroll{overflow:auto}table{border-collapse:collapse;width:100%;font-size:14px}th,td{padding:10px;border:1px solid #38475a;vertical-align:top}th{background:#233247;text-align:left}tr:target{background:#34472d}small,.muted{color:#b8c6d5}code{overflow-wrap:anywhere}input{padding:12px;background:#202e40;color:white;border:1px solid #607089;width:min(520px,90%);margin:16px 0}.warn{border-left:4px solid #e3b259;padding:12px;background:#29291f}summary{cursor:pointer}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px}details{max-width:600px}button{padding:10px;cursor:pointer}</style><main>',
             '<h1>Propojení originálního klienta, Gameforge a serveru</h1>',
             '<p>Anbari a Undred včetně návazných questů mimo tyto mapy. Spojení používá ID a jejich typ: quest, NPC, šablona monstra, druh monstra, předmět a portál.</p>',
             f'<p class="muted">Vytvořeno {e(result["generated_utc"])}. SQL snímek: {e(manifest["sql_snapshot_latest_file_utc"])}. Ověřeno {s["original_files_verified"]} původních souborů a {s["public_detail_pages_verified"]} veřejných detailů pomocí SHA-256.</p>',
             '<nav><a href="#quests">Questy</a><a href="#npcs">NPC a obchody</a><a href="#monsters">Monstra</a><a href="#items">Předměty</a><a href="#quest-items">Questové dropy</a><a href="#portals">Portály</a><a href="#conflicts">Rozpory</a><a href="joined.json">Úplná spojená data</a><a href="sources.json">Důkazy a hashe</a><a href="reconstruction-plan.json">Podklady pro rekonstrukci</a></nav>',
             '<div class="cards">' + ''.join(f'<div class="card"><b>{e(n)}</b>{e(label)}</div>' for n, label in [(s['missions_joined'], 'propojených misí'), (s['monsters_joined'], 'monster'), (s['regional_npcs'], 'regionálních NPC'), (s['items_joined'], 'předmětů'), (s['portals_joined'], 'portálů'), (s['distinct_client_portal_edges'], 'tras z klienta')]) + '</div>',
             '<p class="warn">Tento přehled spojuje důkazy; neprovádí import do serveru. Přesný patch původního klienta není ověřen. Cíl je Gameforge 5.0+ se základnou 5.0.1. Shoda záznamů sama nezaručuje funkční spawn, drop, podmínky questu ani všechny typy odměn.</p>',
             f'<p>Na serveru je {s["present_server_missions"]} z těchto misí, chybí {s["missing_server_missions"]}. Všech {s["monsters_joined"]} šablon monster existuje, ale {s["monsters_with_no_server_spawn_membership"]} nemá žádné přiřazení ke spawnu. U {s["server_monster_level_conflicts"]} šablon level odporuje publikovanému rozsahu. Veřejná databáze uvádí {s["possible_loot_memberships"]:,} možných dvojic monstrum–předmět; ty neurčují počet SQL pravidel ani procenta dropu.</p>',
             f'<p>U {s["server_monster_attribute_rows_missing"]} monster úplně chybí odkazovaný řádek serverových statistik. U {s["publisher_catalogue_detail_level_differences"]} ID se liší rozsah levelů mezi veřejným katalogem a detailem.</p>',
             '<label>Filtrovat tabulky podle ID nebo názvu<br><input id="search" type="search" placeholder="Např. 11174 nebo Hnijící vlčí kůže"></label>',
             '<h2 id="quests">Questy: cíle, NPC, odměny a návaznosti</h2>',
             '<p>Detail uchovává původní typy úkolů a pomocné záznamy, navigační body, serverové podmínky i publikované rodičovské vazby. Nezveřejněná hodnota se nepočítá jako rozpor.</p>']
    rows = []
    for q in result['quests']:
        facts = q['publisher']
        objectives = '<br>'.join(e(o['text']) + ' <small>[' + e(statuses[o['comparison']]) + ']</small>' for o in facts['objectives_structured']) or 'Nezveřejněno'
        parents = f'Klient: {q["client"]["unknown_prefix"]}; web: {e(facts["parent_ids"])}'
        giv = f'Klient: {e(q["client_giver_ids"])}; web: {e(facts["giver_id"])}; odevzdání: {e(q["client_turnin_ids"])}'
        detail = '<details><summary>Všechna spojená pole</summary><pre>' + e(json.dumps(q, ensure_ascii=False, indent=2)) + '</pre></details>'
        rows.append(tr([f'<b>{q["id"]}</b> {e(q["name"])}<br>' + pub(q['publisher_source_ref']),
                        f'{q["client"]["bLevel"]}<br>{e(statuses[q["comparisons"]["level"]])}', objectives,
                        giv + '<br><small>' + e(statuses[q['comparisons']['giver']]) + '</small>',
                        f'EXP: {e(facts["experience"])}<br>Peníze: {e(facts["money"])}',
                        parents, ('Záznam přítomen' if q['server']['record_exists'] else '<b>Chybí mise</b>') + detail], f'quest-{q["id"]}'))
    parts.append(table(['Quest', 'Level', 'Cíle a míra potvrzení', 'NPC', 'Webové odměny', 'Rodič', 'Server a detaily'], rows))
    parts += ['<h2 id="npcs">NPC, obchodníci a vazby na questy</h2>', '<p>Souřadnice pocházejí z originálního klienta. Další NPC mimo regiony jsou zahrnuta jako závislosti questů. Položky obchodu v SQL jsou současný stav serveru.</p>']
    rows = []
    for n in result['npcs']:
        records = n['client_records']
        xyz = '<br>'.join(f'Mapa {r["wMapID"]}: {r["fPosX"]:.2f}, {r["fPosY"]:.2f}, {r["fPosZ"]:.2f}' for r in records) or 'Chybí klientský záznam'
        qs = ', '.join(f'<a href="#quest-{i}">{i}</a>' for i in n['related_quest_ids'])
        stock = ', '.join(str(r['dwItemID']) for r in n['server']['shop_stock'])
        rows.append(tr([f'{n["id"]} {e(n["name"])}<br>' + pub(n['publisher_source_ref']), e(n['region'] or 'Návaznost mimo region'),
                        ', '.join(str(r['bType']) for r in records), xyz,
                        'Přítomen' if n['server']['record_exists'] else '<b>Chybí NPC</b>', qs,
                        f'{len(n["server"]["shop_stock"])} položek<br>{e(stock)}'], f'npc-{n["id"]}'))
    parts.append(table(['NPC', 'Oblast', 'Typ v klientovi', 'Původní XYZ', 'Server', 'Questy', 'Obchod'], rows))
    parts += ['<h2 id="monsters">Monstra: šablona, level, životy, spawny a dropy</h2>',
              '<p>Navíc byly přímým detailem ověřeny ID 3529 a 3588. Rozsahy levelů a životů zůstávají rozsahy; výběr konkrétní varianty vyžaduje další důkaz. Uvedené životy nejsou ověřením serverových atributových křivek.</p>']
    rows = []
    for m in result['monsters']:
        server, facts = m['server'], m['publisher']
        detail = '<details><summary>Klient a SQL</summary><pre>' + e(json.dumps({'client': m['client'], 'server': server}, ensure_ascii=False, indent=2)) + '</pre></details>'
        rows.append(tr([f'{m["id"]} {e(m["name"])}<br>' + pub(m['publisher_source_ref']), e(m['client']['kind']),
                        e(facts['level_range']), e(server['record']['bLevel'] if server['record'] else 'Chybí') + (' <b>Rozpor</b>' if server['level_outside_published_range'] else ''),
                        e(facts['hp_range']) + '<br>SQL: ' + (str(server['attribute_row']['dwMaxHP']) if server['attribute_row'] else '<b>Chybí řádek statistik</b>'), str(len(server['spawn_memberships_any_map'])),
                        f'{facts["possible_loot_items"]} na webu / {len(server["drop_rows"])} SQL pravidel',
                        ', '.join(f'<a href="#quest-{i}">{i}</a>' for i in m['related_quest_ids_by_kind']) + detail], f'monster-{m["id"]}'))
    parts.append(table(['Monstrum', 'Druh v klientovi', 'Web level', 'SQL level', 'Web životy', 'SQL vazby na spawny', 'Možné dropy', 'Questy a detaily'], rows))
    parts += ['<h2 id="items">Předměty: klientské ID a serverová šablona</h2>',
              f'<p>Celkem {s["items_joined"]} předmětů zahrnuje {s["distinct_possible_loot_items"]} různých položek běžného možného lootu a další questové, odměnové a obchodní položky. Všechny mají serverovou šablonu. Dostupnost ve hře závisí na navazujících spawnech, drop pravidlech a questech.</p>']
    rows = []
    for r in result['items']:
        catalogue = r['publisher_catalogue_record']
        reference = f'<a href="{e(catalogue["url"])}">{e(catalogue["name"])}</a>' if catalogue else 'ID doloženo příslušnými loot/quest vazbami; bez záznamu v hlavním katalogu'
        monsters = r['possible_loot_from_monster_ids']
        mons = (f'<details><summary>{len(monsters)} zdrojových monster</summary>' + ', '.join(f'<a href="#monster-{i}">{i}</a>' for i in monsters) + '</details>') if monsters else 'Bez běžného loot zdroje v tomto výběru'
        rows.append(tr([f'{r["id"]} {e(r["name"])}', reference, 'Přítomen' if r['client_record'] else '<b>Chybí</b>',
                        'Přítomen' if r['server_record_exists'] else '<b>Chybí</b>',
                        ', '.join(f'<a href="#quest-{i}">{i}</a>' for i in r['related_quest_ids']), mons], f'item-{r["id"]}'))
    parts.append(table(['Předmět', 'Veřejný katalog', 'Klient', 'SQL šablona', 'Questy', 'Možný loot'], rows))
    parts += ['<h2 id="quest-items">Questové předměty a jejich zdroje</h2>',
              '<p>Web potvrzuje název a počet sbíraného předmětu a odkazuje na zdrojové monstrum. Číselné ID předmětu a pomocné drop úkoly dodává klient. Pravděpodobnost, reset a serverové spouštěče tím nejsou určeny.</p>']
    rows = []
    for r in result['quest_item_monster_links']:
        rows.append(tr([f'{r["item_id"]} {e(r["name"])}', f'<a href="#quest-{r["quest_id"]}">{r["quest_id"]} {e(r["quest_name"])}</a>',
                        str(r['quantity']), f'<a href="#monster-{r["monster_kind"]}">{r["monster_kind"]} {e(r["monster_name"])}</a>',
                        e(r['original_drop_helper_ids']), 'Předmět přítomen; chybí pomocné drop úkoly']))
    parts.append(table(['Předmět', 'Quest', 'Počet', 'Zdrojový druh monstra', 'Pomocné ID z klienta', 'Server'], rows))
    parts += ['<p>Všechny běžné loot vazby jsou v <a href="loot-memberships.json">loot-memberships.json</a>, jednotlivé předměty v <a href="joined.json">joined.json</a>. Slovní četnost není číselná pravděpodobnost. Více předmětů může pokrýt společné serverové drop pravidlo.</p>',
              '<h2 id="portals">Portálová NPC, destinace a původní trasy</h2>',
              '<p>Veřejná databáze potvrzuje NPC; trasy, ceny a příznaky zemí pocházejí z klienta. Výška terénu není ověřená výška přistání. Přístav Luadlan má NPC přibližně o 2,25 m nad terénem.</p>']
    rows = []
    for r in result['portals']:
        edges = '<br>'.join(f'{d["portal_id"]} → {d["destination_id"]}; cena {d["stored_cost"]}; země {e(d["country_flags_D_C_B_PEACE"])}; SQL {"ano" if d["server_record_exists"] else "chybí"}' for d in r['client_edges'])
        rows.append(tr([f'{r["portal_id"]} {e(r["name"])}', f'{r["npc_id"]}<br>' + pub(r['publisher_npc_source_ref']),
                        f'X {r["x"]:.2f}, Z {r["z"]:.2f}<br>terén Y {r["terrain_y_at_portal"]:.2f}<br><b>Přistání Y neověřeno</b>',
                        'Přítomen' if r['server_portal_record'] else '<b>Chybí portál</b>', '<details><summary>Trasy dotýkající se portálu</summary>' + edges + '</details>'], f'portal-{r["portal_id"]}'))
    parts.append(table(['Portál', 'NPC', 'Klientská poloha', 'SQL', 'Původní síť'], rows))
    parts += ['<h2 id="conflicts">Rozpory a chybějící důkazy</h2>', '<p>Rozpory se automaticky nepřepisují. Mohou souviset s rozdílem patche nebo s chybou dat. U questu 11261 klient postrádá zadavatele; web uvádí 26660. Chybějící hodnota se vede odděleně od rozporné hodnoty.</p>']
    parts.append(table(['Typ / ID', 'Pole', 'Klient nebo SQL', 'Gameforge'], [tr([e(r['kind']) + ' ' + str(r['id']), e(r['field']), e(r.get('client', r.get('server'))), e(r['publisher']) + '<br>' + pub(r['source_ref'])]) for r in result['conflicts']]))
    gap_root = output.parent / 'gaps' if (output.parent / 'gaps/report.html').exists() else Path('Gameforge/content-gaps')
    relative = lambda p: Path(os.path.relpath(p, output)).as_posix()
    parts += ['<p>Stále chybí úplná původní populace a časování spawnů, číselné váhy dropů, úplné podmínky a spouštěče questů a ověřené výšky příjezdu portálů. Tyto hodnoty nelze z uvedených shod odvodit přesně.</p>',
              f'<p><a href="{relative(gap_root / "report.html")}">Regionální audit</a> · <a href="{relative(gap_root / "global.html")}">Globální rozdíly</a> · <a href="{relative(Path("Gameforge/client-reconstruction/report.html"))}">Co obsahuje klient</a></p>',
              '<script>document.querySelector("#search").addEventListener("input",e=>{const q=e.target.value.toLocaleLowerCase("cs");document.querySelectorAll("tbody tr").forEach(r=>r.hidden=!r.textContent.toLocaleLowerCase("cs").includes(q))})</script></main></html>']
    (output / 'report.html').write_text('\n'.join(parts), encoding='utf-8')


if __name__ == '__main__':
    main()
