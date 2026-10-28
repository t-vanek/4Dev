"""Refresh bounded primary Gameforge evidence for the regional gap inventory."""
import argparse
import hashlib
import json
from pathlib import Path
import re

from catalogue import Tables
from monsters import Source, text


def load(path):
    return json.loads(path.read_text(encoding='utf-8'))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--cache', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    source = Source('https://cz.4story.gameforge.com', args.cache)
    public = Path('.runtime/gameforge-normalization/public')
    monsters = [r for r in load(public / 'monster-catalogue.json')['records']
                if any(name in str(r['cells']) for name in ('Anbari', 'Undred'))]
    entries = [{'kind': 'monster', **r} for r in monsters]
    for kind, ids in [('quest', (11172, 11174, 11302)), ('npc', (26704, 26718))]:
        rows = {r['id']: r for r in load(public / (kind + '-catalogue.json'))['records']}
        entries += [{'kind': kind, **rows[i]} for i in ids]
    entries += [{'kind': 'npc', 'id': i, 'url': f'https://cz.4story.gameforge.com/npc/details/{i}'}
                for i in (26585, 26588, 26595, 26596, 26597, 26627, 26639, 26640, 26641)]
    entries += [{'kind': 'catalogue', 'id': 0, 'url': 'https://cz.4story.gameforge.com/search'}]
    records = []
    for index, row in enumerate(entries, 1):
        try:
            markup, url, digest = source.get(row['url'])
            heading = re.search(r'<h1\b[^>]*>(.*?)</h1>', markup, re.S)
            heading = text(heading[1]) if heading else ''
            is_detail = bool(heading) and heading.casefold() not in ('npc v iberii', 'npcs v iberii', 'úkoly v iberii', 'nestvůry v iberii', 'předměty v iberii')
            item = {'kind': row['kind'], 'id': row['id'], 'url': url, 'sha256': digest,
                    'heading': heading, 'is_detail': is_detail, 'cache_file': hashlib.sha256(url.encode()).hexdigest() + '.html'}
            if row['kind'] == 'monster' and is_detail:
                parser = Tables()
                parser.feed(markup)
                fields = {r[0]['text'].rstrip(':'): r[1]['text'] for t in parser.tables for r in t['rows'] if len(r) == 2}
                item['level'] = fields.get('Level')
                item['hp'] = fields.get('Život')
                drops = {}
                for table in parser.tables:
                    for cells in table['rows']:
                        if len(cells) != 4 or any(c['header'] for c in cells):
                            continue
                        ids = {int(m[1]) for c in cells for link in c['links'] if (m := re.search(r'/item/details/(\d+)', link))}
                        if len(ids) == 1:
                            ident = next(iter(ids))
                            drops[ident] = {'item_id': ident, 'label': cells[1]['text'],
                                            'frequency': cells[0]['text'], 'item_type': cells[2]['text']}
                item['published_drops'] = list(drops.values())
            records.append(item)
        except Exception as e:
            records.append({'kind': row['kind'], 'id': row['id'], 'url': row['url'], 'error': str(e)})
        if index % 5 == 0:
            print(f'Primary Gameforge pages: {index}/{len(entries)}', flush=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(records, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'pages': len(records), 'errors': sum('error' in r for r in records),
                      'published_monster_item_pairs': sum(len(r.get('published_drops', [])) for r in records)}))


if __name__ == '__main__':
    main()
