"""Read-only inventory of missing client IDs and regional gameplay dependencies, with primary-source links."""
import argparse
import collections
import datetime
import hashlib
from html import escape
import json
import math
import os
from pathlib import Path
import re
import struct

from client_facts import items, npcs, quests
from client_records import fixed, named
from monsters import bounds


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ('source', 'snapshot', 'public', 'fresh', 'fresh-cache', 'output'):
        p.add_argument('--' + name, type=Path, required=True)
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    pinned = {r['path'].lower(): r['sha256'] for r in load(Path('Gameforge/reference.json'))['files']}
    inputs = []

    def original(path):
        data = (args.source / path).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != pinned[path.lower()]:
            raise ValueError('Changed original source: ' + path)
        inputs.append({'path': path, 'sha256': digest})
        return data

    tables = {p.stem: load(p) for p in args.snapshot.glob('*.json') if p.stem not in ('schema', 'counts')}
    sq = {r['dwQuestID']: r for r in tables['TQUESTCHART']}
    sn = {r['wID']: r for r in tables['TNPCCHART']}
    sm = {r['wID']: r for r in tables['TMONSTERCHART']}
    si = {r['wItemID']: r for r in tables['TITEMCHART']}
    ss = {r['wID']: r for r in tables['TSKILLCHART']}
    original('TQuest.mpq')
    cq = {r['dwQuestID']: r for r in quests((args.source / 'TQuest.mpq').read_bytes())['quests']}
    ci = {r['wItemID']: r for r in items(original('Tcd/TItem.tcd'))}
    cn = collections.defaultdict(list)
    for f in sorted((args.source / 'Tcd').glob('TNPC*.tcd')):
        match = re.fullmatch(r'TNPC([0-9a-fA-F]{8})\.tcd', f.name)
        if match and f.stat().st_size:
            for row in npcs(original('Tcd/' + f.name), int(match[1], 16) >> 16):
                cn[row['wID']].append(row)
    original('Tcd/TMon.tcd')
    cm = {r.key: {'id': r.key, 'name': r.names[-1], 'kind': int.from_bytes(r.raw[2:4], 'little')}
          for r in named(args.source / 'Tcd/TMon.tcd')[0]}
    original('Tcd/TSkill.tcd')
    cs = {r.key: {'id': r.key, 'name': r.names[-1]} for r in named(args.source / 'Tcd/TSkill.tcd')[0]}
    catalogues = {kind: {r['id']: r for r in load(args.public / (kind + '-catalogue.json'))['records']}
                  for kind in ('quest', 'monster', 'npc', 'item')}
    plan = load(Path('Gameforge/high-level-regions/plan.json'))
    if load(Path('Gameforge/high-level-regions/deployed-gameplay.json'))['regionPlanSha256'] != hashlib.sha256(Path('Gameforge/high-level-regions/plan.json').read_bytes()).hexdigest():
        raise ValueError('Changed applied regional plan')
    original('TQNode.qpd')
    fresh = load(args.fresh)
    for row in fresh:
        if 'error' in row:
            raise ValueError('Primary-source refresh failed: ' + row['url'])
        if hashlib.sha256((args.fresh_cache / row['cache_file']).read_bytes()).hexdigest() != row['sha256']:
            raise ValueError('Changed captured primary source')
        metadata = load((args.fresh_cache / row['cache_file']).with_suffix('.json'))
        if metadata['sha256'] != row['sha256']:
            raise ValueError('Changed primary-source capture metadata')
        row['retrieved_utc'] = metadata['retrieved_utc']
    fresh_by_id = {(r['kind'], r['id']): r for r in fresh}
    public_monsters = [r for r in load(args.public / 'monster-catalogue.json')['records']
                       if any(name in str(r['cells']) for name in ('Anbari', 'Undred'))]
    units = {0x0103: 'Anbari', 0x0203: 'Undred'}

    def region(map_id, x, z):
        return units.get(math.floor(x / 1024) | (math.floor(z / 1024) << 8)) if map_id == 0 else None

    missing_quests = []
    for q in plan['candidates']:
        ident = q['dwQuestID']
        if ident in sq:
            continue
        points = plan['navigation'][str(ident)]
        original_q = cq[ident]
        if original_q != q:
            raise ValueError('Changed original regional quest')
        pub = catalogues['quest'].get(ident)
        reason = 'objective_dependency' if any(t['bTermType'] != 1 for t in q['terms']) else 'missing_prerequisite'
        missing_quests.append({'id': ident, 'name': q['title'], 'level': q['bLevel'],
            'regions': sorted({r for pt in points if (r := region(pt['map_id'], pt['x'], pt['z']))}),
            'publisher_region': pub['cells'][-1] if pub else None, 'parent_id': q['unknown_prefix'],
            'giver_ids': sorted({pt['npc_id'] for pt in points if pt['type'] == 19}),
            'turnin_ids': sorted({pt['npc_id'] for pt in points if pt['type'] == 20}),
            'terms': q['terms'], 'reason': reason, 'source': pub['url'] if pub else None, 'missing_server_record': True})
    missing_quests.sort(key=lambda r: (r['regions'], r['level'], r['id']))
    local_npcs = [r for rows in cn.values() for r in rows if region(r['wMapID'], r['fPosX'], r['fPosZ'])]
    npc_records = []
    for n in sorted(local_npcs, key=lambda r: r['wID']):
        ident = n['wID']
        fresh_npc = fresh_by_id.get(('npc', ident))
        pub = catalogues['npc'].get(ident)
        npc_records.append({'id': ident, 'name': n['name'], 'title': n['title'], 'type': n['bType'],
            'region': region(n['wMapID'], n['fPosX'], n['fPosZ']), 'server_record_exists': ident in sn,
            'source': fresh_npc['url'] if fresh_npc and fresh_npc['is_detail'] else pub['url'] if pub else None,
            'missing_quest_giver_ids': [q['id'] for q in missing_quests if ident in q['giver_ids']],
            'missing_quest_turnin_ids': [q['id'] for q in missing_quests if ident in q['turnin_ids']],
            'original_position': [n['wMapID'], n['fPosX'], n['fPosY'], n['fPosZ']]})
    portals = {}
    original('Tcd/TPortal.tcd')
    for r in named(args.source / 'Tcd/TPortal.tcd')[0]:
        reg, map_id, x, z, _, _ = struct.unpack('<HHffBB', r.raw[-14:])
        portals[r.key] = {'id': r.key, 'name': r.names[-1], 'map_id': map_id, 'x': x, 'z': z}
    npc_portals = collections.defaultdict(set)
    for r in fixed(original('Tcd/TDest.tcd'), 21, 4, True):
        portal, dest, price, npc = struct.unpack_from('<HHIH', r.raw)
        if npc:
            npc_portals[npc].add(portal)
    current_portals = {r['wPortalID']: r for r in tables['TPORTALCHART']}
    missing_portals = [{**n, 'portal_definitions': [portals[i] for i in sorted(npc_portals[n['id']])],
                        'server_portal_ids_present': [i for i in npc_portals[n['id']] if i in current_portals]}
                       for n in npc_records if n['type'] == 8 and not n['server_record_exists']]
    spawn_by_id = {r['wID']: r for r in tables['TMONSPAWNCHART']}
    mon_spawns = collections.defaultdict(list)
    for r in tables['TMAPMONCHART']:
        if r['wSpawnID'] in spawn_by_id:
            mon_spawns[r['wMonID']].append(spawn_by_id[r['wSpawnID']])
    mon_drops = collections.Counter(r['wMonID'] for r in tables['TMONITEMCHART'])
    monster_records, drop_details = [], {}
    for row in public_monsters:
        ident = row['id']
        pub = fresh_by_id[('monster', ident)]
        if not pub['is_detail']:
            raise ValueError('Catalogue returned instead of monster detail')
        level, hp = bounds(pub['level']), bounds(pub['hp'])
        current = sm.get(ident)
        drops = pub['published_drops']
        monster_records.append({'id': ident, 'name': row['name'], 'publisher_region': row['cells'][-1],
            'source': pub['url'], 'sha256': pub['sha256'], 'published_level': level, 'published_hp': hp,
            'server_template_exists': current is not None, 'server_level': current['bLevel'] if current else None,
            'level_outside_published_range': current is not None and not level[0] <= current['bLevel'] <= level[1],
            'server_spawn_rows_any_map': len(mon_spawns[ident]),
            'server_spawn_rows_target_regions': sum(region(s['wMapID'], s['fPosX'], s['fPosZ']) is not None for s in mon_spawns[ident]),
            'server_drop_rows': mon_drops[ident], 'published_possible_item_count': len(drops),
            'missing_drop_table': bool(drops) and mon_drops[ident] == 0})
        drop_details[str(ident)] = [{'item_id': d['item_id'], 'client_name': ci.get(d['item_id'], {}).get('szNAME', d['label']),
                                    'frequency_label': d['frequency'], 'server_item_template_exists': d['item_id'] in si}
                                   for d in drops]
    quest_items = []
    for q in missing_quests:
        for pt in plan['navigation'][str(q['id'])]:
            if pt['type'] != 2 or not pt['monster_kind']:
                continue
            item, kind = pt['term_id'], pt['monster_kind']
            helpers = [r['dwQuestID'] for r in cq.values() if r['unknown_prefix'] == q['id'] and r['bType'] == 4
                       and any(t['bTermType'] == 5 and t['dwTermID'] == item for t in r['terms'])]
            quest_items.append({'item_id': item, 'name': ci[item]['szNAME'], 'quest_id': q['id'], 'quest_name': q['name'],
                'source': q['source'], 'monster_kind': kind, 'monster_name': cm.get(kind, {}).get('name'),
                'server_item_template_exists': item in si, 'original_drop_helper_ids': helpers,
                'missing_drop_helper_ids': [i for i in helpers if i not in sq],
                'quantity': next((t['bCount'] for t in q['terms'] if t['bTermType'] == 2 and t['dwTermID'] == item), None),
                'numeric_drop_probability_published': False})
    global_quests = [{'id': i, 'name': q['title'], 'type': q['bType'], 'level': q['bLevel'],
                      'source': catalogues['quest'].get(i, {}).get('url')}
                     for i, q in sorted(cq.items()) if i not in sq]
    global_npcs = [{'id': i, 'name': rows[0]['name'], 'title': rows[0]['title'], 'type': rows[0]['bType'],
                    'maps': sorted({r['wMapID'] for r in rows}), 'source': catalogues['npc'].get(i, {}).get('url') or
                    (fresh_by_id[('npc', i)]['url'] if ('npc', i) in fresh_by_id else None)}
                   for i, rows in sorted(cn.items()) if i not in sn]
    global_monsters = [{**r, 'source': catalogues['monster'].get(i, {}).get('url')} for i, r in sorted(cm.items()) if i not in sm]
    global_items = [{'id': i, 'name': r['szNAME'], 'source': catalogues['item'].get(i, {}).get('url')} for i, r in sorted(ci.items()) if i not in si]
    global_skills = [r for i, r in sorted(cs.items()) if i not in ss]
    spawns = {name: {'server_spawn_points': sum(region(r['wMapID'], r['fPosX'], r['fPosZ']) == name for r in spawn_by_id.values()),
                     'original_total_spawn_count_known': False,
                     'quest_guidance_points': sum(region(pt['map_id'], pt['x'], pt['z']) == name and pt['type'] not in (19, 20)
                                                 for pts in plan['navigation'].values() for pt in pts)} for name in units.values()}
    summary = {'regional_missing_missions': len(missing_quests), 'regional_missing_missions_with_objectives': sum(q['reason'] == 'objective_dependency' for q in missing_quests),
        'regional_missing_missions_with_parent_gap': sum(q['reason'] == 'missing_prerequisite' for q in missing_quests),
        'regional_npc_records': len(npc_records), 'regional_missing_npcs': sum(not n['server_record_exists'] for n in npc_records),
        'regional_missing_merchants': sum(n['type'] == 2 and not n['server_record_exists'] for n in npc_records),
        'regional_missing_portal_npcs': len(missing_portals), 'regional_public_monster_records': len(monster_records),
        'regional_public_pve_monster_records': sum(r['publisher_region'] in ('Anbari', 'Undred') for r in monster_records),
        'regional_public_mission_monster_records': sum(r['publisher_region'] not in ('Anbari', 'Undred') for r in monster_records),
        'regional_missing_monster_templates': sum(not r['server_template_exists'] for r in monster_records),
        'regional_missing_drop_tables': sum(r['missing_drop_table'] for r in monster_records),
        'published_monster_item_pairs': sum(len(r) for r in drop_details.values()),
        'unique_published_drop_items': len({d['item_id'] for drops in drop_details.values() for d in drops}),
        'quest_item_monster_links_unimplemented': len(quest_items),
        'global_missing_mission_records': sum(r['type'] == 8 for r in global_quests), 'global_missing_quest_records_including_helpers': len(global_quests),
        'global_missing_npc_records': len(global_npcs), 'global_missing_merchant_npcs': sum(r['type'] == 2 for r in global_npcs),
        'global_missing_monster_templates': len(global_monsters), 'global_missing_item_templates': len(global_items), 'global_missing_skill_templates': len(global_skills)}
    result = {'audited_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'scope': 'Detailed Anbari/Undred dependencies; global selected-client ID gaps as an appendix.',
        'read_only_sql': True, 'summary': summary, 'spawns': spawns, 'missing_regional_quests': missing_quests,
        'regional_npcs': npc_records, 'missing_regional_portal_npcs': missing_portals, 'regional_monsters': monster_records,
        'quest_item_monster_links': quest_items,
        'global_gaps': {'quests': global_quests, 'npcs': global_npcs, 'monsters': global_monsters, 'items': global_items, 'skills': global_skills},
        'client_inputs': inputs, 'sql_inputs': [{'file': p.name, 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(args.snapshot.glob('*.json'))],
        'limitations': ['A missing client ID is not necessarily a missing outdoor monster or player-visible quest.',
                       'Catalogue drop pairs are possible-item memberships, not required one-to-one SQL rows or numeric probabilities.',
                       'Quest navigation locations are not an original population/spawn/timer export.',
                       'Regional published monsters include one Undred mission-map record; it is shown separately.']}
    (args.output / 'audit.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    (args.output / 'drops.json').write_text(json.dumps(drop_details, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    sources = [{k: v for k, v in r.items() if k != 'published_drops'} for r in fresh]
    (args.output / 'publisher-sources.json').write_text(json.dumps(sources, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    write_reports(args.output, result, drop_details)
    print(json.dumps(summary))


def write_reports(out, audit, drop_details):
    def h(value):
        return escape(str(value))
    def linked(name, url):
        return '<a href="' + h(url) + '">' + h(name) + '</a>' if url else h(name)
    def table(headers, rows):
        return '<table><thead><tr>' + ''.join('<th>' + h(x) + '</th>' for x in headers) + '</tr></thead><tbody>' + ''.join('<tr>' + ''.join('<td>' + str(c) + '</td>' for c in r) + '</tr>' for r in rows) + '</tbody></table>'
    def page(title, content):
        return f'''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{h(title)}</title>
<style>body{{font:15px/1.6 system-ui;max-width:1400px;margin:35px auto;padding:0 22px;background:#f5f7fb;color:#17263c}}table{{border-collapse:collapse;width:100%;background:white;margin:18px 0}}th,td{{border:1px solid #d4deeb;padding:8px;text-align:left;vertical-align:top}}a{{color:#1458b4}}.notice{{padding:16px;background:#fff0d3;border-left:5px solid #ab7210}}input{{font:inherit;padding:9px;width:min(600px,90%)}}code{{overflow-wrap:anywhere}}</style>
<h1>{h(title)}</h1><input id="filter" type="search" aria-label="Filtrovat tabulky" placeholder="Filtrovat podle ID, názvu nebo oblasti…">{content}
<script>document.getElementById('filter').addEventListener('input',e=>{{const q=e.target.value.toLocaleLowerCase('cs');for(const r of document.querySelectorAll('tbody tr'))r.hidden=!r.textContent.toLocaleLowerCase('cs').includes(q);}});</script></html>'''
    s = audit['summary']
    quests = table(('ID', 'Quest', 'Level', 'Oblast / evidence webu', 'Zadavatel → příjemce', 'Co chybí'),
        [(q['id'], linked(q['name'], q['source']), q['level'], h(', '.join(q['regions'])) + '<br>Web: ' + h(q['publisher_region']),
          h(', '.join(map(str, q['giver_ids']))) + ' → ' + h(', '.join(map(str, q['turnin_ids']))),
          'Záznam mise a podmínky pro boj/sběr/interakci' if q['reason'] == 'objective_dependency' else 'Záznam mise; nedoplněný předchozí článek řetězce') for q in audit['missing_regional_quests']])
    portals = table(('NPC ID', 'Oblast', 'NPC / zdroj', 'Portál ID a název', 'Souřadnice NPC X / Y / Z'),
        [(n['id'], h(n['region']), linked(n['name'], n['source']), '<br>'.join(h(str(p['id']) + ' — ' + p['name']) for p in n['portal_definitions']),
          h(' / '.join(f'{v:.2f}' for v in n['original_position'][1:]))) for n in audit['missing_regional_portal_npcs']])
    monsters = table(('ID', 'Monster / zdroj', 'Oblast', 'Level na webu / serveru', 'Šablona', 'Spawny v Anbari/Undredu', 'Dropy SQL / veřejné předměty'),
        [(m['id'], linked(m['name'], m['source']), h(m['publisher_region']), h(str(m['published_level']) + ' / ' + str(m['server_level'])),
          'Existuje' if m['server_template_exists'] else 'CHYBÍ', m['server_spawn_rows_target_regions'],
          str(m['server_drop_rows']) + ' / <a href="drops/' + str(m['id']) + '.html">' + str(m['published_possible_item_count']) + '</a>') for m in audit['regional_monsters']])
    drops = table(('Předmět ID', 'Předmět z původního klienta', 'Quest ID / zdroj', 'Druh monster', 'Chybějící pomocné záznamy dropu'),
        [(d['item_id'], h(d['name']), linked(str(d['quest_id']) + ' — ' + d['quest_name'], d['source']),
          h(str(d['monster_kind']) + ' — ' + str(d['monster_name'])), h(', '.join(map(str, d['missing_drop_helper_ids'])))) for d in audit['quest_item_monster_links']])
    npcs = table(('NPC ID', 'NPC', 'Oblast', 'Serverový záznam', 'Chybějící questy zadávané NPC'),
        [(n['id'], linked(n['name'] + ' — ' + n['title'], n['source']), h(n['region']), 'Existuje' if n['server_record_exists'] else 'CHYBÍ',
          h(', '.join(map(str, n['missing_quest_giver_ids'])))) for n in audit['regional_npcs']])
    summary = table(('Kategorie', 'Aktuální stav'), [
        ('Questy', f'{s["regional_missing_missions"]} chybějících misí: {s["regional_missing_missions_with_objectives"]} s dalšími cíli, {s["regional_missing_missions_with_parent_gap"]} s chybějícím předchozím článkem. 12 doručovacích misí již doplněno.'),
        ('Monstra', f'{s["regional_missing_monster_templates"]} chybějících šablon z {s["regional_public_monster_records"]} veřejně uvedených ID. Katalog zahrnuje {s["regional_public_pve_monster_records"]} PvE záznamů a jeden misijní záznam.'),
        ('Spawny', 'Anbari: 0; Undred: 0. Počet původních chybějících spawnů není z těchto zdrojů známý.'),
        ('Obchodníci', 'Lamian 26704 a Elina 26718 existují, každá má 13 doložených nabídek. V těchto dvou oblastech nechybí obchodník typu 2.'),
        ('NPC', f'{s["regional_npc_records"]} původních místních NPC, {s["regional_missing_npcs"]} chybí — všechna jsou portálová. Existující NPC mohou stále postrádat questové návaznosti.'),
        ('Dropy', f'{s["regional_missing_drop_tables"]} monster s veřejně uvedenou kořistí nemá žádný řádek TMONITEMCHART. Dále {s["quest_item_monster_links_unimplemented"]} konkrétních neimplementovaných vazeb questový předmět–monster.')])
    content = '<p>Aktuální SQL snapshot, původní klient a čerstvě načtených 77 stránek <a href="https://cz.4story.gameforge.com/search">oficiální databáze Gameforge</a>. Jde o čtení a porovnání; herní data se tímto auditem nemění.</p>' + summary
    content += '<div class="notice">Chybějící šablona, chybějící spawn a neověřená hodnota jsou odlišné věci. 126 misí zahrnuje i národní varianty a návazné úkoly, které web zařazuje do hlavních měst či Vysočiny. Nejde o 126 unikátních názvů. Jeden z 62 monster záznamů patří do Undred (Mise).</div>'
    client_report = Path(os.path.relpath(Path('Gameforge/client-reconstruction/report.html'), out)).as_posix()
    content += '<p><a href="#quests">Questy</a> · <a href="#monsters">Monstra</a> · <a href="#portals">Portály</a> · <a href="#drops">Dropy</a> · <a href="#npcs">NPC</a> · <a href="global.html">Celý původní klient: zbývající ID</a> · <a href="' + escape(client_report, quote=True) + '">Co lze vyčíst přímo z původního klienta</a> · <a href="audit.json">Úplný audit JSON</a> · <a href="publisher-sources.json">77 primárních zdrojů a otisky</a></p>'
    content += '<h2 id="quests">Chybějící mise — úplný regionální seznam</h2>' + quests
    content += '<h2 id="monsters">Monstra — úplný seznam publikovaných ID</h2><p>Nulové spawny zde označují cílové PvE jednotky světa, nikoli počet spawnů na případných jiných misijních mapách. Pět záznamů stále používá serverový level 90 mimo publikované hodnoty; přesné zveřejněné rozsahy jsou v JSON.</p>' + monsters
    content += '<h2 id="portals">Chybějící portálová NPC</h2><p>ID a vazby portálů pocházejí z původního TDest/TPortal; odkazy vedou na skutečně načtené detaily NPC Gameforge. Nejde jen o chybějící jméno: je potřeba doplnit i serverový portál, přílet a směry cestování.</p>' + portals
    content += '<h2 id="drops">Konkrétní questové předměty s vazbou na monstra</h2><p>Předměty existují v klientovi i SQL. Chybí jejich questy a uvedené pomocné dropové záznamy. Navigační vazba není důkazem procentní šance na drop.</p>' + drops
    content += f'<p>Databáze Gameforge uvádí celkem {s["published_monster_item_pairs"]:,} vazeb monster–možný předmět pro {s["unique_published_drop_items"]} různých předmětů. Kompletní seznamy jsou pod číslem veřejných předmětů u každého monstra. Nejde o stejný počet nutných SQL řádků: skupinová pravidla mohou pokrýt více předmětů. Web používá slovní četnost Běžné / Neobvyklé / Vzácné; číselné váhy z ní nelze převzít.</p>'
    content += '<h2 id="npcs">Všechna původní místní NPC a chybějící zadávané questy</h2>' + npcs
    (out / 'report.html').write_text(page('Co chybí — Anbari a Undred, Gameforge 5.0+', content), encoding='utf-8')
    g = audit['global_gaps']
    global_content = '<p>Porovnání všech ID vybraného původního klienta se současným serverem. Tyto počty nejsou omezené na Anbari/Undred. Uvedený klient má neověřené přesné číslo patche; samotná absence ID neprokazuje, že patří do konkrétního patche 5.0.1.</p><p><a href="report.html">Zpět na podrobný regionální audit</a></p>'
    global_content += f'<p>{len(g["quests"])} questových záznamů včetně {s["global_missing_mission_records"]} misí; {len(g["monsters"])} monster šablon; {len(g["npcs"])} NPC včetně {s["global_missing_merchant_npcs"]} obchodníků typu 2; {len(g["items"])} předmětů; {len(g["skills"])} dovedností. Ostatní questové záznamy zahrnují pomocné skripty a nabídky, nikoli jen hráčské mise.</p>'
    for key, title in [('npcs', 'Chybějící NPC a obchodníci'), ('monsters', 'Chybějící šablony monster'), ('quests', 'Chybějící questové záznamy'), ('items', 'Chybějící předměty'), ('skills', 'Chybějící dovednosti')]:
        global_content += '<h2>' + title + '</h2>' + table(('ID', 'Název', 'Typ / doplněk'),
            [(r['id'], linked(r['name'], r.get('source')), h(str(r.get('type', '')) + ('; level ' + str(r['level']) if 'level' in r else ''))) for r in g[key]])
    (out / 'global.html').write_text(page('Zbývající ID v celém původním klientovi', global_content), encoding='utf-8')
    drop_dir = out / 'drops'
    drop_dir.mkdir(exist_ok=True)
    for m in audit['regional_monsters']:
        body = '<p><a href="../report.html#monsters">Zpět na monstra</a> · ' + linked('Oficiální zdroj kořisti', m['source']) + '</p>'
        body += f'<p>Server má {m["server_drop_rows"]} řádků dropů pro toto ID; na webu je {m["published_possible_item_count"]} možných předmětů. Názvy níže jsou z vybraného původního klienta, četnost z veřejného zdroje. Slovní četnost neurčuje numerickou pravděpodobnost.</p>'
        body += table(('Předmět ID', 'Klientský název', 'Veřejná četnost', 'Šablona předmětu na serveru'),
            [(d['item_id'], h(d['client_name']), h(d['frequency_label']), 'Existuje' if d['server_item_template_exists'] else 'CHYBÍ') for d in drop_details[str(m['id'])]])
        (drop_dir / (str(m['id']) + '.html')).write_text(page(str(m['id']) + ' — ' + m['name'] + ': možné dropy', body), encoding='utf-8')


if __name__ == '__main__':
    main()
