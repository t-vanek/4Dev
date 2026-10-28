"""Freeze a guarded import of complete source-backed rows and mapped monster fields."""
import collections
import datetime
import hashlib
import json
from pathlib import Path
import re
import struct

from client_facts import fixed, items, npcs
from client_records import named
from guarded_import import additions_sql
from identity_audit import normal
from normalization import equal, migration
from prepare_restore import SQL_HEADER, SQL_FOOTER


def load(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def main():
    base = Path('.runtime/gameforge-verified-import/before')
    out = Path('Gameforge/server-completion')
    out.mkdir(parents=True, exist_ok=True)
    if (out / 'plan.json').exists():
        raise ValueError('Plan already frozen; do not regenerate it from a modified database')
    source = Path('C:/Program Files (x86)/4Story')
    pinned = {r['path'].lower(): r['sha256'] for r in load(Path('Gameforge/reference.json'))['files']}
    inputs = {}

    def original(path):
        p = source / path
        if sha(p) != pinned[path.lower()]:
            raise ValueError('Original client changed: ' + path)
        inputs[path] = sha(p)
        return p.read_bytes()

    tables = {p.stem: load(p) for p in base.glob('*.json') if p.stem not in ('counts', 'schema')}
    schema = load(base / 'schema.json')
    original('Tcd/TMon.tcd')
    monsters = {r.key: r for r in named(source / 'Tcd/TMon.tcd')[0]}
    server_monsters = {r['wID']: r for r in tables['TMONSTERCHART']}
    skills = {r['wID'] for r in tables['TSKILLCHART']}
    joined_path = Path('Gameforge/combined-evidence/joined.json')
    joined = load(joined_path)
    sources = load(Path('Gameforge/combined-evidence/sources.json'))
    changes = []
    for entry in joined['monsters']:
        ident = entry['id']
        rec = monsters[ident]
        raw = rec.raw[-125:]
        mapped = {'wKind': int.from_bytes(rec.raw[2:4], 'little'), 'bCanSelect': raw[63],
                  'bCanAttack': raw[65], 'bTame': raw[67], 'fSize': struct.unpack_from('<f', raw, 74)[0]}
        attack_skills = struct.unpack_from('<4H', raw, 54)
        if set(attack_skills) - {0} - skills:
            raise ValueError('Missing required monster skill: ' + str(ident))
        mapped.update({f'wSkill{i + 1}': value for i, value in enumerate(attack_skills)})
        for field, value in mapped.items():
            old = server_monsters[ident][field]
            if not equal(old, value):
                changes.append({'table': 'TMONSTERCHART', 'key_column': 'wID', 'key': ident, 'column': field,
                                'before': old, 'after': value, 'source': 'Tcd/TMon.tcd', 'source_sha256': inputs['Tcd/TMon.tcd']})
    attributes = {(r['wID'], r['bLevel']): r for r in tables['TMONATTRCHART']}
    used = {r['wID'] for r in tables['TMONATTRCHART']} | {r['wMonAttr'] for r in tables['TMONSTERCHART']}
    free = iter(i for i in range(30000, 32768) if i not in used)
    restores, deferred = [], []
    restored_ids = {r['wMonID'] for r in tables['TGAMEFORGEMONSTERRESTORE']}
    for entry in joined['monsters']:
        ident, facts = entry['id'], entry['publisher']
        current = server_monsters[ident]
        donor = attributes.get((current['wMonAttr'], current['bLevel']))
        lo, hi = facts['level_range']
        hp_lo, hp_hi = facts['hp_range']
        if donor and lo <= current['bLevel'] <= hi and hp_lo <= donor['dwMaxHP'] <= hp_hi:
            continue
        reason = ('missing_original_attribute_donor' if donor is None else
                  'public_catalogue_and_detail_conflict' if facts['catalogue_and_detail_level_disagree'] else
                  'published_level_or_hp_is_a_range' if lo != hi or hp_lo != hp_hi else None)
        if reason:
            deferred.append({'domain': 'monster', 'id': ident, 'reason': reason})
            continue
        proof = sources['public_detail_sources'][f'monster:{ident}']
        if sha(Path(proof['cache_file'])) != proof['sha256'] or not entry['client_name_matches_publisher']:
            raise ValueError('Monster evidence changed or identity differs')
        if ident in restored_ids:
            raise ValueError('Already registered monster restore requires separate review')
        restores.append({'id': ident, 'name': entry['name'], 'old_level': current['bLevel'], 'old_attr': current['wMonAttr'],
                         'old_hp': donor['dwMaxHP'], 'new_attr': next(free), 'level': lo, 'hp': hp_lo,
                         'source': proof['url'], 'source_sha256': proof['sha256']})

    wanted = {r['id'] for r in load(Path('Gameforge/content-gaps/audit.json'))['global_gaps']['npcs'] if r['type'] == 2}
    placements = collections.defaultdict(list)
    for p in sorted((source / 'Tcd').glob('TNPC*.tcd')):
        match = re.fullmatch(r'TNPC([0-9a-fA-F]{8})\.tcd', p.name)
        if match and p.stat().st_size:
            # Only pin files actually supplying imported candidates.
            rows = [r for r in npcs(p.read_bytes(), int(match[1], 16) >> 16) if r['wID'] in wanted]
            if rows:
                original('Tcd/' + p.name)
                for row in rows:
                    placements[row['wID']].append(row)
    ci = {r['wItemID']: r for r in items(original('Tcd/TItem.tcd'))}
    ca = {r['wID']: r for r in fixed(original('Tcd/TItemAttr.tcd'), 'HBHHHHHHBB',
           ('wID', 'bGrade', 'min_ap', 'max_ap', 'dp', 'min_map', 'max_map', 'mdp', 'block', 'speed'))}
    cl = {r['level']: r['money'] for r in fixed(original('Tcd/TLevel.tcd'), 'BIII', ('level', 'money', 'registration', 'search'))}
    si = {r['wItemID']: r for r in tables['TITEMCHART']}
    sa = {r['wID']: r for r in tables['TITEMATTRCHART']}
    sl = {r['bLevel']: r['dwMoney'] for r in tables['TLEVELCHART']}

    def price(template, attrs, levels):
        grade = attrs[template['wAttrID']]['bGrade'] if template['wAttrID'] else template['bLevel']
        return int(struct.unpack('<f', struct.pack('<f', levels[grade] * template['fPrice']))[0] + .99)

    additions, shops = collections.defaultdict(list), []
    for proof in load(Path('.runtime/gameforge-verified-import/shop-sources.json')):
        ident = proof['id']
        if ident not in wanted or len(placements[ident]) != 1:
            raise ValueError('Nonunique or unrequested shop placement')
        n = placements[ident][0]
        if n['wMapID'] in (1501, 1502, 1503, 3000):
            deferred.append({'domain': 'npc', 'id': ident, 'reason': 'BoW-specific shop pricing is not implemented in the current server; map 3000 also lacks its route'})
            continue
        cache = Path('.runtime/gameforge-verified-import/web') / (hashlib.sha256(proof['url'].encode()).hexdigest() + '.html')
        if sha(cache) != proof['sha256'] or normal(proof['heading']) not in {normal(n['name']), normal(n['title'] + n['name'])}:
            raise ValueError('Changed shop capture or mismatched name')
        stock = []
        for table in proof['tables']:
            for cells in table['rows']:
                ids = {int(m[1]) for c in cells for link in c['links'] if (m := re.search(r'/item/details/(\d+)', link))}
                if not ids:
                    continue
                cost = re.fullmatch(r'(\d+)\s+(\d+)\s+(\d+)', cells[1]['text'])
                if len(ids) != 1 or not cost:
                    raise ValueError('Incomplete ordinary gold-shop price')
                item = next(iter(ids))
                expected = int(cost[1]) * 1000000 + int(cost[2]) * 1000 + int(cost[3])
                if item not in ci or item not in si or price(ci[item], ca, cl) != expected or price(si[item], sa, sl) != expected:
                    raise ValueError('Original/public/server stock or price differs')
                stock.append({'item_id': item, 'price': expected})
        if not stock or len({r['item_id'] for r in stock}) != len(stock):
            raise ValueError('Invalid shop stock')
        if n['wMapID'] not in {r['wMapID'] for r in tables['TMAPCHART']}:
            raise ValueError('Shop map route missing')
        additions['TNPCCHART'].append({'wID': ident, 'NC_szName2': n['title'], 'szName': n['name'], 'bType': n['bType'],
            'dwClass': n['class_index'], 'bCountryID': n['bCountryID'], 'wLocalID': 0, 'bCondition': 0,
            'bDiscountRate': 0, 'bAddProb': 0, 'wItemID': n['wItemID'], 'wMapID': n['wMapID'],
            'fPosX': n['fPosX'], 'fPosY': n['fPosY'], 'fPosZ': n['fPosZ'], 'szLocal': ''})
        additions['TNPCITEMCHART'] += [{'wNpcID': ident, 'dwItemID': r['item_id']} for r in stock]
        shops.append({'npc_id': ident, 'name': n['name'], 'url': proof['url'], 'sha256': proof['sha256'], 'stock': stock})
    plan = {'prepared_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'baseline': str(base),
            'baseline_sha256': {p.name: sha(p) for p in sorted(base.glob('*.json'))}, 'original_files_sha256': inputs,
            'source_join_sha256': sha(joined_path), 'changes': changes, 'monster_restores': restores,
            'additions': dict(additions), 'shops_imported': shops, 'deferred': deferred,
            'policies': {'monster_attributes': 'Clone the same monster current attribute row, changing only ID, level key and independently published HP. Other combat columns remain unverified.',
                         'monster_fields': 'Directly decoded original kind, select/attack/tame flags, size and four skill IDs; referenced skills must already exist. Complete skill effects and AI are not reconstructed.',
                         'npc_defaults': 'Neutral local/discount metadata follows the previously deployed 034/036 compatibility policy; these fields are not an official server export.',
                         'unresolved_content': 'Do not activate broken mission chains, spawns, drop probabilities or portals with unknown arrival heights.'}}
    (out / 'plan.json').write_text(json.dumps(plan, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    field_sql = out / 'mapped-fields.sql'
    migration(plan, schema, field_sql)
    values = ',\n'.join(f"({r['id']},{r['old_level']},{r['old_attr']},{r['old_hp']},{r['new_attr']},{r['level']},{r['hp']},N'{r['source']}','{r['source_sha256']}')" for r in restores)
    script = 'USE [TGame_gsp];\nGO\nSET XACT_ABORT ON;\nBEGIN TRANSACTION;\nGO\n'
    script += SQL_HEADER.replace('Gameforge/monster-restoration.json', 'Gameforge/server-completion/plan.json') + values + SQL_FOOTER
    script += field_sql.read_text(encoding='utf-8')
    script += additions_sql(additions, schema, 'Gameforge/server-completion/plan.json', {'TNPCITEMCHART': ['wNpcID', 'dwItemID']})
    script += 'COMMIT TRANSACTION;\nGO\n'
    sql_path = Path('Database/migrations/037_gameforge_verified_regional_fields.sql')
    if sql_path.exists():
        raise ValueError('Migration already exists')
    sql_path.write_text(script, encoding='utf-8')
    field_sql.unlink()
    print(json.dumps({'monster_restores': len(restores), 'mapped_field_changes': len(changes), 'changed_monsters': len({r['key'] for r in changes}),
                      'new_shops': len(shops), 'added_shop_rows': sum(map(len, additions.values())), 'deferred': len(deferred)}))


if __name__ == '__main__':
    main()
