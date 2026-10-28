"""Compile the client's actual quest click handler with a recording session and small UI fixtures.

Run from a Visual Studio developer shell: python Tools/GameforgeReference/test_quest_execution.py
The test never starts the game or writes gameplay data.
"""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile


FIXTURE = r'''
#include <cstdint>
#include <iostream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
using BYTE = uint8_t;
using DWORD = uint32_t;
constexpr int QT_MISSION=8, QT_COMPLETE=7, QTT_COMPQUEST=1, OT_NPC=2;
constexpr int TFRAME_NPCDIALOG=0, TFRAME_HELP=1, TERR_NONE=0, THELPLINK_TRIGGER_ACCEPT=1;
struct Term { int m_bType; DWORD m_dwID; };
struct Quest { DWORD m_dwID; int m_bType; DWORD m_dwParentID=0; std::vector<Term*> m_vTTERM; };
using LPTQUEST = Quest*;
struct Reward { BYTE m_bType; DWORD m_dwID; };
using LPTREWARD = Reward*;
struct CQuest { bool m_Completed=false; };
struct Button { std::string m_strText; };
struct CTNPCTalkDlg {
    LPTQUEST current=nullptr;
    CQuest selection;
    Button button;
    Button* m_pACCEPT=&button;
    LPTREWARD m_pTSELREWARD=nullptr;
    LPTQUEST GetCurTQuest() { return current; }
    CQuest* GetCurSelTQuest() { return &selection; }
};
struct CTChart {
    static inline std::map<DWORD,LPTQUEST> m_mapTQUESTTEMP;
    static LPTQUEST FindTMISSION(LPTQUEST q) {
        for(auto t:q->m_vTTERM) if(t->m_bType==QTT_COMPQUEST) return m_mapTQUESTTEMP.at(t->m_dwID);
        return nullptr;
    }
};
struct CTClientObjBase { DWORD m_dwID=20001; int m_bType=OT_NPC; std::map<DWORD,LPTQUEST> m_mapTQUESTTEMP; };
using MAPOBJECT = std::map<DWORD, CTClientObjBase*>;
struct Session {
    struct Request { DWORD quest; BYTE rewardType; DWORD rewardId; };
    std::vector<Request> requests;
    void SendCS_QUESTEXEC_REQ(DWORD q, BYTE t, DWORD r) { requests.push_back({q,t,r}); }
};
struct CTHelpFrame { void OnQuestEvent(int,DWORD) {} };
struct CTClientGame {
    Session* m_pSESSION=nullptr;
    CTClientObjBase* m_pTARGET=nullptr;
    void* m_vTFRAME[2]{};
    struct { MAPOBJECT m_mapTNpc; std::vector<DWORD> m_vNEWQUESTOBJ; } m_vMAP;
    void CheckQuestOBJ() {}
    int OnGM_QUEST_ACCEPT();
};
'''

CASES = r'''
int main() {
    Quest mission{7981,QT_MISSION}, complete{7983,QT_COMPLETE}, otherNpc{7982,QT_COMPLETE}, chained{9000,QT_COMPLETE};
    Term missionTerm{QTT_COMPQUEST,7981}, chainedTerm{QTT_COMPQUEST,7983};
    complete.m_vTTERM={&missionTerm}; otherNpc.m_vTTERM={&missionTerm}; chained.m_vTTERM={&chainedTerm};
    CTChart::m_mapTQUESTTEMP={{7981,&mission},{7982,&otherNpc},{7983,&complete},{9000,&chained}};
    CTNPCTalkDlg dialog; CTHelpFrame help; Session session; CTClientObjBase npc;
    CTClientGame game; game.m_pSESSION=&session; game.m_pTARGET=&npc;
    game.m_vTFRAME[TFRAME_NPCDIALOG]=&dialog; game.m_vTFRAME[TFRAME_HELP]=&help;
    dialog.current=&mission;
    auto check=[&](DWORD expected,const std::string& name) {
        session.requests.clear(); game.OnGM_QUEST_ACCEPT();
        if(expected ? session.requests.size()!=1 || session.requests[0].quest!=expected : !session.requests.empty())
            throw std::runtime_error(name+": unexpected quest request");
        std::cout << "PASS " << name << '\n';
    };
    for(const auto& label:{"Accept","Přijmout"}) {
        dialog.button.m_strText=label; dialog.selection.m_Completed=false;
        npc.m_mapTQUESTTEMP={{7981,&mission}}; check(7981,std::string("accept ")+label);
    }
    dialog.selection.m_Completed=true; npc.m_mapTQUESTTEMP={{7983,&complete}};
    for(const auto& label:{"Dokončit","Complete","Abschließen"}) {
        dialog.button.m_strText=label; check(7983,std::string("complete ")+label);
    }
    Reward reward{1,1234}; dialog.m_pTSELREWARD=&reward; check(7983,"selected reward");
    if(session.requests[0].rewardType!=1 || session.requests[0].rewardId!=1234)
        throw std::runtime_error("Selected reward was lost");
    npc.m_mapTQUESTTEMP.clear(); check(0,"no completion offered by current NPC");
    npc.m_mapTQUESTTEMP={{9000,&chained}}; check(0,"unrelated completion");
    game.m_pTARGET=nullptr; check(0,"target missing"); game.m_pTARGET=&npc;
    npc.m_bType=0; check(0,"target is not NPC"); npc.m_bType=OT_NPC;
    dialog.current=nullptr; check(0,"selection missing"); dialog.current=&mission;
    game.m_pSESSION=nullptr; check(0,"session missing");
    std::cout << "12 quest execution checks passed; no gameplay data written.\n";
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=Path(__file__).resolve().parents[2] / 'Client/TClient/TClientGM.cpp')
    args = parser.parse_args()
    source = args.source.read_text(encoding='utf-8-sig')
    handler = 'int CTClientGame::OnGM_QUEST_ACCEPT()' + source.split('int CTClientGame::OnGM_QUEST_ACCEPT()', 1)[1].split('int CTClientGame::OnGM_QUEST_GIVEUP()', 1)[0]
    compiler = shutil.which('cl')
    if compiler is None:
        raise SystemExit('Run from a Visual Studio developer shell (cl and its standard headers are required).')
    with tempfile.TemporaryDirectory(prefix='quest-execution-') as temporary:
        folder = Path(temporary)
        cpp = folder / 'quest_execution.cpp'
        cpp.write_text(FIXTURE + handler + CASES, encoding='utf-8')
        subprocess.run([compiler, '/nologo', '/std:c++17', '/EHsc', '/utf-8', str(cpp), '/Fe:quest_execution.exe'], cwd=folder, check=True)
        subprocess.run([str(folder / 'quest_execution.exe')], cwd=folder, check=True)


if __name__ == '__main__':
    main()
