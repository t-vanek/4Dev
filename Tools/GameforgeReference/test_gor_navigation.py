"""Native navigation progression for the requested Gor missions, including changed group sizes."""
import json
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import unittest

from quest_navigation_repair import groups,repair,encoded,planned_nodes
from repair_egg_navigation import ROOT
from test_egg_navigation import HARNESS as EGG_HARNESS,function

BUNDLE=ROOT/'Gameforge/gor-training/quest-navigation-batch.json'
PREFIX='#include <string>\n'+EGG_HARNESS[:EGG_HARNESS.index('struct CTClientTerm')]
HARNESS=PREFIX+r'''
struct CTClientTerm{unsigned count=0,needed=1;int GetResult(){return count>=needed?1:0;}};
struct CTClientQuest{Quest* m_pTQUEST;std::map<DWORD,CTClientTerm> terms;CTClientTerm* FindTTerm(DWORD id,BYTE){return &terms[id];}};
struct Char{DWORD id;CTClientQuest* quest;CTClientQuest* FindTQuest(DWORD q){return q==id?quest:nullptr;}};
struct CTGaugePannel{DWORD m_dwCurQuestID=0;BYTE m_bShowPath=1,m_bAutoPath=1;};
struct CTClientGame{Char* m_pMainChar;CTGaugePannel gauge;LPTTERMPOS selected=nullptr;void* m_vTFRAME[1]={&gauge};BYTE ResetTPATHDATA();};
__LOADER__
__SELECTOR__
int main(int argc,char** argv){try{
 DWORD id=static_cast<DWORD>(std::stoul(argv[3]));Quest quest;CTChart::quests[id]=&quest;CTChart::InitTQUESTPOS(argv[1]);
 CTClientQuest progress{&quest};Char ch{id,&progress};CTClientGame game;game.m_pMainChar=&ch;game.gauge.m_dwCurQuestID=id;
 std::ifstream input(argv[2]);unsigned objectives,steps;input>>objectives;
 for(unsigned i=0;i<objectives;i++){unsigned type,term,count;input>>type>>term>>count;quest.m_vTTERM.push_back(new TTERM{static_cast<BYTE>(type),term});progress.terms[term].needed=count;}
 input>>steps;
 for(unsigned i=0;i<steps;i++){
  DWORD term,npc,map;float x,y,z;input>>term>>npc>>map>>x>>y>>z;
  if(!game.ResetTPATHDATA()||!game.selected)throw std::runtime_error("navigation target missing");auto n=game.selected;
  if(n->m_dwID!=term||n->m_dwNpcID!=npc||n->m_dwMapID!=map||n->m_fPosX!=x||n->m_fPosY!=y||n->m_fPosZ!=z)throw std::runtime_error("navigation target mismatch");
  if(n->m_bType==3&&n->m_dwMonKind!=term)throw std::runtime_error("hunt kind mismatch");
  if(term)progress.terms[term].count++;
 }
 return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
'''


def reset_scope(data,plans):
    for plan in plans:
        ident=plan['quest_id'];_,start,end,_=next(g for g in groups(data) if g[0]==ident)
        before,_=planned_nodes(plan)
        data=data[:start]+struct.pack('<II',ident,len(before))+b''.join(encoded(n) for n in before)+data[end:]
    return data


class GorNavigationTests(unittest.TestCase):
    def setUp(self):
        self.plans=json.loads(BUNDLE.read_text(encoding='utf8'))['plans']
        self.before=reset_scope((ROOT/'Game/TQNode.qpd').read_bytes(),self.plans)

    def test_all_repairs_preserve_other_groups_and_egg_fix_and_are_idempotent(self):
        data=self.before
        for p in self.plans:
            data,count=repair(data,p);self.assertGreater(count,0);self.assertEqual((data,0),repair(data,p))
        ids={p['quest_id'] for p in self.plans}
        self.assertEqual([g[3] for g in groups(self.before) if g[0] not in ids],[g[3] for g in groups(data) if g[0] not in ids])
        self.assertEqual(next(g[3] for g in groups(self.before) if g[0]==7906),next(g[3] for g in groups(data) if g[0]==7906))

    def test_target_drift_rejects(self):
        for p in self.plans:
            bad=bytearray(self.before);_,start,_,_=next(g for g in groups(bad) if g[0]==p['quest_id']);bad[start+8+17]=1
            with self.assertRaisesRegex(ValueError,'baseline differs'):repair(bad,p)

    @unittest.skipUnless(shutil.which('cl'),'Run in a Visual Studio developer shell')
    def test_native_selector_tracks_every_objective_count_then_the_correct_turn_in(self):
        chart=(ROOT/'Lib/Own/TChart/TChart/TChart.cpp').read_text(encoding='utf-8-sig');loader=function(chart,'void CTChart::InitTQUESTPOS(')
        game=(ROOT/'Client/TClient/TClientGame.cpp').read_text(encoding='utf-8-sig');start=game.index('BYTE CTClientGame::ResetTPATHDATA()');end=game.index('FLOAT vDIST[',start)
        selector=game[start:end]+'selected=pTNODEPATH;return selected!=nullptr;}'
        data=self.before
        for p in self.plans:data,_=repair(data,p)
        with tempfile.TemporaryDirectory() as directory:
            work=Path(directory);cpp=work/'gor.cpp';exe=work/'gor.exe';cpp.write_text(HARNESS.replace('__LOADER__',loader).replace('__SELECTOR__',selector),encoding='utf8')
            build=subprocess.run(['cl','/nologo','/EHsc','/std:c++17',str(cpp),'/Fe:'+str(exe)],cwd=work,capture_output=True,text=True,errors='replace');self.assertEqual(0,build.returncode,build.stdout+build.stderr)
            for p in self.plans:
                expected=[]
                for term in p['terms']:
                    if term['bTermType'] not in (2,3):continue
                    node=next(n for n in p['after_nodes'] if n['type']==term['bTermType'] and n['term_id']==term['dwTermID'])
                    expected.extend([node]*term['bCount'])
                expected.append(next(n for n in p['after_nodes'] if n['type']==20))
                positions=work/'expected.txt';positions.write_text(str(len(p['terms']))+'\n'+''.join(f"{t['bTermType']} {t['dwTermID']} {t['bCount']}\n" for t in p['terms'])+str(len(expected))+'\n'+''.join(f"{n['term_id']} {n['npc_id']} {n['map_id']} {n['x']:.9g} {n['y']:.9g} {n['z']:.9g}\n" for n in expected),encoding='ascii')
                for old,raw in [(True,self.before),(False,data)]:
                    target=work/'TQNode.qpd';target.write_bytes(raw);result=subprocess.run([str(exe),str(target),str(positions),str(p['quest_id'])],capture_output=True,text=True)
                    if old:self.assertNotEqual(0,result.returncode)
                    else:self.assertEqual(0,result.returncode,str(p['quest_id'])+': '+result.stderr)
                print(f"{p['quest_id']}: {len(expected)-1} objective advances then NPC {p['completion']['dwTriggerID']}")


if __name__=='__main__':unittest.main()
