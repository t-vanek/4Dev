"""Record the public Anbari/Undred monster fields without interpreting guidance markers as spawns."""
import argparse
import hashlib
import json
from pathlib import Path

from catalogue import Tables
from monsters import bounds


def load(path):
    return json.loads(path.read_text(encoding='utf-8'))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ('catalogue', 'snapshot', 'cache', 'region-plan', 'output'):
        p.add_argument('--' + name, type=Path, required=True)
    args = p.parse_args()
    server = {r['wID']: r for r in load(args.snapshot / 'TMONSTERCHART.json')}
    plan = load(args.region_plan)
    records = []
    for row in load(args.catalogue)['records']:
        if not any(name in str(row['cells']) for name in ('Anbari', 'Undred')):
            continue
        path = args.cache / (hashlib.sha256(row['url'].encode()).hexdigest() + '.html')
        raw = path.read_bytes()
        tables = Tables()
        tables.feed(raw.decode('utf-8'))
        fields = {r[0]['text'].rstrip(':'): r[1]['text'] for t in tables.tables for r in t['rows'] if len(r) == 2}
        level, hp = bounds(fields['Level']), bounds(fields['Život'])
        ident = row['id']
        records.append({'id': ident, 'name': row['name'], 'region': row['cells'][2], 'source': row['url'],
                        'sha256': hashlib.sha256(raw).hexdigest(), 'published_levels': level, 'published_hp': hp,
                        'server_template_exists': ident in server, 'server_level': server.get(ident, {}).get('bLevel'),
                        'server_exp_unverified': server.get(ident, {}).get('wExp'),
                        'exact_variant_ambiguous': level[0] != level[1] or hp[0] != hp[1]})
    result = {'publisher_monster_records': records, 'catalogue_count': len(records),
              'ambiguous_variant_count': sum(r['exact_variant_ambiguous'] for r in records),
              'missing_server_templates': [r['id'] for r in records if not r['server_template_exists']],
              'source_guidance_points': sum(p['type'] not in (19, 20) for pts in plan['navigation'].values() for p in pts),
              'original_spawn_points': None, 'spawn_or_drop_values_fabricated': False}
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k != 'publisher_monster_records'}))


if __name__ == '__main__':
    main()
