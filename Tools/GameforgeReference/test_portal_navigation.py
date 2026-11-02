"""Compile the actual native portal-link loader against real client chart records."""
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import unittest

from client_records import named

ROOT=Path(__file__).resolve().parents[2]

HARNESS=r'''
#include <cstdint>
#include <fstream>
#include <iostream>
#include <map>
#include <set>
#include <string>
#include <tuple>
#include <vector>
#include <stdexcept>
using BYTE=uint8_t;using WORD=uint16_t;using DWORD=uint32_t;using LPCSTR=const char*;
struct CString:std::string{CString(const char* p):std::string(p){}operator const char*()const{return c_str();}};
constexpr int FALSE=0,TCONTRY_COUNT=5,TCONTRY_D=0,TCONTRY_C=1,TCONTRY_B=2,TCONTRY_PEACE=4;
struct CFile {static constexpr int modeRead=0,typeBinary=0;std::ifstream stream;CFile(const char* p,int):stream(p,std::ios::binary){if(!stream)throw std::runtime_error("Missing chart");}};
struct CArchive {static constexpr int load=0;CFile* file;CArchive(CFile* f,int):file(f){}template<class T>CArchive& operator>>(T& v){file->stream.read(reinterpret_cast<char*>(&v),sizeof v);if(!file->stream)throw std::runtime_error("Truncated chart");return *this;}};
struct TPORTAL{WORD id;float x,z;};using LPTPORTAL=TPORTAL*;
struct TNPCPORTAL{LPTPORTAL m_pTPORTAL=nullptr;WORD m_wNpcID=0;};using LPTNPCPORTAL=TNPCPORTAL*;using VT=std::vector<LPTNPCPORTAL>;using LPVTNPCPORTAL=VT*;
union UNITID{DWORD m_dwID;struct{WORD m_wMapID;BYTE m_bUnitX,m_bUnitZ;}m_vID;UNITID():m_dwID(0){}};
struct TPORTALLINK{UNITID m_vID;VT m_vTLOCAL[5],m_vTBASEPORTAL[5],m_vTFROMPORTAL[5];LPTNPCPORTAL m_vTHUB[5]{};};using LPTPORTALLINK=TPORTALLINK*;using MAPTPORTALLINK=std::map<DWORD,LPTPORTALLINK>;
struct CTChart{static inline MAPTPORTALLINK m_mapTPORTALLINK;static inline std::map<WORD,TPORTAL> portals;
static DWORD key(WORD m,BYTE x,BYTE z){return m|DWORD(x)<<16|DWORD(z)<<24;}
static LPTPORTALLINK FindTPORTALLINK(WORD m,BYTE x,BYTE z){auto i=m_mapTPORTALLINK.find(key(m,x,z));return i==m_mapTPORTALLINK.end()?nullptr:i->second;}
static LPTPORTAL GetTPORTAL(WORD id){auto i=portals.find(id);return i==portals.end()?nullptr:&i->second;}
static void InitTDESTINATION(CString);
};
__LOADER__
int main(int argc,char**argv){try{
std::ifstream p(argv[2]);unsigned id;float x,z;while(p>>id>>x>>z)CTChart::portals[WORD(id)]={WORD(id),x,z};
CTChart::InitTDESTINATION(argv[1]);
using Entry=std::tuple<DWORD,int,int,WORD,WORD>;std::multiset<Entry> expected,actual;
CFile file(argv[1],0);CArchive input(&file,0);WORD count;input>>count;int checked=0;
for(int i=0;i<count;i++){WORD from,to,npc,map;DWORD unknown;BYTE flags[7],ux,uz;input>>from>>to>>unknown>>npc;for(auto& f:flags)input>>f;input>>map>>ux>>uz;
if(!CTChart::GetTPORTAL(to))continue;int kind=flags[1]?0:flags[0]?1:2;int countries[4]={0,1,2,4};for(int k=0;k<4;k++)if(flags[3+k])expected.insert({CTChart::key(map,ux,uz),kind,countries[k],npc,to});checked++;}
for(auto& [key,unit]:CTChart::m_mapTPORTALLINK)for(int country=0;country<5;country++)for(int kind=0;kind<3;kind++){
auto& v=kind==0?unit->m_vTLOCAL[country]:kind==1?unit->m_vTBASEPORTAL[country]:unit->m_vTFROMPORTAL[country];for(auto link:v)actual.insert({key,kind,country,link->m_wNpcID,link->m_pTPORTAL->id});}
if(actual!=expected)throw std::runtime_error("Loaded portal links point to the wrong endpoint");
auto unit=CTChart::FindTPORTALLINK(0,3,0);bool gor=false;if(unit)for(auto link:unit->m_vTLOCAL[4])if(link->m_wNpcID==26174&&link->m_pTPORTAL->id==4506){gor=true;if(link->m_pTPORTAL->x<3618.09f||link->m_pTPORTAL->x>3618.11f||link->m_pTPORTAL->z<915.26f||link->m_pTPORTAL->z>915.28f)throw std::runtime_error("Gor exit coordinates differ");}
if(!gor)throw std::runtime_error("First guard post cannot navigate through portal 26174 to the second guard post");
std::cout<<"Validated "<<checked<<" real destination rows, country flags, source NPCs and Gor 4505 -> 4506 exit coordinates\n";
return 0;}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
'''


@unittest.skipUnless(shutil.which('cl'),'Run in a Visual Studio developer shell')
class PortalNavigationTests(unittest.TestCase):
    def test_actual_chart_loader_uses_destination_for_local_and_cross_unit_routes(self):
        source=(ROOT/'Lib/Own/TChart/TChart/TChart.cpp').read_text(encoding='utf-8-sig')
        start=source.index('void CTChart::InitTDESTINATION(');end=source.index('void CTChart::InitTUNITLINK(',start)
        with tempfile.TemporaryDirectory() as folder:
            work=Path(folder);cpp=work/'portal_test.cpp';cpp.write_text(HARNESS.replace('__LOADER__',source[start:end]),encoding='utf8')
            exe=work/'portal_test.exe';build=subprocess.run(['cl','/nologo','/EHsc','/std:c++17',str(cpp),'/Fe:'+str(exe)],cwd=work,capture_output=True,text=True,errors='replace')
            self.assertEqual(0,build.returncode,build.stdout+build.stderr)
            for root in [ROOT/'Game',Path('C:/Program Files (x86)/4Story')]:
                portals=work/'portals.txt';rows=named(root/'Tcd/TPortal.tcd')[0]
                portals.write_text(''.join(f'{r.key} {struct.unpack("<HHffBB",r.raw[-14:])[2]:.9g} {struct.unpack("<HHffBB",r.raw[-14:])[3]:.9g}\n' for r in rows),encoding='ascii')
                result=subprocess.run([str(exe),str(root/'Tcd/TDest.tcd'),str(portals)],capture_output=True,text=True)
                self.assertEqual(0,result.returncode,str(root)+': '+result.stderr)
                print(result.stdout.strip())


if __name__=='__main__':unittest.main()
