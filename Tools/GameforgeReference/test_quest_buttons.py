"""Exercise the real quest button handlers without starting the graphical client.

Run from a Visual Studio developer shell. The fixtures record actions and emulate hit testing;
the click, action, next-quest, label and selection-reset implementations come from the client.
"""
import re
import shutil
import subprocess
import tempfile
from pathlib import Path


FIXTURE = r'''
#include <cstdint>
#include <iostream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
using BYTE=uint8_t; using DWORD=uint32_t; using UINT=unsigned; using BOOL=bool; using LPCTSTR=const char*;
constexpr bool TRUE=true,FALSE=false;
constexpr int TFRAME_QUEST_NEWUI=1,TFRAME_NPCDIALOG=2,TFRAME_GAUGE=3;
struct CString:std::string { using std::string::string; using std::string::operator=; void Empty(){clear();} };
struct CPoint { int x; };
struct TButton {
    int id=0; bool visible=true,enabled=true; CString m_strText;
    bool HitTest(CPoint p){return visible && p.x==id;}
    bool HitRect(CPoint p){return HitTest(p);}
    bool IsVisible(){return visible;}
    bool IsEnable(){return enabled;}
    void EnableComponent(bool b){enabled=b;}
};
struct Quest { DWORD m_dwID; }; using LPTQUEST=Quest*;
struct CTClientQuest { LPTQUEST m_pTQUEST; };
struct CQuest {
    bool m_bSelected=false,m_Accepted=false;
    uintptr_t m_dwQuest=0;
    TButton checkbox,row,name;
    TButton* m_pCheckBox=&checkbox; TButton* m_pQuestBtn=&row; TButton* m_pQuestName=&name;
};
struct List {
    TButton category; TButton* m_pQuestCat=&category; bool m_Opened=true;
    std::map<DWORD,std::vector<CQuest>> m_mapQuest;
    int GetSize(){return (int)m_mapQuest.size();}
};
struct CTClientUIBase { int baseClicks=0; void OnLButtonUp(UINT,CPoint){++baseClicks;} };
struct Host { std::map<DWORD,CTClientQuest*> quests; CTClientQuest* FindTQuest(DWORD id){return quests.count(id)?quests[id]:nullptr;} };
struct CTGaugePannel {
    DWORD m_dwCurQuestID=0; bool m_bAutoPath=false,m_bShowPath=false; int updates=0;
    void UpdateQuestBUTTON(){++updates;}
};
struct CTClientGame {
    static inline CTClientGame* instance=nullptr;
    static CTClientGame* GetInstance(){return instance;}
    void* frames[4]{};
    int accepts=0,giveups=0,closed=0;
    int OnGM_QUEST_ACCEPT(){return ++accepts;}
    int OnGM_QUEST_GIVEUP(){return ++giveups;}
    void* GetFrame(int i){return frames[i];}
    void DisableUI(int frame){closed=frame;}
};
struct CTChart {
    static LPCTSTR Localize(LPCTSTR text){
        static const std::map<std::string,LPCTSTR> dictionary={
        @DICTIONARY@
        };
        auto found=dictionary.find(text);return found==dictionary.end()?text:found->second;
    }
};
struct CQuestNewDlg:CTClientUIBase {
    @ENUM@
    List m_QuestList[3];
    TButton accept,refuse,text;
    TButton* m_pACCEPT=&accept; TButton* m_pREFUSE=&refuse; TButton* m_pQuestText=&text;
    CQuest* m_Quest=nullptr; LPTQUEST m_pTQUEST=nullptr; void* m_pTSELREWARD=nullptr;
    Host* m_pHost=nullptr;
    int m_nDesiredLeftScrollY=0;
    ButtonAction m_AcceptAction=ButtonAction::None,m_RefuseAction=ButtonAction::None;
    struct {void Reset(){}} m_RightSide;
    void CheckShowRight(const CQuest&){}
    void ResetTQUEST(LPTQUEST q,CQuest* selected){m_pTQUEST=q;m_Quest=selected;}
    void OnLButtonUp(UINT,CPoint);
    void SetQuestButtons(ButtonAction,ButtonAction);
    void ExecuteButtonAction(ButtonAction);
    void SelectNextQuest();
    void ClearQuestSelection();
};
'''

CASES = r'''
int run() {
    using Action=CQuestNewDlg::ButtonAction;
    CTClientGame game; CTClientGame::instance=&game;
    CQuestNewDlg npc,journal; Host host; CTGaugePannel panel;
    game.frames[TFRAME_NPCDIALOG]=&npc;game.frames[TFRAME_QUEST_NEWUI]=&journal;game.frames[TFRAME_GAUGE]=&panel;
    npc.m_pHost=&host;journal.m_pHost=&host;
    npc.accept.id=1;npc.refuse.id=2;
    auto check=[](bool condition,const char* message){if(!condition)throw std::runtime_error(message);};
    for(auto action:{Action::Accept,Action::Complete}) {
        npc.SetQuestButtons(action,Action::Close);
        for(const auto& caption:{"English","Cesky","libovolny text"}) {
            npc.accept.m_strText=caption;int before=game.accepts;
            npc.OnLButtonUp(0,{1});check(game.accepts==before+1,"One click must execute exactly once");
        }
    }
    npc.SetQuestButtons(Action::GiveUp,Action::Close);npc.OnLButtonUp(0,{1});
    check(game.giveups==1,"Give-up routing");
    npc.accept.enabled=false;npc.OnLButtonUp(0,{1});check(game.giveups==1,"Disabled button executed");
    npc.accept.enabled=true;npc.accept.visible=false;npc.OnLButtonUp(0,{1});check(game.giveups==1,"Hidden button executed");
    npc.accept.visible=true;
    npc.SetQuestButtons(Action::NextQuest,Action::Close);
    check(npc.accept.m_strText=="Dal\232\355" && npc.refuse.m_strText=="Zav\370\355t","Czech next/close captions");
    npc.OnLButtonUp(0,{2});check(game.closed==TFRAME_NPCDIALOG,"Close NPC dialog");game.closed=0;
    Quest a{7981},b{7985},c{7987};CTClientQuest accepted{&b};
    npc.m_QuestList[0].m_mapQuest[1].resize(1);
    npc.m_QuestList[1].m_mapQuest[2].resize(1);
    npc.m_QuestList[2].m_mapQuest[3].resize(1);
    auto& first=npc.m_QuestList[0].m_mapQuest[1][0];first.m_dwQuest=(uintptr_t)&a;
    auto& second=npc.m_QuestList[1].m_mapQuest[2][0];second.m_Accepted=true;second.m_dwQuest=(uintptr_t)&accepted;
    auto& last=npc.m_QuestList[2].m_mapQuest[3][0];last.m_dwQuest=(uintptr_t)&c;
    npc.OnLButtonUp(0,{1});check(npc.m_Quest==&first && npc.m_pTQUEST==&a,"Next selects first quest");
    npc.OnLButtonUp(0,{1});check(npc.m_Quest==&second && npc.m_pTQUEST==&b,"Next crosses categories and handles accepted quest");
    npc.OnLButtonUp(0,{1});check(npc.m_Quest==&last && npc.m_pTQUEST==&c,"Next crosses lists");
    npc.OnLButtonUp(0,{1});check(game.closed==TFRAME_NPCDIALOG,"Next after last quest closes dialog");
    host.quests[b.m_dwID]=&accepted;journal.m_pTQUEST=&b;
    journal.SetQuestButtons(Action::GiveUp,Action::TrackQuest);
    check(journal.refuse.m_strText=="Navigovat","Czech navigation caption");
    journal.ExecuteButtonAction(Action::TrackQuest);
    check(panel.updates==1 && panel.m_dwCurQuestID==7985 && panel.m_bAutoPath && panel.m_bShowPath,"Navigation action");
    host.quests.clear();journal.ExecuteButtonAction(Action::TrackQuest);
    check(panel.updates==1,"Completed or absent quest must not start navigation");
    npc.m_pTSELREWARD=&npc;npc.ClearQuestSelection();
    check(!npc.m_Quest && !npc.m_pTQUEST && !npc.m_pTSELREWARD && !npc.accept.enabled,"Selection reset");
    std::cout<<"Quest buttons passed: one action per click, locale-independent routing, disabled/hidden buttons, next across all lists, close, navigation and stale selection reset.\n";
    return 0;
}
int main(){try{return run();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
'''


def main():
    root = Path(__file__).resolve().parents[2]
    source = (root / 'Client/TClient/TQuestNewDlg.cpp').read_text(encoding='utf-8-sig')
    header = (root / 'Client/TClient/TQuestNewDlg.h').read_text(encoding='utf-8-sig')
    handlers = source[source.index('void CQuestNewDlg::OnLButtonUp('):source.index('void CQuestNewDlg::OnLButtonDown(')]
    enum = re.search(r'enum class ButtonAction\s*\{[^}]+\};', header)[0]
    dictionary = '#include "' + (root / 'Lib/Own/TChart/TChart/CzechUiText.inc').as_posix() + '"'
    compiler = shutil.which('cl')
    if compiler is None:
        raise SystemExit('Run from a Visual Studio developer shell.')
    with tempfile.TemporaryDirectory(prefix='quest-buttons-') as temporary:
        folder = Path(temporary)
        cpp = folder / 'quest_buttons.cpp'
        cpp.write_text(FIXTURE.replace('@ENUM@', enum).replace('@DICTIONARY@', dictionary) + handlers + CASES, encoding='utf-8')
        subprocess.run([compiler, '/nologo', '/std:c++17', '/EHsc', '/utf-8', str(cpp), '/Fe:quest_buttons.exe'], cwd=folder, check=True)
        subprocess.run([str(folder / 'quest_buttons.exe')], cwd=folder, check=True)


if __name__ == '__main__':
    main()
