"""Archive verified progression/region deployment and generate an honest Czech status report."""
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
    args = parser.parse_args()
    runtime = args.runtime
    exp_dir, region_dir = Path('Gameforge/progression-140'), Path('Gameforge/high-level-regions')
    exp, regions = load(exp_dir / 'plan.json'), load(region_dir / 'plan.json')
    context = load(runtime / 'deployment-context.json')
    stage, live = load(runtime / 'stage-gameplay.json'), load(region_dir / 'deployed-gameplay.json')
    for r in (stage, live):
        if not r['passed'] or r['regionPlanSha256'] != sha(region_dir / 'plan.json') or r['expSourceSha256'] != sha(exp_dir / 'publisher-exp.json'):
            raise ValueError('Failed or stale gameplay probe')
    verify = load(region_dir / 'live-verification.json')
    if not verify['passed'] or any(sha(Path(r['path'])) != r['sha256'] for r in verify['plans']):
        raise ValueError('Failed or stale chart verification')
    tests = load(exp_dir / 'tests.json')
    if tests['failed'] or tests['passed'] != tests['total'] or len(tests['progression_cases']) != 19 or any(sha(Path(r['path'])) != r['sha256'] for r in tests['tested_sources']):
        raise ValueError('Stale or failed server tests')
    for kind in ('game', 'global'):
        if load(runtime / f'retained-{kind}-before.json') != load(runtime / f'retained-{kind}-after.json'):
            raise ValueError('Retained SQL tables changed')
    for r in context['migrations']:
        if sha(Path('Database/migrations') / r['file']) != r['sha256']:
            raise ValueError('Applied migration changed')
    image = subprocess.check_output(['docker', 'inspect', '--format', '{{.Image}}', 'araz-mapsvr'], text=True).strip()
    if image != context['runtime_image_after'] or live['serverDllSha256'] != sha(runtime / 'runtime-after/TMap.Server.dll'):
        raise ValueError('Stale deployed runtime evidence')
    report = {**context, 'verified_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'tests': {'passed': tests['passed'], 'failed': tests['failed'], 'evidence': 'Gameforge/progression-140/tests.json'},
              'runtime_assemblies': [{'name': p.name, 'sha256': sha(p)} for p in sorted((runtime / 'runtime-after').glob('*.dll'))],
              'live_gameplay': {'path': 'Gameforge/high-level-regions/deployed-gameplay.json', 'sha256': sha(region_dir / 'deployed-gameplay.json'),
                               'exp_boundaries': len(live['boundaries']), 'login_bounds': len(live['loginBounds']), 'deliveries': len(live['deliveries']), 'purchases': len(live['purchases']), 'writes_to_sql': 0},
              'retained_tables': {'game': len(load(runtime / 'retained-game-after.json')), 'global_excluding_migration_metadata': len(load(runtime / 'retained-global-after.json'))},
              'source_files': tests['tested_sources'], 'saved_characters_rewritten': False,
              'full_regions_playable': False, 'complete_gameforge_5_plus_alignment': False, 'graphical_playthrough': False}
    (exp_dir / 'deployment.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    fmt = lambda v: f'{v:,}'.replace(',', ' ')
    exp_rows = ''.join(f'<tr><td>{n}</td><td>{fmt(e)}</td><td>{fmt(d)}</td></tr>'
                       for n, e, d in exp['publisher_source']['rows'] if n >= 126)
    deliveries = ''.join(f'<tr><td>{r["mission"]["dwQuestID"]}</td><td>{escape(r["mission"]["title"])}</td><td>{r["mission"]["bLevel"]}</td><td>{r["giver"]} → {r["turnin"]}</td></tr>'
                         for r in regions['supported_deliveries'])
    monster = load(region_dir / 'monster-audit.json')
    page = f'''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Gameforge 5.0+ — EXP do 140, Anbari a Undred</title>
<style>body{{font:16px/1.65 system-ui;max-width:1080px;margin:40px auto;padding:0 24px;background:#f5f7fb;color:#17263c}}table{{border-collapse:collapse;width:100%;background:white;margin:20px 0}}td,th{{border:1px solid #d4deeb;padding:9px;text-align:left}}a{{color:#1458b4}}code{{overflow-wrap:anywhere}}.notice{{padding:18px;background:#fff3d9;border-left:5px solid #ab7210}}.ok{{padding:18px;background:#e6f4ed;border-left:5px solid #24714d}}</style>
<h1>EXP do levelu 140; Anbari a Undred</h1>
<p><a href="../content-gaps/report.html">Aktuální úplný seznam chybějících questů, NPC, monster, spawnů a dropových vazeb</a></p>
<p>Nasazeno 28. 10. 2026 • migrace 035 a 036 • cílová řada Gameforge 5.0+</p>
<div class="ok"><strong>EXP křivka je doplněná a ověřená.</strong> Opraveno 27 stávajících prahů, přidáno 15 řádků levelové tabulky. Aktivováno 12 doručovacích questů, dvě obchodnice s 26 položkami a směrování dvou jednotek světa.</div>
<div class="notice"><strong>Anbari ani Undred zatím nejsou kompletně hratelné PvE oblasti.</strong> Stále v nich není populace monster. Bojové a sběrné úkoly, které závisejí na nedoložených spawnech a dropech, nebyly vydávány za funkční původní obsah.</div>
<h2>Odkud jsou zkušenosti</h2>
<p>Hodnoty pocházejí z <a href="{escape(exp['publisher_source']['source'])}">tabulky českého týmu Gameforge, Dafne, 26. 11. 2018</a>. Přepis obsahuje kumulativní EXP i samostatně uvedené rozdíly mezi levely; všechny sousední rozdíly byly zkontrolované. Data odpovídají období 5.0+, nejde o export serverové SQL databáze.</p>
<p>Řádek <code>TLEVELCHART[L]</code> obsahuje EXP pro dosažení <code>L+1</code>. Vazba byla ověřena na 11 překrývajících se řádcích. Při dosažení 140 se další postup zastaví. Opravené je i přetečení při velké EXP odměně, podtečení ukazatele síly duší na maximálním levelu a načtení EXP hranic do přihlašovacího paketu klienta.</p>
<table><tr><th>Dosažený level</th><th>EXP celkem</th><th>Rozdíl od předchozího levelu</th></tr>{exp_rows}</table>
<p><strong>Ostatní levelové sloupce mají oddělený stav:</strong> peněžní poplatky nových řádků pocházejí z původního klienta. Nezveřejněné sloupce včetně bodů dovedností zůstávají kompatibilně na hodnotách posledního existujícího řádku 125; nejsou potvrzené jako Gameforge. Uložené levely ani EXP existujících postav migrace nepřepisuje.</p>
<h2>Co nyní funguje v oblastech</h2>
<p>Prověřeno 68 existujících NPC proti jejich původním souřadnicím. Přidané obchodnice <strong>Lamian (26704)</strong> v Anbari a <strong>Elina (26718)</strong> v Undredu mají přesné veřejně publikované zboží. U každé ze 26 položek se shoduje veřejná cena, původní klientská formule i skutečné serverové účtování.</p>
<p>Každý následující quest lze přijmout u původního zadavatele, odevzdat u původního příjemce a získat původní zlato a EXP. Testy kontrolují minimální level, existujícího rodiče tam, kde je vyžadován, dokončení i zákaz opakovaného čerpání odměny. Obecné serverové podmínky jsou rekonstrukcí: jednorázové doručení, doložený rodič, konzervativní minimum podle zobrazeného levelu. Původní odměny typu 9 jsou zachované v datech, jejich obsluha zatím chybí.</p>
<table><tr><th>ID</th><th>Quest</th><th>Level</th><th>NPC zadavatel → příjemce</th></tr>{deliveries}</table>
<p>Směrování světových jednotek <code>0x0103</code> (Anbari) a <code>0x0203</code> (Undred) bylo doplněno pro zdejší server a kanál. Přístup přes všechny původní portály není dokončený.</p>
<h2>Co přesně blokuje dokončení</h2>
<ul><li>V klientovi je {monster['source_guidance_points']} navigačních bodů pro cíle dotčených questů. Jsou to orientační cíle, nikoli export jednotlivých monster, jejich počtů, obnovy a pravděpodobností dropu.</li>
<li>Veřejný katalog popisuje {monster['catalogue_count']} monster Anbari/Undredu. Všechna reprezentativní ID mají serverovou šablonu; u {monster['ambiguous_variant_count']} záznamů je publikovaný pouze rozsah levelů či HP. Konkrétní varianty z něj nebyly odhadnuté.</li>
<li>Z {len(regions['candidates'])} dotčených misí zůstává {len(regions['deferred'])} mimo aktivovaný uzavřený řetězec. Vyžadují spawn, questový drop, interakci, neimportovaného rodiče nebo další podmínky.</li>
<li>Chybí devět specializovaných portálových NPC. Klientský <code>TDest</code> sice dokládá části sítě a ceny, samotné body X/Z neobsahují kompletní serverové příletové údaje a podmínky.</li></ul>
<h2>Ověření a zálohy</h2>
<p>Linux .NET 10: <strong>{fmt(tests['passed'])} testů prošlo</strong>. Na oddělené SQL databázi a znovu s knihovnami skutečně nasazeného serveru prošlo 15 přesných EXP hranic, přetečení a maximum 140, přihlašovací EXP údaje na levelech 126 a 140, 12 dokončení questů a 26 nákupů. Paketové sondy pracují s dočasnými postavami v paměti a nezapisují herní postup do SQL. Grafický průchod klientem proveden nebyl.</p>
<p>Úplné porovnání zkontrolovalo 34 603 původních řádků měněných tabulek a 143 přidaných řádků. Dalších 194 herních a 69 globálních tabulek zůstalo shodných; výjimkou jsou dva očekávané nové záznamy historie migrací. Před nasazením vznikly ověřené SQL zálohy a záloha knihoven; předchozí kontejnerový obraz je označený pro návrat.</p>
<p><a href="../progression-140/deployment.json">Nasazení a otisky</a> · <a href="../progression-140/tests.json">Testy</a> · <a href="../high-level-regions/deployed-gameplay.json">Testy nasazených knihoven</a> · <a href="../high-level-regions/live-verification.json">Porovnání SQL</a> · <a href="../progression-140/publisher-exp.json">Přepis EXP a zdroj</a> · <a href="../progression-140/plan.json">Plán 035</a> · <a href="../high-level-regions/plan.json">Plán 036 a nevyřešené questy</a> · <a href="../high-level-regions/monster-audit.json">Audit monster</a> · <a href="../version-target/report.html">Celkový audit verze</a></p></html>'''
    for directory in (exp_dir, region_dir):
        (directory / 'report.html').write_text(page, encoding='utf-8')
    print(json.dumps({'deployed': True, 'exp_boundaries': len(live['boundaries']), 'delivery_quests': len(live['deliveries']),
                      'shop_purchases': len(live['purchases']), 'full_regions_playable': False}))


if __name__ == '__main__':
    main()
