"""Compile the actual entry latch and scene dispatch; no renderer or game automation."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[2]
HARNESS=r'''#include <stdexcept>
#include <iostream>
using BYTE=unsigned char;using WORD=unsigned short;
constexpr BYTE FALSE=0,TRUE=1,TCONTRY_D=0,TCONTRY_C=1,TCONTRY_B=2,TCONTRY_PEACE=4;
constexpr WORD TUTORIAL_MAPID=2010,TSTART_D_CHAR_DIR=1,TSTART_C_CHAR_DIR=2,TSTART_B_CHAR_DIR=3,TSTART_P_CHAR_DIR=4;
constexpr int TSCENE_SCRIPT_DEFUGEL_ENTER=1,TSCENE_SCRIPT_CRAXION_ENTER=2,TSCENE_SCRIPT_BROIR_ENTER=3,TSCENE_SCRIPT_PEACE_ENTER=4;
#define INT int
struct Char{BYTE m_bContryID=4;WORD m_wDIR=0;int m_nDIR=0;};
struct Map{WORD m_wMapID=0;};struct Scene{int calls=0,last=0;void StartScene(void*,void*,int n){calls++;last=n;}};
struct CTClientGame{BYTE m_bFirstConnect=1;Char storage;Char* m_pMainChar=&storage;Map m_vMAP;Scene m_vTSCENE_PLAYER;void* m_pDevice=nullptr;void* m_pRES=nullptr;void DoEventFirstConnect();void InitialEntry();};
__HELPER__
__DISPATCH__
void CTClientGame::InitialEntry(){__ENTRY__}
void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
int main(){try{
 CTClientGame gor;gor.InitialEntry();require(!gor.m_bFirstConnect,"Playable Gor retained pending first-entry scene");
 for(int i=0;i<3;i++)gor.DoEventFirstConnect();require(gor.m_vTSCENE_PLAYER.calls==0,"Ordinary Gor portal started the Biseira camera");
 for(BYTE flag: {BYTE(0),BYTE(1),BYTE(2)}){CTClientGame tutorial;tutorial.m_vMAP.m_wMapID=TUTORIAL_MAPID;tutorial.m_bFirstConnect=flag;tutorial.InitialEntry();require(tutorial.m_bFirstConnect==flag,"Tutorial flag was consumed");}
 CTClientGame leaveTutorial;leaveTutorial.m_vMAP.m_wMapID=TUTORIAL_MAPID;leaveTutorial.InitialEntry();leaveTutorial.m_vMAP.m_wMapID=0;leaveTutorial.DoEventFirstConnect();require(leaveTutorial.m_vTSCENE_PLAYER.calls==1,"Actual tutorial exit intro was suppressed");
 gor.storage.m_bContryID=TCONTRY_D;gor.m_bFirstConnect=TRUE;gor.DoEventFirstConnect();require(gor.m_vTSCENE_PLAYER.calls==1&&gor.m_vTSCENE_PLAYER.last==TSCENE_SCRIPT_DEFUGEL_ENTER,"Country change intro regressed");
 gor.DoEventFirstConnect();require(gor.m_vTSCENE_PLAYER.calls==1,"Country intro repeated");
 for(BYTE country: {BYTE(0),BYTE(1),BYTE(2)}){CTClientGame nation;nation.storage.m_bContryID=country;nation.InitialEntry();require(nation.m_bFirstConnect==1,"Other country startup changed");}
 CTClientGame otherMap;otherMap.m_vMAP.m_wMapID=7;otherMap.InitialEntry();require(otherMap.m_bFirstConnect==1,"Unrelated map entry changed");
 CTClientGame unknownFlag;unknownFlag.m_bFirstConnect=2;unknownFlag.InitialEntry();require(unknownFlag.m_bFirstConnect==2,"Other start-act value changed");
 std::cout<<"Gor portals, tutorial entry/exit, country-change rearming, other maps/countries and start-act values validated\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
'''

@unittest.skipUnless(shutil.which('cl'),'Run in a Visual Studio developer shell')
class GorIntroTests(unittest.TestCase):
    def test_actual_initial_entry_consumes_only_stale_gor_scene(self):
        source=(ROOT/'Client/TClient/CSHandler.cpp').read_text(encoding='utf-8-sig')
        start=source.index('static BYTE IntroFlagForInitialEntry(');end=source.index('bool _SortMATCH',start)
        helper=source[start:end]
        start=source.index('m_bFirstConnect = IntroFlagForInitialEntry(',source.index('int CTClientGame::OnCS_CHARINFO_ACK'))
        entry=source[start:source.index(';',start)+1]
        self.assertLess(start,source.index('if(m_bFirstConnect)',start))
        game=(ROOT/'Client/TClient/TClientGame.cpp').read_text(encoding='utf-8-sig')
        start=game.index('void CTClientGame::DoEventFirstConnect()');end=game.index('void CTClientGame::ResetDisplayChallengeInfo()',start)
        dispatch=game[start:end]
        with tempfile.TemporaryDirectory() as folder:
            work=Path(folder)
            for legacy in [True,False]:
                cpp=work/'intro.cpp';exe=work/'intro.exe'
                cpp.write_text(HARNESS.replace('__HELPER__',helper).replace('__DISPATCH__',dispatch).replace('__ENTRY__','' if legacy else entry),encoding='utf8')
                build=subprocess.run(['cl','/nologo','/EHsc','/std:c++17',str(cpp),'/Fe:'+str(exe)],cwd=work,capture_output=True,text=True,errors='replace')
                self.assertEqual(0,build.returncode,build.stdout+build.stderr)
                result=subprocess.run([str(exe)],capture_output=True,text=True)
                if legacy:
                    self.assertNotEqual(0,result.returncode);self.assertIn('retained pending',result.stderr)
                else:self.assertEqual(0,result.returncode,result.stderr);print(result.stdout.strip())

if __name__=='__main__':unittest.main()
