"""Protect full Gor scope, conservative repairs and native completed-quest navigation."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from audit_gor_runtime import local_scope,completion_index,index
from quest_navigation_repair import repair,groups
from test_gor_navigation import HARNESS,reset_scope
from test_egg_navigation import ROOT,function

AUDIT=ROOT/'Gameforge/gor-full-audit/complete-before.json'


class GorRuntimeAuditTests(unittest.TestCase):
    def test_inherited_active_quests_are_in_scope_even_if_pinned_scope_omits_them(self):
        npc=dict(wID=10,bCountryID=3,wMapID=0,fPosX=3600,fPosZ=900)
        _,scope=local_scope({1:dict(dwQuestID=1,bType=8,bTriggerType=3,dwTriggerID=10)}, {},[npc],{99})
        self.assertEqual({1,99},set(scope))

    def test_other_region_neutral_npcs_and_inactive_actions_do_not_expand_scope(self):
        npc=dict(wID=10,bCountryID=3,wMapID=0,fPosX=5000,fPosZ=900)
        headers={1:dict(dwQuestID=1,bType=8,bTriggerType=3,dwTriggerID=10),2:dict(dwQuestID=2,bType=7,bTriggerType=3,dwTriggerID=10)}
        self.assertFalse(local_scope(headers,{},[npc],set())[1])

    def test_completion_index_uses_explicit_target_not_unrelated_parent(self):
        headers={2:dict(dwQuestID=2,bType=7,dwParentID=88)}
        terms=index([dict(dwQuestID=2,bTermType=1,dwTermID=1)],'dwQuestID')
        self.assertEqual([2],[x['dwQuestID'] for x in completion_index(headers,terms)[1]])
        self.assertEqual([],completion_index(headers,terms)[88])

    def test_known_unresolved_identity_and_zero_npc_are_not_repaired(self):
        report=json.loads(AUDIT.read_text(encoding='utf8'));plans={p['quest_id']:p for p in report['plans']}
        self.assertTrue({3199,8906,8907,7837}.isdisjoint(plans))
        darni=next(r for r in report['rows'] if r['id']==7837)
        self.assertIn('completion_npc_zero_position',darni['local_issues'])

    def test_batch_preserves_all_objective_and_unrelated_records_and_rejects_drift(self):
        plans=json.loads(AUDIT.read_text(encoding='utf8'))['plans'];data=reset_scope((ROOT/'Game/TQNode.qpd').read_bytes(),plans);before=data
        from reconstruction_client import navigation
        for p in plans:
            data,count=repair(data,p);self.assertGreater(count,0);self.assertEqual((data,0),repair(data,p))
            bad=bytearray(before);node=next(n for n in navigation(bad) if n['quest_id']==p['quest_id']);bad[node['offset']+17]^=1
            with self.assertRaises(ValueError):repair(bad,p)
        ids={p['quest_id'] for p in plans}
        self.assertEqual([g[3] for g in groups(before) if g[0] not in ids],[g[3] for g in groups(data) if g[0] not in ids])
        self.assertEqual([n for n in navigation(before) if n['quest_id'] in ids and n['type'] not in (19,20)],[n for n in navigation(data) if n['quest_id'] in ids and n['type'] not in (19,20)])

    @unittest.skipUnless(shutil.which('cl'),'Run in a Visual Studio developer shell')
    def test_real_native_loader_and_selector_for_all_forty_completed_missions(self):
        report=json.loads(AUDIT.read_text(encoding='utf8'));plans=report['plans'];rows={r['id']:r for r in report['rows']}
        before=reset_scope((ROOT/'Game/TQNode.qpd').read_bytes(),plans);after=before
        for p in plans:after,_=repair(after,p)
        chart=(ROOT/'Lib/Own/TChart/TChart/TChart.cpp').read_text(encoding='utf-8-sig');loader=function(chart,'void CTChart::InitTQUESTPOS(')
        game=(ROOT/'Client/TClient/TClientGame.cpp').read_text(encoding='utf-8-sig');start=game.index('BYTE CTClientGame::ResetTPATHDATA()');end=game.index('FLOAT vDIST[',start)
        selector=game[start:end]+'selected=pTNODEPATH;return selected!=nullptr;}'
        with tempfile.TemporaryDirectory() as directory:
            work=Path(directory);cpp=work/'gor_audit.cpp';exe=work/'gor_audit.exe';cpp.write_text(HARNESS.replace('__LOADER__',loader).replace('__SELECTOR__',selector),encoding='utf8')
            build=subprocess.run(['cl','/nologo','/EHsc','/std:c++17',str(cpp),'/Fe:'+str(exe)],cwd=work,capture_output=True,text=True,errors='replace');self.assertEqual(0,build.returncode,build.stdout+build.stderr)
            for p in plans:
                terms=rows[p['quest_id']]['terms'];n=next(n for n in p['after_nodes'] if n['type']==20)
                expected=work/'expected.txt';expected.write_text(str(len(terms))+'\n'+''.join(f"{t['bTermType']} {t['dwTermID']} 0\n" for t in terms)+f"1\n{n['term_id']} {n['npc_id']} {n['map_id']} {n['x']:.9g} {n['y']:.9g} {n['z']:.9g}\n",encoding='ascii')
                for old,data in [(True,before),(False,after)]:
                    target=work/'TQNode.qpd';target.write_bytes(data);run=subprocess.run([str(exe),str(target),str(expected),str(p['quest_id'])],capture_output=True,text=True)
                    if old:self.assertNotEqual(0,run.returncode)
                    else:self.assertEqual(0,run.returncode,str(p['quest_id'])+': '+run.stderr)
        print('40 completed missions: original incorrect target reproduced; corrected NPC selected by actual client functions')


if __name__=='__main__':unittest.main()
