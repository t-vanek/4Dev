"""Actual native chart loader/path selector plus byte-preservation checks for egg navigation."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from reconstruction_client import navigation
from repair_egg_navigation import ROOT,PLAN,repair,encoded

HARNESS=r'''
#include <cstdint>
#include <fstream>
#include <vector>
#include <map>
#include <stdexcept>
#include <iostream>
using DWORD=uint32_t;using BYTE=uint8_t;using WORD=uint16_t;using FLOAT=float;using CString=const char*;using INT=int;
#define LPCSTR(x) (x)
constexpr BYTE FALSE=0,TRUE=1,QTT_TIMER=6,QTT_TCOMP_POS=20,TTERMRESULT_INCOMPLETE=0;
constexpr int TFRAME_GAUGE=0;
struct CFile{static constexpr int modeRead=1,typeBinary=2;std::ifstream input;CFile(const char* p,int):input(p,std::ios::binary){if(!input)throw std::runtime_error("Missing chart");}};
struct CArchive{static constexpr int load=0;CFile& file;CArchive(CFile* f,int):file(*f){}template<class T>CArchive& operator>>(T& value){file.input.read(reinterpret_cast<char*>(&value),sizeof(value));if(!file.input)throw std::runtime_error("Truncated chart");return *this;}};
struct TTERMPOS{BYTE m_bType;DWORD m_dwID,m_dwMonKind,m_dwNpcID,m_dwMapID;FLOAT m_fPosX,m_fPosY,m_fPosZ;};using LPTTERMPOS=TTERMPOS*;
struct TTERM{BYTE m_bType=2;DWORD m_dwID;};using LPTTERM=TTERM*;
struct Quest{std::vector<LPTTERMPOS> m_vTTERMPOS;std::vector<LPTTERM> m_vTTERM;};using LPTQUEST=Quest*;
struct CTChart{static std::map<DWORD,Quest*> quests;static Quest* FindTQUEST(DWORD id){auto i=quests.find(id);return i==quests.end()?nullptr:i->second;}static void InitTQUESTPOS(CString path);};std::map<DWORD,Quest*> CTChart::quests;
struct CTClientTerm{bool done=false;int GetResult(){return done?1:0;}};
struct CTClientQuest{Quest* m_pTQUEST;std::map<DWORD,CTClientTerm> terms;CTClientTerm* FindTTerm(DWORD id,BYTE){return &terms[id];}};
struct Char{CTClientQuest* quest;CTClientQuest* FindTQuest(DWORD id){return id==7906?quest:nullptr;}};
struct CTGaugePannel{DWORD m_dwCurQuestID=7906;BYTE m_bShowPath=1,m_bAutoPath=1;};
struct CTClientGame{Char* m_pMainChar;CTGaugePannel gauge;LPTTERMPOS selected=nullptr;void* m_vTFRAME[1]={&gauge};BYTE ResetTPATHDATA();};
__LOADER__
__SELECTOR__
int main(int argc,char** argv){try{
 Quest quest;CTChart::quests[7906]=&quest;CTChart::InitTQUESTPOS(argv[1]);
 CTClientQuest progress{&quest};Char ch{&progress};CTClientGame game;game.m_pMainChar=&ch;
 for(DWORD item=9644;item<=9648;item++)quest.m_vTTERM.push_back(new TTERM{2,item});
 std::ifstream positions(argv[2]);
 for(int step=0;step<6;step++){
  DWORD id,npc,map;float x,y,z;positions>>id>>npc>>map>>x>>y>>z;
  if(!game.ResetTPATHDATA()||!game.selected)throw std::runtime_error("No navigation target");auto node=game.selected;
  if(node->m_dwID!=id||node->m_dwNpcID!=npc||node->m_dwMapID!=map||node->m_fPosX!=x||node->m_fPosY!=y||node->m_fPosZ!=z)throw std::runtime_error("Egg path points away from evidenced nest or turn-in NPC");
  if(step<5)progress.terms[id].done=true;
 }
 std::cout<<"Actual native loader and path selector visit five nests, then Ennahi after all items are complete\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
'''


def function(source,signature):
    start=source.index(signature);opening=source.index('{',start);level=1;end=opening+1
    while level:
        level+=(source[end]=='{')-(source[end]=='}');end+=1
    return source[start:end]


class EggNavigationTests(unittest.TestCase):
    def setUp(self):
        self.plan=json.loads(PLAN.read_text(encoding='utf8'));self.data=(ROOT/'Game/TQNode.qpd').read_bytes()
        # Reconstruct the zero-node input even after the working client has been repaired.
        raw=bytearray(self.data)
        for n in navigation(raw):
            if n['quest_id']==7906:
                change=next(c for c in self.plan['changes'] if c['before']['ordinal']==n['ordinal'])
                raw[n['offset']:n['offset']+29]=encoded(change['before'])
        self.before=bytes(raw)

    def test_repair_is_idempotent_and_preserves_every_unrelated_byte(self):
        after,count=repair(self.before,self.plan);self.assertEqual(7,count);self.assertEqual((after,0),repair(after,self.plan))
        allowed=set()
        for n in navigation(self.before):
            if n['quest_id']==7906:allowed.update(range(n['offset'],n['offset']+29))
        self.assertTrue(all(a==b for i,(a,b) in enumerate(zip(self.before,after)) if i not in allowed));self.assertEqual(len(self.before),len(after))

    def test_drift_rejects_before_writing(self):
        raw=bytearray(self.before);node=next(n for n in navigation(raw) if n['quest_id']==7906 and n['ordinal']==1);raw[node['offset']+17]=1
        with self.assertRaisesRegex(ValueError,'baseline differs'):repair(raw,self.plan)

    @unittest.skipUnless(shutil.which('cl'),'Run in a Visual Studio developer shell')
    def test_actual_loader_and_path_selector_progress_from_each_egg_to_turn_in(self):
        chart=(ROOT/'Lib/Own/TChart/TChart/TChart.cpp').read_text(encoding='utf-8-sig')
        loader=function(chart,'void CTChart::InitTQUESTPOS(')
        game=(ROOT/'Client/TClient/TClientGame.cpp').read_text(encoding='utf-8-sig');start=game.index('BYTE CTClientGame::ResetTPATHDATA()');end=game.index('FLOAT vDIST[',start)
        selector=game[start:end]+'selected=pTNODEPATH;return selected!=nullptr;}'
        after,_=repair(self.before,self.plan)
        with tempfile.TemporaryDirectory() as directory:
            work=Path(directory);cpp=work/'egg.cpp';exe=work/'egg.exe';cpp.write_text(HARNESS.replace('__LOADER__',loader).replace('__SELECTOR__',selector),encoding='utf8')
            build=subprocess.run(['cl','/nologo','/EHsc','/std:c++17',str(cpp),'/Fe:'+str(exe)],cwd=work,capture_output=True,text=True,errors='replace');self.assertEqual(0,build.returncode,build.stdout+build.stderr)
            positions=work/'positions.txt';ordered=[c['after'] for c in self.plan['changes'] if c['after']['type']==2]+[c['after'] for c in self.plan['changes'] if c['after']['type']==20]
            positions.write_text(''.join(f"{n['term_id']} {n['npc_id']} {n['map_id']} {n['x']:.9g} {n['y']:.9g} {n['z']:.9g}\n" for n in ordered),encoding='ascii')
            for old,data in [(True,self.before),(False,after)]:
                chartfile=work/'TQNode.qpd';chartfile.write_bytes(data);result=subprocess.run([str(exe),str(chartfile),str(positions)],capture_output=True,text=True)
                if old:self.assertNotEqual(0,result.returncode);self.assertIn('points away',result.stderr)
                else:self.assertEqual(0,result.returncode,result.stderr);print(result.stdout.strip())


if __name__=='__main__':unittest.main()
