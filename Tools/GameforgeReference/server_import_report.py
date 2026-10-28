"""Archive the verified server deployment and write a Czech report that separates facts from reconstruction."""
import argparse
import datetime
import hashlib
from html import escape
import json
from pathlib import Path
import subprocess


def load(path):
    return json.loads(path.read_text(encoding='utf-8'))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    out, runtime = args.output, args.runtime
    plan = load(out / 'plan.json')
    plan_hash = sha(out / 'plan.json')
    live, probe = load(out / 'live-verification.json'), load(out / 'deployed-gameplay.json')
    stage, stage_probe = load(runtime / 'stage-verification.json'), load(runtime / 'stage-gameplay.json')
    for result, key in ((live, 'plan_sha256'), (stage, 'plan_sha256'),
                        (probe, 'planSha256'), (stage_probe, 'planSha256')):
        if not result['passed'] or result[key] != plan_hash:
            raise ValueError('Failed or stale deployment check')
    retained = {}
    for domain in ('game', 'global'):
        before, after = (load(runtime / ('retained-' + domain + '-' + suffix + '.json')) for suffix in ('before', 'after'))
        if before != after:
            raise ValueError('Unrelated ' + domain + ' tables changed')
        retained[domain] = {'unchanged': True, 'tables': len(before), 'rows': sum(r['RowCount'] for r in before)}
    ctx = load(runtime / 'deployment-context.json')
    migration = Path('Database/migrations/034_gameforge_supported_content.sql')
    if sha(migration) != ctx['migration_sha256']:
        raise ValueError('Deployed migration changed')
    state = json.loads(subprocess.check_output(['docker', 'inspect', '--format', '{{json .State}}', 'araz-mapsvr'], text=True))
    if not state['Running']:
        raise ValueError('Map server is not running')
    report = {'recorded_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'status': 'deployed',
              'plan_sha256': plan_hash, 'migration': migration.as_posix(), 'migration_sha256': sha(migration),
              'summary': plan['summary'], 'data_verification': live, 'production_binary_probe': probe,
              'stage_data_verification': stage, 'stage_gameplay': stage_probe, 'retained_tables': retained,
              'backups': [{'path': str(p.resolve()), 'bytes': p.stat().st_size, 'sha256': sha(p)}
                          for p in sorted((runtime / 'backups').glob('*.bak'))],
              'previous_server_runtime': ctx['previous_map_runtime_backup'],
              'deployed_assemblies': [{'name': p.name, 'sha256': sha(p)}
                                     for p in sorted((runtime / 'deployed-probe').glob('TMap*.dll'))],
              'native_code': [{'path': p, 'sha256': sha(Path(p))} for p in (
                  'Servers/TMapSvr.Net/TMap.Server/Map/MapService.Quest.cs',
                  'Servers/TMapSvr.Net/TMap.Server.Tests/QuestTests.cs')],
              'server_image': subprocess.check_output(['docker', 'inspect', '--format', '{{.Image}}', 'araz-mapsvr'], text=True).strip(),
              'unit_tests': {'passed': 1152, 'failed': 0},
              'graphical_playthrough': {'performed': False, 'reason': 'No graphical client playthrough; existing client requires administrator rights'},
              'scope': 'Supported reconstructed content; not a complete or certified Gameforge server database'}
    (out / 'deployment.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    table_rows = ''.join(f'<tr><td><code>{escape(table)}</code></td><td>{len(rows)}</td></tr>' for table, rows in plan['additions'].items())
    labels = {'item': 'Předměty', 'monster': 'Monstra', 'npc': 'NPC', 'quest': 'Questové záznamy', 'skill': 'Dovednosti'}
    gap_rows = ''.join(f'<tr><td>{labels[k]}</td><td>{n}</td></tr>' for k, n in plan['summary']['deferred_by_domain'].items())
    backups = ''.join('<li><code>' + escape(row['path']) + '</code></li>' for row in report['backups'])
    page = f'''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Gameforge — nasazená serverová rekonstrukce</title>
<style>body{{font:16px/1.65 system-ui;color:#17263c;background:#f5f7fb;max-width:1060px;margin:38px auto;padding:0 24px}}table{{border-collapse:collapse;background:white;width:100%;margin:20px 0}}td,th{{border:1px solid #d4deeb;padding:10px;text-align:left;vertical-align:top}}a{{color:#1458b4}}code{{overflow-wrap:anywhere}}.notice{{background:#fff7e5;border-left:5px solid #ab7210;padding:16px}}.ok{{color:#1a6244;font-weight:650}}</style>
<h1>Nasazená serverová část podle podkladů Gameforge</h1>
<p>Cílová řada projektu je <a href="../version-target/report.html">Gameforge 5.0 a novější</a>. Tento přehled zachycuje rozsah konkrétní migrace 034, nikoli dokončení celé verze.</p>
<p class="ok">Nasazeno 28. 10. 2026 • migrace 034 • mapový a světový server obnoveny</p>
<div class="notice"><strong>Jde o doloženou a otestovanou část rekonstrukce. Kompletní shoda s Gameforge zatím není dosažená.</strong> Přímé hodnoty klienta a wiki jsou v manifestu oddělené od kompatibilních šablon a odvozených pravidel.</div>
<p>Aktivováno <strong>134 výrobních receptů</strong> s 298 položkami surovin, 134 odměnami a 48 odvozenými podmínkami povolání. Server nově obsluhuje výrobní questy přímo z inventáře, spotřebuje správné počty surovin, vydá výsledek a odešle odpovídající herní pakety. Recepty lze opakovat bez vzniku uložené rozpracované mise.</p>
<p>Doplněno také <strong>128 NPC</strong>, <strong>24 explicitních vazeb NPC na existující portály</strong>, <strong>8 jezdeckých licencí</strong> a <strong>7 nebojových šablon společníků</strong>. Pro mapy přidaných NPC vzniklo devět místních směrovacích záznamů. Registrace společníka sama neznamená doplnění nedoložených vazeb všech nových run.</p>
<h2>Co je převzaté a co odvozené</h2>
<ul><li>Počty surovin a výsledků receptů jsou z původního <code>TQuest.mpq</code>. Zadávající řemeslník a výstup receptu byly porovnané s veřejnou databází podle ID a názvu.</li>
<li>Výrobní pravidlo je rekonstrukce: přímá opakovatelná výroba, podmínka povolání podle původního povolání výsledného předmětu. <strong>Těchto 48 podmínek není certifikovaný export serverových podmínek Gameforge.</strong></li>
<li>Jezdecké licence zachovávají všechna dekódovaná původní pole. Neuložená serverová pole se přebírají jen při shodě mezi ekvivalentními kompatibilními šablonami; konkrétní hodnoty a donor jsou vypsané v plánu.</li>
<li>NPC mají původní ID, typ, zemi, položku a souřadnice. Nulové zónové a slevové výchozí hodnoty jsou označené jako místní kompatibilní nastavení. U portálů byla vyžadována jednoznačná vazba v původním <code>TDest.tcd</code> a existence cílové serverové definice.</li>
<li>Nové nebojové šablony nepřebírají vymyšlené bojové statistiky, zkušenosti nebo loot. Ostatní bojová monstra zůstávají v seznamu chybějících podkladů.</li>
<li>Serverové názvy NPC a monster jsou rozšířeny na Unicode. Původní hodnoty, výchozí omezení a názvové indexy zůstaly zachované.</li></ul>
<h2>Ověření</h2>
<ul><li>Všech <strong>1 152 serverových testů prošlo</strong>, včetně pěti nových scénářů výroby: úspěch/opakování, chybějící suroviny, plný inventář, povolání a země NPC.</li>
<li>Import byl aplikován dvakrát na samostatné kopii databáze. Přibylo přesně <strong>{live['added_rows']} řádků</strong>; všech <strong>{live['old_chart_rows_preserved']}</strong> původních řádků dotčených tabulek zůstalo beze změny.</li>
<li>Na živých datech byly původní řádky znovu porovnané. Dalších <strong>{retained['game']['tables']} herních a {retained['global']['tables']} globálních tabulek</strong> se před obnovením serverů shodovalo s původními otisky; výjimkou z globální kontroly je pouze záznam aplikované migrace.</li>
<li>Loader a paketové handlery byly otestované s knihovnami převzatými z nasazeného kontejneru, přímo v Linuxu: <strong>268 úspěšných výrob, 134 odmítnutí bez surovin, 8 použitých jezdeckých licencí, 128 registrací NPC a 24 navázaných portálů</strong>. Postavy byly dočasné v paměti; test nezapsal herní postup do SQL.</li>
<li>Mapový server skutečně načetl 8 150 předmětů, 2 751 NPC a 6 395 questů a připojil se ke světovému serveru. Naslouchá na portech 5816/5817.</li>
<li>Grafický průchod klientem zatím neproběhl. Kontroly paketů nejsou vizuální test zobrazení či průchodu celou hrou.</li></ul>
<h2>Rozsah databázové migrace</h2><table><tr><th>Tabulka</th><th>Přidáno řádků</th></tr>{table_rows}</table>
<h2>Co stále chybí</h2>
<p>Jde o rozdíl celé vybrané původní instalace vůči serveru, tedy širší rozsah než předchozí audit pouze nově doplněných klientských ID. Počty questů zahrnují také interní kroky; nejsou počtem samostatných příběhových misí.</p>
<table><tr><th>Oblast</th><th>Dosud neaktivované definice</th></tr>{gap_rows}</table>
<p>U každého ID je v <a href="plan.json">plánu</a> konkrétní důvod. Mezi mezerami jsou bojové atributy/AI/spawny, účinky dovedností, podmínky běžných misí, chybějící cíle portálů nebo specializované obchody. Záznamy nebyly vydávané za kompletní funkční obsah.</p>
<p><a href="research.html"><strong>Navazující analýza Gameforge</strong></a> nově dokládá obě NPC vazby u 323 chybějících questů a 690 veřejných hran questových stromů. Tyto informace jsou připravené pro další rekonstrukci; nebyly bez ověřených podmínek automaticky aktivované jako hotové mise.</p>
<h2>Zálohy a audit</h2><ul>{backups}</ul>
<p>Předchozí serverové soubory: <code>{escape(report['previous_server_runtime'])}</code>. Zálohy SQL byly ověřené pomocí <code>RESTORE VERIFYONLY WITH CHECKSUM</code>. Osobní a provozní zálohy zůstávají v ignorované složce <code>.runtime</code>.</p>
<p><a href="deployment.json">Nasazení, otisky a ověření</a> · <a href="live-verification.json">Porovnání živých tabulek</a> · <a href="deployed-gameplay.json">Paketové testy nasazených knihoven</a> · <a href="research.json">Strojová analýza návazností</a></p>
</html>'''
    (out / 'report.html').write_text(page, encoding='utf-8')
    print(json.dumps({'status': report['status'], 'added_rows': live['added_rows'], 'retained_tables': retained,
                      'report': str(out / 'report.html')}))


if __name__ == '__main__':
    main()
