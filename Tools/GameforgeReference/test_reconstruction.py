import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
import sqlite3
import zlib
from types import SimpleNamespace

from collect_reconstruction import complete_pcapng_prefix,session_identity,discard_committed_snapshot,collection_lock,packet_inventory,semantics_version,import_client
from decode_capture import semantic
from reconstruction_client import full_quests,navigation,resource_index
from reconstruction_protocol import semantic_world,BASE
from reconstruction_store import SQLiteStore,batches,key,row
from reconstruction_publisher import publisher_rows


class ReconstructionTests(unittest.TestCase):
    def memory_store(self):
        store=SQLiteStore(':memory:');self.addCleanup(store.db.close);return store

    def test_interrupted_client_import_resumes_all_records_before_completion_marker(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'client';(source/'Tcd').mkdir(parents=True)
            (source/'Tcd'/'Unknown.tcd').write_bytes(b'original source')
            manifest=root/'reference.json';manifest.write_text('{"files":[]}',encoding='utf8')
            args=SimpleNamespace(source=source,manifest=manifest,work=root/'work')
            store=self.memory_store();put=store.put;calls=0
            def interrupted(records):
                nonlocal calls
                calls+=1
                if calls==2:raise RuntimeError('simulated disconnect')
                return put(records)
            parsed=[('client.test',str(n),dict(id=n)) for n in range(1201)]
            with patch('reconstruction_client.parse_source',return_value=parsed):
                with patch.object(store,'put',side_effect=interrupted):
                    with self.assertRaisesRegex(RuntimeError,'disconnect'):import_client(args,store,False)
                self.assertEqual(1,len(store.rows(kind='source.client')))
                self.assertEqual([],store.rows(kind='import.client_complete'))
                import_client(args,store,False)
                self.assertEqual(1201,len(store.rows(kind='client.test')))
                self.assertEqual(1,len(store.rows(kind='import.client_complete')))
                self.assertEqual(0,import_client(args,store,False))

    def test_collection_lock_rejects_live_owner_and_releases_after_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            work=Path(folder)
            with self.assertRaisesRegex(RuntimeError,'deliberate'):
                with collection_lock(work):
                    with patch('collect_reconstruction.process_alive',return_value=True):
                        with self.assertRaisesRegex(RuntimeError,'Another collection'):
                            with collection_lock(work):pass
                    raise RuntimeError('deliberate')
            self.assertFalse((work/'cycle.lock').exists())

    def test_coverage_ignores_obsolete_interpretations(self):
        store=self.memory_store();source=key('capture')
        packet=dict(stream=1,direction='s2c',sequence=1,id_hex='0x2100',frame=1,body_hex='00')
        store.put([row('packet',source,'1',packet),row('event.old',source,'1',dict(packet,kind='old',semantic_version='obsolete'))])
        self.assertEqual(1,packet_inventory(store)[0]['uninterpreted_packets'])
        store.put([row('event.current',source,'1',dict(packet,kind='current',semantic_version=semantics_version()))])
        self.assertEqual(0,packet_inventory(store)[0]['uninterpreted_packets'])

    def test_publisher_archive_preserves_url_hash_unicode_and_unknown_release(self):
        with tempfile.TemporaryDirectory() as folder:
            source=Path(folder)/'source';source.mkdir();work=Path(folder)/'work'
            (source/'quest-catalogue.json').write_text(json.dumps(dict(records=[dict(id=1,title='Osvědčení')]),ensure_ascii=False),encoding='utf8')
            db=sqlite3.connect(source/'quest-details.sqlite')
            try:
                db.execute('CREATE TABLE details(kind TEXT,id INTEGER,url TEXT,sha256 TEXT,facts BLOB)')
                db.execute('INSERT INTO details VALUES(?,?,?,?,?)',('quest',1,'https://cz.4story.gameforge.com/quest/details/1','a'*64,zlib.compress(json.dumps(dict(title='Osvědčení')).encode())))
                db.commit()
            finally:db.close()
            store=self.memory_store();batches(store,publisher_rows(source,work))
            detail=json.loads(store.rows(kind='publisher.detail.quest')[0]['payload'])
            self.assertEqual('Osvědčení',detail['facts']['title']);self.assertEqual('a'*64,detail['page_sha256'])
            self.assertFalse(json.loads(store.rows(kind='source.publisher')[0]['payload'])['current_server_version_match_verified'])
            self.assertEqual(0,batches(store,publisher_rows(source,work,{r['source'] for r in store.rows(kind='source.publisher')})))

    def test_immutable_sql_rows_roundtrip_and_retry_are_idempotent(self):
        store=self.memory_store();source=key('test')
        record=row('client.quest',source,'11015',dict(id=11015,title="Oko 'žluťoučké'",text='literal $(test) \\u1234\n'),b'\x00\xff')
        self.assertEqual(1,store.put([record]));self.assertEqual(0,store.put([record]))
        self.assertEqual(record['payload'],store.rows(kind='client.quest')[0]['payload'])
        self.assertEqual(b'\x00\xff',store.read_blob(record['key']))

    def test_immutable_identity_conflict_rolls_back_prior_rows_in_batch(self):
        store=self.memory_store();source=key('test');original=row('packet',source,'1',dict(sequence=1),stable=True)
        store.put([original])
        new=row('packet',source,'2',dict(sequence=2),stable=True)
        bad=row('packet',source,'1',dict(sequence=99),stable=True)
        with self.assertRaises(ValueError):store.put([new,bad])
        self.assertEqual(1,store.counts()['packet'])

    def test_large_source_is_chunked_and_restored_with_exact_hash(self):
        store=self.memory_store();data=bytes(range(256))*15626
        record=row('source.client',key('test'),'large',dict(sha256=hashlib.sha256(data).hexdigest()),data)
        batches(store,[record]);self.assertIsNone(store.fetch_blob(record['key']))
        self.assertEqual(data,store.read_blob(record['key']))
        counts=store.counts();self.assertEqual(0,batches(store,[record]));self.assertEqual(counts,store.counts())

    def test_missing_blob_chunk_fails_closed(self):
        store=self.memory_store();data=b'x'*4_000_001
        record=row('source.client',key('test'),'large',dict(bytes=len(data)),data);batches(store,[record])
        store.db.execute("DELETE FROM Evidence WHERE Kind='blob.chunk' AND EntityKey='1'")
        with self.assertRaisesRegex(ValueError,'chunk'):store.read_blob(record['key'])

    def block(self,kind,body):
        size=12+len(body);return struct.pack('<II',kind,size)+body+struct.pack('<I',size)

    def test_snapshot_trims_only_incomplete_last_block(self):
        first=self.block(0x0a0d0d0a,struct.pack('<IHHq',0x1a2b3c4d,1,0,-1))
        second=self.block(1,b'abcd')
        self.assertEqual((first,7),complete_pcapng_prefix(first+second[:7]))
        self.assertEqual((first+second,0),complete_pcapng_prefix(first+second))

    def test_corrupt_complete_block_is_not_silently_trimmed(self):
        first=self.block(0x0a0d0d0a,struct.pack('<IHHq',0x1a2b3c4d,1,0,-1))
        bad=bytearray(first);bad[-4:]=b'\x00'*4
        with self.assertRaisesRegex(ValueError,'trailer'):complete_pcapng_prefix(bad)

    def test_cleanup_refuses_parent_directory_and_preserves_neighbor(self):
        with tempfile.TemporaryDirectory() as folder:
            work=Path(folder);digest='a'*64;target=work/'snapshots'/digest;target.mkdir(parents=True)
            photo=work/'user.png';photo.write_bytes(b'keep')
            (target/'capture.pcapng').write_bytes(b'temporary')
            with self.assertRaises(ValueError):discard_committed_snapshot(work/'snapshots'/'capture.pcapng',work,digest)
            discard_committed_snapshot(target/'capture.pcapng',work,digest)
            self.assertEqual(b'keep',photo.read_bytes());self.assertFalse(target.exists())

    def test_actual_captured_level_message_has_no_extra_show_byte(self):
        event=semantic(BASE+0x23,bytes.fromhex('1016000008'),'s2c')
        self.assertEqual((5648,8),(event['char_id'],event['level']))
        self.assertNotIn('show',event)

    def test_capture_watermarks_are_per_connection_and_direction(self):
        store=self.memory_store();source=key('test')
        for stream,direction,seq in [(1,'s2c',2),(1,'s2c',3),(1,'c2s',8),(2,'s2c',1)]:
            store.put([row('packet',source,f'{stream}:{direction}:{seq}',dict(stream=stream,direction=direction,sequence=seq),stable=True)])
        self.assertEqual({(1,'s2c'):3,(1,'c2s'):8,(2,'s2c'):1},store.watermarks(source))

    def test_navigation_coordinates_are_observations_not_spawn_rules(self):
        data=struct.pack('<III',1,11015,1)+struct.pack('<BIIIIfff',2,9586,773,0,0,1.5,2.0,3.5)
        node=navigation(data)[0];self.assertEqual((11015,9586,773),(node['quest_id'],node['term_id'],node['monster_kind']))
        self.assertNotIn('spawn_probability',node)

    def test_resource_paths_cannot_escape_client_data(self):
        name='../secret'.encode('cp1250')
        data=struct.pack('<iii',1,1,len(name))+name+struct.pack('<IiI',1,0,0)
        with self.assertRaisesRegex(ValueError,'Unsafe'):resource_index(data,'TClientM.IDX')

    def test_loot_list_contains_actual_item_and_money_without_probability(self):
        item=bytes.fromhex('0972257225000100000000000000000000000000000000000000000000000000000000')
        body=struct.pack('<BBIIIIB',0,0,123,0,0,72,1)+item
        event=semantic_world(BASE+0x89,body,'s2c')
        self.assertEqual(9586,event['items'][0]['template_id']);self.assertEqual(72,event['copper'])
        self.assertNotIn('drop_probability',event)

    def test_monster_appearance_preserves_uninterpreted_tail(self):
        event=semantic_world(BASE+0x11,struct.pack('<IHBIIIIfff',1,773,3,578,578,80,80,1,2,3)+b'\0'*16,'s2c')
        self.assertEqual(578,event['max_hp']);self.assertEqual('inspected_prefix',event['semantic_scope'])
        self.assertEqual(16,event['uninterpreted_tail_bytes'])

    def test_quest_full_text_and_unknown_flag_are_preserved(self):
        def text(s):data=s.encode('cp1250');return struct.pack('<i',len(data))+data
        data=struct.pack('<i',0)+struct.pack('<IIBIBB',11014,11015,8,388,0,1)
        data+=b''.join(text(s) for s in ['Zrádné oko','dialogue','summary','','','','',''])
        data+=struct.pack('<Bi',4,1)+struct.pack('<BIB',2,9586,1)+text('Serunino oko')+text('source')
        data+=struct.pack('<iBIBBBII',1,6,328,0,1,0,0,0)
        _,quests=full_quests(data);quest=quests[0]
        self.assertEqual('Serunino oko',quest['terms'][0]['texts'][0])
        self.assertEqual(1,quest['unknown_flag']);self.assertNotIn('completion_cap',quest)


if __name__=='__main__':unittest.main()
