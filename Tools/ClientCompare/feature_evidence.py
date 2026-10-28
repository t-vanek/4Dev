"""Map MSVC RTTI/vtables to code and the exported Ghidra functions for key features."""
import bisect,csv,json,pathlib,re,struct,sys
from compare import ROOT,write_csv,write_json
import pefile

def find_all(data,pattern):
    at=0
    while (at:=data.find(pattern,at))>=0:
        yield at;at+=1

def build(out):
    targets=re.compile(r'^(CTBL.*|CTBattleLeagueTicket|CTWorldWar.*|CTMentor.*|CTMentee.*|CTTwitter.*|CTWebCashShopDlg|CTNewCashShopDlg|CTClientPetEffect|CTInnerPet.*|CTClientSoul|CTGear.*|CTSkyGarden.*|CBR.*|CTBow.*|CTCompanion.*|CTCustomCloakDlg|CTSecuritySystemDlg)$')
    results=[]
    for label,path in [('official',out/'TClient.official.analysis.exe'),('ours',ROOT/'Game/TClient.exe')]:
        pe=pefile.PE(str(path));base=pe.OPTIONAL_HEADER.ImageBase;image=pe.get_memory_mapped_image()
        fpath=out/('decompilation-'+label)/'functions.tsv'
        functions=list(csv.DictReader(fpath.open(encoding='utf8'),delimiter='\t'))
        starts=sorted((int(f['address'],16),f['name'],int(f['body_bytes']))for f in functions if f.get('address') and f.get('body_bytes') and f.get('status'))
        addresses=[x[0]for x in starts]
        code_limit=pe.OPTIONAL_HEADER.BaseOfData
        def owner(va):
            idx=bisect.bisect_right(addresses,va)-1
            if idx>=0 and va<starts[idx][0]+starts[idx][2]:return starts[idx][:2]
            return 0,''
        for match in re.finditer(rb'\.\?AV([A-Za-z0-9_]+)@@\x00',image):
            name=match[1].decode()
            if not targets.fullmatch(name):continue
            td=base+match.start()-8
            for ref in find_all(image,struct.pack('<I',td)):
                col=ref-12
                if col<0 or col+20>len(image):continue
                sig,offset,cd,typ,hierarchy=struct.unpack_from('<5I',image,col)
                if sig!=0 or offset>65536 or cd>65536 or not base<hierarchy<base+len(image):continue
                for ptr in find_all(image,struct.pack('<I',base+col)):
                    vt=ptr+4
                    if vt+16>len(image):continue
                    methods=struct.unpack_from('<4I',image,vt)
                    if not all(base+4096<=f<base+code_limit for f in methods):continue
                    callsites=[]
                    for instruction in find_all(image[:code_limit],struct.pack('<I',base+vt)):
                        address=base+instruction;fn,name_fn=owner(address)
                        if fn:callsites.append(dict(address=hex(address),function=hex(fn),name=name_fn))
                    results.append(dict(client=label,class_name=name,type_descriptor=hex(td),locator=hex(base+col),vftable=hex(base+vt),
                                        methods=[hex(x)for x in methods],code_references=callsites))
    write_json(out/'class-code-evidence.json',results)
    write_csv(out/'class-code-evidence.csv',[dict(client=x['client'],class_name=x['class_name'],type_descriptor=x['type_descriptor'],vftable=x['vftable'],
              method_addresses='; '.join(x['methods']),code_references='; '.join(r['function']for r in x['code_references']))for x in results],
              ['client','class_name','type_descriptor','vftable','method_addresses','code_references'])
    # Save a small, readable code bundle per client, tied to exact function addresses.
    for label in ('official','ours'):
        path=out/('decompilation-'+label)/'decompiled.c';text=path.read_text(encoding='utf8',errors='replace')
        chunks=re.split(r'(?=/\* Address: )',text)
        by_address={}
        for chunk in chunks:
            m=re.match(r'/\* Address: ([0-9a-fA-F]+);',chunk)
            if m:by_address[int(m[1],16)]=chunk
        snippets=['/* Selected Ghidra pseudocode tied to class/vtable evidence; inferred, not original source. */\n']
        seen=set()
        for row in results:
            if row['client']!=label:continue
            addresses={int(x,16)for x in row['methods']}|{int(x['function'],16)for x in row['code_references']}
            for address in sorted(addresses):
                if address in seen or address not in by_address:continue
                seen.add(address);snippets.append('/* RTTI/vtable: '+row['class_name']+' */\n'+by_address[address])
        (out/('features-'+label+'.c')).write_text('\n'.join(snippets),encoding='utf8')
    print('Mapped',len(results),'class/vtable records')

if __name__=='__main__':build(pathlib.Path(sys.argv[1]).resolve())
