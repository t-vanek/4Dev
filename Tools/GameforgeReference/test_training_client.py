import json
from pathlib import Path
import unittest

from client_records import quests
from repair_training_client import ROOT,repaired_bytes,validate


class TrainingClientTests(unittest.TestCase):
    def test_client_rejects_unknown_advertised_alias_and_repair_preserves_existing_records(self):
        plan=json.loads((ROOT/'Gameforge/gor-training/plan.json').read_text(encoding='utf8'))
        data=(ROOT/'Game/TQuest.mpq').read_bytes();classes,records,_=quests(data,False)
        # Recreate the actual missing-alias client state even after the repair is applied locally.
        from client_records import serialize
        baseline=serialize(classes,False)+b''.join(r.raw for r in records if str(r.key) not in plan['routing_aliases'])
        with self.assertRaisesRegex(ValueError,'cannot resolve advertised'):validate(baseline,plan)
        fixed,count=repaired_bytes(baseline,plan);self.assertEqual(3,count);self.assertTrue(fixed.startswith(baseline))
        index=validate(fixed,plan)
        self.assertEqual([9629,9630,9631],[index[i].extra['terms'][0][1] for i in (40007930,40007931,40007932)])
        self.assertEqual((fixed,0),repaired_bytes(fixed,plan))

    def test_installed_native_client_resolves_all_three_live_routing_actions(self):
        plan=json.loads((ROOT/'Gameforge/gor-training/plan.json').read_text(encoding='utf8'))
        validate((ROOT/'Game/TQuest.mpq').read_bytes(),plan)


if __name__=='__main__':unittest.main()
