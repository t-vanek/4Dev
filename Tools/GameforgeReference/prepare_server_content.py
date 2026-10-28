"""Reconstruct supported missing server content from pinned client records and captured public details.

This deliberately distinguishes client facts from local compatibility policy. It never invents combat
attributes, spawn coordinates/timers, drop probabilities, skill effects or ordinary mission conditions.
"""
import argparse
import collections
import datetime
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import struct
import zlib

from catalogue import quest_facts
from client_facts import items, npcs, quests
from client_records import fixed, named
from identity_audit import normal
from normalization import encode, equal, sql_type, sql_value


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


class Plan:
    def __init__(self, args):
        self.args = args
        self.schema = load(args.snapshot / 'schema.json')
        self.tables = {p.stem: load(p) for p in args.snapshot.glob('*.json')
                       if p.stem not in ('schema', 'counts')}
        self.columns = {t: [r for r in self.schema if r['table_name'] == t] for t in self.tables}
        self.pinned = {r['path'].lower(): r for r in load(args.manifest)['files']}
        self.report = {'prepared_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                       'source': str(args.source.resolve()), 'source_files': [], 'additions': {},
                       'evidence': [], 'deferred': [], 'initial_missing_ids': {}}
        self.changes = self.report['additions']
        self.npc_source = collections.defaultdict(list)
        for path in sorted((args.source / 'Tcd').glob('TNPC*.tcd')):
            match = re.fullmatch(r'TNPC([0-9a-fA-F]{8})\.tcd', path.name)
            if not match or not path.stat().st_size:
                continue
            for row in npcs(self.read('Tcd/' + path.name), int(match[1], 16) >> 16):
                row['source_file'] = 'Tcd/' + path.name
                self.npc_source[row['wID']].append(row)
        self.item_source = {r['wItemID']: r for r in items(self.read('Tcd/TItem.tcd'))}
        self.quest_source = {r['dwQuestID']: r for r in quests(self.read('TQuest.mpq'))['quests']}
        self.read('Tcd/TMon.tcd')
        self.monsters, layout = named(args.source / 'Tcd/TMon.tcd')
        if 'suffix=125;' not in layout:
            raise ValueError('Unexpected original monster layout')
        self.read('Tcd/TSkill.tcd')
        self.skills, _ = named(args.source / 'Tcd/TSkill.tcd')
        domains = {'item': (self.item_source, 'TITEMCHART', 'wItemID'),
                   'monster': ({r.key: r for r in self.monsters}, 'TMONSTERCHART', 'wID'),
                   'npc': (self.npc_source, 'TNPCCHART', 'wID'),
                   'quest': (self.quest_source, 'TQUESTCHART', 'dwQuestID'),
                   'skill': ({r.key: r for r in self.skills}, 'TSKILLCHART', 'wID')}
        for domain, (incoming, table, key) in domains.items():
            self.report['initial_missing_ids'][domain] = sorted(set(incoming) - {r[key] for r in self.tables[table]})

    def read(self, relative):
        data = (self.args.source / relative).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != self.pinned[relative.lower()]['sha256']:
            raise ValueError('Changed original file: ' + relative)
        if relative not in {r['path'] for r in self.report['source_files']}:
            self.report['source_files'].append({'path': relative, 'sha256': digest})
        return data

    def defer(self, domain, ident, reason):
        self.report['deferred'].append({'domain': domain, 'id': ident, 'reason': reason})

    def add(self, table, row, evidence):
        cols = self.columns[table]
        if set(row) != {c['column_name'] for c in cols}:
            raise ValueError('Incomplete row for ' + table)
        # The legacy varchar labels cannot represent Czech. The migration widens these labels without
        # changing their pre-existing values. Everything the game displays already comes from the client.
        row = {c['column_name']: encode(row[c['column_name']],
               {**c, 'data_type': 'nvarchar', 'max_length': c['max_length'] * 2}
               if table in ('TNPCCHART', 'TMONSTERCHART') and c['data_type'] == 'varchar'
               and c['column_name'] != 'szLocal' else c) for c in cols}
        self.changes.setdefault(table, []).append(row)
        self.report['evidence'].append({'table': table, 'key': {
            c['column_name']: row[c['column_name']] for c in cols if c.get('primary_key_ordinal')}, **evidence})

    def item_rows(self):
        # Mount deeds use an established runtime path. Combat equipment, dungeon tickets,
        # renames and consumables whose skill is absent remain deferred, rather than creating inert items.
        mount_ids = {r['wMountID'] for r in self.tables['TMOUNTCHART']}
        for ident in self.report['initial_missing_ids']['item']:
            src = self.item_source[ident]
            if not ((src['bType'], src['bKind']) == (12, 23) and src['wUseValue'] in mount_ids):
                self.defer('item', ident, 'Requires unpublished equipment fields or an unimplemented/missing use target')
                continue
            siblings = [r for r in self.tables['TITEMCHART']
                        if r['bType'] == src['bType'] and r['bKind'] == src['bKind']]
            if not siblings:
                self.defer('item', ident, 'No compatible existing server template')
                continue
            keys = set(src) - {'wItemID', 'szNAME', 'wUseValue', 'wAttrID'}
            matching = [r for r in siblings if all(equal(r[k], src[k]) for k in keys)]
            if not matching:
                self.defer('item', ident, 'The existing template differs in a represented original field')
                continue
            inherited = sorted(set(matching[0]) - set(src))
            signatures = {json.dumps({k: r[k] for k in inherited}, sort_keys=True) for r in matching}
            if len(signatures) != 1:
                self.defer('item', ident, 'Unrepresented server fields disagree among equivalent templates')
                continue
            donor = min(matching, key=lambda r: r['wItemID'])
            self.add('TITEMCHART', {**donor, **src}, {
                'source': 'Tcd/TItem.tcd', 'kind': 'original fields plus equivalent-template compatibility policy',
                'donor_id': donor['wItemID'], 'inherited_fields': {k: donor[k] for k in inherited},
                'unpublished_fields_are_not_certified_gameforge': True})

    def monster_rows(self):
        donor = next(r for r in self.tables['TMONSTERCHART'] if r['wID'] == 31182)
        if donor['bCanAttack'] or donor['wExp'] or donor['wMonAttr']:
            raise ValueError('The noncombat companion compatibility template changed')
        selected = set(range(31183, 31190))
        for rec in self.monsters:
            if rec.key not in self.report['initial_missing_ids']['monster']:
                continue
            if rec.key not in selected:
                self.defer('monster', rec.key, 'No complete authoritative attributes, AI, exp, loot and spawn data')
                continue
            raw = rec.raw[-125:]
            if raw[65] != 0:
                raise ValueError('A proposed companion can attack in the original client')
            attack_skills = struct.unpack_from('<4H', raw, 54)
            if set(attack_skills) - {0} - {r['wID'] for r in self.tables['TSKILLCHART']}:
                self.defer('monster', rec.key, 'Companion references an absent skill')
                continue
            row = {**donor, 'wID': rec.key, 'szName': rec.names[-1], 'szName2': rec.names[-1],
                   'wKind': int.from_bytes(rec.raw[2:4], 'little'), 'bCanSelect': raw[63],
                   'bCanAttack': raw[65], 'bTame': raw[67], 'fSize': struct.unpack_from('<f', raw, 74)[0]}
            row.update({f'wSkill{i+1}': value for i, value in enumerate(attack_skills)})
            self.add('TMONSTERCHART', row, {'source': 'Tcd/TMon.tcd', 'kind': 'noncombat companion registration',
                'donor_id': 31182, 'combat_statistics_not_invented': True,
                'limitation': 'Species registration; unavailable rune mappings are not inferred from ID order'})

    def npc_rows(self):
        links = collections.defaultdict(set)
        for row in fixed(self.read('Tcd/TDest.tcd'), 21, 4, True):
            portal, dest, price, npc = struct.unpack_from('<HHIH', row.raw)
            if npc:
                links[npc].add(portal)
        portal_ids = {r['wPortalID'] for r in self.tables['TPORTALCHART']}
        # Standard NPCs use no territory discount condition in the existing compatible schemas. Keep the
        # policy explicit; portal stock is added only when the original TDest names one existing portal.
        for ident in self.report['initial_missing_ids']['npc']:
            source = self.npc_source[ident]
            if len(source) != 1:
                self.defer('npc', ident, 'Conflicting source placements for a single registry ID')
                continue
            src = source[0]
            portal = next(iter(links[ident])) if len(links[ident]) == 1 and links[ident] <= portal_ids else None
            if src['bType'] not in (0, 3) and not (src['bType'] == 8 and portal is not None):
                self.defer('npc', ident, 'Missing verified portal destination, shop stock or specialized NPC action')
                continue
            row = {'wID': ident, 'NC_szName2': src['title'], 'szName': src['name'], 'bType': src['bType'],
                   'dwClass': src['class_index'], 'bCountryID': src['bCountryID'], 'wItemID': src['wItemID'],
                   'wMapID': src['wMapID'], 'fPosX': src['fPosX'], 'fPosY': src['fPosY'], 'fPosZ': src['fPosZ'],
                   'wLocalID': 0, 'bCondition': 0, 'bDiscountRate': 0, 'bAddProb': 0, 'szLocal': ''}
            self.add('TNPCCHART', row, {'source': src['source_file'], 'kind': 'original NPC placement and attributes',
                'compatibility_defaults': {'wLocalID': 0, 'bCondition': 0, 'bDiscountRate': 0, 'bAddProb': 0}})
            if portal is not None and src['bType'] == 8:
                self.add('TNPCITEMCHART', {'wNpcID': ident, 'dwItemID': portal},
                         {'source': 'Tcd/TDest.tcd', 'kind': 'explicit NPC-to-portal association'})
        # Routing is a deployment property, not Gameforge gameplay data. This installation has one map
        # server. Route the added NPC maps on precisely its existing group/channel/server configuration.
        roots = [r for r in self.tables['TMAPCHART'] if r['wMapID'] == 0 and r['bChannel'] == 1]
        if len(roots) != 1:
            raise ValueError('Ambiguous local map-server route')
        route = roots[0]
        represented = {r.key for r in fixed(self.read('Tcd/TMAP.tcd'), 46, 4)}
        current = {(r['bGroupID'], r['bChannel'], r['wMapID']) for r in self.tables['TMAPCHART']}
        for map_id in sorted({r['wMapID'] for r in self.changes.get('TNPCCHART', [])}):
            key = route['bGroupID'], route['bChannel'], map_id
            if key in current:
                continue
            if map_id not in represented:
                raise ValueError('NPC map has no original client geometry')
            self.add('TMAPCHART', {**route, 'wMapID': map_id}, {
                'source': 'Tcd/TMAP.tcd', 'kind': 'local single-server routing; not an official topology'})

    def craft_rows(self):
        with sqlite3.connect(self.args.public / 'quest-details.sqlite') as db:
            public = {ident: json.loads(zlib.decompress(blob))
                      for ident, blob in db.execute("SELECT id,facts FROM details WHERE kind='quest'")}
        items_present = {r['wItemID'] for r in self.tables['TITEMCHART']} | {
            r['wItemID'] for r in self.changes.get('TITEMCHART', [])}
        npcs_present = {r['wID']: r for r in self.tables['TNPCCHART']}
        next_id = {t: max((r['dwID'] for r in self.tables[t]), default=0) + 1
                   for t in ('TQUESTTERMCHART', 'TQREWARDCHART', 'TQCONDITIONCHART')}
        for ident in self.report['initial_missing_ids']['quest']:
            src = self.quest_source[ident]
            if src['bType'] != 18:
                self.defer('quest', ident, 'Ordinary mission trigger/conditions/child graph are not fully encoded in the client')
                continue
            pub = public.get(ident)
            if not pub or normal(pub['name']) != normal(src['title']):
                self.defer('quest', ident, 'No matching independently published recipe/giver')
                continue
            payload = (self.args.cache / (hashlib.sha256(pub['url'].encode()).hexdigest() + '.html')).read_bytes()
            if hashlib.sha256(payload).hexdigest() != pub['sha256']:
                raise ValueError('Changed public evidence')
            web = quest_facts(payload.decode('utf-8'))
            giver = web['giver_id']
            if giver not in npcs_present or npcs_present[giver]['bType'] != 20:
                self.defer('quest', ident, 'Published giver is not an existing craft NPC')
                continue
            if src['unknown_prefix'] or src['unknown_flag'] or src['min_level'] or src['max_level']:
                self.defer('quest', ident, 'Recipe has additional restrictions requiring validation')
                continue
            terms, rewards = src['terms'], src['rewards']
            if not terms or any(t['bTermType'] != 2 or t['bCount'] == 0 or t['dwTermID'] not in items_present for t in terms):
                self.defer('quest', ident, 'Invalid or missing recipe ingredient')
                continue
            if len({t['dwTermID'] for t in terms}) != len(terms):
                self.defer('quest', ident, 'Duplicate ingredient entries require aggregation')
                continue
            if len(rewards) != 1 or any(r['bRewardType'] != 2 or r['bCount'] == 0 or r['bTakeMethod'] != 1
                    or r['bTakeData'] != 0 or r['dwRewardID'] not in items_present for r in rewards):
                self.defer('quest', ident, 'Unsupported or absent recipe output')
                continue
            expected = [(r['dwRewardID'], r['bCount']) for r in rewards]
            if sorted((r['id'], r['count']) for r in web['reward_items']) != sorted(expected):
                self.defer('quest', ident, 'Published/client recipe output or count differs')
                continue
            output = self.item_source[rewards[0]['dwRewardID']]
            if not 0 < output['dwClassID'] <= 63:
                self.defer('quest', ident, 'Invalid output class mask')
                continue
            self.add('TQUESTCHART', {'dwQuestID': ident, 'dwParentID': 0, 'bType': 18, 'bForceRun': 0,
                     'bTriggerType': 3, 'dwTriggerID': giver, 'bCountMax': 0, 'bLevel': src['bLevel'],
                     'bMain': src['bMain'], 'bConditionCheck': 0}, {
                'source': 'TQuest.mpq', 'web_source': pub['url'], 'web_sha256': pub['sha256'],
                'kind': 'client recipe + published giver/output + reconstructed recipe policy',
                'policy': 'Repeatable direct craft; class restriction derived from original output-item class mask',
                'server_only_conditions_are_not_certified_gameforge': True})
            if output['dwClassID'] != 63:
                t = 'TQCONDITIONCHART'
                self.add(t, {'dwID': next_id[t], 'dwQuestID': ident, 'bConditionType': 5,
                             'dwConditionID': output['dwClassID'], 'bCount': 0},
                         {'source': 'Tcd/TItem.tcd', 'kind': 'derived output-class restriction'})
                next_id[t] += 1
            for term in terms:
                t = 'TQUESTTERMCHART'
                self.add(t, {'dwID': next_id[t], 'dwQuestID': ident, **term},
                         {'source': 'TQuest.mpq', 'kind': 'exact ingredient ID and count'})
                next_id[t] += 1
            for reward in rewards:
                t = 'TQREWARDCHART'
                self.add(t, {'dwID': next_id[t], 'dwQuestID': ident, **reward, 'dwQuestMob': 0,
                             'dwQuestTime': 0, 'dwQuestPathMob': 0, 'dwTicketID': 0, 'bSendQ': 0},
                         {'source': 'TQuest.mpq', 'kind': 'exact output; unused legacy metadata defaults to zero'})
                next_id[t] += 1

    def run(self):
        self.item_rows()
        self.monster_rows()
        self.npc_rows()
        self.craft_rows()
        for ident in self.report['initial_missing_ids']['skill']:
            self.defer('skill', ident, 'Client timing/range/visuals do not encode complete TSKILLDATA effects')
        self.report['summary'] = {'initial_missing': {k: len(v) for k, v in self.report['initial_missing_ids'].items()},
                                  'added_rows': {k: len(v) for k, v in self.changes.items()},
                                  'deferred_by_domain': dict(collections.Counter(r['domain'] for r in self.report['deferred']))}
        self.args.output.parent.mkdir(parents=True, exist_ok=True)
        self.args.output.write_text(json.dumps(self.report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        self.write_sql()
        print(json.dumps(self.report['summary']))

    def write_sql(self):
        lines = ['-- Sources, exact rows and explicit reconstruction policies: Gameforge/server-import/plan.json',
                 'USE [TGame_gsp];', 'GO', 'SET QUOTED_IDENTIFIER ON;', 'SET ANSI_NULLS ON;',
                 'SET NOCOUNT ON;', 'SET XACT_ABORT ON;', 'BEGIN TRANSACTION;']
        lines.append('DECLARE @GfDefaultName SYSNAME,@GfDefaultDefinition NVARCHAR(MAX),@GfAlter NVARCHAR(MAX);')
        # Unicode widening preserves old strings while allowing the selected CZ client labels.
        for t in ('TNPCCHART', 'TMONSTERCHART'):
            for c in self.columns[t]:
                if c['data_type'] == 'varchar' and c['column_name'] != 'szLocal':
                    col = c['column_name']
                    index_name = 'IX_TNPCCHART_NAME' if t == 'TNPCCHART' else 'IX_TMONSTERCHART_NAME_copy1'
                    index_guard = []
                    if col == 'szName':
                        # These two existing one-column indexes were inspected on this baseline. Guard
                        # their shape before rebuilding, retaining their names, filegroup and fill factor.
                        index_guard = [
                            f"IF NOT EXISTS(SELECT 1 FROM sys.indexes i JOIN sys.data_spaces d ON d.data_space_id=i.data_space_id WHERE i.object_id=OBJECT_ID(N'dbo.{t}') AND i.name=N'{index_name}' AND i.type=2 AND i.is_unique=0 AND i.fill_factor=80 AND i.has_filter=0 AND i.is_disabled=0 AND i.is_padded=0 AND i.ignore_dup_key=0 AND i.allow_row_locks=1 AND i.allow_page_locks=1 AND d.name=N'PRIMARY' AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id)=1 AND EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND c.name=N'szName' AND ic.key_ordinal=1 AND ic.is_descending_key=0 AND ic.is_included_column=0)) THROW 51000,'Unexpected existing NPC/monster name index',1;",
                            f'DROP INDEX [{index_name}] ON dbo.[{t}];']
                    lines.extend([
                        f"IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.{t}') AND name=N'{col}' AND system_type_id=167) BEGIN",
                        'SET @GfDefaultName=NULL; SET @GfDefaultDefinition=NULL;',
                        f"SELECT @GfDefaultName=d.name,@GfDefaultDefinition=d.definition FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE c.object_id=OBJECT_ID(N'dbo.{t}') AND c.name=N'{col}';",
                        f"IF @GfDefaultName IS NOT NULL BEGIN SET @GfAlter=N'ALTER TABLE dbo.[{t}] DROP CONSTRAINT '+QUOTENAME(@GfDefaultName); EXEC sys.sp_executesql @GfAlter; END;",
                        *index_guard,
                        f'ALTER TABLE dbo.[{t}] ALTER COLUMN [{col}] NVARCHAR({c["max_length"]}) COLLATE Latin1_General_CI_AS_KS NOT NULL;',
                        f"IF @GfDefaultName IS NOT NULL BEGIN SET @GfAlter=N'ALTER TABLE dbo.[{t}] ADD CONSTRAINT '+QUOTENAME(@GfDefaultName)+N' DEFAULT '+@GfDefaultDefinition+N' FOR [{col}]'; EXEC sys.sp_executesql @GfAlter; END;",
                        *([f'CREATE NONCLUSTERED INDEX [{index_name}] ON dbo.[{t}]([szName] ASC) WITH(FILLFACTOR=80,ALLOW_ROW_LOCKS=ON,ALLOW_PAGE_LOCKS=ON) ON [PRIMARY];'] if col == 'szName' else []),
                        'END;'])
        for table, rows in self.changes.items():
            cols = self.columns[table]
            cols = [{**c, 'data_type': 'nvarchar', 'max_length': c['max_length'] * 2}
                    if table in ('TNPCCHART', 'TMONSTERCHART') and c['data_type'] == 'varchar'
                    and c['column_name'] != 'szLocal' else c for c in cols]
            if any(c['is_identity'] or c['is_computed'] for c in cols):
                raise ValueError('Explicit identity handling required: ' + table)
            names = ','.join('[' + c['column_name'] + ']' for c in cols)
            temp = '#Gf_' + table
            definition = ','.join('[' + c['column_name'] + '] ' + sql_type(c) + (' NULL' if c['is_nullable'] else ' NOT NULL') for c in cols)
            lines.append(f'CREATE TABLE {temp}({definition});')
            values = ['(' + ','.join(sql_value(r[c['column_name']], c['data_type']) for c in cols) + ')' for r in rows]
            for offset in range(0, len(values), 250):
                lines.append(f'INSERT {temp} ({names}) VALUES ' + ',\n'.join(values[offset:offset + 250]) + ';')
            pk = [c['column_name'] for c in sorted(cols, key=lambda c: c.get('primary_key_ordinal') or 999)
                  if c.get('primary_key_ordinal')]
            # TNPCITEMCHART has no declared key; its meaningful identity is the pair.
            if not pk:
                if table != 'TNPCITEMCHART':
                    raise ValueError('No stable import key: ' + table)
                pk = ['wNpcID', 'dwItemID']
            join = ' AND '.join(f't.[{k}]=n.[{k}]' for k in pk)
            left = ','.join('t.[' + c['column_name'] + ']' for c in cols)
            right = ','.join('n.[' + c['column_name'] + ']' for c in cols)
            lines.append(f"IF EXISTS(SELECT 1 FROM {temp} n JOIN dbo.[{table}] t ON {join} WHERE EXISTS(SELECT {left} EXCEPT SELECT {right})) THROW 51000,'Conflicting existing {table} import key',1;")
            lines.append(f'INSERT dbo.[{table}] ({names}) SELECT {right} FROM {temp} n WHERE NOT EXISTS(SELECT 1 FROM dbo.[{table}] t WHERE {join});')
            lines.append(f"IF EXISTS(SELECT {names} FROM {temp} EXCEPT SELECT {names} FROM dbo.[{table}]) THROW 51000,'Missing or changed {table} import row',1;")
            lines.append(f'DROP TABLE {temp};')
        lines += ['COMMIT TRANSACTION;', 'GO']
        self.args.sql.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('snapshot', 'source', 'public', 'cache', 'output', 'sql'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--manifest', type=Path, default=Path('Gameforge/reference.json'))
    Plan(parser.parse_args()).run()


if __name__ == '__main__':
    main()
