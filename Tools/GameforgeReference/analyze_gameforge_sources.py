"""Research Gameforge implementation evidence, preserving source/version boundaries and quest navigation links."""
import argparse
import collections
import datetime
import hashlib
from html import escape
from html.parser import HTMLParser
import json
import math
from pathlib import Path
import re
import sqlite3
import struct
from urllib.parse import urlparse
from urllib.error import URLError
from urllib.request import Request, urlopen
import zlib

from catalogue import quest_facts
from client_facts import Reader, quests


SOURCES = [
    ('publisher_database_guide', 'Oficiální návod k veřejné databázi',
     'https://forum.4story.gameforge.com/forum/thread/519-guida-utilizzo-del-glossario-e-della-banca-dati/'),
    ('patch_4_0_4_1', 'Gameforge: změny 4.0 / 4.1 (2013)',
     'https://pl.4story.gameforge.com/news/display/317/40_41_patchnotes_rise_of_the_dragons'),
    ('dungeons_2020', 'Gameforge: nové podoby dungeonů (2020)',
     'https://en.4story.gameforge.com/news/display/1063/a-time-for-new-discoveries'),
    ('quest_example', 'Gameforge: The Battle of Narwashi — quest 4715',
     'https://en.4story.gameforge.com/quest/details/4715'),
    ('private_wiki_scope', '4Classic Wiki — deklarovaný soukromý server', 'https://wiki.4classic.eu/'),
]


class Chain(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.depth = 0
        self.stack = []
        self.nodes = set()
        self.edges = set()

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'ul':
            if self.depth or 'tree' in attrs.get('class', '').split():
                self.depth += 1
        elif self.depth and tag == 'li':
            self.stack.append(None)
        elif self.depth and tag == 'a' and self.stack:
            match = re.search(r'/quest/details/(\d+)(?:/|$)', attrs.get('href', ''))
            if match:
                ident = int(match[1])
                self.stack[-1] = ident
                self.nodes.add(ident)
                if len(self.stack) > 1 and self.stack[-2] is not None:
                    self.edges.add((self.stack[-2], ident))

    def handle_endtag(self, tag):
        if self.depth and tag == 'li' and self.stack:
            self.stack.pop()
        elif self.depth and tag == 'ul':
            self.depth -= 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--public', type=Path, required=True)
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--research-cache', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    args.research_cache.mkdir(parents=True, exist_ok=True)
    manifest = json.loads(Path('Gameforge/reference.json').read_text(encoding='utf-8'))
    pinned = {r['path'].lower(): r['sha256'] for r in manifest['files']}

    def original(relative):
        data = (args.source / relative).read_bytes()
        if hashlib.sha256(data).hexdigest() != pinned[relative.lower()]:
            raise ValueError('Changed reference: ' + relative)
        return data

    reader = Reader(original('TQNode.qpd'))
    group_count = reader.u32()
    navigation = collections.defaultdict(list)
    for _ in range(group_count):
        ident, count = reader.u32(), reader.u32()
        for _ in range(count):
            kind, term, mon, npc, map_id, x, y, z = struct.unpack('<BIIIIfff', reader.take(29))
            if not all(math.isfinite(n) for n in (x, y, z)):
                raise ValueError('Invalid navigation position')
            navigation[ident].append({'type': kind, 'term_id': term, 'monster_kind': mon,
                                     'npc_id': npc, 'map_id': map_id, 'x': x, 'y': y, 'z': z})
    reader.done()
    client = {r['dwQuestID']: r for r in quests(original('TQuest.mpq'))['quests']}
    server = {r['dwQuestID']: r for r in json.loads((args.snapshot / 'TQUESTCHART.json').read_text(encoding='utf-8'))}
    missing_monsters = set(json.loads((args.output / 'plan.json').read_text(encoding='utf-8'))['initial_missing_ids']['monster'])
    with sqlite3.connect(args.public / 'quest-details.sqlite') as db:
        public = {i: json.loads(zlib.decompress(blob)) for i, blob in db.execute("SELECT id,facts FROM details WHERE kind='quest'")}
    links = []
    disagreements = []
    published_edges = set()
    for ident, points in sorted(navigation.items()):
        start = sorted({p['npc_id'] for p in points if p['type'] == 19 and p['npc_id']})
        end = sorted({p['npc_id'] for p in points if p['type'] == 20 and p['npc_id']})
        pub = public.get(ident)
        web = None
        chain = Chain()
        if pub:
            payload = (args.cache / (hashlib.sha256(pub['url'].encode()).hexdigest() + '.html')).read_bytes()
            if hashlib.sha256(payload).hexdigest() != pub['sha256']:
                raise ValueError('Changed cached public evidence')
            markup = payload.decode('utf-8')
            web = quest_facts(markup)
            chain.feed(markup)
            published_edges.update(chain.edges)
            if start and web['giver_id'] not in start:
                disagreements.append({'quest_id': ident, 'client_givers': start, 'published_giver': web['giver_id'], 'url': pub['url']})
        links.append({'quest_id': ident, 'type': client[ident]['bType'] if ident in client else None,
                      'absent_from_server': ident not in server, 'giver_ids': start, 'turnin_ids': end,
                      'published_giver': web['giver_id'] if web else None,
                      'giver_matches_public': bool(start and web and web['giver_id'] in start),
                      'published_chain_edges': sorted(chain.edges),
                      'public_source': pub['url'] if pub else None, 'navigation': points})
    captures = []
    allowed = {'forum.4story.gameforge.com', 'pl.4story.gameforge.com', 'en.4story.gameforge.com', 'wiki.4classic.eu'}
    for ident, title, url in SOURCES:
        if urlparse(url).hostname not in allowed:
            raise ValueError('Unexpected research host')
        key = hashlib.sha256(url.encode()).hexdigest()
        path = args.research_cache / (key + '.html')
        if not path.exists():
            try:
                with urlopen(Request(url, headers={'User-Agent': '4Dev-Gameforge-reference-audit/1.0'}), timeout=25) as response:
                    if urlparse(response.url).hostname not in allowed:
                        raise ValueError('Unexpected research redirect')
                    payload = response.read(5_000_001)
                    if len(payload) > 5_000_000:
                        raise ValueError('Oversized research page')
                path.write_bytes(payload)
            except (URLError, TimeoutError) as error:
                captures.append({'id': ident, 'title': title, 'url': url, 'capture_error': str(error),
                                 'scope': 'URL inspected in browser search; no raw capture available'})
                continue
        captures.append({'id': ident, 'title': title, 'url': url, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                         'scope': 'Own private-server identity only; excluded as a Gameforge numeric baseline'
                         if ident == 'private_wiki_scope' else 'Publisher documentation; date/version context must be retained'})
    missing_linked = [r for r in links if r['absent_from_server'] and r['giver_ids'] and r['turnin_ids']]
    summary = {'navigation_groups': group_count, 'navigation_points': sum(map(len, navigation.values())),
               'quests_with_giver_ids': sum(bool(r['giver_ids']) for r in links),
               'quests_with_turnin_ids': sum(bool(r['turnin_ids']) for r in links),
               'missing_server_quests_with_both_npc_links': len(missing_linked),
               'npc_giver_matches_public': sum(r['giver_matches_public'] for r in links),
               'npc_giver_disagreements': len(disagreements), 'published_chain_edges': len(published_edges),
               'points_referencing_missing_monsters': sum(p['monster_kind'] in missing_monsters
                                                        for pts in navigation.values() for p in pts)}
    result = {'analyzed_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'summary': summary,
              'client_evidence': {k: pinned[k.lower()] for k in ('TQNode.qpd', 'TQuest.mpq')},
              'sources': captures, 'quest_links': links, 'giver_disagreements': disagreements,
              'published_chain_edges': sorted(published_edges),
              'limitations': ['Navigation anchors are not a complete spawn chart with counts, timers or AI.',
                              'Published quest-tree edges do not encode every server condition or repeat/reset policy.',
                              'Term 21 must not be blindly joined to a legacy spawn ID; version semantics require a separate implementation.',
                              'Exact selected client patch is unknown; no release is inferred from the PE timestamp.',
                              'No complete official server database was located in the sources checked here.']}
    (args.output / 'research.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    source_links = ''.join('<li><a href="' + escape(r['url']) + '">' + escape(r['title']) + '</a></li>' for r in captures)
    page = f'''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Gameforge — podklady pro implementaci</title>
<style>body{{font:16px/1.65 system-ui;color:#17263c;background:#f5f7fb;max-width:1040px;margin:38px auto;padding:0 24px}}table{{border-collapse:collapse;background:white;width:100%}}td,th{{border:1px solid #d4deeb;padding:10px;text-align:left;vertical-align:top}}h1{{line-height:1.2}}a{{color:#1458b4}}code{{overflow-wrap:anywhere}}.notice{{background:#fff7e5;border-left:5px solid #ab7210;padding:16px}}</style>
<h1>Podklady pro implementaci podle Gameforge</h1>
<p><strong>Aktuální cíl je Gameforge 5.0 a novější.</strong> <a href="../version-target/report.html">Audit cílových verzí a priorit</a>. Starší poznámky níže jsou historické srovnání, nikoli cílová pravidla 5.x.</p>
<p>Analýza 28. 10. 2026. Místní Gameforge CZ klient zůstává pevnou referencí; přesné číslo patche není potvrzené. Veřejné podklady z různých let jsou evidované odděleně.</p>
<div class="notice">Nebyla nalezena kompletní oficiální serverová databáze. Z klienta a veřejných stránek lze obnovit více návazností, ale nikoli bez dalšího doplnit původní AI, všechny podmínky, spawnovací časy a dropovací pravděpodobnosti.</div>
<h2>Nově zjištěné návaznosti</h2>
<p><code>TQNode.qpd</code> obsahuje {summary['navigation_points']} navigačních bodů ve {group_count} skupinách. Nativní čtečka a definice <code>QTT_TSTART_POS=19</code> a <code>QTT_TCOMP_POS=20</code> potvrzují význam počátečního a cílového NPC. Jde o ID a souřadnice, ne pouze názvy.</p>
<p>Získáno {summary['quests_with_giver_ids']} odkazů na zadávající NPC a {summary['quests_with_turnin_ids']} na odevzdávající NPC. Oba odkazy jsou dostupné u <strong>{len(missing_linked)} questů chybějících v původním serverovém snapshotu</strong>. S nezávisle zachyceným veřejným zadavatelem souhlasí {summary['npc_giver_matches_public']} případů; {len(disagreements)} rozdílů je označeno k prověření. Veřejné stromy navíc poskytly {len(published_edges)} různých hran návazností.</p>
<p>Tyto odkazy zpřesňují budoucí import misí a jejich dokončovacích kroků. Samy neurčují podmínky povolání, denní reset, opakovatelnost, případné skriptové větvení ani kompletní obsah instancí. V navigačních bodech nebyl nalezen odkaz na žádné ze 140 chybějících monster.</p>
<h2>Co lze použít a co je potřeba dořešit</h2>
<table><tr><th>Oblast</th><th>Doložené podklady</th><th>Implementační závěr</th></tr>
<tr><td>Předměty</td><td>Identita, typ, atributový odkaz, používání, obchodovatelnost a další číselná pole klienta; veřejné ceny a statistiky.</td><td>Odlišit přesné klientské hodnoty od serverových polí převzatých z kompatibilní šablony. Zbraně vyžadují ověřené revize a rychlosti; shodný vzhled je nepotvrzuje.</td></tr>
<tr><td>NPC a portály</td><td>Přesné souřadnice v <code>TNPC*.tcd</code>, země a typ. <code>TDest.tcd</code> obsahuje také vazbu NPC–portál.</td><td>Registrace NPC nestačí bez obchodu, cíle portálu nebo příslušné akce. Devět mapových cest v připraveném importu je místní infrastruktura jednoho serveru.</td></tr>
<tr><td>Questy</td><td>Cíle a odměny v <code>TQuest.mpq</code>, NPC přes navigaci, veřejné stromy a zadavatelé.</td><td>Rekonstruovat odděleně mise, dokončovací kroky, recepty a interní akce. Doložit spouštěče i podmínky, teprve pak aktivovat konkrétní řetězec.</td></tr>
<tr><td>Monstra a spawny</td><td>Modely, výbava, ID dovedností a příznaky v klientu; publikované HP/levely u části monster; navigační body.</td><td>Navigační ID nelze automaticky považovat za ID staré SQL spawnovací tabulky. Pro bojová monstra zbývají statistiky, AI, počty, obnova, loot a odměny.</td></tr>
<tr><td>Dungeony</td><td>Oznámení Gameforge z roku 2020 popisuje nové podoby instancí a oddělené vybavení.</td><td>Modely, mapy a vstupenky nejsou hotová instance. Je třeba řešit vstup, vlastní vybavení instance, postup, odměny a návrat. Stará pravidla z roku 2013 nelze bez ověření přenést na verzi 2020.</td></tr>
<tr><td>Verze a wiki</td><td>Oficiální změny 4.0/4.1 měnily statistiky a questy. Wiki 4Classic sama deklaruje soukromý server.</td><td>4Classic nepoužívat jako číselný základ Gameforge. Informace z Fandomu se při tomto průzkumu nepodařilo ověřit kvůli omezenému přístupu.</td></tr></table>
<h2>Rozdíly verzí, které přímo ovlivňují kód</h2>
<p>Oznámení 4.0/4.1 z roku 2013 zmiňuje změny zdraví/obrany monster, odměn a opakovatelných úkolů. Popisuje i veteránský start a dovednosti v základní variantě na úrovni nula. Tyto historické vlastnosti nejsou samy o sobě důkazem aktuálního nastavení místní instalace; požadovaný začátek postavy na levelu 1 zůstává zachovaný.</p>
<p>Oznámení z roku 2020 zase dokumentuje nové podoby dungeonů s omezením přinesené výbavy. Podporuje potřebu samostatné implementace dungeonového režimu; neuvádí kompletní serverové tabulky. Jde o popis pravidel, ne zdroj přesných dropovacích sazeb nebo AI.</p>
<h2>Další práce v pořadí závislostí</h2>
<ol><li>Dokončit a nasadit otestované recepty a ověřené NPC/portálové vazby.</li><li>Vytvořit kandidáty běžných misí spojením NPC start/cíl, veřejných řetězců a původních cílů. Rozpory ponechat jako konkrétní blokace.</li><li>Vyřešit význam cílových typů novějšího klienta, zvláště typu 21, a odměny, které současný server ještě neobsluhuje.</li><li>Rekonstruovat po jednotlivých mapách spawny a combat; přesné hodnoty držet odděleně od odhadů.</li><li>Implementovat dungeonové režimy a projít konkrétní cestu od vstupenky po návrat a uložení odměny.</li></ol>
<h2>Podklady</h2><ul>{source_links}</ul>
<p><a href="research.json">Strojová analýza, všechny navigační vazby, veřejné hrany a otisky</a> · <a href="plan.json">Připravený serverový import a přesné odvozené politiky</a></p>
</html>'''
    (args.output / 'research.html').write_text(page, encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
