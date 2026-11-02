"""Captured Moray combat and opt-in isolated SQL/native-engine regression checks."""
import json
import os
from pathlib import Path
import re
import subprocess
import unittest
import uuid

from reconstruction_protocol import semantic_world
from reconstruction_store import SqlServerStore

ROOT=Path(__file__).resolve().parents[2]
PROOF=ROOT/'Gameforge/gor-training/moray-combat.json'


class MorayEvidenceTests(unittest.TestCase):
    def test_approved_defense_migration_matches_tested_proposal(self):
        proposal=(ROOT/'Gameforge/gor-training/moray-defense-proposal.sql').read_text(encoding='utf8')
        migration=(ROOT/'Database/migrations/060_gor_moray_defense.sql').read_text(encoding='utf8')
        self.assertEqual(proposal.split('USE [TGame_gsp];',1)[1],migration.split('USE [TGame_gsp];',1)[1])
        evidence=json.loads((ROOT/'Gameforge/gor-training/moray-defense-proposal.json').read_text(encoding='utf8'))
        changes={f:(v,evidence['attribute_after'][f]) for f,v in evidence['attribute_before'].items() if v!=evidence['attribute_after'][f]}
        self.assertEqual({'wDL':(124,11),'wMDL':(124,11)},changes)

    def test_original_combat_and_appearance_snapshots(self):
        evidence=json.loads(PROOF.read_text(encoding='utf8'))
        self.assertEqual(11,len(evidence['observations']))
        instances={a['decoded']['instance_id'] for a in evidence['appearance_observations']}
        for observation in evidence['observations']:
            packet=observation['packet'];body=bytes.fromhex(packet['body_hex'])
            event=semantic_world(packet['id'],body,packet['direction'])
            self.assertEqual(observation['decoded'],event)
            self.assertIn(event['attacker_id'],instances)
            self.assertEqual((15,23,10,72,75,74,77,700,1),tuple(event[k] for k in
                ['critical_probability','attack_level','attacker_level','physical_min','physical_max','magic_min','magic_max','skill_id','skill_level']))
            self.assertIn(event['effects'][0]['value'],[17,18,19,26])
        self.assertTrue(all((a['decoded']['max_hp'],a['decoded']['max_mp'])==(711,485) for a in evidence['appearance_observations']))

    def test_truncated_or_wrong_count_rejected(self):
        observation=json.loads(PROOF.read_text(encoding='utf8'))['observations'][0]['packet']
        body=bytes.fromhex(observation['body_hex'])
        for damaged in [body[:87],body[:-1],body[:87]+bytes([2])+body[88:],body+b'\x00']:
            with self.assertRaises(ValueError):semantic_world(observation['id'],damaged,'s2c')
        self.assertIsNone(semantic_world(observation['id'],body,'c2s'))

    @unittest.skipUnless(os.environ.get('FOURDEV_MORAY_SQL_TEST')=='1','Set FOURDEV_MORAY_SQL_TEST=1 for isolated SQL/native checks')
    def test_isolated_migration_and_sql_spawned_native_hits(self):
        store=SqlServerStore(initialize=False);database='4DevQuestTest_moray_'+uuid.uuid4().hex
        created=False;passed=[]
        def execute(sql):return store.execute(sql,database=database)
        def query(sql):return json.loads(''.join(execute(sql).splitlines()) or '[]')
        evidence=json.loads(PROOF.read_text(encoding='utf8'))
        migration=(ROOT/'Database/migrations/059_gor_moray_combat.sql').read_text(encoding='utf8').replace('USE [TGame_gsp];','USE ['+database+'];')
        def reset():
            execute('DELETE TMONATTRCHART; INSERT TMONATTRCHART SELECT * FROM Seed_TMONATTRCHART; DELETE TMONSTERCHART; INSERT TMONSTERCHART SELECT * FROM Seed_TMONSTERCHART; DELETE TCharacterSaveQueue;')
        def native(before=False,defense=False):
            secret=subprocess.run(['docker','exec','araz-mssql','/bin/bash','-c','printenv MSSQL_SA_PASSWORD'],capture_output=True,text=True,check=True).stdout.strip()
            env=dict(os.environ,QUEST_TEST_DB=f'Server=127.0.0.1,11433;Database={database};User ID=sa;Password={secret};TrustServerCertificate=True;Encrypt=False')
            mode='--moray-defense-readonly' if defense else '--moray-combat-readonly'
            source=ROOT/'Gameforge/gor-training/moray-defense-proposal.json' if defense else PROOF
            args=['dotnet',str(ROOT/'Tools/QuestEngineCheck/bin/Release/net10.0/QuestEngineCheck.dll'),mode,str(source)]+(['--before'] if before else [])
            result=subprocess.run(args,env=env,capture_output=True,text=True,encoding='utf8',errors='replace')
            if result.returncode:raise AssertionError(result.stderr[-3500:])
            return json.loads(result.stdout)
        try:
            store.execute('CREATE DATABASE ['+database+'];',database='master');created=True
            objects=json.loads(''.join(store.execute("SELECT name FROM TGame_gsp.sys.tables WHERE schema_id=1 UNION SELECT name FROM TGame_gsp.sys.views WHERE schema_id=1 AND name LIKE '%CHART' FOR JSON PATH;",database='master').splitlines()))
            copies=[]
            for obj in objects:
                name=obj['name'];assert re.fullmatch('[A-Za-z0-9_]+',name)
                if name=='TCharacterSaveQueue':continue
                full=name.endswith('CHART') or name in {'TSKILLDATA','ACCESSORYMAGICTABLE','TLOCALTABLE','TLOCALOCCUPYTABLE','TMISSIONTABLE'}
                copies.append('SELECT '+('' if full else 'TOP(0) ')+'* INTO ['+name+'] FROM TGame_gsp.dbo.['+name+'];')
            execute('\n'.join(copies)+'\nCREATE TABLE TCharacterSaveQueue(FixtureId int NOT NULL); INSERT TQUESTTABLE(dwCharID,dwQuestID,dwTick,bCompleteCount,bTriggerCount) VALUES(999,11025,0,0,1);')
            original=evidence['attribute_before'];execute('UPDATE TMONATTRCHART SET '+','.join('['+f+']='+str(v) for f,v in original.items() if f not in ('wID','bLevel'))+' WHERE wID=30050 AND bLevel=10; SELECT * INTO Seed_TMONATTRCHART FROM TMONATTRCHART; SELECT * INTO Seed_TMONSTERCHART FROM TMONSTERCHART;')
            old=native(True)
            with self.assertRaisesRegex(AssertionError,'differs from original wire'):native()
            passed.append('old SQL reproduces excessive power and fails captured-power check')
            execute(migration);execute(migration)
            self.assertEqual([evidence['attribute_after']],query('SELECT * FROM TMONATTRCHART WHERE wID=30050 AND bLevel=10 FOR JSON PATH;'))
            for table,where in [('TMONATTRCHART','WHERE NOT(wID=30050 AND bLevel=10)'),('TMONSTERCHART','')]:
                execute(f"IF EXISTS(SELECT * FROM {table} {where} EXCEPT SELECT * FROM Seed_{table} {where}) OR EXISTS(SELECT * FROM Seed_{table} {where} EXCEPT SELECT * FROM {table} {where}) THROW 51059,'Unrelated rows changed',1;")
            after=native();reloaded=native()
            self.assertEqual(after,reloaded)
            passed+=['idempotent import and unrelated monster rows preserved','new native process reloads SQL-spawned Moray and 32 authoritative hit reports match captured power']
            for label,change in [('attribute drift','UPDATE TMONATTRCHART SET wAP=251 WHERE wID=30050 AND bLevel=10;'),('missing attribute','DELETE TMONATTRCHART WHERE wID=30050 AND bLevel=10;'),('shared attribute','UPDATE TMONSTERCHART SET wMonAttr=30050 WHERE wID=800;'),('missing monster','DELETE TMONSTERCHART WHERE wID=801;'),('pending saves','INSERT TCharacterSaveQueue VALUES(1);')]:
                reset();execute(change)
                snapshots={t:query('SELECT * FROM '+t+' FOR JSON PATH;') for t in ['TMONATTRCHART','TMONSTERCHART']}
                with self.assertRaisesRegex(RuntimeError,'51059'):execute(migration)
                for t,expected in snapshots.items():self.assertEqual(expected,query('SELECT * FROM '+t+' FOR JSON PATH;'))
                passed.append(label+' rejects atomically')
            self.assertEqual(1,query('SELECT COUNT(*) AS n FROM TQUESTTABLE WHERE dwCharID=999 AND dwQuestID=11025 FOR JSON PATH;')[0]['n'])
            if os.environ.get('FOURDEV_MORAY_DEFENSE_TEST')=='1':
                proposal=json.loads((ROOT/'Gameforge/gor-training/moray-defense-proposal.json').read_text(encoding='utf8'))
                candidate=(ROOT/'Gameforge/gor-training/moray-defense-proposal.sql').read_text(encoding='utf8').replace('USE [TGame_gsp];','USE ['+database+'];')
                reset();execute(migration)
                defense_before=native(True,True)
                with self.assertRaisesRegex(AssertionError,'Defense level differs'):native(False,True)
                execute(candidate);execute(candidate)
                self.assertEqual([proposal['attribute_after']],query('SELECT * FROM TMONATTRCHART WHERE wID=30050 AND bLevel=10 FOR JSON PATH;'))
                defense_after=native(False,True);self.assertEqual(defense_after,native(False,True))
                for table,where in [('TMONATTRCHART','WHERE NOT(wID=30050 AND bLevel=10)'),('TMONSTERCHART','')]:
                    execute(f"IF EXISTS(SELECT * FROM {table} {where} EXCEPT SELECT * FROM Seed_{table} {where}) OR EXISTS(SELECT * FROM Seed_{table} {where} EXCEPT SELECT * FROM {table} {where}) THROW 51060,'Unrelated rows changed',1;")
                defense_passed=['old SQL-spawned defense reproduces 20% accuracy floor','proposed DL/MDL raise fixture accuracy to about 97% without changing attack/HP/MP/defense power','idempotent import and fresh-process SQL reload','unrelated monster rows retained']
                for label,change in [('defense drift','UPDATE TMONATTRCHART SET wDL=125 WHERE wID=30050 AND bLevel=10;'),('earlier attack repair drift','UPDATE TMONATTRCHART SET wAP=57 WHERE wID=30050 AND bLevel=10;'),('missing row','DELETE TMONATTRCHART WHERE wID=30050 AND bLevel=10;'),('shared attribute','UPDATE TMONSTERCHART SET wMonAttr=30050 WHERE wID=800;'),('pending saves','INSERT TCharacterSaveQueue VALUES(1);')]:
                    reset();execute(migration);execute(change)
                    snapshots={t:query('SELECT * FROM '+t+' FOR JSON PATH;') for t in ['TMONATTRCHART','TMONSTERCHART','TQUESTTABLE']}
                    with self.assertRaisesRegex(RuntimeError,'51060'):execute(candidate)
                    for t,expected in snapshots.items():self.assertEqual(expected,query('SELECT * FROM '+t+' FOR JSON PATH;'))
                    defense_passed.append(label+' rejects atomically')
                print(json.dumps(dict(DefenseProposalChecks=defense_passed,Before=defense_before,After=defense_after,SourceDatabaseModified=False,PlayerRowsCopied=0)),flush=True)
            print(json.dumps(dict(Passed=passed,Before=old,After=after,SourceDatabaseModified=False,PlayerRowsCopied=0)),flush=True)
        finally:
            if created:store.execute('ALTER DATABASE ['+database+'] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ['+database+'];',database='master')


if __name__=='__main__':unittest.main()
