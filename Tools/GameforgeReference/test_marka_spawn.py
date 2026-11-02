"""Captured Marka placement and opt-in isolated SQL/native spawn regression."""
import json
import os
from pathlib import Path
import re
import subprocess
import unittest
import uuid

from reconstruction_protocol import semantic_world
from reconstruction_store import SqlServerStore

ROOT = Path(__file__).resolve().parents[2]
PROOF = ROOT / 'Gameforge/gor-training/marka-spawn.json'


def native(database, before=False):
    secret = subprocess.run(['docker', 'exec', 'araz-mssql', '/bin/bash', '-c', 'printenv MSSQL_SA_PASSWORD'], capture_output=True, text=True, check=True).stdout.strip()
    env = dict(os.environ, QUEST_TEST_DB=f'Server=127.0.0.1,11433;Database={database};User ID=sa;Password={secret};TrustServerCertificate=True;Encrypt=False')
    args = ['dotnet', str(ROOT / 'Tools/QuestEngineCheck/bin/Release/net10.0/QuestEngineCheck.dll'), '--marka-spawn-readonly', str(PROOF)]
    if before:
        args.append('--before')
    result = subprocess.run(args, env=env, capture_output=True, text=True, encoding='utf8', errors='replace')
    if result.returncode:
        raise AssertionError(result.stderr[-2500:])
    return json.loads(result.stdout)


class MarkaSpawnTests(unittest.TestCase):
    def test_packets_agree_with_pinned_hunt_marker_and_spawn_identity(self):
        proof = json.loads(PROOF.read_text(encoding='utf8'))
        node = proof['original_navigation']['node']
        self.assertGreaterEqual(len(proof['observations']), 2)
        for observation in proof['observations']:
            packet = observation['packet']
            event = semantic_world(packet['id'], bytes.fromhex(packet['body_hex']), packet['direction'])
            self.assertEqual((800, 5024, 10, 711, 711), (event['template_id'], event['instance_id'] >> 16, event['level'], event['max_hp'], event['hp']))
            for key in ('x', 'y', 'z'):
                self.assertEqual(event[key], observation['decoded'][key])
                self.assertEqual(node[key], event[key])
        self.assertEqual({'fPosX', 'fPosY', 'fPosZ'}, {k for k, v in proof['spawn_before'].items() if v != proof['spawn_after'][k]})
        for key in ('x', 'y', 'z'):
            self.assertEqual(node[key], proof['spawn_after']['fPos' + key.upper()])

    @unittest.skipUnless(os.environ.get('FOURDEV_MARKA_SQL_TEST') == '1', 'Set FOURDEV_MARKA_SQL_TEST=1 for isolated SQL/native checks')
    def test_isolated_import_rejection_atomicity_and_native_spawn(self):
        store = SqlServerStore(initialize=False)
        database = '4DevQuestTest_marka_' + uuid.uuid4().hex
        created = False
        proof = json.loads(PROOF.read_text(encoding='utf8'))
        migration = (ROOT / 'Database/migrations/061_gor_marka_spawn.sql').read_text(encoding='utf8').replace('USE [TGame_gsp];', 'USE [' + database + '];')
        def execute(query):
            return store.execute(query, database=database)
        def query(statement):
            return json.loads(''.join(execute(statement).splitlines()) or '[]')
        def reset():
            execute('DELETE TMONSPAWNCHART; INSERT TMONSPAWNCHART SELECT * FROM Seed_TMONSPAWNCHART; DELETE TMAPMONCHART; INSERT TMAPMONCHART SELECT * FROM Seed_TMAPMONCHART; DELETE TCharacterSaveQueue; UPDATE TMONSTERCHART SET wKind=800 WHERE wID=800;')
        try:
            store.execute('CREATE DATABASE [' + database + '];', database='master')
            created = True
            objects = json.loads(''.join(store.execute("SELECT name FROM TGame_gsp.sys.tables WHERE schema_id=1 UNION SELECT name FROM TGame_gsp.sys.views WHERE schema_id=1 AND name LIKE '%CHART' FOR JSON PATH;", database='master').splitlines()))
            copies = []
            for obj in objects:
                name = obj['name']
                assert re.fullmatch('[A-Za-z0-9_]+', name)
                if name == 'TCharacterSaveQueue':
                    continue
                full = name.endswith('CHART') or name in {'TSKILLDATA', 'ACCESSORYMAGICTABLE', 'TLOCALTABLE', 'TLOCALOCCUPYTABLE', 'TMISSIONTABLE'}
                copies.append('SELECT ' + ('' if full else 'TOP(0) ') + '* INTO [' + name + '] FROM TGame_gsp.dbo.[' + name + '];')
            execute('\n'.join(copies) + '\nCREATE TABLE TCharacterSaveQueue(FixtureId int NOT NULL); INSERT TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(999,11027,0,0,1);')
            before = proof['spawn_before']
            execute('UPDATE TMONSPAWNCHART SET ' + ','.join(k + '=' + str(before[k]) for k in ('fPosX', 'fPosY', 'fPosZ')) + ' WHERE wID=5024; SELECT * INTO Seed_TMONSPAWNCHART FROM TMONSPAWNCHART; SELECT * INTO Seed_TMAPMONCHART FROM TMAPMONCHART;')
            old = native(database, True)
            with self.assertRaisesRegex(AssertionError, 'SQL Marka placement differs'):
                native(database)
            saved = query('SELECT * FROM TQUESTTABLE FOR JSON PATH;')
            execute(migration)
            execute(migration)
            actual = query('SELECT * FROM TMONSPAWNCHART WHERE wID=5024 FOR JSON PATH;')[0]
            # SQL JSON real numbers use fewer decimal digits; compare values as IEEE float32.
            import struct
            for key, expected in proof['spawn_after'].items():
                if key.startswith('fPos'):
                    self.assertEqual(struct.pack('<f', expected), struct.pack('<f', actual[key]))
                else:
                    self.assertEqual(expected, actual[key])
            after = native(database)
            self.assertEqual(after, native(database))
            execute("IF EXISTS(SELECT * FROM TMONSPAWNCHART WHERE wID<>5024 EXCEPT SELECT * FROM Seed_TMONSPAWNCHART WHERE wID<>5024) OR EXISTS(SELECT * FROM Seed_TMONSPAWNCHART WHERE wID<>5024 EXCEPT SELECT * FROM TMONSPAWNCHART WHERE wID<>5024) THROW 51061,'Unrelated spawns changed',1;")
            self.assertEqual(saved, query('SELECT * FROM TQUESTTABLE FOR JSON PATH;'))
            passed = ['old SQL-spawned Marka is over 85 units from hunt marker', 'only placement changes, idempotent import', 'SQL-loaded appearance, hunt credit and existing respawn delay', 'fresh-process SQL reload', 'unrelated spawns and saved progress retained']
            for label, change in [
                ('missing spawn', 'DELETE TMONSPAWNCHART WHERE wID=5024;'),
                ('duplicate spawn', 'INSERT TMONSPAWNCHART SELECT * FROM TMONSPAWNCHART WHERE wID=5024;'),
                ('coordinate drift', 'UPDATE TMONSPAWNCHART SET fPosX=1 WHERE wID=5024;'),
                ('spawn rule drift', 'UPDATE TMONSPAWNCHART SET bRange=4 WHERE wID=5024;'),
                ('membership drift', 'UPDATE TMAPMONCHART SET wMonID=801 WHERE wSpawnID=5024;'),
                ('monster identity drift', 'UPDATE TMONSTERCHART SET wKind=801 WHERE wID=800;'),
                ('pending saves', 'INSERT TCharacterSaveQueue VALUES(1);')]:
                reset()
                execute(change)
                snapshot = {t: query('SELECT * FROM ' + t + ' FOR JSON PATH;') for t in ('TMONSPAWNCHART', 'TMAPMONCHART', 'TQUESTTABLE')}
                with self.assertRaisesRegex(RuntimeError, '51061'):
                    execute(migration)
                for table, expected in snapshot.items():
                    self.assertEqual(expected, query('SELECT * FROM ' + table + ' FOR JSON PATH;'))
                passed.append(label + ' rejects atomically')
            result = dict(Passed=passed, Before=old, After=after, SourceDatabaseModified=False, PlayerRowsCopied=0)
            output = ROOT / '.runtime/marka-spawn/sql-validation.json'
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf8')
            print(json.dumps(result), flush=True)
        finally:
            if created:
                store.execute('ALTER DATABASE [' + database + '] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [' + database + '];', database='master')


if __name__ == '__main__':
    unittest.main()
