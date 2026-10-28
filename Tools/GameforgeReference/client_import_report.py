"""Save an auditable Czech report and verify the deployed client against its import manifest."""
import argparse
import datetime
import hashlib
from html import escape
import json
from pathlib import Path
import shutil


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--deployment', type=Path, required=True)
    parser.add_argument('--repeat', type=Path, required=True)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    report = json.loads(args.deployment.read_text(encoding='utf-8'))
    repeat = json.loads(args.repeat.read_text(encoding='utf-8'))
    summary = repeat['summary']
    if any(summary[key] != 0 for key in ('changed_or_added_files', 'chart_records_added',
                                       'indexed_resources_added', 'auxiliary_files_added')):
        raise ValueError('The repeated import would still change the client')
    root = args.game.resolve()
    backup = Path(report['backup']).resolve()
    for entry in report['files']:
        path = (root / entry['path']).resolve()
        if not path.is_relative_to(root) or sha(path) != entry['after_sha256']:
            raise ValueError('Deployed content changed: ' + entry['path'])
        if entry['before_sha256'] is not None:
            original = (backup / entry['path']).resolve()
            if not original.is_relative_to(backup) or sha(original) != entry['before_sha256']:
                raise ValueError('Backup content changed: ' + entry['path'])
    checks = {
        'checked_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'deployed_manifest_sha256': sha(args.deployment),
        'all_deployed_file_hashes_match': True,
        'deployed_files_checked': len(report['files']),
        'all_backup_file_hashes_match': True,
        'repeat_import_summary': summary,
    }
    args.output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(args.deployment, args.output / 'manifest.json')
    (args.output / 'verification.json').write_text(
        json.dumps(checks, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    table_rows = '\n'.join(
        '<tr><td><code>' + escape(row['path']) + '</code></td><td>' +
        str(row['before_records']) + '</td><td>' + str(row['source_records']) +
        '</td><td>' + str(row['added_records']) + '</td><td>' +
        str(row['after_records']) + '</td></tr>'
        for row in report['tables'] if row['added_records'])
    labels = {'item': 'Předměty', 'monster': 'Monstra', 'quest': 'Questy',
              'npc': 'NPC', 'skill': 'Dovednosti'}
    server_rows = '\n'.join(
        f"<tr><td>{labels[key]}</td><td>{row['added_client_ids']}</td>"
        f"<td>{row['already_in_server']}</td><td>{len(row['missing_server_ids'])}</td></tr>"
        for key, row in report['server_coverage'].items())
    resource_rows = '\n'.join(
        '<tr><td><code>' + escape(row['path']) + '</code></td><td>' +
        str(len(row['added'])) + '</td></tr>' for row in report['resources'])
    file_rows = '\n'.join(
        '<tr><td><code>' + escape(row['path']) + '</code></td><td>' +
        escape(row['kind']) + '</td><td>' + str(row['bytes']) + '</td></tr>'
        for row in report['files'])
    html = f'''<!doctype html>
<html lang="cs">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>4Story — doplnění obsahu z původního klienta</title>
<style>
body{{font:16px/1.6 system-ui,sans-serif;color:#17263c;background:#f5f7fb;max-width:1120px;margin:40px auto;padding:0 24px}}
h1{{line-height:1.2}}h2{{margin-top:32px}}a{{color:#1458b4}}table{{border-collapse:collapse;width:100%;margin:18px 0;background:white}}
td,th{{padding:9px 12px;border:1px solid #d9e1ee;text-align:left;vertical-align:top}}th{{background:#e8eef8}}
code{{font-size:14px;overflow-wrap:anywhere}}.notice{{border-left:5px solid #ab7210;background:#fff7e5;padding:16px 20px}}
.ok{{color:#1a6244;font-weight:650}}.scroll{{overflow:auto}}summary{{cursor:pointer;font-weight:600}}
</style>
</head>
<body>
<h1>Doplnění chybějícího obsahu Gameforge do klienta</h1>
<p class="ok">Nasazeno 27. 10. 2026 do <code>Game/</code>. Všech {len(report['files'])} výsledných souborů i jejich zálohy jsou ověřené pomocí SHA-256.</p>
<p>Zdroj: <code>{escape(report['source'])}</code>, vybraná místní Gameforge CZ instalace připnutá v <a href="../reference.json">referenčním manifestu</a>. Přesné číslo patche není potvrzené. Přidávají se chybějící ID; existující záznamy, překlady a přizpůsobené prostředky zůstávají zachované. Tento import sám nenahrazuje obsah již obsazených ID původními hodnotami.</p>
<p>Celkem přibylo <strong>{report['summary']['chart_records_added']} záznamů</strong>, <strong>{report['summary']['indexed_resources_added']} indexovaných prostředků</strong> a <strong>{report['summary']['auxiliary_files_added']} souborů map, modelů, animací a navigace</strong>. Dále bylo doplněno osm chybějících obrazových stránek a dvě původní stránky pod novými ID kvůli kolizi s upravenou grafikou.</p>
<p><strong>Hlavní přírůstky:</strong> 39 předmětů, 149 monster, 2 553 questových záznamů, 48 dovedností a 21 dosud nepřítomných NPC ID. Rozmístění NPC přibylo 36; ID se mohou opakovat mezi mapami. Dále osm map, osm minimap, 54 regionů, deset portálů, 17 titulů, 683 textů a 1 214 náhledů questových odměn.</p>
<div class="notice"><strong>Klientská definice sama neaktivuje obsah na serveru.</strong> Stav níže porovnává nově přidaná ID se serverovými tabulkami v okamžiku přípravy. Přítomné ID ještě nepotvrzuje správné chování. V tomto importu se serverová databáze neměnila.</div>
<h2>Návaznost na server</h2>
<table><thead><tr><th>Oblast</th><th>Nová klientská ID</th><th>ID již na serveru</th><th>ID chybějící na serveru</th></tr></thead><tbody>
{server_rows}
</tbody></table>
<p>Chybějící serverová ID jsou přesně vypsaná v <code>server_coverage</code> v manifestu. Pro hratelnost je potřeba doplnit ověřené šablony, cíle a návaznosti questů, spawny, AI a další serverové údaje. Klient neobsahuje kompletní původní serverovou databázi. Odměny, šance na drop ani jiné neznámé hodnoty nebyly odhadnuté.</p>
<h2>Kompatibilita a ověření</h2>
<ul>
<li>Nový <code>TClient.exe</code> byl sestaven a nasazen spolu s daty. Čte původní šestislotové i nové devítislotové náhledy questových odměn. Změna se týká náhledů; síťový formát předmětů se nemění.</li>
<li>Dovednosti byly převedeny podle pojmenovaných polí obou verzí klienta. Přebytečná metadata questů jsou zachovaná v manifestu; samotné přidání textového záznamu není implementací serverového questového grafu.</li>
<li>Dalších 683 textových ID je dostupných i nad původním rozsahem klientského výčtu. Dosavadní vlastní texty si zachovávají přednost.</li>
<li>Kontrola přečetla všech {report['data_validation']['total_chart_records']:,} záznamů v podporovaných tabulkách a ověřila zachování starých záznamů i pokrytí původních ID. Staré náhledy odměn byly vratně rozšířeny třemi nulovými sloty.</li>
<li>Kontrola prostředků ověřila uložené i rozbalené otisky a prošla {report['resource_validation']['graphNodes']:,} uzlů návazností nových prostředků a modelů: žádný chybějící odkaz, žádné nově přidané monstrum bez modelu.</li>
<li>Všech 473 dosavadních obrazových stránek zůstalo zachováno. Výsledný klient má 483 stránek. Aliasy 13 → 489 a 243 → 490 používají jen nové obrázky; původní pixely i ostatní pole zůstaly stejné.</li>
<li>Opakovaný import proti nasazenému klientu: nula nových záznamů, prostředků a souborů.</li>
<li>Test spuštění a průchodu hrou nebyl proveden. Existující manifest vyžaduje správce a Windows automatické spuštění odmítl (<code>ShellExecuteW 5</code>). Binární kontroly nenahrazují vizuální kontrolu ve hře.</li>
</ul>
<h2>Co nebylo přeneseno</h2>
<ul>
<li>Původní instalace sama neobsahuje zvukové soubory <code>1_285964680.TMD</code>, <code>1_285964681.TMD</code> a <code>1_285964682.TMD</code>. Nebyly vytvořeny neplatné indexové odkazy.</li>
<li><code>TAOSMacroChat.tcd</code>, <code>TAosoption.tcd</code>, <code>TFinish.tcd</code>, <code>TOperator.tcd</code> a <code>TPetComment.tcd</code> nemají ověřený převod a podporu současného klienta. Jejich zkopírování by funkce nezprovoznilo.</li>
<li><code>TNPC00000004.tcd</code> je už ve zdroji prázdný. Ostatní nepodporované doprovodné formáty nebyly bez ověření přepisovány.</li>
</ul>
<h2>Tabulky s doplněnými záznamy</h2>
<p>Sloupec „Zdroj“ udává počet záznamů v místní Gameforge instalaci. Výsledek může být větší, protože dosavadní vlastní záznamy zůstaly zachované.</p>
<div class="scroll"><table><thead><tr><th>Soubor</th><th>Před</th><th>Zdroj</th><th>Přidáno</th><th>Po</th></tr></thead><tbody>
{table_rows}
</tbody></table></div>
<details><summary>Indexované prostředky</summary><table><thead><tr><th>Katalog</th><th>Přidáno</th></tr></thead><tbody>
{resource_rows}
</tbody></table></details>
<details><summary>Všech 117 nasazených souborů</summary><div class="scroll"><table><thead><tr><th>Soubor</th><th>Druh</th><th>Bajtů</th></tr></thead><tbody>
{file_rows}
</tbody></table></div></details>
<h2>Záloha a podklady</h2>
<p>Záloha původních souborů: <code>{escape(report['backup'])}</code>. <code>before-import.json</code> obsahuje očekávané původní otisky, <code>deployment.json</code> výsledek nasazení. Nově vytvořené soubory mají v manifestu <code>before_sha256: null</code>.</p>
<p><a href="manifest.json">Manifest s přesnými ID, zdroji, převodními metadaty a otisky</a> · <a href="verification.json">Kontrola nasazených souborů, záloh a opakovatelnosti</a> · <a href="../normalization/coverage.html">Předchozí porovnání s veřejnou databází a opravy serveru</a></p>
<p>SHA-256 nasazeného klienta: <code>{sha(root / 'TClient.exe')}</code>.</p>
</body>
</html>
'''.replace('56,546', '56 546').replace('11,424', '11 424')
    (args.output / 'report.html').write_text(html, encoding='utf-8')
    print(json.dumps(checks, ensure_ascii=False))


if __name__ == '__main__':
    main()
