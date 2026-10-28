"""Run the actual popup-menu and companion-transfer handlers against recording UI fixtures.

Run from a Visual Studio developer shell. No game, network or gameplay database is started.
"""
import re
import shutil
import subprocess
import tempfile
from pathlib import Path


FIXTURE = r'''
#include <cstdint>
#include <functional>
#include <iostream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
using BYTE=uint8_t;using DWORD=uint32_t;using UINT=unsigned;using BOOL=bool;using HRESULT=int;using LPCTSTR=const char*;
constexpr bool TRUE=true,FALSE=false;
struct CString:std::string {using std::string::string;using std::string::operator=;void Empty(){clear();}};
struct CPoint{int x;};
struct TComponent {
    int id=0,image=0;bool visible=true,enabled=true;CString m_strText;
    void ShowComponent(bool b){visible=b;}void EnableComponent(bool b){enabled=b;}
    bool IsVisible(){return visible;}bool IsEnable(){return enabled;}
    bool HitTest(CPoint p){return visible && p.x==id;}
    void SetCurImage(int i){image=i;}int GetCurImage(){return image;}
};
using TButton=TComponent;using TImageList=TComponent;using TScroll=TComponent;
struct TGauge {DWORD tick=0;void SetGauge(DWORD n,DWORD){tick=n;}};
struct CTClientUIBase {
    static inline bool m_bDragLock=false;
    bool visible=true;void AddKid(TComponent*){}void RemoveKid(TComponent*){}
    void ShowComponent(bool b){visible=b;}bool IsVisible(){return visible;}
    void OnLButtonUp(UINT,CPoint){}int Render(DWORD){return 0;}
};
@ENUMS@
@CONSTANTS@
struct CTSecuritySystemDlg {
    bool exists=false,enabled=false;int mode=-1;
    bool GetExist(){return exists;}bool GetEnabled(){return enabled;}
    void PrepareDisplay(){}void SwitchMode(int n){mode=n;}
};
struct CTClientSession {
    int transfers=0;void SendCS_FINISHCOMPANIONTRANSFER_ACK(BYTE,BYTE,BYTE){++transfers;}
};
struct CTClientGame {
    static inline CTClientGame* instance=nullptr;static inline bool bow=false,br=false;
    static CTClientGame* GetInstance(){return instance;}
    static bool IsInBOWMap(){return bow;}static bool IsInBRMap(){return br;}
    CTSecuritySystemDlg security;CTClientSession session;std::vector<std::string> actions;
    std::function<void()> reset;
    void* GetFrame(int){return &security;}
    CTClientSession* GetSession(){return &session;}
    void ResetPopupMenu(){if(reset)reset();}
    void EnableUI(int){actions.push_back("security");}
    @ACTIONS@
};
struct CTChart {
    static CString LoadString(int){return "caption";}
    static LPCTSTR Localize(LPCTSTR text){static const std::map<std::string,LPCTSTR> dictionary={
    @DICTIONARY@
    };auto found=dictionary.find(text);return found==dictionary.end()?text:found->second;}
};
struct CTPopupNewMenuDlg:CTClientUIBase {
    TComponent images[TMENUNEW_COUNT],names[TMENUNEW_COUNT],buttons[TMENUNEW_COUNT],title,scroll;
    TImageList* m_pCatImg[TMENUNEW_COUNT];TComponent* m_pCatName[TMENUNEW_COUNT];TButton* m_pCatBtn[TMENUNEW_COUNT];
    TComponent* m_pFrameName=&title;TScroll* m_pScroll=&scroll;
    int m_nCurMode=0,m_InvenActions[TMENUNEW_COUNT]{};
    CTPopupNewMenuDlg(){for(int i=0;i<TMENUNEW_COUNT;i++){m_pCatImg[i]=&images[i];m_pCatName[i]=&names[i];m_pCatBtn[i]=&buttons[i];buttons[i].id=i+1;}}
    CString GetVKString(int){return "key";}
    void ClearPopup();void ClearButton(){ShowComponent(false);}
    void AddSystem();void AddChar();void AddCommunity();void AddWar();void AddHelp();void AddInven();
    void ExecuteInvenAction(int);void OnLButtonUp(UINT,CPoint);
};
constexpr DWORD GAUGE_MOVING_IN_SECS=3000;
struct CCompStyleDlg:CTClientUIBase {
    TComponent start,finish,face,target;TGauge gauge;
    TButton* m_pStart=&start;TComponent* m_pFinishTxt=&finish;TImageList* m_pFaceIcon=&face;TImageList* m_pTargetIcon=&target;TGauge* m_pGauge=&gauge;
    BYTE m_bFromSlot=1,m_bInvenID=2,m_bItemID=3;bool m_bFinished=false,m_bProgress=false;DWORD m_dwTransferTick=0;
    CCompStyleDlg(){start.id=1;target.image=42;}
    HRESULT Render(DWORD);void OnLButtonUp(UINT,CPoint);void ShowComponent(BOOL);
};
'''

CASES = r'''
int run(){
    auto check=[](bool b,const char* s){if(!b)throw std::runtime_error(s);};
    CTClientGame game;CTClientGame::instance=&game;CTPopupNewMenuDlg menu;
    game.reset=[&]{menu.ClearPopup();};
    for(bool exists:{false,true})for(bool enabled:{false,true}) {
        game.security.exists=exists;game.security.enabled=enabled;
        std::vector<std::string> expected={"OnGM_TOGGLE_DEFINVEN","OnGM_TOGGLE_CASH_CAB","security"};
        if(exists)expected.push_back("security");expected.push_back("OnGM_TOGGLE_CUSTOMCLOAK");
        for(int row=0;row<(int)expected.size();row++) {
            menu.AddInven();menu.ShowComponent(true);game.actions.clear();
            menu.m_pCatName[row]->m_strText="libovolny popisek";
            menu.OnLButtonUp(0,{row+1});
            check(game.actions==std::vector<std::string>{expected[row]},"Inventory action/caption/row mismatch");
            if(row==2)check(game.security.mode==(exists?TDLG_SC_MCHANGE:TDLG_SC_MCREATE),"Create/change security action");
            if(exists && row==3)check(game.security.mode==(enabled?TDLG_SC_MDISABLE:TDLG_SC_MENABLE),"Enable/disable security action");
        }
        menu.AddInven();game.actions.clear();menu.m_pCatBtn[0]->EnableComponent(false);menu.OnLButtonUp(0,{1});
        check(game.actions.empty(),"Disabled inventory button executed");
    }
    auto clickRows=[&](auto build,const std::vector<std::string>& expected){
        for(int row=0;row<(int)expected.size();row++){
            (menu.*build)();game.actions.clear();menu.OnLButtonUp(0,{row+1});
            check(game.actions==std::vector<std::string>{expected[row]},"Popup route mismatch");
        }
    };
    clickRows(&CTPopupNewMenuDlg::AddSystem,{"OnGM_SYSTEM_SETTING","OnGM_GAME_SETTING","OnGM_CONTROL_SETTING","OnGM_TOGGLE_HELP","OnGM_TOGGLE_CHAT_LOCK","OnGM_SYSTEM","OnGM_QUIT"});
    clickRows(&CTPopupNewMenuDlg::AddChar,{"OnGM_TOGGLE_CHARINFO","OnGM_TOGGLE_SKILLINFO","OnGM_TOGGLE_TITLE","OnGM_TOGGLE_PETMANAGE","OnGM_TOGGLE_QUESTINFO","OnGM_OPEN_PRIVSHOP_FOR_SELL","OnGM_TOGGLE_COMPANION"});
    clickRows(&CTPopupNewMenuDlg::AddHelp,{"OnGM_TOGGLE_GUILD_BASE_NEW","OnGM_TOGGLE_MESSENGER","OnGM_TOGGLE_BLOCKLIST","OnGM_TOGGLE_ACTLIST"});
    clickRows(&CTPopupNewMenuDlg::AddWar,{"OnGM_WAR_INFO","OnGM_TOGGLE_FAMERANK","OnGM_RANK_INFO"});
    clickRows(&CTPopupNewMenuDlg::AddCommunity,{"OnGM_TOGGLE_MAIL"});
    CTClientGame::bow=true;
    clickRows(&CTPopupNewMenuDlg::AddChar,{"OnGM_TOGGLE_CHARINFO","OnGM_TOGGLE_SKILLINFO","OnGM_TOGGLE_PETMANAGE","OnGM_TOGGLE_COMPANION","OnGM_TOGGLE_ACTLIST"});
    CTClientGame::bow=false;menu.AddChar();game.actions.clear();menu.m_pCatBtn[TMENUNEWCHAR_PET]->ShowComponent(false);
    menu.OnLButtonUp(0,{TMENUNEWCHAR_COMPANION+1});check(game.actions==std::vector<std::string>{"OnGM_TOGGLE_COMPANION"},"Companion visibility checked the pet button");
    CCompStyleDlg transfer;transfer.start.m_strText="Zavrit";transfer.OnLButtonUp(0,{1});
    check(transfer.m_bProgress,"Transfer must use state, not caption");
    transfer.Render(1500);transfer.OnLButtonUp(0,{1});transfer.Render(1499);
    check(game.session.transfers==0,"Transfer completed early or repeated click reset timer");
    transfer.Render(1);check(game.session.transfers==1 && transfer.m_bFinished,"Transfer completion");
    transfer.Render(5000);check(game.session.transfers==1,"Transfer sent twice");
    transfer.start.m_strText="Transfer";transfer.OnLButtonUp(0,{1});check(!transfer.visible,"Finished transfer must close regardless of caption");
    transfer.m_bFinished=false;transfer.ShowComponent(true);transfer.OnLButtonUp(0,{1});transfer.Render(1500);transfer.ShowComponent(false);transfer.Render(5000);
    check(game.session.transfers==1,"Hidden dialog completed transfer");
    transfer.ShowComponent(true);transfer.OnLButtonUp(0,{1});transfer.Render(2999);check(game.session.transfers==1,"Cancelled transfer retained timer");transfer.Render(1);
    check(game.session.transfers==2,"Restarted transfer failed");
    CCompStyleDlg first,second;first.OnLButtonUp(0,{1});second.OnLButtonUp(0,{1});first.Render(2000);second.Render(1000);
    check(game.session.transfers==2,"Transfer timers shared between instances");
    second.Render(2000);check(game.session.transfers==3 && !first.m_bFinished,"Independent transfer timer");
    std::cout<<"Popup routes passed for inventory/security variants, settings, character, guild/social, war, mail and battle menus. Companion state, timer, cancellation and repeat-send checks passed.\n";
    return 0;
}
int main(){try{return run();}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
'''


def method(source, signature):
    start = source.index(signature)
    opening = source.index('{', start)
    level = 1
    pos = opening + 1
    while level:
        if source[pos] == '{':
            level += 1
        elif source[pos] == '}':
            level -= 1
        pos += 1
    return source[start:pos] + '\n'


def main():
    root = Path(__file__).resolve().parents[2]
    popup = (root / 'Client/TClient/TPopupNewMenu.cpp').read_text(encoding='utf-8-sig')
    transfer = (root / 'Client/TClient/CompStyleDlg.cpp').read_text(encoding='utf-8-sig')
    types = (root / 'Client/TClient/TClientType.h').read_text(encoding='utf-8-sig')
    enums = '\n'.join(m[0] for m in re.finditer(r'typedef enum (?:TMAINBUTTONNEW|TMENUNEW\w*)\s*\{[^}]+\}[^;]+;', types))
    handlers = ''.join(method(popup, 'void CTPopupNewMenuDlg::' + name + '(') for name in
                       ('ClearPopup', 'AddInven', 'ExecuteInvenAction', 'AddSystem', 'AddChar', 'AddHelp', 'AddWar', 'AddCommunity', 'OnLButtonUp'))
    handlers += method(transfer, 'HRESULT CCompStyleDlg::Render(')
    handlers += method(transfer, 'void CCompStyleDlg::OnLButtonUp(')
    handlers += method(transfer, 'void CCompStyleDlg::ShowComponent(')
    constants = sorted(set(re.findall(r'\b(?:TFRAME_\w+|TKEY_\w+|TDLG_SC_\w+|TSTR_\w+)\b', handlers)))
    declarations = '\n'.join('constexpr ' + ('auto ' + name + '="' + name + '";' if name.startswith('TSTR_SC_')
                                            else 'int ' + name + '=' + str(i+1) + ';') for i, name in enumerate(constants))
    actions = sorted(set(re.findall(r'->(OnGM_\w+)\(', handlers)))
    action_stubs = '\n'.join('void '+name+'(){actions.push_back("'+name+'");}' for name in actions)
    dictionary = '#include "' + (root / 'Lib/Own/TChart/TChart/CzechUiText.inc').as_posix() + '"'
    fixture = FIXTURE.replace('@ENUMS@', enums).replace('@CONSTANTS@', declarations).replace('@ACTIONS@', action_stubs).replace('@DICTIONARY@', dictionary)
    compiler = shutil.which('cl')
    if compiler is None:
        raise SystemExit('Run from a Visual Studio developer shell.')
    with tempfile.TemporaryDirectory(prefix='ui-actions-') as temporary:
        folder = Path(temporary)
        cpp = folder / 'ui_actions.cpp'
        cpp.write_text(fixture + handlers + CASES, encoding='utf-8')
        subprocess.run([compiler, '/nologo', '/std:c++17', '/EHsc', '/utf-8', str(cpp), '/Fe:ui_actions.exe'], cwd=folder, check=True)
        subprocess.run([str(folder / 'ui_actions.exe')], cwd=folder, check=True)


if __name__ == '__main__':
    main()
