"""Audit the explicit Gameforge 5.0+ target without relabelling an unknown client or inventing progression data."""
import argparse
import datetime
import hashlib
from html import escape
import json
import os
from pathlib import Path
import re

from client_facts import fixed, quests
from client_records import named
from monsters import Source
from sql_snapshot import sql


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--profile', type=Path, default=Path('Gameforge/target.json'))
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--deployment', type=Path)
    parser.add_argument('--tests', type=Path)
    parser.add_argument('--content-deployment', type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    target = json.loads(args.profile.read_text(encoding='utf-8'))
    manifest = json.loads(Path(target['reference_manifest']).read_text(encoding='utf-8'))
    expected = {r['path'].lower(): r['sha256'] for r in manifest['files']}
    source_evidence = []

    def original(relative):
        data = (args.source / relative).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != expected[relative.lower()]:
            raise ValueError('Changed source: ' + relative)
        source_evidence.append({'path': relative, 'sha256': digest})
        return data

    fee_levels = fixed(original('Tcd/TLevel.tcd'), 'BIII', ('level', 'money', 'registration', 'search'))
    missions = quests(original('TQuest.mpq'))['quests']
    original('Tcd/TRegion.tcd')
    regions, _ = named(args.source / 'Tcd/TRegion.tcd')
    levels = sql('SELECT bLevel,dwEXP,bSkillPoint,dwMoney FROM TLEVELCHART ORDER BY bLevel FOR JSON PATH;')
    wanted = set(range(1, target['player_level_target'] + 1))
    missing = sorted(wanted - {r['bLevel'] for r in levels})
    config = sql('SELECT (SELECT COUNT(*) FROM TBOWSETTINGSCHART) AS bow_settings_rows,(SELECT COUNT(*) FROM TGUILDSKILLCHART) AS guild_skill_rows FOR JSON PATH;')[0]
    constants = {}
    paths = {'native': 'Lib/Own/TProtocol/include/ProtocolBase.h',
             'login': 'Servers/TLoginSvr.Net/TLogin.Protocol/NetCode.cs',
             'map': 'Servers/TMapSvr.Net/TMap.Protocol/NetCode.cs'}
    for component, path in paths.items():
        code = Path(path).read_text(encoding='utf-8-sig')
        match = re.search(r'TVERSION\s+\(\(WORD\)\s*(0x[0-9a-fA-F]+)', code) if component == 'native' else re.search(r'ClientVersion\s*=\s*(0x[0-9a-fA-F]+)', code)
        if not match:
            raise ValueError('Unknown protocol declaration: ' + path)
        constants[component] = match[1].lower()
    if len(set(constants.values())) != 1:
        raise ValueError('Native/login/map protocol constants disagree')
    articles = [
        ('5.0.1', 'https://cz.4story.gameforge.com/news/display/585'),
        ('5.1', 'https://cz.4story.gameforge.com/news/display/623/patchnotes-51'),
        ('5.2', 'https://en.4story.gameforge.com/news/display/696/patch-notes-52'),
        ('5.3', 'https://en.4story.gameforge.com/news/display/772/patch-53-now-live'),
    ]
    captures = []
    for version, url in articles:
        host = 'https://' + url.split('/')[2]
        markup, actual_url, digest = Source(host, args.cache / version).get(url)
        if version not in markup:
            raise ValueError('Patch documentation returned unexpected content')
        captures.append({'version': version, 'url': actual_url, 'sha256': digest})
    deployed = None
    if args.deployment:
        deployed = json.loads(args.deployment.read_text(encoding='utf-8'))
        if deployed['source_sha256'] != hashlib.sha256(Path('Servers/TMapSvr.Net/TMap.Server/Map/MapService.Companion.cs').read_bytes()).hexdigest():
            raise ValueError('Stale companion deployment evidence')
    tests = None
    if args.tests:
        tests = json.loads(args.tests.read_text(encoding='utf-8'))
        source_hash = hashlib.sha256(Path('Servers/TMapSvr.Net/TMap.Server/Map/MapService.Companion.cs').read_bytes()).hexdigest()
        if tests['source_sha256'] != source_hash or tests['failed'] or tests['passed'] != tests['total']:
            raise ValueError('Stale or unsuccessful companion test evidence')
    content = None
    if args.content_deployment:
        content = json.loads(args.content_deployment.read_text(encoding='utf-8'))
        if content['tests']['failed'] or any(hashlib.sha256(Path(r['path']).read_bytes()).hexdigest() != r['sha256'] for r in content['source_files']):
            raise ValueError('Stale or unsuccessful content deployment evidence')
    features = [
        {'id': 'player_progression', 'version': '5.0.1', 'priority': 'P0',
         'status': 'exp_deployed_other_level_fields_unverified' if not missing and content else 'blocked',
         'target': 'Level 140 with a validated complete EXP/skill-point curve',
         'evidence': {'highest_server_level_row': max(r['bLevel'] for r in levels), 'missing_level_rows': missing,
                      'original_fee_chart_max': max(r['level'] for r in fee_levels), 'fee_chart_contains_exp': False}},
        {'id': 'anbari_undred', 'version': '5.0.1', 'priority': 'P0',
         'status': 'delivery_subset_deployed_monster_population_missing' if content else 'partial_evidence',
         'target': 'World, NPC, missions and spawns for the level 100+ areas',
         'evidence': {'reference_region_mentions': {name: [r.key for r in regions if any(name.casefold() in t.casefold() for t in r.names)] for name in ('Anbari', 'Undred')},
                      'reference_named_missions_level_100_plus': sum(r['bType'] == 8 and r['bLevel'] >= 100 and r['title'] != '_' for r in missions)},
         'note': 'Presence of labels/data is not proof of a complete playable region.'},
        {'id': 'wire_protocol', 'version': 'independent', 'priority': 'P0', 'status': 'internal_consistency_verified',
         'target': 'Keep the actual native/login/map wire contract aligned', 'evidence': constants,
         'note': '0x2918 is not a semantic Gameforge release number; official-binary interchangeability is unverified.'},
        {'id': 'companion_level', 'version': '5.0.1/5.1', 'priority': 'P1',
         'status': 'cap_fix_deployed' if deployed else 'cap_fix_in_source',
         'target': 'Maximum level 20; no levelling beyond it or consumption of EXP potions at the cap'},
        {'id': 'companion_point_budget', 'version': '5.0.1', 'priority': 'P1', 'status': 'needs_reconciliation',
         'target': 'Documented budget of 55 allocatable points and per-attribute limit 30',
         'evidence': {'current_creation_points': 1, 'current_level_grants_1_to_19': sum(1 if n <= 4 else 2 if n <= 9 else 3 if n <= 12 else 4 if n <= 14 else 5 for n in range(1, 20))},
         'note': 'The current creation/growth formula needs a separate audit; existing characters have not been rewritten.'},
        {'id': 'companion_two_skills', 'version': '5.1', 'priority': 'P1', 'status': 'partial',
         'target': 'Two simultaneously equipped companion skills with working effects',
         'note': 'Two equipment slots exist; several item effects are still unported.'},
        {'id': 'battle_of_worlds', 'version': '5.1', 'priority': 'P2', 'status': 'disabled_or_unverified',
         'target': 'Official BoW mode, rewards, phases and equipment transfer', 'evidence': {'settings_rows': config['bow_settings_rows']}},
        {'id': 'guild_skills_and_reserve_bag', 'version': '5.2', 'priority': 'P2', 'status': 'partial_or_unverified',
         'target': 'Guild-rank skill rules and a separate expandable companion reserve bag',
         'evidence': {'guild_skill_rows': config['guild_skill_rows']},
         'note': 'The reserve bag is distinct from the five-companion active roster. Do not replace the roster cap with 24.'},
        {'id': 'later_content', 'version': '5.3+', 'priority': 'P3', 'status': 'version_gated',
         'target': 'Retain and implement later selected-client content with its own dated rule set',
         'note': 'Do not backport conflicting later balance values into a nominal 5.0 profile without recording the change.'},
    ]
    result = {'audited_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'profile': target,
              'reference_release_remains_unknown': manifest['release_version'] is None,
              'source_files': source_evidence, 'publisher_sources': captures, 'features': features,
              'companion_deployment': deployed, 'tests': tests, 'content_deployment': content,
              'complete_gameforge_5_plus_alignment': False}
    (args.output / 'audit.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    labels = {'player_progression': 'Postup do levelu 140', 'anbari_undred': 'Anbari a Undred', 'wire_protocol': 'Protokol',
              'companion_level': 'Společník: maximum 20', 'companion_point_budget': 'Body společníka',
              'companion_two_skills': 'Dvě schopnosti společníka', 'battle_of_worlds': 'Bitva světů',
              'guild_skills_and_reserve_bag': 'Klanové schopnosti a úložiště společníků', 'later_content': 'Pozdější obsah'}
    rows = ''.join('<tr><td>' + f['priority'] + '</td><td>' + escape(f['version']) + '</td><td>' + labels[f['id']] +
                   '</td><td><code>' + f['status'] + '</code><br>' + escape(f['target']) + '</td></tr>' for f in features)
    source_links = ''.join('<li><a href="' + escape(r['url']) + '">Gameforge ' + r['version'] + '</a></li>' for r in captures)
    validation = ''
    if tests:
        test_link = escape(Path(os.path.relpath(args.tests, args.output)).as_posix())
        validation += f'<p>Linux .NET 10: {tests["passed"]} z {tests["total"]} serverových testů prošlo, včetně {len(tests["companion_cap_cases"])} případů pro hranici levelu společníka. <a href="{test_link}">Výsledky a otisk testovaného zdroje</a>.</p>'
    if deployed:
        validation += '<p>Oprava maxima společníka je nasazená v běžícím Map serveru. Ověřeno načtení herní databáze, naslouchání na portech 5816/5817 a spojení s World serverem. <a href="deployment.json">Záznam nasazení a otisky sestavení</a>. Nejde o ověření celé hry průchodem v grafickém klientovi.</p>'
    progression = f'Serverová tabulka stále postrádá levely {", ".join(map(str, missing))}.' if missing else 'Serverová tabulka nyní obsahuje všechny řádky do 140. EXP prahy jsou podle tabulky českého týmu Gameforge; body dovedností a další nezveřejněné sloupce nových řádků zatím nejsou potvrzené jako původní.'
    content_update = '' if not content else '<p><strong>Nově nasazeno:</strong> EXP do 140, 12 doručovacích questů, dvě obchodnice s 26 položkami a směrování Anbari/Undredu. Populace monster, další mise a portály zůstávají nedokončené. <a href="../progression-140/report.html">Podrobný stav, EXP tabulka a ověření</a>.</p>'
    page = f'''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>4Story — cílová verze Gameforge 5.0+</title>
<style>body{{font:16px/1.65 system-ui;color:#17263c;background:#f5f7fb;max-width:1080px;margin:40px auto;padding:0 24px}}td,th{{padding:10px;border:1px solid #d4deeb;text-align:left;vertical-align:top}}table{{border-collapse:collapse;width:100%;background:white}}a{{color:#1458b4}}code{{overflow-wrap:anywhere}}.notice{{padding:18px;background:#fff7e5;border-left:5px solid #ab7210}}</style>
<h1>Cílová verze: Gameforge 5.0 a novější</h1>
<p><a href="../content-gaps/report.html">Konkrétní chybějící questy, monstra, NPC, portály a dropy — aktuální inventura</a></p>
<p>Požadavek zaznamenán 28. 10. 2026. Minimem je řada 5.0; doloženým základním bodem jsou oficiální pravidla 5.0.1. Novější ověřené funkce mají vlastní verzi a zdroj. Původní start postavy na levelu 1 zůstává požadavkem tohoto projektu.</p>
<div class="notice"><strong>Sjednocení zatím není dokončené.</strong> Změna cílového profilu není potvrzením verze původního EXE ani dokončenou migrací všech systémů. Přesný patch místní referenční instalace zůstává neověřený.</div>
{content_update}
<h2>Největší nalezený rozdíl</h2>
<p>Gameforge 5.0.1 stanovuje maximum postavy 140. {progression} Původní klientská tabulka poplatků sahá do {max(r['level'] for r in fee_levels)}, ale EXP neobsahuje; její technický rozsah tedy neurčuje skutečný limit hraní.</p>
<p>Čísla protokolu nativního klienta, Login a Map se shodují na <code>{constants['native']}</code>. Tato hodnota určuje přenosové rozhraní a nelze ji přepsat na „5.0“ jako náhradu skutečné kompatibility.</p>
<h2>Pořadí sjednocení</h2><table><tr><th>Priorita</th><th>Verze</th><th>Oblast</th><th>Stav a cíl</th></tr>{rows}</table>
<h2>Pravidla společníků</h2>
<p>Oficiální 5.0.1 zvyšuje maximum společníka na 20. Poznámky 5.1 potvrzují konec získávání EXP na tomto maximu. Doplněna je ochrana proti dalšímu postupu a spotřebě EXP elixíru na levelu 20 či na neplatné vyšší hodnotě. Existující nadlimitní záznamy nebyly hromadně měněné.</p>
<p>Další bod k vyřešení: dokumentovaný rozpočet 55 bodů oproti současné tvorbě a růstovým přírůstkům. Úložiště přidané v 5.2 je samostatná funkce; není důvod zaměnit jeho kapacitu až 24 s počtem pěti současně vlastněných aktivních společníků.</p>
{validation}
<h2>Jak se používají různé verze</h2>
<p>Poznámky 3.x/4.x slouží jako historie a pomoc při převodu starých tabulek. Pro nový cílový stav nepřebírají přednost před pravidly 5.x. Změny 5.1, 5.2, 5.3 a pozdějších aktualizací se evidují samostatně, aby se nemíchaly neslučitelné hodnoty. Oficiální PvP režimy patří k auditu Gameforge; soukromé PvP sazby a zvýhodněné zakládání postav nejsou referencí.</p>
<p><a href="audit.json">Strojový audit a otisky zdrojů</a> · <a href="../target.json">Cílový profil</a> · <a href="../server-import/research.html">Předchozí rozbor dat</a> · <a href="../server-import/report.html">Poslední obsahová migrace</a></p>
<h2>Oficiální zdroje</h2><ul>{source_links}</ul></html>'''
    (args.output / 'report.html').write_text(page, encoding='utf-8')
    print(json.dumps({'minimum_version': target['minimum_content_version'], 'documented_anchor': target['documented_baseline'],
                      'missing_player_levels': missing, 'protocol': constants, 'companion_cap_fix_deployed': bool(deployed)}))


if __name__ == '__main__':
    main()
