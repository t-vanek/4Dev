"""Build the Czech comparison report from completed local analysis artifacts."""
import csv,html,json,pathlib,sys
from compare import write_json,write_csv

def make(out):
    d=json.loads((out/'summary.json').read_text(encoding='utf8'))
    unpack=json.loads((out/'unpack.json').read_text(encoding='utf8'))
    decomp={label:json.loads((out/('decompilation-'+label)/'summary.json').read_text(encoding='utf8'))for label in ('official','ours')}
    evidence=json.loads((out/'class-code-evidence.json').read_text(encoding='utf8'))
    extracted=list(csv.DictReader((out/'official-only-resources/manifest.csv').open(encoding='utf-8-sig')))
    features=[
        dict(area='Mentor / učitel a student',status='Implementace originálu nám chybí',confidence='Vysoká pro klientské UI',
             official='CTMentorDlg, CTMentorFrm, CTMenteeListItem; hlavní dialog ID 1103. Konstruktor CTMentorDlg: 0x0060C7D0.',
             ours='Odpovídající třídy ani dialog 1103 nejsou přítomné. Texty učitele přežívají v jiných UI rámcích (např. 1244).',
             conclusion='Ponechané texty nejsou důkazem funkčního systému učitele; chybí původní klientská implementace.'),
        dict(area='Twitter přihlášení a vložený web',status='Originál má integrované moduly, u nás externí odkaz',confidence='Vysoká pro přítomnost kódu',
             official='CTTwitter, CTTwitterLoginDlg, CTSNSBaseDlg, CTClientWebCtrl, THttpClient; UI 1100 a 1102; Browser/ obsahuje 123 souborů. Twitter dialog: 0x006887F0.',
             ours='OnGM_TOGGLE_TWITTER otevírá externí web. Native dialogy 1100/1102 a uvedené třídy v EXE chybí.',
             conclusion='Chybí integrované přihlášení/webová komponenta. Funkčnost historických online služeb v roce 2026 nebyla ověřována.'),
        dict(area='Původní Battle League',status='Originál obsahuje samostatné moduly, náš klient má odlišné PvP systémy',confidence='Vysoká pro rozdíl implementace; herní parita neověřená',
             official='CTBLReady, CTBLSkillShop, CTBLInsigniaList, CTBLScoreBoard, CTBLTeamBPBoard, CTBLVictorySplash a CTBattleLeagueTicket; CTBLReady: 0x00483DB0.',
             ours='Tyto třídy chybí. Existují CTBFGStatus, CTBattleInvenDlg, CTBow* a CBR*; BFG/BoW/BR obsluha je v CSHandler.cpp a CSSender.cpp.',
             conclusion='Původní BL moduly nejsou převzaté. Nelze ztotožnit všechny jejich funkce s naším BoW/BR bez rozboru konkrétních pravidel a packetů.'),
        dict(area='Battle Royale',status='Rozšíření našeho klienta',confidence='Vysoká pro klientské moduly a UI',
             official='V analyzovaném EXE nebyly nalezeny CBRTeamDlg, CBRRankingDlg, CBRTRanking ani odpovídající nové rámce.',
             ours='CBRTeamDlg (0x0040B750), CBRPannels, CBRRankingDlg, CBRTRanking, solo/team obsluha; nové UI 1302 a 0x100004BF–0x100004C1.',
             conclusion='Máme vlastní klientskou implementaci BR včetně týmu a žebříčků. Běh celého zápasu se serverem nebyl testován.'),
        dict(area='World War / Battle of the Worlds',status='Oba klienti, jiná implementace',confidence='Vysoká pro přítomnost obou implementací',
             official='CTWorldWarEntryDlg (0x00695A00), CTWorldWarGauge, CTWorldWarRewardInfo, CTWorldWarScoreBoard.',
             ours='CTBowRegisterDlg (0x004734B0), CTBowRankingDlg, CTBowRespawnDlg, CTBowBPTradeDlg; společné UI 1264–1266.',
             conclusion='BoW není výlučnou funkcí našeho klienta a World War není automaticky chybějící funkcí. Rozdílné názvy tříd neprokazují rozdíl pravidel.'),
        dict(area='Společníci / pets a mounty',status='Oba klienti, odlišné modely a více mountů u nás',confidence='Vysoká pro data a přítomnost kódu',
             official='CTClientSoul, CTClientPetEffect, CTInnerPetStats/Ability/Storage, CTPetManagerDlg, CTMountManagerDlg.',
             ours='CTClientCompanion, CTClientSpolecnik, CTCompanionDlg, CTCompanionAttrs/Stats, CTPetManageDlg. TMount: 196 řádků proti 43.',
             conclusion='Nelze tvrdit, že originál nemá společníky nebo že nám chybí správa petů. TPet má změněné schéma a nebyl sémanticky dekódován.'),
        dict(area='Skygarden',status='Oba klienti',confidence='Vysoká',
             official='CTSkyGarden a CTSkyGardenResult v RTTI.',
             ours='CTSkygardenMap, CTSkygardenEnd; OnCS_ENTERSKYGARDEN_ACK a další handlery v CSHandler.cpp.',
             conclusion='Rozdíl názvů tříd; nepovažovat za chybějící herní režim.'),
        dict(area='Vlastní obrázky plášťů',status='Rozšíření našeho klienta',confidence='Vysoká pro dialog a kód',
             official='Odpovídající CTCustomCloakDlg ani UI 1400 v analyzovaném klientu nejsou.',
             ours='CTCustomCloakDlg (0x0055F800), TClientCustomCloak.cpp, UI 1400 s výběrem obrázku a náhledem.',
             conclusion='Máme vlastní modul pro výběr obrázku pláště; přenos a zobrazení dalším hráčům vyžaduje serverovou podporu.'),
        dict(area='Ověření přihlášení kódem',status='Dodatečný dialog našeho klienta',confidence='Vysoká pro klientské UI',
             official='CTSecuritySystemDlg ani UI 1300 v analyzovaném EXE/UI nejsou.',
             ours='CTSecuritySystemDlg (0x005E2C50), UI 1300 popisuje kód z e-mailu při přihlášení z neznámého počítače.',
             conclusion='Prokázaná je přítomnost dialogu a klientského kódu; nebylo prověřeno odeslání e-mailu ani dokončení ověření na serveru.'),
        dict(area='Engine a konfigurace',status='Náš klient má modernizovaný technický základ',confidence='Vysoká: zdrojáky a binární doklady',
             official='Importy Direct3D9/D3DX, DirectSound, MFC/Win32 komponenty. MPRESS 2.1x + LZMA.',
             ours='bgfx, SDL3, miniaudio, čitelný config.ini; BGFXRenderer=D3D12. EXE obsahuje SDL, WASAPI a BGFXRenderer řetězce. CMake build a zdroje rendereru.',
             conclusion='Máme moderní backendy a správu okna/audio. D3D9 je stále přítomný jako kompatibilní backend/závislost, není správné označit jej za odstraněný.'),
        dict(area='Cash shop',status='Odlišná integrace',confidence='Vysoká pro klientské moduly',
             official='CTNewCashShopDlg, CTCashShopItem/Slot a vložené webové komponenty.',
             ours='CTCashShopDlg zůstává. Konstrukce TFRAME_CASHSHOP_NEW a TFRAME_WEB_CASHSHOP je v TClientWnd.cpp zakomentovaná; OnGM_TOGGLE_CASH_SHOP otevírá externí URL.',
             conclusion='Chybí původní integrovaná varianta, nelze tvrdit, že náš klient nemá žádný cash shop.'),
    ]
    write_json(out/'feature-matrix.json',features)
    write_csv(out/'feature-matrix.csv',features,['area','status','confidence','official','ours','conclusion'])
    lines=[]
    def add(s=''):lines.append(s)
    add('4STORY — POROVNÁNÍ ORIGINÁLNÍHO A NAŠEHO KLIENTA')
    add('Datum: 27. 10. 2026 (Europe/Prague)')
    add('Originál: '+d['official']);add('Náš runtime: '+d['ours']);add('Náš kód: Client/ + Lib/Own/ v repozitáři 4Dev')
    add()
    add('HLAVNÍ ZJIŠTĚNÍ')
    add('Náš klient má modernizovaný engine, vlastní BR, pláště a výrazně více mountů, modelů a předmětů. Originální instalace má více úkolových záznamů, skillů, regionů a některé klientské moduly, které jsme nepřevzali. Žádná strana neobsahuje úplně vše z druhé.')
    add('Jde o tuto lokální instalaci, nikoli o aktuální online vydání Gameforge. PE timestamp originálního TClient.exe je 25. 6. 2018 08:55:12 UTC; soubor má jiný čas na disku. Náš PE timestamp je 25. 10. 2026 20:06:02 UTC. Timestampy nejsou ověřené datum vydání.')
    add()
    add('DATA — OVĚŘENÉ ZÁZNAMY A ID')
    add('Tabulka                   Originál      Náš   Jen originál   Jen náš')
    for key in ['TQuest.mpq','TSkill.tcd','TItem.tcd','TMon.tcd','TRegion.tcd','TPortal.tcd','TCharTitle.tcd','TMount.tcd','TString.tcd']:
        t=d['tables'][key];add(f'{key:25} {t["official"]:8} {t["ours"]:8} {len(t["official_only"]):12} {len(t["ours_only"]):9}')
    add('Počty jsou unikátní ID v souborech. Nejde o počet právě dostupných herních aktivit. TMon obsahuje také další typy objektů; TRegion je seznam regionů, nikoli seznam celých map. TString obsahuje i systémové texty a ID mezi verzemi mění význam.')
    add('Předměty: 7 727 společných ID; 7 535 má shodná netextová data, 7 222 i celý záznam. Rozdíl názvu/překladu tedy sám o sobě není nová herní funkce.')
    add('Příklady 39 chybějících předmětových ID: 12068–12079 (Ratha Panda, Plameňák, Pekelný oř, Sibiřský husky v časových variantách), 13000–13007 (vstupenky/deklarace války pro levelové varianty instancí), 19061–19070 (různé runy společníků). Úplný seznam: tables/TItem.tcd.diff.csv.')
    add('Příklady 48 chybějících skill ID: 831–870, 948–952, 1633–1635. Jde i o NPC/event/transformační schopnosti; nejsou to automaticky chybějící hráčské skilly.')
    add('Chybějící regiony zahrnují varianty Ostrova mořských lvů, Britronu, elfích lokalit, Ďáblova doupěte a Jeskyně věčného ledu. Úkolové rozdíly obsahují i craftingové záznamy a opakované varianty; nelze je celé označit za nové příběhové úkoly.')
    add('TMount má 43 původních ID i u nás; přidáváme 153 ID. Ve zdroji InitTPET jsou dále runtime doplňky, tabulka nevyjadřuje konečný počet ve hře.')
    add()
    add('INDEXOVANÉ PROSTŘEDKY')
    add('Index                     Originál      Náš   Jen originál   Jen náš')
    for key,t in d['indices'].items():add(f'{key:25} {t["official"]:8} {t["ours"]:8} {len(t["official_only"]):12} {len(t["ours_only"]):9}')
    add('S = textury (0/1/2 jsou tři úrovně detailu), A = animace, I = obrázky UI, M = mesh, O = objekty, P = mapové prostředky, W = média, X = efekty. Tři LODy textur se nesčítají jako tři různé funkce.')
    add('Chybějící ID může mít byte-identický obsah pod jiným ID: 11 animací, 2 efektové záznamy a 1 mapový záznam takový protějšek u nás mají. Například originální mapové ID 999231774 odpovídá našemu 999231756. Detailní mapování je v summary.json.')
    add('Index médií má 815 proti 812 ID; 810 existujících společných souborů má shodný SHA-256. Originál sám postrádá pět odkazovaných médií; náš klient jedno. Tři pouze originální mediální ID 285964680–285964682 nemají fyzický soubor ani v originální instalaci, proto je nelze extrahovat jako chybějící zvuky.')
    add(f'Vytaženo {len(extracted)} původních záznamů s chybějícím ID ({sum(int(x["bytes"])for x in extracted):,} B) do official-only-resources/. Manifest obsahuje původní pack, offset, ID a SHA-256. Záznamy zahrnují uvedené aliasy a všechny tři LODy. Nejsou to soubory připravené k přímému nasazení; potřebují vyřešit reference a zápis indexů.')
    add()
    add('FUNKCE A IMPLEMENTACE')
    for row in features:
        add(row['area']+' — '+row['status']);add('  Originál: '+row['official']);add('  Náš: '+row['ours']);add('  Závěr: '+row['conclusion']);add('  Jistota: '+row['confidence']);add()
    add('UI A SHODA SOUBORŮ')
    fs=d['files']['statuses'];add(f'Celkem originál {fs["identical"]+fs["different"]+fs["official_only"]} souborů, náš {fs["identical"]+fs["different"]+fs["ours_only"]}. Shodných SHA-256: {fs["identical"]}; společná cesta, rozdílný obsah: {fs["different"]}; pouze originál: {fs["official_only"]}; pouze náš: {fs["ours_only"]}.')
    add('UI originálu: 310 rámců a 74 fontů, formát TIF v1, 158 112 090 B. Naše UI: 318 rámců / 316 unikátních ID a 74 fontů, formát v2, 3 941 004 B. V2 sdílí 49 seznamů potomků; menší velikost není důkaz odstraněného UI.')
    add('307 ID rámců je společných. Pouze originál: 1100, 1102 (Twitter), 1103 (Mentor). Pouze náš: 1136, 1137, 1300, 1302, 1400, 1401, 268436671–268436673. Obsah rámců se může přepisovat i při shodném ID.')
    add('V našem TIF je ID 1274 třikrát. Export uchovává všechny tři výskyty; srovnání podle ID používá poslední výskyt. Dopad na parser za běhu nebyl v této analýze měněn ani testován.')
    add('Soubory Browser/, staré instalátory, DLL a nevyužité anti-cheat balíky nejsou automaticky herní funkce. Samotná přítomnost GameGuard/HShield souborů neprokazuje aktivní ochranu za běhu.')
    add()
    add('DEKOMPILACE A DOHLEDATELNOST')
    add('Originální TClient.exe: '+unpack['source_sha256'])
    add('Náš TClient.exe: '+d['pe']['ours']['sha256'])
    add('Originál byl staticky rozbalen z MPRESS 2.1x/LZMA (lc=5); obnoveno 847 importů a vstupní bod RVA 0x0047C0B2. Nebyl spuštěn originální ani náš herní EXE. Rozbalený PE je jen analytická kopie.')
    for label,title in [('official','Originál'),('ours','Náš')]:
        s=decomp[label];external=s['discovered']-s['completed']-s['failed']-s['thunks']
        add(f'{title}: Ghidra nalezla {s["discovered"]} funkcí včetně externích; exportovala {s["completed"]} pseudokódů, {s["failed"]} dekompilací selhalo, {s["thunks"]} thunků a {external} externích importů se jako běžné tělo neexportuje.')
    add('Úplné exporty: decompilation-official/decompiled.c a decompilation-ours/decompiled.c; seznamy funkcí a stavy v functions.tsv, řetězce s odkazy na funkce v strings-xrefs.tsv. Projekty pro Ghidru: build/client-compare-ghidra/OfficialClient.gpr a OurClient.gpr.')
    add(f'Pro vybrané funkce bylo propojeno {len(evidence)} RTTI/vtable záznamů s adresami metod a míst v kódu. class-code-evidence.csv/json a menší features-official.c / features-ours.c ukazují konkrétní implementace. Pseudokód není původní C++, ztracené symboly/typy jsou částečně odhadnuté. Tři a třináct neúspěšných dekompilací a hranice obnovy PE jsou dohledatelné v logu a unpack.json.')
    add()
    add('KOMPATIBILITA A HRANICE ZÁVĚRŮ')
    add('Originální quest formát má oproti našemu další DWORD před záznamem a další bajt za hlavním příznakem. TSkill má v originálu ID před názvem a 144bajtový konec; náš formát má název před ID a 166bajtový konec. Přímé přepsání těchto tabulek bez konverze není kompatibilní.')
    add(f'Úspěšně načteno a na konec souboru ověřeno {len(d["tables"])} párových tabulek/quest souborů. Nepodporované: prázdný TNPC00000004.tcd a odlišný TPet.tcd. Další neznámé tabulky jsou porovnané binárně; jejich první WORD je uveden jen jako neověřený údaj a nepoužívá se jako počet záznamů.')
    add('Nebyl proveden živý test originálního serveru, handshaku ani end-to-end test přihlášení či PvP. Přítomnost ID, třídy nebo UI nedokazuje, že je funkce zapnutá nebo dostupná na serveru. Absence konkrétního názvu třídy sama neprokazuje absenci odpovídajícího chování.')
    add('Náš zdrojový strom obsahuje rozpracované změny. Zdrojové soubory a nasazený Game/TClient.exe jsou vedené odděleně; nová implementace se považuje za přítomnou v EXE jen při binárním dokladu.')
    add()
    add('DOPORUČENÉ POŘADÍ PŘÍPADNÉHO PORTU')
    add('1. Úkoly a levelové varianty instancí: 2 553 chybějících quest ID + související NPC/regiony/portály, včetně konverze a doplnění serveru.')
    add('2. Konkrétních 39 předmětů, 48 skill ID a jejich modelové/efektové závislosti. Použít exporty podle ID, ne přepsat celé tabulky.')
    add('3. Mentor, pokud jej chceme zpřístupnit: chybí hlavní klientské moduly, samotné texty nestačí.')
    add('4. Jednotlivé původní Battle League funkce porovnat s naším BFG/BoW/BR a rozhodnout podle pravidel režimu.')
    add('5. Integrované webové/Twitter části mají historické závislosti; přínos vyžaduje samostatné produktové rozhodnutí.')
    add()
    add('VÝSTUPY A OPAKOVÁNÍ')
    add('summary.json: strojové výsledky včetně chybějících ID a aliasů; files-diff.csv: všechny cesty/hashy; tables/: úplné čitelné záznamy a porovnání; indices/: seznamy prostředků; ui-*.json a ui-diff.csv: UI; feature-matrix.*: závěry s doklady; official-only-resources/: vytažené záznamy.')
    add('Opakovatelný nástroj a postup: Tools/ClientCompare/README.txt. Produkční soubory a zdroje klienta se při analýze neměnily.')
    add('Nástroje a formátové reference: https://github.com/NationalSecurityAgency/ghidra/releases/tag/Ghidra_12.2_build ; https://www.7-zip.org/download.html ; https://github.com/avast/retdec/tree/master/src/unpackertool/plugins/mpress . Verze a SHA-256 stažených nástrojů: ../client-compare-tools/tool-provenance.json.')
    text='\n'.join(lines)+'\n';(out/'REPORT.cs.txt').write_text(text,encoding='utf8')
    # HTML remains self-contained and makes long data sections easy to navigate.
    esc=html.escape
    def tab(headers,rows):return '<table><thead><tr>'+''.join('<th>'+esc(str(x))+'</th>'for x in headers)+'</tr></thead><tbody>'+''.join('<tr>'+''.join('<td>'+esc(str(x))+'</td>'for x in row)+'</tr>'for row in rows)+'</tbody></table>'
    table_rows=[(k,d['tables'][k]['official'],d['tables'][k]['ours'],len(d['tables'][k]['official_only']),len(d['tables'][k]['ours_only']))for k in ['TQuest.mpq','TSkill.tcd','TItem.tcd','TMon.tcd','TRegion.tcd','TCharTitle.tcd','TMount.tcd']]
    feature_rows=[(f['area'],f['status'],f['conclusion'])for f in features]
    links=[('REPORT.cs.txt','Úplný český report'),('files-diff.csv','Soubory a SHA-256'),('feature-matrix.csv','Matice funkcí a důkazů'),('summary.json','Výsledky v JSON'),('official-only-resources/manifest.csv','439 vytažených záznamů'),('class-code-evidence.csv','Adresy tříd a metod'),('features-official.c','Vybrané funkce originálu'),('features-ours.c','Vybrané funkce našeho klienta'),('decompilation-official/decompiled.c','Úplná dekompilace originálu'),('decompilation-ours/decompiled.c','Úplná dekompilace našeho klienta')]
    markup='''<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>4Story — porovnání klientů</title><style>body{font:16px/1.55 system-ui,sans-serif;max-width:1180px;margin:40px auto;padding:0 24px;color:#182538;background:#f6f8fb}h1,h2{line-height:1.2}h1{font-size:36px}p{max-width:950px}table{border-collapse:collapse;width:100%;background:white;margin:22px 0;font-size:14px}th,td{text-align:left;vertical-align:top;padding:12px;border-bottom:1px solid #dde4ee}th{background:#e8eef7}a{color:#1654b8}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:white;padding:24px;border:1px solid #dde4ee;font:14px/1.6 ui-monospace,monospace}li{margin:5px 0}.meta{color:#57677b}summary{cursor:pointer;font-weight:600;padding:16px 0}</style>'''
    markup+='<h1>4Story: originální a náš klient</h1><p class="meta">27. 10. 2026 · lokální instalace Gameforge a runtime 4Dev</p>'
    markup+='<p>'+esc(lines[7])+'</p><h2>Rozdíly v datech</h2>'+tab(['Tabulka','Originál','Náš','ID pouze originál','ID pouze náš'],table_rows)
    markup+='<p>Počty označují záznamy a ID. Pro dostupnost ve hře je potřeba odpovídající klientský kód, data a server. Celá tabulka obsahuje i varianty, craftingové a eventové záznamy.</p>'
    markup+='<h2>Funkce a implementace</h2>'+tab(['Oblast','Výsledek','Co bylo ověřeno'],feature_rows)
    markup+='<h2>Výstupy</h2><ul>'+''.join('<li><a href="'+esc(path)+'">'+esc(label)+'</a></li>'for path,label in links)+'</ul>'
    markup+='<details><summary>Úplný report, metodika, adresy, omezení a pořadí portu</summary><pre>'+esc(text)+'</pre></details></html>'
    (out/'REPORT.html').write_text(markup,encoding='utf8')
    print('Reports created:',out/'REPORT.html',out/'REPORT.cs.txt')

if __name__=='__main__':make(pathlib.Path(sys.argv[1]).resolve())
