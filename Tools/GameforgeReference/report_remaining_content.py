"""Report only unresolved content after the verified live import, retaining exact IDs and evidence."""
import collections
import datetime
import hashlib
from html import escape
import json
from pathlib import Path
import subprocess


def load(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def main():
    root = Path('Gameforge/server-completion')
    runtime = Path('.runtime/gameforge-verified-import')
    plan = load(root / 'plan.json')
    joined = load(root / 'current/joined.json')
    gaps = load(root / 'gaps/audit.json')
    verification = load(root / 'live-verification.json')
    gameplay = load(root / 'live-gameplay.json')
    context = load(runtime / 'deployment-context.json')
    if not verification['passed'] or verification['plan_sha256'] != sha(root / 'plan.json') or not gameplay['passed']:
        raise ValueError('Missing or stale live verification')
    if gameplay['regionPlanSha256'] != sha(runtime / 'probe-plan.json') or gameplay['writesToSql'] != 0:
        raise ValueError('Gameplay probe differs from the tested input')
    for kind in ('game', 'global'):
        if load(runtime / f'retained-{kind}-before.json') != load(runtime / f'retained-{kind}-after.json'):
            raise ValueError('Unrelated tables changed')
    image = subprocess.check_output(['docker', 'inspect', '--format', '{{.Image}}', 'araz-mapsvr'], text=True).strip()
    deployment = {**context, 'verified_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  'runtime_image': image, 'runtime_server_dll_sha256': gameplay['serverDllSha256'],
                  'runtime_templates_reloaded': True, 'live_gameplay_writes_to_sql': 0,
                  'purchases_verified': len(gameplay['purchases']), 'monster_restores_verified': len(gameplay['monsterRestores']),
                  'mapped_monster_fields_verified': len(gameplay['monsterFields']),
                  'old_delivery_regressions_verified': len(gameplay['deliveries']),
                  'retained_game_tables': 198, 'retained_global_tables_excluding_migration_metadata': 69,
                  'affected_old_rows_verified': verification['old_rows_checked'],
                  'graphical_playthrough': False, 'full_gameforge_alignment': False,
                  'evidence': {p.name: sha(p) for p in root.glob('*.json') if p.name not in ('deployment.json', 'remaining.json', 'report-verification.json')},
                  'original_client_files_modified': False, 'saved_character_rows_rewritten_by_import': 0}
    (root / 'deployment.json').write_text(json.dumps(deployment, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    unresolved_monsters = [r for r in joined['monsters'] if r['server']['level_outside_published_range'] or
                           r['server']['hp_outside_published_range'] or not r['server']['attribute_row_exists']]
    remaining = {'generated_utc': deployment['verified_utc'], 'after_migration': context['migration_file'],
                 'scope': 'Anbari/Undred and all original client IDs currently absent in the server',
                 'reference_client_patch_unknown': True, 'summary': {**gaps['summary'],
                     'regional_referenced_monsters_including_direct_details': len(joined['monsters']),
                     'referenced_monsters_without_spawns': joined['summary']['monsters_with_no_server_spawn_membership'],
                     'referenced_monsters_missing_published_loot_rules': joined['summary']['monsters_with_published_loot_but_no_drop_rows'],
                     'unresolved_regional_monster_levels': joined['summary']['server_monster_level_conflicts'],
                     'missing_monster_attribute_rows': joined['summary']['server_monster_attribute_rows_missing']},
                 'missing_regional_quests': gaps['missing_regional_quests'],
                 'unresolved_regional_monsters': unresolved_monsters,
                 'regional_spawn_state': gaps['spawns'], 'missing_regional_portals': joined['portals'],
                 'quest_item_dependencies': joined['quest_item_monster_links'], 'conflicts': joined['conflicts'],
                 'missing_global_ids': gaps['global_gaps'], 'deferred_verified_import_candidates': plan['deferred'],
                 'unresolved_gameplay_rules': [
                     'Original spawn population, variants per point, timers and probabilities.',
                     'Numeric ordinary/quest drop weights and all required item-generation parameters.',
                     'Complete original quest eligibility, triggers, repeats/resets and helper conditions.',
                     'RT_SOUL reward type 9 is not implemented in the current quest handler.',
                     'Term 21 requires spawn-instance semantics in the current protocol, not a direct monster ID join.',
                     'BoW shop pricing and, for NPCs 26611/26612, map-3000 routing.',
                     'Verified portal arrival heights and full server portal condition mapping.',
                     'Unpublished combat damage, defence, EXP, mana and AI behavior remain unverified.',
                     'Non-EXP/non-client-fee progression columns above 125 retain compatibility values.',
                     'Exact patch of the selected original client is unknown; not every global missing ID is proved to belong to 5.0.1.']}
    (root / 'remaining.json').write_text(json.dumps(remaining, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    e = lambda x: escape(str(x), quote=True)
    def link(label, href):
        return f'<a href="{e(href)}">{e(label)}</a>'
    def table(headers, rows):
        return '<div class="scroll"><table><thead><tr>' + ''.join('<th>' + e(h) + '</th>' for h in headers) + '</tr></thead><tbody>' + ''.join('<tr>' + ''.join('<td>' + str(v) + '</td>' for v in row) + '</tr>' for row in rows) + '</tbody></table></div>'
    s = remaining['summary']
    page = ['<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">',
            '<title>Gameforge — co ještě chybí po doplnění serveru</title><style>',
            'body{font:16px/1.6 system-ui;background:#101722;color:#e4eaf2;margin:0}main{max-width:1300px;padding:28px;margin:auto}a{color:#96cfff}h1{font-size:29px}h2{margin-top:38px}.box{background:#203346;padding:16px;border-left:4px solid #55bc91}.warn{background:#332d20;padding:16px;border-left:4px solid #edb954}.scroll{overflow:auto}table{border-collapse:collapse;width:100%;font-size:14px}th,td{border:1px solid #425163;padding:10px;text-align:left;vertical-align:top}th{background:#25364b}input{width:min(500px,90%);padding:12px;background:#203346;color:white;border:1px solid #64788c}nav{display:flex;gap:18px;flex-wrap:wrap}code{overflow-wrap:anywhere}.muted{color:#b5c5d5}</style><main>',
            '<h1>Co ještě chybí po doplnění serveru</h1>',
            f'<p class="box">Nasazena migrace 037: opraveny levely a životy 2 monster, 16 původních polí u dalších 5 monster a přidáni 2 obchodníci Elrod s 22 položkami. Server data znovu načetl. Prošly nákupy všech 22 položek, kontroly monster i 12 již nasazených doručovacích questů.</p>',
            '<p class="muted">Stav po importu z 28. 10. 2026. Následující seznam obsahuje zbývající nedostatky. Anbari ani Undred zatím nejsou kompletní hratelné PvE oblasti.</p>',
            '<nav><a href="#overview">Přehled</a><a href="#quests">Questy</a><a href="#monsters">Monstra a spawny</a><a href="#drops">Dropy</a><a href="#portals">Portály</a><a href="#global">Ostatní mapy a ID</a><a href="#rules">Pravidla serveru</a></nav>',
            '<h2 id="overview">Zbývající obsah Anbari a Undred</h2>',
            table(['Oblast', 'Co chybí', 'Proč není aktivováno'], [
                ('Questy', '126 misí ze 138 porovnávaných', 'Nedoložené podmínky, spouštěče, pomocné úkoly nebo chybějící populace a loot.'),
                ('Spawny', '64 souvisejících šablon monster nemá žádné přiřazení ke spawnu', 'V obou jednotkách světa je 0 spawnů; původní počet není známý. Navigační body questů nejsou spawnovací tabulka.'),
                ('Statistiky monster', '7 nesrovnaných levelů; 2 neexistující řádky atributů', 'Rozsahy, spor katalogu s detailem nebo chybějící základ statistik.'),
                ('Běžný loot', '59 monster má zveřejněný možný loot a žádná serverová drop pravidla', 'Chybí číselné váhy, pravděpodobnosti a parametry generování předmětů.'),
                ('Questové dropy', '8 vztahů předmět–monstrum–quest, 10 pomocných záznamů', 'Chybí úplné podmínky a serverové spouštěče dropu.'),
                ('Portály', '9 portálových NPC, 9 definic a 48 tras', 'Neověřené výšky přistání a neúplný převod podmínek přístupu.')]),
            '<p>Všech 1 051 relevantních předmětů již má serverovou šablonu. Jejich získávání závisí na chybějících vazbách výše.</p>',
            '<h2 id="quests">Konkrétní chybějící questy</h2>',
            '<p>Seznam zahrnuje 65 misí souvisejících s Anbari a 61 s Undred. Zahrnuje frakční varianty i zadání začínající v jiných mapách; nejde o 126 různých názvů.</p>',
            '<label>Filtrovat ID nebo název: <input id="filter" type="search" placeholder="Např. 11174"></label>',
            table(['ID a název', 'Level', 'Oblast', 'Rodič', 'Zadavatel / odevzdání', 'Vazba k doplnění'], [
                (link(str(q['id']) + ' ' + q['name'], q['source']) if q['source'] else e(str(q['id']) + ' ' + q['name']), q['level'], e(', '.join(q['regions'])), q['parent_id'],
                 e(str(q['giver_ids']) + ' / ' + str(q['turnin_ids'])), e(q['terms'])) for q in remaining['missing_regional_quests']]),
            '<p>Quest 11292: klient uvádí zadavatele 26666, Gameforge 26673. Quest 11261: zadavatel chybí v klientské navigaci, veřejná databáze uvádí 26660. Tato zadání nebyla automaticky aktivována.</p>',
            '<h2 id="monsters">Monstra: zbývající statistiky a všechny spawny</h2>',
            table(['ID a název', 'SQL level / HP', 'Publikovaný level / HP', 'Neuzavřený problém'], [
                (link(str(m['id']) + ' ' + m['name'], 'current/report.html#monster-' + str(m['id'])),
                 e(str(m['server']['record']['bLevel']) + ' / ' + str(m['server']['attribute_row']['dwMaxHP'] if m['server']['attribute_row'] else 'chybí atributy')),
                 e(str(m['publisher']['level_range']) + ' / ' + str(m['publisher']['hp_range'])),
                 'Chybí základní řádek statistik' if not m['server']['attribute_row_exists'] else 'Rozpor veřejného katalogu a detailu' if m['publisher']['catalogue_and_detail_level_disagree'] else 'Publikované hodnoty mají rozsah; konkrétní varianta není doložena') for m in unresolved_monsters]),
            '<p>Chybějící atributové odkazy mají ID 3538 a 3557. U 3547 publikuje katalog level 113–114, detail 112–113. Ostatní bojové parametry, EXP za zabití a AI zůstávají neověřené i u monster s opraveným levelem a HP.</p>',
            '<p>Pro všech 64 souvisejících ID chybí skutečné rozmístění, počty, výběr variant, časování respawnu a návazné spawnovací skupiny. '+link('Úplný seznam monster a jejich aktuální SQL stav', 'current/report.html#monsters')+'</p>',
            '<h2 id="drops">Questové a běžné dropy</h2>',
            table(['Předmět', 'Quest', 'Zdrojový druh', 'Počet', 'Chybějící pomocná ID'], [
                (e(str(r['item_id']) + ' ' + r['name']), r['quest_id'], e(str(r['monster_kind']) + ' ' + r['monster_name']), r['quantity'], e(r['missing_drop_helper_ids'])) for r in remaining['quest_item_dependencies']]),
            '<p>U běžného lootu je doloženo 59 271 možných dvojic monstrum–předmět a slovní četnosti, nikoliv procenta. Tyto dvojice nejsou počet požadovaných SQL řádků: společná pravidla mohou pokrývat více předmětů. '+link('Doložené loot vazby', 'current/loot-memberships.json')+'</p>',
            '<h2 id="portals">Chybějící portály</h2>',
            table(['Portál', 'NPC', 'Souřadnice X / Z', 'Co chybí'], [(e(str(r['portal_id']) + ' ' + r['name']), r['npc_id'], f'{r["x"]:.2f} / {r["z"]:.2f}', 'Definice, NPC vazba, trasy, ověřené přistání Y a podmínky přístupu') for r in remaining['missing_regional_portals']]),
            '<p>Výška terénu není ověřená výška přistání na mostě či plošině. U přístavu Luadlan stojí původní NPC přibližně o 2,25 m nad terénem. '+link('Původních 48 tras s cenami a příznaky zemí', 'current/report.html#portals')+'</p>',
            '<h2 id="global">Zbývající ID v celém porovnávaném klientovi</h2>',
            '<p>Tyto počty se překrývají s regionálním seznamem výše. Patch klienta není ověřený; ne každý chybějící ID záznam lze označit za obsah přesně verze 5.0.1.</p>',
            table(['Druh', 'Chybějící záznamy'], [('Mise', s['global_missing_mission_records']), ('Všechny typy questů včetně pomocných', s['global_missing_quest_records_including_helpers']),
                ('NPC', s['global_missing_npc_records']), ('Z toho obchodníci', s['global_missing_merchant_npcs']), ('Šablony monster', s['global_missing_monster_templates']),
                ('Šablony předmětů', s['global_missing_item_templates']), ('Šablony dovedností', s['global_missing_skill_templates'])]),
            '<p>'+link('Úplné seznamy všech chybějících ID a názvů', 'gaps/global.html')+'</p>',
            '<p>Zbývajících 8 BB obchodníků: 26579–26584, 26611 a 26612. Jejich speciální cenový režim není implementovaný; mapa 3000 navíc nemá serverové směrování. Elrod 26647 a 26648 už mezi chybějícími není.</p>',
            '<h2 id="rules">Pravidla a ověření, která ještě chybí</h2>',
            '<ul><li>Úplné původní podmínky questů, opakování a denní resety; typ odměny 9 (duše).</li><li>Převod podmínek typu 21 na skutečné spawnovací instance v současném protokolu.</li><li>Úplné serverové účinky chybějících dovedností a ověření bojového vyvážení, dropů a EXP od monster.</li><li>Původní ne-EXP statistické křivky pro levely nad 125; část těchto sloupců používá kompatibilní hodnoty levelu 125.</li><li>Potvrzení přesného patche vybrané oficiální instalace a následné hraní celých questových řetězců v grafickém klientovi.</li></ul>',
            '<p class="warn">Tento import nedoplňoval odhadnuté populace monster, procenta dropů ani neověřené podmínky. U dvou opravených monster byly převzaty přesné publikované levely a HP; ostatní bojové sloupce zůstaly zachované a nejsou tím potvrzené jako původní Gameforge.</p>',
            '<p>'+link('Strojově čitelný seznam zbývajícího obsahu', 'remaining.json')+' · '+link('Důkazy nasazení a zálohy', 'deployment.json')+' · '+link('Ověření živých SQL řádků', 'live-verification.json')+' · '+link('Testy skutečného serverového loaderu a paketů', 'live-gameplay.json')+'</p>',
            '<script>document.querySelector("#filter").addEventListener("input",e=>{const q=e.target.value.toLocaleLowerCase("cs");document.querySelectorAll("tbody tr").forEach(r=>r.hidden=!r.textContent.toLocaleLowerCase("cs").includes(q))})</script></main></html>']
    (root / 'report.html').write_text('\n'.join(page), encoding='utf-8')
    print(json.dumps({'remaining_regional_missions': 126, 'remaining_global_npcs': s['global_missing_npc_records'],
                      'remaining_regional_monster_level_conflicts': len(unresolved_monsters), 'report': str(root / 'report.html')}))


if __name__ == '__main__':
    main()
