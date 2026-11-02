"""Capture current publisher Gor quest details without changing live gameplay charts."""
import argparse
import concurrent.futures
import datetime
import html
import json
import re
from pathlib import Path

from analyze_gameforge_sources import Chain
from audit_quest_turn_in_npcs import load_reference
from catalogue import listing, quest_facts
from monsters import Source, text


def detail_facts(markup):
    heading = re.search(r'<h1\b[^>]*>(.*?)</h1>', markup, re.S)
    if heading is None or text(heading[1]).casefold() == 'úkoly v iberii':
        raise ValueError('Expected a quest detail, received a landing/error page')
    facts = quest_facts(markup)
    if facts['level_label'] is None or facts['giver_id'] is None:
        raise ValueError('Quest eligibility or giver missing from publisher detail')
    description = re.search(r'id="tabs-2"(.*?)(?=<div[^>]+id="tabs-3"|<!-- sub-layout: end -->)', markup, re.S)
    body = description[1] if description else ''
    speakers = [int(i) for i in re.findall(r'<cite>\s*<a[^>]+href\s*=\s*["\'][^"\']*/npc/details/(\d+)', body, re.S)]
    blocks = re.findall(r'<blockquote\b[^>]*>(.*?)</blockquote>', body, re.S)
    final_reply = text(blocks[-1]) if blocks else ''
    points = re.findall(r'(\d+)\s+bod(?:ů|y|u)\s+úspěchu', final_reply, re.I)
    first_tab = re.search(r'id="tabs-1"(.*?)(?=<div[^>]+id="tabs-2")', markup, re.S)
    right = first_tab[1].split('<div class="detail_right">', 1) if first_tab else []
    objective_markup = re.split(r'<hr\s*/?>', right[1], maxsplit=1)[0] if len(right)==2 else ''
    objective_npcs = sorted(set(map(int, re.findall(r'/npc/details/(\d+)', objective_markup))))
    explicit_target = objective_npcs[0] if len(objective_npcs)==1 and 'Mluvit s' in text(objective_markup) else None
    chain = Chain()
    chain.feed(markup)
    return dict(heading=text(heading[1]), **facts,
                dialogue_speaker_ids=list(dict.fromkeys(speakers)),
                objective_npc_ids=objective_npcs,
                inferred_completion_npc_id=explicit_target if explicit_target is not None else speakers[-1] if speakers else None,
                completion_npc_inference='Explicit single talk-objective NPC link.' if explicit_target is not None else 'Last NPC speaker in the publisher description; compare with pinned navigation before using as a trigger.',
                achievement_points_in_completion_text=sorted(set(map(int, points))),
                chain_nodes=sorted(chain.nodes), chain_edges=sorted(chain.edges))


def compare(reference, published):
    differences = []
    for name, reward_type in [('money', 1), ('experience', 6)]:
        if published[name] is not None:
            amounts = [r['dwRewardID'] for r in reference['rewards'] if r['bRewardType'] == reward_type and r['bTakeMethod'] == 1]
            if amounts != [published[name]]:
                differences.append(dict(field=name, publisher=published[name], pinned_amounts=amounts))
    points = published['achievement_points_in_completion_text']
    amounts = [r['dwRewardID'] for r in reference['rewards'] if r['bRewardType'] == 10 and r['bTakeMethod'] == 1]
    if points and points != amounts:
        differences.append(dict(field='achievement_points', publisher_completion_text=points, pinned_amounts=amounts))
    return differences


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--catalogue', type=Path, default=Path('.runtime/gameforge-normalization/public/quest-catalogue.json'))
    parser.add_argument('--scope', type=Path, default=Path('Gameforge/gor-quests/audit.json'))
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError('Choose a fresh output directory to preserve prior evidence')
    source = Source('https://cz.4story.gameforge.com', args.cache)
    pages, current = [], []
    total = None
    page = 1
    while total is None or len(current) < total:
        markup, url, digest = source.get(f'/quest/browse/{page}?level_min=0&level_max=140&map_id=3&type=0&query=')
        found, count = listing(markup, source.base, 'quest')
        if total is not None and count != total:
            raise ValueError('Publisher catalogue changed during capture')
        total = count
        if any(r['cells'][-1] != 'Tebekut' for r in found):
            raise ValueError('Region filter was not respected')
        current.extend(found)
        pages.append(dict(url=url, sha256=digest))
        page += 1
    if len(current) != total or len({r['id'] for r in current}) != total:
        raise ValueError('Non-reconciling publisher region catalogue')
    old = json.loads(args.catalogue.read_text(encoding='utf-8-sig'))
    catalogue = {r['id']:r for r in old['records']}
    catalogue.update({r['id']:r for r in current})
    scope = json.loads(args.scope.read_text(encoding='utf-8'))
    # Include the seal-producing mission required by Ragoen's mission outside Tebekut.
    ids = {r['id'] for r in scope['rows']} | {r['id'] for r in current} | {3210}
    reference, navigation, _, evidence = load_reference(Path('C:/Program Files (x86)/4Story'), json.loads(Path('Gameforge/reference.json').read_text()))

    def fetch(ident):
        row = catalogue.get(ident, dict(url=source.base+f'/quest/details/{ident}'))
        markup, url, digest = source.get(row['url'])
        facts = detail_facts(markup)
        ref = reference.get(ident)
        nav = navigation.get(ident, [])
        start = sorted({p['npc_id'] for p in nav if p['type']==19 and p['npc_id']})
        end = sorted({p['npc_id'] for p in nav if p['type']==20 and p['npc_id']})
        return dict(id=ident, title=catalogue.get(ident,{}).get('name',facts['heading']),url=url,sha256=digest,
                    listed_in_current_tebekut_catalogue=ident in {r['id'] for r in current},publisher=facts,
                    pinned_start_npc_ids=start,pinned_completion_npc_ids=end,
                    publisher_giver_matches_pinned_navigation=facts['giver_id'] in start if start else None,
                    inferred_completion_matches_pinned_navigation=facts['inferred_completion_npc_id'] in end if end and facts['inferred_completion_npc_id'] else None,
                    source_differences=compare(ref,facts) if ref else [],
                    pinned_reward_types=sorted({r['bRewardType'] for r in ref['rewards']}) if ref else [])

    rows, errors = [], []
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        work = {pool.submit(fetch, ident):ident for ident in sorted(ids)}
        for future in concurrent.futures.as_completed(work):
            try:
                rows.append(future.result())
            except Exception as error:
                errors.append(dict(id=work[future],error=str(error)))
            if (len(rows)+len(errors)) % 25 == 0:
                print(json.dumps(dict(processed=len(rows)+len(errors),total=len(ids),errors=len(errors))),flush=True)
    rows.sort(key=lambda r:r['id'])
    report=dict(schema=1,captured_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        source=source.base+'/quest',publisher_release_version=None,reference_release_version=None,
        scope='All current publisher Tebekut catalogue rows, existing pinned Gor scope and Ragoen seal producer 3210.',
        counts=dict(current_tebekut_catalogue=total,requested_details=len(ids),captured_details=len(rows),errors=len(errors),
                    reward_amount_difference_rows=sum(bool(r['source_differences']) for r in rows),
                    giver_navigation_difference_rows=sum(r['publisher_giver_matches_pinned_navigation'] is False for r in rows),
                    completion_candidate_navigation_difference_rows=sum(r['inferred_completion_matches_pinned_navigation'] is False for r in rows)),
        catalogue_pages=pages,rows=rows,errors=errors,pinned_inputs=evidence,
        limitations=['Read-only evidence capture; no live SQL or gameplay-state changes.',
            'Website patch identity is unknown. Historical patch notes do not override the selected client.',
            'Completion candidates use a unique linked talk objective when present, otherwise the last dialogue speaker. The latter is an inference, not an automatically approved completion trigger.',
            'An omitted reward on the website does not establish a zero reward or justify deleting pinned RT_SOUL/RT_POINT rows.',
            'Spawn probabilities, calendar rules, repeat caps and playable runtime contracts are not established by catalogue presence.'])
    args.output.mkdir(parents=True)
    (args.output/'audit.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    escape=html.escape
    def display_differences(row):
        items=list(row['source_differences'])
        if row['publisher_giver_matches_pinned_navigation'] is False:
            items.append(dict(field='giver_npc',publisher=row['publisher']['giver_id'],pinned_navigation=row['pinned_start_npc_ids']))
        if row['inferred_completion_matches_pinned_navigation'] is False:
            items.append(dict(field='completion_npc_candidate',publisher=row['publisher']['inferred_completion_npc_id'],pinned_navigation=row['pinned_completion_npc_ids']))
        return items

    table=''.join('<tr>'+''.join('<td>'+escape(str(value))+'</td>' for value in
        (r['id'],r['title'],r['publisher']['level_label'],r['publisher']['giver_id'],r['publisher']['inferred_completion_npc_id'],
         r['publisher'].get('objectives',''),display_differences(r)))+
        '<td><a href="'+escape(r['url'],quote=True)+'">Gameforge</a></td></tr>' for r in rows)
    page='<!doctype html><meta charset="utf-8"><title>Questy Goru — Gameforge</title><style>body{font:15px system-ui;margin:30px}table{border-collapse:collapse}td,th{padding:8px;border:1px solid #ccc;text-align:left;vertical-align:top}</style><h1>Questy Goru a Tebekutu na oficiálním webu</h1><p>Zdroj: cz.4story.gameforge.com/quest. Aktuální stránky nemají doloženou verzi patche. Příjemce odvozený z poslední repliky je výslovně označen jako odhad; tabulka sama nic neaktivuje na serveru.</p><pre>'+escape(json.dumps(report['counts'],ensure_ascii=False,indent=2))+'</pre><table><tr><th>ID</th><th>Úkol</th><th>Úroveň</th><th>Zadavatel</th><th>Příjemce podle cíle či poslední repliky</th><th>Cíl</th><th>Rozdíly zdrojů</th><th>Odkaz</th></tr>'+table+'</table>'
    (args.output/'audit.html').write_text(page,encoding='utf-8')
    print(json.dumps(report['counts']),flush=True)
    if errors:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
