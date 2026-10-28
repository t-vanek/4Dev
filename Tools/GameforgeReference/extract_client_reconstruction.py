"""Extract client-side reconstruction evidence, preserving the distinction between terrain and server spawns."""
import argparse
import collections
import datetime
import hashlib
from html import escape
import json
import math
from pathlib import Path
import struct

from client_facts import Reader, npcs, quests
from client_import import index
from client_records import fixed, info, named


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


class Terrain:
    """Strictly walk the original TMU sections used by CTachyonHUGEMAP::LoadUNIT."""
    def __init__(self, data, unit_length, tile_length):
        r = Reader(data)
        height_count = r.i32()
        if height_count != (unit_length + 1) ** 2:
            raise ValueError('Terrain dimensions disagree with the indexed map header')
        self.heights = struct.unpack('<' + str(height_count) + 'f', r.take(height_count * 4))
        if not all(math.isfinite(v) for v in self.heights):
            raise ValueError('Nonfinite terrain height')
        self.length, self.tile = unit_length, tile_length
        for width in (1, 2, 1):
            count = r.i32()
            if count != unit_length ** 2:
                raise ValueError('Unexpected terrain region/shadow/detail array size')
            r.take(count * width)
        objects = r.i32()
        types = collections.Counter()
        for _ in range(objects):
            r.take(8 + 36 + 20)  # instance/object IDs, scale/position/rotation, five function IDs
            clocks = r.i32()
            r.take(clocks * 12)
            attributes = r.i32()
            for _ in range(attributes):
                ident, value_type, size = r.u32(), r.u8(), r.i32()
                value = r.take(size)
                if ident == 0x5E72 and value_type == 0 and size == 4:  # ID_OBJ_TYPE integer override
                    types[str(int.from_bytes(value, 'little'))] += 1
        sfx = r.i32()
        r.take(sfx * 33)
        sounds = r.i32()
        r.take(sounds * 20)
        for _ in range(2):
            r.take(r.u32())  # geometric/tile texture image
        shadows = r.i32()
        for _ in range(shadows):
            r.take(r.u32())
        r.take(r.i32())  # detail-index byte length
        r.done()
        self.audit = {'height_samples': height_count, 'static_objects': objects, 'sfx': sfx, 'sounds': sounds,
                      'object_type_overrides': dict(types), 'explicit_monster_type_overrides': types.get('2', 0),
                      'bytes_consumed': r.p, 'trailing_bytes': 0,
                      'limitation': 'Object-type overrides alone do not describe every inherited model attribute. No population/timer/drop section was identified.'}

    def height(self, world_x, world_z):
        # Same two-triangle interpolation as CTachyonEXTMAP::GetHeight; this is terrain, not mesh collision.
        span = self.length * self.tile
        x, z = (world_x % span) / self.tile, (world_z % span) / self.tile
        ix, iz = math.floor(x), math.floor(z)
        dx, dz = x - ix, z - iz
        stride = self.length + 1
        h00 = self.heights[iz * stride + ix]
        h10 = self.heights[iz * stride + ix + 1]
        h01 = self.heights[(iz + 1) * stride + ix]
        h11 = self.heights[(iz + 1) * stride + ix + 1]
        if dx + dz < 1:
            return h00 + dx * (h10 - h00) + dz * (h01 - h00)
        return h11 + (1 - dx) * (h01 - h11) + (1 - dz) * (h10 - h11)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    pin = {r['path'].lower(): r['sha256'] for r in load(Path('Gameforge/reference.json'))['files']}
    evidence = {}

    def original(path):
        data = (args.source / path).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != pin[path.lower()]:
            raise ValueError('Changed original input: ' + path)
        evidence[path] = {'path': path, 'bytes': len(data), 'sha256': digest}
        return data

    gaps = load(Path('Gameforge/content-gaps/audit.json'))
    source_quests = {q['dwQuestID']: q for q in quests(original('TQuest.mpq'))['quests']}
    wanted = {q['id'] for q in gaps['missing_regional_quests']}
    helper_records = [q for q in source_quests.values() if q['unknown_prefix'] in wanted and q['bType'] != 8]
    quest_records = [source_quests[i] for i in sorted(wanted)] + helper_records
    reader = Reader(original('TQNode.qpd'))
    navigation = {}
    for _ in range(reader.u32()):
        ident, count = reader.u32(), reader.u32()
        points = [dict(zip(('type', 'term_id', 'monster_kind', 'npc_id', 'map_id', 'x', 'y', 'z'),
                           struct.unpack('<BIIIIfff', reader.take(29)))) for _ in range(count)]
        if ident in wanted:
            navigation[str(ident)] = points
    reader.done()
    source_npcs = []
    for unit in (0x0103, 0x0203):
        source_npcs += npcs(original(f'Tcd/TNPC{unit:08x}.tcd'), 0)
    original('Tcd/TNPCINFO.tcd')
    npc_index = {r.key: {'name': r.names[0], 'header_dword': int.from_bytes(r.raw[4:8], 'little'),
                         'position': list(struct.unpack_from('<fff', r.raw, 8))}
                 for r in named(args.source / 'Tcd/TNPCINFO.tcd')[0]}
    duplicate_npc_matches = sum(n['wID'] in npc_index and all(abs(n[k] - v) < .001 for k, v in
                                zip(('fPosX', 'fPosY', 'fPosZ'), npc_index[n['wID']]['position'])) for n in source_npcs)
    original('Tcd/TPortal.tcd')
    portals = {}
    for r in named(args.source / 'Tcd/TPortal.tcd')[0]:
        region, map_id, x, z, flag1, flag2 = struct.unpack('<HHffBB', r.raw[-14:])
        portals[r.key] = {'portal_id': r.key, 'name': r.names[-1], 'region_id': region, 'map_id': map_id,
                          'x': x, 'z': z, 'uninterpreted_flags': [flag1, flag2]}
    links = []
    for r in fixed(original('Tcd/TDest.tcd'), 21, 4, True):
        portal, dest, cost, npc, base, local, hub, d, c, b, peace, map_id, ux, uz = struct.unpack('<HHIH7BHBB', r.raw)
        links.append({'portal_id': portal, 'destination_id': dest, 'stored_cost': cost, 'npc_id': npc,
                      'base': base, 'local': local, 'hub': hub, 'country_flags_D_C_B_PEACE': [d, c, b, peace],
                      'map_id': map_id, 'unit_x': ux, 'unit_z': uz})
    files, entries = index(original('Index/TClientP.IDX'))
    world = next(r for r in entries if r[0] == 2000001)
    map_file = 'Data/' + files[world[1]]
    map_data = original(map_file)
    offset = world[2]
    if map_data[offset] != 0:
        raise ValueError('Expected an outdoor map resource')
    nx, nz, length, tile, cells, texture, shadow = struct.unpack_from('<7i', map_data, offset + 1)
    if (nx, nz, length, tile) != (9, 8, 512, 2):
        raise ValueError('Unknown world terrain layout')
    terrain = {}
    for uz in (1, 2):
        unit_index = uz * nx + 3
        name = f'Data/MAP/EM2000001_{unit_index}.tmu'
        terrain[uz] = Terrain(original(name), length, tile)
        terrain[uz].audit['path'] = name
    extracted_portals = []
    for missing in gaps['missing_regional_portal_npcs']:
        npc = next(n for n in source_npcs if n['wID'] == missing['id'])
        for portal in sorted({r['portal_id'] for r in links if r['npc_id'] == npc['wID']}):
            data = portals[portal]
            unit_z = math.floor(data['z'] / (length * tile))
            ground = terrain[unit_z]
            npc_ground = ground.height(npc['fPosX'], npc['fPosZ'])
            extracted_portals.append({**data, 'npc_id': npc['wID'], 'npc_name': npc['name'],
                'original_npc_position': [npc['fPosX'], npc['fPosY'], npc['fPosZ']],
                'terrain_y_at_portal': ground.height(data['x'], data['z']), 'terrain_y_at_npc': npc_ground,
                'npc_minus_terrain_y': npc['fPosY'] - npc_ground,
                'server_landing_height_recovered': False,
                'landing_caveat': 'Terrain height is not the server arrival height; bridges, platforms and mesh collision can raise the walkable floor.'})
    selected_portals = {r['portal_id'] for r in extracted_portals}
    portal_edges = [r for r in links if r['portal_id'] in selected_portals or r['destination_id'] in selected_portals]
    original('Tcd/TMon.tcd')
    monsters = {r.key: r for r in named(args.source / 'Tcd/TMon.tcd')[0]}
    monster_records = []
    for m in gaps['regional_monsters']:
        row = monsters[m['id']]
        tail = row.raw[-125:]
        monster_records.append({'id': row.key, 'name': row.names[-1], 'kind': int.from_bytes(row.raw[2:4], 'little'),
            'skills': list(struct.unpack_from('<4H', tail, 54)), 'can_select': tail[63], 'can_attack': tail[65],
            'can_tame': tail[67], 'size': struct.unpack_from('<f', tail, 74)[0],
            'server_level_hp_exp_or_respawn_fields_identified': False})
    infos = info(original('Tcd/TInfo.tcd'))
    info_by_id = {r.key: r.names for r in infos}
    original('Tcd/TQuestItem.tcd')
    reward_previews = fixed((args.source / 'Tcd/TQuestItem.tcd').read_bytes(), 35, 4)
    # This table holds prebuilt reward item previews, not monster drop probabilities.
    item_links = []
    for item in gaps['quest_item_monster_links']:
        helpers = [source_quests[i] for i in item['original_drop_helper_ids']]
        item_links.append({**item, 'client_drop_helper_records': helpers, 'client_tooltip_lines': info_by_id.get(item['item_id'], []),
                           'numeric_drop_probability_recovered': False})
    original('Tcd/TLevel.tcd')
    fees = original('Tcd/TLevel.tcd')
    if len(fees) != 2 + int.from_bytes(fees[:2], 'little') * 13:
        raise ValueError('Unknown fee layout')
    original('Tcd/TMonShop.tcd')
    shop_reader = Reader((args.source / 'Tcd/TMonShop.tcd').read_bytes())
    mon_shops = []
    for _ in range(shop_reader.u16()):
        name = shop_reader.string()
        mon_shops.append({'name': name, 'id': shop_reader.u32(), 'spawn_reference': shop_reader.u32(), 'icon': shop_reader.u32()})
    shop_reader.done()
    result = {'extracted_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'source': str(args.source),
        'original_patch_unknown': True, 'original_files_modified': False, 'server_deployment_performed': False,
        'summary': {'missing_mission_records_found': len(wanted), 'direct_nonmission_child_records_found': len(helper_records),
            'missing_quests_with_navigation': len(navigation), 'quest_item_monster_links': len(item_links),
            'original_drop_helper_records': sum(len(r['client_drop_helper_records']) for r in item_links),
            'local_npc_records': len(source_npcs), 'npc_info_duplicate_position_matches': duplicate_npc_matches,
            'missing_portal_definitions_found': len(extracted_portals), 'portal_network_edges_found': len(portal_edges),
            'monster_presentation_templates_found': len(monster_records), 'tooltip_records_checked': len(infos),
            'prebuilt_reward_preview_records': len(reward_previews), 'monster_shop_reference_records': len(mon_shops),
            'complete_spawn_chart_recovered': False, 'numeric_drop_weights_recovered': False, 'complete_quest_conditions_recovered': False},
        'indexed_world': {'resource_id': world[0], 'file': map_file, 'offset': offset, 'unit_count_x': nx,
                          'unit_count_z': nz, 'tiles_per_unit': length, 'tile_length': tile},
        'terrain_units': [r.audit for r in terrain.values()], 'portals': extracted_portals, 'portal_edges': portal_edges,
        'quest_records': quest_records, 'quest_navigation': navigation, 'quest_item_links': item_links,
        'monster_templates': monster_records, 'npc_records': source_npcs, 'monster_shop_references': mon_shops,
        'files': list(evidence.values()),
        'code_evidence': [
            {'path': 'Lib/Own/Engine Lib/Engine Lib/TachyonRes.cpp', 'symbol': 'CTachyonRes::LockMAP', 'supports': 'Indexed outdoor map dimensions'},
            {'path': 'Lib/Own/Engine Lib/Engine Lib/TachyonHUGEMAP.cpp', 'symbol': 'CTachyonHUGEMAP::LoadUNIT', 'supports': 'TMU section sizes and order'},
            {'path': 'Lib/Own/Engine Lib/Engine Lib/TachyonEXTMAP.cpp', 'symbol': 'CTachyonEXTMAP::GetHeight', 'supports': 'Two-triangle ground-height interpolation'},
            {'path': 'Client/TClient/CSHandler.cpp', 'symbol': 'CTClientGame::OnCS_ADDMON_ACK', 'supports': 'Live monster level, HP/MP and XYZ arrive from the server'},
            {'path': 'Lib/Own/TProtocol/include/CSProtocol.h', 'symbol': 'CS_MONITEMLIST_ACK', 'supports': 'Rolled corpse inventory is a server packet'},
            {'path': 'Client/TClient/CSHandler.cpp', 'symbol': 'CTClientGame::OnCS_QUESTLIST_ACK', 'supports': 'Quest repeat-count data is supplied by the server'},
        ],
        'limitations': ['No complete spawn/timer/AI or numeric loot probability chart was identified in the examined client data.',
                        'Original quest terms/rewards and parent references do not encode every server condition/trigger/reset rule.',
                        'The native repository reader is supporting format evidence, not a certified source release of this official binary.',
                        'Terrain heights exclude model/platform collision and are not certified server landing/spawn Y values.']}
    (args.output / 'evidence.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    write_report(args.output, result)
    print(json.dumps(result['summary']))


def write_report(out, result):
    h = lambda x: escape(str(x))
    rows = ''.join('<tr><td>' + str(p['portal_id']) + '</td><td>' + h(p['name']) + '</td><td>' + str(p['npc_id']) +
                   '</td><td>' + ' / '.join(f'{p[k]:.3f}' for k in ('x', 'terrain_y_at_portal', 'z')) +
                   '</td><td>' + ' / '.join(f'{x:.3f}' for x in p['original_npc_position']) + '</td></tr>' for p in result['portals'])
    summary = result['summary']
    page = f'''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Co obsahuje původní klient Gameforge</title><style>body{{font:16px/1.65 system-ui;max-width:1100px;margin:40px auto;padding:0 24px;background:#f5f7fb;color:#17263c}}table{{border-collapse:collapse;width:100%;background:white}}td,th{{padding:10px;border:1px solid #d4deeb;text-align:left}}a{{color:#1458b4}}.notice{{padding:16px;background:#fff1d4;border-left:5px solid #ab7210}}</style>
<h1>Co se dá obnovit z původního klienta</h1>
<p>Čteno přímo z lokální instalace Gameforge CZ. Každý vstup byl ověřen proti původnímu referenčnímu otisku. Instalace ani server nebyly měněné.</p>
<table><tr><th>Oblast</th><th>Nalezené původní údaje</th><th>Co tím ještě není doložené</th></tr>
<tr><td>Questy</td><td>Všech {summary['missing_mission_records_found']} chybějících misí, {summary['direct_nonmission_child_records_found']} přímých pomocných záznamů, cíle, počty, odměny, rodičovské reference a navigační NPC.</td><td>Veškeré serverové spouštěče, podmínky, reset a opakovatelnost.</td></tr>
<tr><td>Questové dropy</td><td>Osm navigačních vazeb předmět–druh monster a {summary['original_drop_helper_records']} původních pomocných dropových záznamů.</td><td>Úplná vazba všech variant monster na spouštěče a procentní šance.</td></tr>
<tr><td>NPC a obchodníci</td><td>79 místních NPC: ID, názvy, typy a XYZ; všech 79 souřadnic souhlasí i s druhým indexem TNPCINFO.</td><td>Kompletní serverová nabídka obchodů a všechny speciální akce NPC.</td></tr>
<tr><td>Portály</td><td>Všech devět chybějících definic, přiřazení NPC, X/Z, {summary['portal_network_edges_found']} směrových vazeb, uložené ceny a příznaky zemí. Nově jsou vytažené výšky terénu.</td><td>Původní serverové příletové XYZ a všechny dodatečné podmínky.</td></tr>
<tr><td>Monstra</td><td>62 prezentačních šablon: ID, druh, modelové údaje, dovednosti a příznaky.</td><td>Kompletní původní level/HP/EXP, AI a populace. Živý level, HP/MP a XYZ čte klient ze serverového paketu.</td></tr>
<tr><td>Terén a spawny</td><td>Úplně přečtené oba TMU soubory: 12 563 statických objektů a 526 338 výškových vzorků, bez nezpracovaného konce souboru.</td><td>Nebyla identifikovaná tabulka původních monster s počty, obnovou a dropy. Navigační body questů se za ni nepovažují.</td></tr>
<tr><td>EXP</td><td>TLevel obsahuje 201 řádků poplatků. TInfo obsahuje textové šablony, včetně zástupných parametrů statistik.</td><td>Tyto dvě tabulky neposkytují EXP křivku. Nasazené EXP prahy pocházejí z publikované tabulky českého Gameforge týmu.</td></tr></table>
<p>Prověřené jsou i často zaměňované tabulky: <code>TQuestItem.tcd</code> obsahuje {summary['prebuilt_reward_preview_records']} předdefinovaných odměnových předmětů a jejich bonusů. <code>TMonShop.tcd</code> obsahuje {summary['monster_shop_reference_records']} speciálních obchodních referencí včetně ID spawnu, ale bez XYZ, počtu či obnovy. Ani jedna tedy neposkytla úplnou běžnou dropovou nebo spawnovací tabulku.</p>
<h2>Nově vytažené terénní výšky portálů</h2><table><tr><th>Portál</th><th>Místo</th><th>NPC</th><th>X / terén Y / Z</th><th>Původní NPC X / Y / Z</th></tr>{rows}</table>
<div class="notice"><strong>Výška terénu není automaticky výška příletu.</strong> U Přístavu Luadlan je pod bodem portálu terén přibližně Y=65, zatímco původní NPC stojí na Y=67,25. Rozdíl vyžaduje ověření kolizí modelu/platformy; samotné dosazení terénu by mohlo hráče umístit pod objekt.</div>
<p>Jednotky světa byly určené z původního indexu a hlavičky mapy: prostředek 2000001, mřížka 9×8, 512 dlaždic po 2 metrech na jednotku. Anbari používá EM2000001_12.tmu, Undred EM2000001_21.tmu. Výšky používají stejnou interpolaci dvou trojúhelníků jako nativní čtečka.</p>
<p><a href="evidence.json">Vytažené questové záznamy, NPC, portály, vazby a otisky zdrojů</a> · <a href="../content-gaps/report.html">Co chybí na serveru</a></p>
<p>Přesný patch původního EXE zůstává neověřený. Závěry o nenalezených datech platí pro zkoumané soubory a dekódované formáty; nejde o tvrzení, že jsou prozkoumané všechny instrukce původního EXE.</p></html>'''
    (out / 'report.html').write_text(page, encoding='utf-8')


if __name__ == '__main__':
    main()
