"""Opt-in real SQL Server roundtrips; all test rows are isolated and cleaned up."""
import json
import os
import unittest
import uuid

from reconstruction_store import SqlServerStore,batches,key,row


@unittest.skipUnless(os.environ.get('FOURDEV_REFERENCE_SQL_TEST')=='1','Set FOURDEV_REFERENCE_SQL_TEST=1 for the existing SQL container')
class ReferenceSqlRoundtrips(unittest.TestCase):
    def setUp(self):
        self.store=SqlServerStore();self.source=key('collector-sql-test',str(uuid.uuid4()));self.parents=[]

    def tearDown(self):
        # Only generated test rows/chunks in the dedicated reference database are touched.
        for parent in self.parents:
            self.store.execute("DELETE dbo.Evidence WHERE Kind=N'blob.chunk' AND SourceKey='"+parent+"';")
        self.store.execute("DELETE dbo.Evidence WHERE SourceKey='"+self.source+"' AND Kind=N'test.reference';")

    def test_unicode_long_json_literal_escapes_and_reconnect(self):
        value=dict(title="Příliš žluťoučký 'kůň'",text=('$(literal) \\u1234\n漢字🙂 '*500))
        record=row('test.reference',self.source,'unicode',value,b'\x00\xff\x01')
        self.assertEqual(1,self.store.put([record]));self.assertEqual(0,self.store.put([record]))
        fresh=SqlServerStore(initialize=False)
        self.assertEqual(value,json.loads(fresh.get_record(record['key'])['payload']))
        self.assertEqual(b'\x00\xff\x01',fresh.read_blob(record['key']))

    def test_large_binary_chunk_roundtrip_and_idempotence(self):
        data=bytes(range(256))*15626
        record=row('test.reference',self.source,'binary',dict(bytes=len(data)),data);self.parents.append(record['key'])
        batches(self.store,[record])
        self.assertIsNone(self.store.fetch_blob(record['key']))
        self.assertEqual(data,self.store.read_blob(record['key']))
        self.assertEqual(0,batches(self.store,[record]))

    def test_conflict_does_not_partially_commit_batch(self):
        original=row('test.reference',self.source,'one',dict(value=1),stable=True);self.store.put([original])
        candidate=row('test.reference',self.source,'two',dict(value=2),stable=True)
        conflict=row('test.reference',self.source,'one',dict(value=99),stable=True)
        with self.assertRaises(RuntimeError):self.store.put([candidate,conflict])
        self.assertIsNone(self.store.get_record(candidate['key']))
        self.assertEqual(dict(value=1),json.loads(self.store.get_record(original['key'])['payload']))


if __name__=='__main__':unittest.main()
